using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sink.Services.LanSync;

internal enum LanChoice { Skip, Local, Remote }

internal sealed record LanConflict(string Key, string Item, string Field, string Local, string Remote);
internal sealed record LanPlan(LanSnapshot Local, LanSnapshot Remote, JsonObject Baseline, IReadOnlyList<LanConflict> Conflicts);

internal sealed class LanMerge(IReadOnlyDictionary<string, LanChoice>? choices = null)
{
    private readonly List<LanConflict> _conflicts = [];
    private readonly Dictionary<Guid, string> _trackNames = [];
    private sealed record Values(JsonNode? Local, JsonNode? Remote, JsonNode? Baseline);

    public static void ValidateBaseline(JsonObject baseline)
    {
        foreach (var name in new[] { "Tracks", "Playlists", "Podcasts" }) ValidateRows(baseline, name);
        foreach (var playlist in Rows(baseline, "Playlists"))
            if (playlist["TrackIds"] is not JsonArray trackIds || ReadIds(trackIds).Any(id => id == Guid.Empty))
                throw new System.IO.InvalidDataException("The peer sent an invalid playlist baseline.");
        foreach (var podcast in Rows(baseline, "Podcasts")) ValidateRows(podcast, "Episodes");
    }

    private static void ValidateRows(JsonObject source, string property)
    {
        if (source[property] is not JsonArray array) throw new System.IO.InvalidDataException("The peer sent an incomplete sync baseline.");
        var ids = new HashSet<Guid>();
        foreach (var row in array)
            if (row is not JsonObject item || item["Id"] is not JsonValue value
                || !value.TryGetValue<Guid>(out var id) || id == Guid.Empty || !ids.Add(id))
                throw new System.IO.InvalidDataException("The peer sent an invalid item in the sync baseline.");
    }

    public LanPlan Build(LanSnapshot local, LanSnapshot remote, JsonObject? baseline)
    {
        var left = local.Catalog();
        var right = remote.Catalog();
        var songs = Align(Rows(left, "Tracks"), Rows(right, "Tracks"), Rows(baseline, "Tracks"), row => Text(row, "FilePath"));
        foreach (var row in Rows(right, "Playlists"))
            row["TrackIds"] = IdArray(ReadIds(row["TrackIds"]).Select(id => songs.GetValueOrDefault(id, id)));
        Align(Rows(left, "Playlists"), Rows(right, "Playlists"), Rows(baseline, "Playlists"), row => Text(row, "Name"));
        var localShows = Rows(left, "Podcasts");
        var remoteShows = Rows(right, "Podcasts");
        var baselineShows = Rows(baseline, "Podcasts").ToDictionary(Id);
        Align(localShows, remoteShows, baselineShows.Values.ToList(), row => Text(row, "FeedUrl"));
        Align(localShows, remoteShows, baselineShows.Values.ToList(),
            row => PodcastService.ShowFolderName(Label(row)), SameRecoveredShow);
        var episodes = new Dictionary<Guid, Guid>();
        var byShow = localShows.ToDictionary(Id);
        foreach (var show in remoteShows)
        {
            var showId = Id(show);
            if (!byShow.TryGetValue(showId, out var localShow)) continue;
            foreach (var key in new[] { "EpisodeGuid", "LocalPath" })
                foreach (var pair in Align(Rows(localShow, "Episodes"), Rows(show, "Episodes"), Rows(baselineShows.GetValueOrDefault(showId), "Episodes"), row => Text(row, key)))
                    episodes.Add(pair.Key, pair.Value);
        }
        foreach (var track in Rows(left, "Tracks").Concat(Rows(right, "Tracks")))
            _trackNames.TryAdd(Id(track), Label(track));

        var remapped = remote.Listens.Select(listen =>
        {
            var ids = listen.Kind == Models.ListenKind.Song ? songs : episodes;
            return ids.TryGetValue(listen.ItemId, out var id) ? listen.WithItemId(id) : listen;
        }).ToList();
        var (history, _) = ReflectStore.MergeHistory(local.Listens, remapped);
        var localResult = new JsonObject();
        var remoteResult = new JsonObject();
        var common = new JsonObject();
        foreach (var name in new[] { "Tracks", "Playlists", "Podcasts" })
        {
            var merged = MergeRows(name, Rows(left, name), Rows(right, name), Rows(baseline, name));
            localResult[name] = merged.Local;
            remoteResult[name] = merged.Remote;
            common[name] = merged.Baseline;
        }
        var l = localResult.Deserialize<LanSnapshot>()!;
        var r = remoteResult.Deserialize<LanSnapshot>()!;
        l.Listens = r.Listens = history;
        l.Validate();
        r.Validate();
        return new(l, r, common, _conflicts.ToArray());
    }

