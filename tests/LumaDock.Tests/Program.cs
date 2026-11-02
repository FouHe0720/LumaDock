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
    Assert(state.Apps[2].Category == "" && state.PinnedIds.SequenceEqual(["b"]));
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
    Assert(s.Settings.Opacity == .70 && s.Settings.IconSize == 80 && s.Settings.Spacing == 4 && s.Settings.CornerRadius == 24);
    Assert(s.Settings.AnimationStrength == 1 && s.Settings.Columns == 10 && s.Settings.Theme == "System" && s.Settings.Position == "Bottom");
});
Test("Normalize retains custom categories and removes duplicate apps", () =>
{
    var s = new AppState { Apps = [Entry("a", category: "我的分组"), Entry("A")], Categories = ["", "其他", "其他"] };
    Catalog.Normalize(s); Assert(s.Apps.Count == 1 && s.Categories.SequenceEqual(["我的分组"]));
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
    var st = Store("future"); Directory.CreateDirectory(st.DirectoryPath); const string future = "{\"SchemaVersion\":3,\"FutureData\":\"must survive\"}"; File.WriteAllText(st.ConfigPath, future);
    Throws<InvalidDataException>(() => st.Load()); Assert(File.ReadAllText(st.ConfigPath) == future && !File.Exists(st.ConfigPath + ".bak"));
});
Test("Import normalizes explicit null optional values", () =>
{
    var path = Path.Combine(root, "nullable.json");
    File.WriteAllText(path, """{"SchemaVersion":1,"Apps":[{"Id":"x","LaunchPath":"C:\\fixture\\editor.lnk","Name":null,"Arguments":null,"WorkingDirectory":null,"ExecutablePath":null,"AppUserModelId":null,"Category":null,"Source":null}],"Categories":null,"PinnedIds":null,"Settings":null}""");
    var state = StateStore.Read(path);
    Assert(Catalog.Search(state, "editor").Count() == 1);
    Assert(state.Apps[0].Arguments == "" && state.Apps[0].ExecutablePath == "" && state.Apps[0].Category == "");
    Assert(state.Settings != null && state.PinnedIds.Count == 0);
});
Test("Future schema cannot be saved over current config", () =>
{
    var st = Store("future-save"); st.Save(new() { Apps = [Entry("safe")] }); var before = File.ReadAllText(st.ConfigPath);
    Throws<InvalidDataException>(() => st.Save(new AppState { SchemaVersion = 3 }));
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
    Assert(read.Settings.FollowSystemIconFont && read.Settings.Theme == "Dark" && read.Settings.DockShape == "Capsule" && read.PinnedIds.SequenceEqual(new[] { "editor" }));
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

Test("Every Dock silhouette and palette survives configuration export", () =>
{
    var st = Store("dock-personalization");
    foreach (var shape in new[] { "Capsule", "Pebble", "Rail" })
    foreach (var accent in new[] { "Lagoon", "Jade", "Amber", "Iris", "Rose", "Graphite" })
    {
        var state = new AppState { Settings = new() { DockShape = shape, Accent = accent } };
        st.Save(state); var read = st.Load();
        Assert(read.Settings.DockShape == shape && read.Settings.Accent == accent, "Dock personalization changed during save/reload.");
        var exported = Path.Combine(root, "dock-personalization-export.json"); StateStore.Export(read, exported);
        var imported = StateStore.Read(exported);
        Assert(imported.Settings.DockShape == shape && imported.Settings.Accent == accent, "Dock personalization changed during export/import.");
    }
});
Test("Unknown or null Dock silhouettes recover without losing organization", () =>
{
    var path = Path.Combine(root, "invalid-dock-personalization.json");
    foreach (var value in new[] { "\"unknown\"", "null" })
    {
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"Settings\":{\"DockShape\":" + value + ",\"Accent\":" + value + "},\"Apps\":[{\"Id\":\"editor\",\"Name\":\"Editor\",\"LaunchPath\":\"C:\\\\fixture\\\\editor.lnk\"}],\"PinnedIds\":[\"editor\"]}");
        var read = StateStore.Read(path);
        Assert(read.Settings.DockShape == "Capsule" && read.Settings.Accent == "Lagoon");
        Assert(read.PinnedIds.SequenceEqual(new[] { "editor" }), "Invalid appearance removed valid pins.");
    }
});

Test("Live clear and frosted preferences roundtrip and invalid concentration recovers", () =>
{
    var store = Store("live-glass-style"); var state = new AppState { Settings = new() { LiveGlassStyle = "Frosted", BackdropOpacity = .5 } };
    store.Save(state); var read = store.Load(); Assert(read.Settings.LiveGlassStyle == "Frosted" && read.Settings.BackdropOpacity == .5);
    read.Settings.LiveGlassStyle = "other"; read.Settings.BackdropOpacity = double.NaN; Catalog.Normalize(read);
    Assert(read.Settings.LiveGlassStyle == "Clear" && read.Settings.BackdropOpacity == .65);
});
Test("A local custom icon roundtrips and survives application discovery", () =>
{
    var state = new AppState { Apps = [Entry("icon")] };
    state.Apps[0].CustomIconPath = @"C:\fixture\brand.ico";
    var store = Store("custom-icon"); store.Save(state); var read = store.Load();
    Assert(read.Apps[0].CustomIconPath == @"C:\fixture\brand.ico");
    Catalog.Merge(read, [Entry("icon")]); Assert(read.Apps[0].CustomIconPath == @"C:\fixture\brand.ico");
});
Test("Custom icons reject network, device, URL and relative imports", () =>
{
    foreach (var path in new[] { @"\\server\share\icon.ico", @"\\?\C:\icon.ico", "//server/share/icon.png", "https://example.invalid/a.png", "file:///C:/a.ico", "relative.ico", @"C:\fixture\run.cmd" })
        Assert(Catalog.NormalizeIconPath(path) == "", "Accepted nonlocal or unsupported icon path");
    Assert(Catalog.NormalizeIconPath(null) == "");
});
Test("Browser shortcut attribution collapses one app and remaps pins without losing custom data", () =>
{
    var menu = Entry("menu", "Microsoft Edge", "浏览器"); menu.Source = "StartMenu";
    menu.ExecutablePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"; menu.Arguments = "--source-shortcut-location=start-menu";
    var desktop = Entry("desktop", "Microsoft Edge", "我的浏览器"); desktop.Source = "Desktop";
    desktop.ExecutablePath = menu.ExecutablePath.ToUpperInvariant(); desktop.Arguments = "--source-shortcut-location=desktop";
    desktop.CustomIconPath = @"C:\fixture\edge.ico"; desktop.DesktopPaths = [@"C:\fixture\Desktop\Edge.lnk"];
    var state = new AppState { Apps = [menu, desktop], PinnedIds = ["desktop", "menu"] };
    Catalog.Normalize(state);
    Assert(state.Apps.Count == 1 && state.Apps[0].Id == "menu" && state.PinnedIds.SequenceEqual(["menu"]));
    Assert(menu.Category == "我的浏览器" && menu.CategoryIsUserAssigned && menu.CustomIconPath == desktop.CustomIconPath);
    Assert(menu.DesktopPaths.SequenceEqual(desktop.DesktopPaths) && menu.Arguments == "--source-shortcut-location=start-menu", "Original launch command was modified");
});
Test("Application identities preserve profiles, argument case and distinct hosted app IDs", () =>
{
    var first = Entry("a"); first.LaunchPath = first.ExecutablePath = @"C:\fixture\msedge.exe"; first.Arguments = "--profile-directory=Default";
    var second = Entry("b"); second.LaunchPath = second.ExecutablePath = first.ExecutablePath; second.Arguments = "--profile-directory=Profile2";
    Assert(!AppIdentity.AreEquivalent(first, second), "Profiles on the same executable were merged");
    second.Arguments = "--profile-directory=default"; Assert(!AppIdentity.AreEquivalent(first, second), "Arguments were case-folded");
    first.Arguments = second.Arguments = ""; first.AppUserModelId = "host!First"; second.AppUserModelId = "host!Second";
    Assert(!AppIdentity.AreEquivalent(first, second), "Different hosted app IDs were merged");
    second.AppUserModelId = first.AppUserModelId; second.Arguments = "--private";
    Assert(!AppIdentity.AreEquivalent(first, second), "AUMID hid a distinct launch argument");
});
Test("Unknown executables and quoted source-location text retain their arguments", () =>
{
    var first = Entry("a"); var second = Entry("b"); second.ExecutablePath = first.ExecutablePath;
    first.Arguments = "--source-shortcut-location=start-menu"; second.Arguments = "--source-shortcut-location=desktop";
    Assert(!AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\msedge.exe";
    first.Arguments = "--label \"text --source-shortcut-location=start-menu suffix\"";
    second.Arguments = "--label \"text --source-shortcut-location=desktop suffix\"";
    Assert(!AppIdentity.AreEquivalent(first, second), "A switch embedded inside quoted text was stripped");
    first.Arguments = "--profile-directory=Default --source-shortcut-location=start-menu --new-window";
    second.Arguments = "--profile-directory=Default --source-shortcut-location=desktop --new-window";
    Assert(AppIdentity.AreEquivalent(first, second));
});
Test("Different working directories preserve distinct relative-file launch actions", () =>
{
    var first = Entry("a"); var second = Entry("b"); second.ExecutablePath = first.ExecutablePath;
    first.Arguments = second.Arguments = "local-project.json";
    first.WorkingDirectory = @"C:\fixture\project-one"; second.WorkingDirectory = @"C:\fixture\project-two";
    Assert(!AppIdentity.AreEquivalent(first, second));
    second.WorkingDirectory = first.WorkingDirectory.ToUpperInvariant(); Assert(AppIdentity.AreEquivalent(first, second));
});
Test("Internet shortcut aliases merge by exact URI while path query and protocol stay meaningful", () =>
{
    var first = Entry("url-a"); first.ExecutablePath = ""; first.LaunchUri = "https://EXAMPLE.invalid/Path?q=Case";
    var second = Entry("url-b"); second.ExecutablePath = ""; second.AppUserModelId = "https://example.invalid/Path?q=Case"; second.LaunchPath = "shell:AppsFolder\\" + second.AppUserModelId;
    Assert(AppIdentity.AreEquivalent(first, second));
    second.AppUserModelId = "https://example.invalid/path?q=Case"; Assert(!AppIdentity.AreEquivalent(first, second));
    second.AppUserModelId = "https://example.invalid/Path?q=case"; Assert(!AppIdentity.AreEquivalent(first, second));
    second.AppUserModelId = "http://example.invalid/Path?q=Case"; Assert(!AppIdentity.AreEquivalent(first, second));
    first.LaunchUri = "steam://rungameid/123"; second.AppUserModelId = "steam://rungameid/123";
    Assert(AppIdentity.AreEquivalent(first, second));
    Assert(AppIdentity.NormalizeLaunchUri(@"C:\fixture\program.exe") == "" && AppIdentity.NormalizeLaunchUri("shell:AppsFolder\\Fixture!App") == "");
});
Test("Direct Win32 executable identity outranks changed shortcut AUMIDs but shared hosts stay distinct", () =>
{
    var first = Entry("direct-a"); var second = Entry("direct-b"); second.ExecutablePath = first.ExecutablePath;
    first.AppUserModelId = "vendor.old"; second.AppUserModelId = "vendor.new";
    Assert(AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\rundll32.exe";
    Assert(!AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\program.exe";
    first.AppUserModelId = "package!First"; second.AppUserModelId = "package!Second";
    Assert(!AppIdentity.AreEquivalent(first, second));
});
Test("Only known complete Weixin and WPS source-attribution switches are ignored", () =>
{
    var first = Entry("source-a"); var second = Entry("source-b");
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\Weixin.exe"; second.Arguments = "--scene=desktop";
    Assert(AppIdentity.AreEquivalent(first, second));
    second.Arguments = "--scene=conversation"; Assert(!AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\unknown.exe"; second.Arguments = "--scene=desktop";
    Assert(!AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\ksolaunch.exe";
    first.Arguments = "/prometheus /fromksolaunch /from=startmenu"; second.Arguments = "/prometheus /fromksolaunch /from=desktop_shortcut";
    Assert(AppIdentity.AreEquivalent(first, second));
    second.Arguments = "/prometheus /fromksolaunch /from=other-mode"; Assert(!AppIdentity.AreEquivalent(first, second));
    first.ExecutablePath = second.ExecutablePath = @"C:\fixture\sldBgDwld.exe";
    first.Arguments = "/launch_from 3"; second.Arguments = "/launch_from 0";
    Assert(!AppIdentity.AreEquivalent(first, second));
});
Test("Same display names alone never collapse separate programs or uninstall actions", () =>
{
    var first = Entry("a", "Editor"); var second = Entry("b", "Editor");
    Assert(!AppIdentity.AreEquivalent(first, second));
    second.ExecutablePath = first.ExecutablePath; second.Arguments = "--uninstall";
    Assert(!AppIdentity.AreEquivalent(first, second));
});
Test("A late Shell record connects AUMID-only and file identities without changing first order", () =>
{
    var first = Entry("first", "Fixture");
    var shell = Entry("shell", "Fixture"); shell.LaunchPath = "shell:AppsFolder\\Fixture!App"; shell.ExecutablePath = ""; shell.AppUserModelId = "Fixture!App"; shell.Source = "AppsFolder";
    var bridge = Entry("bridge", "Fixture"); bridge.ExecutablePath = first.ExecutablePath; bridge.AppUserModelId = shell.AppUserModelId;
    var state = new AppState { Apps = [first, shell, bridge], PinnedIds = ["shell", "bridge"] };
    Catalog.Normalize(state);
    Assert(state.Apps.Count == 1 && state.Apps[0].Id == "first" && state.PinnedIds.SequenceEqual(["first"]));
});
Test("Discovery aliases retain existing hidden tombstones and manual grouping", () =>
{
    var old = Entry("old", "Fixture", "保留分组", true); old.CustomIconPath = @"C:\fixture\brand.ico";
    var found = Entry("found", "Fixture"); found.ExecutablePath = old.ExecutablePath; found.Source = "StartMenu";
    var state = new AppState { Apps = [old], PinnedIds = [] };
    Catalog.Merge(state, [found]);
    Assert(state.Apps.Count == 1 && state.Apps[0].Id == "old" && old.Hidden && old.Category == "保留分组" && old.CustomIconPath.EndsWith("brand.ico"));
    Assert(ReferenceEquals(Catalog.FindEquivalent(state, found), old));
});
Test("Legacy automatic classifications clear once and never return after discovery", () =>
{
    var game = Entry("game", "无畏契约", "其他"); var design = Entry("design", "Autodesk Fusion", "其他");
    var chat = Entry("chat", "飞书", "其他"); var manual = Entry("manual", "Firefox", "稍后处理");
    var state = new AppState { SchemaVersion = 1, Apps = [game, design, chat, manual], Categories = ["开发工具", "游戏", "其他", "稍后处理", "空栏目"] }; Catalog.Normalize(state);
    Assert(game.Category == "" && design.Category == "" && chat.Category == "");
    Assert(manual.Category == "稍后处理" && manual.CategoryIsUserAssigned && state.SchemaVersion == 2 && state.OrganizationVersion == 1);
    Assert(state.Categories.SequenceEqual(["稍后处理", "空栏目"]));
    Catalog.Merge(state, [Entry("game", "无畏契约"), Entry("new", "Microsoft Edge")]);
    Assert(game.Category == "" && state.Apps.Single(a => a.Id == "new").Category == "" && !state.Categories.Contains("浏览器"));
});
Test("Legacy category hints remain compatible without assigning catalog entries", () =>
{
    foreach (var (name, category) in new[] { ("Visual Studio Code", "开发工具"), ("GitHub Desktop", "开发工具"), ("Digital Clock", "其他"), ("Video Codec Sample", "影音娱乐"), ("Bridge Studio", "其他"), ("Microsoft Edge", "浏览器"), ("Microsoft Teams", "社交通讯"), ("便笺", "办公学习"), ("获取帮助", "系统工具"), ("单击以执行", "系统工具"), ("截图工具", "系统工具"), ("卸载 Firefox", "系统工具"), ("酷狗音乐", "影音娱乐"), ("无畏契约", "游戏"), ("Autodesk Fusion", "创作设计"), ("Component Services", "系统工具"), ("Task Scheduler", "系统工具"), ("Microsoft Clipchamp", "创作设计"), ("WSL", "开发工具") })
        Assert(Catalog.SuggestCategory(name) == category, "Incorrect category for " + name);
    var unknown = Entry("unknown", "自定义名称", "其他"); unknown.ExecutablePath = @"C:\fixture\winword.exe";
    Assert(Catalog.SuggestCategory(unknown) == "办公学习", "Recognized executable metadata was ignored");
});
Test("Manual category provenance survives persistence and reclassification", () =>
{
    var state = new AppState { Apps = [Entry("a", "Firefox", "浏览器")] }; Catalog.AssignCategory(state.Apps[0], "稍后处理");
    var store = Store("category-provenance"); store.Save(state); var read = store.Load();
    Assert(read.Apps[0].CategoryIsUserAssigned && read.Apps[0].Category == "稍后处理" && read.Apps[0].CategoryRuleVersion == Catalog.CategoryRuleVersion);
    Catalog.Merge(read, [Entry("a", "Firefox")]); Assert(read.Apps[0].Category == "稍后处理");
});
Test("A new catalog has no suggested columns and discovery does not classify apps", () =>
{
    var state = new AppState(); Catalog.Normalize(state);
    Assert(state.Categories.Count == 0);
    Catalog.Merge(state, [Entry("browser", "Microsoft Edge"), Entry("editor", "Visual Studio Code")]);
    Assert(state.Categories.Count == 0 && state.Apps.All(a => a.Category.Length == 0 && !a.CategoryIsUserAssigned));
});
Test("Old built-in columns disappear even when previously assigned manually", () =>
{
    var app = Entry("old", "Fixture", "游戏"); app.CategoryIsUserAssigned = true; app.CategoryRuleVersion = 1;
    var state = new AppState { SchemaVersion = 1, Categories = ["游戏", "其他", "应用库", "桌面集", "自己的栏目"], Apps = [app] };
    Catalog.Normalize(state);
    Assert(app.Category == "" && !app.CategoryIsUserAssigned && state.Categories.SequenceEqual(["自己的栏目"]));
    Assert(!Catalog.CreateCategory(state, "应用库") && !Catalog.CreateCategory(state, "桌面集"));
    Assert(Catalog.CreateCategory(state, "游戏")); Catalog.AssignCategory(app, "游戏"); Catalog.Normalize(state);
    Assert(app.Category == "游戏" && state.Categories.Contains("游戏"));
});
Test("Custom columns can be created renamed and removed without removing apps or pins", () =>
{
    var state = new AppState { Apps = [Entry("a"), Entry("b")], PinnedIds = ["b", "a"] }; Catalog.Normalize(state);
    Assert(Catalog.CreateCategory(state, "  常用  ") && Catalog.CreateCategory(state, "空栏目"));
    Assert(!Catalog.CreateCategory(state, "常用") && !Catalog.CreateCategory(state, "全部应用") && !Catalog.CreateCategory(state, new string('x', 41)));
    Catalog.AssignCategory(state.Apps[0], "常用");
    Assert(!Catalog.RenameCategory(state, "常用", "空栏目") && Catalog.RenameCategory(state, "常用", "工具箱"));
    Assert(state.Apps[0].Category == "工具箱" && Catalog.DeleteCategory(state, "工具箱"));
    Catalog.Normalize(state);
    Assert(state.Apps[0].Category == "" && !state.Apps[0].CategoryIsUserAssigned);
    Assert(state.Apps.Count == 2 && state.PinnedIds.SequenceEqual(["b", "a"]) && state.Categories.SequenceEqual(["空栏目"]));
});
Test("Deleted columns remain deleted after persistence and future discovery", () =>
{
    var state = new AppState { Apps = [Entry("a")] }; Catalog.Normalize(state); Catalog.CreateCategory(state, "工作");
    Catalog.AssignCategory(state.Apps[0], "工作"); Catalog.DeleteCategory(state, "工作");
    var store = Store("deleted-column"); store.Save(state); var read = store.Load(); Catalog.Merge(read, [Entry("a"), Entry("new")]);
    Assert(read.Categories.Count == 0 && read.Apps.All(a => a.Category == ""));
});
Test("Imported case aliases of one custom column normalize to one label", () =>
{
    var app = Entry("case", category: "WORK"); app.CategoryIsUserAssigned = true;
    var state = new AppState { OrganizationVersion = 1, Categories = ["Work", "work"], Apps = [app] };
    Catalog.Normalize(state);
    Assert(state.Categories.SequenceEqual(["Work"]) && app.Category == "Work");
});
Test("Unavailable apps stay recoverable with pin order and custom metadata but are excluded from search", () =>
{
    var state = new AppState { Apps = [Entry("a"), Entry("b")], PinnedIds = ["b", "a"] };
    state.Apps[0].IsAvailable = false; state.Apps[0].CustomIconPath = @"C:\fixture\brand.ico";
    Catalog.Normalize(state); Assert(state.PinnedIds.SequenceEqual(["b", "a"]) && Catalog.Search(state, "").Single().Id == "b");
    var found = Entry("new-a"); found.ExecutablePath = state.Apps[0].ExecutablePath; found.Source = "StartMenu";
    Catalog.Merge(state, [found]);
    Assert(state.Apps.Count == 2 && state.Apps[0].IsAvailable && state.Apps[0].Id == "a" && state.Apps[0].CustomIconPath.EndsWith("brand.ico"));
    Assert(state.PinnedIds.SequenceEqual(["b", "a"]) && Catalog.Search(state, "").Count() == 2);
});
Test("Dock transparency migration and reflection controls preserve explicit user settings", () =>
{
    var defaults = new AppState { SchemaVersion = 1, Settings = new() { Opacity = .88 } }; Catalog.Normalize(defaults);
    Assert(defaults.Settings.Opacity == .70 && defaults.Settings.IconReflection && defaults.Settings.ReflectionStrength == .24);
    defaults.Settings.Opacity = .88; Catalog.Normalize(defaults); Assert(defaults.Settings.Opacity == .88);
    var custom = new AppState { Settings = new() { Opacity = .23, ReflectionStrength = 900, IconReflection = false } }; Catalog.Normalize(custom);
    Assert(custom.Settings.Opacity == .23 && custom.Settings.ReflectionStrength == .85 && !custom.Settings.IconReflection);
    custom.Settings.Opacity = 0; custom.Settings.ReflectionStrength = double.NaN; Catalog.Normalize(custom);
    Assert(custom.Settings.Opacity == .02 && custom.Settings.ReflectionStrength == .24);
});
Test("Extended Dock clarity, reflection and vibrancy survive saving and reject non-finite values", () =>
{
    var store = Store("dock-color"); var state = new AppState { Settings = new() { Opacity = .04, ReflectionStrength = .72, DockIconVibrancy = .50 } };
    store.Save(state); var read = store.Load();
    Assert(read.Settings.Opacity == .04 && read.Settings.ReflectionStrength == .72 && read.Settings.DockIconVibrancy == .50);
    read.Settings.DockIconVibrancy = double.NaN; Catalog.Normalize(read); Assert(read.Settings.DockIconVibrancy == .28);
    read.Settings.DockIconVibrancy = 100; Catalog.Normalize(read); Assert(read.Settings.DockIconVibrancy == 1);
    read.Settings.DockIconVibrancy = -1; Catalog.Normalize(read); Assert(read.Settings.DockIconVibrancy == 0);
});
Test("Text colors normalize to bounded opaque RGB and reject brushes or markup", () =>
{
    Assert(Catalog.NormalizeTextColor(" #c0a1ff ") == "#C0A1FF");
    foreach (var value in new[] { "", "red", "#12345", "#00112233", "{DynamicResource TextBrush}", "#GGGGGG", "https://example.invalid/brush" })
        Assert(Catalog.NormalizeTextColor(value) == "");
});
Test("System Dock controls default on without altering persisted user appearance", () =>
{
    var old = JsonSerializer.Deserialize<AppState>("""{"SchemaVersion":2,"Settings":{"Theme":"Dark","IconSize":64,"AutoHide":true}}""")!;
    Catalog.Normalize(old);
    Assert(old.Settings.DockSystemEnabled && old.Settings.ShowRunningApps);
    Assert(old.Settings.DockStatusIconSize == 16 && old.Settings.DockStatusFontSize == 12 && old.Settings.DockStatusFontWeight == 600);
    Assert(old.Settings.Theme == "Dark" && old.Settings.IconSize == 64 && old.Settings.AutoHide);
});
Test("System Dock controls persist independently of name and application icon settings", () =>
{
    var st = Store("system-dock-style");
    var state = new AppState { Settings = new() { DockSystemEnabled = false, ShowRunningApps = false,
        DockStatusIconSize = 23, DockStatusFontSize = 17, DockStatusFontWeight = 800, IconSize = 55,
        FollowSystemIconFont = false, NameFontSize = 22, NameFontBold = false } };
    st.Save(state); var read = st.Load().Settings;
    Assert(!read.DockSystemEnabled && !read.ShowRunningApps && read.DockStatusIconSize == 23 && read.DockStatusFontSize == 17 && read.DockStatusFontWeight == 800);
    Assert(read.IconSize == 55 && read.NameFontSize == 22 && !read.NameFontBold && !read.FollowSystemIconFont);
});
Test("Imported system Dock dimensions reject non-finite and out-of-range values", () =>
{
    var state = new AppState { Settings = new() { DockStatusIconSize = double.NaN, DockStatusFontSize = double.PositiveInfinity, DockStatusFontWeight = int.MinValue } };
    Catalog.Normalize(state); Assert(state.Settings.DockStatusIconSize == 16 && state.Settings.DockStatusFontSize == 12 && state.Settings.DockStatusFontWeight == 400);
    state.Settings.DockStatusIconSize = 99; state.Settings.DockStatusFontSize = -10; state.Settings.DockStatusFontWeight = int.MaxValue;
    Catalog.Normalize(state); Assert(state.Settings.DockStatusIconSize == 24 && state.Settings.DockStatusFontSize == 10 && state.Settings.DockStatusFontWeight == 800);
    state.Settings.DockStatusIconSize = -1; state.Settings.DockStatusFontSize = 99;
    Catalog.Normalize(state); Assert(state.Settings.DockStatusIconSize == 12 && state.Settings.DockStatusFontSize == 18);
});
var reportIndex = Array.IndexOf(args, "--report");
var report = new { timestamp = DateTimeOffset.Now, os = Environment.OSVersion.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, passed = results.Count(r => r.Passed), failed = results.Count(r => !r.Passed), cases = results, fixtureDirectory = root };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (reportIndex >= 0 && reportIndex + 1 < args.Length) { var path = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); }
Console.WriteLine($"{report.passed}/{results.Count} passed. Test fixtures: {root}");
return report.failed == 0 ? 0 : 1;

record TestResult(string Name, bool Passed, string? Error);
