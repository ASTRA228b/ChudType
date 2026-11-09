using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ChudType.Audio;
using ChudType.Core;
using ChudType.Speech;
using ChudType.Storage;
using ChudType.UI;
using ChudType.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChudType.Tests;

public sealed class ThemeTests(WpfTestFixture wpf) : IClassFixture<WpfTestFixture>
{
    [Fact]
    public Task SetupSelectorsUseReadableColorsInBothThemes() => wpf.RunAsync(app =>
    {
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (bool light in new[] { false, true })
        {
            applyTheme.Invoke(app, [new Preferences { Light = light }]);
            var selector = new ComboBox { ItemsSource = new[] { "Whisper Base", "Whisper Tiny" }, SelectedIndex = 0 };
            selector.Style = (Style)app.FindResource(typeof(ComboBox));
            selector.ApplyTemplate();
            var popup = Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
            var panel = Assert.IsType<Border>(popup.Child);
            var ink = ((SolidColorBrush)selector.Foreground).Color;
            var field = ((SolidColorBrush)app.Resources["Surface"]).Color;
            var page = ((SolidColorBrush)app.Resources["Canvas"]).Color;
            Assert.True(Contrast(ink, field) >= 7, $"Theme light={light}, text {ink}, field {field}");
            Assert.True(Contrast(ink, page) >= 7);
            Assert.True(Contrast(ink, ((SolidColorBrush)panel.Background).Color) >= 7);
        }
    });

