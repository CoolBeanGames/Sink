using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sink.Services;

namespace Sink.Dialogs;

/// <summary>
/// App settings: library / podcast folders, sync-on-connect, import mode, plus
/// one-shot library maintenance actions (refresh, export, import).
/// </summary>
public sealed class SettingsWindow : SinkDialog
{
    private readonly TextBox _library = Field();
    private readonly TextBox _podcasts = Field();
    private readonly CheckBox _syncOnConnect = new() { Content = "Sync as soon as an iPod is connected", Foreground = Hex("#C7CCD6") };
    private readonly ComboBox _importMode = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };

    private readonly Func<int> _refresh;
    private readonly Action _export;
    private readonly Action _import;

    /// <summary>Set when the user pressed Save.</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings settings, Func<int> refreshLibrary, Action exportLibrary, Action importLibrary)
    {
        _refresh = refreshLibrary;
        _export = exportLibrary;
        _import = importLibrary;

        Width = 560;
        SizeToContent = SizeToContent.Height;
        Title = "Settings";

        _library.Text = settings.LibraryLocation;
        _podcasts.Text = settings.PodcastLocation;
        _syncOnConnect.IsChecked = settings.SyncOnConnect;
        _importMode.Items.Add("Reference — leave files where they are");
        _importMode.Items.Add("Copy — copy files into the library folder");
        _importMode.Items.Add("Move — move files into the library folder");
        _importMode.SelectedIndex = (int)settings.ImportMode;

        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        body.Children.Add(Eyebrow("SETTINGS"));
        body.Children.Add(new TextBlock { Text = "Preferences", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 4) });

        body.Children.Add(FolderRow("Library folder", "Downloaded and copied/moved tracks live here.", _library));
        body.Children.Add(FolderRow("Podcast folder", "Downloaded podcast episodes live here.", _podcasts));

        body.Children.Add(Label("Import mode"));
        body.Children.Add(_importMode);

        _syncOnConnect.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(_syncOnConnect);

        body.Children.Add(Label("Library maintenance"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(SecondaryButton("Refresh library", (_, _) =>
        {
            var n = _refresh();
            MessageBox.Show(this, $"Re-read {n} track{(n == 1 ? "" : "s")}. Missing files were removed.", "Refresh library",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        actions.Children.Add(SecondaryButton("Export library…", (_, _) => _export()));
        actions.Children.Add(SecondaryButton("Import library…", (_, _) => _import()));
        actions.Children.Add(SecondaryButton("Open logs…", (_, _) => Log.OpenFolder()));
        for (var i = 1; i < actions.Children.Count; i++)
            actions.Children[i].SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        body.Children.Add(actions);
        body.Children.Add(new TextBlock
        {
            Text = $"Logs — including why a download failed — are written to {Log.Directory}",
            Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0),
        };
        footer.Children.Add(FooterBtn("Cancel", primary: false, (_, _) => Close()));
        footer.Children.Add(FooterBtn("Save", primary: true, Save_Click));
        body.Children.Add(footer);

        Content = body;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new AppSettings
        {
            LibraryLocation = _library.Text.Trim(),
            PodcastLocation = _podcasts.Text.Trim(),
            SyncOnConnect = _syncOnConnect.IsChecked == true,
            ImportMode = (ImportMode)Math.Max(0, _importMode.SelectedIndex),
        };
        DialogResult = true;
    }

    private UIElement FolderRow(string label, string hint, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Foreground = Hex("#7D8493"), FontSize = 10, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = hint, Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 2, 0, 5) });
        var row = new DockPanel();
        var browse = SecondaryButton("Browse…", (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = label, InitialDirectory = SafeDir(box.Text) };
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        panel.Children.Add(row);
        return panel;
    }

    private static string SafeDir(string path)
    {
        try { return System.IO.Directory.Exists(path) ? path : ""; } catch { return ""; }
    }

    private TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = Hex("#7D8493"), FontSize = 10, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 20, 0, 6),
    };

    private TextBlock Eyebrow(string text) => new()
    {
        Text = text, Foreground = Hex("#8B7CFF"), FontSize = 10, FontWeight = FontWeights.SemiBold,
    };

    private Button SecondaryButton(string label, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(13, 8, 13, 8), BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Hex("#242A34"), Foreground = Hex("#D1D5DD"),
        };
        b.Click += onClick;
        return b;
    }

    private Button FooterBtn(string label, bool primary, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? Hex("#6559CE") : Hex("#242A34"), Foreground = primary ? Hex("#FFFFFF") : Hex("#D1D5DD"),
        };
        b.Click += onClick;
        return b;
    }

    private static TextBox Field() => new()
    {
        Height = 34, Padding = new Thickness(10, 0, 10, 0),
        Foreground = Hex("#F4F6FA"), Background = Hex("#0F1218"), BorderBrush = Hex("#353C49"),
        BorderThickness = new Thickness(1), CaretBrush = Hex("#F4F6FA"),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
}
