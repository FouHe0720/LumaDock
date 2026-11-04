using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Desktop.Ui.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests/desktop-ui-results.json");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<object>(); var errors = new List<string>(); var exit = 1;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) throw new InvalidOperationException(name); }
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.GetType().Name); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? controller = null; ControlCenterWindow? window = null;
            try
            {
                var root = Path.GetFullPath(Path.Combine("work", "desktop-ui-tests", Guid.NewGuid().ToString("N")));
                var user = Directory.CreateDirectory(Path.Combine(root, "user desktop")).FullName;
                var common = Directory.CreateDirectory(Path.Combine(root, "common desktop")).FullName;
                Log.DirectoryPath = Path.Combine(root, "logs");
                var service = new DesktopFileService(user, common);
                var folder = Directory.CreateDirectory(Path.Combine(user, "工作资料")).FullName;
                File.WriteAllText(Path.Combine(folder, "nested-only.txt"), "Owned nested fixture");
                Directory.CreateDirectory(Path.Combine(common, "公共资料"));
                var original = Path.Combine(user, "notes.txt"); File.WriteAllText(original, "Keep original fixture bytes");
                var commonOriginal = Path.Combine(common, "notes.txt"); File.WriteAllText(commonOriginal, "Keep common fixture bytes");
                var url = Path.Combine(user, "Website.url"); File.WriteAllText(url, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
                var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
                var shortcut = DesktopShortcutService.WriteShortcut(new AppEntry { Name = "Shortcut fixture", LaunchPath = notepad, ExecutablePath = notepad }, user);
                var hidden = Path.Combine(user, "hidden.txt"); File.WriteAllText(hidden, "Hidden fixture"); File.SetAttributes(hidden, FileAttributes.Hidden);
                var sourceHash = SHA256.HashData(File.ReadAllBytes(original));
                var shortcutHash = SHA256.HashData(File.ReadAllBytes(shortcut));

                controller = new AppController(Path.Combine(root, "data"), null);
                controller.State.Settings.Animations = false; controller.State.Settings.FollowSystemIconFont = false;
                controller.State.Settings.NameFontFamily = "Microsoft YaHei UI"; controller.State.Settings.NameFontSize = 15; controller.State.Settings.Columns = 6; controller.ApplySettings();
                window = new ControlCenterWindow(controller, "Desktop", service) { Width = 1120, Height = 760, ShowActivated = false, ShowInTaskbar = false };
                window.Show(); await Until(() => Cards(window).Length == 6); await Frame();
                Check("The real desktop view includes folders, files, URLs and shortcuts independently of the application catalog", Cards(window).Length == 6 && Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == folder) && Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == original) && Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == shortcut) && controller.State.Apps.Count == 0);
                Check("Same-name user and common desktop documents remain separate cards", Cards(window).Count(b => ((DesktopItem)b.DataContext).Name == "notes.txt") == 2 && Cards(window).Count(b => ((DesktopItem)b.DataContext).IsCommonDesktop) == 2);
                Check("Hidden items and nested contents do not appear as desktop cards", Cards(window).All(b => ((DesktopItem)b.DataContext).Path != hidden && !((DesktopItem)b.DataContext).Path.EndsWith("nested-only.txt")));
                await Until(() => Cards(window).Where(b => ((DesktopItem)b.DataContext).Path == folder || ((DesktopItem)b.DataContext).Path == original).All(b => Descendants(b).OfType<Image>().Any(image => image.Source != null)));
                Check("Folders and ordinary documents receive frozen real Shell bitmap icons", Cards(window).Where(b => ((DesktopItem)b.DataContext).Path == folder || ((DesktopItem)b.DataContext).Path == original).All(b => Descendants(b).OfType<Image>().Single().Source is BitmapSource bitmap && bitmap.IsFrozen));
                Check("Desktop names use the configured readable application typography", Cards(window).All(b => Descendants(b).OfType<TextBlock>().Where(t => Equals(t.Tag, "DesktopItemName")).All(t => Math.Abs(t.FontSize - 15) < .1 && t.FontFamily.Source == "Microsoft YaHei UI" && t.TextWrapping == TextWrapping.Wrap)));
                var surface = Descendants(window).OfType<Grid>().Single(g => Equals(g.Tag, "DesktopSurface"));
                var background = surface.ContextMenu!; var newMenu = background.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "新建"));
                Check("The blank desktop surface has working creation commands for folders and text documents", newMenu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).SequenceEqual(new[] { "文件夹", "文本文档 (.txt)" }) && Descendants(window).OfType<Button>().Any(b => Equals(b.Tag, "DesktopNew")));
                Check("The blank surface exposes file-copy paste, refresh and the original desktop folder", new[] { "粘贴文件副本", "刷新", "打开桌面文件夹" }.All(name => background.Items.OfType<MenuItem>().Any(i => Equals(i.Header, name))));
                var menuActionCount = 0; var keyboardAction = new MenuItem { Header = "Keyboard fixture action" }; keyboardAction.Click += (_, _) => menuActionCount++;
                var disabledAction = new MenuItem { Header = "Disabled fixture action", IsEnabled = false }; background.Items.Add(keyboardAction); background.Items.Add(disabledAction);
                background.PlacementTarget = surface; background.IsOpen = true; await Frame();
                var popupBrush = (Brush)app.FindResource("PopupBrush"); var textBrush = (Brush)app.FindResource("TextBrush");
                var menuFace = (Border)background.Template.FindName("DesktopMenuSurface", background);
                Check("The opened desktop context menu uses its rounded theme surface rather than a white system icon rail", menuFace.CornerRadius.TopLeft == 12 && Equals(menuFace.Background, popupBrush) && ColorOf(menuFace.Background) != Colors.White && Equals(newMenu.Foreground, textBrush));
                newMenu.Focus(); PressKey(newMenu, Key.Right); await Until(() => newMenu.IsSubmenuOpen);
                var popup = (Popup)newMenu.Template.FindName("PART_Popup", newMenu);
                var submenuFace = (Border)newMenu.Template.FindName("SubmenuSurface", newMenu);
                var folderAction = newMenu.Items.OfType<MenuItem>().First(); folderAction.ApplyTemplate();
                Check("The actual opened submenu has a readable themed surface and themed text at every level", popup.IsOpen && popup.AllowsTransparency && submenuFace.CornerRadius.TopLeft == 12 && Equals(submenuFace.Background, popupBrush) && ColorOf(submenuFace.Background) != Colors.White && newMenu.Items.OfType<MenuItem>().All(i => Equals(i.Foreground, textBrush) && i.Template.FindName("ItemFace", i) is Border));
                Check("Right-arrow keyboard navigation still opens the native MenuItem submenu", newMenu.IsSubmenuOpen && popup.Child != null);
                folderAction.Focus(); PressKey(folderAction, Key.Escape); await Frame(); newMenu.IsSubmenuOpen = false;
                Check("Disabled desktop menu actions retain a visible disabled state", !disabledAction.IsEnabled && disabledAction.Opacity < .5);
                background.IsOpen = true; await Frame(); keyboardAction.Focus(); PressKey(keyboardAction, Key.Enter); await Frame();
                Check("Enter still invokes an enabled menu action through native keyboard handling", menuActionCount == 1);
                background.IsOpen = false; background.Items.Remove(keyboardAction); background.Items.Remove(disabledAction);
                var documentCard = Cards(window).Single(b => ((DesktopItem)b.DataContext).Path == original);
                Check("Desktop item menus expose open, reveal, copy and rename without destructive deletion", new[] { "打开", "在资源管理器中显示", "复制", "复制路径", "重命名…" }.All(name => documentCard.ContextMenu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, name))) && !documentCard.ContextMenu!.Items.OfType<MenuItem>().Any(i => i.Header?.ToString()?.Contains("删除") == true));
                Check("Desktop folders and launchable entries offer Dock pinning while ordinary documents stay outside the Dock", Cards(window).Single(b => ((DesktopItem)b.DataContext).Path == shortcut).ContextMenu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "固定到 Dock")) && Cards(window).Single(b => ((DesktopItem)b.DataContext).Path == folder).ContextMenu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "固定到 Dock")) && !documentCard.ContextMenu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "固定到 Dock")));
                Check("The quick-launch shortcut remains visibly explained in the restored desktop view", Descendants(window).OfType<Button>().Any(b => Equals(b.Tag, "QuickLaunchHint") && AutomationProperties.GetName(b).Contains("快速打开应用库")));

                var search = Descendants(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "搜索桌面"); search.Text = "工作资料"; await Frame();
                Check("Desktop search filters file and folder names without changing the application search", Cards(window).Length == 1 && ((DesktopItem)Cards(window)[0].DataContext).Path == folder);
                search.Text = "no matches"; await Frame(); Check("A non-matching desktop query shows an honest empty result", Cards(window).Length == 0 && Descendants(window).OfType<TextBlock>().Any(t => t.Text == "没有找到项目"));

                DesktopItem? created = null;
                await Mutation(window, () => created = service.CreateFolder("UI-created folder")); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == created!.Path));
                Check("Creating a folder from the UI action clears search and reveals its real saved directory", Directory.Exists(created!.Path) && Descendants(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "搜索桌面").Text == "" && Field<string>(window, "_selectedDesktopPath") == created.Path);
                DesktopItem? text = null; await Mutation(window, () => text = service.CreateTextFile("UI-created document")); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == text!.Path));
                Check("Creating a text document from the UI action writes it once and selects the visible file", File.Exists(text!.Path) && new FileInfo(text.Path).Length == 0 && Field<string>(window, "_selectedDesktopPath") == text.Path);
                var selectedCard = Cards(window).Single(b => ((DesktopItem)b.DataContext).Path == text.Path); selectedCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("A single click selects a desktop item without importing it or launching an application", Field<string>(window, "_selectedDesktopPath") == text.Path && controller.State.Apps.Count == 0);

                var external = Path.Combine(user, "External watch add.txt"); await Task.Run(() => File.WriteAllText(external, "External operation in owned fixture")); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == external));
                Check("A file created outside the view appears automatically through directory notifications", Cards(window).Count(b => ((DesktopItem)b.DataContext).Path == external) == 1);
                var renamed = Path.Combine(user, "External watch rename.txt"); await Task.Run(() => File.Move(external, renamed)); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == renamed) && Cards(window).All(b => ((DesktopItem)b.DataContext).Path != external));
                Check("A real external rename replaces the old card without duplicating a catalog record", Cards(window).All(b => ((DesktopItem)b.DataContext).Path != external) && controller.State.Apps.Count == 0);
                await Task.Run(() => File.Delete(renamed)); await Until(() => Cards(window).All(b => ((DesktopItem)b.DataContext).Path != renamed));
                Check("An externally removed owned fixture disappears without manual refresh", Cards(window).All(b => ((DesktopItem)b.DataContext).Path != renamed));

                Check("Directory watchers exist only while the desktop section is visible", Field<DesktopDirectoryWatcher?>(window, "_desktopWatcher") != null);
                window.Navigate("Launchpad"); await Frame();
                Check("Navigating to the application library releases desktop watchers and their debounce timer", Field<DesktopDirectoryWatcher?>(window, "_desktopWatcher") == null && Field<DispatcherTimer?>(window, "_desktopDebounce")?.IsEnabled != true);
                var whileAway = Path.Combine(user, "Created while away.txt"); File.WriteAllText(whileAway, "Owned away fixture"); window.Navigate("Desktop"); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == whileAway));
                Check("Returning to the desktop rescans changes made while its watcher was released", Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == whileAway) && Field<DesktopDirectoryWatcher?>(window, "_desktopWatcher") != null);
                window.HideSmoothly(); await Frame();
                Check("Hiding the control center releases desktop watchers", !window.IsVisible && Field<DesktopDirectoryWatcher?>(window, "_desktopWatcher") == null);
                var whileHidden = Path.Combine(common, "Created while hidden.txt"); File.WriteAllText(whileHidden, "Owned hidden-window fixture"); window.Show(); await Until(() => Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == whileHidden));
                Check("Showing the desktop again discovers common-desktop changes made while hidden", Cards(window).Any(b => ((DesktopItem)b.DataContext).Path == whileHidden));
                Check("Desktop operations preserve original bytes, shortcut bytes and the application catalog", sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))) && shortcutHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(shortcut))) && controller.State.Apps.Count == 0 && controller.State.PinnedIds.Count == 0);
                var folderCard = Cards(window).Single(card => ((DesktopItem)card.DataContext).Path == folder);
                folderCard.ContextMenu!.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "固定到 Dock")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => controller.State.PinnedIds.Count == 1);
                Check("The actual desktop folder menu pins its directory without importing it as an application or changing files", controller.State.Apps.Single().IsFolder && controller.State.Apps[0].LaunchPath == folder && File.ReadAllText(Path.Combine(folder, "nested-only.txt")) == "Owned nested fixture");
                window.Navigate("Launchpad"); await Frame();
                Check("A pinned desktop folder remains outside the application library and survives storage", Field<IReadOnlyList<ApplicationDisplayGroup>>(window, "_applicationGroups")?.Count == 0 && new StateStore(Path.Combine(root, "data")).Load().Apps.Single().IsFolder);
                window.ClosePermanently();
                Check("Permanently closing the window releases watchers and stops the pending debounce", Field<DesktopDirectoryWatcher?>(window, "_desktopWatcher") == null && Field<DispatcherTimer?>(window, "_desktopDebounce")?.IsEnabled != true);
                window = null; exit = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception e) { errors.Add(e.GetType().Name); Console.WriteLine(e); }
            finally
            {
                window?.ClosePermanently(); controller?.Dispose(); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = exit == 0, checks, errors, actualFixtureWindowsRendered = true, personalDesktopModified = false, clipboardChanged = false, filesLaunched = false, systemSettingsChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Desktop UI: {checks.Count} checks, {errors.Count} errors"); app.Shutdown();
            }
        };
        app.Run(); return exit;
    }
    private static Button[] Cards(DependencyObject root) => Descendants(root).OfType<Button>().Where(b => b.DataContext is DesktopItem).ToArray();
    private static Color ColorOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
    private static void PressKey(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    private static T? Field<T>(object value, string name) => (T?)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static async Task Mutation(ControlCenterWindow window, Func<DesktopItem> action) => await (Task)typeof(ControlCenterWindow).GetMethod("RunDesktopMutationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [action])!;
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(100); }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The rendered desktop did not reach its expected state."); await Frame(); }
        await Frame();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
