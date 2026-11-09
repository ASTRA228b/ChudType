using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ChudType.Core;
using ChudType.Storage;
using Microsoft.Win32;

namespace ChudType.UI;

internal partial class MainWindow
{
    private void BuildHistory()
    {
        Heading("LOCAL TRANSCRIPTS", "History", "Search, edit, copy or export transcripts saved on this device.");
        var card = Card("Saved transcripts", vm.Settings.History ? "History is on." : "History is off. Existing entries can be deleted below.");
        var search = new TextBox();
        AutomationProperties.SetName(search, "Search history");
        card.Children.Add(search);
        var list = new ListBox { Height = 175, Margin = new Thickness(0, 9, 0, 10), SelectionMode = SelectionMode.Extended };
        card.Children.Add(list);
        var detail = AddText(card, "", "Muted");
        var edited = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(edited, "Edit transcript");
        card.Children.Add(edited);
        var raw = AddText(card, "", "Muted");
        list.SelectionChanged += (_, _) =>
        {
            var item = (list.SelectedItem as HistoryRow)?.Item;
            edited.Text = item?.Text ?? "";
            raw.Text = item is null ? "" : "Raw · " + item.Raw;
            detail.Text = item?.Details ?? "";
        };
        void Load() => list.ItemsSource = store.History(search.Text).Select(item => new HistoryRow(item)).ToList();
        search.TextChanged += (_, _) => Guard(Load);
        Load();

        var row = Row(card);
        row.Children.Add(ActionButton("Save edit", () =>
        {
            if (list.SelectedItem is HistoryRow selected) { store.Edit(selected.Item.Id, edited.Text); Load(); }
        }));
        row.Children.Add(ActionButton("Copy", () => Copy(edited.Text)));
        row.Children.Add(ActionButton("Copy raw", () => Copy((list.SelectedItem as HistoryRow)?.Item.Raw)));
        row.Children.Add(ActionButton("Export selected", () => Export(list.SelectedItems.Cast<HistoryRow>().Select(x => x.Item).ToArray(), "transcripts")));
        row.Children.Add(ActionButton("Delete selected", () =>
        {
            if (list.SelectedItems.Count == 0 || !Confirm("Delete the selected transcripts?")) return;
            foreach (var item in list.SelectedItems.Cast<HistoryRow>()) store.Delete(item.Item.Id);
            Load();
        }));
        row.Children.Add(ActionButton("Clear history", () =>
        {
            if (!Confirm("Delete all saved transcripts?")) return;
            store.ClearHistory();
            Load();
        }));
    }

    private sealed record HistoryRow(Transcript Item)
    {
        public override string ToString() => Item.DateLabel + "  ·  " + Item.Text.ReplaceLineEndings(" ")[..Math.Min(70, Item.Text.ReplaceLineEndings(" ").Length)];
    }

