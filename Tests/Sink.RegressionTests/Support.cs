// Keep device-database tests isolated from the user's library, logs, tools,
// backups and physical drives. The actual production iPod services are linked
// into this runner and exercise Clickwheel's real database writer and reader.
namespace Sink.Services
{
    public static class LibraryStore
    {
        public static string Directory { get; set; } = "";
    }
    public static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(string message, Exception? exception = null) => Console.Error.WriteLine($"{message} {exception}");
    }
    public static class DriveEject
    {
        public static void Flush(string root) { }
    }
    public static class Artwork
    {
        public static string? Save(string key, byte[] bytes, string? mime) => null;
    }
}
namespace Sink.Services.Download
{
    public static class ToolManager
    {
        public static string FfmpegPath => Path.Combine(Sink.Services.LibraryStore.Directory, "unused-ffmpeg.exe");
        public static string YtDlpPath => Path.Combine(Sink.Services.LibraryStore.Directory, "unused-yt-dlp.exe");
    }
}
