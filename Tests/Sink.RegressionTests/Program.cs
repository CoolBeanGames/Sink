using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Clickwheel;
using Sink.Services;
using Sink.Services.Ipod;
using Track = Sink.Models.Track;
using CwTrack = Clickwheel.Parsers.iTunesDB.Track;
using MediaType = Clickwheel.Parsers.iTunesDB.MediaType;

var workspace = Path.Combine(Path.GetTempPath(), "Sink-iPod-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
LibraryStore.Directory = Path.Combine(workspace, "app-data");
Directory.CreateDirectory(LibraryStore.Directory);
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message); passed++;
}
IPod Open(string root) => IPod.GetiPodByDrive(new DirectoryInfo(root), IPodLoadAction.NoSync);
CwTrack Find(IPod ipod, string title)
{
    foreach (var track in ipod.Tracks) if (track.Title == title) return track;
    throw new Exception("Missing test track: " + title);
}
Track Source(string title, string genre = "Rock", bool exclude = false)
{
    var path = Path.Combine(workspace, title + ".wav");
    WriteWave(path);
    return new Track { Title = title, Artist = "Test artist", Album = "Test album", Genre = genre,
        FileName = Path.GetFileName(path), FilePath = path, Duration = TimeSpan.FromSeconds(1), ExcludedFromShuffle = exclude };
}
void CheckPodcastOnly(IPod ipod, string title)
{
    var episode = Find(ipod, title);
    Check(episode.MediaType == MediaType.Podcast && episode.PodcastFlag, title + " retains podcast media type");
    Check(IpodShuffleFlag.Get(episode) && episode.RememberPlaybackPosition, title + " skips shuffle and remembers position");
    Check(ipod.Playlists.GetPlaylistByName("Podcasts")?.ContainsTrack(episode) == true, title + " belongs to Podcasts");
    foreach (var playlist in ipod.Playlists)
        if (!playlist.IsPodcastPlaylist) Check(!playlist.ContainsTrack(episode), title + " is absent from " + playlist.Name);
}

