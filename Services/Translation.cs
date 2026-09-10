using System.Net.Http;
using System.Text.Json;

namespace Sink.Services;

/// <summary>
/// Best-effort "make this English" helper for track / album / artist titles on
/// the download page. Uses Google's keyless <c>translate_a</c> endpoint — the
/// same one the web translator's widget calls — so there's nothing to install
/// or configure. If the text is already English (or the call fails) the
/// original string is returned unchanged.
/// </summary>
public static class Translation
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    /// <summary>
    /// Translates <paramref name="text"/> to English. Returns the original text
    /// when it is already English, empty, or the service could not be reached.
    /// </summary>
    public static async Task<string> ToEnglishAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q="
                  + Uri.EscapeDataString(text);
        try
        {
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // [ [ ["translated","original",...], ... ], null, "ja", ... ]
            var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
                ? root[2].GetString()
                : null;
            if (string.Equals(detected, "en", StringComparison.OrdinalIgnoreCase))
                return text;

            if (root[0].ValueKind != JsonValueKind.Array) return text;
            var builder = new System.Text.StringBuilder();
            foreach (var segment in root[0].EnumerateArray())
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                    && segment[0].ValueKind == JsonValueKind.String)
                    builder.Append(segment[0].GetString());

            var translated = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(translated) ? text : translated;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Warn($"Translation failed for \"{text}\": {ex.Message}");
            return text;
        }
    }
}
