using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChudType.Windows;

internal sealed class GlobalShortcut : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Native.KeyboardHook callback;
    private nint hook;
    private Shortcut shortcut;
    private uint suppressedKey;
    private bool active;
    private EnterKey enter;
    private EnterKey keypadEnter;
    private readonly HashSet<uint> keysDown = [];

    public GlobalShortcut()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        callback = OnKey;
    }

    public event Action? Pressed;
    public event Action? Released;
    public event Action? EnterPressed;
    public Func<bool>? CanCaptureEnter { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsCapturing { get; set; }

    public void Configure(string value)
    {
        var next = Shortcut.Parse(value);
        if (hook != 0 && next == shortcut) return;
        if (hook == 0)
        {
            hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The global keyboard shortcut could not start.");
        }
        if (!Native.RegisterHotKey(0, 15583, next.Modifiers, next.Key))
            throw new InvalidOperationException("Windows or another app is already using that shortcut.");
        Native.UnregisterHotKey(0, 15583);
        shortcut = next;
    }

    private nint OnKey(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, message, data);
        var input = Marshal.PtrToStructure<Native.KeyboardEvent>(data);
        if ((input.Flags & 0x10) != 0) return Native.CallNextHookEx(hook, code, message, data);
        uint key = input.Key;
        bool down = message is 0x100 or 0x104;

        // GetAsyncKeyState is updated after this callback, so remember both sides of the modifier chord.
        if (key is >= 0xA0 and <= 0xA5)
        {
            if (down) keysDown.Add(key);
            else keysDown.Remove(key);
        }

        uint mods = ModifierState();
        if (active && (key == suppressedKey && !down || (mods & shortcut.Modifiers) != shortcut.Modifiers))
        {
            active = false;
            dispatcher.BeginInvoke(() => Released?.Invoke());
        }
        if (suppressedKey != 0 && key == suppressedKey)
        {
            if (!down) suppressedKey = 0;
            return 1;
        }
        if (key == 0x0D)
        {
            ref var enterKey = ref ((input.Flags & 1) != 0 ? ref keypadEnter : ref enter);
            bool capture = down && Enabled && !IsCapturing && (mods == 0 || active && mods == shortcut.Modifiers)
                && CanCaptureEnter?.Invoke() == true;
            if (enterKey.Handle(down, capture, out bool pressed))
            {
                if (pressed) dispatcher.BeginInvoke(() => EnterPressed?.Invoke());
                return 1;
            }
        }
        if (Enabled && !IsCapturing && down && key == shortcut.Key && mods == shortcut.Modifiers)
        {
            suppressedKey = key;
            active = true;
            dispatcher.BeginInvoke(() => Pressed?.Invoke());
            return 1;
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    private uint ModifierState()
    {
        uint modifiers = 0;
        if (keysDown.Contains(0xA4) || keysDown.Contains(0xA5)) modifiers |= 1;
        if (keysDown.Contains(0xA2) || keysDown.Contains(0xA3)) modifiers |= 2;
        if (keysDown.Contains(0xA0) || keysDown.Contains(0xA1)) modifiers |= 4;
        if (Native.Down(0x5B) || Native.Down(0x5C)) modifiers |= 8;
        return modifiers;
    }

    public void Dispose()
    {
        if (hook != 0) Native.UnhookWindowsHookEx(hook);
        hook = 0;
    }

    internal struct EnterKey
    {
        private bool held;
        private bool captured;

        public bool Handle(bool down, bool capture, out bool pressed)
        {
            pressed = false;
            if (!down)
            {
                bool suppress = captured;
                held = captured = false;
                return suppress;
            }
            if (!held)
            {
                held = true;
                captured = pressed = capture;
            }
            return captured;
        }
    }

    internal readonly record struct Shortcut(uint Key, uint Modifiers)
    {
        public const string CaptureHint = "Use Ctrl, Alt or Shift with a letter, number, Space or F1–F11.";

        public static string? FromKeys(Key key, ModifierKeys modifiers)
        {
            if (modifiers == ModifierKeys.None || modifiers.HasFlag(ModifierKeys.Windows) || !IsSupportedKey(key))
                return null;
            var parts = new List<string>(4);
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            var name = key.ToString();
            parts.Add(key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 ? name[1..] : name);
            return string.Join("+", parts);
        }

        public static Shortcut Parse(string value)
        {
            string[] parts = value.Split('+', StringSplitOptions.TrimEntries);
            uint modifiers = 0;
            foreach (var part in parts.SkipLast(1))
                modifiers |= part.ToUpperInvariant() switch
                {
                    "ALT" => 1u, "CTRL" or "CONTROL" => 2u, "SHIFT" => 4u,
                    _ => throw new FormatException(CaptureHint)
                };
            string main = parts[^1];
            if (main.Length == 1 && char.IsAsciiDigit(main[0])) main = "D" + main;
            if (modifiers == 0 || main.Length == 0 || char.IsAsciiDigit(main[0]) || !Enum.TryParse<Key>(main, true, out var key) ||
                !IsSupportedKey(key))
                throw new FormatException(CaptureHint);
            return new((uint)KeyInterop.VirtualKeyFromKey(key), modifiers);
        }

        private static bool IsSupportedKey(Key key) => key is
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z or
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 or
            >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F11 or System.Windows.Input.Key.Space;
    }
}
