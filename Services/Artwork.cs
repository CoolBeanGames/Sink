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
            if (!File.Exists(path)) SquareCropTo(data, path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-runs the square centre-crop on an existing artwork file in place (for
    /// art that was imported before cropping, or that just looks wrong). Returns
    /// true on success.
    /// </summary>
    public static bool Recrop(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            SquareCropTo(data, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return false;
        }
    }

    /// <summary>Extracts embedded art from an audio file, force-cropping (overwrites any cached copy).</summary>
    public static string? ExtractAndCrop(string audioFilePath, string key)
    {
        try
        {
            using var tag = TagLib.File.Create(audioFilePath);
            var picture = tag.Tag.Pictures?.FirstOrDefault(p => p.Data?.Data?.Length > 0);
            if (picture is null) return null;
            System.IO.Directory.CreateDirectory(Directory);
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            SquareCropTo(picture.Data.Data, path);
            return path;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scales the image so its shorter side is <see cref="Size"/>, then crops the
    /// centred <see cref="Size"/> x <see cref="Size"/> square and writes it as JPEG.
    /// Explicit two-step so the output is always exactly square whatever the source.
    /// </summary>
    private static void SquareCropTo(byte[] data, string path)
    {
        using var image = Image.Load(data);
        var scale = (double)Size / Math.Min(image.Width, image.Height);
        var scaledW = Math.Max(Size, (int)Math.Ceiling(image.Width * scale));
        var scaledH = Math.Max(Size, (int)Math.Ceiling(image.Height * scale));
        image.Mutate(ctx =>
        {
            ctx.Resize(scaledW, scaledH);
            ctx.Crop(new Rectangle((scaledW - Size) / 2, (scaledH - Size) / 2, Size, Size));
        });
        image.SaveAsJpeg(path);
    }
}
