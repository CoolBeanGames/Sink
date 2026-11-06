using Sink.Services.Download;

internal static class MusicSearchChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string song = "That Green Gentleman";
        const string artist = "Panic! At the Disco";
        const string album = "Pretty. Odd.";
        var exact = Track(song + " (Things Have Changed)", artist, album);
        var restOfAlbum = Track("Behind the Sea", artist, album);
        var alsoOnAlbum = Track("Nine in the Afternoon", artist, album);
        var cover = Track(song + " (Acoustic Cover)", "A Cover Artist", "");
        var otherSong = Track("I Write Sins Not Tragedies", artist, "A Fever You Can't Sweat Out");
        var partial = Track("That Green Gentleman Explained", "Panic At the Disco Tribute", "Pretty Odd Covers");
        var albumCard = new MusicSearchResult { Kind = MusicSearchResultKind.Album, Title = album, Artist = artist };
        var artistCard = new MusicSearchResult { Kind = MusicSearchResultKind.Artist, Title = artist };
        var candidates = new[] { partial, cover, otherSong, artistCard, albumCard, restOfAlbum, alsoOnAlbum, exact };
        var ranked = MusicSearchService.RankResults(candidates, song, "Pretty Odd", "Panic At the Disco").ToList();
        check(ranked[0] == exact && ranked.Skip(1).Take(3).ToHashSet().SetEquals([restOfAlbum, alsoOnAlbum, albumCard]),
            "download search puts the official song first and the rest of the matching album immediately after it");
        check(ranked[4] == cover && ranked.Skip(5).Take(2).ToHashSet().SetEquals([otherSong, artistCard]) && ranked[^1] == partial,
            "download search puts exact song covers ahead of artist-only matches and partial matches");
        check(MusicSearchService.RankResults(candidates, "", "Pretty Odd", "Panic At the Disco").First() == albumCard
            && MusicSearchService.RankResults(candidates, "", "", "Panic At the Disco").First() == artistCard,
            "album-only and artist-only searches retain their matching browse entry");

        var reissue = Track(exact.Title, artist, "Greatest Hits");
        var officialVideo = Track(exact.Title, artist, "");
        officialVideo.YouTubeUrl = "https://example.test/official-video";
        var merged = MusicSearchService.MergeTracks([exact, reissue], [officialVideo, officialVideo]);
        check(merged.Count == 2 && merged.Contains(reissue) && exact.YouTubeUrl == officialVideo.YouTubeUrl,
            "download search retains album editions and attaches an official video to the matching album first");
        var parsed = MusicSearchService.ParseYouTubeTitle($"{artist} - {song} (Cover)", "Cover uploader", song);
        check(parsed.artist == "Cover uploader" && parsed.title == song + " (Cover)",
            "video title parsing recognizes the covered song without crediting it as the original artist");
    }

    private static MusicSearchResult Track(string title, string artist, string album) => new()
    { Kind = MusicSearchResultKind.Track, Title = title, Artist = artist, Album = album };
}
