using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sink.Models;
using Sink.Services;
using Sink.Services.Download;

namespace Sink.Dialogs;

/// <summary>
/// App settings: library / podcast folders, sync-on-connect, import mode, plus
/// one-shot library maintenance actions (refresh, export, import).
/// </summary>
public sealed class SettingsWindow : SinkDialog
{
    private static readonly string[] CookieChoices = ["auto", "none", "edge", "chrome", "firefox", "brave", "file"];

    private readonly TextBox _library = Field();
    private readonly TextBox _podcasts = Field();
    private readonly CheckBox _syncOnConnect = new() { Content = "Sync as soon as an iPod is connected", Foreground = Hex("#C7CCD6") };
    private readonly ComboBox _importMode = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _youTubeCookies = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cookieFile = Field();
    private readonly UIElement _cookieFileRow;
    private readonly TextBox _deezerArl = Field();
    private readonly CheckBox _keepHighQuality = new() { Content = "Keep high quality", Foreground = Hex("#C7CCD6") };
    private readonly TextBox _highQuality = Field();
    private readonly AppSettings _initial;

    private readonly Func<IProgress<LibraryMaintenanceProgress>, Task<int>> _refresh;
    private readonly Action _export;
    private readonly Action _import;
    private readonly Func<IProgress<LibraryMaintenanceProgress>, Task<string>> _organize;
    private readonly List<Button> _actionButtons = [];
    private readonly TextBlock _maintenanceStatus = new()
    {
        Foreground = Hex("#A79DFF"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
    };
    private readonly ProgressBar _maintenanceProgress = new()
    {
        Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 7, 0, 0),
        Background = Hex("#1B1F28"), Foreground = Hex("#8B7CFF"), BorderThickness = new Thickness(0),
        Visibility = Visibility.Collapsed,
    };
    private StackPanel? _footer;
    private bool _maintenanceRunning;

