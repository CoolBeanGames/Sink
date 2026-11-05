namespace Sink.Services.Download;

/// <summary>Keeps catalog selections separate from the queue, which prunes completed rows.</summary>
internal static class DownloadSelection
{
    public static DownloadNode Snapshot(DownloadNode source)
    {
        var copy = new DownloadNode(source.Kind)
        {
            Url = source.Url, Title = source.Title, Artist = source.Artist, Album = source.Album,
            Genre = source.Genre, TrackNumber = source.TrackNumber, ArtworkOverride = source.ArtworkOverride,
            Index = source.Index, ScannedTitle = source.ScannedTitle, Scanned = source.Scanned,
            IsMixedPlaylist = source.IsMixedPlaylist, ReplacesTrackId = source.ReplacesTrackId,
            State = DownloadState.Ready, StatusText = "Ready",
        };
        // Retain unchecked tracks and their original positions: downloaders use
        // the complete list to distinguish a partial album from a full one.
        foreach (var child in source.Children) copy.Children.Add(Snapshot(child));
        if (copy.Children.Count == 0) copy.Enabled = source.Enabled;
        return copy;
    }

    public static IReadOnlyList<DownloadNode> PendingUnits(DownloadNode root) => root.SelfAndDescendants()
        .Where(n => n.Kind is DownloadKind.Album or DownloadKind.Single)
        .Where(n => n.State != DownloadState.Done && n.Enabled != false)
        .Where(n => n.Children.Count == 0 || n.Children.Any(c => c.Enabled == true))
        .ToList();
}