    [Fact]
    public Task DictationPagesBindMicrophoneMeterToReadOnlyLevel() => wpf.RunAsync(_ =>
    {
        var directory = Directory.CreateTempSubdirectory("ChudType.Tests-");
        try
        {
            var store = new LocalStore(directory.FullName);
            store.Preferences = new Preferences { SetupComplete = true };
            using var http = new HttpClient();
            using var shortcut = new GlobalShortcut();
            using var target = new TextTarget();
            using var whisper = new WhisperEngine();
            using var remote = new RemoteCleanup();
            var models = new ModelCatalog(store, http);
            var microphone = new Microphone();
            var session = new DictationSession(store, models, microphone.Start, whisper, target,
                new TextCleanup(new DictationModel()), remote, NullLogger<DictationSession>.Instance);
            var vm = new DashboardViewModel(store, shortcut, session, models);
            var window = new MainWindow(vm, store, microphone, remote);
            try
            {
                AssertMicrophoneMeter(window);
                window.Navigate("Dictation");
                AssertMicrophoneMeter(window);
                window.Navigate("Setup");
                var page = Assert.IsType<StackPanel>(window.FindName("Page"));
                for (int i = 0; i < 3; i++)
                {
                    var next = Assert.Single(page.Children.OfType<WrapPanel>().SelectMany(row => row.Children.OfType<Button>()),
                        button => Equals(button.Content, "Continue"));
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                AssertMicrophoneMeter(window);
            }
            finally
            {
                window.Exiting = true;
                window.Close();
            }
        }
        finally { directory.Delete(recursive: true); }
    });

    [Fact]
    public Task DictationReleasesCaptureOnDisplayFailureAndOnModelStop() => wpf.RunAsync(_ =>
    {
        var directory = Directory.CreateTempSubdirectory("ChudType.Tests-");
        try
        {
            var store = new LocalStore(directory.FullName);
            store.Preferences = new Preferences { Model = "tiny", MicConsent = true };
            using var http = new HttpClient();
            using var shortcut = new GlobalShortcut();
            using var target = new TextTarget();
            using var remote = new RemoteCleanup();
            var models = new ModelCatalog(store, http);
            var model = ModelCatalog.Get("tiny");
            using (var file = File.Create(models.FilePath(model))) file.SetLength(model.Bytes);
            var speech = new FakeSpeechEngine();
            var failedCapture = new FakeRecording();
            var nextCapture = new FakeRecording();
            var captures = new Queue<FakeRecording>([failedCapture, nextCapture]);
            var session = new DictationSession(store, models, id => captures.Dequeue(), speech, target,
                new TextCleanup(new DictationModel()), remote, NullLogger<DictationSession>.Instance);
            var vm = new DashboardViewModel(store, shortcut, session, models);
            session.Initialize();

            void BrokenDisplay()
            {
                if (session.State == DictationState.Listening) throw new FormatException("Display failed.");
            }

            session.Changed += BrokenDisplay;
            session.StartAsync(test: true).GetAwaiter().GetResult();
            Assert.Equal(DictationState.Error, session.State);
            Assert.True(failedCapture.Disposed);
            Assert.False(session.IsBusy);
            Assert.Equal(0, failedCapture.FinishCalls);

            session.Changed -= BrokenDisplay;
            session.StartAsync(test: true).GetAwaiter().GetResult();
            Assert.Equal("Selected model: Whisper Tiny · Recording", vm.ModelStatus);
            Assert.True(vm.CanStopModel);
            session.StopModelAsync().GetAwaiter().GetResult();
            Assert.True(nextCapture.Disposed);
            Assert.Equal(1, nextCapture.FinishCalls);
            Assert.Null(speech.LoadedModel);
            Assert.False(vm.CanStopModel);
            Assert.Equal("Selected model: Whisper Tiny · Ready to use", vm.ModelStatus);
        }
        finally { directory.Delete(recursive: true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EnterFinishesDictationAndUsesItsOriginalField(bool preview) => wpf.RunAsync(async _ =>
    {
        string? assets = Environment.GetEnvironmentVariable("CHUDTYPE_TEST_ASSETS");
        if (assets is null) return;
        var directory = Directory.CreateTempSubdirectory("ChudType.Tests-");
        try
        {
            var store = new LocalStore(directory.FullName);
            store.Preferences = new Preferences { Model = "tiny", MicConsent = true, Preview = preview };
            using var http = new HttpClient();
            using var remote = new RemoteCleanup();
            var models = new ModelCatalog(store, http);
            var model = ModelCatalog.Get("tiny");
            File.Copy(Path.Combine(assets, model.FileName), models.FilePath(model));
            var target = new FakeTarget();
            var capture = new FakeRecording();
            var speech = new FakeSpeechEngine(new SpeechResult("A test sentence.", "en"));
            var session = new DictationSession(store, models, id => capture, speech, target,
                new TextCleanup(new DictationModel()), remote, NullLogger<DictationSession>.Instance);
            session.Initialize();
            await session.StartAsync(test: false);
            Assert.Equal(DictationState.Listening, session.State);
            Assert.True(session.CanCaptureEnter());

            target.CurrentWindow = 2;
            Assert.False(session.CanCaptureEnter());
            await session.FinishWithEnterAsync();
            Assert.Equal(DictationState.Listening, session.State);

            target.CurrentWindow = 1;
            await session.FinishWithEnterAsync();
            Assert.True(capture.Disposed);
            if (preview)
            {
                Assert.Equal(DictationState.Review, session.State);
                Assert.Null(target.Text);
                Assert.True(session.CanCaptureEnter());
                await session.FinishWithEnterAsync();
            }
            Assert.Equal(DictationState.Completed, session.State);
            Assert.Equal("A test sentence.", target.Text);
            Assert.False(session.CanCaptureEnter());
        }
        finally { directory.Delete(recursive: true); }
    });

    private sealed class FakeTarget : IDictationTarget
    {
        private readonly TextTarget.Snapshot original = new(1, 3, [42], false, true, 0);
        public nint CurrentWindow { get; set; } = 1;
        public string? Text { get; private set; }
        public event Action? ForegroundChanged { add { } remove { } }
        public bool IsFocused(TextTarget.Snapshot target) => target == original && CurrentWindow == original.Window;
        public Task<TextTarget.Snapshot> CaptureAsync(nint expectedWindow, CancellationToken cancellation)
        {
            Assert.Equal(original.Window, expectedWindow);
            return Task.FromResult(original);
        }
        public Task<string?> InsertAsync(TextTarget.Snapshot? target, string text, CancellationToken cancellation)
        {
            Assert.Same(original, target);
            Assert.Equal(original.Window, CurrentWindow);
            Text = text;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeRecording : IMicrophoneRecording
    {
        public double Level => 0;
        public double Threshold { get; set; }
        public double Seconds => 0;
        public double SilenceSeconds => 0;
        public bool HeardVoice => false;
        public Task Stopped { get; } = new TaskCompletionSource().Task;
        public bool Disposed { get; private set; }
        public int FinishCalls { get; private set; }
        public Task<CapturedAudio> FinishAsync()
        {
            FinishCalls++;
            return Task.FromResult(new CapturedAudio([], 0));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSpeechEngine(SpeechResult? result = null) : ISpeechEngine
    {
        public string? LoadedModel { get; private set; } = "ggml-tiny";
        public Task<SpeechResult> TranscribeAsync(string path, short[] samples, string language, double sensitivity,
            CancellationToken cancellation) => Task.FromResult(result ??
                throw new InvalidOperationException("Canceled audio must never be transcribed."));
        public Task UnloadAsync()
        {
            LoadedModel = null;
            return Task.CompletedTask;
        }
    }

    private static void AssertMicrophoneMeter(MainWindow window)
    {
        var page = Assert.IsType<StackPanel>(window.FindName("Page"));
        var meter = Assert.Single(page.Children.OfType<Border>().Select(x => x.Child)
            .OfType<StackPanel>().SelectMany(x => x.Children.OfType<ProgressBar>()));
        var binding = Assert.IsType<BindingExpression>(meter.GetBindingExpression(ProgressBar.ValueProperty));
        Assert.Equal(BindingMode.OneWay, binding.ParentBinding.Mode);
        window.Measure(new Size(990, 760));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(BindingStatus.Active, binding.Status);
        Assert.Equal(0, meter.Value);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte value)
        {
            double s = value / 255d;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        double left = Luminance(a), right = Luminance(b);
        return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
    }
}

public sealed class WpfTestFixture : IDisposable
{
    private readonly TaskCompletionSource<App> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;

    public WpfTestFixture()
    {
        thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                ready.SetResult(app);
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task RunAsync(Action<App> action)
    {
        var app = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await app.Dispatcher.InvokeAsync(() => action(app)).Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public async Task RunAsync(Func<App, Task> action)
    {
        var app = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await app.Dispatcher.InvokeAsync(() => action(app)).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        if (ready.Task.IsCompletedSuccessfully)
            ready.Task.Result.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
