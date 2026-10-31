using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.App;

/// <summary>Local backdrop optics; idle Dock and inactive windows have no capture loop.</summary>
internal sealed class LiquidGlassSurface : FrameworkElement, IDisposable
{
    private readonly Window host;
    private readonly AppController controller;
    private readonly WallpaperService wallpaper = new();
    private readonly DispatcherTimer throttle;
    private readonly DispatcherTimer live;
    private CancellationTokenSource? pending;
    private BitmapSource? frame;
    private bool disposed, attached;
    private long generation;
    private static readonly object CacheGate = new();
    private sealed record WallpaperPixels(LensSource Source, int NativeWidth, int NativeHeight);
    private static readonly Dictionary<string, WallpaperPixels> Sources = new(StringComparer.OrdinalIgnoreCase);
    private bool loggedFailure;
    private sealed record SceneStamp(ulong Hash, int SourceWidth, int SourceHeight, int Width, int Height,
        double OffsetX, double OffsetY, double PlacementWidth, double PlacementHeight, uint Background, double Radius, double Clarity, bool Dark);
    private SceneStamp? lastScene;
    private readonly BackdropRefreshCadence cadence = new();
    /// <summary>Optional material radius in DIPs, for the Dock's independent silhouette.</summary>
    public double? CornerRadiusOverride { get; set; }
    public double LastRenderMilliseconds { get; private set; }
    public string SourceKind { get; private set; } = "System backdrop";

