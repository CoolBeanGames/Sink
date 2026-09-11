namespace Sink.Models;

/// <summary>
/// One row in the notification panel (task 125). <see cref="DedupeKey"/>
/// controls how many of a kind can exist at once: a null key (device
/// connection, sync complete, download finished, podcast downloads
/// complete) means at most one of that kind — a fresh occurrence replaces
/// the existing row in place instead of stacking. A non-null key (new
/// episodes, keyed by show) allows one per key.
/// </summary>
public sealed class AppNotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Kind { get; init; }
    public string? DedupeKey { get; init; }
    public required string Text { get; set; }
    public DateTime Occurred { get; set; } = DateTime.Now;
}
