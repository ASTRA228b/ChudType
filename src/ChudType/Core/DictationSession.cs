using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ChudType.Audio;
using ChudType.Speech;
using ChudType.Storage;
using ChudType.Windows;
using Microsoft.Extensions.Logging;

namespace ChudType.Core;

internal sealed class DictationSession(LocalStore store, ModelCatalog models, Func<string, IMicrophoneRecording> startRecording, ISpeechEngine speech,
    IDictationTarget target, TextCleanup cleanup, ITextProvider remote, ILogger<DictationSession> log)
{
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(65) };
    private IMicrophoneRecording? recording;
    private CancellationTokenSource? run;
    private TextTarget.Snapshot? field;
    private Preferences? activeSettings;
    private bool isTest;
    private bool isHold;
    private bool handsFree;
    private bool stopping;
    private bool finishWhenReady;
    private bool confirmWithEnter;
    public bool IsUnloadingModel { get; private set; }

    public DictationState State { get; private set; }
    public bool IsBusy => run != null || State == DictationState.Review || IsUnloadingModel;
    public string? ActiveModel => run is not null ? activeSettings?.Model : null;
    public string? LoadedModel => speech.LoadedModel;
    public bool CanStopModel => !IsUnloadingModel && (run is not null || LoadedModel is not null);
    public nint Anchor { get; private set; }
    public string Status { get; private set; } = "Ready";
    public Transcript? Latest { get; private set; }
    public double Level => recording?.Level ?? 0;
    public double Seconds => recording?.Seconds ?? Latest?.Seconds ?? 0;
    public bool Enabled { get; private set; } = true;
    public event Action? Changed;
    public event Action? LevelChanged;

    public void Initialize() => tick.Tick += OnTick;

    public void SetEnabled(bool value)
    {
        Enabled = value;
        if (!value) Cancel();
        if (!IsBusy) Publish(DictationState.Idle, value ? "Ready" : "Dictation paused");
    }

    public void ShortcutPressed()
    {
        if (!Enabled) return;
        var options = store.Preferences;
        if (!IsBusy)
            _ = StartAsync(test: false, hold: options.Mode == "Hold", continuous: options.Mode == "Hands-free");
        else if (State == DictationState.Listening && !isHold)
            _ = StopAsync();
    }

    public void ShortcutReleased()
    {
        if (!isHold) return;
        if (State == DictationState.Listening) _ = StopAsync();
        else if (State == DictationState.Starting) Cancel();
    }

    public bool CanCaptureEnter() => Enabled && !isTest && Anchor != 0 && target.CurrentWindow == Anchor &&
        (State is DictationState.Starting or DictationState.Listening or DictationState.Transcribing or DictationState.SendingText ||
         State == DictationState.Review && confirmWithEnter && field is { Editable: true, Password: false } && target.IsFocused(field));

    public Task FinishWithEnterAsync()
    {
        if (!CanCaptureEnter()) return Task.CompletedTask;
        if (State == DictationState.Starting) finishWhenReady = true;
        if (State == DictationState.Listening) return StopAsync();
        return State == DictationState.Review ? AcceptAsync(raw: false) : Task.CompletedTask;
    }

    public async Task StartAsync(bool test, bool hold = false, bool continuous = false)
    {
        if (!Enabled || IsBusy) return;
        var options = store.Preferences;
        Anchor = target.CurrentWindow;
        if (!options.MicConsent)
        {
            Publish(DictationState.Error, "Allow microphone access in ChudType setup.");
            return;
        }
        if (!models.IsInstalled(options.Model))
        {
            Publish(DictationState.Error, "Download the selected Whisper model in Models.");
            return;
        }

        var operation = new CancellationTokenSource();
        run = operation;
        activeSettings = options;
        isTest = test;
        isHold = hold;
        handsFree = continuous;
        finishWhenReady = false;
        confirmWithEnter = false;
        Latest = null;
        try
        {
            Publish(DictationState.Starting, "Opening microphone");
            field = test ? null : await target.CaptureAsync(Anchor, operation.Token);
            if (field is { Password: true })
                throw new InvalidOperationException("Dictation is disabled in password fields.");
            operation.Token.ThrowIfCancellationRequested();
            recording = startRecording(options.Microphone);
            recording.Threshold = options.Sensitivity;
            tick.Start();
            Publish(DictationState.Listening, "Listening");
            if (finishWhenReady) await StopAsync();
        }
        catch (OperationCanceledException) { Finish(operation, DictationState.Idle, "Canceled"); }
        catch (Exception error)
        {
            bool opened = recording is not null;
            log.LogWarning(error, "Could not start dictation during {Stage}", opened ? "listening display" : "microphone setup");
            Finish(operation, DictationState.Error, opened ? "Recording stopped because the dictation display failed." : error switch
            {
                UnauthorizedAccessException => "Windows denied microphone access. Check microphone privacy settings.",
                COMException => "Microphone unavailable. Check the selected input and Windows privacy settings.",
                InvalidOperationException => error.Message,
                _ => "Could not start recording. Check your microphone and try again."
            });
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (recording is null) return;
        if (recording.Stopped.IsCompleted || recording.Seconds >= Microphone.Recording.LimitSeconds ||
            handsFree && (recording.HeardVoice && recording.SilenceSeconds >= 3 || recording.Seconds >= 15 && !recording.HeardVoice))
            _ = StopAsync();
        else LevelChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (stopping || recording is null || run is null || activeSettings is null) return;
        var operation = run;
        var options = activeSettings;
        var input = recording;
        recording = null;
        stopping = true;
        tick.Stop();
        try
        {
            CapturedAudio audio;
            try
            {
                if (!operation.IsCancellationRequested) Publish(DictationState.Transcribing, "Transcribing on device");
                audio = await input.FinishAsync();
            }
            finally { input.Dispose(); }
            operation.Token.ThrowIfCancellationRequested();
            var path = await models.OpenVerifiedAsync(options.Model, operation.Token);
            var timer = Stopwatch.StartNew();
            var result = await speech.TranscribeAsync(path, audio.Samples, options.Language, options.Sensitivity, operation.Token);
            log.LogInformation("Local recognition took {ElapsedMs} ms for {AudioSeconds:F2}s of audio using {Model}",
                timer.ElapsedMilliseconds, audio.Seconds, options.Model);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                Finish(operation, DictationState.Error, "No speech was recognized. Try speaking closer to the microphone.");
                return;
            }
            var text = cleanup.Apply(result.Text, result.Language, options, store.Vocabulary, store.Snippets);
            string provider = options.Cleanup == "ChudGPT Dictation" && result.Language == "en" ? "ChudGPT Dictation" : "Local";
            bool snippet = !string.Equals(text, result.Text, StringComparison.Ordinal) &&
                result.Text.Trim().StartsWith("insert snippet ", StringComparison.OrdinalIgnoreCase);
            bool remoteFallback = false;
            var currentOptions = store.Preferences;
            bool consentStillApplies = currentOptions.RemoteConsent && currentOptions.Provider == options.Provider &&
                currentOptions.RemoteEndpoint == options.RemoteEndpoint && currentOptions.RemoteModel == options.RemoteModel;
            if (!snippet && options.Cleanup != "Raw" && options.Provider != "Local only" && options.RemoteConsent && consentStillApplies)
            {
                Publish(DictationState.SendingText, "Sending transcript text");
                try
                {
                    text = await remote.RewriteAsync(text, options, operation.Token);
                    provider = options.Provider == "Custom" ? "Custom" : "ChudGPT";
                }
                catch (Exception error) when (!operation.IsCancellationRequested)
                {
                    log.LogWarning(error, "Remote cleanup failed. Using local text");
                    remoteFallback = true;
                }
            }

            operation.Token.ThrowIfCancellationRequested();
            Latest = new(0, DateTimeOffset.Now, result.Text, text, result.Language, audio.Seconds, options.Model,
                provider, TextCleanup.WordCount(text));
            if (isTest)
            {
                Save(Latest, options);
                Finish(operation, DictationState.Completed, remoteFallback ? "Remote unavailable. Local test transcript ready." : "Test transcript ready");
            }
            else if (options.Preview || provider is "ChudGPT" or "Custom")
            {
                confirmWithEnter = field is { Editable: true, Password: false } && target.IsFocused(field);
                string review = remoteFallback ? "Remote unavailable · review local transcript" : "Review transcript";
                Finish(operation, DictationState.Review, confirmWithEnter ? review + " · press Enter to insert" : review);
            }
            else
            {
                string? problem = await target.InsertAsync(field, text, operation.Token);
                if (problem is not null) log.LogInformation("Transcript insertion deferred: {Reason}", problem);
                Save(Latest, options);
                Finish(operation, problem is null ? DictationState.Completed : DictationState.Review,
                    problem ?? (remoteFallback ? "Inserted local text · remote unavailable" : "Inserted"));
            }
        }
        catch (OperationCanceledException) { Finish(operation, DictationState.Idle, "Canceled"); }
        catch (Exception error)
        {
            log.LogWarning(error, "Dictation failed");
            Finish(operation, DictationState.Error, error switch
            {
                COMException => "The microphone disconnected or became unavailable.",
                InvalidDataException or FileNotFoundException => error.Message,
                TimeoutException => "The microphone did not stop in time.",
                _ => "Transcription failed. Check the speech model and local event log."
            });
        }
        finally { stopping = false; }
    }

    public async Task AcceptAsync(bool raw)
    {
        if (State != DictationState.Review || Latest is null || activeSettings is null || run is not null) return;
        var operation = new CancellationTokenSource();
        run = operation;
        var accepted = raw ? Latest with { Text = Latest.Raw, Words = TextCleanup.WordCount(Latest.Raw) } : Latest;
        try
        {
            var problem = await target.InsertAsync(field, accepted.Text, operation.Token);
            if (problem is not null) log.LogInformation("Transcript insertion deferred: {Reason}", problem);
            confirmWithEnter = false;
            Latest = accepted;
            if (problem is null) Save(accepted, activeSettings);
            Finish(operation, problem is null ? DictationState.Completed : DictationState.Review, problem ?? "Inserted");
        }
        catch (OperationCanceledException) { Finish(operation, DictationState.Idle, "Canceled"); }
        catch (Exception error)
        {
            confirmWithEnter = false;
            log.LogWarning(error, "Text insertion failed");
            Finish(operation, DictationState.Review, "Text insertion failed. The transcript is ready to copy.");
        }
    }

    public void Copied()
    {
        if (State != DictationState.Review || Latest is null || activeSettings is null || run is not null) return;
        Save(Latest, activeSettings);
        Publish(DictationState.Completed, "Copied");
    }

    public void Cancel()
    {
        if (run is not null)
        {
            run.Cancel();
            if (recording is not null) _ = StopAsync();
        }
        else if (State == DictationState.Review)
        {
            Latest = null;
            Publish(DictationState.Idle, "Canceled");
        }
    }

    public async Task ShutdownAsync()
    {
        Cancel();
        while (run != null || IsUnloadingModel) await Task.Delay(30);
        tick.Stop();
    }

    public async Task StopModelAsync()
    {
        if (!CanStopModel) return;
        IsUnloadingModel = true;
        try
        {
            Changed?.Invoke();
            if (run is not null) Cancel();
            while (run is not null) await Task.Delay(30);
            await speech.UnloadAsync();
        }
        catch (Exception error)
        {
            log.LogWarning(error, "Could not stop the speech model");
            Status = "Could not stop the speech model.";
        }
        finally
        {
            IsUnloadingModel = false;
            Changed?.Invoke();
        }
    }

    private void Save(Transcript item, Preferences options)
    {
        try
        {
            var current = store.Preferences;
            store.Record(item, options with { History = options.History && current.History, Statistics = options.Statistics && current.Statistics });
            store.Prune(current);
        }
        catch (Exception error) { log.LogWarning(error, "Could not store dictation activity"); }
    }

    private void Finish(CancellationTokenSource operation, DictationState state, string message)
    {
        if (run != operation) return;
        tick.Stop();
        var input = recording;
        recording = null;
        try { input?.Dispose(); }
        catch (Exception error) { log.LogWarning(error, "Could not release the microphone"); }
        run = null;
        operation.Dispose();
        Publish(state, message);
    }

    private void Publish(DictationState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
