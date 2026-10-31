using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using LumaDock.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>();
        var failed = 0;
        var snapshot = DesktopFontService.Read();
        var wpf = new DesktopFontInfo(SystemFonts.IconFontFamily.Source, SystemFonts.IconFontSize,
            SystemFonts.IconFontWeight.ToOpenTypeWeight(), SystemFonts.IconFontStyle == FontStyles.Italic,
            SystemFonts.IconFontTextDecorations.Any(decoration => decoration.Location == TextDecorationLocation.Underline),
            SystemFonts.IconFontTextDecorations.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough));

        void Test(string name, Action check)
        {
            try { check(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Test("Live desktop label font is a usable WPF font description", () =>
        {
            Require(!string.IsNullOrWhiteSpace(snapshot.FamilyName), "No native family");
            Require(double.IsFinite(snapshot.SizeDip) && snapshot.SizeDip > 0, "Invalid native font size");
            Require(snapshot.Weight is >= 1 and <= 999, "Invalid OpenType weight");
        });
        Test("Live desktop label font agrees with independent WPF SystemFonts snapshot", () =>
        {
            Require(string.Equals(snapshot.FamilyName, wpf.FamilyName, StringComparison.OrdinalIgnoreCase), "Font family differs from WPF");
            Require(Math.Abs(snapshot.SizeDip - wpf.SizeDip) < .01, "DIP size differs from WPF");
            Require(snapshot.Weight == wpf.Weight && snapshot.Italic == wpf.Italic
                && snapshot.Underline == wpf.Underline && snapshot.Strikeout == wpf.Strikeout, "Style differs from WPF");
        });
        Test("Stable explicit refresh retains font without raising Changed", () =>
        {
            using var service = new DesktopFontService();
            var events = 0; service.Changed += (_, _) => events++;
            var before = service.Current;
            Require(!service.Refresh(), "Unchanged native font reported as changed");
            Require(before == service.Current && events == 0, "Unchanged font generated UI work");
        });
        Test("Disposal is idempotent and prevents further refresh notifications", () =>
        {
            var service = new DesktopFontService();
            var events = 0; service.Changed += (_, _) => events++;
            var before = service.Current;
            service.Dispose(); service.Dispose();
            Require(!service.Refresh() && service.Current == before && events == 0, "Disposed service refreshed");
        });
        Test("Disposed service is not retained by static system events", () =>
        {
            var weak = CreateDisposedService();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(!weak.IsAlive, "Disposed service is still rooted");
        });

        var report = new { schemaVersion = 1, measuredAtUtc = DateTimeOffset.UtcNow,
            platform = Environment.OSVersion.VersionString, passed = results.Count - failed, failed,
            systemSettingsChanged = false, windowsOpened = 0, nativeFont = snapshot, independentWpfFont = wpf, tests = results,
            limitations = "Read-only integration checks use the current real Windows font. They do not change system settings, synthesize preference broadcasts or prove external-tool Apply/IME interaction." };
        var path = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/tests/font-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Desktop font checks: {results.Count - failed}/{results.Count} passed; report: {path}");
        return failed == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedService()
    {
        var service = new DesktopFontService(); service.Dispose(); return new WeakReference(service);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
