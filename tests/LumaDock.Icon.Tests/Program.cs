using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumaDock.Core;
using LumaDock.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>(); var failed = 0; var clock = Stopwatch.StartNew();
        var skipped = new List<string>();
        var directory = Path.GetFullPath(Path.Combine("work", "icon-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        var png = Path.Combine(directory, "alpha.png"); WritePicture(png, 80, 40, 250, 30, 10, true);
        var icon = Path.Combine(directory, "multi.ico"); WriteMultiIcon(icon);
        var legacy = Path.Combine(directory, "legacy.ico"); WriteLegacyIcon(legacy);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var entry = new AppEntry { LaunchPath = executable, ExecutablePath = executable, CustomIconPath = png };
        using var service = new IconService();
        BitmapSource Read(AppEntry value, int size = 80) => service.GetAsync(value, size).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Image unavailable");
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failed++; results.Add(new { name, passed = false, error = exception.GetType().Name }); Console.WriteLine("FAIL " + name + " (" + exception.GetType().Name + ")"); }
        }
        Check("Custom PNG retains transparent, semitransparent and opaque pixels without inventing a background", () =>
        {
            var image = Read(entry); var pixels = Pixels(image);
            Require(image.IsFrozen && image.PixelWidth == 80 && image.PixelHeight == 40);
            Require(pixels.Where((_, index) => index % 4 == 3).Contains((byte)0));
            Require(pixels.Where((_, index) => index % 4 == 3).Contains((byte)128));
            Require(pixels.Where((_, index) => index % 4 == 3).Contains((byte)255));
        });
        Check("Custom image decoding is bounded and preserves a non-square aspect ratio", () =>
        {
            var image = Read(entry, 32); Require(image.PixelWidth == 32 && image.PixelHeight == 16);
        });
        Check("ICO selects a larger real frame before scaling instead of enlarging the first tiny frame", () =>
        {
            var image = Read(new AppEntry { CustomIconPath = icon }, 48); var pixels = Pixels(image); var middle = (image.PixelWidth * (image.PixelHeight / 2) + image.PixelWidth / 2) * 4;
            Require(image.IsFrozen && image.PixelWidth == 48 && pixels[middle + 1] > pixels[middle] + 100);
        });
        Check("Legacy ICO transparency masks survive both file decoding and Shell extraction", () =>
        {
            foreach (var value in new[] { new AppEntry { CustomIconPath = legacy }, new AppEntry { LaunchPath = legacy } })
            {
                var pixels = Pixels(Read(value, 32)); Require(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 0) && pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 255));
            }
        });
        Check("Default Shell lookup requests an icon, not a picture thumbnail", () =>
        {
            var thumbnail = Read(entry); var associatedIcon = Read(new AppEntry { LaunchPath = png });
            Require(!Pixels(thumbnail).SequenceEqual(Pixels(associatedIcon)));
        });
        Check("An existing shortcut keeps its own icon instead of a merged AppsFolder identity", () =>
        {
            var shortcutPath = Path.Combine(directory, "custom.lnk"); object? shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); shortcut = ((dynamic)shell!).CreateShortcut(shortcutPath);
                ((dynamic)shortcut).TargetPath = executable; ((dynamic)shortcut).IconLocation = icon + ",0"; ((dynamic)shortcut).Save();
                var direct = Read(new AppEntry { LaunchPath = shortcutPath });
                var merged = Read(new AppEntry { LaunchPath = shortcutPath, AppUserModelId = "invalid.fixture.identity" });
                Require(Pixels(direct).SequenceEqual(Pixels(merged)));
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        });
        Check("Concurrent equal requests share the same task and frozen image", () =>
        {
            var tasks = new Task<BitmapSource?>[24]; Parallel.For(0, tasks.Length, index => tasks[index] = service.GetAsync(entry));
            Task.WaitAll(tasks); Require(tasks.All(task => ReferenceEquals(task, tasks[0])) && tasks.All(task => ReferenceEquals(task.Result, tasks[0].Result)));
        });
        Check("Changing a custom file at the same path invalidates its cached image", () =>
        {
            var before = Read(entry); var previous = File.GetLastWriteTimeUtc(png);
            WritePicture(png, 80, 40, 15, 240, 20, true); File.SetLastWriteTimeUtc(png, previous.AddSeconds(2));
            var after = Read(entry); Require(!ReferenceEquals(before, after) && !Pixels(before).SequenceEqual(Pixels(after)));
        });
        Check("Missing custom files, corrupt images and missing shortcuts fall back to real application icons", () =>
        {
            var corrupt = Path.Combine(directory, "corrupt.png"); File.WriteAllText(corrupt, "not an image");
            foreach (var custom in new[] { Path.Combine(directory, "missing.png"), corrupt, "https://example.invalid/icon.png" })
                Require(Read(new AppEntry { LaunchPath = executable, CustomIconPath = custom }).IsFrozen);
            Require(Read(new AppEntry { LaunchPath = Path.Combine(directory, "missing.lnk"), ExecutablePath = executable }).IsFrozen);
        });
        Check("Failed extractions are retried rather than permanently cached", () =>
        {
            var missing = new AppEntry { LaunchPath = Path.Combine(directory, "not-created.exe") };
            var first = service.GetAsync(missing); Require(first.GetAwaiter().GetResult() == null); Thread.Sleep(30);
            var second = service.GetAsync(missing); Require(second.GetAwaiter().GetResult() == null && !ReferenceEquals(first, second));
        });
        Check("The cache stays bounded across many completed requests", () =>
        {
            for (var index = 0; index < 300; index++) Read(new AppEntry { CustomIconPath = png, AppUserModelId = "fixture:" + index });
            var cache = typeof(IconService).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Require((int)cache.GetType().GetProperty("Count")!.GetValue(cache)! <= 256);
        });
        Check("Real installed file and registered app icon samples remain frozen and sized", () =>
        {
            var apps = AppDiscovery.Scan(); var sample = apps.Where(app => app.LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(4).Concat(apps.Where(app => app.AppUserModelId.Length > 0).Take(4)).ToArray();
            Require(sample.Length > 0); foreach (var item in sample) { var image = Read(item, 96); Require(image.IsFrozen && image.PixelWidth > 0 && image.PixelHeight > 0); }
        });
        var desktop = AppDiscovery.DesktopEntries(); var referenceShortcut = desktop.FirstOrDefault(app => app.Name.Contains("无畏契约", StringComparison.OrdinalIgnoreCase) || app.Name.Contains("VALORANT", StringComparison.OrdinalIgnoreCase));
        if (referenceShortcut != null) Check("The selected existing game shortcut preserves its original artwork and alpha", () =>
        {
            var image = Read(referenceShortcut); Require(image.IsFrozen); var alpha = Pixels(image).Where((_, index) => index % 4 == 3); Require(alpha.Any(value => value == 0) && alpha.Any(value => value == 255));
        });
        else skipped.Add("The optional reference game shortcut is not installed on this computer.");
        Check("Disposal safely rejects further requests", () =>
        {
            var temporary = new IconService(); temporary.Dispose(); Require(temporary.GetAsync(entry).GetAwaiter().GetResult() == null);
        });
        var reportIndex = Array.IndexOf(args, "--report"); var reportPath = Path.GetFullPath(reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : "artifacts/tests/icon-results-0.8.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { recordedAtUtc = DateTimeOffset.UtcNow, passed = results.Count - failed, failed, skipped, elapsedMs = clock.Elapsed.TotalMilliseconds, syntheticFixturesAndReadOnlyShell = true, originalFilesChanged = false, externalAppsLaunched = 0, screenshotsIncluded = false, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{results.Count - failed}/{results.Count} icon checks passed."); return failed == 0 ? 0 : 1;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Icon contract failed"); }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }
    private static byte[] Picture(int width, int height, byte red, byte green, byte blue, bool alpha)
    {
        var pixels = new byte[width * height * 4]; for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { var offset = (y * width + x) * 4; pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = alpha ? x < width / 3 ? (byte)0 : x < width * 2 / 3 ? (byte)128 : (byte)255 : (byte)255; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4))); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static void WritePicture(string path, int width, int height, byte red, byte green, byte blue, bool alpha) => File.WriteAllBytes(path, Picture(width, height, red, green, blue, alpha));
    private static void WriteMultiIcon(string path)
    {
        var small = Picture(16, 16, 0, 0, 255, false); var large = Picture(64, 64, 0, 255, 0, true);
        using var output = new BinaryWriter(File.Create(path)); output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)2);
        void Entry(byte size, byte[] data, uint offset) { output.Write(size); output.Write(size); output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32); output.Write((uint)data.Length); output.Write(offset); }
        Entry(16, small, 38); Entry(64, large, (uint)(38 + small.Length)); output.Write(small); output.Write(large);
    }
    private static void WriteLegacyIcon(string path)
    {
        using var output = new BinaryWriter(File.Create(path)); output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)1); output.Write((byte)16); output.Write((byte)16); output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)24); output.Write((uint)(40 + 16 * 48 + 16 * 4)); output.Write((uint)22);
        output.Write((uint)40); output.Write(16); output.Write(32); output.Write((ushort)1); output.Write((ushort)24); output.Write((uint)0); output.Write((uint)768); for (var i = 0; i < 4; i++) output.Write(0);
        for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++) { output.Write((byte)30); output.Write((byte)100); output.Write((byte)220); }
        for (var y = 0; y < 16; y++) { output.Write((byte)0); output.Write((byte)255); output.Write((ushort)0); }
    }
}
