using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace ChudType.Core;

public sealed class DictationModel
{
    private static readonly Regex Candidate = new(@"(?<!\w)(?:um+|uh+)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Previous = new(@"(\w+)[,.!?;:]?$");
    private static readonly Regex Following = new(@"^[,.:;!?]?\s*(\w+)");
    private const string Quotes = "\"'“”‘’";
    private const string Symbols = "/\\_-+=#@<>\u0060";
    private static readonly HashSet<string> Mentions = new(StringComparer.OrdinalIgnoreCase)
    {
        "say", "said", "says", "saying", "word", "called", "named", "type",
        "typed", "spell", "spelled", "wrote", "write", "letter", "sound", "pronounce", "remove",
        "replace", "delete", "literal", "search", "for", "means", "stands", "is", "was", "and",
        "or", "versus", "with", "example"
    };

    private readonly Lazy<Checkpoint> weights;
    public DictationModel() : this(() => Application.GetResourceStream(
        new Uri("/ChudType;component/Assets/dictation-v1.json", UriKind.Relative)).Stream) { }

    public DictationModel(Func<Stream> open) => weights = new(() =>
    {
        using var stream = open();
        return JsonSerializer.Deserialize<Checkpoint>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("ChudGPT Dictation weights are missing.");
    });

    public string Apply(string text)
    {
        var output = text;
        bool edited = false;
        foreach (Match match in Candidate.Matches(text).Cast<Match>().Reverse())
        {
            if (!ShouldRemove(text, match)) continue;
            int start = match.Index;
            int end = start + match.Length;
            while (end < output.Length && output[end] == ' ') end++;
            if (end < output.Length && output[end] == ',')
            {
                end++;
                int before = start;
                while (before > 0 && output[before - 1] == ' ') before--;
                if (before > 0 && output[before - 1] == ',') start = before - 1;
            }
            while (end < output.Length && output[end] == ' ') end++;
            output = output.Remove(start, end - start);
            if (start > 0 && start < output.Length && char.IsLetterOrDigit(output[start - 1]) && char.IsLetterOrDigit(output[start]))
                output = output.Insert(start, " ");
            edited = true;
        }
        return edited ? output.Trim() : text;
    }

    private bool ShouldRemove(string text, Match match)
    {
        string left = text[..match.Index].TrimEnd();
        string right = text[(match.Index + match.Length)..].TrimStart();
        var head = Previous.Match(left);
        var tail = Following.Match(right);
        char last = left.Length == 0 ? '\0' : left[^1];
        char first = right.Length == 0 ? '\0' : right[0];
        bool mention = Mentions.Contains(head.Groups[1].Value) || Mentions.Contains(tail.Groups[1].Value);
        bool quoted = Quotes.Contains(last) || Quotes.Contains(first) || left.Count(c => c == '"') % 2 != 0 ||
            left.Count(c => c == '“') > left.Count(c => c == '”') || left.Count(c => c == '‘') > left.Count(c => c == '’');
        bool symbol = Symbols.Contains(last) || Symbols.Contains(first);
        if (!tail.Success || mention || quoted || symbol || char.IsDigit(last) || match.Value.All(char.IsUpper)) return false;

        Span<double> input =
        [
            Flag(head.Success), Flag(tail.Success), Flag(last == ','), Flag(first == ','),
            Flag(Mentions.Contains(head.Groups[1].Value)), Flag(Mentions.Contains(tail.Groups[1].Value)),
            Flag(quoted), Flag(".!?".Contains(last)), Flag(symbol)
        ];
        var model = weights.Value;
        double score = model.OutputBias;
        for (int i = 0; i < model.HiddenBias.Length; i++)
        {
            double activation = model.HiddenBias[i];
            for (int j = 0; j < input.Length; j++) activation += model.HiddenWeights[i][j] * input[j];
            score += model.OutputWeights[i] * Math.Tanh(activation);
        }
        return 1 / (1 + Math.Exp(-score)) >= model.Threshold;
    }

    private static int Flag(bool value) => value ? 1 : 0;
    private sealed record Checkpoint(string Version, double Threshold, double[][] HiddenWeights,
        double[] HiddenBias, double[] OutputWeights, double OutputBias);
}
