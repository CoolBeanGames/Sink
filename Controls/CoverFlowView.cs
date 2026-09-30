using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Sink.Models;

namespace Sink.Controls;

/// <summary>One album on the Cover Flow shelf.</summary>
public sealed record CoverFlowAlbum(string Album, string Artist, string Genre, string? ArtPath, IReadOnlyList<Track> Tracks);

/// <summary>
/// Experimental "Cover Flow" (coverflow branch): every album is a 3D CD jewel
/// case standing spine-out on a wooden shelf against a light wall. The case at
/// the centre is the selection — it slides out of the shelf and turns to face
/// the viewer, and slides back when something else is selected. Clicking the
/// selected case flips it over to its track list; clicking a track there plays
/// it. Scroll / arrow keys / clicking another case browse; Esc flips back or
/// closes. Everything is drawn in one Viewport3D, and a per-frame easing loop
/// (CompositionTarget.Rendering) moves every case toward where it should be,
/// so browsing glides instead of jumping.
/// </summary>
public sealed class CoverFlowView : UserControl
{
    // Jewel-case proportions (142 × 125 × 10 mm), in scene units.
    private const double W = 1.42, H = 1.25, T = 0.11;
    /// <summary>Spacing between cases standing on the shelf.</summary>
    private const double Spacing = T + 0.018;
    /// <summary>Extra room either side of the pulled-out case.</summary>
    private const double Gap = W / 2 + 0.28;
    /// <summary>Width of the black hinge bar down the spine edge of the front and back.</summary>
    private const double HingeWidth = 0.09;
    /// <summary>Vertical field of view: WPF's FieldOfView is horizontal, so it's recomputed from this on resize to keep the shelf framed at any window shape.</summary>
    private const double VerticalFov = 17;
    private const int BackWidth = 568, BackHeight = 500, BackHeaderHeight = 86, BackRowMax = 30;

    public event Action? CloseRequested;
    /// <summary>Raised with the album's tracks and the index of the one to start with.</summary>
    public event Action<IReadOnlyList<Track>, int>? PlayRequested;

    private readonly IReadOnlyList<CoverFlowAlbum> _all;
    private readonly List<CaseVisual> _cases = [];
    private readonly Dictionary<MeshGeometry3D, (CaseVisual Case, bool IsBack)> _meshes = [];
    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly ModelVisual3D _caseRoot = new();
    private readonly AutoCompleteTextBox _search;
    private readonly TextBlock _caption = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subCaption = new() { FontSize = 12, Foreground = SoftInk, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _count = new() { FontSize = 12, Foreground = SoftInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _playButton;

    private double _position;      // eased, fractional centre index
    private int _target;           // centre index we're heading to
    private int _flipped = -1;     // case showing its back
    private double _flipAmount;    // 0 front … 1 back, eased
    private TimeSpan _lastFrame;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x2B, 0x24, 0x1E));
    private static readonly Brush SoftInk = new SolidColorBrush(Color.FromRgb(0x6E, 0x62, 0x55));