    // Prefer shared IDs. A unique portable path, feed or name can align records
    // independently created on both PCs. Never remap a previously synced ID.
    private static Dictionary<Guid, Guid> Align(
        List<JsonObject> local, List<JsonObject> remote, List<JsonObject> baseline, Func<JsonObject, string?> key,
        Func<JsonObject, JsonObject, bool>? match = null)
    {
        var previous = baseline.Select(Id).ToHashSet();
        var localIds = local.Select(Id).ToHashSet();
        var remoteIds = remote.Select(Id).ToHashSet();
        var localKeys = local.Where(row => !remoteIds.Contains(Id(row)) && !previous.Contains(Id(row)))
            .ToLookup(key, StringComparer.OrdinalIgnoreCase);
        var remoteKeys = remote.Where(row => !localIds.Contains(Id(row)) && !previous.Contains(Id(row)))
            .ToLookup(key, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<Guid, Guid>();
        foreach (var group in remoteKeys)
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1 || localKeys[group.Key].Count() != 1) continue;
            var row = group.Single();
            var localRow = localKeys[group.Key].Single();
            if (match is not null && !match(localRow, row)) continue;
            var target = Id(localRow);
            aliases.Add(Id(row), target);
            row["Id"] = target;
        }
        return aliases;
    }

    private static bool SameRecoveredShow(JsonObject left, JsonObject right) =>
        (string.IsNullOrWhiteSpace(Text(left, "FeedUrl")) || string.IsNullOrWhiteSpace(Text(right, "FeedUrl")))
        && Rows(left, "Episodes").Select(row => Text(row, "LocalPath")).OfType<string>()
            .Intersect(Rows(right, "Episodes").Select(row => Text(row, "LocalPath")).OfType<string>(), StringComparer.OrdinalIgnoreCase).Any();

    private Values MergeRows(string path, List<JsonObject> left, List<JsonObject> right, List<JsonObject> baseline)
    {
        var local = left.ToDictionary(Id);
        var remote = right.ToDictionary(Id);
        var old = baseline.ToDictionary(Id);
        var l = new JsonArray();
        var r = new JsonArray();
        var b = new JsonArray();
        foreach (var id in local.Keys.Concat(remote.Keys).Distinct())
        {
            var a = local.GetValueOrDefault(id);
            var z = remote.GetValueOrDefault(id);
            var o = old.GetValueOrDefault(id);
            var key = $"{path}/{id}";
            var label = Label(a ?? z!);
            Values merged;
            if (a is null || z is null)
                merged = o is null ? Both(a ?? z) : MergeValue(key, label, "Item", a, z, o, true);
            else
                merged = MergeFields(key, label, a, z, o);
            if (merged.Local is not null) l.Add(merged.Local);
            if (merged.Remote is not null) r.Add(merged.Remote);
            if (merged.Baseline is not null) b.Add(merged.Baseline);
        }
        return new(l, r, b);
    }

    private Values MergeFields(string path, string label, JsonObject a, JsonObject z, JsonObject? old)
    {
        var l = new JsonObject();
        var r = new JsonObject();
        var b = new JsonObject();
        var grouped = new HashSet<string>();
        MergePair("Download rule", "RuleCount", "RuleMode");
        MergePair("Playback", "IsPlayed", "PositionSeconds");
        foreach (var field in a.Select(p => p.Key).Union(z.Select(p => p.Key)))
        {
            if (grouped.Contains(field)) continue;
            var key = $"{path}/{field}";
            var o = old?[field];
            var merged = field switch
            {
                "Episodes" => MergeRows(key, Rows(a, field), Rows(z, field), Rows(old, field)),
                "TrackIds" => MergePlaylist(key, label, a[field], z[field], o),
                _ => MergeValue(key, label, field, a[field], z[field], o, old?.ContainsKey(field) == true),
            };
            l[field] = merged.Local;
            r[field] = merged.Remote;
            if (merged.Baseline is not null || old?.ContainsKey(field) == true
                || Equivalent(field, merged.Local, merged.Remote)) b[field] = merged.Baseline;
        }
        return new(l, r, b);

        void MergePair(string description, string first, string second)
        {
            if (!a.ContainsKey(first)) return;
            grouped.Add(first);
            grouped.Add(second);
            JsonObject Pair(JsonObject row) => new()
            { [first] = row[first]?.DeepClone(), [second] = row[second]?.DeepClone() };
            var hasPrevious = old?.ContainsKey(first) == true && old.ContainsKey(second);
            var merged = MergeValue($"{path}/{first}", label, description, Pair(a), Pair(z),
                hasPrevious ? Pair(old!) : null, hasPrevious);
            foreach (var field in new[] { first, second })
            {
                l[field] = merged.Local?[field]?.DeepClone();
                r[field] = merged.Remote?[field]?.DeepClone();
                if (merged.Baseline is not null) b[field] = merged.Baseline[field]?.DeepClone();
            }
        }
    }

    private Values MergePlaylist(string key, string label, JsonNode? left, JsonNode? right, JsonNode? baseline)
    {
        var a = ReadIds(left).Distinct().ToList();
        var z = ReadIds(right).Distinct().ToList();
        var o = ReadIds(baseline).Distinct().ToList();
        if (a.SequenceEqual(z)) return Both(left);
        if (baseline is not null && a.SequenceEqual(o)) return Both(right);
        if (baseline is not null && z.SequenceEqual(o)) return Both(left);

        var original = o.ToHashSet();
        var aSet = a.ToHashSet();
        var zSet = z.ToHashSet();
        var keep = a.Concat(z).Where(id => !original.Contains(id) || (aSet.Contains(id) && zSet.Contains(id))).ToHashSet();
        var commonOrder = o.Where(keep.Contains).ToList();
        var aOrder = a.Where(id => original.Contains(id) && keep.Contains(id)).ToList();
        var zOrder = z.Where(id => original.Contains(id) && keep.Contains(id)).ToList();
        var aReordered = !aOrder.SequenceEqual(commonOrder);
        var zReordered = !zOrder.SequenceEqual(commonOrder);
        JsonArray WithAdditions(IEnumerable<Guid> preferred, IEnumerable<Guid> other) =>
            IdArray(preferred.Concat(other).Where(keep.Contains).Distinct());
        if (aReordered && zReordered && !aOrder.SequenceEqual(zOrder))
            return Resolve(key, label, "Track order", WithAdditions(a, z), WithAdditions(z, a), baseline);
        return Both(zReordered ? WithAdditions(z, a) : WithAdditions(a, z));
    }

    private Values MergeValue(string key, string label, string field, JsonNode? a, JsonNode? z, JsonNode? old, bool hasBaseline)
    {
        if (Equivalent(field, a, z)) return Both(a);
        if (hasBaseline)
        {
            if (Equivalent(field, a, old)) return Both(z);
            if (Equivalent(field, z, old)) return Both(a);
        }
        else
        {
            if (Empty(a)) return Both(z);
            if (Empty(z)) return Both(a);
        }
        return Resolve(key, label, field, a, z, old);
    }

    private Values Resolve(string key, string label, string field, JsonNode? a, JsonNode? z, JsonNode? old)
    {
        _conflicts.Add(new(key, label, field, Describe(a), Describe(z)));
        return choices?.GetValueOrDefault(key) switch
        {
            LanChoice.Local => Both(a),
            LanChoice.Remote => Both(z),
            _ => new(a?.DeepClone(), z?.DeepClone(), old?.DeepClone()),
        };
    }

    private string Describe(JsonNode? node) => node switch
    {
        null => "(missing or removed)",
        JsonObject row when row["RuleCount"] is { } count => $"{count} {(row["RuleMode"]?.GetValue<int>() == 1 ? "oldest" : "newest")}",
        JsonObject row when row["IsPlayed"] is { } played => $"{(played.GetValue<bool>() ? "Played" : "Unplayed")} · {row["PositionSeconds"]} seconds",
        JsonObject row => $"{Label(row)} · edited item",
        JsonArray items => string.Join("\n", ReadIds(items).Take(12).Select(id => _trackNames.GetValueOrDefault(id, id.ToString())))
            + (items.Count > 12 ? $"\n… and {items.Count - 12} more" : ""),
        _ => node.ToString(),
    };

    private static bool Empty(JsonNode? node) => node is null || node is JsonValue value
        && value.TryGetValue<string>(out var text) && text.Length == 0;
    private static bool Equivalent(string field, JsonNode? a, JsonNode? z) => JsonNode.DeepEquals(a, z)
        || field is "FilePath" or "OriginalPath" or "LocalPath" && a is not null && z is not null
        && string.Equals(a.ToString(), z.ToString(), StringComparison.OrdinalIgnoreCase);
    private static Values Both(JsonNode? node) => new(node?.DeepClone(), node?.DeepClone(), node?.DeepClone());
    private static string Label(JsonObject row) => Text(row, "Title") ?? Text(row, "Name") ?? "Library item";
    private static string? Text(JsonObject row, string property) => row[property]?.GetValue<string>();
    private static Guid Id(JsonObject row) => row["Id"]!.GetValue<Guid>();
    private static List<JsonObject> Rows(JsonObject? node, string property) =>
        node?[property] is JsonArray rows ? rows.Cast<JsonObject>().ToList() : [];
    private static IEnumerable<Guid> ReadIds(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item!.GetValue<Guid>()) : [];
    private static JsonArray IdArray(IEnumerable<Guid> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
}
