using System.IO;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Sink.Services;

/// <summary>
/// Extracts embedded cover art from audio files into %AppData%/Sink/artwork so
/// the UI can bind to a plain file path. Files are keyed by album+artist, so a
/// whole album reuses one image.
/// </summary>
public static class Artwork
{
    public static string Directory { get; } = Path.Combine(LibraryStore.Directory, "artwork");

    /// <summary>Every imported cover is normalised to this square size, centre-cropped.</summary>
    public const int Size = 300;

    /// <summary>Saves the picture bytes for an album key and returns the file path, or null.</summary>
    public static string? Save(string key, byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // All cover art, regardless of source, is centre-cropped to a square
            // and resized to Size x Size so the UI and the iPod get uniform tiles.
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            if (!File.Exists(path))
            {
                using var image = Image.Load(data);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(Size, Size),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                image.SaveAsJpeg(path);
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }
}
