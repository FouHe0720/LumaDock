using System.Text.Json;
using LumaDock.Core;
using LumaDock.Storage;

// No test framework dependency: every case exercises a public business/storage contract.
var results = new List<TestResult>();
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work/tests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
Directory.CreateDirectory(root);
void Test(string name, Action action)
{
    try { action(); results.Add(new(name, true, null)); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { results.Add(new(name, false, ex.ToString())); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
StateStore Store(string name) => new(Path.Combine(root, name));
AppEntry Entry(string id, string name = "Editor", string category = "开发工具", bool hidden = false) => new() { Id = id, Name = name, Category = category, Hidden = hidden, LaunchPath = $"C:\\fixture\\{id}.lnk", ExecutablePath = $"C:\\fixture\\{id}.exe" };

Test("Stable IDs ignore Windows path case", () => Assert(Catalog.StableId("C:\\APPS\\Editor.exe") == Catalog.StableId("c:\\apps\\editor.exe")));
Test("Stable IDs distinguish different targets", () => Assert(Catalog.StableId("C:\\a.exe") != Catalog.StableId("C:\\b.exe")));
Test("Merge preserves organization and tombstones", () =>
{
    var state = new AppState { Apps = [Entry("a", "Old", "个人分类", true), Entry("b")], PinnedIds = ["b"] };
    var fresh = Entry("a", "Updated", "其他"); fresh.LaunchPath = "C:\\updated.lnk";
    fresh.Source = "StartMenu"; fresh.DesktopPaths = ["C:\\fixture\\Desktop\\updated.lnk"];
    Catalog.Merge(state, [fresh, Entry("c", "Firefox")]);
    Assert(state.Apps.Select(a => a.Id).SequenceEqual(["a", "b", "c"]), "User order changed");
    Assert(state.Apps[0].Hidden && state.Apps[0].Category == "个人分类", "User metadata lost");
    Assert(state.Apps[0].Name == "Updated" && state.Apps[0].LaunchPath == fresh.LaunchPath, "Launch metadata stale");
    Assert(state.Apps[0].Source == "StartMenu" && state.Apps[0].DesktopPaths.SequenceEqual(fresh.DesktopPaths), "Desktop shortcut association lost during canonical merge");
    Assert(state.Apps[2].Category == "浏览器" && state.PinnedIds.SequenceEqual(["b"]));
});
Test("Merge duplicate discovery results once", () => { var s = new AppState(); Catalog.Merge(s, [Entry("a"), Entry("A")]); Assert(s.Apps.Count == 1); });
Test("Search is case insensitive, token based and hides removed records", () =>
{
    var s = new AppState { Apps = [Entry("a", "Visual Studio Code"), Entry("b", "Visual Studio", hidden: true), Entry("c", "Firefox", "浏览器")] };
    Assert(Catalog.Search(s, "  VISUAL  开发工具 ").Select(a => a.Id).SequenceEqual(["a"]));
    Assert(Catalog.Search(s, "", "浏览器").Single().Id == "c");
    Assert(Catalog.Search(s, "missing").Count() == 0);
});
Test("Normalize removes orphan and hidden pins preserving order", () =>
{
    var s = new AppState { Apps = [Entry("a"), Entry("b"), Entry("h", hidden: true)], PinnedIds = ["b", "missing", "a", "B", "h"] };
    Catalog.Normalize(s); Assert(s.PinnedIds.SequenceEqual(["b", "a"]));
});
Test("Normalize constrains hostile settings", () =>
{
    var s = new AppState { Settings = new() { Opacity = double.NaN, IconSize = 900, Spacing = -3, CornerRadius = double.PositiveInfinity, AnimationStrength = 3, Columns = 100, Theme = "unknown", Position = "invalid" } };
    Catalog.Normalize(s);
    Assert(s.Settings.Opacity == .88 && s.Settings.IconSize == 80 && s.Settings.Spacing == 4 && s.Settings.CornerRadius == 24);
    Assert(s.Settings.AnimationStrength == 1 && s.Settings.Columns == 10 && s.Settings.Theme == "System" && s.Settings.Position == "Bottom");
});
Test("Normalize retains custom categories and removes duplicate apps", () =>
{
    var s = new AppState { Apps = [Entry("a", category: "我的分组"), Entry("A")], Categories = ["", "其他", "其他"] };
    Catalog.Normalize(s); Assert(s.Apps.Count == 1 && s.Categories.SequenceEqual(["其他", "我的分组"]));
});
Test("First run returns defaults without creating config", () => { var st = Store("first"); Assert(st.Load().Apps.Count == 0 && !File.Exists(st.ConfigPath)); });
Test("Persistence roundtrip retains launch arguments and ordering", () =>
{
    var st = Store("roundtrip"); var s = new AppState { Apps = [Entry("x", "中文应用")], PinnedIds = ["x"], Settings = new() { Theme = "Dark", IconSize = 64, AutoHide = true, MonitorDevice = "\\\\.\\DISPLAY2" } };
    s.Apps[0].Arguments = "--profile \"My profile\""; s.Apps[0].AppUserModelId = "Fixture!App"; st.Save(s);
    var loaded = new StateStore(st.DirectoryPath).Load();
    Assert(loaded.Apps[0].Name == "中文应用" && loaded.Apps[0].Arguments == s.Apps[0].Arguments && loaded.Apps[0].AppUserModelId == "Fixture!App");
    Assert(loaded.PinnedIds.SequenceEqual(["x"]) && loaded.Settings.Theme == "Dark" && loaded.Settings.MonitorDevice == s.Settings.MonitorDevice);
    Assert(!File.Exists(st.ConfigPath + ".tmp"));
});
Test("Atomic save creates last known backup", () =>
{
    var st = Store("backup"); st.Save(new() { Apps = [Entry("a")] }); st.Save(new() { Apps = [Entry("b")] });
    Assert(StateStore.Read(st.ConfigPath).Apps[0].Id == "b" && StateStore.Read(st.ConfigPath + ".bak").Apps[0].Id == "a");
});
Test("Corrupt config falls back to backup and preserves damaged bytes", () =>
{
    var st = Store("recovery"); st.Save(new() { Apps = [Entry("a")] }); st.Save(new() { Apps = [Entry("b")] });
    const string damaged = "{ malformed-json"; File.WriteAllText(st.ConfigPath, damaged);
    var recovered = st.Load();
    Assert(recovered.Apps.Single().Id == "a" && st.RecoveryMessage != null);
    Assert(File.ReadAllText(Directory.GetFiles(st.DirectoryPath, "*.corrupt-*").Single()) == damaged);
});
Test("Corrupt config without usable backup recovers defaults", () =>
{
    var st = Store("defaults-recovery"); Directory.CreateDirectory(st.DirectoryPath); File.WriteAllText(st.ConfigPath, "null");
    Assert(st.Load().Apps.Count == 0 && st.RecoveryMessage != null && Directory.GetFiles(st.DirectoryPath, "*.corrupt-*").Length == 1);
});
Test("Saving recovered config retains a usable backup", () =>
{
    var st = Store("recover-save"); st.Save(new() { Apps = [Entry("a")] }); st.Save(new() { Apps = [Entry("b")] });
    File.WriteAllText(st.ConfigPath, "broken"); var recovered = st.Load(); st.Save(recovered);
    Assert(StateStore.Read(st.ConfigPath + ".bak").Apps.Single().Id == "a");
    File.WriteAllText(st.ConfigPath, "broken again"); Assert(new StateStore(st.DirectoryPath).Load().Apps.Single().Id == "a");
});
Test("Future schema is rejected without overwriting original", () =>
{
    var st = Store("future"); Directory.CreateDirectory(st.DirectoryPath); const string future = "{\"SchemaVersion\":999,\"FutureData\":\"must survive\"}"; File.WriteAllText(st.ConfigPath, future);
    Throws<InvalidDataException>(() => st.Load()); Assert(File.ReadAllText(st.ConfigPath) == future && !File.Exists(st.ConfigPath + ".bak"));
});
Test("Import normalizes explicit null optional values", () =>
{
    var path = Path.Combine(root, "nullable.json");
    File.WriteAllText(path, """{"SchemaVersion":1,"Apps":[{"Id":"x","LaunchPath":"C:\\fixture\\editor.lnk","Name":null,"Arguments":null,"WorkingDirectory":null,"ExecutablePath":null,"AppUserModelId":null,"Category":null,"Source":null}],"Categories":null,"PinnedIds":null,"Settings":null}""");
    var state = StateStore.Read(path);
    Assert(Catalog.Search(state, "editor").Count() == 1);
    Assert(state.Apps[0].Arguments == "" && state.Apps[0].ExecutablePath == "" && state.Apps[0].Category == "其他");
    Assert(state.Settings != null && state.PinnedIds.Count == 0);
});
Test("Future schema cannot be saved over current config", () =>
{
    var st = Store("future-save"); st.Save(new() { Apps = [Entry("safe")] }); var before = File.ReadAllText(st.ConfigPath);
    Throws<InvalidDataException>(() => st.Save(new AppState { SchemaVersion = 999 }));
    Assert(File.ReadAllText(st.ConfigPath) == before && !File.Exists(st.ConfigPath + ".tmp"));
});
Test("Export and import preserve complete state", () =>
{
    var s = new AppState { Apps = [Entry("a", hidden: true)], Settings = new() { PowerSaver = true } }; var export = Path.Combine(root, "export.json");
    StateStore.Export(s, export); var read = StateStore.Read(export); Assert(read.Apps[0].Hidden && read.Settings.PowerSaver);
});
Test("Oversized import is rejected before JSON parsing", () =>
{
    var path = Path.Combine(root, "oversized.json"); using (var stream = File.Create(path)) stream.SetLength(16 * 1024 * 1024 + 1);
    Throws<IOException>(() => StateStore.Read(path));
});

Test("Older configurations follow the system icon font without losing pins", () =>
{
    var path = Path.Combine(root, "old-font-config.json");
    File.WriteAllText(path, """{"SchemaVersion":1,"Settings":{"Theme":"Dark"},"Apps":[{"Id":"editor","Name":"Editor","LaunchPath":"C:\\fixture\\editor.lnk"}],"PinnedIds":["editor"]}""");
    var read = StateStore.Read(path);
    Assert(read.Settings.FollowSystemIconFont && read.Settings.Theme == "Dark" && read.PinnedIds.SequenceEqual(new[] { "editor" }));
});
Test("Manual application typography survives a persistence roundtrip", () =>
{
    var st = Store("manual-font");
    st.Save(new AppState { Settings = new() { FollowSystemIconFont = false, NameFontSize = 28, NameFontBold = true, NameFontFamily = "Microsoft YaHei UI", NameFontItalic = true } });
    var read = st.Load();
    Assert(!read.Settings.FollowSystemIconFont && read.Settings.NameFontSize == 28 && read.Settings.NameFontBold);
    Assert(read.Settings.NameFontFamily == "Microsoft YaHei UI" && read.Settings.NameFontItalic);
});
Test("Invalid manual font sizes recover to a readable supported value", () =>
{
    var state = new AppState { Settings = new() { NameFontSize = double.NaN } };
    Catalog.Normalize(state); Assert(state.Settings.NameFontSize == 15);
    state.Settings.NameFontSize = -200; Catalog.Normalize(state); Assert(state.Settings.NameFontSize >= 10);
});

Test("Liquid material preferences roundtrip and invalid clarity recovers", () =>
{
    var st = Store("liquid-material"); var state = new AppState { Settings = new() { LiquidGlass = false, LiveGlass = false, GlassClarity = .8 } };
    st.Save(state); var read = st.Load();
    Assert(!read.Settings.LiquidGlass && !read.Settings.LiveGlass && read.Settings.GlassClarity == .8);
    read.Settings.GlassClarity = double.NaN; Catalog.Normalize(read); Assert(read.Settings.GlassClarity == .65);
});

Test("Material palette and flow survive export and reload", () =>
{
    var st = Store("personal-material");
    var state = new AppState { Settings = new() { GlassMotion = false, GlassFlowStrength = .72, Accent = "Iris" } };
    st.Save(state); var read = st.Load();
    Assert(!read.Settings.GlassMotion && read.Settings.GlassFlowStrength == .72 && read.Settings.Accent == "Iris");
    var exported = Path.Combine(root, "personal-material-export.json"); StateStore.Export(read, exported);
    Assert(StateStore.Read(exported).Settings.Accent == "Iris");
});
Test("Imported fonts remain family names and invalid flow falls back", () =>
{
    var path = Path.Combine(root, "invalid-personalization.json");
    File.WriteAllText(path, """{"SchemaVersion":1,"Settings":{"NameFontFamily":"https://example.invalid/font.ttf#Fake","Accent":"invalid","GlassFlowStrength":90}}""");
    var read = StateStore.Read(path);
    Assert(read.Settings.NameFontFamily == "" && read.Settings.Accent == "Lagoon" && read.Settings.GlassFlowStrength == 1);
    read.Settings.GlassFlowStrength = double.NaN; Catalog.Normalize(read); Assert(read.Settings.GlassFlowStrength == .45);
});

var reportIndex = Array.IndexOf(args, "--report");
var report = new { timestamp = DateTimeOffset.Now, os = Environment.OSVersion.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, passed = results.Count(r => r.Passed), failed = results.Count(r => !r.Passed), cases = results, fixtureDirectory = root };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (reportIndex >= 0 && reportIndex + 1 < args.Length) { var path = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); }
Console.WriteLine($"{report.passed}/{results.Count} passed. Test fixtures: {root}");
return report.failed == 0 ? 0 : 1;

record TestResult(string Name, bool Passed, string? Error);
