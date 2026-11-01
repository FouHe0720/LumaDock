using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.Windows.Tests;

/// <summary>Read-only Shell checks and shortcut roundtrips in a private fixture directory. No app activation.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<TestResult>();
        var observations = new Dictionary<string, object>();
        var total = Stopwatch.StartNew();
        List<AppEntry> apps = [];
        void Check(string name, Action action)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                action(); results.Add(new(name, true, clock.Elapsed.TotalMilliseconds, null)); Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                // The shareable report deliberately omits messages/stacks, which can contain personal paths.
                results.Add(new(name, false, clock.Elapsed.TotalMilliseconds, error.GetType().Name));
                Console.WriteLine($"FAIL {name} ({error.GetType().Name})");
            }
        }
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Integration check did not meet its documented condition."); }

        Check("Shell discovery returns real named launch records with unique stable identities", () =>
        {
            apps = AppDiscovery.Scan();
            Require(apps.Count > 0);
            Require(apps.All(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.LaunchPath)));
            Require(apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == apps.Count);
            Require(apps.All(a => a.LaunchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) || File.Exists(a.LaunchPath)));
            observations["applicationCount"] = apps.Count;
            observations["appsFolderIdentityCount"] = apps.Count(a => !string.IsNullOrEmpty(a.AppUserModelId));
            observations["fileShortcutCount"] = apps.Count(a => a.LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
        });

        Check("Desktop discovery accepts empty desktops and returns existing shortcut or program files", () =>
        {
            var desktop = AppDiscovery.DesktopEntries();
            var roots = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p).TrimEnd('\\') + "\\").ToList();
            Require(desktop.All(a => a.Source == "Desktop" && File.Exists(a.LaunchPath) && roots.Any(root => a.LaunchPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))));
            Require(desktop.All(a => new[] { ".lnk", ".url", ".exe" }.Contains(Path.GetExtension(a.LaunchPath), StringComparer.OrdinalIgnoreCase)));
            observations["desktopRecordCount"] = desktop.Count;
        });

        var transferFixture = Path.GetFullPath(Path.Combine("work", "dock-transfer-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(transferFixture);
        var transferExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var transferApp = new AppEntry { Name = "Transfer fixture", LaunchPath = transferExecutable, ExecutablePath = transferExecutable,
            Arguments = "--fixture \"profile with spaces\"", WorkingDirectory = transferFixture };
        string originalTransfer = "";
        Check("New desktop shortcut preserves executable arguments and working directory", () =>
        {
            originalTransfer = DesktopShortcutService.WriteShortcut(transferApp, transferFixture);
            var resolved = AppDiscovery.FromPath(originalTransfer);
            Require(resolved.ExecutablePath.Equals(transferExecutable, StringComparison.OrdinalIgnoreCase));
            Require(resolved.Arguments == transferApp.Arguments && resolved.WorkingDirectory.Equals(transferFixture, StringComparison.OrdinalIgnoreCase));
        });
        Check("Repeated exports reserve distinct names without overwriting an existing shortcut", () =>
        {
            Require(File.Exists(originalTransfer)); var before = SHA256.HashData(File.ReadAllBytes(originalTransfer));
            var second = DesktopShortcutService.WriteShortcut(transferApp, transferFixture);
            Require(!second.Equals(originalTransfer, StringComparison.OrdinalIgnoreCase));
            Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(originalTransfer))));
        });
        Check("Exporting a shortcut copies its exact bytes and leaves its original untouched", () =>
        {
            var before = SHA256.HashData(File.ReadAllBytes(originalTransfer));
            var copy = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Copied fixture", LaunchPath = originalTransfer }, transferFixture);
            Require(copy != originalTransfer && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(copy))));
            Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(originalTransfer))));
        });
        Check("Internet shortcut export keeps the original URL file and its extension", () =>
        {
            var original = Path.Combine(transferFixture, "original.url");
            File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.invalid/fixture\r\n");
            var bytes = File.ReadAllBytes(original);
            var copy = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "URL fixture", LaunchPath = original }, transferFixture);
            Require(Path.GetExtension(copy) == ".url" && bytes.SequenceEqual(File.ReadAllBytes(copy)) && bytes.SequenceEqual(File.ReadAllBytes(original)));
        });
        Check("Shortcut display names cannot escape the selected destination directory", () =>
        {
            var path = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "../../\\CON:\t?*", LaunchPath = transferExecutable }, transferFixture);
            Require(Path.GetDirectoryName(path) == transferFixture && File.Exists(path));
        });
        Check("An unavailable launch target fails without leaving an empty shortcut", () =>
        {
            var count = Directory.GetFiles(transferFixture).Length; var failed = false;
            try { DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Missing fixture", LaunchPath = Path.Combine(transferFixture, "missing.exe") }, transferFixture); }
            catch (FileNotFoundException) { failed = true; }
            Require(failed && count == Directory.GetFiles(transferFixture).Length);
        });
        var packagedExport = apps.FirstOrDefault(app => app.AppUserModelId.Contains('!'));
        observations["packagedShortcutExportAvailable"] = packagedExport != null;
        if (packagedExport != null)
            Check("Packaged application shortcut keeps a real Shell namespace identity on reimport", () =>
            {
                var exported = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Packaged fixture", AppUserModelId = packagedExport.AppUserModelId,
                    LaunchPath = "shell:AppsFolder\\" + packagedExport.AppUserModelId }, transferFixture);
                var resolved = AppDiscovery.FromPath(exported);
                Require(resolved.AppUserModelId == packagedExport.AppUserModelId && File.Exists(exported));
            });

        Check("Native Shell icon sample loads asynchronously and produces frozen real bitmaps", () =>
        {
            Require(apps.Count > 0);
            // Include both file-backed shortcuts and Shell namespace identities when available.
            var sample = apps.Where(a => a.LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Take(8)
                .Concat(apps.Where(a => !string.IsNullOrWhiteSpace(a.AppUserModelId)).Take(8))
                .Concat(apps.Take(8)).DistinctBy(a => a.Id).Take(24).ToList();
            using var icons = new IconService();
            var tasks = sample.Select(a => icons.GetAsync(a, 64)).ToArray();
            Require(Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(45)));
            var loaded = tasks.Select(task => task.Result).Where(image => image != null).ToList();
            Require(loaded.Count >= Math.Min(8, sample.Count));
            Require(loaded.All(image => image!.IsFrozen && image.PixelWidth > 0 && image.PixelHeight > 0));
            // Cached requests return the completed native image rather than invoking another extraction.
            Require(ReferenceEquals(tasks[0], icons.GetAsync(sample[0], 64)));
            observations["iconSampleCount"] = sample.Count;
            observations["loadedIconCount"] = loaded.Count;
            observations["missingIconCount"] = sample.Count - loaded.Count;
        });

        Check("Native monitor work areas fit their physical monitor bounds", () =>
        {
            var monitors = MonitorService.GetMonitors();
            Require(monitors.Count > 0 && monitors.Count(m => m.IsPrimary) == 1);
            Require(monitors.All(m => m.Width > 0 && m.Height > 0 && m.WorkWidth > 0 && m.WorkHeight > 0 &&
                m.WorkLeft >= m.Left && m.WorkTop >= m.Top && m.WorkLeft + m.WorkWidth <= m.Left + m.Width && m.WorkTop + m.WorkHeight <= m.Top + m.Height));
            observations["monitorCount"] = monitors.Count;
            observations["monitorGeometry"] = monitors.Select(m => new { m.Left, m.Top, m.Width, m.Height, m.WorkLeft, m.WorkTop, m.WorkWidth, m.WorkHeight, m.IsPrimary }).ToArray();
        });

        Check("Real window snapshots have unique handles and correlate identifiable application windows", () =>
        {
            using var tracker = new WindowTracker(); tracker.Start();
            var windows = tracker.Windows;
            // A clean interactive session may legitimately have no other visible windows.
            Require(windows.All(w => w.Handle != IntPtr.Zero && w.ProcessId != 0 && !string.IsNullOrWhiteSpace(w.Title)));
            Require(windows.Select(w => w.Handle).Distinct().Count() == windows.Count);
            var identifiable = windows.Where(w => !string.IsNullOrEmpty(w.AppUserModelId) || w.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            Require(identifiable.All(w => tracker.IsRunning(new AppEntry { ExecutablePath = w.ExecutablePath, AppUserModelId = w.AppUserModelId })));
            observations["visibleWindowCount"] = windows.Count;
            observations["identifiableWindowCount"] = identifiable.Count;
            observations["windowWithAumidCount"] = windows.Count(w => !string.IsNullOrEmpty(w.AppUserModelId));
            observations["fullscreenDetected"] = tracker.IsForegroundFullscreen;
            // Read-only diagnostic: never Activate, never start Calculator, never assert it is installed.
            var calculators = windows.Where(w => w.ExecutablePath.EndsWith("\\CalculatorApp.exe", StringComparison.OrdinalIgnoreCase) ||
                w.AppUserModelId.StartsWith("Microsoft.WindowsCalculator_", StringComparison.OrdinalIgnoreCase)).ToList();
            observations["calculatorAlreadyRunning"] = calculators.Count > 0;
            if (calculators.Count > 0)
            {
                Require(calculators.All(w => tracker.IsRunning(new AppEntry { ExecutablePath = w.ExecutablePath, AppUserModelId = w.AppUserModelId })));
                observations["calculatorIdentityMatched"] = true;
            }
        });

        var failed = results.Count(result => !result.Passed);
        var reportIndex = Array.IndexOf(args, "--report");
        var reportPath = reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] :
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/tests/windows-results.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            recordedAtUtc = DateTimeOffset.UtcNow,
            platform = "Windows x64 interactive session",
            readOnlySystemChecks = true,
            shortcutChecksUsePrivateFixtureDirectory = true,
            elapsedMs = total.Elapsed.TotalMilliseconds,
            passed = results.Count - failed,
            failed,
            observations,
            tests = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Windows integration: {results.Count - failed}/{results.Count} passed. Report contains aggregate counts only.");
        return failed == 0 ? 0 : 1;
    }

    private sealed record TestResult(string Name, bool Passed, double ElapsedMs, string? FailureCategory);
}
