using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Text;
using ChudType.Core;
using ChudType.Speech;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace ChudType.Tests;

public sealed class EngineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecognizesJfkFixtureWhenAssetsAreSupplied()
    {
        string? directory = Environment.GetEnvironmentVariable("CHUDTYPE_TEST_ASSETS");
        if (directory == null) return;
        string model = Path.Combine(directory, "ggml-tiny.bin");
        using var wav = new WaveFileReader(Path.Combine(directory, "jfk.wav"));
        Assert.Equal(16000, wav.WaveFormat.SampleRate);
        Assert.Equal(1, wav.WaveFormat.Channels);
        Assert.Equal(16, wav.WaveFormat.BitsPerSample);
        byte[] pcm = new byte[wav.Length];
        wav.ReadExactly(pcm);
        short[] samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        using var whisper = new WhisperEngine();
        for (int i = 0; i < 2; i++)
        {
            var timer = Stopwatch.StartNew();
            var result = await whisper.TranscribeAsync(model, samples, "auto", 0.006, CancellationToken.None);
            timer.Stop();
            output.WriteLine($"{(i == 0 ? "Cold" : "Warm")} {wav.TotalTime.TotalSeconds:F2}s audio: {timer.Elapsed.TotalSeconds:F3}s");
            output.WriteLine(result.Text);
            Assert.Contains("your country", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("en", result.Language);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => whisper.TranscribeAsync(model, samples, "en", .006, cancelled.Token));
        Assert.Equal("ggml-tiny", whisper.LoadedModel);
        await whisper.UnloadAsync();
        Assert.Null(whisper.LoadedModel);
    }

    [Fact]
    public async Task PlatoUsesStatelessSchemaAndRequiresConsent()
    {
        using var handler = new Handler("""{"reply":"Clear text.","source":"neural","model":"plato"}""");
        using var remote = new RemoteCleanup(handler);
        var options = new Preferences { Provider = "ChudGPT" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => remote.RewriteAsync("test", options, CancellationToken.None));
        Assert.Null(handler.Body);
        var result = await remote.RewriteAsync("test", options with { RemoteConsent = true }, CancellationToken.None);
        Assert.Equal("Clear text.", result);
        Assert.Equal("/api/plato/models/plato/chat", handler.Path);
        Assert.Contains("\"messages\"", handler.Body);
    }

    [Fact]
    public async Task MainUsesOneShotGenerationAndRejectsNonJson()
    {
        using var handler = new Handler("<html>offline</html>", "text/html");
        using var remote = new RemoteCleanup(handler);
        var options = new Preferences { Provider = "ChudGPT", RemoteModel = "pro", RemoteConsent = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => remote.RewriteAsync("test", options, CancellationToken.None));
        Assert.Equal("/api/models/pro/generate", handler.Path);
        Assert.Contains("\"message\"", handler.Body);
        Assert.DoesNotContain("session_id", handler.Body);
    }

    private sealed class Handler(string reply, string mediaType = "application/json") : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, mediaType) };
        }
    }
}
