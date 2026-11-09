using System.Text;
using ChudType.Audio;
using ChudType.Core;
using NAudio.Wave;
using Whisper.net;

namespace ChudType.Speech;

public interface ISpeechEngine
{
    string? LoadedModel { get; }
    Task<SpeechResult> TranscribeAsync(string path, short[] samples, string language, double sensitivity, CancellationToken cancellation);
    Task UnloadAsync();
}

public sealed class WhisperEngine : ISpeechEngine, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WhisperFactory? factory;
    private string? currentPath;
    private DateTime currentWrite;

    public string? LoadedModel => Path.GetFileNameWithoutExtension(Volatile.Read(ref currentPath));

    public async Task<SpeechResult> TranscribeAsync(string path, short[] samples, string language, double sensitivity, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            return await Task.Run(async () =>
            {
                var chunks = VoiceActivity.Segment(samples, sensitivity);
                if (chunks.Count == 0) return new SpeechResult("", language);
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (factory is null || path != currentPath || lastWrite != currentWrite)
                {
                    ReleaseModel();
                    factory = WhisperFactory.FromPath(path);
                    Volatile.Write(ref currentPath, path);
                    currentWrite = lastWrite;
                }

                var text = new StringBuilder();
                string detected = language;
                foreach (var chunk in chunks)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await using var processor = factory.CreateBuilder().WithLanguage(detected)
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8)).Build();
                    var audio = Array.ConvertAll(chunk.ToArray(), value => value / 32768f);
                    await foreach (var segment in processor.ProcessAsync(audio, cancellation))
                    {
                        string part = segment.Text.Trim();
                        if (part.Length == 0) continue;
                        if (text.Length > 0) text.Append(' ');
                        text.Append(part);
                        if (detected == "auto" && !string.IsNullOrWhiteSpace(segment.Language))
                            detected = segment.Language;
                    }
                }
                return new SpeechResult(text.ToString(), detected);
            }, cancellation);
        }
        finally { gate.Release(); }
    }

    public async Task UnloadAsync()
    {
        await gate.WaitAsync();
        try { await Task.Run(ReleaseModel); }
        finally { gate.Release(); }
    }

    private void ReleaseModel()
    {
        var loaded = factory;
        factory = null;
        Volatile.Write(ref currentPath, null);
        loaded?.Dispose();
    }

    public void Dispose()
    {
        ReleaseModel();
        gate.Dispose();
    }
}
