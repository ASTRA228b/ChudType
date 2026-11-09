using System.IO;
using System.Windows.Input;
using ChudType.Audio;
using ChudType.Core;
using ChudType.Storage;
using ChudType.UI;
using ChudType.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChudType.Tests;

public class CoreTests
{
    [Fact]
    public void FloatingPositionRestoresOnAMonitorWithNegativeCoordinates()
    {
        var work = new Native.Rect { Left = -1920, Top = -120, Right = 0, Bottom = 920 };
        var point = new Native.Point { X = -1600, Y = 660 };
        var saved = Overlay.SavePosition("Display 2", point, work);
        Assert.Equal(point, Overlay.RestorePosition(saved, work));

        var resized = new Native.Rect { Left = -1200, Top = 0, Right = 0, Bottom = 800 };
        Assert.Equal(new Native.Point { X = -1000, Y = 600 }, Overlay.RestorePosition(saved, resized));
    }

    [Fact]
    public void FloatingBarStaysInTheWorkAreaWhenItExpands()
    {
        var work = new Native.Rect { Left = -1000, Top = 50, Right = 0, Bottom = 750 };
        var requested = new Native.Point { X = -30, Y = 725 };
        Assert.Equal(new Native.Point { X = -300, Y = 660 }, Overlay.ClampToWorkArea(requested, work, 300, 90));
        Assert.Equal(new Native.Point { X = -1000, Y = 50 }, Overlay.ClampToWorkArea(requested, work, 1500, 900));
    }

