using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sink.Models;
using Sink.Services;
using Sink.Services.LanSync;

internal static class LanSyncChecks
{
    public static async Task RunAsync(string workspace, Action<bool, string> check)
    {
        var aRoot = Path.Combine(workspace, "A");
        var bRoot = Path.Combine(workspace, "B");
        var aSong = Song("Original", Path.Combine(aRoot, "Artist", "song.mp3"));
        var bSong = Song("Original", Path.Combine(bRoot, "Artist", "song.mp3"));
        var date = DateTime.UnixEpoch;
        var eventId = Guid.NewGuid();
        var aListen = new ListenEvent { EventId = eventId, Kind = ListenKind.Song, ItemId = aSong.Id, Occurred = date, Duration = TimeSpan.FromMinutes(2) };
        var bListen = aListen.WithItemId(bSong.Id);
        var nativeA = new LanLocalData(new LibraryData
        {
            Tracks = [aSong], Playlists = [new() { Name = "Road trip", TrackIds = [aSong.Id] }],
        }, [Show("Example", "https://example.test/feed", new PodcastEpisode { EpisodeGuid = "episode-1", Title = "First", LocalPath = Path.Combine(aRoot, "episode.mp3") })], [aListen]);
        var nativeB = new LanLocalData(new LibraryData
        {
            Tracks = [bSong], Playlists = [new() { Name = "Road trip", TrackIds = [bSong.Id] }],
        }, [Show("Example", "https://example.test/feed", new PodcastEpisode { EpisodeGuid = "episode-1", Title = "First", LocalPath = Path.Combine(bRoot, "episode.mp3") })], [bListen]);
        bSong.ArtworkPath = Path.Combine(workspace, "private-cover.png");
        bSong.SyncedIpodPlayCounts = new() { ["local-device"] = 19 };

        var portableA = LanProjection.Capture(nativeA, aRoot, aRoot, aRoot);
        var portableB = LanProjection.Capture(nativeB, bRoot, bRoot, bRoot);
        check(portableB.Tracks.Single().FilePath == "Artist/song.mp3" && portableB.Tracks.Single().ArtworkPath is null,
            "PC sync sends portable media paths and omits local artwork paths");
        var first = new LanMerge().Build(portableA, portableB, null);
        check(first.Conflicts.Count == 0 && first.Remote.Tracks.Single().Id == aSong.Id
            && first.Remote.Playlists.Single().TrackIds.SequenceEqual([aSong.Id])
            && first.Remote.Podcasts.Single().Episodes.Count == 1, "first PC sync aligns independently imported tracks, playlists, and podcast episodes");
        check(first.Remote.Listens.Count == 1 && first.Remote.Listens.Single().ItemId == aSong.Id,
            "PC sync remaps listening history and counts the same stable event once");
        var fileOnly = portableB.Copy();
        fileOnly.Podcasts[0].FeedUrl = "";
        var attached = new LanMerge().Build(portableA, fileOnly, null);
        check(attached.Conflicts.Count == 0 && attached.Remote.Podcasts.Single().FeedUrl == nativeA.Podcasts.Single().FeedUrl,
            "a recovered show matches a feed-linked show when their downloaded paths agree");
        var restored = LanProjection.Restore(first.Remote, nativeB, bRoot, bRoot, bRoot);
        check(restored.Library.Tracks.Single().FilePath == bSong.FilePath && restored.Library.Tracks.Single().ArtworkPath == bSong.ArtworkPath
            && restored.Library.Tracks.Single().SyncedIpodPlayCounts?["local-device"] == 19
            && restored.Podcasts.Single().Episodes.Single().LocalPath == Path.Combine(bRoot, "episode.mp3"),
            "PC sync uses the receiving PC's folders and retains its local device bookkeeping");

        var left = first.Local.Copy();
        var right = first.Remote.Copy();
        left.Tracks[0].Title = "Local edit";
        right.Tracks[0].Genre = "Remote genre";
        var independent = new LanMerge().Build(left, right, first.Baseline);
        check(independent.Conflicts.Count == 0 && independent.Remote.Tracks[0].Title == "Local edit"
            && independent.Local.Tracks[0].Genre == "Remote genre", "separate metadata fields merge against a common baseline");

        right.Tracks[0].Title = "Remote edit";
        var skipped = new LanMerge().Build(left, right, first.Baseline);
        var conflict = skipped.Conflicts.Single();
        check(conflict.Field == "Title" && skipped.Local.Tracks[0].Title == "Local edit" && skipped.Remote.Tracks[0].Title == "Remote edit",
            "skipping a metadata conflict preserves the local values on both PCs");
        var again = new LanMerge().Build(skipped.Remote, skipped.Local, skipped.Baseline);
        check(again.Conflicts.Single().Field == "Title", "skipped conflicts return when either PC starts the next sync");
        var resolved = new LanMerge(new Dictionary<string, LanChoice> { [conflict.Key] = LanChoice.Remote }).Build(left, right, first.Baseline);
        check(resolved.Local.Tracks[0].Title == "Remote edit" && resolved.Remote.Tracks[0].Title == "Remote edit",
            "the chosen side of a conflict is applied to both databases");

        var deleted = first.Local.Copy();
        deleted.Tracks.Clear();
        var cleanDelete = new LanMerge().Build(deleted, first.Remote, first.Baseline);
        check(cleanDelete.Conflicts.Count == 0 && cleanDelete.Remote.Tracks.Count == 0, "a deletion propagates after a shared baseline");
        var editDelete = new LanMerge().Build(deleted, right, first.Baseline);
        check(editDelete.Conflicts.Single().Field == "Item" && editDelete.Local.Tracks.Count == 0
            && editDelete.Remote.Tracks.Count == 1, "a deletion against a concurrent edit is a conflict");

        left = first.Local.Copy();
        right = first.Remote.Copy();
        var lSong = Song("Local addition", "left.mp3");
        var rSong = Song("Remote addition", "right.mp3");
        left.Tracks.Add(lSong); left.Playlists[0].TrackIds.Add(lSong.Id);
        right.Tracks.Add(rSong); right.Playlists[0].TrackIds.Add(rSong.Id);
        left.Podcasts[0].Episodes.Add(new() { EpisodeGuid = "local", Title = "Local episode" });
        right.Podcasts[0].Episodes.Add(new() { EpisodeGuid = "remote", Title = "Remote episode" });
        var additions = new LanMerge().Build(left, right, first.Baseline);
        check(additions.Conflicts.Count == 0 && additions.Remote.Playlists.Single().TrackIds.Count == 3
            && additions.Local.Podcasts.Single().Episodes.Count == 3, "independent playlist additions and podcast episodes merge");

        left.Podcasts[0].RuleCount = 5;
        right.Podcasts[0].RuleMode = PodcastRuleMode.Oldest;
        left.Podcasts[0].Episodes[0].IsPlayed = true;
        right.Podcasts[0].Episodes[0].PositionSeconds = 30;
        var podcastConflicts = new LanMerge().Build(left, right, first.Baseline);
        check(podcastConflicts.Conflicts.Select(c => c.Field).ToHashSet().SetEquals(["Download rule", "Playback"])
            && podcastConflicts.Remote.Podcasts[0].RuleCount == 3 && !podcastConflicts.Remote.Podcasts[0].Episodes[0].IsPlayed,
            "concurrent podcast rule and playback edits are reviewed as complete settings");

        try { LanPaths.Resolve(aRoot, "../outside.mp3"); throw new Exception("Traversal accepted"); }
        catch (InvalidDataException) { check(true, "PC sync rejects media paths that escape a configured folder"); }

        var originalDirectory = LibraryStore.Directory;
        try
        {
            LibraryStore.Directory = Path.Combine(workspace, "lan-data");
            using var store = new LanSyncStore();
            var trust = new LanTrust(Guid.NewGuid(), "Peer", store.Fingerprint, "fixture-secret");
            store.Commit(trust, restored, first.Baseline, bRoot);
            store.Commit(trust with { Baselines = store.Find(trust.Id)!.Baselines }, restored, additions.Baseline, bRoot);
            check(store.Find(trust.Id)?.Baselines.Count == 2 && File.Exists(Path.Combine(LibraryStore.Directory, "reflect.json"))
                && !File.Exists(Path.Combine(LibraryStore.Directory, "lan-commit.json")),
                "PC sync commits all databases and retains an earlier shared baseline for an interrupted two-PC sync");
            using var reload = new LanSyncStore();
            check(reload.Id == store.Id && reload.Find(trust.Id)?.Baselines.Count == 2, "pairing identity and baselines survive restart");
            var retainedMedia = Path.Combine(bRoot, "Deleted show", "0001 - First.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(retainedMedia)!);
            File.WriteAllText(retainedMedia, "existing media");
            var removedShow = Show("Deleted show", "", new PodcastEpisode { Title = "First", LocalPath = retainedMedia });
            store.Commit(store.Find(trust.Id)!, restored, additions.Baseline, bRoot, [removedShow]);
            var recovered = new List<Podcast>();
            var suppressed = LanSyncStore.PodcastFolder(bRoot)!.RemovedDownloads.ToHashSet(StringComparer.OrdinalIgnoreCase);
            check(PodcastDownloads.TryRecover(bRoot, recovered, out _, suppressed) && recovered.Count == 0 && File.Exists(retainedMedia),
                "a synced podcast deletion stays deleted while its original audio file remains on disk");
            var names = new[] { "library.json", "podcasts.json", "reflect.json", LanSyncStore.PodcastFolderFile, "lan-peers.json" };
            var journal = names.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(LibraryStore.Directory, name)));
            File.WriteAllBytes(Path.Combine(LibraryStore.Directory, "lan-commit.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
            File.Delete(Path.Combine(LibraryStore.Directory, "reflect.json"));
            LanSyncStore.Recover();
            check(File.ReadAllBytes(Path.Combine(LibraryStore.Directory, "reflect.json")).SequenceEqual(journal["reflect.json"]),
                "an interrupted local journal replays before the app loads its databases");

            await CheckTransportAsync(store, first, check);
        }
        finally { LibraryStore.Directory = originalDirectory; }
    }

    private static async Task CheckTransportAsync(LanSyncStore store, LanPlan plan, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var network = new LanNetwork(store.Id, "Loopback Sink", discoveryPort: 0);
        var advertisement = network.AdvertiseAsync(token);
        using (var discover = new UdpClient(AddressFamily.InterNetwork))
        {
            await discover.SendAsync("Sink PC sync 1"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, network.DiscoveryListenPort), token);
            var answer = await discover.ReceiveAsync(token);
            check(JsonSerializer.Deserialize<LanPeer>(answer.Buffer) == network.Self, "LAN discovery returns the running install and its sync port");
        }
        var server = Task.Run(async () =>
        {
            using var accepted = await network.AcceptAsync(token);
            await using var tls = await LanNetwork.ServerTlsAsync(accepted, store.Certificate, token);
            var proposal = await LanWire.ReadAsync<LanProposal>(tls, token);
            proposal.Snapshot.Validate();
            LanMerge.ValidateBaseline(proposal.Baseline);
            await LanWire.SendAsync(tls, proposal.Snapshot.Listens.Count, token);
        });
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            var (stream, fingerprint) = await LanNetwork.ClientTlsAsync(client, IPAddress.Loopback, network.Self, store.Fingerprint[..16], token);
            await using var tls = stream;
            await LanWire.SendAsync(tls, new LanProposal(plan.Remote, plan.Baseline), token);
            check(fingerprint == store.Fingerprint && await LanWire.ReadAsync<int>(tls, token) == 1,
                "real Windows TLS verifies the pairing fingerprint and transports a complete database plan");
        }
        await server;
        var wrongPinServer = Task.Run(async () =>
        {
            using var accepted = await network.AcceptAsync(token);
            try { await using var tls = await LanNetwork.ServerTlsAsync(accepted, store.Certificate, token); }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        });
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            try
            {
                var wrong = (store.Fingerprint[0] == '0' ? "1" : "0") + store.Fingerprint[1..16];
                var (stream, _) = await LanNetwork.ClientTlsAsync(client, IPAddress.Loopback, network.Self, wrong, token);
                await stream.DisposeAsync();
                throw new Exception("A wrong fingerprint was accepted");
            }
            catch (AuthenticationException) { check(true, "a wrong pairing fingerprint is rejected before any database frames"); }
        }
        await wrongPinServer;
        timeout.Cancel();
        try { await advertisement; } catch (OperationCanceledException) { }
    }

    private static Track Song(string title, string path) => new()
    { Title = title, Artist = "Artist", Album = "Album", Genre = "", FileName = Path.GetFileName(path), FilePath = path };
    private static Podcast Show(string title, string feed, PodcastEpisode episode) => new()
    { Title = title, FeedUrl = feed, Episodes = [episode] };
}
