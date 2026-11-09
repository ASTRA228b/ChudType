using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChudType.Audio;

public sealed record MicrophoneDevice(string Id, string Name);
public sealed record CapturedAudio(short[] Samples, double Seconds);

internal interface IMicrophoneRecording : IDisposable
{
    double Level { get; }
    double Threshold { get; set; }
    double Seconds { get; }
    double SilenceSeconds { get; }
    bool HeardVoice { get; }
    Task Stopped { get; }
    Task<CapturedAudio> FinishAsync();
}

public sealed class Microphone
{
    public IReadOnlyList<MicrophoneDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<MicrophoneDevice> { new("", "Windows default microphone") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        }
        return devices;
    }

    public Recording Start(string id) => new(id);

    public sealed class Recording : IMicrophoneRecording
    {
        public const int SampleRate = 16000;
        public const int LimitSeconds = 300;
        private readonly MMDeviceEnumerator enumerator = new();
        private readonly MMDevice device;
        private readonly WasapiCapture capture;
        private readonly MemoryStream pcm = new();
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sampleCount;
        private double level;
        private int lastVoiceSample;

        public Recording(string id)
        {
            MMDevice? selected = null;
            WasapiCapture? input = null;
            try
            {
                selected = string.IsNullOrEmpty(id)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                    : enumerator.GetDevice(id);
                input = new WasapiCapture(selected, useEventSync: false, audioBufferMillisecondsLength: 40)
                {
                    WaveFormat = new WaveFormat(SampleRate, 16, 1)
                };
                input.DataAvailable += OnData;
                input.RecordingStopped += (_, args) =>
                {
                    if (args.Exception is { } error) stopped.TrySetException(error);
                    else stopped.TrySetResult();
                };
                input.StartRecording();
                capture = input;
                device = selected;
            }
            catch
            {
                input?.Dispose();
                selected?.Dispose();
                enumerator.Dispose();
                pcm.Dispose();
                throw;
            }
        }

        public double Level => Volatile.Read(ref level);
        public double Threshold { get; set; } = 0.012;
        public double Seconds => Volatile.Read(ref sampleCount) / (double)SampleRate;
        public double SilenceSeconds => (Volatile.Read(ref sampleCount) - Volatile.Read(ref lastVoiceSample)) / (double)SampleRate;
        public bool HeardVoice => Volatile.Read(ref lastVoiceSample) > 0;
        public Task Stopped => stopped.Task;

        private void OnData(object? sender, WaveInEventArgs args)
        {
            int bytes = (int)Math.Min(args.BytesRecorded, LimitSeconds * SampleRate * 2L - pcm.Length) & ~1;
            if (bytes == 0) return;
            var samples = MemoryMarshal.Cast<byte, short>(args.Buffer.AsSpan(0, bytes));
            double rms = VoiceActivity.Rms(samples);
            pcm.Write(args.Buffer, 0, bytes);
            Volatile.Write(ref level, rms);
            Volatile.Write(ref sampleCount, (int)pcm.Length / 2);
            if (rms >= Threshold) Volatile.Write(ref lastVoiceSample, sampleCount);
        }

        public async Task<CapturedAudio> FinishAsync()
        {
            capture.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var samples = MemoryMarshal.Cast<byte, short>(pcm.GetBuffer().AsSpan(0, (int)pcm.Length)).ToArray();
            return new(samples, samples.Length / (double)SampleRate);
        }

        public void Dispose()
        {
            capture.Dispose();
            device.Dispose();
            enumerator.Dispose();
            pcm.Dispose();
        }
    }
}
