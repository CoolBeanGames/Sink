using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sink.Models;

namespace Sink.Services.LanSync;

internal sealed record LanBaseline(string Id, JsonObject Catalog);
internal sealed record LanTrust(Guid Id, string Name, string Certificate, string Secret)
{
    public List<LanBaseline> Baselines { get; init; } = [];
}

internal sealed record LanLocalData(LibraryData Library, List<Podcast> Podcasts, List<ListenEvent> Listens);
internal sealed record LanPodcastFolder(string Folder, List<string> RemovedDownloads);
internal sealed class LanRecoveryRequiredException(Exception inner)
    : IOException("Sink could not finish saving. Restart Sink to recover the sync journal.", inner);

internal sealed class LanSyncStore : IDisposable
{
    private const string IdentityFile = "lan-identity.pfx";
    private const string PeersFile = "lan-peers.json";
    private const string JournalFile = "lan-commit.json";
    internal const string PodcastFolderFile = "lan-podcast-folder.json";
    private static readonly string[] CommitFiles = ["library.json", "podcasts.json", "reflect.json", PodcastFolderFile, PeersFile];
    private Dictionary<Guid, LanTrust> _peers;
    public X509Certificate2 Certificate { get; }
    public Guid Id { get; }
    public string Fingerprint { get; }

    public LanSyncStore()
    {
        var path = DataPath(IdentityFile);
        if (!File.Exists(path))
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Sink PC Sync", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            Write(path, certificate.Export(X509ContentType.Pfx));
        }
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
        var hash = SHA256.HashData(Certificate.RawData);
        Id = new Guid(hash.AsSpan(0, 16));
        Fingerprint = Convert.ToHexString(hash);
        _peers = File.Exists(DataPath(PeersFile))
            ? JsonSerializer.Deserialize<Dictionary<Guid, LanTrust>>(File.ReadAllText(DataPath(PeersFile))) ?? []
            : [];
    }

    public LanTrust? Find(Guid id) => _peers.GetValueOrDefault(id);
    public IReadOnlyCollection<LanTrust> Peers => _peers.Values;
    public void Forget(Guid id)
    {
        var peers = new Dictionary<Guid, LanTrust>(_peers);
        peers.Remove(id);
        Write(DataPath(PeersFile), JsonSerializer.SerializeToUtf8Bytes(peers));
        _peers = peers;
    }

    public void Remember(LanTrust peer)
    {
        var peers = new Dictionary<Guid, LanTrust>(_peers) { [peer.Id] = peer };
        Write(DataPath(PeersFile), JsonSerializer.SerializeToUtf8Bytes(peers));
        _peers = peers;
    }

    public void Commit(LanTrust peer, LanLocalData data, JsonObject baseline, string podcastFolder, IReadOnlyList<Podcast>? previousPodcasts = null)
    {
        var id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(baseline)));
        var baselines = new[] { new LanBaseline(id, baseline) }.Concat(peer.Baselines)
            .DistinctBy(b => b.Id).Take(4).ToList();
        var peers = new Dictionary<Guid, LanTrust>(_peers) { [peer.Id] = peer with { Baselines = baselines } };
        var presentDownloads = DownloadPaths(podcastFolder, data.Podcasts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedDownloads = (PodcastFolder(podcastFolder)?.RemovedDownloads ?? []).Concat(DownloadPaths(podcastFolder, previousPodcasts ?? []))
            .Where(path => !presentDownloads.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var files = new Dictionary<string, byte[]>
        {
            ["library.json"] = JsonSerializer.SerializeToUtf8Bytes(data.Library),
            ["podcasts.json"] = JsonSerializer.SerializeToUtf8Bytes(data.Podcasts),
            ["reflect.json"] = JsonSerializer.SerializeToUtf8Bytes(data.Listens),
            [PodcastFolderFile] = JsonSerializer.SerializeToUtf8Bytes(new LanPodcastFolder(Path.GetFullPath(podcastFolder), removedDownloads)),
            [PeersFile] = JsonSerializer.SerializeToUtf8Bytes(peers),
        };
        var previous = files.Keys.ToDictionary(name => name,
            name => File.Exists(DataPath(name)) ? File.ReadAllBytes(DataPath(name)) : null);
        var journal = DataPath(JournalFile);
        Write(journal, JsonSerializer.SerializeToUtf8Bytes(files));
        try
        {
            foreach (var (name, contents) in files) Write(DataPath(name), contents);
            File.Delete(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                foreach (var (name, contents) in previous)
                    if (contents is null) File.Delete(DataPath(name));
                    else Write(DataPath(name), contents);
                File.Delete(journal);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                throw new LanRecoveryRequiredException(new AggregateException(ex, rollback));
            }
            throw;
        }
        _peers = peers;
    }

    public static LanPodcastFolder? PodcastFolder(string root)
    {
        try
        {
            var data = JsonSerializer.Deserialize<LanPodcastFolder>(File.ReadAllBytes(DataPath(PodcastFolderFile)));
            return data is not null && string.Equals(data.Folder, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static IEnumerable<string> DownloadPaths(string root, IEnumerable<Podcast> podcasts) =>
        podcasts.SelectMany(show => show.Episodes).Select(e => LanPaths.Relative(root, e.LocalPath)).OfType<string>();

    internal static void Recover()
    {
        var journal = DataPath(JournalFile);
        if (!File.Exists(journal)) return;
        var files = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(File.ReadAllBytes(journal));
        if (files is null || !files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(CommitFiles))
            throw new InvalidDataException("The PC sync journal is invalid.");
        foreach (var (name, contents) in files) Write(DataPath(name), contents);
        File.Delete(journal);
    }

    private static string DataPath(string name) => Path.Combine(LibraryStore.Directory, name);

    private static void Write(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => Certificate.Dispose();
}
