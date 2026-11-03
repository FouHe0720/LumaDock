using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.Desktop.Tests;

/// <summary>Real file and Shell fixtures in an owned directory. Never uses a personal desktop.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests/desktop-results.json");
        var results = new List<TestResult>();
        var observations = new Dictionary<string, object>();
        var clock = Stopwatch.StartNew();
        void Check(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); Console.WriteLine("PASS " + name); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.GetType().Name)); Console.WriteLine("FAIL " + name + " (" + error.GetType().Name + ")"); }
        }
        static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Documented condition not met."); }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("An invalid or colliding request was accepted.");
        }
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var root = Path.GetFullPath(Path.Combine("work", "desktop-file-tests", Guid.NewGuid().ToString("N")));
        var user = Directory.CreateDirectory(Path.Combine(root, "user desktop")).FullName;
        var common = Directory.CreateDirectory(Path.Combine(root, "common desktop")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var service = new DesktopFileService(user, common);
        var folder = Directory.CreateDirectory(Path.Combine(user, "资料 分类")).FullName;
        File.WriteAllText(Path.Combine(folder, "nested-only.txt"), "Owned nested fixture");
        Directory.CreateDirectory(Path.Combine(common, "Shared folder"));
        var original = Path.Combine(user, "report.txt"); File.WriteAllText(original, "User fixture original - must survive");
        var commonOriginal = Path.Combine(common, "report.txt"); File.WriteAllText(commonOriginal, "Common fixture original - must survive");
        File.WriteAllText(Path.Combine(user, "中文 文件.md"), "# Owned markdown fixture");
        File.WriteAllText(Path.Combine(user, "README"), "Owned extensionless fixture");
        var internet = Path.Combine(user, "Website.url"); File.WriteAllText(internet, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
        var hidden = Path.Combine(user, "hidden.txt"); File.WriteAllText(hidden, "Owned hidden fixture"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = Path.Combine(user, "system.txt"); File.WriteAllText(system, "Owned system fixture"); File.SetAttributes(system, FileAttributes.System);
        var ini = Path.Combine(user, "desktop.ini"); File.WriteAllText(ini, "; Owned shell metadata fixture");
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var shortcut = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Shortcut 示例", LaunchPath = notepad, ExecutablePath = notepad, Arguments = "--desktop-fixture", WorkingDirectory = user }, user);
        var originalHashes = new[] { original, commonOriginal, internet, shortcut, hidden, system, ini }.ToDictionary(x => x, Hash);

        Check("Configured roots use actual full user and common directory paths", () => Require(service.UserDesktopDirectory == user && service.CommonDesktopDirectory == common && service.Roots.Count == 2));
        Check("One snapshot includes folders, documents, extensionless files, URLs and real Shell shortcuts", () =>
        {
            var scan = service.Scan(); Require(scan.Errors.Count == 0 && scan.Items.Count == 8);
            Require(new[] { folder, original, commonOriginal, internet, shortcut, Path.Combine(user, "README"), Path.Combine(user, "中文 文件.md") }.All(p => scan.Items.Any(i => i.Path == p)));
            Require(scan.Items.Single(i => i.Path == folder).IsDirectory);
            observations["initialVisibleItems"] = scan.Items.Count;
        });
        Check("Hidden, system and desktop.ini entries stay absent while originals remain untouched", () =>
        {
            var scan = service.Scan(); Require(new[] { hidden, system, ini }.All(p => scan.Items.All(i => i.Path != p) && File.Exists(p) && Hash(p) == originalHashes[p]));
        });
        Check("A desktop snapshot scans only immediate children and never merges nested content", () => Require(service.Scan().Items.All(i => !i.Path.EndsWith("nested-only.txt", StringComparison.OrdinalIgnoreCase))));
        Check("Same-name user and public desktop documents remain two independent real paths", () =>
        {
            var names = service.Scan().Items.Where(i => i.Path == original || i.Path == commonOriginal).ToArray();
            Require(names.Length == 2 && names.Count(i => i.IsCommonDesktop) == 1 && names.Single(i => !i.IsCommonDesktop).Path == original);
        });
        Check("Repeated roots never create duplicate cards", () =>
        {
            var scan = new DesktopFileService(user, user).Scan(); Require(scan.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == scan.Items.Count);
        });
        Check("A missing desktop root is reported instead of silently dropping the error", () =>
        {
            var scan = new DesktopFileService(user, Path.Combine(root, "missing common")).Scan(); Require(scan.Errors.Count == 1 && scan.Items.Any(i => i.Path == original));
        });
        Check("An explicitly disabled common desktop remains optional", () => Require(new DesktopFileService(user, "").Scan().Errors.Count == 0));

        Check("Creating a folder writes a real directory to the configured user desktop", () =>
        {
            var created = service.CreateFolder("新建 资料"); Require(created.IsDirectory && !created.IsCommonDesktop && Directory.Exists(created.Path) && Path.GetDirectoryName(created.Path) == user && service.Scan().Items.Any(i => i.Path == created.Path));
        });
        Check("A folder name collision preserves its contents and creates a distinct new directory", () =>
        {
            var created = service.CreateFolder("资料 分类"); Require(created.Path != folder && Directory.Exists(created.Path) && File.ReadAllText(Path.Combine(folder, "nested-only.txt")) == "Owned nested fixture");
        });
        Check("A file-versus-folder name collision also gets a distinct destination", () =>
        {
            var created = service.CreateFolder("report.txt"); Require(created.Path != original && Directory.Exists(created.Path) && Hash(original) == originalHashes[original]);
        });
        Check("Default folder and text creation produce existing visible desktop items", () =>
        {
            var directory = service.CreateFolder(); var text = service.CreateTextFile();
            Require(Directory.Exists(directory.Path) && File.Exists(text.Path) && Path.GetExtension(text.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase) && new FileInfo(text.Path).Length == 0);
        });
        Check("A named text document appends txt and preserves Unicode names", () =>
        {
            var text = service.CreateTextFile("会议 记录"); Require(File.Exists(text.Path) && Path.GetFileName(text.Path) == "会议 记录.txt" && new FileInfo(text.Path).Length == 0);
        });
        Check("Creating a colliding text document never truncates an existing desktop file", () =>
        {
            var created = service.CreateTextFile("report.txt"); Require(created.Path != original && File.Exists(created.Path) && Hash(original) == originalHashes[original]);
        });
        Check("Concurrent document creation reserves unique destinations without overwriting", () =>
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.CreateTextFile("Concurrent text"))).ToArray(); Task.WaitAll(tasks);
            Require(tasks.Select(t => t.Result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tasks.Length && tasks.All(t => File.Exists(t.Result.Path)));
        });
        Check("Concurrent folder creation reserves unique directories", () =>
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.CreateFolder("Concurrent folder"))).ToArray(); Task.WaitAll(tasks);
            Require(tasks.Select(t => t.Result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tasks.Length && tasks.All(t => Directory.Exists(t.Result.Path)));
        });
        Check("Creation rejects traversal, rooted names, invalid characters and reserved device names", () =>
        {
            foreach (var name in new[] { "", " ", "..", ".", "../escape", "..\\escape", Path.Combine(outside, "escape"), "bad/name", "bad\\name", "bad:name", "bad?name", "CON", "NUL.txt", "COM1", "LPT9.log", "name.", "name " })
            { Reject(() => service.CreateFolder(name)); Reject(() => service.CreateTextFile(name)); }
            Require(!File.Exists(Path.Combine(outside, "escape")) && !Directory.Exists(Path.Combine(outside, "escape")));
        });

        Check("Renaming an owned text file preserves its contents and updates the snapshot", () =>
        {
            var item = service.CreateTextFile("Before rename"); File.WriteAllText(item.Path, "Rename fixture bytes"); var hash = Hash(item.Path);
            var renamed = service.Rename(item, "After rename.txt"); Require(!File.Exists(item.Path) && File.Exists(renamed.Path) && Hash(renamed.Path) == hash && service.Scan().Items.Any(i => i.Path == renamed.Path));
        });
        Check("Renaming a folder keeps nested content in place under the new name", () =>
        {
            var item = service.CreateFolder("Before folder rename"); File.WriteAllText(Path.Combine(item.Path, "retained.txt"), "Owned rename fixture");
            var renamed = service.Rename(item, "After folder rename"); Require(!Directory.Exists(item.Path) && Directory.Exists(renamed.Path) && File.ReadAllText(Path.Combine(renamed.Path, "retained.txt")) == "Owned rename fixture");
        });
        Check("A rename collision preserves both source and destination bytes", () =>
        {
            var source = service.CreateTextFile("Collision source"); File.WriteAllText(source.Path, "Keep source bytes"); var hash = Hash(source.Path);
            Reject(() => service.Rename(source, "report.txt")); Require(File.Exists(source.Path) && Hash(source.Path) == hash && Hash(original) == originalHashes[original]);
        });
        Check("Case-only renames preserve bytes and update the actual stored file name", () =>
        {
            var source = service.CreateTextFile("Case-only rename.txt"); File.WriteAllText(source.Path, "Case-sensitive display fixture"); var hash = Hash(source.Path);
            var renamed = service.Rename(source, "case-only rename.txt"); Require(Hash(renamed.Path) == hash && Directory.EnumerateFiles(user).Any(path => Path.GetFileName(path) == "case-only rename.txt"));
        });
        Check("A forged alternate-data-stream source cannot be renamed as a desktop file", () =>
        {
            var source = service.CreateTextFile("ADS source"); File.WriteAllText(source.Path, "Owned base stream"); var hash = Hash(source.Path);
            var ads = source.Path + ":private"; File.WriteAllText(ads, "Owned alternate stream");
            Reject(() => service.Rename(new DesktopItem(ads, "ADS source.txt:private", false, false), "stream-escaped.txt")); Require(Hash(source.Path) == hash && !File.Exists(Path.Combine(user, "stream-escaped.txt")));
        });
        Check("Rename validation prevents traversal and reserved names", () =>
        {
            var source = service.CreateTextFile("Invalid rename source");
            foreach (var name in new[] { "../escape", "..\\escape", "..", Path.Combine(outside, "escape"), "NUL.txt", "bad:name" }) Reject(() => service.Rename(source, name));
            Require(File.Exists(source.Path));
        });
        Check("A forged outside item cannot rename arbitrary files beyond desktop roots", () =>
        {
            var path = Path.Combine(outside, "outside.txt"); File.WriteAllText(path, "Keep outside bytes");
            Reject(() => service.Rename(new DesktopItem(path, "outside.txt", false, false), "stolen.txt")); Require(File.Exists(path) && File.ReadAllText(path) == "Keep outside bytes");
        });
        Check("A nested child cannot be forged into a top-level desktop item", () =>
        {
            var path = Path.Combine(folder, "nested-only.txt"); Reject(() => service.Rename(new DesktopItem(path, "nested-only.txt", false, false), "nested-renamed.txt")); Require(File.Exists(path));
        });

        Check("Copying real files is copy-only and preserves the original bytes", () =>
        {
            var source = Path.Combine(outside, "copy source.txt"); File.WriteAllText(source, "Keep source copy bytes"); var hash = Hash(source);
            var copied = service.CopyItems([source]); Require(copied.Items.Count == 1 && copied.Failures.Count == 0 && !copied.Cancelled && copied.Items[0].Path != source && Hash(copied.Items[0].Path) == hash && Hash(source) == hash);
        });
        Check("Copy collisions create distinct files without replacing the desktop original", () =>
        {
            var source = Path.Combine(outside, "report.txt"); File.WriteAllText(source, "Different copy bytes");
            var copied = service.CopyItems([source]); Require(copied.Items.Count == 1 && copied.Items[0].Path != original && Hash(original) == originalHashes[original] && Hash(copied.Items[0].Path) == Hash(source));
        });
        Check("Unsupported folder copy and missing files report individual failures", () =>
        {
            var copied = service.CopyItems([folder, Path.Combine(outside, "missing.txt")]); Require(copied.Items.Count == 0 && copied.Failures.Count == 2 && Directory.Exists(folder));
        });
        Check("A pre-cancelled copy request creates no destination", () =>
        {
            var source = Path.Combine(outside, "cancelled-copy.txt"); File.WriteAllText(source, "Cancellation fixture");
            var cancelled = service.CopyItems([source], new CancellationToken(true)); Require(cancelled.Cancelled && cancelled.Items.Count == 0 && !File.Exists(Path.Combine(user, "cancelled-copy.txt")) && File.Exists(source));
        });

        Check("Directory change notifications observe new real items without polling", () =>
        {
            using var watcher = service.CreateWatcher(); using var changed = new ManualResetEventSlim(); watcher.Changed += (_, _) => changed.Set();
            Require(watcher.Errors.Count == 0); File.WriteAllText(Path.Combine(user, "watch-created.txt"), "Owned watch fixture"); Require(changed.Wait(TimeSpan.FromSeconds(5)));
        });
        Check("The common desktop is watched as well as the user desktop", () =>
        {
            using var watcher = service.CreateWatcher(); using var changed = new ManualResetEventSlim(); watcher.Changed += (_, _) => changed.Set();
            File.WriteAllText(Path.Combine(common, "watch-common.txt"), "Owned common watch fixture"); Require(changed.Wait(TimeSpan.FromSeconds(5)));
        });
        Check("Disposal releases the watch and suppresses callbacks from subsequent changes", () =>
        {
            var watcher = service.CreateWatcher(); var count = 0; watcher.Changed += (_, _) => Interlocked.Increment(ref count); watcher.Dispose();
            File.WriteAllText(Path.Combine(user, "watch-after-dispose.txt"), "Owned disposed watch fixture"); Thread.Sleep(500); Require(count == 0); watcher.Dispose();
        });
        Check("Watcher setup errors are observable for an unavailable common directory", () =>
        {
            using var watcher = new DesktopFileService(user, Path.Combine(root, "missing common")).CreateWatcher(); Require(watcher.Errors.Count > 0);
        });
        Check("Original desktop fixture files remain byte-identical after all supported operations", () => Require(originalHashes.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value)));

        observations["actualPersonalDesktopReadOrModified"] = false;
        observations["fileAndDirectoryFixturesRetained"] = true;
        var passed = results.All(r => r.Passed); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, version = typeof(DesktopFileService).Assembly.GetName().Version?.ToString(3), durationMs = clock.Elapsed.TotalMilliseconds, checks = results, observations, systemSettingsChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Desktop files: {results.Count(r => r.Passed)}/{results.Count} checks passed"); return passed ? 0 : 1;
    }
    private sealed record TestResult(string Name, bool Passed, double DurationMs, string? Error);
}
