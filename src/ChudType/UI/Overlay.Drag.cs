using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ChudType.Core;
using ChudType.Windows;
using Microsoft.Extensions.Logging;

namespace ChudType.UI;

internal partial class Overlay
{
    private readonly DispatcherTimer pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private PointerPress? pointer;
    private FloatingPosition? drop;

    private void InitializeDragging(Action<FloatingPosition> savePosition, ILogger<Overlay> log)
    {
        Shell.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (pointer is not null || !OverLogo(args.GetPosition(LogoButton)) ||
                !Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(handle, out var bounds))
                return;

            double scale = MonitorScale(Native.MonitorFromWindow(handle, 2));
            pointer = new(cursor, (cursor.X - bounds.Left) / scale, (cursor.Y - bounds.Top) / scale, scale);
            args.Handled = true;
            pointerTimer.Start();
        };

        Shell.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (pointer is null) return;
            args.Handled = true;
            if (Native.GetCursorPos(out var cursor))
                await CompletePointerAsync(cursor, savePosition, log);
            else
                ClearPointer();
        };

        // A nonactivating window cannot rely on receiving mouse moves outside its bounds.
        // Poll only during the gesture so it can follow the pointer without taking focus.
        pointerTimer.Tick += async (_, _) =>
        {
            if (pointer is null || !Native.GetCursorPos(out var cursor))
            {
                ClearPointer();
                Place();
                return;
            }
            int primary = Native.GetSystemMetrics(23) != 0 ? 2 : 1; // SM_SWAPBUTTON
            if (Native.Down(primary)) MoveWithPointer(cursor);
            else await CompletePointerAsync(cursor, savePosition, log);
        };
        Closed += (_, _) => ClearPointer();
    }

    private async Task CompletePointerAsync(Native.Point cursor, Action<FloatingPosition> savePosition, ILogger<Overlay> log)
    {
        MoveWithPointer(cursor);
        var saved = drop;
        ClearPointer();
        if (saved != null)
        {
            try { savePosition(saved); }
            catch (Exception error) { log.LogWarning(error, "Could not save the floating bar position"); }
            Place();
        }
        else if (LogoButton.IsEnabled && OverLogo(LogoButton.PointFromScreen(new Point(cursor.X, cursor.Y))))
        {
            await session.StartAsync(test: false);
        }
        else Place();
    }

    private void ClearPointer()
    {
        pointerTimer.Stop();
        pointer = null;
        drop = null;
    }

    private bool OverLogo(Point point) =>
        point.X >= 0 && point.X < LogoButton.ActualWidth && point.Y >= 0 && point.Y < LogoButton.ActualHeight;

    private void MoveWithPointer(Native.Point cursor)
    {
        if (pointer is not { } press) return;
        if (drop is null &&
            Math.Abs(cursor.X - press.Start.X) < SystemParameters.MinimumHorizontalDragDistance * press.Scale &&
            Math.Abs(cursor.Y - press.Start.Y) < SystemParameters.MinimumVerticalDragDistance * press.Scale)
            return;

        nint monitor = Native.MonitorFromPoint(cursor, 2);
        if (!TryGetMonitor(monitor, out var info)) return;
        double scale = MonitorScale(monitor);
        var requested = new Native.Point
        {
            X = cursor.X - (int)Math.Round(press.OffsetX * scale),
            Y = cursor.Y - (int)Math.Round(press.OffsetY * scale)
        };
        var clamped = ClampToWorkArea(requested, info.Work, (int)Math.Ceiling(ActualWidth * scale),
            (int)Math.Ceiling(ActualHeight * scale));
        if (MoveTo(clamped)) drop = SavePosition(info.Device, clamped, info.Work);
    }

    private bool MoveTo(Native.Point point) =>
        Native.SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE

    private static bool TryGetMonitor(nint monitor, out Native.MonitorInfo info)
    {
        info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        return Native.GetMonitorInfo(monitor, ref info);
    }

    private static double MonitorScale(nint monitor) =>
        Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;

    private static nint FindMonitor(string device)
    {
        nint match = 0;
        Native.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            if (!TryGetMonitor(monitor, out var info) || !string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase))
                return true;
            match = monitor;
            return false;
        }, 0);
        return match;
    }

    internal static Native.Point ClampToWorkArea(Native.Point point, Native.Rect work, int width, int height) => new()
    {
        X = Math.Clamp(point.X, work.Left, Math.Max(work.Left, work.Right - width)),
        Y = Math.Clamp(point.Y, work.Top, Math.Max(work.Top, work.Bottom - height))
    };

    internal static FloatingPosition SavePosition(string device, Native.Point point, Native.Rect work) =>
        new(device, (point.X - work.Left) / (double)Math.Max(1, work.Right - work.Left),
            (point.Y - work.Top) / (double)Math.Max(1, work.Bottom - work.Top));

    internal static Native.Point RestorePosition(FloatingPosition position, Native.Rect work) => new()
    {
        X = work.Left + (int)Math.Round(Math.Clamp(position.X, 0, 1) * (work.Right - work.Left)),
        Y = work.Top + (int)Math.Round(Math.Clamp(position.Y, 0, 1) * (work.Bottom - work.Top))
    };

    private sealed record PointerPress(Native.Point Start, double OffsetX, double OffsetY, double Scale);
}
