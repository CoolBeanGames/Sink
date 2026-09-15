using System.IO;
using Microsoft.Data.Sqlite;

namespace Sink.Services.Download;

/// <summary>
/// Exports YouTube/Google cookies straight out of Firefox's own cookie
/// database into a Netscape-format cookies.txt yt-dlp can use via --cookies —
/// possible for Firefox specifically because its cookie store is a plain,
/// unencrypted SQLite database (unlike Chrome/Edge, which encrypt theirs with
/// Windows DPAPI "App-Bound Encryption" in a way not even yt-dlp can reliably
/// decrypt anymore). A static exported file also sidesteps the "profile
/// locked while the browser is open" failure live --cookies-from-browser
/// extraction can hit.
/// </summary>
public static class FirefoxCookieExporter
{
    public sealed record Result(bool Ok, string? Path, int Count, string? Error);

    public static Result Export()
    {
        try
        {
            var profile = FindMostRecentProfile();
            if (profile is null)
                return new Result(false, null, 0, "No Firefox profile with a cookies.sqlite was found.");

            // Copy first rather than opening the live file directly — Firefox
            // cookies.sqlite is safe to read while open (no OS-level lock),
            // but a copy avoids any chance of reading mid-write and needing
            // to reason about WAL files here at all.
            var tempCopy = Path.Combine(Path.GetTempPath(), $"sink-ff-cookies-{Guid.NewGuid():N}.sqlite");
            File.Copy(profile, tempCopy, overwrite: true);
            try
            {
                var rows = ReadYouTubeCookies(tempCopy);
                if (rows.Count == 0)
                    return new Result(false, null, 0, "No YouTube/Google cookies found in Firefox — make sure you're signed into YouTube there.");

                var outPath = Path.Combine(LibraryStore.Directory, "cookies.txt");
                WriteNetscapeFile(outPath, rows);
                Log.Info($"Exported {rows.Count} YouTube/Google cookie(s) from Firefox profile \"{Path.GetFileName(Path.GetDirectoryName(profile))}\" to {outPath}");
                return new Result(true, outPath, rows.Count, null);
            }
            finally
            {
                try { File.Delete(tempCopy); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Log.Error("Firefox cookie export failed", ex);
            return new Result(false, null, 0, ex.Message);
        }
    }

    /// <summary>The profile whose cookies.sqlite was touched most recently — the one actually in day-to-day use, without needing to parse profiles.ini's install-to-profile mapping.</summary>
    private static string? FindMostRecentProfile()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profilesDir = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesDir)) return null;
        return new DirectoryInfo(profilesDir).GetDirectories()
            .Select(d => Path.Combine(d.FullName, "cookies.sqlite"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private sealed record CookieRow(string Host, string Path, bool Secure, long ExpirySeconds, string Name, string Value);

    private static List<CookieRow> ReadYouTubeCookies(string sqlitePath)
    {
        var rows = new List<CookieRow>();
        // Pooling=False so the underlying file handle is actually released the
        // moment this connection is disposed, instead of SQLite's connection
        // pool keeping the temp copy open — the immediate delete right after
        // reading otherwise fails with "in use by another process".
        using var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT host, path, isSecure, expiry, name, value FROM moz_cookies WHERE host LIKE '%youtube.com' OR host LIKE '%google.com'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Verified directly against a real profile: this column holds
            // milliseconds since epoch here, not the seconds most Netscape
            // cookie-file readers (and Firefox's own historical docs) expect —
            // dividing by 1000 is what turns it into a plausible expiry date
            // instead of one tens of thousands of years out.
            var expirySeconds = reader.GetInt64(3) / 1000;
            rows.Add(new CookieRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, expirySeconds, reader.GetString(4), reader.GetString(5)));
        }
        return rows;
    }

    private static void WriteNetscapeFile(string path, List<CookieRow> rows)
    {
        var lines = new List<string> { "# Netscape HTTP Cookie File", "# Generated by Sink from Firefox — re-export if downloads start needing sign-in again", "" };
        foreach (var row in rows)
        {
            var domain = row.Host.StartsWith('.') ? row.Host : "." + row.Host;
            lines.Add(string.Join('\t',
                domain, "TRUE", string.IsNullOrEmpty(row.Path) ? "/" : row.Path,
                row.Secure ? "TRUE" : "FALSE", row.ExpirySeconds.ToString(), row.Name, row.Value));
        }
        File.WriteAllLines(path, lines);
    }
}
