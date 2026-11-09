using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ChudType.Core;
using ChudType.Windows;
using Microsoft.Extensions.Logging;

namespace ChudType.UI;

internal partial class Overlay : Window
{
    private readonly DictationSession session;
    private readonly Func<Preferences> preferences;
    private readonly DispatcherTimer linger = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Border[] bars;
    private nint handle;
    private bool compact;
    private int phase;

    public Overlay(DictationSession session, Func<Preferences> preferences, Action<FloatingPosition> savePosition, ILogger<Overlay> log)
    {
        InitializeComponent();
        this.session = session;
        this.preferences = preferences;
        InitializeDragging(savePosition, log);
        bars = Enumerable.Range(0, 7).Select(_ =>
        {
            var bar = new Border { Width = 4, Height = 3, Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            Wave.Children.Add(bar);
            return bar;
        }).ToArray();
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            nint styles = Native.GetWindowLongPtr(handle, -20);
            Native.SetWindowLongPtr(handle, -20, styles | 0x08000080); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            HwndSource.FromHwnd(handle).AddHook(WindowMessage);
        };
        SizeChanged += (_, _) => Place();
        linger.Tick += (_, _) =>
        {
            linger.Stop();
            compact = true;
            Refresh();
        };
        session.Changed += OnSessionChanged;
        session.LevelChanged += UpdateWave;
    }

    public void OnSessionChanged()
    {
        linger.Stop();
        compact = false;
        if (session.State is DictationState.Completed or DictationState.Error) linger.Start();
        Refresh();
    }

    public void Refresh()
    {
        var settings = preferences();
        var state = compact ? DictationState.Idle : session.State;
        bool idle = state == DictationState.Idle;
        if (idle && (settings.Overlay != "Always" || !settings.SetupComplete || !session.Enabled))
        {
            ClearPointer();
            Hide();
            return;
        }
        bool listening = state == DictationState.Listening;
        bool review = state == DictationState.Review;
        Live.Visibility = Visible(!idle);
        StopButton.Visibility = Visible(listening);
        if (listening) StopButton.Content = session.CanCaptureEnter() ? "Enter ↵" : "Stop";
        CancelButton.Visibility = Visible(session.IsBusy);
        Wave.Visibility = Clock.Visibility = Visible(listening);
        Working.Visibility = Visible(state is DictationState.Starting or DictationState.Transcribing or DictationState.SendingText);
        Review.Visibility = Visible(review);
        LogoButton.IsEnabled = !session.IsBusy;
        Label.Text = state switch
        {
            DictationState.Starting => "Starting",
            DictationState.Listening => "Listening",
            DictationState.Transcribing => "Transcribing",
            DictationState.SendingText => "Sending text",
            DictationState.Completed => session.Status is "Inserted" or "Copied" ? "✓ " + session.Status : "✓ Ready",
            DictationState.Error => "⚠ " + session.Status,
            DictationState.Review => "Review transcript",
            _ => "Ready"
        };
        Shell.ToolTip = session.Status;
        if (review)
        {
            Detail.Text = session.Status;
            Preview.Text = session.Latest?.Text ?? "";
        }
        if (!IsVisible) Show();
        UpdateWave();
        Place();
    }

    public void Place()
    {
        if (!IsVisible || handle == 0 || ActualWidth <= 0 || ActualHeight <= 0 || pointer != null) return;
        nint anchor = session.IsBusy && session.Anchor != 0 ? session.Anchor : TextTarget.Foreground;
        nint monitor = Native.MonitorFromWindow(anchor, 2);
        var settings = preferences();
        if (settings.FloatingPosition is { } saved)
        {
            nint preferred = FindMonitor(saved.Monitor);
            if (preferred != 0) monitor = preferred;
        }
        if (!TryGetMonitor(monitor, out var info)) return;
        double scale = MonitorScale(monitor);
        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        if (settings.FloatingPosition is { } position)
        {
            MoveTo(ClampToWorkArea(RestorePosition(position, info.Work), info.Work, width, height));
            return;
        }
        int margin = (int)Math.Round(18 * scale);
        int x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        int y = info.Work.Bottom - height - margin;
        switch (settings.Position)
        {
            case "Top": y = info.Work.Top + margin; break;
            case "Left":
                x = info.Work.Left + margin;
                y = info.Work.Top + (info.Work.Bottom - info.Work.Top - height) / 2;
                break;
            case "Right":
                x = info.Work.Right - width - margin;
                y = info.Work.Top + (info.Work.Bottom - info.Work.Top - height) / 2;
                break;
        }
        MoveTo(ClampToWorkArea(new Native.Point { X = x, Y = y }, info.Work, width, height));
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021)
        {
            handled = true;
            return 3; // MA_NOACTIVATE; still deliver the mouse event to the button.
        }
        if (message is 0x02E0 or 0x007E or 0x001A)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        return 0;
    }

    private void UpdateWave()
    {
        if (session.State != DictationState.Listening) return;
        double volume = Math.Clamp(Math.Sqrt(session.Level) * 2.2, 0, 1);
        for (int i = 0; i < bars.Length; i++)
            bars[i].Height = 3 + 20 * volume * (0.35 + 0.65 * Math.Abs(Math.Sin((phase + i * 1.4) * 0.6)));
        phase = (phase + 1) % 1000;
        Clock.Text = FormatElapsed(session.Seconds);
    }

    internal static string FormatElapsed(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private async void StartClick(object sender, RoutedEventArgs e) => await session.StartAsync(test: false);
    private async void StopClick(object sender, RoutedEventArgs e) => await session.StopAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => session.Cancel();
    private async void InsertClick(object sender, RoutedEventArgs e) => await session.AcceptAsync(raw: false);
    private async void RawClick(object sender, RoutedEventArgs e) => await session.AcceptAsync(raw: true);
    private void CopyClick(object sender, RoutedEventArgs e) => Copy(session.Latest?.Text);
    private void CopyRawClick(object sender, RoutedEventArgs e) => Copy(session.Latest?.Raw);

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            session.Copied();
        }
        catch (COMException) { Detail.Text = "The clipboard is busy. Try copying again."; }
    }
}