try
{
    var root = Path.Combine(workspace, "device");
    CreateDevice(root);
    Check(Open(root).IsWritable, "synthetic iPod 5-era database is writable by the real Clickwheel parser");
    var song = Source("Regular song");
    var excluded = Source("Excluded song", exclude: true);
    var episode = Source("New podcast", "Podcast");
    var result = IpodWriteService.Sync(root, [song, excluded, episode]);
    Check(result.Error is null && result.Added == 3, "new music and podcast sync succeeds");
    var saved = Open(root);
    Check(saved.Tracks.Count == 3, "podcast isolation preserves global track count");
    CheckPodcastOnly(saved, episode.Title);
    Check(IpodShuffleFlag.Get(Find(saved, excluded.Title)), "new song shuffle exclusion survives reopening");
    Check(!IpodShuffleFlag.Get(Find(saved, song.Title)), "ordinary music remains included in shuffle");
    var read = IpodReader.Read(root);
    Check(read.Tracks.Single(t => t.Title == excluded.Title).ExcludedFromShuffle, "Sink's device model reads the saved shuffle preference");
    Check(read.Tracks.Single(t => t.Title == episode.Title).IsPodcast, "Sink's device model keeps repaired episodes classified as podcasts");

    // Recreate the old bug on a track already present on the device, with its
    // source download unavailable. Also put it in an ordinary music playlist.
    var legacy = Source("Legacy podcast", "Podcast");
    var legacyResult = IpodWriteService.Sync(root, [legacy]);
    Check(legacyResult.Error is null, "existing podcast fixture is synced");
    saved = Open(root);
    var old = Find(saved, legacy.Title);
    old.MediaType = MediaType.Audio; old.PodcastFlag = false; old.RememberPlaybackPosition = false;
    IpodShuffleFlag.Set(old, false);
    old.Genre = "Spoken word"; old.PlayCount = 7; IpodBookmarks.SetMs(old, 12000);
    var master = saved.Playlists[0];
    var add = master.GetType().GetMethod("AddTrack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
        null, [typeof(CwTrack), typeof(int), typeof(bool)], null)!;
    add.Invoke(master, [old, -1, true]);
    var mix = saved.Playlists.Add("Music mix"); mix.AddTrack(old); mix.AddTrack(Find(saved, song.Title));
    saved.SaveChanges();
    var file = IpodReader.ResolvePath(root, old.FilePath); var beforeHash = SHA256.HashData(File.ReadAllBytes(file));
    var beforeCount = saved.Tracks.Count; var beforeId = old.Id; var beforeDbId = old.DBId;
    File.Delete(legacy.FilePath!);
    var playCountsPath = Path.Combine(root, "iPod_Control", "iTunes", "Play Counts");
    File.WriteAllBytes(playCountsPath, [1, 2, 3, 4]);
    var repaired = IpodMaintenance.RepairPodcastCategories(root, new HashSet<string>());
    Check(repaired > 0, "repair finds legacy episodes via existing Podcasts membership without a local source");
    Check(File.ReadAllBytes(playCountsPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "repair does not consume pending device Play Counts");
    File.Delete(playCountsPath);
    saved = Open(root); old = Find(saved, legacy.Title);
    CheckPodcastOnly(saved, legacy.Title);
    Check(saved.Tracks.Count == beforeCount && old.Id == beforeId && old.DBId == beforeDbId, "existing podcast identity and track count are preserved");
    Check(old.PlayCount == 7 && IpodBookmarks.GetMs(old) == 12000, "existing play count and bookmark are preserved");
    Check(SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(beforeHash), "existing podcast audio is untouched");
    Check(saved.Playlists.GetPlaylistByName("Music mix")!.ContainsTrack(Find(saved, song.Title)), "repair preserves music playlist membership");
    Check(IpodMaintenance.RepairPodcastCategories(root, new HashSet<string>()) == 0, "repeated podcast repair is idempotent");

    // A completely unclassified legacy episode can still be matched using
    // the subscribed feed identity, without requiring its local download.
    var missed = Source("Unclassified episode");
    Check(IpodWriteService.Sync(root, [missed]).Error is null, "unclassified episode fixture is synced");
    var key = IpodDbTrack.MakeKey(missed.Title, missed.Artist, missed.Album, missed.TrackNumber);
    File.Delete(missed.FilePath!);
    Check(IpodMaintenance.RepairPodcastCategories(root, new HashSet<string> { key }) == 1, "feed identity repairs an episode with no podcast flags or local audio");
    CheckPodcastOnly(Open(root), missed.Title);

    // Exercise the exact-match duplicate path and normal playlist sync too.
    Check(IpodWriteService.Sync(root, [song, excluded, episode]).Error is null, "repeat sync succeeds without duplicating tracks");
    CheckPodcastOnly(Open(root), episode.Title);
    Check(IpodWriteService.SyncPlaylist(root, "Another mix", [song, episode]).Error is null, "playlist sync succeeds");
    CheckPodcastOnly(Open(root), episode.Title);

    var matched = IpodMaintenance.SetShuffleExclusion(root, [song], true, matchDevicePaths: false);
    Check(matched == 1 && IpodShuffleFlag.Get(Find(Open(root), song.Title)), "exclude an existing song and verify its saved flag");
    var raw = File.ReadAllBytes(Path.Combine(root, "iPod_Control", "iTunes", "iTunesDB"));
    var songId = Find(Open(root), song.Title).Id; var rawFlag = -1;
    for (var offset = 0; offset + 212 <= raw.Length; offset++)
        if (raw.AsSpan(offset, 4).SequenceEqual("mhit"u8) && BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset + 16, 4)) == songId)
        { rawFlag = raw[offset + 165]; break; }
    Check(rawFlag == 1, "serialized iTunesDB byte 165 contains the firmware shuffle-skip flag");
    IpodMaintenance.SetShuffleExclusion(root, [song], false, matchDevicePaths: false);
    Check(!IpodShuffleFlag.Get(Find(Open(root), song.Title)), "include in shuffle clears the saved flag");
    var native = Find(Open(root), song.Title);
    var deviceSong = new Track { Title = native.Title, Artist = native.Artist, Album = native.Album, Genre = native.Genre,
        FileName = Path.GetFileName(native.FilePath), FilePath = IpodReader.ResolvePath(root, native.FilePath) };
    Check(IpodMaintenance.SetShuffleExclusion(root, [deviceSong], true, matchDevicePaths: true) == 1, "shuffle update matches tracks directly on the iPod");
    IpodMaintenance.SetShuffleExclusion(root, [song], false, matchDevicePaths: false);
    song.ExcludedFromShuffle = true;
    Check(IpodWriteService.PushTrackMetadata(root, [song], null) == 1 && IpodShuffleFlag.Get(Find(Open(root), song.Title)), "Sync changes persists a flag-only shuffle exclusion");
    song.ExcludedFromShuffle = false;
    Check(IpodWriteService.PushTrackMetadata(root, [song], null) == 1 && !IpodShuffleFlag.Get(Find(Open(root), song.Title)), "Sync changes can include a previously excluded song again");
    IpodMaintenance.SetShuffleExclusion(root, [episode], false, matchDevicePaths: false);
    Check(IpodShuffleFlag.Get(Find(Open(root), episode.Title)), "podcast shuffle exclusion cannot be cleared by an old music preference");

    var queue = new PendingShufflePreferences();
    queue.Enqueue(root, [song], true, matchDevicePaths: false);
    var oldWrite = queue.Values.Single();
    queue.Enqueue(root, [song], false, matchDevicePaths: false);
    Check(queue.Count == 1 && !queue.Values.Single().Excluded, "rapid edits coalesce to the latest preference");
    IpodMaintenance.SetShuffleExclusion(root, [oldWrite.Track], oldWrite.Excluded, oldWrite.MatchDevicePaths);
    queue.Acknowledge(oldWrite);
    Check(queue.Count == 1, "completion of an older write retains the newer queued edit");
    var latestWrite = queue.Values.Single();
    IpodMaintenance.SetShuffleExclusion(root, [latestWrite.Track], latestWrite.Excluded, latestWrite.MatchDevicePaths);
    queue.Acknowledge(latestWrite);
    Check(queue.Count == 0 && !IpodShuffleFlag.Get(Find(Open(root), song.Title)), "latest queued preference wins in the saved iPod database");
    var duplicatePath = new Track { Title = deviceSong.Title, Artist = deviceSong.Artist, Album = deviceSong.Album, Genre = deviceSong.Genre,
        FileName = deviceSong.FileName, FilePath = deviceSong.FilePath + ".second" };
    queue.Enqueue(root, [deviceSong, duplicatePath], true, matchDevicePaths: true);
    Check(queue.Count == 2, "device tracks with identical metadata retain separate path-based updates");

    if (args.Length == 1)
    {
        // Optional replay against a COPY of a real existing backup. Never
        // modify the supplied backup or access its original media/device.
        var sourceHash = SHA256.HashData(File.ReadAllBytes(args[0]));
        var replayRoot = Path.Combine(workspace, "backup-replay"); CreateDevice(replayRoot);
        File.Copy(args[0], Path.Combine(replayRoot, "iPod_Control", "iTunes", "iTunesDB"), overwrite: true);
        var original = Open(replayRoot);
        var identities = new Dictionary<int, (long DbId, string File, int Plays, long Bookmark)>();
        foreach (var track in original.Tracks) identities[track.Id] = (track.DBId, track.FilePath, track.PlayCount, IpodBookmarks.GetMs(track));
        var repairs = IpodMaintenance.RepairPodcastCategories(replayRoot, new HashSet<string>());
        var replay = Open(replayRoot); var podcastCount = 0;
        foreach (var track in replay.Tracks)
        {
            var oldIdentity = identities[track.Id];
            if ((track.DBId, track.FilePath, track.PlayCount, IpodBookmarks.GetMs(track)) != oldIdentity)
                throw new Exception("Backup replay changed a track's identity, file path, play count or bookmark.");
            if (!IpodPodcastClassification.IsPodcast(track)) continue;
            podcastCount++;
            if (!IpodShuffleFlag.Get(track) || replay.Playlists.GetPlaylistByName("Podcasts")?.ContainsTrack(track) != true)
                throw new Exception("Backup replay did not preserve podcast membership and shuffle exclusion.");
            foreach (var playlist in replay.Playlists)
                if (!playlist.IsPodcastPlaylist && playlist.ContainsTrack(track)) throw new Exception("Backup replay left a podcast in music.");
        }
        Check(replay.Tracks.Count == identities.Count, $"real backup replay preserves all {identities.Count} track identities, paths and playback data");
        Check(podcastCount > 0 && repairs > 0, $"real backup replay repairs {podcastCount} podcast episodes in place");
        Check(SHA256.HashData(File.ReadAllBytes(args[0])).SequenceEqual(sourceHash), "original real backup is unchanged");
    }
    Console.WriteLine($"All {passed} regression checks passed.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixture retained at " + workspace); return 1; }

