using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LumaDock.App;
using LumaDock.Core;
using LumaDock.Windows;

// Deterministic projection checks. Synthetic snapshot records never reach Win32,
// create windows, activate applications, or alter any user configuration.
var cases = new List<Case>();
var elapsed = Stopwatch.StartNew();
void Test(string name, Action action)
{
    try { action(); cases.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { cases.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Require(bool value, string message = "Projection contract mismatch") { if (!value) throw new InvalidOperationException(message); }
AppEntry App(string id, string executable, string aumid = "", string arguments = "") => new()
{ Id = id, Name = "Fixture " + id, ExecutablePath = executable, LaunchPath = executable, AppUserModelId = aumid, Arguments = arguments };
RunningWindow Window(int handle, string executable = @"C:\Fixture\Editor.exe", string aumid = "", uint pid = 42, string title = "Owned snapshot")
    => new(new IntPtr(handle), title, executable, aumid, pid);

Test("An empty snapshot creates no transient records or persisted entries", () =>
{
    var state = new AppState(); Require(RunningApps.Project(state, []).Count == 0);
    Require(state.Apps.Count == 0 && state.PinnedIds.Count == 0 && state.Categories.Count == 0);
});
Test("Null snapshot inputs fail before projection", () =>
{
    var failures = 0;
    try { RunningApps.Project(null!, []); } catch (ArgumentNullException) { failures++; }
    try { RunningApps.Project(new(), null!); } catch (ArgumentNullException) { failures++; }
    Require(failures == 2);
});
Test("Empty handles, missing process IDs, blank titles and duplicate handles are excluded", () =>
{
    var valid = Window(12);
    var groups = RunningApps.Project(new(), [Window(0), Window(10, pid: 0), Window(11, title: " "), valid, valid, valid with { ProcessId = 900 }]);
    Require(groups.Count == 1 && groups[0].Windows.Count == 1 && groups[0].Windows[0] == valid);
});
Test("Ordinary executable windows share one group while first-seen ordering remains stable", () =>
{
    var windows = new[] { Window(1), Window(2, @"C:\Fixture\Other.exe", pid: 50), Window(3, @"c:\fixture\EDITOR.exe", pid: 51) };
    var groups = RunningApps.Project(new(), windows);
    Require(groups.Count == 2 && groups[0].Windows.Select(w => w.Handle).SequenceEqual([new IntPtr(1), new IntPtr(3)]));
    Require(groups[1].Windows.Single().Handle == new IntPtr(2));
});
Test("A pinned available application suppresses its transient duplicate without changing pin order", () =>
{
    var pinned = App("editor", @"C:\Fixture\Editor.exe");
    var state = new AppState { Apps = [pinned], PinnedIds = ["EDITOR", "reserved"] };
    var groups = RunningApps.Project(state, [Window(1), Window(2), Window(3, @"C:\Fixture\Other.exe")]);
    Require(groups.Count == 1 && groups[0].App.ExecutablePath.EndsWith("Other.exe"));
    Require(state.PinnedIds.SequenceEqual(["EDITOR", "reserved"]) && state.Apps.Count == 1);
});
Test("Unavailable or hidden catalog pins do not swallow a real running task", () =>
{
    var pin = App("editor", @"C:\Fixture\Editor.exe");
    var state = new AppState { Apps = [pin], PinnedIds = [pin.Id] };
    pin.IsAvailable = false; Require(RunningApps.Project(state, [Window(1)]).Count == 1);
    pin.IsAvailable = true; pin.Hidden = true; Require(RunningApps.Project(state, [Window(1)]).Count == 1);
    Require(pin.Hidden && state.PinnedIds.SequenceEqual([pin.Id]));
});
Test("Different explicit AUMIDs never collapse through a common executable or pin", () =>
{
    var pin = App("first", @"C:\Fixture\Shared.exe", "Fixture.Package!First");
    var state = new AppState { Apps = [pin], PinnedIds = [pin.Id] };
    var second = Window(2, pin.ExecutablePath, "Fixture.Package!Second");
    var third = Window(3, pin.ExecutablePath, "Fixture.Package!Third");
    var groups = RunningApps.Project(state, [Window(1, pin.ExecutablePath, pin.AppUserModelId), second, third]);
    Require(groups.Count == 2 && groups.All(group => group.App.Id != pin.Id));
    Require(groups.Select(group => group.App.AppUserModelId).SequenceEqual([second.AppUserModelId, third.AppUserModelId]));
});
Test("AUMID grouping is case insensitive and survives executable path changes", () =>
{
    var groups = RunningApps.Project(new(), [Window(1, @"C:\Fixture\First.exe", "Fixture!App"), Window(2, @"C:\Fixture\Other.exe", "fixture!app", 48)]);
    Require(groups.Count == 1 && groups[0].Windows.Count == 2 && groups[0].IdentityKey == "aumid:FIXTURE!APP");
});
Test("A known exact AUMID is preferred over unrelated executable catalog aliases", () =>
{
    var pathAlias = App("path-alias", @"C:\Fixture\Editor.exe");
    var exact = App("exact", @"C:\Fixture\Migrated.exe", "Fixture!Editor");
    var state = new AppState { Apps = [pathAlias, exact] };
    var group = RunningApps.Project(state, [Window(1, pathAlias.ExecutablePath, exact.AppUserModelId)]).Single();
    Require(ReferenceEquals(group.App, exact) && !group.IsEphemeral);
});
Test("Shared runtime hosts group only within a PID and cannot offer a reconstructed pin", () =>
{
    foreach (var host in new[] { "powershell.exe", "pwsh.exe", "cmd.exe", "dotnet.exe", "javaw.exe", "python.exe", "pythonw.exe", "rundll32.exe", "electron.exe" })
    {
        var executable = @"C:\Fixture\" + host;
        var groups = RunningApps.Project(new(), [Window(1, executable, pid: 10), Window(2, executable, pid: 10), Window(3, executable, pid: 11)]);
        Require(groups.Count == 2 && groups[0].Windows.Count == 2 && groups[1].Windows.Single().ProcessId == 11, host);
        Require(groups.All(group => group.IsEphemeral && !RunningApps.CanPin(group.App)), "Hosted task unexpectedly became a launchable pin: " + host);
    }
});
Test("An unknown browser profile is not suppressed or relabeled as a parameterized pinned profile", () =>
{
    var pin = App("profile-work", @"C:\Fixture\msedge.exe", arguments: "--profile-directory=Work");
    var state = new AppState { Apps = [pin], PinnedIds = [pin.Id] };
    var group = RunningApps.Project(state, [Window(1, pin.ExecutablePath)]).Single();
    Require(group.IsEphemeral && !ReferenceEquals(group.App, pin) && group.App.Arguments.Length == 0);
    Require(pin.Arguments == "--profile-directory=Work" && state.PinnedIds.SequenceEqual([pin.Id]));
});
Test("Known generic launch mode wins over an earlier argument variant without rewriting either", () =>
{
    var variant = App("safe", @"C:\Fixture\Editor.exe", arguments: "--safe-mode");
    variant.WorkingDirectory = @"C:\Fixture\Safe";
    var generic = App("default", variant.ExecutablePath);
    var state = new AppState { Apps = [variant, generic] };
    var group = RunningApps.Project(state, [Window(1)]).Single();
    Require(ReferenceEquals(group.App, generic) && state.Apps.Count == 2);
    Require(variant.Arguments == "--safe-mode" && variant.WorkingDirectory == @"C:\Fixture\Safe" && generic.Arguments.Length == 0);
});
Test("Unidentified windows stay separate and switch-only instead of fabricating executable paths", () =>
{
    var group = RunningApps.Project(new(), [Window(1, "", pid: 10), Window(2, "", pid: 10)]);
    Require(group.Count == 2 && group.All(item => item.IsEphemeral && item.App.LaunchPath.Length == 0 && !RunningApps.CanPin(item.App)));
    Require(group[0].App.Name == "Owned snapshot" && group[0].Windows.Single().Handle == new IntPtr(1));
});
Test("Only a real packaged identity supplies a namespace launch for an inaccessible executable", () =>
{
    var packaged = RunningApps.Project(new(), [Window(1, "", "Fixture.Package!App")]).Single();
    Require(packaged.App.LaunchPath == "shell:AppsFolder\\Fixture.Package!App" && packaged.App.ExecutablePath.Length == 0);
    Require(RunningApps.CanPin(packaged.App));
    var ordinary = RunningApps.Project(new(), [Window(1, "", "Fixture.Desktop.App")]).Single();
    Require(ordinary.App.LaunchPath.Length == 0 && !RunningApps.CanPin(ordinary.App));
});
Test("Relative, network and non-executable snapshot paths remain switch-only", () =>
{
    foreach (var path in new[] { "editor.exe", @"\\server\share\editor.exe", @"C:\Fixture\content.txt", "https://example.invalid/editor.exe", @"C:\Fixture\bad" + '\0' + ".exe" })
    {
        var group = RunningApps.Project(new(), [Window(1, path)]).Single();
        Require(group.App.LaunchPath.Length == 0 && group.App.ExecutablePath.Length == 0 && !RunningApps.CanPin(group.App), path);
    }
});
Test("Temporary ordinary applications offer a local executable pin without persisting on discovery", () =>
{
    var state = new AppState(); var window = Window(1);
    var first = RunningApps.Project(state, [window]).Single();
    var next = RunningApps.Project(state, [window with { Title = "Changed caption" }]).Single();
    Require(first.IsEphemeral && first.App.Source == "Running" && first.App.Name == "Editor" && RunningApps.CanPin(first.App));
    Require(first.App.LaunchPath == window.ExecutablePath && first.App.Id == next.App.Id && state.Apps.Count == 0);
});
Test("Projection leaves complete user state and source snapshot metadata unchanged", () =>
{
    var app = App("editor", @"C:\Fixture\Editor.exe", arguments: "--fixture"); app.Category = "My column";
    app.CustomIconPath = @"C:\Fixture\icon.png"; app.DesktopPaths = [@"C:\Fixture\Desktop\Editor.lnk"];
    var state = new AppState { Apps = [app], Categories = ["My column"], Settings = new() { IconSize = 62 }, PinnedIds = [] };
    var windows = new[] { Window(1), Window(2, "", pid: 99) };
    var stateBefore = JsonSerializer.Serialize(state); var windowsBefore = JsonSerializer.Serialize(windows.Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Title, w.ExecutablePath, w.AppUserModelId }));
    var groups = RunningApps.Project(state, windows);
    Require(groups.Count == 2 && JsonSerializer.Serialize(state) == stateBefore);
    Require(JsonSerializer.Serialize(windows.Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Title, w.ExecutablePath, w.AppUserModelId })) == windowsBefore);
    Require(groups[0].Windows is System.Collections.ObjectModel.ReadOnlyCollection<RunningWindow>);
});
var controllerChecks = args.Contains("--with-controller", StringComparer.Ordinal);
if (controllerChecks) ControllerPinChecks.Run(Test, Require);
var index = Array.IndexOf(args, "--report");
var reportPath = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/tests/running-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var report = new { schemaVersion = 1, recordedAtUtc = DateTimeOffset.UtcNow, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
    deterministicSnapshotsOnly = !controllerChecks, controllerPinChecks = controllerChecks,
    noNativeWindowActions = true, noSystemSettingsChanges = true,
    passed = cases.Count(c => c.Passed), failed = cases.Count(c => !c.Passed), tests = cases };
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Running projection: {report.passed}/{cases.Count} passed");
return report.failed == 0 ? 0 : 1;
record Case(string Name, bool Passed, string? Error);
