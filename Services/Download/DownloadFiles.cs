using System.IO;

namespace Sink.Services.Download;

internal sealed class DownloadWorkFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(LibraryStore.Directory, "download-work", Guid.NewGuid().ToString("N"));

    public DownloadWorkFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not clean up a temporary download folder: {ex.Message}");
        }
    }
}

internal static class DownloadFiles
{
    public static bool HasFormat(string path, string extension) =>
        string.Equals(Path.GetExtension(path), "." + extension, StringComparison.OrdinalIgnoreCase);

    public static void Publish(string source, string destination, string extension)
    {
        if (!HasFormat(source, extension) || !HasFormat(destination, extension) || new FileInfo(source).Length == 0)
            throw new IOException($"The downloader did not produce a usable {extension.ToUpperInvariant()} file.");

        // App data and Music can be on different drives. Finish copying to a
        // non-audio temporary name before exposing the file to the library.
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.Copy(source, partial, overwrite: false);
            File.Move(partial, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
