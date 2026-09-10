using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Sink.Services;

/// <summary>
/// Best-effort "make this English" helper for track / album / artist titles on
/// the download page. Uses Google's keyless translate endpoints — the ones the
/// web widget calls — so there's nothing to install or configure. Tries the
/// primary endpoint, then a second one, with retries, before giving up. If the
/// text is already English (or every attempt failed) the original is returned
/// and <see cref="Result.Ok"/> says which.
/// </summary>
public static class Translation
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // A browser-ish UA — the keyless endpoints throttle obvious bots harder.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return client;
    }

    /// <summary>Outcome of one translate call.</summary>
    public readonly record struct Result(string Text, bool Ok, bool WasEnglish)
    {
        /// <summary>The translated text actually differs from the input.</summary>
        public bool Changed => Ok && !WasEnglish && Text.Length > 0;
    }

    /// <summary>
    /// Translates <paramref name="text"/> to English, trying two endpoints with
    /// backoff. The result distinguishes "already English" from "couldn't reach
    /// the service" so callers can report unreliability instead of silently
    /// keeping the original.
    /// </summary>
    public static async Task<Result> ToEnglishAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Result(text, Ok: true, WasEnglish: true);

        Func<CancellationToken, Task<Result?>>[] providers =
        [
            ct => GoogleDjAsync(text, ct),
            ct => GoogleClients5Async(text, ct),
        ];

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            foreach (var provider in providers)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await provider(token).ConfigureAwait(false) is { } result)
                        return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    // try the next provider / attempt
                }
            }

            if (attempt < 4)
                try { await Task.Delay(400 * attempt, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
        }

        Log.Warn($"Translation failed for \"{text}\" after all attempts");
        return new Result(text, Ok: false, WasEnglish: false);
    }

    /// <summary>Primary: translate_a/single with dj=1 → clean {"sentences":[…],"src":"ja"} JSON.</summary>
    private static async Task<Result?> GoogleDjAsync(string text, CancellationToken token)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dj=1&sl=auto&tl=en&dt=t&q="
                  + Uri.EscapeDataString(text);
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var src = root.TryGetProperty("src", out var s) ? s.GetString() : null;
        if (!root.TryGetProperty("sentences", out var sentences) || sentences.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new System.Text.StringBuilder();
        foreach (var sentence in sentences.EnumerateArray())
            if (sentence.TryGetProperty("trans", out var trans) && trans.ValueKind == JsonValueKind.String)
                builder.Append(trans.GetString());

        return BuildResult(text, builder.ToString(), src);
    }

    /// <summary>Fallback: clients5 translate_a/t → ["translated","src"] (or [["translated","src"]]).</summary>
    private static async Task<Result?> GoogleClients5Async(string text, CancellationToken token)
    {
        var url = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=en&q="
                  + Uri.EscapeDataString(text);
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

        var first = root[0];
        string? translated = null;
        string? src = root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.String ? root[1].GetString() : null;
        if (first.ValueKind == JsonValueKind.String)
            translated = first.GetString();
        else if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String)
        {
            translated = first[0].GetString();
            if (src is null && first.GetArrayLength() > 1 && first[1].ValueKind == JsonValueKind.String)
                src = first[1].GetString();
        }

        return translated is null ? null : BuildResult(text, translated, src);
    }

    private static Result BuildResult(string original, string translated, string? detectedLang)
    {
        if (string.Equals(detectedLang, "en", StringComparison.OrdinalIgnoreCase))
            return new Result(original, Ok: true, WasEnglish: true);

        translated = translated.Trim();
        if (string.IsNullOrWhiteSpace(translated))
            return new Result(original, Ok: true, WasEnglish: true);

        return new Result(translated, Ok: true,
            WasEnglish: string.Equals(translated, original.Trim(), StringComparison.Ordinal));
    }
}
