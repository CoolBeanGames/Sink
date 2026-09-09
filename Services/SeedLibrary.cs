using Sink.Models;

namespace Sink.Services;

public static class SeedLibrary
{
    public static List<Track> Create() =>
    [
        Song("Neon Current", "Aster Vale", "Night Swim", "Electronic", 244, 1, 2025),
        Song("Low Tide", "Aster Vale", "Night Swim", "Electronic", 218, 2, 2025),
        Song("Passenger Window", "June Arcade", "Slow Motion", "Indie", 196, 1, 2024),
        Song("Half Awake", "June Arcade", "Slow Motion", "Indie", 231, 2, 2024),
        Song("Blue Hour", "Marlowe Street", "After Images", "Jazz", 308, 1, 2023),
        Song("Soft Focus", "Marlowe Street", "After Images", "Jazz", 277, 2, 2023),
        Song("Northern Lines", "Common Atlas", "Field Notes", "Folk", 205, 1, 2022),
        Song("Paper Map", "Common Atlas", "Field Notes", "Folk", 189, 2, 2022)
    ];

    private static Track Song(string title, string artist, string album, string genre, int seconds, int number, int year) => new()
    {
        Title = title, Artist = artist, Album = album, Genre = genre, TrackNumber = number, Year = year,
        Duration = TimeSpan.FromSeconds(seconds), FileName = $"{number:00}-{title.ToLowerInvariant().Replace(' ', '-')}.mp3"
    };
}