    [Fact]
    public void EnterIsConsumedThroughReleaseEvenWhenDictationFinishes()
    {
        var key = new GlobalShortcut.EnterKey();
        Assert.True(key.Handle(down: true, capture: true, out bool pressed));
        Assert.True(pressed);
        Assert.True(key.Handle(down: true, capture: false, out pressed));
        Assert.False(pressed);
        Assert.True(key.Handle(down: false, capture: false, out pressed));
        Assert.False(pressed);
        Assert.False(key.Handle(down: true, capture: false, out pressed));
        Assert.False(key.Handle(down: true, capture: true, out pressed));
        Assert.False(pressed);
        Assert.False(key.Handle(down: false, capture: false, out _));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(62, "01:02")]
    [InlineData(300, "05:00")]
    public void FloatingBarShowsElapsedMinutesAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, Overlay.FormatElapsed(seconds));

    [Theory]
    [InlineData(Key.Space, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Space")]
    [InlineData(Key.D5, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+5")]
    [InlineData(Key.A, ModifierKeys.None, null)]
    [InlineData(Key.A, ModifierKeys.Windows | ModifierKeys.Control, null)]
    public void KeyCaptureSavesSupportedChords(Key key, ModifierKeys modifiers, string? expected)
    {
        string? result = GlobalShortcut.Shortcut.FromKeys(key, modifiers);
        Assert.Equal(expected, result);
        if (result is not null) Assert.Equal((uint)KeyInterop.VirtualKeyFromKey(key), GlobalShortcut.Shortcut.Parse(result).Key);
    }

    private static readonly DictationModel LocalModel = new(() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "dictation-v1.json")));
    private static readonly TextCleanup Cleaner = new(LocalModel);

    [Theory]
    [InlineData("Could you, um, check the calendar?", "Could you check the calendar?")]
    [InlineData("Uh, I'll look in the other folder.", "I'll look in the other folder.")]
    [InlineData("The clip says \"um wait for me\".", "The clip says \"um wait for me\".")]
    [InlineData("Please spell um for the transcript.", "Please spell um for the transcript.")]
    [InlineData("The file was /um/defaults.json.", "The file was /um/defaults.json.")]
    [InlineData("We need um four of these.", "We need four of these.")]
    [InlineData("The width is 5 um long.", "The width is 5 um long.")]
    public void DedicatedModelEditsOnlyCandidateFillers(string input, string expected) => Assert.Equal(expected, LocalModel.Apply(input));

    [Fact]
    public void CleanupPreservesUnicodeAndAppliesOnlyMatchingWholeWords()
    {
        var settings = new Preferences();
        Vocabulary[] dictionary = [new("chudgpt", "ChudGPT", "es"), new("mañana", "MAÑANA", "fr")];
        Assert.Equal("chudgptx ChudGPT mañana 日本語", Cleaner.Apply("chudgptx chudgpt mañana 日本語", "es", settings, dictionary, []));
        Assert.Equal("chudgptx chudgpt MAÑANA 日本語", Cleaner.Apply("chudgptx chudgpt mañana 日本語", "fr", settings, dictionary, []));
    }

    [Theory]
    [InlineData("Insert snippet Bonjour.", true, "Bonjour, André 👋")]
    [InlineData("Insert snippet Bonjour.", false, "Insert snippet Bonjour.")]
    [InlineData("Could you insert snippet Bonjour?", true, "Could you insert snippet Bonjour?")]
    public void SnippetsRequireAnEnabledExactTrigger(string input, bool enabled, string expected) =>
        Assert.Equal(expected, Cleaner.Apply(input, "en", new(), [], [new("Bonjour", "Bonjour, André 👋", enabled)]));

    [Fact]
    public void SilenceAndShortSpikesAreNotTranscribed()
    {
        var buffer = new short[16000 * 2];
        Assert.Empty(VoiceActivity.Segment(buffer, 0.012));
        buffer[9000] = 12000;
        Assert.Empty(VoiceActivity.Segment(buffer, 0.012));
        Array.Fill(buffer, (short)1000, 8000, 8000);
        var speech = Assert.Single(VoiceActivity.Segment(buffer, 0.012));
        Assert.InRange(speech.Offset, 4000, 5000);
        Assert.InRange(speech.Count, 14_000, 16_000);
    }

    [Fact]
    public void LongSpeechUsesBoundedChunks()
    {
        var buffer = Enumerable.Repeat((short)1100, 16000 * 55).ToArray();
        var chunks = VoiceActivity.Segment(buffer, 0.012);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Count, 1, 27 * 16000));
    }

    [Fact]
    public void PreferencesHistoryAndStatisticsPersistInVersionedDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), "ChudType.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new LocalStore(path);
            var settings = new Preferences { Language = "ja", Statistics = true };
            store.Preferences = settings;
            var item = new Transcript(0, DateTimeOffset.Now, "東京 raw", "東京", "ja", 2, "tiny", "Local", 1);
            store.Record(item, settings);
            var reopened = new LocalStore(path);
            Assert.Equal(settings, reopened.Preferences);
            Assert.Empty(reopened.History());
            Assert.Equal(1, reopened.GetUsage().Today);
            reopened.Record(item, settings with { History = true });
            var saved = Assert.Single(reopened.History("東京"));
            Assert.Equal("東京 raw", saved.Raw);
            reopened.Edit(saved.Id, "こんにちは");
            Assert.Equal("こんにちは", Assert.Single(reopened.History()).Text);
            using var db = new SqliteConnection($"Data Source={Path.Combine(path, "chudtype.db")};Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void ShortcutParsingRequiresSupportedNonModifierKey()
    {
        Assert.Equal(new GlobalShortcut.Shortcut(0x20, 6), GlobalShortcut.Shortcut.Parse("Ctrl + Shift + Space"));
        Assert.Equal(new GlobalShortcut.Shortcut(0x35, 3), GlobalShortcut.Shortcut.Parse("ctrl+alt+5"));
        Assert.Throws<FormatException>(() => GlobalShortcut.Shortcut.Parse("Space"));
        Assert.Throws<FormatException>(() => GlobalShortcut.Shortcut.Parse("Win+Ctrl+A"));
        Assert.Throws<FormatException>(() => GlobalShortcut.Shortcut.Parse("Ctrl+13"));
    }
}
