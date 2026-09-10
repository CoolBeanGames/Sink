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
        Height = 34,
        Padding = new Thickness(10, 7, 10, 7),
        Foreground = Hex("#F4F6FA"),
        Background = Hex("#0F1218"),
        BorderBrush = Hex("#353C49"),
        CaretBrush = Hex("#F4F6FA"),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    public string Link => _link.Text.Trim();

    public AddLinkWindow()
    {
        Width = 460;
        Height = 210;
        Title = "Add link";

        _link.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Accept(); };
        Loaded += (_, _) => { _link.Focus(); TryPasteClipboard(); };

        Compose("Download", "Paste a link", "YouTube or YouTube Music — a track, album, or playlist.", _link,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Add", true, (_, _) => Accept()));
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
