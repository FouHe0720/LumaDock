using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using LumaDock.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>(); int failed = 0;
        var timer = Stopwatch.StartNew();
        var snapshot = WallpaperService.Read();
        timer.Stop();
        var monitors = MonitorService.GetMonitors();
        var decoded = new List<object>();
        void Test(string name, Action check)
        {
            try { check(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Test("The live Shell wallpaper snapshot identifies attached physical displays", () =>
        {
            Require(snapshot.Available, snapshot.Failure ?? "Shell API unavailable");
            Require(snapshot.Monitors.Count > 0 && Enum.IsDefined(snapshot.Position), "Missing wallpaper geometry or position");
            foreach (var item in snapshot.Monitors)
                Require(monitors.Any(m => m.DeviceName == item.DeviceName && m.Left == item.Left && m.Top == item.Top && m.Width == item.Width && m.Height == item.Height), "Wallpaper display does not match monitor geometry");
        });
        Test("Reported wallpaper files are real readable images or explicitly have no source", () =>
        {
            foreach (var item in snapshot.Monitors)
            {
                if (!item.ImageAvailable) continue; // Solid, unavailable or restricted source is an explicit fallback.
                Require(File.Exists(item.ImagePath) && item.ImageBytes > 0 && item.LastWriteUtcTicks > 0, "Advertised source is not an existing image file");
                using var stream = File.OpenRead(item.ImagePath);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Require(decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0 && decoder.Frames[0].PixelHeight > 0, "Wallpaper does not decode");
                decoded.Add(new { width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight, bytes = item.ImageBytes });
            }
        });
        Test("An unchanged refresh does not invalidate a surface", () =>
        {
            using var service = new WallpaperService(); var count = 0;
            service.Changed += (_, _) => count++;
            Require(!service.Refresh() && count == 0, "Stable metadata triggered a redraw notification");
        });
        Test("An MTA caller obtains the same Shell metadata through the STA boundary", () =>
        {
            var background = Task.Run(WallpaperService.Read).GetAwaiter().GetResult();
            Require(background.Available == snapshot.Available && background.Position == snapshot.Position
                && background.BackgroundColor == snapshot.BackgroundColor && background.Monitors.SequenceEqual(snapshot.Monitors), "STA metadata differs");
        });
        Test("Disposal is idempotent and rejects further invalidations", () =>
        {
            var service = new WallpaperService(); int count = 0;
            service.Changed += (_, _) => count++;
            service.Dispose(); service.Dispose();
            Require(!service.Refresh() && count == 0, "Disposed service refreshed");
        });
        Test("Static system events do not retain a disposed wallpaper service", () =>
        {
            var reference = ReleasedService(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(!reference.IsAlive, "Static wallpaper subscription retained disposed instance");
        });
        var report = new
        {
            version = 1, measuredAtUtc = DateTimeOffset.UtcNow, platform = Environment.OSVersion.VersionString,
            passed = results.Count - failed, failed, firstMetadataReadMs = timer.Elapsed.TotalMilliseconds,
            shellAvailable = snapshot.Available, position = snapshot.Position.ToString(), monitorCount = snapshot.Monitors.Count,
            readableImageCount = snapshot.Monitors.Count(m => m.ImageAvailable), decodedImages = decoded,
            physicalMonitors = snapshot.Monitors.Select(m => new { m.Left, m.Top, m.Width, m.Height, m.ImageAvailable }),
            tests = results, screenCaptureUsed = false, systemSettingsChanged = false, imagePathsIncluded = false,
            interpretation = "Read-only IDesktopWallpaper metadata and source decoding. Does not capture application windows, desktop icons, animated wallpapers or prove renderer appearance/performance."
        };
        var path = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/tests/wallpaper-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wallpaper checks: {results.Count - failed}/{results.Count} passed; report: {path}");
        return failed == 0 ? 0 : 1;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference ReleasedService()
    { var service = new WallpaperService(); service.Dispose(); return new WeakReference(service); }
}
