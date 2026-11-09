using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using ChudType.Audio;
using ChudType.Core;
using ChudType.Speech;
using ChudType.Storage;
using ChudType.UI;
using ChudType.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace ChudType;

public partial class App : Application
{
    private ServiceProvider? services;
    private MainWindow? dashboard;
    private Overlay? overlay;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Mutex? instance;
    private bool ownsMutex;
    private bool closing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(false, @"Local\CHUDAI.ChudType");
        try { ownsMutex = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            if (!e.Args.Contains("--background"))
                MessageBox.Show("ChudType is already running. Open it from its icon in the Windows system tray.", "ChudType");
            Shutdown();
            return;
        }

        try
        {
            services = new ServiceCollection()
                .AddSingleton(_ => new LocalStore())
                .AddLogging()
                .AddSingleton<ILoggerProvider, LocalLogProvider>()
                .AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                .AddSingleton<ModelCatalog>()
                .AddSingleton<Microphone>()
                .AddSingleton<Func<string, IMicrophoneRecording>>(sp => sp.GetRequiredService<Microphone>().Start)
                .AddSingleton<ISpeechEngine, WhisperEngine>()
                .AddSingleton<DictationModel>()
                .AddSingleton<TextCleanup>()
                .AddSingleton<ITextProvider, RemoteCleanup>()
                .AddSingleton<IDictationTarget, TextTarget>()
                .AddSingleton<GlobalShortcut>()
                .AddSingleton<DictationSession>()
                .AddSingleton<DashboardViewModel>()
                .BuildServiceProvider();
            var vm = services.GetRequiredService<DashboardViewModel>();
            var target = services.GetRequiredService<IDictationTarget>();
            var shortcut = services.GetRequiredService<GlobalShortcut>();
            var session = vm.Session;
            session.Initialize();
            shortcut.Pressed += session.ShortcutPressed;
            shortcut.Released += session.ShortcutReleased;
            shortcut.CanCaptureEnter = session.CanCaptureEnter;
            shortcut.EnterPressed += async () => await session.FinishWithEnterAsync();
            string? shortcutError = null;
            try { shortcut.Configure(vm.Settings.Shortcut); }
            catch (Exception error) { shortcutError = error.Message; }

            ApplyTheme(vm.Settings);
            dashboard = new MainWindow(vm, services.GetRequiredService<LocalStore>(),
                services.GetRequiredService<Microphone>(), services.GetRequiredService<ITextProvider>());
            MainWindow = dashboard;
            dashboard.SourceInitialized += (_, _) => ApplyTitleBar(vm.Settings.Light);
            overlay = new Overlay(session, () => vm.Settings,
                position => vm.Update(settings => settings with { FloatingPosition = position }),
                services.GetRequiredService<ILogger<Overlay>>());
            target.ForegroundChanged += overlay.Place;
            vm.SettingsChanged += () => { ApplyTheme(vm.Settings); overlay.Refresh(); };
            dashboard.ExitRequested += ExitApp;
            MakeTray(vm, shortcut);
            if (!e.Args.Contains("--background") || !vm.Settings.SetupComplete) OpenDashboard();
            overlay.Refresh();
            if (shortcutError != null) tray?.ShowBalloonTip(4000, "ChudType shortcut", shortcutError, Forms.ToolTipIcon.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show($"ChudType could not open. {error.Message}", "ChudType", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void MakeTray(DashboardViewModel vm, GlobalShortcut shortcut)
    {
        using var icon = GetResourceStream(new Uri("/ChudType;component/Assets/chudgpt.ico", UriKind.Relative)).Stream;
        using var loadedIcon = new System.Drawing.Icon(icon);
        trayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        var menu = new Forms.ContextMenuStrip();
        var session = vm.Session;
        menu.Items.Add("Open ChudType", null, (_, _) => OpenDashboard());
        menu.Items.Add("Start dictation", null, (_, _) => Dispatcher.BeginInvoke(async () => await session.StartAsync(test: false)));
        menu.Items.Add("Stop dictation", null, async (_, _) => await session.StopAsync());
        var toggle = menu.Items.Add("Pause dictation", null, (_, _) =>
        {
            shortcut.Enabled = !session.Enabled;
            session.SetEnabled(shortcut.Enabled);
        });
        var microphone = menu.Items.Add("Microphone", null, (_, _) => OpenDashboard("Dictation"));
        menu.Items.Add("Settings", null, (_, _) => OpenDashboard("Settings"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) =>
        {
            toggle.Text = session.Enabled ? "Pause dictation" : "Enable dictation";
            try
            {
                string id = vm.Settings.Microphone;
                var mic = services!.GetRequiredService<Microphone>().List().FirstOrDefault(m => m.Id == id);
                microphone.Text = "Microphone · " + (mic?.Name ?? "Unavailable");
            }
            catch (Exception) { microphone.Text = "Microphone · Unavailable"; }
        };
        tray = new Forms.NotifyIcon { Text = "ChudType", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenDashboard();
    }

    private void OpenDashboard(string? page = null)
    {
        if (dashboard is null || closing) return;
        if (page != null) dashboard.Navigate(page);
        if (!dashboard.IsVisible) dashboard.Show();
        if (dashboard.WindowState == WindowState.Minimized) dashboard.WindowState = WindowState.Normal;
        dashboard.Activate();
    }

    private async void ExitApp()
    {
        if (closing) return;
        closing = true;
        dashboard?.StopDownloads();
        if (services != null) await services.GetRequiredService<DictationSession>().ShutdownAsync();
        if (dashboard != null) dashboard.Exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        services?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }

    private void ApplyTitleBar(bool light)
    {
        if (dashboard is null) return;
        nint hwnd = new WindowInteropHelper(dashboard).Handle;
        if (hwnd == 0) return;
        int dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void ApplyTheme(Preferences options)
    {
        Brush Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return (Brush)(Resources[key] = brush);
        }
        Set("Canvas", options.Light ? "#F7F5FC" : "#101017");
        Set("Panel", options.Light ? "#FFFFFF" : "#181822");
        Set("Surface", options.Light ? "#F0EDF8" : "#222231");
        Set("Border", options.Light ? "#D9D3E6" : "#38354A");
        Set("Highlight", options.Light ? "#E9DFFB" : "#382D52");
        Set("ScrollThumb", options.Light ? "#A89EBC" : "#55506A");
        ApplyTitleBar(options.Light);
        Set("Ink", options.Light ? "#211936" : "#F4F2FC");
        Set("Muted", options.Light ? "#554C68" : "#B3AEC6");
        var accent = options.Accent switch
        {
            "Blue" => ("#95CAFF", "#235484"),
            "Mint" => ("#8FE3C8", "#1D6251"),
            "Rose" => ("#FFABCB", "#8A2E57"),
            _ => ("#BFA8FF", "#4B2B86")
        };
        Set("Accent", options.Light ? accent.Item2 : accent.Item1);
        Set("AccentBase", accent.Item2);
    }
}