    private void BuildDictionary()
    {
        Heading("PERSONAL VOCABULARY", "Dictionary", "Match whole words or phrases and set the spelling you prefer.");
        var card = Card("Entries", "Language codes are optional. Pronunciation hints are stored for reference.");
        var search = Input(card, "Search", "");
        var list = new ListBox { Height = 150, Margin = new Thickness(0, 7, 0, 7) };
        card.Children.Add(list);
        var heard = Input(card, "Spoken text", "");
        var preferred = Input(card, "Preferred spelling", "");
        var language = Input(card, "Language code, e.g. en", "");
        var hint = Input(card, "Pronunciation hint", "");
        Vocabulary? selection = null;
        void Load() => list.ItemsSource = store.Vocabulary.Where(v => v.Heard.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
            || v.Preferred.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Select(v => new VocabularyRow(v)).ToList();
        list.SelectionChanged += (_, _) =>
        {
            selection = (list.SelectedItem as VocabularyRow)?.Item;
            heard.Text = selection?.Heard ?? "";
            preferred.Text = selection?.Preferred ?? "";
            language.Text = selection?.Language ?? "";
            hint.Text = selection?.Hint ?? "";
        };
        search.TextChanged += (_, _) => Guard(Load);
        Load();
        var row = Row(card);
        row.Children.Add(ActionButton("New", () => list.UnselectAll()));
        row.Children.Add(ActionButton("Save entry", () =>
        {
            var entry = new Vocabulary(heard.Text.Trim(), preferred.Text.Trim(), language.Text.Trim().ToLowerInvariant(), hint.Text.Trim());
            Validate(entry);
            var all = store.Vocabulary;
            if (selection != null) all.Remove(selection);
            all.RemoveAll(v => v.Heard.Equals(entry.Heard, StringComparison.OrdinalIgnoreCase) && v.Language == entry.Language);
            all.Add(entry);
            store.Vocabulary = all;
            Load();
        }, primary: true));
        row.Children.Add(ActionButton("Delete", () =>
        {
            if (selection is null) return;
            var all = store.Vocabulary;
            all.Remove(selection);
            store.Vocabulary = all;
            Load();
        }));
        row.Children.Add(ActionButton("Export", () => Export(store.Vocabulary, "dictionary")));
        row.Children.Add(ActionButton("Import", () =>
        {
            var imported = Import<List<Vocabulary>>();
            if (imported == null) return;
            foreach (var entry in imported) Validate(entry);
            if (!Confirm("Replace the current dictionary with the imported entries?")) return;
            store.Vocabulary = imported;
            Load();
        }));
    }

    private static void Validate(Vocabulary? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Heard) || string.IsNullOrWhiteSpace(value.Preferred) ||
            value.Language is null || value.Hint is null || value.Language.Length > 0 && !Languages.Contains(value.Language))
            throw new InvalidDataException("Dictionary entries need spoken text, preferred spelling and a supported language code or blank.");
    }

    private sealed record VocabularyRow(Vocabulary Item)
    {
        public override string ToString() => $"{Item.Heard} → {Item.Preferred}" + (Item.Language.Length > 0 ? $" ({Item.Language})" : "");
    }

    private void BuildSnippets()
    {
        Heading("REUSABLE TEXT", "Snippets", "Say “insert snippet” followed by the trigger as its own dictation.");
        var card = Card("Snippets", "A trigger expands to saved text.");
        var list = new ListBox { Height = 150 };
        card.Children.Add(list);
        var trigger = Input(card, "Trigger phrase", "");
        var text = Input(card, "Expanded text", "");
        text.AcceptsReturn = true;
        text.TextWrapping = TextWrapping.Wrap;
        text.Height = 95;
        var active = new CheckBox { Content = "Enabled", IsChecked = true };
        card.Children.Add(active);
        Snippet? selection = null;
        void Load() => list.ItemsSource = store.Snippets.Select(s => new SnippetRow(s)).ToList();
        list.SelectionChanged += (_, _) =>
        {
            selection = (list.SelectedItem as SnippetRow)?.Item;
            trigger.Text = selection?.Trigger ?? "";
            text.Text = selection?.Output ?? "";
            active.IsChecked = selection?.Enabled ?? true;
        };
        Load();
        var row = Row(card);
        row.Children.Add(ActionButton("New", () => list.UnselectAll()));
        row.Children.Add(ActionButton("Save snippet", () =>
        {
            var entry = new Snippet(trigger.Text.Trim().TrimEnd('.', '?', '!'), text.Text, active.IsChecked == true);
            Validate(entry);
            var all = store.Snippets;
            if (selection != null) all.Remove(selection);
            all.RemoveAll(v => v.Trigger.Equals(entry.Trigger, StringComparison.OrdinalIgnoreCase));
            all.Add(entry);
            store.Snippets = all;
            Load();
        }, primary: true));
        row.Children.Add(ActionButton("Delete", () =>
        {
            if (selection is null) return;
            var all = store.Snippets;
            all.Remove(selection);
            store.Snippets = all;
            Load();
        }));
        row.Children.Add(ActionButton("Export", () => Export(store.Snippets, "snippets")));
        row.Children.Add(ActionButton("Import", () =>
        {
            var imported = Import<List<Snippet>>();
            if (imported == null) return;
            foreach (var entry in imported) Validate(entry);
            if (!Confirm("Replace the current snippets with the imported entries?")) return;
            store.Snippets = imported;
            Load();
        }));
    }

    private static void Validate(Snippet? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Trigger) || string.IsNullOrWhiteSpace(value.Output))
            throw new InvalidDataException("Every snippet needs a trigger and expanded text.");
    }

    private sealed record SnippetRow(Snippet Item)
    {
        public override string ToString() => Item.Trigger + (Item.Enabled ? "" : " · Disabled");
    }

    private void BuildStatistics()
    {
        Heading("ON-DEVICE ACTIVITY", "Statistics", "Statistics are calculated from sessions recorded after you turn tracking on.");
        var usage = store.GetUsage();
        var stats = Card("Usage", $"{usage.Words:N0} words · {usage.Sessions:N0} sessions · {TimeSpan.FromSeconds(usage.Seconds):g} of audio");
        AddText(stats, $"Today: {usage.Today:N0} words   ·   Last 7 days: {usage.Week:N0} words");
        var wpm = usage.Seconds > 0 ? usage.Words * 60 / usage.Seconds : 0;
        AddText(stats, $"Dictation pace: {wpm:N0} words/min   ·   Local: {usage.Sessions - usage.Remote:N0} sessions   ·   Online: {usage.Remote:N0} sessions");
        AddText(stats, $"Top languages: {(usage.Languages.Length == 0 ? "—" : usage.Languages)}");
        var timeSaved = Math.Max(0, usage.Words / 40.0 - usage.Seconds / 60);
        AddText(stats, $"Estimated typing time saved: {timeSaved:N1} min at 40 words/min", "Muted");
        Toggle(stats, "Track statistics", vm.Settings.Statistics, value => vm.Update(s => s with { Statistics = value }));
        stats.Children.Add(ActionButton("Reset statistics", () =>
        {
            if (!Confirm("Reset the local statistics?")) return;
            store.ClearUsage();
            Navigate("Statistics");
        }));
    }

    private void BuildSettings()
    {
        Heading("PREFERENCES", "Settings", "Appearance, Windows integration and privacy.");
        var visual = Card("Appearance", null);
        Toggle(visual, "Use light theme", vm.Settings.Light, value => vm.Update(s => s with { Light = value }));
        Select(visual, "Accent", new[] { "Violet", "Blue", "Mint", "Rose" }, vm.Settings.Accent, value => vm.Update(s => s with { Accent = value }));
        var windows = Card("Windows", null);
        Toggle(windows, "Start with Windows", vm.Settings.StartWithWindows, value => vm.Update(s => s with { StartWithWindows = value }));
        Toggle(windows, "Keep running in the tray when the window closes", vm.Settings.MinimizeToTray,
            value => vm.Update(s => s with { MinimizeToTray = value }));
        var privacy = Card("Privacy", "Local data is stored in your Windows user profile. Recording takes place only during dictation. Online cleanup needs your consent.");
        Toggle(privacy, "Allow microphone use during dictation", vm.Settings.MicConsent, value => vm.Update(s => s with { MicConsent = value }));
        Toggle(privacy, "Save transcripts locally", vm.Settings.History, value => vm.Update(s => s with { History = value }));
        Toggle(privacy, "Store local statistics", vm.Settings.Statistics, value => vm.Update(s => s with { Statistics = value }));
        Select(privacy, "Transcript retention in days · 0 keeps entries until deleted", new[] { 0, 7, 30, 90, 365 }, vm.Settings.HistoryDays,
            value => vm.Update(s => s with { HistoryDays = value }));
        var row = Row(privacy);
        row.Children.Add(ActionButton("Delete saved transcripts", () =>
        {
            if (Confirm("Delete all saved transcripts?")) store.ClearHistory();
        }));
        row.Children.Add(ActionButton("Export preferences", () => Export(vm.Settings, "preferences")));
        row.Children.Add(ActionButton("First-run setup", () => { setup = 0; Navigate("Setup"); }));
    }

    private void BuildAbout()
    {
        Heading("CHUDAI", "About ChudType", "Windows voice dictation for the ChudGPT ecosystem.");
        var info = Card("ChudType 0.1.4", "Local speech recognition powered by Whisper.net and whisper.cpp.");
        AddText(info, "ChudGPT Dictation v1 is a compact local text-editing model. Voice recognition and text cleanup can both run offline.", "Muted");
        AddText(info, "Data directory · " + store.DirectoryPath, "Muted");
        info.Children.Add(ActionButton("Copy data folder path", () => Copy(store.DirectoryPath)));
    }

    private void RefreshFooter() => Notice.Text = vm.Settings.Provider == "Local only" || !vm.Settings.RemoteConsent
        ? "On-device recognition · Local processing"
        : $"On-device recognition · Transcript text may be sent to {vm.Settings.Provider}";

    private void Heading(string eyebrow, string title, string description)
    {
        AddText(Page, eyebrow, "Accent", 11, FontWeights.Bold);
        var heading = AddText(Page, title, "Ink", 35, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 3, 0, 5);
        var detail = AddText(Page, description, "Muted");
        detail.Margin = new Thickness(0, 0, 0, 19);
    }

    private StackPanel Card(string title, string? description)
    {
        var panel = new StackPanel();
        var container = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(17, 12, 17, 14),
            Margin = new Thickness(0, 0, 0, 11), BorderThickness = new Thickness(1), Child = panel };
        container.SetResourceReference(Border.BackgroundProperty, "Panel");
        container.SetResourceReference(Border.BorderBrushProperty, "Border");
        Page.Children.Add(container);
        AddText(panel, title, "Ink", 17, FontWeights.SemiBold);
        if (description != null) AddText(panel, description, "Muted");
        return panel;
    }

    private static TextBlock AddText(Panel panel, string value, string color = "Ink", double font = 14, FontWeight? weight = null)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = font, Margin = new Thickness(0, 3, 0, 5) };
        if (weight.HasValue) text.FontWeight = weight.Value;
        text.SetResourceReference(TextBlock.ForegroundProperty, color);
        panel.Children.Add(text);
        return text;
    }

    private static WrapPanel Row(Panel parent)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        parent.Children.Add(row);
        return row;
    }

    private Button ActionButton(string label, Action action, bool primary = false) =>
        AsyncButton(label, () => { action(); return Task.CompletedTask; }, primary);

    private Button AsyncButton(string label, Func<Task> action, bool primary = false, bool disableWhileRunning = true)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 5), HorizontalAlignment = HorizontalAlignment.Left };
        if (primary)
        {
            button.SetResourceReference(BackgroundProperty, "AccentBase");
            button.SetResourceReference(ForegroundProperty, "AccentInk");
        }
        button.Click += async (_, _) =>
        {
            if (disableWhileRunning) button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { Notice.Text = error.Message; }
            finally { if (disableWhileRunning) button.IsEnabled = true; }
        };
        return button;
    }

    private void Select<T>(Panel panel, string label, IEnumerable<T> values, T selection, Action<T> update, string display = "")
    {
        AddText(panel, label, "Muted");
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selection, MinWidth = 205, MaxWidth = 390, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 0, 6) };
        combo.DisplayMemberPath = display;
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is T value) Guard(() => update(value));
        };
        panel.Children.Add(combo);
    }

    private void Toggle(Panel panel, string label, bool value, Action<bool> update)
    {
        var check = new CheckBox { IsChecked = value, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } };
        check.Checked += (_, _) => Guard(() => update(true));
        check.Unchecked += (_, _) => Guard(() => update(false));
        panel.Children.Add(check);
    }

    private static TextBox Input(Panel panel, string label, string value)
    {
        AddText(panel, label, "Muted");
        var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 5) };
        AutomationProperties.SetName(box, label);
        panel.Children.Add(box);
        return box;
    }

    private void Field(Panel panel, string label, string value, Action<string> update, string action = "Apply")
    {
        var box = Input(panel, label, value);
        panel.Children.Add(ActionButton(action, () => update(box.Text.Trim())));
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { Notice.Text = error.Message; }
    }

    private bool Confirm(string message) => MessageBox.Show(this, message, "ChudType", MessageBoxButton.YesNo,
        MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Notice.Text = "Copied to the clipboard."; }
        catch (COMException) { Notice.Text = "The clipboard is busy. Try again."; }
    }

    private void Export<T>(T value, string name)
    {
        var dialog = new SaveFileDialog { FileName = $"chudtype-{name}.json", Filter = "JSON files|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, LocalStore.Export(value));
    }

    private T? Import<T>() where T : class
    {
        var dialog = new OpenFileDialog { Filter = "JSON files|*.json" };
        if (dialog.ShowDialog(this) != true) return null;
        if (new FileInfo(dialog.FileName).Length > 10_000_000) throw new InvalidDataException("The import exceeds 10 MB.");
        return LocalStore.Import<T>(File.ReadAllText(dialog.FileName));
    }
}