    public LiquidGlassSurface(Window host, AppController controller)
    {
        this.host = host; this.controller = controller;
        IsHitTestVisible = false;
        throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        throttle.Tick += RenderTick;
        live = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        live.Tick += LiveTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); else ReleaseFrame(); };
        host.Closed += HostClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (attached || disposed) return;
        attached = true;
        host.LocationChanged += GeometryChanged;
        host.SizeChanged += SizeChangedOnHost;
        host.Activated += Activated;
        host.Deactivated += ActivityChanged;
        host.MouseEnter += MouseActivity; host.MouseLeave += MouseActivity;
        wallpaper.Changed += WallpaperChanged;
        Refresh();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (attached)
        {
            host.LocationChanged -= GeometryChanged; host.SizeChanged -= SizeChangedOnHost;
            host.Activated -= Activated; wallpaper.Changed -= WallpaperChanged;
            host.Deactivated -= ActivityChanged;
            host.MouseEnter -= MouseActivity; host.MouseLeave -= MouseActivity;
            attached = false;
        }
        ReleaseFrame();
    }
    private void GeometryChanged(object? sender, EventArgs e) { cadence.Reset(); Refresh(); }
    private void SizeChangedOnHost(object sender, SizeChangedEventArgs e) { cadence.Reset(); Refresh(); }
    private void Activated(object? sender, EventArgs e) { cadence.Reset(); wallpaper.Refresh(); Refresh(); }
    private void ActivityChanged(object? sender, EventArgs e) => UpdateActivity();
    private void MouseActivity(object sender, MouseEventArgs e) { cadence.Reset(); UpdateActivity(); }
    private void LiveTick(object? sender, EventArgs e) { if (pending == null) Refresh(); }
    private void UpdateActivity()
    {
        var active = host is DockWindow ? host.IsMouseOver : host.IsActive;
        var enabled = IsVisible && active && SourceKind == "Captured backdrop" && controller.State.Settings.LiveGlass && controller.State.Settings.LiquidGlass && !controller.ReducedMotion && !SystemParameters.HighContrast;
        if (enabled)
        {
            var interval = TimeSpan.FromMilliseconds(cadence.DelayMilliseconds);
            if (live.Interval != interval) live.Interval = interval;
            if (!live.IsEnabled) live.Start();
        }
        else live.Stop();
    }
    private void WallpaperChanged(object? sender, EventArgs e) { cadence.Reset(); Refresh(); }
    private void HostClosed(object? sender, EventArgs e) => Dispose();

    public void Refresh()
    {
        if (disposed) return;
        // The sampled frame already contains the backdrop and its optical tint.
        // Dock opacity is applied by the plate overlay, never by fading this frame
        // over an opaque underlay (which would invert the transparency control).
        Opacity = 1;
        UpdateActivity();
        if (!IsVisible || ActualWidth < 10 || ActualHeight < 10 || controller.State.Settings.PowerSaver || SystemParameters.HighContrast || !controller.State.Settings.LiquidGlass)
        { ReleaseFrame(); return; }
        if (!throttle.IsEnabled) throttle.Start();
    }
    private void ReleaseFrame()
    {
        throttle.Stop(); live.Stop(); generation++;
        pending?.Cancel(); frame = null; lastScene = null; cadence.Reset(); InvalidateVisual();
    }
    private async void RenderTick(object? sender, EventArgs e)
    {
        throttle.Stop();
        if (disposed || !IsVisible || ActualWidth < 10 || ActualHeight < 10 || !controller.State.Settings.LiquidGlass || controller.State.Settings.PowerSaver || SystemParameters.HighContrast) return;
        var snapshot = wallpaper.Current;
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = PointToScreen(new Point());
        var physicalWidth = ActualWidth * dpi.DpiScaleX;
        var physicalHeight = ActualHeight * dpi.DpiScaleY;
        var monitor = snapshot.Monitors.OrderByDescending(m => Intersection(origin.X, origin.Y, physicalWidth, physicalHeight, m)).FirstOrDefault();
        var thisGeneration = ++generation;
        pending?.Cancel(); pending?.Dispose(); pending = new CancellationTokenSource();
        var token = pending.Token;
        var dark = Theme.IsDark;
        var clarity = controller.State.Settings.GlassClarity;
        var radius = (CornerRadiusOverride ?? controller.State.Settings.CornerRadius) * dpi.DpiScaleX;
        var scale = Math.Min(1, Math.Sqrt(360_000 / Math.Max(1, physicalWidth * physicalHeight)));
        var width = Math.Max(1, (int)Math.Ceiling(physicalWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(physicalHeight * scale));
        var viewport = new LensViewport(origin.X, origin.Y, physicalWidth, physicalHeight);
        var ownWindows = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).Select(w => new WindowInteropHelper(w).Handle).Where(h => h != IntPtr.Zero).ToArray();
        var previousScene = lastScene;
        try
        {
            var watch = Stopwatch.StartNew();
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var captured = BackdropSampler.Capture((int)Math.Floor(origin.X) - 32, (int)Math.Floor(origin.Y) - 32,
                    (int)Math.Ceiling(physicalWidth) + 64, (int)Math.Ceiling(physicalHeight) + 64, ownWindows);
                LensSource source; WallpaperPlacement placement; string kind;
                if (captured != null)
                {
                    source = new LensSource(captured.Width, captured.Height, captured.Pixels);
                    placement = new WallpaperPlacement(captured.Left, captured.Top, captured.Width, captured.Height, false, 0, 0, 0);
                    kind = "Captured backdrop";
                }
                else if (monitor is { ImageAvailable: true })
                {
                    var wallpaperPixels = LoadSource(monitor.ImagePath, snapshot.BackgroundColor);
                    source = wallpaperPixels.Source;
                    placement = Placement(snapshot, monitor, wallpaperPixels); kind = "Wallpaper";
                }
                else return (Pixels: (byte[]?)null, Kind: "System backdrop", Stamp: (SceneStamp?)null);
                var stamp = new SceneStamp(Fingerprint(source.Bgra), source.Width, source.Height, width, height,
                    viewport.X - placement.Left, viewport.Y - placement.Top, placement.Width, placement.Height,
                    (uint)(placement.R | placement.G << 8 | placement.B << 16), radius, clarity, dark);
                if (stamp == previousScene) return (Pixels: (byte[]?)null, Kind: kind, Stamp: (SceneStamp?)stamp);
                var pixels = LiquidLensRenderer.Render(source, placement, viewport, width, height, radius, dark, clarity, token);
                return (Pixels: (byte[]?)pixels, Kind: kind, Stamp: (SceneStamp?)stamp);
            }, token);
            if (disposed || token.IsCancellationRequested || thisGeneration != generation) return;
            cadence.Observe(bitmap.Pixels != null);
            LastRenderMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (bitmap.Pixels != null)
            {
                // Keep one GPU resource for a stable surface size. Changing the
                // bitmap object every frame needlessly rebuilds composition resources.
                var writable = frame as WriteableBitmap;
                var replace = writable == null || writable.PixelWidth != width || writable.PixelHeight != height;
                if (replace) writable = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                writable!.WritePixels(new Int32Rect(0, 0, width, height), bitmap.Pixels, width * 4, 0);
                frame = writable; SourceKind = bitmap.Kind;
                lastScene = bitmap.Stamp;
                if (replace) InvalidateVisual();
            }
            UpdateActivity();
            controller.RecordGlassFrame(host is DockWindow ? "dock" : "center", SourceKind, LastRenderMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!loggedFailure) { Log.Write("Wallpaper glass unavailable; using system backdrop", ex); loggedFailure = true; } }
        finally { if (thisGeneration == generation) { pending?.Dispose(); pending = null; } }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (frame != null) dc.DrawImage(frame, new Rect(RenderSize));
    }
    private static ulong Fingerprint(byte[] pixels)
    {
        // A sparse scene fingerprint avoids rerendering an unchanged background.
        // Full source capture is still bounded and only runs during active use.
        ulong hash = 14695981039346656037;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        for (var index = 0; index < words.Length; index += 13) hash = (hash ^ words[index]) * 1099511628211;
        return hash;
    }
    private static double Intersection(double x, double y, double w, double h, WallpaperMonitor m)
        => Math.Max(0, Math.Min(x + w, m.Left + m.Width) - Math.Max(x, m.Left)) * Math.Max(0, Math.Min(y + h, m.Top + m.Height) - Math.Max(y, m.Top));

    private static WallpaperPlacement Placement(DesktopWallpaperSnapshot snapshot, WallpaperMonitor monitor, WallpaperPixels source)
    {
        double left = monitor.Left, top = monitor.Top, width = monitor.Width, height = monitor.Height;
        if (snapshot.Position is WallpaperPosition.Span or WallpaperPosition.Tile)
        {
            left = snapshot.Monitors.Min(m => m.Left); top = snapshot.Monitors.Min(m => m.Top);
            width = snapshot.Monitors.Max(m => m.Left + m.Width) - left;
            height = snapshot.Monitors.Max(m => m.Top + m.Height) - top;
        }
        double imageW = source.NativeWidth, imageH = source.NativeHeight;
        switch (snapshot.Position)
        {
            case WallpaperPosition.Stretch: imageW = width; imageH = height; break;
            case WallpaperPosition.Fill: case WallpaperPosition.Span:
                var fill = Math.Max(width / imageW, height / imageH); imageW *= fill; imageH *= fill; break;
            case WallpaperPosition.Fit:
                var fit = Math.Min(width / imageW, height / imageH); imageW *= fit; imageH *= fit; break;
        }
        var tile = snapshot.Position == WallpaperPosition.Tile;
        return new WallpaperPlacement(tile ? left : left + (width - imageW) / 2, tile ? top : top + (height - imageH) / 2, imageW, imageH, tile,
            (byte)(snapshot.BackgroundColor & 255), (byte)((snapshot.BackgroundColor >> 8) & 255), (byte)((snapshot.BackgroundColor >> 16) & 255));
    }
    private static WallpaperPixels LoadSource(string path, uint background)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new WallpaperPixels(new LensSource(1, 1, [(byte)((background >> 16) & 255), (byte)((background >> 8) & 255), (byte)(background & 255), 255]), 1, 1);
        var file = new FileInfo(path);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Wallpaper image exceeds cache limit.");
        var key = path + "|" + file.LastWriteTimeUtc.Ticks;
        lock (CacheGate)
        {
            if (Sources.TryGetValue(key, out var cached)) return cached;
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var original = decoder.Frames[0];
            var pixelWidth = original.PixelWidth; var pixelHeight = original.PixelHeight;
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
            if (pixelWidth >= pixelHeight) image.DecodePixelWidth = Math.Min(2560, pixelWidth); else image.DecodePixelHeight = Math.Min(2560, pixelHeight);
            image.EndInit(); image.Freeze();
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); converted.Freeze();
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            var result = new WallpaperPixels(new LensSource(converted.PixelWidth, converted.PixelHeight, pixels), pixelWidth, pixelHeight);
            if (Sources.Count >= 2) Sources.Clear();
            Sources[key] = result; return result;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; OnUnloaded(this, new RoutedEventArgs());
        host.Closed -= HostClosed; wallpaper.Dispose();
        throttle.Tick -= RenderTick; pending?.Dispose(); pending = null;
        live.Tick -= LiveTick;
    }
}
