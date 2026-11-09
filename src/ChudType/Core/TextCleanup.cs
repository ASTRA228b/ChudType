using System.Globalization;
using System.Text.RegularExpressions;

namespace ChudType.Core;

public sealed class TextCleanup(DictationModel model)
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*");
    private static readonly Regex Sentences = new(@"(?:^|[.!?]\s+)(\p{Ll})");
    private static readonly Regex SpokenPunctuation = new(@"\b(?:open parenthesis|close parenthesis|open brace|close brace|semicolon|double equals|dot)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string[]> Profiles = new()
    {
        ["C#"] = ["C#", "System.Linq", "IEnumerable", "NuGet", "BepInEx"],
        ["C++"] = ["C++", "CMake", "constexpr", "nullptr", "std::vector"],
        ["Python"] = ["Python", "PyTorch", "NumPy", "pytest", "venv"],
        ["JavaScript"] = ["JavaScript", "Node.js", "npm", "JSON", "React"],
        ["TypeScript"] = ["TypeScript", "tsconfig", "npm", "JavaScript"],
        ["HTML"] = ["HTML", "DOCTYPE", "SVG", "iframe"],
        ["CSS"] = ["CSS", "Flexbox", "Tailwind", "WebKit"],
        ["Unity"] = ["Unity", "MonoBehaviour", "BepInEx", "UnityEngine", "PhotonNetwork", "Quaternion", "GUILayout"],
        ["Unreal Engine"] = ["Unreal Engine", "UnrealBuildTool", "Blueprint", "UObject", "UFUNCTION"],
        ["CHUD"] = ["CHUD", "ChudGPT", "CHUD.Core"]
    };
    public static IReadOnlyCollection<string> DeveloperProfiles => Profiles.Keys;
    public static int WordCount(string text) => Word.Matches(text).Count;

    public string Apply(string raw, string language, Preferences settings, IEnumerable<Vocabulary> dictionary, IEnumerable<Snippet> snippets)
    {
        string text = raw.Trim();
        string trigger = text.TrimEnd('.', '?', '!').Trim();
        foreach (var snippet in snippets)
            if (snippet.Enabled && trigger.Equals("insert snippet " + snippet.Trigger, StringComparison.OrdinalIgnoreCase))
                return snippet.Output;

        if (settings.Cleanup is "Clean" or "Professional" or "Casual" or "ChudGPT Dictation")
        {
            if (language == "en")
            {
                if (settings.Cleanup == "ChudGPT Dictation") text = model.Apply(text);
                text = Sentences.Replace(text, match => match.Value[..^1] + char.ToUpperInvariant(match.Value[^1]));
                text = Regex.Replace(text, @"(?<!\w)i(?!\w)", "I", RegexOptions.CultureInvariant);
            }
            if (settings.Cleanup is "Clean" or "Professional" or "ChudGPT Dictation")
                text = Regex.Replace(text, @"[ \t]+([,.;!?])", "$1");
            if (settings.Cleanup == "Professional" && language == "en" && text.Length > 0 && char.IsLetterOrDigit(text[^1]))
                text += ".";
        }

        if (settings.Cleanup == "Developer")
        {
            if (Profiles.TryGetValue(settings.DeveloperProfile, out var vocabulary))
                foreach (string term in vocabulary) text = ReplaceWord(text, term, term);
            if (settings.SpokenCodeSymbols && language == "en")
                text = SpokenPunctuation.Replace(text, match => match.Value.ToLowerInvariant() switch
                {
                    "open parenthesis" => "(", "close parenthesis" => ")", "open brace" => "{",
                    "close brace" => "}", "semicolon" => ";", "double equals" => "==", _ => "."
                });
            if (settings.SpokenCodeSymbols)
                text = Regex.Replace(Regex.Replace(text, @" +([();.])", "$1"), @"([( .]) (?=[)])|([(.]) +", "$1$2");
        }

        if (settings.Cleanup != "Raw")
            foreach (var term in dictionary)
                if (!string.IsNullOrWhiteSpace(term.Heard) && (term.Language.Length == 0 || term.Language == language))
                    text = ReplaceWord(text, term.Heard, term.Preferred);
        return text;
    }

    private static string ReplaceWord(string text, string heard, string preferred) => Regex.Replace(text,
        $@"(?<!\w){Regex.Escape(heard)}(?!\w)", _ => preferred, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
