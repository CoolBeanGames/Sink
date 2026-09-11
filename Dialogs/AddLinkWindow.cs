using System.Windows;
using System.Windows.Controls;

namespace Sink.Dialogs;

/// <summary>
/// Prompts for one or more YouTube / YouTube Music links, one per line. Same
/// dark chrome as the metadata editor; the caller dims and blurs the page
/// behind it.
/// </summary>
public sealed class AddLinkWindow : SinkDialog
{
    private readonly TextBox _link = new()
    {
        MinHeight = 96,
        MaxHeight = 260,
        Padding = new Thickness(11, 9, 11, 9),
        FontSize = 13,
        Foreground = Hex("#F4F6FA"),
        Background = Hex("#0F1218"),
        BorderBrush = Hex("#353C49"),
        BorderThickness = new Thickness(1),
        CaretBrush = Hex("#F4F6FA"),
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    /// <summary>Every non-blank line, trimmed — one link each.</summary>
    public IReadOnlyList<string> Links => _link.Text
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    public AddLinkWindow()
    {
        Width = 520;
        SizeToContent = SizeToContent.Height;
        Title = "Add links";

        Loaded += (_, _) => { _link.Focus(); TryPasteClipboard(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        buttons.Children.Add(MakeButton("Cancel", primary: false, (_, _) => Close()));
        buttons.Children.Add(MakeButton("Add", primary: true, (_, _) => Accept()));

        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        body.Children.Add(new TextBlock
        {
            Text = "DOWNLOAD", Foreground = Hex("#8B7CFF"), FontSize = 10, FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Paste links", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0),
        });
        body.Children.Add(new TextBlock
        {
            Text = "YouTube or YouTube Music — one link per line. A track, album, artist, or playlist.",
            Foreground = Hex("#858C9B"), FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        _link.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(_link);
        body.Children.Add(buttons);

        Content = body;
    }

    private Button MakeButton(string label, bool primary, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? Hex("#6559CE") : Hex("#242A34"),
            Foreground = primary ? Hex("#FFFFFF") : Hex("#D1D5DD"),
        };
        button.Click += onClick;
        return button;
    }

    private void TryPasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                // Only auto-fill for a clipboard that's actually link-shaped —
                // one or more lines that each look like a URL — so an ordinary
                // copied sentence doesn't land in the box uninvited.
                var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (lines.Count > 0 && lines.All(l => l.Contains("youtu", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(l, UriKind.Absolute, out _)))
                    _link.Text = string.Join('\n', lines);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or OutOfMemoryException) { }
        _link.SelectAll();
    }

    private void Accept()
    {
        if (Links.Count == 0) return;
        DialogResult = true;
    }
}
