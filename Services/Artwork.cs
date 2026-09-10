using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sink.Services;

/// <summary>
/// Extracts embedded cover art from audio files into %AppData%/Sink/artwork so
/// the UI can bind to a plain file path. Files are keyed by album+artist, so a
/// whole album reuses one image.
/// </summary>
public static class Artwork
{
    public static string Directory { get; } = Path.Combine(LibraryStore.Directory, "artwork");

    /// <summary>Saves the picture bytes for an album key and returns the file path, or null.</summary>
    public static string? Save(string key, byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var ext = mimeType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ext;
            var path = Path.Combine(Directory, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