    public CoverFlowView(IReadOnlyList<CoverFlowAlbum> albums)
    {
        _all = albums;
        Focusable = true;
        FocusVisualStyle = null;

        // --- light wall ---
        var root = new Grid { Background = WallBrush() };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // --- top bar: close, title, search ---
        var bar = new DockPanel { Margin = new Thickness(24, 18, 24, 6), LastChildFill = false };
        var close = new Button
        {
            Content = "✕  Close", Padding = new Thickness(14, 7, 14, 7), BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x2B, 0x24, 0x1E)), Foreground = Ink, FontWeight = FontWeights.SemiBold,
        };
        close.Click += (_, _) => CloseRequested?.Invoke();
        bar.Children.Add(close);
        bar.Children.Add(new TextBlock { Text = "Cover Flow", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_count);
        _search = new AutoCompleteTextBox
        {
            SuggestionField = SuggestionField.Any, Width = 360, Height = 36, Padding = new Thickness(12, 0, 12, 0), FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center, Foreground = Ink, CaretBrush = Ink,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xBC, 0xAA)),
            ToolTip = "Filter by genre, artist, album or track",
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        var searchWrap = new DockPanel();
        searchWrap.Children.Add(new TextBlock { Text = "⌕", FontSize = 16, Foreground = SoftInk, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        searchWrap.Children.Add(_search);
        DockPanel.SetDock(searchWrap, Dock.Right);
        bar.Children.Add(searchWrap);
        root.Children.Add(bar);

        // --- 3D scene ---
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, 0.5, 7.6), new Vector3D(0, -0.075, -1), new Vector3D(0, 1, 0), 36);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x8C, 0x86, 0x80)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xD8, 0xD2, 0xC8), new Vector3D(-0.35, -0.55, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x4C, 0x48), new Vector3D(0.6, -0.2, -0.4)));
        _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(new ModelVisual3D { Content = Shelf() });
        _viewport.Children.Add(_caseRoot);
        Grid.SetRow(_viewport, 1);
        root.Children.Add(_viewport);
        // Keep the vertical framing fixed: a shorter or wider window used to
        // shrink the visible height and clip the top of the selected case.
        _viewport.SizeChanged += (_, _) =>
        {
            if (_viewport.ActualHeight <= 0 || _viewport.Camera is not PerspectiveCamera camera) return;
            var aspect = _viewport.ActualWidth / _viewport.ActualHeight;
            var half = Math.Tan(VerticalFov * Math.PI / 360) * aspect;
            camera.FieldOfView = Math.Min(150, 2 * Math.Atan(half) * 180 / Math.PI);
        };

        // --- caption + play ---
        var footer = new StackPanel { Margin = new Thickness(24, 0, 24, 22), HorizontalAlignment = HorizontalAlignment.Center };
        footer.Children.Add(_caption);
        footer.Children.Add(_subCaption);
        _playButton = new Button
        {
            Content = "▶  Play album", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x2E)), Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        _playButton.Click += (_, _) => { if (Current is { } c) PlayRequested?.Invoke(c.Album.Tracks, 0); };
        footer.Children.Add(_playButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        ApplyFilter();

        MouseWheel += (_, e) => { Move(e.Delta > 0 ? -1 : 1); e.Handled = true; };
        _viewport.MouseLeftButtonUp += Viewport_Click;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { CompositionTarget.Rendering += OnFrame; Focus(); };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private CaseVisual? Current => _target >= 0 && _target < _cases.Count ? _cases[_target] : null;

    // ---- browsing ---------------------------------------------------------

    private void Move(int delta)
    {
        if (_cases.Count == 0) return;
        _flipped = -1;
        _target = Math.Clamp(_target + delta, 0, _cases.Count - 1);
        UpdateCaption();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_search.IsKeyboardFocusWithin && e.Key is not (Key.Escape or Key.Down)) return;
        switch (e.Key)
        {
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.PageUp: Move(-8); break;
            case Key.PageDown: Move(8); break;
            case Key.Home: Move(-_cases.Count); break;
            case Key.End: Move(_cases.Count); break;
            case Key.Down: Focus(); break;
            case Key.Enter or Key.Space: ToggleFlip(); break;
            case Key.Escape:
                if (_flipped >= 0) { _flipped = -1; UpdateCaption(); }
                else CloseRequested?.Invoke();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void ToggleFlip()
    {
        if (Current is not { } c) return;
        if (_flipped == _target) _flipped = -1;
        else { c.EnsureBack(); _flipped = _target; }
        UpdateCaption();
    }

    private void Viewport_Click(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport)) as RayMeshGeometry3DHitTestResult;
        if (hit is null || !_meshes.TryGetValue(hit.MeshHit, out var info)) return;
        var index = _cases.IndexOf(info.Case);
        if (index != _target) { _flipped = -1; _target = index; UpdateCaption(); return; }
        if (_flipped == index && info.IsBack)
        {
            // Which track row was clicked: interpolate the hit's texture coordinate.
            var mesh = hit.MeshHit;
            var uv = Blend(mesh.TextureCoordinates[hit.VertexIndex1], hit.VertexWeight1)
                     + (Vector)Blend(mesh.TextureCoordinates[hit.VertexIndex2], hit.VertexWeight2)
                     + (Vector)Blend(mesh.TextureCoordinates[hit.VertexIndex3], hit.VertexWeight3);
            var row = info.Case.RowAt(uv.Y * BackHeight);
            if (row >= 0) { PlayRequested?.Invoke(info.Case.Album.Tracks, row); return; }
        }
        ToggleFlip();
    }

    private static Point Blend(Point p, double w) => new(p.X * w, p.Y * w);

    // ---- filtering --------------------------------------------------------

    private void ApplyFilter()
    {
        var q = _search.Text?.Trim() ?? "";
        var keep = Current?.Album;
        var shown = q.Length == 0 ? _all : _all.Where(a =>
            a.Album.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Genre.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Tracks.Any(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        _caseRoot.Children.Clear();
        _meshes.Clear();
        _cases.Clear();
        foreach (var album in shown)
        {
            var c = new CaseVisual(album, _meshes);
            _cases.Add(c);
            _caseRoot.Children.Add(c.Visual);
        }
        _flipped = -1;
        var again = keep is null ? -1 : shown.ToList().IndexOf(keep);
        _target = again >= 0 ? again : 0;
        _position = _target;
        _count.Text = $"{_cases.Count} album{(_cases.Count == 1 ? "" : "s")}";
        foreach (var c in _cases) c.Place(_cases.IndexOf(c) - _position, 0);
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        if (Current is not { } c)
        {
            _caption.Text = _cases.Count == 0 ? "No albums match" : "";
            _subCaption.Text = "";
            _playButton.Visibility = Visibility.Collapsed;
            return;
        }
        _caption.Text = c.Album.Album;
        var tracks = c.Album.Tracks.Count;
        _subCaption.Text = _flipped == _target
            ? $"{c.Album.Artist} · click a track to play it · Esc to flip back"
            : $"{c.Album.Artist}{(string.IsNullOrWhiteSpace(c.Album.Genre) ? "" : " · " + c.Album.Genre)} · {tracks} track{(tracks == 1 ? "" : "s")} · click the case to see its tracks";
        _playButton.Visibility = _flipped == _target ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- animation --------------------------------------------------------

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        if (_cases.Count == 0) return;

        var ease = 1 - Math.Exp(-dt * 9);
        var previous = _position;
        _position += (_target - _position) * ease;
        if (Math.Abs(_target - _position) < 0.0005) _position = _target;
        var previousFlip = _flipAmount;
        _flipAmount += ((_flipped == _target ? 1 : 0) - _flipAmount) * (1 - Math.Exp(-dt * 7));
        if (Math.Abs(previous - _position) < 1e-6 && Math.Abs(previousFlip - _flipAmount) < 1e-5) return;

        // Only cases near the centre move; the rest just keep sliding along the shelf.
        for (var i = 0; i < _cases.Count; i++)
            _cases[i].Place(i - _position, i == _target || i == _flipped ? _flipAmount : 0);
    }

    // ---- scene pieces -----------------------------------------------------

    private static Brush WallBrush()
    {
        var wall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        wall.GradientStops.Add(new GradientStop(Color.FromRgb(0xF6, 0xF1, 0xE8), 0));
        wall.GradientStops.Add(new GradientStop(Color.FromRgb(0xEC, 0xE4, 0xD7), 0.65));
        wall.GradientStops.Add(new GradientStop(Color.FromRgb(0xDD, 0xD2, 0xC2), 1));
        wall.Freeze();
        return wall;
    }

    private static Model3D Shelf()
    {
        var wood = new DiffuseMaterial(new ImageBrush(WoodTexture()) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1) });
        var group = new Model3DGroup();
        // The plank the cases stand on, and a slightly darker lip along its front edge.
        group.Children.Add(Box(new Point3D(0, -H / 2 - 0.07, -0.05), 60, 0.14, 1.9, wood, uRepeat: 18));
        var lip = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x6A, 0x42, 0x22)));
        group.Children.Add(Box(new Point3D(0, -H / 2 - 0.2, 0.93), 60, 0.12, 0.05, lip, uRepeat: 1));
        return group;
    }

    /// <summary>A procedural wood-grain strip: warm base, wavy darker grain lines and a little noise.</summary>
    private static BitmapSource WoodTexture()
    {
        const int w = 512, h = 128;
        var pixels = new byte[w * h * 4];
        var rng = new Random(7);
        var noise = Enumerable.Range(0, h).Select(_ => rng.NextDouble()).ToArray();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Periodic in x (whole cycles across the 512 px), so the tiled plank has no seam.
                var wave = Math.Sin(y * 0.19 + Math.Sin(x * 2 * Math.PI / w * 3 + noise[y] * 2) * 2.4 + noise[y] * 0.9);
                var grain = Math.Pow(Math.Abs(wave), 7);
                var fleck = (rng.NextDouble() - 0.5) * 10;
                var r = 176 - grain * 52 + fleck;
                var g = 122 - grain * 42 + fleck * 0.7;
                var b = 74 - grain * 30 + fleck * 0.5;
                var i = (y * w + x) * 4;
                pixels[i] = (byte)Math.Clamp(b, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(r, 0, 255);
                pixels[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>An axis-aligned box centred on <paramref name="c"/>, one material, top/front textured with u repeating.</summary>
    private static Model3D Box(Point3D c, double sx, double sy, double sz, Material material, double uRepeat)
    {
        var mesh = new MeshGeometry3D();
        void Face(Point3D a, Point3D b, Point3D d, Point3D e)
        {
            var start = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(d); mesh.Positions.Add(e);
            mesh.TextureCoordinates.Add(new Point(0, 1)); mesh.TextureCoordinates.Add(new Point(uRepeat, 1));
            mesh.TextureCoordinates.Add(new Point(uRepeat, 0)); mesh.TextureCoordinates.Add(new Point(0, 0));
            foreach (var k in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(start + k);
        }
        double x0 = c.X - sx / 2, x1 = c.X + sx / 2, y0 = c.Y - sy / 2, y1 = c.Y + sy / 2, z0 = c.Z - sz / 2, z1 = c.Z + sz / 2;
        Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1)); // front
        Face(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0)); // top
        Face(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0)); // back
        Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1)); // bottom
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>
    /// One jewel case. Six faces, each its own mesh so a click can tell which
    /// face (and on the back, which track) was hit: art on the front (glossy),
    /// artist / album on the spine, the track list on the back (rendered only
    /// once the case is first flipped), dark tinted plastic elsewhere.
    /// </summary>
    private sealed class CaseVisual
    {
        public CoverFlowAlbum Album { get; }
        public ModelVisual3D Visual { get; } = new();
        private readonly AxisAngleRotation3D _rotation = new(new Vector3D(0, 1, 0), 90);
        private readonly TranslateTransform3D _translate = new();
        private readonly GeometryModel3D _back;
        private double[] _rowTops = [];

        public CaseVisual(CoverFlowAlbum album, Dictionary<MeshGeometry3D, (CaseVisual, bool)> meshes)
        {
            Album = album;
            var plastic = new MaterialGroup();
            plastic.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x22, 0x23, 0x27))));
            plastic.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 30));

            var front = new MaterialGroup();
            front.Children.Add(new DiffuseMaterial(new ImageBrush(Art(album)) { Stretch = Stretch.UniformToFill }));
            front.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 60));

            var group = new Model3DGroup();
            GeometryModel3D Face(Point3D a, Point3D b, Point3D c, Point3D d, Material m, bool isBack = false)
            {
                var mesh = new MeshGeometry3D
                {
                    Positions = [a, b, c, d],
                    TextureCoordinates = [new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0)],
                    TriangleIndices = [0, 1, 2, 0, 2, 3],
                };
                meshes[mesh] = (this, isBack);
                var model = new GeometryModel3D(mesh, m);
                group.Children.Add(model);
                return model;
            }
            double x0 = -W / 2, x1 = W / 2, y0 = -H / 2, y1 = H / 2, z0 = -T / 2, z1 = T / 2;
            // A real jewel case's hinge: a black bar down the spine edge of
            // both faces — left of the art on the front, right of the insert
            // on the back.
            var hinge = new MaterialGroup();
            hinge.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10))));
            hinge.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 40));
            var xh = x0 + HingeWidth;
            Face(new(x0, y0, z1), new(xh, y0, z1), new(xh, y1, z1), new(x0, y1, z1), hinge);                    // front hinge
            Face(new(xh, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(xh, y1, z1), front);                    // front: art
            Face(new(xh, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(xh, y1, z0), hinge);                    // back hinge
            _back = Face(new(x1, y0, z0), new(xh, y0, z0), new(xh, y1, z0), new(x1, y1, z0), plastic, true);    // back: tracks (lazy)
            Face(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0),
                new DiffuseMaterial(new ImageBrush(Spine(album))));                                                 // spine (−X)
            Face(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), plastic);                  // opening edge
            Face(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), plastic);                  // top
            Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), plastic);                  // bottom

            Visual.Content = group;
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(_rotation));
            transform.Children.Add(_translate);
            Visual.Transform = transform;
        }

        /// <summary>
        /// Positions the case for its distance <paramref name="d"/> from the
        /// centre (fractional while gliding). At the centre it stands out of
        /// the shelf facing the viewer; further away it's spine-out on the
        /// shelf, with a gap either side of the pulled-out one.
        /// </summary>
        public void Place(double d, double flip)
        {
            var selected = Math.Max(0, 1 - Math.Abs(d));
            var s = selected * selected * (3 - 2 * selected); // smoothstep
            var sign = Math.Sign(d);
            var x = sign * (Spacing * Math.Abs(d) + Gap * Math.Min(1, Math.Abs(d)));
            _translate.OffsetX = x;
            _translate.OffsetY = s * 0.22;
            _translate.OffsetZ = s * 1.05 + flip * 0.3;
            // Spine-out on the shelf is +90° (the −X spine faces the camera);
            // selected turns to 0° (front), flipped carries on to 180° (back).
            _rotation.Angle = 90 * (1 - s) + 180 * flip;
        }

        public void EnsureBack()
        {
            if (_rowTops.Length > 0 || Album.Tracks.Count == 0) return;
            var (image, rows) = TrackList(Album);
            _rowTops = rows;
            _back.Material = new DiffuseMaterial(new ImageBrush(image));
        }

        /// <summary>The track index at a vertical pixel position on the back, or −1.</summary>
        public int RowAt(double y)
        {
            for (var i = 0; i < _rowTops.Length - 1; i++)
                if (y >= _rowTops[i] && y < _rowTops[i + 1]) return i;
            return -1;
        }

        private static ImageSource Art(CoverFlowAlbum album)
        {
            if (album.ArtPath is { } path && File.Exists(path))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelWidth = 360;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch { }
            }
            // No art: a coloured card with the album's initial.
            var palette = new[] { "#273A78", "#6E354B", "#285D56", "#6C4D31" };
            var card = new Grid
            {
                Width = 360, Height = 317,
                Background = (Brush)new BrushConverter().ConvertFromString(palette[Math.Abs(album.Album.GetHashCode()) % palette.Length])!,
            };
            card.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(album.Album) ? "?" : album.Album[..1].ToUpperInvariant(), FontSize = 120,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            return Render(card, 360, 317);
        }

        /// <summary>The spine: artist and album running top to bottom, like a real CD.</summary>
        private static ImageSource Spine(CoverFlowAlbum album)
        {
            const int w = 44, h = 500;
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
            text.Children.Add(new TextBlock { Text = album.Artist.ToUpperInvariant(), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xEA)) });
            text.Children.Add(new TextBlock { Text = "   " + album.Album, FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC2, 0xB8)), TextTrimming = TextTrimming.CharacterEllipsis });
            var rotated = new Border { Width = h, Height = w, Child = text, ClipToBounds = true };
            var host = new Grid { Width = w, Height = h, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24)) };
            rotated.LayoutTransform = new RotateTransform(90);
            host.Children.Add(rotated);
            return Render(host, w, h);
        }

        /// <summary>The back insert: album, artist, then numbered tracks. Also returns each row's top (pixels) for click mapping.</summary>
        private static (ImageSource, double[]) TrackList(CoverFlowAlbum album)
        {
            // Rows follow Album.Tracks' own order (already sorted by track
            // number), so a clicked row's index is the track to play.
            var tracks = album.Tracks;
            var panel = new Canvas { Width = BackWidth, Height = BackHeight, Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xEE, 0xE4)) };
            panel.Children.Add(Positioned(new TextBlock { Text = album.Album, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ink, Width = BackWidth - 48, TextTrimming = TextTrimming.CharacterEllipsis }, 24, 16));
            panel.Children.Add(Positioned(new TextBlock { Text = album.Artist, FontSize = 15, Foreground = SoftInk, Width = BackWidth - 48, TextTrimming = TextTrimming.CharacterEllipsis }, 24, 50));
            var rowHeight = Math.Min(BackRowMax, (BackHeight - BackHeaderHeight - 12.0) / Math.Max(1, tracks.Count));
            var fontSize = Math.Clamp(rowHeight * 0.55, 9, 15);
            var tops = new double[tracks.Count + 1];
            for (var i = 0; i < tracks.Count; i++)
            {
                var top = BackHeaderHeight + i * rowHeight;
                tops[i] = top;
                if (i % 2 == 1)
                    panel.Children.Add(Positioned(new Border { Width = BackWidth - 32, Height = rowHeight, Background = new SolidColorBrush(Color.FromArgb(0x18, 0x6B, 0x4A, 0x2E)) }, 16, top));
                var number = tracks[i].TrackNumber > 0 ? tracks[i].TrackNumber : i + 1;
                panel.Children.Add(Positioned(new TextBlock { Text = number.ToString("00", CultureInfo.InvariantCulture), FontSize = fontSize, Foreground = SoftInk }, 26, top + (rowHeight - fontSize * 1.33) / 2));
                panel.Children.Add(Positioned(new TextBlock { Text = tracks[i].Title, FontSize = fontSize, Foreground = Ink, Width = BackWidth - 150, TextTrimming = TextTrimming.CharacterEllipsis }, 64, top + (rowHeight - fontSize * 1.33) / 2));
                panel.Children.Add(Positioned(new TextBlock { Text = tracks[i].DurationText, FontSize = fontSize, Foreground = SoftInk }, BackWidth - 76, top + (rowHeight - fontSize * 1.33) / 2));
            }
            tops[tracks.Count] = BackHeaderHeight + tracks.Count * rowHeight;
            return (Render(panel, BackWidth, BackHeight), tops);
        }

        private static UIElement Positioned(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            return element;
        }

        private static BitmapSource Render(FrameworkElement element, int w, int h)
        {
            element.Measure(new Size(w, h));
            element.Arrange(new Rect(0, 0, w, h));
            element.UpdateLayout();
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(element);
            bmp.Freeze();
            return bmp;
        }
    }
}
