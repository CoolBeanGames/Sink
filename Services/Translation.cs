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

    /// <summary>Outcome of one translate call.</summary>
    public readonly record struct Result(string Text, bool Ok, bool WasEnglish)
    {
        /// <summary>The translated text actually differs from the input.</summary>
        public bool Changed => Ok && !WasEnglish && Text.Length > 0;
    }

    /// <summary>
    /// Translates <paramref name="text"/> to English. Retries a couple of times
    /// on a transient failure (the keyless endpoint rate-limits bursts). The
    /// result distinguishes "already English" from "couldn't reach the service"
    /// so callers can report unreliability instead of silently keeping the
    /// original.
    /// </summary>
    public static async Task<Result> ToEnglishAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Result(text, Ok: true, WasEnglish: true);

        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q="
                  + Uri.EscapeDataString(text);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
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
                    return new Result(text, Ok: true, WasEnglish: true);

                if (root[0].ValueKind != JsonValueKind.Array)
                    return new Result(text, Ok: true, WasEnglish: true);
                var builder = new System.Text.StringBuilder();
                foreach (var segment in root[0].EnumerateArray())
                    if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                        && segment[0].ValueKind == JsonValueKind.String)
                        builder.Append(segment[0].GetString());

                var translated = builder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(translated))
                    return new Result(text, Ok: true, WasEnglish: true);
                return new Result(translated, Ok: true,
                    WasEnglish: string.Equals(translated, text, StringComparison.Ordinal));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                if (attempt == 3)
                {
                    Log.Warn($"Translation failed for \"{text}\": {ex.Message}");
                    return new Result(text, Ok: false, WasEnglish: false);
                }
                try { await Task.Delay(350 * attempt, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }
        return new Result(text, Ok: false, WasEnglish: false);
    }
}
