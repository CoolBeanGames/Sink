using System.Windows;
using System.Windows.Controls;

namespace Sink.Dialogs;

/// <summary>
/// Prompts for a single YouTube / YouTube Music link. Same dark chrome as the
/// metadata editor; the caller dims and blurs the page behind it.
/// </summary>
public sealed class AddLinkWindow : SinkDialog
{
    private readonly TextBox _link = new()
    {
        Height = 40,
        Padding = new Thickness(11, 0, 11, 0),
        FontSize = 13,
        Foreground = Hex("#F4F6FA"),
        Background = Hex("#0F1218"),
        BorderBrush = Hex("#353C49"),
        BorderThickness = new Thickness(1),
        CaretBrush = Hex("#F4F6FA"),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    public string Link => _link.Text.Trim();

    public AddLinkWindow()
    {
        Width = 480;
        SizeToContent = SizeToContent.Height;
        Title = "Add link";

        _link.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Accept(); };
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
            Text = "Paste a link", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0),
        });
        body.Children.Add(new TextBlock
        {
            Text = "YouTube or YouTube Music — a track, album, or playlist.",
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
                if (text.Contains("youtu", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out _))
                    _link.Text = text;
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or OutOfMemoryException) { }
        _link.SelectAll();
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(Link)) return;
        DialogResult = true;
    }
}
