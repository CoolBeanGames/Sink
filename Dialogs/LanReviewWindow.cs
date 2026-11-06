using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Sink.Services.LanSync;

namespace Sink.Dialogs;

internal sealed class LanReviewWindow : SinkDialog
{
    private readonly Dictionary<string, ComboBox> _choices = [];
    public IReadOnlyDictionary<string, LanChoice> Choices =>
        _choices.ToDictionary(pair => pair.Key, pair => (LanChoice)pair.Value.SelectedIndex);

    public LanReviewWindow(string peer, IReadOnlyList<LanConflict> conflicts)
    {
        Width = 760;
        Height = 570;
        Title = "Resolve sync conflicts";
        var rows = new StackPanel();
        foreach (var conflict in conflicts)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 14, 16) };
            row.Children.Add(Line($"{conflict.Item} · {FieldName(conflict.Field)}", "#F4F6FA"));
            var values = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            values.ColumnDefinitions.Add(new ColumnDefinition());
            values.ColumnDefinitions.Add(new ColumnDefinition());
            var local = Line($"This PC\n{Shorten(conflict.Local)}", "#C7CCD6");
            local.Margin = new Thickness(0, 0, 12, 0);
            var remote = Line($"{peer}\n{Shorten(conflict.Remote)}", "#C7CCD6");
            Grid.SetColumn(remote, 1);
            values.Children.Add(local);
            values.Children.Add(remote);
            row.Children.Add(values);
            var choice = new ComboBox
            {
                ItemsSource = new[] { "Skip for now", "Keep this PC's value", $"Keep {peer}'s value" },
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180,
            };
            row.Children.Add(choice);
            _choices.Add(conflict.Key, choice);
            rows.Children.Add(row);
        }
        Compose("PC sync", "Resolve conflicts", "Skipped changes stay as they are on each PC.",
            new ScrollViewer { Content = rows, Height = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            new("Cancel", false, (_, _) => Close()), new("Continue to preview", true, (_, _) => DialogResult = true));
    }

    public LanReviewWindow(string peer, LanSnapshot before, LanSnapshot after, string? outboundSummary = null)
    {
        Width = 700;
        Height = 530;
        Title = "Review PC sync";
        var detail = $"This PC\n{Describe(before, after)}";
        if (outboundSummary is not null) detail += $"\n\n{peer}\n{outboundSummary}";
        Compose("PC sync", "Review changes", $"Both PCs must approve before the databases change.",
            new ScrollViewer { Content = Line(detail, "#DCE0EA"), Height = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            new("Cancel", false, (_, _) => Close()), new("Approve changes", true, (_, _) => DialogResult = true));
    }

    internal static string Describe(LanSnapshot before, LanSnapshot after)
    {
        var beforeCatalog = before.Catalog();
        var afterCatalog = after.Catalog();
        var sections = new List<string>();
        foreach (var (key, label) in new[] { ("Tracks", "Tracks"), ("Playlists", "Playlists"), ("Podcasts", "Podcast shows and episodes") })
        {
            var old = ((JsonArray)beforeCatalog[key]!).Cast<JsonObject>().ToDictionary(row => row["Id"]!.GetValue<Guid>());
            var next = ((JsonArray)afterCatalog[key]!).Cast<JsonObject>().ToDictionary(row => row["Id"]!.GetValue<Guid>());
            var added = next.Keys.Except(old.Keys).Select(id => ItemName(next[id])).ToList();
            var removed = old.Keys.Except(next.Keys).Select(id => ItemName(old[id])).ToList();
            var updated = next.Keys.Intersect(old.Keys).Where(id => !JsonNode.DeepEquals(old[id], next[id])).Select(id => ItemName(next[id])).ToList();
            if (added.Count + removed.Count + updated.Count == 0) continue;
            sections.Add($"{label} · {added.Count} added, {updated.Count} updated, {removed.Count} removed"
                + Examples("Add", added) + Examples("Update", updated) + Examples("Remove", removed));
        }
        if (after.Listens.Count > before.Listens.Count)
            sections.Add($"Reflect · {after.Listens.Count - before.Listens.Count} listening events added");
        return sections.Count == 0 ? "No database entries to add, update, or remove." : string.Join("\n\n", sections);
    }

    private static string Examples(string action, List<string> values) => values.Count == 0 ? "" :
        $"\n  {action} · {string.Join(", ", values.Take(5).Select(Shorten))}" + (values.Count > 5 ? $", and {values.Count - 5} more" : "");
    private static string ItemName(JsonObject row) => row["Title"]?.ToString() ?? row["Name"]?.ToString() ?? "Library item";
    private static string Shorten(string value) => value.Length <= 350 ? value : value[..350] + "…";
    private static string FieldName(string name) => name switch
    {
        "Item" => "removed on one PC",
        "FilePath" or "LocalPath" => "file location",
        "OriginalPath" => "original audio location",
        "RuleCount" => "auto-download count",
        "RuleMode" => "auto-download order",
        "PositionSeconds" => "playback position in seconds",
        "IsPlayed" => "played",
        "ExcludeFromIpod" => "exclude from iPod",
        "ExcludedFromShuffle" => "exclude from shuffle",
        _ => name,
    };
    private static TextBlock Line(string value, string color) => new()
    { Text = value, Foreground = Hex(color), TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
}
