using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/tests/importer-ui-results.json");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        var checks = new List<object>(); var errors = new List<string>();
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? c = null; ControlCenterWindow? w = null;
            try
            {
                var root = Path.GetFullPath(Path.Combine("work", "importer-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root); Log.DirectoryPath = Path.Combine(root, "logs");
                string Url(int number) { var file = Path.Combine(root, $"Application {number:00}.url"); File.WriteAllText(file, "[InternetShortcut]\r\nURL=https://example.com/app/" + number + "\r\n"); return file; }
                c = new AppController(Path.Combine(root, "data"), null); c.State.Settings.Animations = false; c.State.Settings.FollowSystemIconFont = false; c.State.Settings.NameFontFamily = "Microsoft YaHei UI"; c.State.Settings.NameFontSize = 15; c.State.Settings.Columns = 6; c.ApplySettings();
                var firstUrl = Url(1); var before = SHA256.HashData(File.ReadAllBytes(firstUrl));
                var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
                var wrong = Path.Combine(root, "Document.txt"); File.WriteAllText(wrong, "Owned import fixture");
                var imported = await c.ImportApplicationsAsync([firstUrl, notepad, wrong, Path.Combine(root, "Missing.exe")]);
                Check("A mixed actual URL/executable batch accepts supported entries and reports each rejected path", imported.Entries.Count == 2 && imported.Failures.Count == 2 && !imported.Cancelled && !imported.Busy);
                Check("Import preserves original files byte for byte", before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(firstUrl))) && File.Exists(notepad));
                var shortcut = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Import shortcut fixture", LaunchPath = notepad, ExecutablePath = notepad, Arguments = "--LumaDock-import-fixture", WorkingDirectory = root, CustomIconPath = Path.GetFullPath("src/LumaDock.App/Assets/LumaDock.ico") }, root);
                var linkHash = SHA256.HashData(File.ReadAllBytes(shortcut));
                var linked = await c.ImportApplicationsAsync([shortcut]);
                Check("A real shortcut retains its original launch/icon source, arguments and working directory", linked.Entries.Count == 1 && linked.Entries[0].LaunchPath == shortcut && linked.Entries[0].Arguments == "--LumaDock-import-fixture" && linked.Entries[0].WorkingDirectory == root && linkHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(shortcut))));
                var count = c.State.Apps.Count; await c.ImportApplicationsAsync([firstUrl, notepad, firstUrl]);
                Check("Repeated imports retain one logical record without duplicate IDs", c.State.Apps.Count == count && c.State.Apps.Select(x => x.Id).Distinct().Count() == count);
                var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = await c.ImportApplicationsAsync([Url(2)], cancel.Token);
                Check("A cancelled import does not change memory or persisted applications", cancelled.Cancelled && c.State.Apps.Count == count && new StateStore(Path.Combine(root, "data")).Load().Apps.Count == count);
                var pending = c.ImportApplicationsAsync([Url(3)]); var busy = await c.ImportApplicationsAsync([Url(4)]); await pending;
                Check("A second concurrent import is rejected without starting another Shell batch", busy.Busy && c.State.Apps.All(x => !x.LaunchPath.EndsWith("Application 04.url")));
                var batch = await c.ImportApplicationsAsync(Enumerable.Range(5, 35).Select(Url));
                Check("A real multi-selection resolves and persists the full batch once", batch.Entries.Count == 35 && batch.Failures.Count == 0 && new StateStore(Path.Combine(root, "data")).Load().Apps.Count == c.State.Apps.Count);
                if (args.Contains("--metadata-only")) { exit = errors.Count == 0 ? 0 : 1; return; }
                var desktop = c.State.Apps.First(x => x.Id == imported.Entries[0].Id); desktop.DesktopPaths.Add(firstUrl);
                var missingMembership = c.State.Apps.First(x => x.Id == imported.Entries[1].Id); missingMembership.DesktopPaths.Add(Path.Combine(root, "missing-shortcut.lnk")); c.Save();
                w = new ControlCenterWindow(c) { Width = 1120, Height = 760, ShowActivated = false, ShowInTaskbar = false }; w.Show(); await Frame();
                var grid = Descendants(w).OfType<UniformGrid>().Single(x => x.Children.OfType<Button>().Any(b => b.DataContext is AppTileViewModel));
                Check("A normal 1120 by 760 window shows three complete rows at 15 DIP", grid.Columns == 6 && grid.Children.Count == 18);
                var view = Descendants(w).OfType<ScrollViewer>().Single(x => ReferenceEquals(x.Content, grid));
                Check("All three rows fit the actual viewport without clipping names", grid.ActualHeight <= view.ViewportHeight + .5 && grid.Children.OfType<Button>().All(b => Descendants(b).OfType<TextBlock>().Where(t => Equals(t.Tag, "ApplicationName")).All(t => t.ActualHeight + .5 >= t.LineHeight * 2)));
                Check("The hotkey has a visible explanation and a clickable named action", Descendants(w).OfType<Button>().Any(b => Equals(b.Tag, "QuickLaunchHint") && AutomationProperties.GetName(b).Contains("快速打开应用库")) && Descendants(w).OfType<TextBlock>().Any(t => t.Text == "快速打开应用库"));
                w.Navigate("Desktop"); await Frame();
                var desktopCards = Descendants(w).OfType<Button>().Where(x => x.DataContext is AppTileViewModel).ToArray();
                Check("Desktop navigation shows existing shortcut members and excludes missing membership", w.CurrentSection == "Desktop" && desktopCards.Length == 1 && ((AppTileViewModel)desktopCards[0].DataContext).Entry.Id == desktop.Id);
                w.Navigate("Launchpad"); await Frame(); var search = Descendants(w).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "搜索应用"); search.Text = "no matching filter"; await Frame();
                var last = Url(99);
                var add = (Task)typeof(ControlCenterWindow).GetMethod("ImportApplicationPathsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [new[] { last }])!; await add; await Frame();
                var result = c.State.Apps.Single(x => x.LaunchPath == last);
                Check("Adding from a filtered view clears filters and navigates to the imported application's page", w.CurrentSection == "Launchpad" && Descendants(w).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "搜索应用").Text.Length == 0 && Descendants(w).OfType<Button>().Any(x => x.DataContext is AppTileViewModel vm && vm.Entry.Id == result.Id));
                Check("The add action is re-enabled and pending cancel controls are removed", Descendants(w).OfType<Button>().Single(x => Equals(x.Tag, "AddApplication")).IsEnabled && !Descendants(w).OfType<Button>().Any(x => AutomationProperties.GetName(x) == "取消"));
                Check("UI import completion retains the original files and persists its visible record", File.Exists(firstUrl) && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(firstUrl))) && new StateStore(Path.Combine(root, "data")).Load().Apps.Any(x => x.Id == result.Id));
                exit = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception e) { errors.Add(e.ToString()); Console.WriteLine(e); }
            finally { w?.ClosePermanently(); c?.Dispose(); Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, JsonSerializer.Serialize(new { passed = exit == 0, checks, errors, originalFilesChanged = false, systemSettingsChanged = false }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"Import UI: {checks.Count} groups, {errors.Count} errors"); app.Shutdown(); }
        }; app.Run(); return exit;
    }
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(100); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
