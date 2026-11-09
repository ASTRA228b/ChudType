namespace ChudType.Audio;

public static class VoiceActivity
{
    private const int Rate = Microphone.Recording.SampleRate;
    private const int Frame = Rate / 50;

    public static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (double sample in samples) sum += sample * sample;
        return Math.Sqrt(sum / samples.Length) / 32768;
    }

    public static IReadOnlyList<ArraySegment<short>> Segment(short[] samples, double sensitivity)
    {
        int frames = (samples.Length + Frame - 1) / Frame;
        var levels = new double[frames];
        for (int i = 0; i < frames; i++)
            levels[i] = Rms(samples.AsSpan(i * Frame, Math.Min(Frame, samples.Length - i * Frame)));

        var result = new List<ArraySegment<short>>();
        const int padding = 12;
        const int minimumVoiceFrames = 6;
        const int maximumFrames = 27 * 50;
        int start = -1, latestVoice = -1, voiceFrames = 0;
        for (int i = 0; i <= frames; i++)
        {
            bool voiced = i < frames && levels[i] >= sensitivity;
            if (voiced)
            {
                if (start < 0) start = Math.Max(0, i - padding);
                latestVoice = i;
                voiceFrames++;
            }

            if (start < 0) continue;
            bool limit = i - start >= maximumFrames;
            if (i < frames && i - latestVoice < 65 && !limit) continue;

            int end = Math.Min(i, latestVoice + padding + 1);
            if (limit)
            {
                // Choose the quietest boundary in the last 400 ms to reduce clipped words.
                end = Enumerable.Range(i - 20, 20).MinBy(index => levels[index]);
            }
            if (voiceFrames >= minimumVoiceFrames)
            {
                int offset = start * Frame;
                int length = Math.Min(samples.Length, end * Frame) - offset;
                result.Add(new(samples, offset, length));
            }
            if (limit) i = end - 1;
            start = latestVoice = -1;
            voiceFrames = 0;
        }
        return result;
    }
}
