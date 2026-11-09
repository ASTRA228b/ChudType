using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ChudType.Audio;
using ChudType.Core;
using ChudType.Speech;
using ChudType.Storage;
using ChudType.Windows;
using Microsoft.Win32;

namespace ChudType.UI;

internal partial class MainWindow : Window
{
    private static readonly string[] Pages = ["Home", "Dictation", "History", "Dictionary", "Snippets", "AI Processing", "Models",
        "Statistics", "Settings", "About"];
    private static readonly string[] Languages = ["auto", "en", "es", "fr", "pt", "de", "ru", "it", "ja", "ko", "zh", "pl", "uk"];
    private readonly DashboardViewModel vm;
    private readonly LocalStore store;
    private readonly Microphone microphone;
    private readonly ITextProvider remote;
    private readonly Dictionary<string, Button> nav = [];
    private CancellationTokenSource? transfer;
    private string pageName = "";
    private int setup;
    public bool Exiting { get; set; }

    public MainWindow(DashboardViewModel vm, LocalStore store, Microphone microphone, ITextProvider remote)
    {
        this.vm = vm;
        this.store = store;
        this.microphone = microphone;
        this.remote = remote;
        InitializeComponent();
        DataContext = vm;
        foreach (string name in Pages)
        {
            var button = ActionButton(name, () => Navigate(name));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, 5);
            nav.Add(name, button);
            Navigation.Children.Add(button);
        }
        vm.SettingsChanged += RefreshFooter;
        Closing += (_, args) =>
        {
            if (Exiting) return;
            args.Cancel = true;
            if (vm.Settings.MinimizeToTray) Hide();
            else ExitRequested?.Invoke();
        };
        RefreshFooter();
        Navigate(vm.Settings.SetupComplete ? "Home" : "Setup");
    }

    public event Action? ExitRequested;
    public void StopDownloads() => transfer?.Cancel();

    public void Navigate(string name)
    {
        pageName = name;
        Page.Children.Clear();
        PageScroll.ScrollToTop();
        foreach (var (key, button) in nav)
            button.SetResourceReference(BackgroundProperty, name == key ? "AccentBase" : "Panel");
        switch (name)
        {
            case "Setup": BuildSetup(); break;
            case "Home": BuildHome(); break;
            case "Dictation": BuildDictation(); break;
            case "History": BuildHistory(); break;
            case "Dictionary": BuildDictionary(); break;
            case "Snippets": BuildSnippets(); break;
            case "AI Processing": BuildProcessing(); break;
            case "Models": BuildModels(); break;
            case "Statistics": BuildStatistics(); break;
            case "Settings": BuildSettings(); break;
            case "About": BuildAbout(); break;
        }
    }

    private void BuildSetup()
    {
        Heading("FIRST-RUN SETUP", "Set up ChudType", $"Step {setup + 1} of 4");
        switch (setup)
        {
            case 0:
                var privacy = Card("Microphone and privacy", "ChudType captures audio only during dictation. Audio stays in memory and speech recognition runs on this computer.");
                Toggle(privacy, "Allow ChudType to use my microphone during dictation", vm.Settings.MicConsent,
                    value => vm.Update(s => s with { MicConsent = value }));
                Toggle(privacy, "Save transcripts on this computer", vm.Settings.History, value => vm.Update(s => s with { History = value }));
                Toggle(privacy, "Track local dictation statistics", vm.Settings.Statistics, value => vm.Update(s => s with { Statistics = value }));
                break;
            case 1:
                MicrophoneControls(Card("Input", "Select the microphone ChudType should use."));
                ModelControls(Card("Whisper model", "Download one verified model to enable offline transcription."));
                break;
            case 2:
                ShortcutControls(Card("Activation", "Choose how to start and stop system-wide dictation."));
                break;
            case 3:
                DictationControls(Card("Try it here", "A test transcript appears here so you can check the microphone and recognition."));
                break;
        }

        var buttons = Row(Page);
        if (setup > 0) buttons.Children.Add(ActionButton("Back", () => { setup--; Navigate("Setup"); }));
        buttons.Children.Add(ActionButton(setup < 3 ? "Continue" : "Finish setup", () =>
        {
            if (setup < 3) { setup++; Navigate("Setup"); }
            else
            {
                vm.Update(s => s with { SetupComplete = true });
                Navigate("Home");
            }
        }, primary: true));
    }

    private void BuildHome()
    {
        Heading("OVERVIEW", "Your voice, anywhere", "Dictate into the app you’re already using.");
        var options = vm.Settings;
        var setupCard = Card("Dictation workspace", $"{ModelCatalog.Get(options.Model).Display} · {options.Language} · {options.Cleanup}");
        AddText(setupCard, CurrentMicrophone(), "Muted");
        AddText(setupCard, $"Processing · {options.Provider}    Shortcut · {options.Shortcut}", "Muted");
        if (options.Provider != "Local only")
            setupCard.Children.Add(ActionButton("Check provider", () => Navigate("AI Processing")));
        DictationControls(setupCard);
        var usage = store.GetUsage();
        var stats = Card("Activity", options.Statistics
            ? $"{usage.Today:N0} words today · {usage.Words:N0} total · {TimeSpan.FromSeconds(usage.Seconds):hh\\:mm\\:ss}"
            : "Local statistics are off.");
        var recent = store.History().Take(4).ToList();
        if (recent.Count > 0)
        {
            var card = Card("Recent transcripts", null);
            foreach (var item in recent)
                AddText(card, $"{item.DateLabel}   {item.Text[..Math.Min(90, item.Text.Length)]}", "Muted");
        }
        if (!options.SetupComplete)
            stats.Children.Add(ActionButton("Open first-run setup", () => Navigate("Setup")));
    }

    private void BuildDictation()
    {
        Heading("VOICE INPUT", "Dictation", "Test your microphone and customize how dictation appears on your screen.");
        DictationControls(Card("Microphone test", "This test keeps the transcript in ChudType."));
        MicrophoneControls(Card("Input device", null));
        ShortcutControls(Card("Global shortcut", "Focus an editable field, then press your configured shortcut."));
        var overlay = Card("Floating bar", "Click the ChudGPT logo to start. The bar follows the active app’s monitor.");
        Select(overlay, "Visibility", new[] { "Always", "During dictation" }, vm.Settings.Overlay,
            value => vm.Update(s => s with { Overlay = value }));
        Select(overlay, "Dock", new[] { "Bottom", "Top", "Left", "Right" }, vm.Settings.Position,
            value => vm.Update(s => s with { Position = value, FloatingPosition = null }));
        AddText(overlay, "Drag the ChudGPT logo to move the bar. Reset position returns it to the selected dock.", "Muted");
        overlay.Children.Add(ActionButton("Reset position", () => vm.Update(s => s with { FloatingPosition = null })));
        Toggle(overlay, "Review text before inserting", vm.Settings.Preview, value => vm.Update(s => s with { Preview = value }));
    }

    private void DictationControls(Panel panel)
    {
        var meter = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 9, 0, 12) };
        meter.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(vm.Level)) { Mode = BindingMode.OneWay });
        AutomationProperties.SetName(meter, "Live microphone level");
        panel.Children.Add(meter);
        var row = Row(panel);
        var test = AsyncButton("", vm.TestDictationAsync, primary: true, disableWhileRunning: false);
        test.SetBinding(ContentControl.ContentProperty, new Binding(nameof(vm.DictateLabel)));
        row.Children.Add(test);
        row.Children.Add(ActionButton("Copy latest text", () => Copy(vm.Latest)));
        row.Children.Add(ActionButton("Copy raw text", () => Copy(vm.Session.Latest?.Raw)));
        var output = AddText(panel, "", "Muted");
        output.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.Latest)));
    }

    private void MicrophoneControls(Panel panel)
    {
        try
        {
            var items = microphone.List().ToList();
            if (items.All(x => x.Id != vm.Settings.Microphone)) items.Add(new(vm.Settings.Microphone, "Selected microphone (unavailable)"));
            Select(panel, "Microphone", items, items.First(x => x.Id == vm.Settings.Microphone),
                value => vm.Update(s => s with { Microphone = value.Id }), nameof(MicrophoneDevice.Name));
        }
        catch (Exception)
        {
            AddText(panel, "Windows could not enumerate microphone devices.", "Muted");
        }
        AddText(panel, "Speech threshold · lower values detect quieter input.", "Muted");
        var slider = new Slider { Minimum = 0.003, Maximum = 0.08, Value = vm.Settings.Sensitivity, Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 5), TickFrequency = 0.001, IsSnapToTickEnabled = true };
        AutomationProperties.SetName(slider, "Speech threshold");
        slider.ValueChanged += (_, args) => Guard(() => vm.Update(s => s with { Sensitivity = args.NewValue }));
        panel.Children.Add(slider);
    }

    private string CurrentMicrophone()
    {
        try { return microphone.List().FirstOrDefault(m => m.Id == vm.Settings.Microphone)?.Name ?? "Selected microphone unavailable"; }
        catch (Exception) { return "Microphone unavailable"; }
    }

    private void ShortcutControls(Panel panel)
    {
        AddText(panel, "Current shortcut", "Muted");
        var row = Row(panel);
        var shortcutButton = new Button();
        row.Children.Add(shortcutButton);
        var help = AddText(panel, "Select the shortcut to change it.", "Muted");
        bool capturing = false;
        Key pendingKey = Key.None;
        string? pendingShortcut = null;

        string Display(string value) => string.Join(" + ", value.Split('+', StringSplitOptions.TrimEntries));
        shortcutButton.Content = Display(vm.Settings.Shortcut);
        AutomationProperties.SetName(shortcutButton, "Change dictation shortcut");
        AutomationProperties.SetLiveSetting(help, AutomationLiveSetting.Polite);

        void EndCapture(string message)
        {
            if (!capturing) return;
            capturing = false;
            vm.CaptureShortcut(false);
            pendingShortcut = null;
            pendingKey = Key.None;
            shortcutButton.Content = Display(vm.Settings.Shortcut);
            help.Text = message;
        }

        shortcutButton.Click += (_, _) =>
        {
            if (capturing) return;
            shortcutButton.Focus();
            capturing = true;
            vm.CaptureShortcut(true);
            shortcutButton.Content = "Press your shortcut…";
            help.Text = GlobalShortcut.Shortcut.CaptureHint + " Press Esc to cancel.";
        };
        shortcutButton.PreviewKeyDown += (_, args) =>
        {
            if (!capturing) return;
            Key key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (key == Key.Tab)
            {
                EndCapture("Shortcut change canceled.");
                return;
            }
            args.Handled = true;
            if (key == Key.Escape)
            {
                EndCapture("Shortcut change canceled.");
                return;
            }
            if (args.IsRepeat || pendingShortcut is not null || key is Key.LeftCtrl or Key.RightCtrl or
                Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
            pendingShortcut = GlobalShortcut.Shortcut.FromKeys(key, Keyboard.Modifiers);
            if (pendingShortcut is null)
            {
                help.Text = GlobalShortcut.Shortcut.CaptureHint;
                return;
            }
            pendingKey = key;
            shortcutButton.Content = Display(pendingShortcut);
            help.Text = "Release the key to save this shortcut.";
        };
        shortcutButton.PreviewKeyUp += (_, args) =>
        {
            if (!capturing || pendingShortcut is null) return;
            Key key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (key != pendingKey) return;
            args.Handled = true;
            try
            {
                vm.SetShortcut(pendingShortcut);
                EndCapture("Shortcut saved.");
            }
            catch (Exception error)
            {
                pendingShortcut = null;
                pendingKey = Key.None;
                shortcutButton.Content = "Try another shortcut…";
                help.Text = error.Message;
            }
        };
        shortcutButton.LostKeyboardFocus += (_, _) => EndCapture("Shortcut change canceled.");
        shortcutButton.Unloaded += (_, _) => EndCapture("Shortcut change canceled.");
        Select(panel, "Activation mode", new[] { "Hold", "Toggle", "Hands-free" }, vm.Settings.Mode,
            value => vm.Update(s => s with { Mode = value }));
        AddText(panel, "Hands-free stops after a pause. Hold to record until you release the shortcut.", "Muted");
    }

    private void BuildModels()
    {
        Heading("ON-DEVICE RECOGNITION", "Models", "Whisper converts microphone audio into text on your computer.");
        ModelControls(Card("Speech model", "Whisper Tiny, Base and Small are multilingual model sizes."));
        var local = Card("ChudGPT Dictation v1", "Bundled English text model for context-sensitive filler-word removal. It runs on-device.");
        local.Children.Add(ActionButton("Open AI Processing", () => Navigate("AI Processing")));
    }

    private void ModelControls(Panel panel)
    {
        var model = ModelCatalog.Get(vm.Settings.Model);
        var status = AddText(panel, ModelStatus(model), "Muted");
        Select(panel, "Whisper model", ModelCatalog.Models, model, value =>
        {
            vm.Update(s => s with { Model = value.Id });
            status.Text = ModelStatus(value);
        });
        Select(panel, "Language", Languages, vm.Settings.Language, value => vm.Update(s => s with { Language = value }));
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(progress);
        var row = Row(panel);
        row.Children.Add(AsyncButton("Download selected model", async () =>
        {
            if (transfer != null) { status.Text = "A model download is already active."; return; }
            var selected = ModelCatalog.Get(vm.Settings.Model);
            if (!Confirm($"Download {selected.Display} ({selected.Size}) from Hugging Face?")) return;
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            transfer = token;
            status.Text = "Downloading " + selected.Display;
            try
            {
                await vm.Models.DownloadAsync(selected.Id, new Progress<double>(value => progress.Value = value), token.Token);
                status.Text = selected.Display + " verified and ready.";
                vm.RefreshModelStatus();
            }
            catch (OperationCanceledException) { status.Text = "Download canceled or timed out."; }
            catch (Exception error) { status.Text = "Download failed · " + error.Message; }
            finally { transfer = null; }
        }, primary: true));
        row.Children.Add(ActionButton("Cancel download", StopDownloads));
    }

    private string ModelStatus(ModelSpec model) => vm.Models.IsInstalled(model.Id) ? "Model file available" : "Download required for " + model.Display;

    private void BuildProcessing()
    {
        Heading("TEXT FORMATTING", "AI Processing", "Keep the raw transcription and choose how ChudType polishes your text.");
        var local = Card("Cleanup", "ChudGPT Dictation v1 removes some spoken English fillers. Whisper provides automatic punctuation.");
        Select(local, "Cleanup mode", new[] { "Raw", "Clean", "Professional", "Casual", "Developer", "Custom", "ChudGPT Dictation" }, vm.Settings.Cleanup,
            value => vm.Update(s => s with { Cleanup = value }));
        Select(local, "Developer vocabulary", TextCleanup.DeveloperProfiles, vm.Settings.DeveloperProfile,
            value => vm.Update(s => s with { DeveloperProfile = value }));
        Toggle(local, "Convert spoken programming punctuation in Developer mode", vm.Settings.SpokenCodeSymbols,
            value => vm.Update(s => s with { SpokenCodeSymbols = value }));
        Field(local, "Custom rewrite instructions · for an opted-in online provider", vm.Settings.CustomStyle,
            value => vm.Update(s => s with { CustomStyle = value }));
        var provider = Card("Provider", "Online cleanup sends transcript text and shows the rewrite for review. Microphone audio stays on this device.");
        Select(provider, "Provider", new[] { "Local only", "ChudGPT", "Custom" }, vm.Settings.Provider,
            value => UpdateProvider(s => s with { Provider = value }));
        Select(provider, "ChudGPT model", RemoteCleanup.Models, vm.Settings.RemoteModel,
            value => UpdateProvider(s => s with { RemoteModel = value }));
        Field(provider, "HTTPS endpoint · blank uses the official ChudGPT route", vm.Settings.RemoteEndpoint,
            value => UpdateProvider(s => s with { RemoteEndpoint = value }));
        Toggle(provider, "Allow ChudType to send transcript text to the selected provider", vm.Settings.RemoteConsent,
            value => vm.Update(s => s with { RemoteConsent = value }));
        var result = AddText(provider, "", "Muted");
        provider.Children.Add(AsyncButton("Test connection", async () =>
        {
            result.Text = "Checking the endpoint…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await remote.TestAsync(vm.Settings, timeout.Token);
                result.Text = "Endpoint responded with JSON. Inference availability is checked during dictation.";
            }
            catch (Exception error) { result.Text = "Connection check failed · " + error.Message; }
        }));
    }

    private void UpdateProvider(Func<Preferences, Preferences> change)
    {
        vm.Update(change);
        Navigate("AI Processing");
    }

    private async void StopModelClick(object sender, RoutedEventArgs args) => await vm.Session.StopModelAsync();
}
