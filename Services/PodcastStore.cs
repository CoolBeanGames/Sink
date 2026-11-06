using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sink.Models;

namespace Sink.Services;

/// <summary>Keeps the catalog with the downloads and a local fallback for older installs and offline drives.</summary>
public static class PodcastStore
{
    private const string CatalogName = "podcasts.json";
    private static string LocalCatalog => Path.Combine(LibraryStore.Directory, CatalogName);
    private static string? _unreadableCatalog;

    public static string Folder => string.IsNullOrWhiteSpace(AppSettings.Current.PodcastLocation)
        ? Path.Combine(LibraryStore.Directory, "Podcasts") : AppSettings.Current.PodcastLocation;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<Podcast> Load(out bool readable)
    {
        _unreadableCatalog = null;
        var portable = Path.Combine(Folder, CatalogName);
        readable = TryRead(portable, out var shows);
        if (!readable) _unreadableCatalog = portable;
        if (shows is not null) return shows;
        readable &= TryRead(LocalCatalog, out shows);
        return shows ?? [];
    }

    private static bool TryRead(string path, out List<Podcast>? shows)
    {
        shows = null;
        try { shows = JsonSerializer.Deserialize<List<Podcast>>(File.ReadAllText(path), Options) ?? []; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Could not read the podcast catalog", ex);
            return false;
        }
        return true;
    }

    public static void Save(IEnumerable<Podcast> podcasts)
    {
        var json = JsonSerializer.Serialize(podcasts.ToList(), Options);
        Write(LocalCatalog, json);
        var portable = Path.Combine(Folder, CatalogName);
        if (Directory.Exists(Folder) && !string.Equals(portable, _unreadableCatalog, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFullPath(portable), Path.GetFullPath(LocalCatalog), StringComparison.OrdinalIgnoreCase))
            Write(portable, json);
    }

    private static void Write(string path, string json)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not save the podcast catalog", ex); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
