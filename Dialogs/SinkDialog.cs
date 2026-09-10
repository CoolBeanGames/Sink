using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Sink.Dialogs;

/// <summary>
/// Base class for Sink's modal pop-ups. Gives every dialog the same dark
/// chrome plus a consistent eyebrow / heading / body / footer layout so new
/// dialogs only have to supply their body content and footer buttons.
/// </summary>
public abstract class SinkDialog : Window
{
    private static readonly BrushConverter Brushes = new();
    protected static Brush Hex(string hex) => (Brush)Brushes.ConvertFromString(hex)!;

    protected SinkDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SnapsToDevicePixels = true;
        Background = Hex("#161A22");
        Foreground = Hex("#F4F6FA");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
    }

    protected sealed record FooterButton(string Label, bool Primary, RoutedEventHandler OnClick);

    /// <summary>
    /// Lets the arrow keys walk a stack of edit fields: Up / Down move between
    /// them, Left / Right do too once the caret has reached the end of the text.
    /// Matches the inline metadata grid on the download page (task 104).
    /// </summary>
    protected static void EnableArrowFieldNavigation(params Control[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            var index = i;
            fields[i].PreviewKeyDown += (sender, e) =>
            {
                if (e.Handled) return; // e.g. an autocomplete popup ate the arrow
                var target = index;
                switch (e.Key)
                {
                    case Key.Down: target = index + 1; break;
                    case Key.Up: target = index - 1; break;
                    case Key.Right when AtEnd(sender): target = index + 1; break;
                    case Key.Left when AtStart(sender): target = index - 1; break;
                    default: return;
                }
                if (target < 0 || target >= fields.Length || target == index) return;
                fields[target].Focus();
                if (fields[target] is TextBox box) box.SelectAll();
                e.Handled = true;
            };
        }

        static bool AtEnd(object s) => s is TextBox t && t.SelectionLength == 0 && t.CaretIndex >= t.Text.Length;
        static bool AtStart(object s) => s is TextBox t && t.SelectionLength == 0 && t.CaretIndex == 0;
    }

    /// <summary>Builds and installs the standard shell around <paramref name="body"/>.</summary>
    protected void Compose(string eyebrow, string heading, string? subtitle, UIElement body, params FooterButton[] footer)
    {
        var root = new Grid { Margin = new Thickness(26, 24, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = eyebrow.ToUpperInvariant(), Foreground = Hex("#8B7CFF"),
            FontSize = 10, FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = heading, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            header.Children.Add(new TextBlock
            {
                Text = subtitle, Foreground = Hex("#858C9B"), FontSize = 12,
                Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap
            });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var bodyHost = new ContentControl
        {
            Content = body,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(bodyHost, 1);
        root.Children.Add(bodyHost);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 20, 0, 0)
        };
        foreach (var spec in footer)
        {
            var button = new Button
            {
                Content = spec.Label, Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = spec.Primary ? Hex("#6559CE") : Hex("#242A34"),
                Foreground = spec.Primary ? Hex("#FFFFFF") : Hex("#D1D5DD")
            };
            button.Click += spec.OnClick;
            buttons.Children.Add(button);
        }
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
