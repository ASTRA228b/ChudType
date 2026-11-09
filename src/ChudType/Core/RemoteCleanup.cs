using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChudType.Core;

internal interface ITextProvider
{
    Task<string> RewriteAsync(string text, Preferences settings, CancellationToken cancellation);
    Task TestAsync(Preferences settings, CancellationToken cancellation);
}

internal sealed class RemoteCleanup : ITextProvider, IDisposable
{
    public static readonly string[] Models = ["plato", "plato-mini", "public", "buggy", "700", "1300", "1500",
        "1600", "ultimate", "plus", "pro", "code", "mega"];
    private readonly HttpClient http;

    public RemoteCleanup() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal RemoteCleanup(HttpMessageHandler handler) => http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<string> RewriteAsync(string text, Preferences settings, CancellationToken cancellation)
    {
        if (!settings.RemoteConsent) throw new InvalidOperationException("Allow sending transcript text first.");
        var (endpoint, plato, _) = Resolve(settings);
        var style = settings.Cleanup switch
        {
            "Professional" => "Use clear professional wording.",
            "Casual" => "Keep a casual conversational style.",
            "Developer" => "Preserve all programming identifiers and code.",
            "Custom" => settings.CustomStyle,
            _ => "Make minimal punctuation and grammar corrections."
        };
        var prompt = $"Return only the corrected dictation. Preserve meaning, names, dates and numbers. {style}\n\nDictation:\n{text}";
        if (prompt.Length > 4000) throw new InvalidOperationException("This transcript exceeds the provider's input limit.");
        object body = settings.Provider == "Custom"
            ? new { text, style }
            : plato ? new { messages = new[] { new { role = "user", content = prompt } }, max_new_tokens = 192 }
                : new { message = prompt };

        for (int attempt = 0; ; attempt++)
        {
            using var response = await http.PostAsJsonAsync(endpoint, body, cancellation);
            if (attempt == 0 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(900);
                if (delay <= TimeSpan.FromSeconds(3))
                {
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellation);
                    continue;
                }
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new InvalidDataException("The provider returned an unexpected content type.");
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
            if (plato && (!payload.TryGetProperty("source", out var source) || source.GetString() != "neural" ||
                !payload.TryGetProperty("model", out var model) || model.GetString() != settings.RemoteModel))
                throw new InvalidDataException("The provider returned an unexpected model response.");
            if (!payload.TryGetProperty("reply", out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("The provider returned no usable rewrite.");
            return value.GetString()!.Trim();
        }
    }

    public async Task TestAsync(Preferences settings, CancellationToken cancellation)
    {
        var (_, _, status) = Resolve(settings);
        using var response = await http.GetAsync(status, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidDataException("The provider did not return a JSON status response.");
    }

    private static (Uri Endpoint, bool Plato, Uri Status) Resolve(Preferences settings)
    {
        if (settings.Provider == "Local only") throw new InvalidOperationException("Select an online provider first.");
        if (!string.IsNullOrEmpty(settings.RemoteEndpoint) && (!Uri.TryCreate(settings.RemoteEndpoint, UriKind.Absolute, out var custom) ||
            custom.Scheme != Uri.UriSchemeHttps || custom.UserInfo.Length != 0))
            throw new FormatException("Enter an HTTPS endpoint without embedded credentials.");
        if (settings.Provider == "Custom")
        {
            if (string.IsNullOrEmpty(settings.RemoteEndpoint)) throw new FormatException("Enter a custom HTTPS endpoint.");
            var endpoint = new Uri(settings.RemoteEndpoint);
            return (endpoint, false, endpoint);
        }
        if (!Models.Contains(settings.RemoteModel)) throw new FormatException("Select a supported ChudGPT model.");
        bool plato = settings.RemoteModel is "plato" or "plato-mini";
        string origin = settings.RemoteModel == "public" ? "https://chudgpt-public.vercel.app" : "https://chudgpt-xi.vercel.app";
        string prefix = plato ? "/api/plato" : "/api";
        string modelPath = $"{prefix}/models/{settings.RemoteModel}";
        return (new Uri(string.IsNullOrEmpty(settings.RemoteEndpoint)
            ? origin + modelPath + (plato ? "/chat" : "/generate")
            : settings.RemoteEndpoint), plato, string.IsNullOrEmpty(settings.RemoteEndpoint)
                ? new Uri(origin + modelPath) : new Uri(settings.RemoteEndpoint));
    }

    public void Dispose() => http.Dispose();
}
