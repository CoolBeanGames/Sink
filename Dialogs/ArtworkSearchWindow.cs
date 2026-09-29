using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Sink.Services;

namespace Sink.Dialogs;

/// <summary>
/// Shows Google Image results for an artist or genre and hands back the bytes
/// of the image the user picks. Google only serves image results to a real
/// browser (plain HTTP requests get a "turn on JavaScript" page), so the
/// results are shown in an embedded WebView2. An image is picked either by
/// opening it and pressing "Use selected image" (takes the biggest image on
/// screen, preferring a full-size one over Google's thumbnails) or by
/// right-clicking any image and choosing "Use as artwork".
/// </summary>
public sealed class ArtworkSearchWindow : SinkDialog
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly string _name;
    private readonly WebView2 _web = new() { Height = 560 };
    private readonly TextBlock _status;

    /// <summary>The picked image, set when the dialog closes with true.</summary>
    public byte[]? ImageBytes { get; private set; }

    public ArtworkSearchWindow(string name, string query)
    {
        _name = name;
        Title = $"Artwork for {name}";
        Width = 1000;
        SizeToContent = SizeToContent.Height;

        _status = new TextBlock
        {
            Text = "Click an image to open it, then press “Use selected image” — or right-click any image and choose “Use as artwork”.",
            Foreground = Hex("#858C9B"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var body = new StackPanel();
        body.Children.Add(new Border
        {
            Child = _web, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            BorderBrush = Hex("#262C38"), BorderThickness = new Thickness(1),
        });
        body.Children.Add(_status);

        Compose("Artwork", $"Search artwork for {name}", null, body,
            new FooterButton("Cancel", false, (_, _) => DialogResult = false),
            new FooterButton("Use selected image", true, async (_, _) => await UseSelectedAsync()));

        Loaded += async (_, _) => await StartAsync(query);
        Closed += (_, _) => _web.Dispose();
    }

    private async Task StartAsync(string query)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(LibraryStore.Directory, "webview2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Artwork search: WebView2 unavailable", ex);
            _status.Text = "Image search needs the Microsoft Edge WebView2 Runtime, which couldn't be started on this PC.";
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.ContextMenuRequested += (_, e) =>
        {
            var target = e.ContextMenuTarget;
            if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || string.IsNullOrEmpty(target.SourceUri)) return;
            var source = target.SourceUri;
            var item = core.Environment.CreateContextMenuItem($"Use as artwork for {_name}", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += async (_, _) => await UseImageAsync(source);
            e.MenuItems.Insert(0, item);
        };
        core.Navigate("https://www.google.com/search?tbm=isch&hl=en&q=" + Uri.EscapeDataString(query));
    }

    // The biggest image currently on screen, weighting real full-size images
    // (not Google's own gstatic thumbnails / data URIs) well above thumbnails —
    // once a result is opened, its large preview is what this lands on.
    private const string SelectedImageScript = """
        (() => {
          let best = null, bestScore = 0;
          for (const img of document.images) {
            const r = img.getBoundingClientRect();
            if (r.width < 40 || r.height < 40 || r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) continue;
            const src = img.currentSrc || img.src;
            if (!src) continue;
            const full = /^https?:/.test(src) && !/(gstatic|google)\.com/.test(src);
            const score = r.width * r.height * (full ? 4 : 1);
            if (score > bestScore) { bestScore = score; best = src; }
          }
          return best;
        })()
        """;

    private async Task UseSelectedAsync()
    {
        if (_web.CoreWebView2 is null) return;
        string? src = null;
        try { src = JsonSerializer.Deserialize<string?>(await _web.CoreWebView2.ExecuteScriptAsync(SelectedImageScript)); }
        catch (JsonException) { }
        if (string.IsNullOrEmpty(src))
        {
            _status.Text = "No image is open yet — click one of the results first.";
            return;
        }
        await UseImageAsync(src);
    }

    private async Task UseImageAsync(string source)
    {
        _status.Text = "Downloading image…";
        try
        {
            byte[] bytes;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0 || !source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("unsupported image data");
                bytes = Convert.FromBase64String(source[(comma + 1)..]);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(source);
            }
            if (bytes.Length == 0) throw new InvalidDataException("empty image");
            ImageBytes = bytes;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException)
        {
            Log.Warn($"Artwork search: couldn't download {source[..Math.Min(120, source.Length)]}: {ex.Message}");
            _status.Text = "That image couldn't be downloaded — the site may block it. Try another one.";
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("image/webp,image/jpeg,image/png,image/*;q=0.8");
        return http;
    }
}