    /// <summary>Set when the user pressed Save.</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(
        AppSettings settings,
        Func<IProgress<LibraryMaintenanceProgress>, Task<int>> refreshLibrary,
        Action exportLibrary,
        Action importLibrary,
        Func<IProgress<LibraryMaintenanceProgress>, Task<string>> organizeLibrary)
    {
        _refresh = refreshLibrary;
        _export = exportLibrary;
        _import = importLibrary;
        _organize = organizeLibrary;

        Width = 640;
        SizeToContent = SizeToContent.Height;
        Title = "Settings";

        _initial = settings;
        _library.Text = settings.LibraryLocation;
        _keepHighQuality.IsChecked = settings.KeepHighQuality;
        _highQuality.Text = settings.HighQualityRoot;
        _podcasts.Text = settings.PodcastLocation;
        _syncOnConnect.IsChecked = settings.SyncOnConnect;
        _importMode.Items.Add("Reference — leave files where they are");
        _importMode.Items.Add("Copy — copy files into the library folder");
        _importMode.Items.Add("Move — move files into the library folder");
        _importMode.SelectedIndex = (int)settings.ImportMode;

        _youTubeCookies.Items.Add("Auto-detect (recommended)");
        _youTubeCookies.Items.Add("Off");
        _youTubeCookies.Items.Add("Microsoft Edge");
        _youTubeCookies.Items.Add("Google Chrome");
        _youTubeCookies.Items.Add("Firefox");
        _youTubeCookies.Items.Add("Brave");
        _youTubeCookies.Items.Add("Cookie file…");
        var cookieIndex = Array.IndexOf(CookieChoices, (settings.YouTubeCookies ?? "auto").Trim().ToLowerInvariant());
        _youTubeCookies.SelectedIndex = Math.Max(0, cookieIndex);

        _cookieFile.Text = settings.CookieFilePath;
        _deezerArl.Text = StreamripToolManager.GetDeezerArl();
        var browseCookieFile = SecondaryButton("Browse…", (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Choose a cookies.txt file", Filter = "Cookie files|*.txt|All files|*.*" };
            if (dialog.ShowDialog(this) == true) _cookieFile.Text = dialog.FileName;
        });
        browseCookieFile.Margin = new Thickness(8, 0, 0, 0);
        var exportFirefoxCookies = SecondaryButton("Export Firefox cookies…", (_, _) =>
        {
            var result = FirefoxCookieExporter.Export();
            if (!result.Ok)
            {
                MessageBox.Show(this, result.Error ?? "Couldn't export cookies from Firefox.", "Export Firefox cookies",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cookieFile.Text = result.Path;
            _youTubeCookies.SelectedIndex = Array.IndexOf(CookieChoices, "file");
            MessageBox.Show(this, $"Exported {result.Count} cookie{(result.Count == 1 ? "" : "s")} from Firefox.", "Export Firefox cookies",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
        exportFirefoxCookies.Margin = new Thickness(8, 0, 0, 0);
        var cookieFileRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(exportFirefoxCookies, Dock.Right);
        cookieFileRow.Children.Add(exportFirefoxCookies);
        DockPanel.SetDock(browseCookieFile, Dock.Right);
        cookieFileRow.Children.Add(browseCookieFile);
        cookieFileRow.Children.Add(_cookieFile);
        _cookieFileRow = cookieFileRow;
        void UpdateCookieFileRowVisibility() =>
            cookieFileRow.Visibility = CookieChoices[Math.Max(0, _youTubeCookies.SelectedIndex)] == "file" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCookieFileRowVisibility();
        _youTubeCookies.SelectionChanged += (_, _) => UpdateCookieFileRowVisibility();

        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        body.Children.Add(Eyebrow("SETTINGS"));
        body.Children.Add(new TextBlock { Text = "Preferences", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 4) });

        body.Children.Add(FolderRow("Library folder", "Downloaded and copied/moved tracks live here.", _library));
        body.Children.Add(FolderRow("Podcast folder", "Downloaded podcast episodes live here.", _podcasts));

        _keepHighQuality.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(_keepHighQuality);
        body.Children.Add(new TextBlock
        {
            Text = "Deezer downloads are kept as real FLAC in the high-quality folder, and your library copy is converted from that into " +
                   "the format picked on the Download page. With FLAC picked, the library file already is the FLAC, so nothing is stored twice. " +
                   "\"Download high quality\" (right-click an album, artist, genre or track) fetches a real FLAC for anything that doesn't have one yet.",
            Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(FolderRow("High-quality folder", "Where FLAC originals are kept.", _highQuality));

        body.Children.Add(Label("Import mode"));
        body.Children.Add(_importMode);

        body.Children.Add(Label("YouTube cookies"));
        body.Children.Add(_youTubeCookies);
        body.Children.Add(_cookieFileRow);
        body.Children.Add(new TextBlock
        {
            Text = "Lets yt-dlp borrow a signed-in browser's cookies so YouTube doesn't ask Sink to \"confirm you're not a bot\". " +
                   "\"Cookie file\" uses a cookies.txt you point at directly instead of reading live from a browser — more reliable than Chrome/Edge (their cookie store can't always be read at all) and immune to a browser being open. " +
                   "\"Export Firefox cookies…\" builds one for you straight from Firefox, since its cookie store is the one browser Sink can read directly and safely.",
            Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(Label("Deezer ARL token"));
        
        var deezerArlRow = new DockPanel();
        var extractArlBtn = SecondaryButton("Extract from Firefox…", (_, _) =>
        {
            var arl = FirefoxCookieExporter.GetDeezerArl();
            if (string.IsNullOrWhiteSpace(arl))
            {
                MessageBox.Show(this, "Could not find a Deezer ARL cookie in Firefox. Make sure you are logged into deezer.com in Firefox.", "Extract ARL", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _deezerArl.Text = arl;
                try
                {
                    StreamripToolManager.SetDeezerArl(arl);
                    MessageBox.Show(this, "Successfully extracted your Deezer ARL from Firefox and saved it to Streamrip's config!", "Extract ARL", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Extracted successfully, but couldn't save to config: {ex.Message}", "Extract ARL", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        });
        extractArlBtn.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(extractArlBtn, Dock.Right);
        deezerArlRow.Children.Add(extractArlBtn);
        deezerArlRow.Children.Add(_deezerArl);
        
        body.Children.Add(deezerArlRow);
        body.Children.Add(new TextBlock
        {
            Text = "Used by Streamrip to download tracks from Deezer. You can extract this straight from Firefox if you're signed in, or grab it manually from your browser's cookies.",
            Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        _syncOnConnect.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(_syncOnConnect);

        body.Children.Add(Label("Library maintenance"));
        // WrapPanel rather than a horizontal StackPanel: a 4th button here
        // already pushed "Open logs…" past the dialog's edge at a fixed
        // width — wrapping to a second row degrades gracefully instead of
        // silently clipping the next one that gets added (task 155).
        var actions = new WrapPanel();
        var refreshButton = SecondaryButton("Refresh library", async (_, _) =>
        {
            await RunMaintenanceAsync("Refreshing library…", "Refresh library", async progress =>
            {
                var count = await _refresh(progress);
                return $"Re-read {count} track{(count == 1 ? "" : "s")}. Missing files were removed.";
            });
        });
        actions.Children.Add(refreshButton);
        actions.Children.Add(SecondaryButton("Export library…", (_, _) => _export()));
        actions.Children.Add(SecondaryButton("Import library…", (_, _) => _import()));
        var organizeButton = SecondaryButton("Organize library…", async (_, _) =>
        {
            await RunMaintenanceAsync("Organizing library…", "Organize library", _organize);
        });
        actions.Children.Add(organizeButton);
        actions.Children.Add(SecondaryButton("Open logs…", (_, _) => Log.OpenFolder()));
        for (var i = 0; i < actions.Children.Count; i++)
            actions.Children[i].SetValue(MarginProperty, new Thickness(i == 0 ? 0 : 8, 0, 0, 8));
        _actionButtons.AddRange(actions.Children.OfType<Button>());
        body.Children.Add(actions);
        body.Children.Add(_maintenanceStatus);
        body.Children.Add(_maintenanceProgress);
        body.Children.Add(new TextBlock
        {
            Text = $"Logs — including why a download failed — are written to {Log.Directory}",
            Foreground = Hex("#6E7584"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        _footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0),
        };
        _footer.Children.Add(FooterBtn("Cancel", primary: false, (_, _) => Close()));
        _footer.Children.Add(FooterBtn("Save", primary: true, Save_Click));
        body.Children.Add(_footer);

        Content = body;
        Closing += (_, e) =>
        {
            if (!_maintenanceRunning) return;
            e.Cancel = true;
            _maintenanceStatus.Text = "Wait for library maintenance to finish before closing Settings.";
            _maintenanceStatus.Foreground = Hex("#E7B86E");
        };
    }

    private async Task RunMaintenanceAsync(
        string startingMessage,
        string dialogTitle,
        Func<IProgress<LibraryMaintenanceProgress>, Task<string>> work)
    {
        if (_maintenanceRunning) return;
        _maintenanceRunning = true;
        SetMaintenanceControlsEnabled(false);
        _maintenanceStatus.Text = startingMessage;
        _maintenanceStatus.Foreground = Hex("#A79DFF");
        _maintenanceStatus.Visibility = Visibility.Visible;
        _maintenanceProgress.Value = 0;
        _maintenanceProgress.IsIndeterminate = true;
        _maintenanceProgress.Visibility = Visibility.Visible;

        var progress = new Progress<LibraryMaintenanceProgress>(update =>
        {
            _maintenanceStatus.Text = update.Message;
            _maintenanceStatus.Foreground = Hex("#A79DFF");
            _maintenanceProgress.IsIndeterminate = update.IsIndeterminate;
            if (!update.IsIndeterminate) _maintenanceProgress.Value = update.Percent;
        });

        try
        {
            var summary = await work(progress);
            _maintenanceStatus.Text = summary;
            _maintenanceStatus.Foreground = Hex("#8FD69A");
            _maintenanceProgress.IsIndeterminate = false;
            _maintenanceProgress.Value = 100;
            MessageBox.Show(this, summary, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error($"{dialogTitle} failed", ex);
            _maintenanceStatus.Text = $"{dialogTitle} failed: {ex.Message}";
            _maintenanceStatus.Foreground = Hex("#E0918C");
            _maintenanceProgress.IsIndeterminate = false;
            _maintenanceProgress.Value = 0;
            MessageBox.Show(this, ex.Message, $"{dialogTitle} failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _maintenanceRunning = false;
            SetMaintenanceControlsEnabled(true);
        }
    }

    private void SetMaintenanceControlsEnabled(bool enabled)
    {
        foreach (var button in _actionButtons) button.IsEnabled = enabled;
        if (_footer is not null) _footer.IsEnabled = enabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cookieChoice = CookieChoices[Math.Max(0, _youTubeCookies.SelectedIndex)];
        // Start from the settings this dialog was opened with, so values it
        // doesn't show (download options, sidebar width) aren't reset to
        // their defaults on Save.
        Result = _initial.Clone();
        Result.LibraryLocation = _library.Text.Trim();
        Result.PodcastLocation = _podcasts.Text.Trim();
        Result.SyncOnConnect = _syncOnConnect.IsChecked == true;
        Result.ImportMode = (ImportMode)Math.Max(0, _importMode.SelectedIndex);
        Result.YouTubeCookies = cookieChoice;
        Result.CookieFilePath = _cookieFile.Text.Trim();
        Result.KeepHighQuality = _keepHighQuality.IsChecked == true;
        // Stored blank while it's still the default, so it keeps following
        // the library folder if that moves.
        var hq = _highQuality.Text.Trim();
        var defaultHq = new AppSettings { LibraryLocation = Result.LibraryLocation }.HighQualityRoot;
        Result.HighQualityLocation = string.Equals(hq, defaultHq, StringComparison.OrdinalIgnoreCase) ? "" : hq;
        // A cookie failure earlier this session (e.g. Chrome's DPAPI-encrypted
        // store) latches "give up on cookies" for the rest of the process —
        // otherwise switching to a browser that actually works, like Firefox,
        // would silently never get retried and keep failing the same way.
        Sink.Services.Download.DownloadService.ResetCookieLatch();
        try { StreamripToolManager.SetDeezerArl(_deezerArl.Text.Trim()); } catch (Exception ex) { Log.Warn($"Could not save ARL: {ex.Message}"); }
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