static void CreateDevice(string root)
{
    var itunes = Path.Combine(root, "iPod_Control", "iTunes");
    var device = Path.Combine(root, "iPod_Control", "Device");
    Directory.CreateDirectory(itunes); Directory.CreateDirectory(device);
    Directory.CreateDirectory(Path.Combine(root, "iPod_Control", "Music"));
    File.WriteAllText(Path.Combine(device, "SysInfoExtended"), "<plist><dict><key>SerialNumber</key><string>TEST-IPOD</string><key>FamilyID</key><integer>6</integer></dict></plist>");
    var tracks = new byte[28]; Encoding.ASCII.GetBytes("mhsd").CopyTo(tracks, 0);
    Put(tracks, 4, 16); Put(tracks, 8, tracks.Length); Put(tracks, 12, 1);
    Encoding.ASCII.GetBytes("mhlt").CopyTo(tracks, 16); Put(tracks, 20, 12);
    byte[] Playlists(int type)
    {
        var data = new byte[136]; Encoding.ASCII.GetBytes("mhsd").CopyTo(data, 0);
        Put(data, 4, 16); Put(data, 8, data.Length); Put(data, 12, type);
        Encoding.ASCII.GetBytes("mhlp").CopyTo(data, 16); Put(data, 20, 12); Put(data, 24, 1);
        Encoding.ASCII.GetBytes("mhyp").CopyTo(data, 28); Put(data, 32, 108); Put(data, 36, 108); data[48] = 1;
        return data;
    }
    var rootHeader = new byte[50]; Encoding.ASCII.GetBytes("mhbd").CopyTo(rootHeader, 0);
    Put(rootHeader, 4, 50); Put(rootHeader, 8, 350); Put(rootHeader, 12, 1); Put(rootHeader, 16, 19); Put(rootHeader, 20, 3);
    File.WriteAllBytes(Path.Combine(itunes, "iTunesDB"), [.. rootHeader, .. tracks, .. Playlists(2), .. Playlists(3)]);
}
static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
static void WriteWave(string path)
{
    using var writer = new BinaryWriter(File.Create(path));
    var audio = new byte[88200];
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + audio.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(audio.Length); writer.Write(audio);
}
