using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChudType.Core;
using ChudType.Speech;
using ChudType.Storage;
using ChudType.Windows;
using Microsoft.Win32;

namespace ChudType.UI;

internal sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly LocalStore store;
    private readonly GlobalShortcut shortcut;
    public DashboardViewModel(LocalStore store, GlobalShortcut shortcut, DictationSession session, ModelCatalog models)
    {
        this.store = store;
        this.shortcut = shortcut;
        Session = session;
        Models = models;
        Settings = store.Preferences;
        session.Changed += () =>
        {
            Notify(nameof(Status));
            Notify(nameof(Level));
            Notify(nameof(DictateLabel));
            Notify(nameof(Latest));
            RefreshModelStatus();
        };
        session.LevelChanged += () => Notify(nameof(Level));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SettingsChanged;
    public Preferences Settings { get; private set; }
    public DictationSession Session { get; }
    public ModelCatalog Models { get; }
    public string Status => Session.Status;
    public double Level => Math.Clamp(Math.Sqrt(Session.Level) * 1.8, 0, 1);
    public string DictateLabel => Session.State switch
    {
        DictationState.Listening => "Stop and transcribe",
        DictationState.Starting or DictationState.Transcribing or DictationState.SendingText or DictationState.Review => "Cancel dictation",
        _ => "Start test dictation"
    };
    public string Latest => Session.Latest?.Text ?? "";
    public bool CanStopModel => Session.CanStopModel;
    public string ModelStatus
    {
        get
        {
            var selected = ModelCatalog.Get(Settings.Model);
            var active = Session.ActiveModel is { } id ? ModelCatalog.Get(id) : selected;
            var loaded = ModelCatalog.Models.FirstOrDefault(model =>
                Path.GetFileNameWithoutExtension(model.FileName) == Session.LoadedModel);
            string detail = Session.IsUnloadingModel ? "Stopping…" : Session.State switch
            {
                DictationState.Starting => "Opening microphone",
                DictationState.Listening => "Recording",
                DictationState.Transcribing => "Transcribing",
                DictationState.SendingText => "Sending text for cleanup",
                _ when loaded == selected => "Loaded · idle",
                _ when loaded is not null => $"{loaded.Display} loaded · idle",
                _ when Models.IsInstalled(selected.Id) => "Ready to use",
                _ => "Download required"
            };
            if (active != selected) detail = $"{active.Display} · {detail}";
            return $"Selected model: {selected.Display} · {detail}";
        }
    }

    public void RefreshModelStatus()
    {
        Notify(nameof(ModelStatus));
        Notify(nameof(CanStopModel));
    }

    public void CaptureShortcut(bool value) => shortcut.IsCapturing = value;

    public void SetShortcut(string value)
    {
        if (value == Settings.Shortcut) shortcut.Configure(value);
        else Update(options => options with { Shortcut = value });
    }

    public void Update(Func<Preferences, Preferences> change)
    {
        var next = change(Settings);
        if (next.Provider != Settings.Provider || next.RemoteEndpoint != Settings.RemoteEndpoint || next.RemoteModel != Settings.RemoteModel)
            next = next with { RemoteConsent = false };
        if (next == Settings) return;
        if (next.Shortcut != Settings.Shortcut) shortcut.Configure(next.Shortcut);
        if (next.StartWithWindows != Settings.StartWithWindows) SetStartup(next.StartWithWindows);
        store.Preferences = next;
        Settings = next;
        if (!Settings.MicConsent && Session.IsBusy) Session.Cancel();
        store.Prune(Settings);
        RefreshModelStatus();
        SettingsChanged?.Invoke();
    }

    public Task TestDictationAsync()
    {
        if (Session.State == DictationState.Listening) return Session.StopAsync();
        if (Session.IsBusy)
        {
            Session.Cancel();
            return Task.CompletedTask;
        }
        return Session.StartAsync(test: true);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CHUDAI.ChudType", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("CHUDAI.ChudType", throwOnMissingValue: false);
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
