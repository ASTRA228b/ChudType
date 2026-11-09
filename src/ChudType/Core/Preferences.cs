namespace ChudType.Core;

public sealed record Preferences
{
    public string Microphone { get; init; } = "";
    public string Model { get; init; } = "base";
    public string Language { get; init; } = "auto";
    public string Mode { get; init; } = "Hold";
    public string Shortcut { get; init; } = "Ctrl+Shift+Space";
    public string Overlay { get; init; } = "Always";
    public string Position { get; init; } = "Bottom";
    public FloatingPosition? FloatingPosition { get; init; }
    public string Cleanup { get; init; } = "Clean";
    public string DeveloperProfile { get; init; } = "C#";
    public bool SpokenCodeSymbols { get; init; }
    public string CustomStyle { get; init; } = "";
    public string Provider { get; init; } = "Local only";
    public string RemoteModel { get; init; } = "plato";
    public string RemoteEndpoint { get; init; } = "";
    public bool RemoteConsent { get; init; }
    public bool History { get; init; }
    public bool Statistics { get; init; }
    public int HistoryDays { get; init; } = 30;
    public bool Preview { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool Light { get; init; }
    public string Accent { get; init; } = "Violet";
    public double Sensitivity { get; init; } = 0.012;
    public bool MicConsent { get; init; }
    public bool SetupComplete { get; init; }
}

public sealed record FloatingPosition(string Monitor, double X, double Y);

public sealed record Vocabulary(string Heard, string Preferred, string Language = "", string Hint = "");
public sealed record Snippet(string Trigger, string Output, bool Enabled = true);
public sealed record Transcript(long Id, DateTimeOffset Time, string Raw, string Text, string Language, double Seconds, string Model, string Provider, int Words)
{
    public string DateLabel => Time.ToLocalTime().ToString("g");
    public string Details => $"{Language} · {Words} words · {Seconds:F1}s · {Provider}";
}

public sealed record Usage(int Words, int Today, int Week, double Seconds, int Sessions, int Remote, string Languages);
public enum DictationState { Idle, Starting, Listening, Transcribing, SendingText, Review, Completed, Error }
public sealed record SpeechResult(string Text, string Language);
