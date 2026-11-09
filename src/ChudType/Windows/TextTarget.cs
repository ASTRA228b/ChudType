using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ChudType.Windows;

internal interface IDictationTarget
{
    nint CurrentWindow { get; }
    event Action? ForegroundChanged;
    bool IsFocused(TextTarget.Snapshot target);
    Task<TextTarget.Snapshot> CaptureAsync(nint expectedWindow, CancellationToken cancellation);
    Task<string?> InsertAsync(TextTarget.Snapshot? target, string text, CancellationToken cancellation);
}

internal sealed class TextTarget : IDictationTarget, IDisposable
{
    internal sealed record Snapshot(nint Window, nint Focus, int[]? Element, bool Password, bool Editable, int FocusVersion);
    private readonly Native.WinEventHook callback;
    private readonly nint foregroundHook;
    private readonly nint focusHook;
    private int focusVersion;

    public TextTarget()
    {
        callback = OnEvent;
        // Skip our own UI so showing the overlay cannot invalidate the target.
        foregroundHook = Native.SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
        focusHook = Native.SetWinEventHook(0x8005, 0x8005, 0, callback, 0, 0, 2);
    }

    public event Action? ForegroundChanged;
    public static nint Foreground => Native.GetForegroundWindow();
    public nint CurrentWindow => Foreground;

    public bool IsFocused(Snapshot target) =>
        target.FocusVersion == focusVersion && Handles() == (target.Window, target.Focus);

    private void OnEvent(nint hook, uint eventId, nint hwnd, int objectId, int child, uint thread, uint time)
    {
        if (eventId == 3 || (hwnd != 0 && objectId is 0 or -4))
            focusVersion++;
        if (eventId == 3) ForegroundChanged?.Invoke();
    }

    public async Task<Snapshot> CaptureAsync(nint expectedWindow, CancellationToken cancellation)
    {
        int version = focusVersion;
        var (window, focus) = Handles();
        if (window == 0 || window != expectedWindow || !OnInputDesktop())
            throw new InvalidOperationException("Focus a text field on the Windows desktop.");
        bool nativePassword = IsNativePassword(focus);
        try
        {
            var uia = await Task.Run(ReadAutomation, cancellation).WaitAsync(TimeSpan.FromMilliseconds(600), cancellation);
            bool stable = version == focusVersion && Handles() == (window, focus);
            return new(window, focus, stable ? uia.Id : null, nativePassword || uia.Password,
                stable && uia.Editable, version);
        }
        catch (TimeoutException)
        {
            return new(window, focus, null, nativePassword, false, version);
        }
    }

    private static (nint Window, nint Focus) Handles()
    {
        nint window = Foreground;
        uint thread = Native.GetWindowThreadProcessId(window, out _);
        if (thread == 0) return (window, 0);
        var info = new Native.GuiThread { Size = Marshal.SizeOf<Native.GuiThread>() };
        return (window, Native.GetGUIThreadInfo(thread, ref info) ? info.Focus : 0);
    }

    private static bool IsNativePassword(nint focus)
    {
        if (focus == 0) return false;
        var name = new StringBuilder(128);
        Native.GetClassName(focus, name, name.Capacity);
        return (name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                name.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) &&
            (Native.GetWindowLongPtr(focus, -16).ToInt64() & 0x20) != 0;
    }

    private static bool OnInputDesktop()
    {
        nint input = Native.OpenInputDesktop(0, false, 1);
        if (input == 0) return false;
        try
        {
            var actual = new StringBuilder(128);
            var current = new StringBuilder(128);
            return Native.GetUserObjectInformation(input, 2, actual, 256, out _) &&
                Native.GetUserObjectInformation(Native.GetThreadDesktop(Native.GetCurrentThreadId()), 2, current, 256, out _) &&
                actual.ToString() == current.ToString();
        }
        finally { Native.CloseDesktop(input); }
    }

    private static (int[]? Id, bool Password, bool Editable) ReadAutomation()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return default;
            var current = focused.Current;
            if (current.IsPassword) return (null, true, false);
            if (!current.IsEnabled || !current.HasKeyboardFocus) return default;
            bool writable = focused.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                ? !((ValuePattern)value).Current.IsReadOnly
                : current.ControlType == ControlType.Edit ||
                    current.ControlType == ControlType.Document && IsWritableText(focused);
            return (focused.GetRuntimeId(), false, writable);
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return default;
        }
    }

    private static bool IsWritableText(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return false;
        var selections = ((TextPattern)pattern).GetSelection();
        return selections.Length > 0 && selections[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
    }

    public async Task<string?> InsertAsync(Snapshot? target, string text, CancellationToken cancellation)
    {
        if (target is null || target.Password || !target.Editable || target.Element is null)
            return "The target does not expose a supported editable field. Copy the transcript from ChudType.";
        if (text.Any(char.IsControl))
            return "Copy this multiline transcript from ChudType to paste it as text.";

        for (int i = 0; i < 40 && ModifiersDown(); i++)
            await Task.Delay(40, cancellation);

        if (ModifiersDown() || !IsFocused(target))
            return "Focus changed or a shortcut key is still held. The transcript is ready to copy.";
        Snapshot current;
        try { current = await CaptureAsync(target.Window, cancellation); }
        catch (InvalidOperationException)
        {
            return "The original text field is no longer active. The transcript is ready to copy.";
        }
        if (current.Password || !current.Editable || current.Element is null ||
            !target.Element.SequenceEqual(current.Element) || !IsFocused(target) || ModifiersDown())
            return "The original text field is no longer active. The transcript is ready to copy.";

        cancellation.ThrowIfCancellationRequested();
        var input = new Native.Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            input[2 * i] = Unicode(text[i], false);
            input[2 * i + 1] = Unicode(text[i], true);
        }
        uint sent = Native.SendInput((uint)input.Length, input, Marshal.SizeOf<Native.Input>());
        if (sent == 0) return "Windows blocked input to this app. The transcript is ready to copy.";
        if (sent != input.Length) return "Windows accepted only part of the text. Check the field before pasting.";
        return null;
    }

    private static bool ModifiersDown() => Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12) ||
        Native.Down(0x5B) || Native.Down(0x5C);

    private static Native.Input Unicode(char character, bool up) => new()
    {
        Type = 1,
        Payload = new() { Keyboard = new() { Scan = character, Flags = up ? 6u : 4u } }
    };

    public void Dispose()
    {
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        if (focusHook != 0) Native.UnhookWinEvent(focusHook);
    }
}
