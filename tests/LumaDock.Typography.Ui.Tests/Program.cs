using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Reflection;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Typography.Ui.Tests;

/// <summary>Actual WPF label/layout checks; no app launching, tray, hotkeys or OS setting changes.</summary>
internal static class Program
{
    private static readonly List<TestResult> Tests = [];
    private static readonly List<Snapshot> Snapshots = [];
    private static readonly List<string> Errors = [];
    private static readonly List<object> NativeCaretChecks = [];
    private static int exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        var reportPath = Path.GetFullPath(Argument(args, "--report") ?? "artifacts/tests/typography-ui-results.json");
        var dataPath = Path.GetFullPath(Path.Combine("work", "typography-ui-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(dataPath);
        Log.DirectoryPath = Path.Combine(dataPath, "logs");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };
        application.Startup += async (_, _) =>
        {
            var timer = Stopwatch.StartNew();
            AppController? controller = null;
            ControlCenterWindow? window = null;
            DesktopFontInfo? nativeFont = null;
            try
            {
                controller = new AppController(dataPath, null);
                controller.State.Settings.Animations = false;
                controller.State.Settings.CloseToTray = true;
                await controller.RefreshAppsAsync();
                Require(controller.State.Apps.Count > 0, "No real applications were discovered.");
                Require(controller.State.Settings.FollowSystemIconFont, "A fresh profile must follow the desktop font.");
                nativeFont = DesktopFontService.Read();
                var fixtureDesktop = Directory.CreateDirectory(Path.Combine(dataPath, "fixture-desktop")).FullName;
                var fixtureCommonDesktop = Directory.CreateDirectory(Path.Combine(dataPath, "fixture-common-desktop")).FullName;
                for (var index = 0; index < 20; index++) File.WriteAllText(Path.Combine(fixtureDesktop, $"桌面字体排版示例文档 {index:00}.txt"), "Owned typography fixture");
                for (var index = 0; index < 4; index++) Directory.CreateDirectory(Path.Combine(fixtureDesktop, $"桌面字体排版示例文件夹 {index:00}"));
                File.WriteAllText(Path.Combine(fixtureCommonDesktop, "公共桌面文档.txt"), "Owned common fixture");
                Directory.CreateDirectory(Path.Combine(fixtureCommonDesktop, "公共桌面文件夹"));
                var desktopFiles = new DesktopFileService(fixtureDesktop, fixtureCommonDesktop);
                window = new ControlCenterWindow(controller, desktopFiles: desktopFiles) { ShowActivated = false, ShowInTaskbar = false };
                window.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(window).Handle, false);
                window.Show(); await Frame();

                Test("A fresh collection has library and desktop pages and no automatic columns", () =>
                {
                    Require(controller.State.Categories.Count == 0, "Automatic categories returned in a fresh profile.");
                    var navigation = Descendants<TextBlock>(window).Where(text => Equals(text.Tag, "NavigationLabel")).Select(text => text.Text).ToArray();
                    Require(navigation.SequenceEqual(new[] { "应用库", "桌面集", "偏好设置", "关于" }), "Duplicate desktop or library navigation remains.");
                    Require(!Descendants<Button>(window).Any(button => Equals(button.Tag, "Dock 已固定")), "A built-in pinned category remains.");
                    Require(Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "PageHeading")).Text == "应用库", "Unified heading is missing.");
                });
                var totalBeforeAlias = System.Windows.Automation.AutomationProperties.GetHelpText(Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用")));
                var firstPageBeforeAlias = Descendants<Button>(window).Where(button => button.DataContext is AppTileViewModel).Select(button => ((AppTileViewModel)button.DataContext).Entry.Id).ToArray();
                window.Navigate("Desktop"); await WaitForDesktop(window);
                Test("Desktop navigation shows real top-level files and folders with a matching count and no app categories", () =>
                {
                    Require(window.CurrentSection == "Desktop", "Desktop view was not restored.");
                    var expected = desktopFiles.Scan();
                    Require(expected.Errors.Count == 0 && expected.Items.Count == 26, "Owned desktop fixture is incomplete.");
                    Require(Descendants<TextBlock>(window).Any(text => text.Text.StartsWith($"{expected.Items.Count} 个项目 ·", StringComparison.Ordinal)), "Desktop count does not match its real snapshot.");
                    var cards = Descendants<Button>(window).Where(button => button.DataContext is DesktopItem).ToArray();
                    Require(cards.Length > 0 && cards.Any(button => ((DesktopItem)button.DataContext).IsDirectory) && cards.Any(button => !((DesktopItem)button.DataContext).IsDirectory), "Files and folders were not rendered together.");
                    Require(cards.All(button => expected.Items.Any(item => item.Path == ((DesktopItem)button.DataContext).Path)), "Desktop contains a non-desktop card.");
                    Require(!Descendants<Button>(window).Any(button => button.DataContext is AppTileViewModel || Equals(button.Tag, "全部应用")), "Desktop still presents catalog records or app category controls.");
                });
                var createColumn = typeof(ControlCenterWindow).GetMethod("TryCreateCategory", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var renameColumn = typeof(ControlCenterWindow).GetMethod("TryRenameCategory", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Require((bool)createColumn.Invoke(window, ["随手"])!, "The column creation action rejected a valid name."); await Frame();
                Test("Creating a column updates actual sidebar, chip, selected page and storage", () =>
                {
                    Require(new StateStore(dataPath).Load().Categories.SequenceEqual(new[] { "随手" }), "Created column did not persist.");
                    Require(Descendants<Button>(window).Any(button => Equals(button.Tag, "CategoryNavigation:随手")), "Column navigation is missing.");
                    Require(Descendants<Button>(window).Single(button => Equals(button.Tag, "随手")).ContextMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "删除栏目")), "Created column has no removal action.");
                    Require(Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "PageHeading")).Text == "随手", "New column was not selected.");
                });
                window.Navigate("Launchpad"); await Frame();
                var groupedEntry = controller.State.Apps.First(entry => !entry.Hidden && entry.IsAvailable);
                var groupedSourceHash = File.Exists(groupedEntry.LaunchPath) ? FileHash(groupedEntry.LaunchPath) : null;
                var pinOrder = controller.State.PinnedIds.ToArray();
                var groupedTile = Descendants<Button>(window).First(button => button.DataContext is AppTileViewModel model && model.Entry.Id == groupedEntry.Id);
                var putMenu = groupedTile.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "放入栏目"));
                putMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "随手")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Frame();
                Descendants<Button>(window).Single(button => Equals(button.Tag, "随手")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Test("The actual application menu assigns a manual column and its chip filters correctly", () =>
                {
                    var saved = new StateStore(dataPath).Load().Apps.Single(entry => entry.Id == groupedEntry.Id);
                    Require(saved.Category == "随手" && saved.CategoryIsUserAssigned, "Manual assignment did not persist.");
                    var visible = Descendants<Button>(window).Where(button => button.DataContext is AppTileViewModel).ToArray();
                    Require(visible.Length == 1 && ((AppTileViewModel)visible[0].DataContext).Entry.Id == groupedEntry.Id, "Column selection is not the assigned application.");
                });
                Require((bool)renameColumn.Invoke(window, ["随手", "常用"])!, "The column rename action rejected a valid name."); await Frame();
                Test("Renaming the selected column updates existing assignments and actual controls", () =>
                {
                    Require(groupedEntry.Category == "常用" && new StateStore(dataPath).Load().Categories.SequenceEqual(new[] { "常用" }), "Rename did not persist or update membership.");
                    Require(Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "PageHeading")).Text == "常用", "Renamed selected column lost its heading.");
                    Require(Descendants<Button>(window).Any(button => Equals(button.Tag, "CategoryNavigation:常用")), "Renamed sidebar is stale.");
                });
                Descendants<Button>(window).Single(button => Equals(button.Tag, "常用")).ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除栏目")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Frame();
                Test("The actual delete-column menu restores the complete list without changing applications or pins", () =>
                {
                    var saved = new StateStore(dataPath).Load();
                    Require(saved.Categories.Count == 0 && saved.Apps.Single(entry => entry.Id == groupedEntry.Id).Category.Length == 0, "Deleting a column left an assignment behind.");
                    Require(saved.Apps.Count == controller.State.Apps.Count && pinOrder.SequenceEqual(saved.PinnedIds), "Deleting a column removed an application or changed pins.");
                    Require(System.Windows.Automation.AutomationProperties.GetHelpText(Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用"))) == totalBeforeAlias, "Deleting a selected column did not restore all applications.");
                    Require(Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "PageHeading")).Text == "应用库", "Deleting a selected column did not restore the master heading.");
                    Require(groupedSourceHash == null || FileHash(groupedEntry.LaunchPath).SequenceEqual(groupedSourceHash), "Column organization changed an original application or shortcut.");
                });
                Require((bool)createColumn.Invoke(window, ["个人栏目"])!, "An explicit personal column could not be created for typography coverage.");
                window.Navigate("Launchpad"); await Frame();

                groupedEntry.IsAvailable = false; controller.Save(); await Frame();
                Test("The unified list omits an unavailable record without erasing its organizing data", () =>
                {
                    Require(!Descendants<Button>(window).Any(button => button.DataContext is AppTileViewModel model && model.Entry.Id == groupedEntry.Id), "An unavailable application is still visible.");
                    Require(controller.State.Apps.Any(entry => entry.Id == groupedEntry.Id) && !groupedEntry.Hidden, "Availability filtering removed or hid the record.");
                });
                groupedEntry.IsAvailable = true; controller.Save(); await Frame();

                var defaultSnapshot = Inspect(window, "system-launchpad");
                Test("Launchpad names use the actual Windows icon-title font by default", () =>
                {
                    CheckFont(window, nativeFont);
                    CheckLabelBounds(window);
                });
                Test("Sidebar, categories and headings read actual system font weight without enlarging navigation", () => CheckInterfaceFont(window, nativeFont));
                await TestAsync("A local font-change delivery updates existing interface labels without modifying Windows", async () =>
                {
                    // Inject into this controller's font service only: this exercises the
                    // same Changed handler used for native notifications, never SPI_SET.
                    var service = (DesktopFontService)typeof(AppController).GetField("desktopFont", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;
                    var current = typeof(DesktopFontService).GetProperty(nameof(DesktopFontService.Current))!;
                    var changed = typeof(AppController).GetMethod("DesktopFontChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var originalLabels = Descendants<TextBlock>(window).Where(text => Equals(text.Tag, "NavigationLabel")).ToArray();
                    var injected = nativeFont with { Weight = nativeFont.Weight == 800 ? 700 : 800 };
                    try
                    {
                        current.SetValue(service, injected); changed.Invoke(controller, [service, EventArgs.Empty]);
                        Require(originalLabels.All(label => label.FontWeight.ToOpenTypeWeight() == injected.Weight), "Existing navigation labels ignored font-change resources.");
                        await Frame(); CheckInterfaceFont(window, injected); CheckFont(window, injected);
                        Require(DesktopFontService.Read() == nativeFont, "Font notification test changed the real system font.");
                    }
                    finally { current.SetValue(service, nativeFont); changed.Invoke(controller, [service, EventArgs.Empty]); await Frame(); }
                });
                Test("Each application exposes one named accessible button with an invoke action", () =>
                {
                    var tiles = Descendants<Button>(window).Where(b => b.DataContext is AppTileViewModel).ToArray();
                    Require(tiles.Length > 0, "No application actions were exposed.");
                    foreach (var tile in tiles)
                    {
                        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(tile)!;
                        Require(peer.GetName() == ((AppTileViewModel)tile.DataContext).Name, "Accessible application name is incorrect.");
                        Require(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) != null, "Application has no accessible invoke action.");
                        Require((peer.GetChildren()?.Count ?? 0) == 0, "Decorative image and text were exposed as duplicate actions.");
                    }
                });

                var sampleName = controller.State.Apps.First(app => !app.Hidden).Name;
                var query = sampleName[..Math.Min(2, sampleName.Length)];
                var search = Descendants<TextBox>(window).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "搜索应用");
                search.Text = query; await Frame();
                Test("Category counts follow the actual visible search results", () =>
                {
                    var expected = ApplicationPresentation.Group(controller.State.Apps.Where(app => !app.Hidden && app.IsAvailable
                        && (app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || app.LaunchPath.Contains(query, StringComparison.OrdinalIgnoreCase)))).Count;
                    var all = Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用"));
                    Require(System.Windows.Automation.AutomationProperties.GetHelpText(all) == $"{expected} 个应用", "Category count does not match the actual search results.");
                    Require(Descendants<TextBlock>(all).Any(text => text.Text == expected.ToString()), "The visible count did not update.");
                });
                search.Clear(); await Frame();

                // Enable only this isolated application's own window for the
                // native caret contract, then return to the disabled test mode.
                var nativeBlinkBefore = GetCaretBlinkTime();
                var ownHwnd = new WindowInteropHelper(window).Handle;
                EnableWindow(ownHwnd, true);
                try
                {
                    window.Activate(); search.Focus();
                    var focusedClock = await WaitForStoppedCaret(window, search);
                    Test("Focused search stops the retained root clock while preserving a native caret", () =>
                    {
                        Require(search.IsKeyboardFocused, "The actual search TextBox did not receive keyboard focus.");
                        Require(focusedClock.CurrentState == ClockState.Stopped, "The retained caret root clock remained active.");
                        CheckNativeCaret(ownHwnd, "focused-search");
                        Require(GetCaretBlinkTime() == nativeBlinkBefore, "The Windows caret blink setting changed.");
                    });
                    search.Text = "LumaDock 查找"; search.Select(0, 8); search.SelectedText = "窗口"; search.Select(0, 2);
                    await Frame();
                    Test("Steady caret preserves actual text editing and selection", () =>
                    {
                        Require(search.Text.StartsWith("窗口", StringComparison.Ordinal) && search.SelectedText == "窗口" && search.SelectionLength == 2, "Search editing or selection stopped working.");
                        Require(GetCaretBlinkTime() == nativeBlinkBefore, "Editing changed the system caret blink setting.");
                    });
                    search.Clear(); search.Select(0, 0); await WaitForStoppedCaret(window, search);
                    var unfocusedClock = CaretClock(window)!;
                    Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用")).Focus(); await Frame();
                    Test("Moving focus away stops the previous caret clock", () => Require(unfocusedClock.CurrentState == ClockState.Stopped, "An unfocused TextBox retained an active root clock."));
                    search.Focus(); await WaitForStoppedCaret(window, search);
                    search.FontFamily = new FontFamily("Consolas"); search.FontWeight = FontWeights.Normal; search.FontStyle = FontStyles.Italic; search.FontSize = 18;
                    var changedClock = await WaitForStoppedCaret(window, search);
                    Test("Font changes keep the focused native caret usable without restarting idle animation", () =>
                    {
                        Require(changedClock.CurrentState == ClockState.Stopped, "A font change restarted the retained clock.");
                        CheckNativeCaret(ownHwnd, "font-change");
                    });
                    // A pending Begin is deliberately queued immediately before
                    // removal to exercise cleanup before WPF's next timing tick.
                    changedClock.Controller!.Begin(); window.Navigate("Settings"); await Frame();
                    Test("Removing a focused search cancels even a pending native clock start", () =>
                    {
                        Require(!search.IsLoaded && changedClock.CurrentState == ClockState.Stopped, "A removed TextBox left an orphan root clock.");
                    });
                    window.Navigate("Launchpad"); await Frame(); search = Descendants<TextBox>(window).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "搜索应用");
                    search.Focus(); var beforeThemeClock = await WaitForStoppedCaret(window, search);
                    var previousTheme = controller.State.Settings.Theme;
                    controller.State.Settings.Theme = previousTheme == "Dark" ? "Light" : "Dark"; controller.ApplySettings(); await Frame();
                    search = Descendants<TextBox>(window).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "搜索应用");
                    search.Focus(); await WaitForStoppedCaret(window, search);
                    Test("Theme refresh retires the old clock and preserves the new native caret", () =>
                    {
                        Require(beforeThemeClock.CurrentState == ClockState.Stopped, "Theme refresh left the old clock active.");
                        CheckNativeCaret(ownHwnd, "theme-change");
                        Require(GetCaretBlinkTime() == nativeBlinkBefore, "Caret suppression changed the Windows setting.");
                    });
                    controller.State.Settings.Theme = previousTheme; controller.ApplySettings(); await Frame();
                }
                finally { EnableWindow(ownHwnd, false); }
                window.Navigate("Launchpad"); await Frame();

                // Use the visible tile and its real controller/menu/binding data
                // flow. Only the isolated profile and a synthetic PNG are written.
                var iconTarget = Descendants<Button>(window).Where(button => button.IsVisible && button.DataContext is AppTileViewModel)
                    .Select(button => ((AppTileViewModel)button.DataContext).Entry).First();
                var customIconFile = Path.Combine(dataPath, "local-icon-fixture.png");
                var iconPixels = new byte[96 * 48 * 4];
                for (var offset = 0; offset < iconPixels.Length; offset += 4)
                { iconPixels[offset] = 117; iconPixels[offset + 1] = 33; iconPixels[offset + 2] = 241; iconPixels[offset + 3] = 255; }
                var iconEncoder = new PngBitmapEncoder();
                iconEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(96, 48, 96, 96, PixelFormats.Bgra32, null, iconPixels, 96 * 4)));
                using (var output = File.Create(customIconFile)) iconEncoder.Save(output);
                var fixtureHashBefore = FileHash(customIconFile);
                var originalPath = iconTarget.LaunchPath;
                var originalFileHash = File.Exists(originalPath) ? FileHash(originalPath) : null;
                var originalIconTile = await WaitForIcon(window, iconTarget.Id, image => image != null);
                var originalIcon = ((AppTileViewModel)originalIconTile.DataContext).Icon!;
                await TestAsync("A custom icon reaches the visible tile binding and isolated persisted record", async () =>
                {
                    Require(originalIconTile.ContextMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "更换图标…")), "The visible application's change-icon action is missing.");
                    controller.SetCustomIcon(iconTarget, customIconFile);
                    var tile = await WaitForIcon(window, iconTarget.Id, image => image != null && image.PixelWidth <= 96 && image.PixelWidth == image.PixelHeight * 2 && IconBytes(image)[2] == 241);
                    var vm = (AppTileViewModel)tile.DataContext;
                    var boundImage = Descendants<Image>(tile).Single();
                    Require(ReferenceEquals(boundImage.Source, vm.Icon), "The actual WPF image binding did not receive the new icon.");
                    Require(vm.Entry.CustomIconPath == customIconFile && new StateStore(dataPath).Load().Apps.Single(app => app.Id == iconTarget.Id).CustomIconPath == customIconFile, "The custom icon path did not persist.");
                    Require(tile.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复原图标")).IsEnabled, "The restore-icon action did not become available.");
                    Require(iconTarget.LaunchPath == originalPath, "Changing an icon changed the launch target.");
                });
                await TestAsync("The actual restore-icon menu action restores artwork and leaves source files unchanged", async () =>
                {
                    var tile = Descendants<Button>(window).Single(button => button.DataContext is AppTileViewModel vm && vm.Entry.Id == iconTarget.Id);
                    var restore = tile.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复原图标"));
                    Require(restore.IsEnabled, "Restore is unavailable after setting the fixture icon.");
                    restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    tile = await WaitForIcon(window, iconTarget.Id, image => image != null && IconBytes(image).SequenceEqual(IconBytes(originalIcon)));
                    var vm = (AppTileViewModel)tile.DataContext;
                    Require(vm.Entry.CustomIconPath.Length == 0 && new StateStore(dataPath).Load().Apps.Single(app => app.Id == iconTarget.Id).CustomIconPath.Length == 0, "Restoring the original icon did not clear the saved override.");
                    Require(ReferenceEquals(Descendants<Image>(tile).Single().Source, vm.Icon), "Restored artwork did not reach the actual image binding.");
                    Require(!tile.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复原图标")).IsEnabled, "Restore stayed enabled without an override.");
                    Require(FileHash(customIconFile).SequenceEqual(fixtureHashBefore), "The custom icon source was modified.");
                    Require(originalFileHash == null || FileHash(originalPath).SequenceEqual(originalFileHash), "The original application or shortcut was modified.");
                });

                controller.State.Settings.FollowSystemIconFont = false;
                controller.State.Settings.NameFontSize = 28;
                controller.State.Settings.NameFontBold = true;
                controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                var mediumSnapshot = Inspect(window, "manual-28-bold-launchpad");
                Test("Manual 28 DIP bold names retain two lines inside their tiles", () =>
                {
                    CheckManualFont(window, 28);
                    CheckInterfaceFont(window, controller.NameFont);
                    CheckLabelBounds(window);
                    Require(mediumSnapshot.Columns <= defaultSnapshot.Columns, "Larger names increased the column density.");
                });

                controller.State.Settings.NameFontFamily = "Consolas";
                controller.State.Settings.NameFontItalic = true;
                controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                Test("Manual family and italic reach real names and persisted preferences", () =>
                {
                    Require(Labels(window).All(label => label.FontFamily.Source == "Consolas" && label.FontStyle == FontStyles.Italic), "Family or italic did not reach the names.");
                    var saved = new StateStore(dataPath).Load().Settings;
                    Require(saved.NameFontFamily == "Consolas" && saved.NameFontItalic && !saved.FollowSystemIconFont, "Manual typography was not saved.");
                    Require(DesktopFontService.Read() == nativeFont, "Application typography changed the Windows font.");
                });
                controller.State.Settings.NameFontFamily = "";
                controller.State.Settings.NameFontItalic = false;
                controller.State.Settings.NameFontSize = 40;
                controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                var largeSnapshot = Inspect(window, "manual-40-bold-launchpad");
                Test("Large 40 DIP names reduce grid density without vertical clipping", () =>
                {
                    CheckManualFont(window, 40);
                    CheckLabelBounds(window);
                    Require(largeSnapshot.Columns < mediumSnapshot.Columns, "40 DIP labels kept the smaller font's column density.");
                    Require(largeSnapshot.NameHeight > mediumSnapshot.NameHeight, "Large labels did not receive more vertical space.");
                    Require(largeSnapshot.VisibleTileCount < defaultSnapshot.VisibleTileCount, "Large labels were squeezed into the default page density.");
                });

                window.Navigate("Desktop"); await WaitForDesktop(window);
                var desktopSnapshot = Inspect(window, "manual-40-bold-desktop-files");
                Test("Desktop entries preserve the selected font and adaptive layout", () =>
                {
                    Require(desktopSnapshot.VisibleTileCount > 0 && window.CurrentSection == "Desktop", "Desktop entry did not use the desktop collection.");
                    CheckManualFont(window, 40);
                    CheckLabelBounds(window);
                });

                controller.State.Settings.FollowSystemIconFont = true;
                controller.ApplySettings(); window.Navigate("Desktop"); await WaitForDesktop(window);
                Inspect(window, "restored-system-desktop");
                Test("Switching back to the system font restores native desktop typography", () =>
                {
                    CheckFont(window, DesktopFontService.Read());
                    CheckLabelBounds(window);
                });
                controller.State.Settings.Accent = "Jade";
                controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                Test("Personal accent reaches actual WPF resources and persistence", () =>
                {
                    var brush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                    Require(brush.Color.G > brush.Color.R && brush.Color.G > brush.Color.B, "Jade palette did not reach the visual resources.");
                    Require(new StateStore(dataPath).Load().Settings.Accent == "Jade", "Accent did not persist.");
                });
                controller.State.Settings.FollowSystemIconFont = false;
                controller.State.Settings.NameFontFamily = "Microsoft YaHei UI";
                controller.State.Settings.NameFontSize = 18;
                controller.State.Settings.NameFontItalic = true;
                controller.ApplySettings(); window.Navigate("Settings"); await Frame();
                window.Width = window.MinWidth; await Frame();
                Test("All six palette controls fit at the minimum supported window width", () =>
                {
                    var swatches = Descendants<Button>(window).Where(button => button.Tag is string tag && tag.StartsWith("Accent:")).ToArray();
                    Require(swatches.Length == 6, "The complete six-color palette was not exposed.");
                    var palette = Ancestor<WrapPanel>(swatches[0])!;
                    Require(palette.ActualWidth <= 322.5, "Palette exceeds its settings column.");
                    foreach (var button in swatches)
                    {
                        var point = button.TranslatePoint(new Point(), palette);
                        Require(point.X >= -.5 && point.X + button.ActualWidth <= palette.ActualWidth + .5, "A color control extends beyond its palette.");
                    }
                });
                Test("Reflection preference and lower Dock concentration are real persisted controls", () =>
                {
                    var reflect = Descendants<CheckBox>(window).Single(check => System.Windows.Automation.AutomationProperties.GetName(check) == "图标倒影");
                    reflect.IsChecked = false; reflect.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Require(!new StateStore(dataPath).Load().Settings.IconReflection, "The actual reflection toggle did not save.");
                    reflect.IsChecked = true; reflect.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var strength = Descendants<Slider>(window).Single(slider => System.Windows.Automation.AutomationProperties.GetName(slider) == "倒影强度");
                    Require(Math.Abs(strength.Maximum - .95) < .001, "The expanded reflection strength is unavailable.");
                    strength.Value = .90;
                    strength.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    var density = Descendants<Slider>(window).Single(slider => System.Windows.Automation.AutomationProperties.GetName(slider) == "Dock 浓度");
                    Require(Math.Abs(density.Minimum - .02) < .001, "The transparent range is still restricted to the old minimum.");
                    density.Value = .04;
                    density.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    var vivid = Descendants<Slider>(window).Single(slider => System.Windows.Automation.AutomationProperties.GetName(slider) == "图标鲜艳度");
                    vivid.Value = .50;
                    vivid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    var saved = new StateStore(dataPath).Load().Settings;
                    Require(saved.IconReflection && Math.Abs(saved.ReflectionStrength - .90) < .001 && Math.Abs(saved.Opacity - .04) < .001 && Math.Abs(saved.DockIconVibrancy - .50) < .001, "Reflection strength, vibrancy or concentration did not persist through actual controls.");
                    Require(Descendants<TextBlock>(window).Any(text => text.Text == "Windows 桌面图标"), "The original desktop icon recovery control is no longer reachable.");
                });
                foreach (var value in new[] { "Rose", "Graphite" })
                {
                    var swatch = Descendants<Button>(window).Single(button => Equals(button.Tag, "Accent:" + value));
                    swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                    Test(value + " palette applies through its actual settings action", () =>
                    {
                        var expected = value == "Rose" ? (Color)ColorConverter.ConvertFromString("#A24F68") : (Color)ColorConverter.ConvertFromString("#4E6071");
                        var darkExpected = value == "Rose" ? (Color)ColorConverter.ConvertFromString("#F3BDC8") : (Color)ColorConverter.ConvertFromString("#D2DCE3");
                        var actual = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
                        Require(actual == expected || actual == darkExpected, "The selected color did not reach the real theme resources.");
                        Require(new StateStore(dataPath).Load().Settings.Accent == value, "The selected color was not saved.");
                        Require(System.Windows.Automation.AutomationProperties.GetItemStatus(swatch) == "已选择", "The selected palette state was not exposed.");
                    });
                }
                foreach (var value in new[] { "Capsule", "Pebble", "Rail" })
                {
                    var shape = Descendants<Button>(window).Single(button => Equals(button.Tag, "DockShape:" + value));
                    shape.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                    Test(value + " Dock silhouette applies and persists through its actual settings action", () =>
                    {
                        Require(controller.State.Settings.DockShape == value && new StateStore(dataPath).Load().Settings.DockShape == value, "The selected silhouette was not applied and saved.");
                        var selected = Descendants<Button>(window).Where(button => button.Tag is string tag && tag.StartsWith("DockShape:") && System.Windows.Automation.AutomationProperties.GetItemStatus(button) == "已选择").ToArray();
                        Require(selected.Length == 1 && ReferenceEquals(selected[0], shape), "The selected silhouette state is ambiguous.");
                    });
                }
                window.Width = 1120; await Frame();
                var textJump = Descendants<Button>(window).Single(button => Equals(button.Content, "文字"));
                textJump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Test("The text shortcut positions the font section at the top of its viewport", () =>
                {
                    var heading = Descendants<TextBlock>(window).Single(text => text.Text == "字体与文字");
                    var viewport = Ancestor<ScrollViewer>(heading)!;
                    var y = heading.TranslatePoint(new Point(), viewport).Y;
                    Require(y >= -1 && y < 50, "The text shortcut left the font controls below the viewport.");
                });
                Test("Installed-font selector and preview reflect the local typography settings", () =>
                {
                    var selector = Descendants<ComboBox>(window).Single(combo => Equals(combo.Tag, "NameFontFamily"));
                    Require(Equals(selector.SelectedValue, "Microsoft YaHei UI"), "Installed family selection is wrong.");
                    var preview = Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "NameFontPreview"));
                    Require(preview.FontFamily.Source == "Microsoft YaHei UI" && preview.FontSize == 18 && preview.FontStyle == FontStyles.Italic, "Live font preview does not match local settings.");
                    Require(DesktopFontService.Read() == nativeFont, "Font preview changed Windows typography.");
                });
                var textSwatch = Descendants<Button>(window).Single(button => Equals(button.Tag, "TextColor:#B8E2F5"));
                textSwatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Test("Text color selection applies through the actual setting action and persists", () =>
                {
                    Require(new StateStore(dataPath).Load().Settings.TextColor == "#B8E2F5", "Text color did not persist.");
                    var preview = Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "NameFontPreview"));
                    Require(((SolidColorBrush)preview.Foreground).Color == Color.FromRgb(184, 226, 245), "Text color did not reach the live preview.");
                    Require(System.Windows.Automation.AutomationProperties.GetItemStatus(textSwatch) == "已选择", "Selected text color state was not exposed.");
                });
                window.Navigate("Launchpad"); await Frame();
                Test("Custom text color reaches sidebar, categories, page heading and real application names", () => CheckPrimaryTextColor(window, Color.FromRgb(184, 226, 245)));
                controller.State.Settings.Theme = "Light"; controller.ApplySettings(); await Frame();
                Test("Theme changes preserve explicitly selected text color", () => CheckPrimaryTextColor(window, Color.FromRgb(184, 226, 245)));
                window.Navigate("Settings"); await Frame();
                var automaticText = Descendants<Button>(window).Single(button => Equals(button.Tag, "TextColor:"));
                automaticText.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                window.Navigate("Launchpad"); await Frame();
                Test("Automatic text color restores theme contrast and leaves Windows font untouched", () =>
                {
                    CheckPrimaryTextColor(window, Color.FromRgb(32, 44, 59));
                    Require(new StateStore(dataPath).Load().Settings.TextColor == "", "Automatic color did not clear the local override.");
                    Require(DesktopFontService.Read() == nativeFont, "Text personalization changed Windows typography.");
                });
                // Exercise the actual finite WPF motion timer; these calls affect only this isolated window.
                var rim = Descendants<FrameworkElement>(window).Single(element => element.GetType().Name == "GlassRim");
                var rimType = rim.GetType();
                rimType.GetProperty("MotionEnabled")!.SetValue(rim, true);
                rimType.GetMethod("TrackPointer")!.Invoke(rim, [new Point(120, 80)]);
                Test("Interactive reflection starts on actual pointer movement", () => Require((bool)rimType.GetProperty("IsMotionRunning")!.GetValue(rim)!, "Motion did not start."));
                await Task.Delay(850);
                Test("Interactive reflection settles and identical coordinates cannot restart it", () =>
                {
                    Require(!(bool)rimType.GetProperty("IsMotionRunning")!.GetValue(rim)!, "Motion remained active at idle.");
                    rimType.GetMethod("TrackPointer")!.Invoke(rim, [new Point(120, 80)]);
                    Require(!(bool)rimType.GetProperty("IsMotionRunning")!.GetValue(rim)!, "An unchanged pointer restarted idle motion.");
                });
                rimType.GetMethod("TrackPointer")!.Invoke(rim, [new Point(320, 110)]);
                rimType.GetProperty("MotionEnabled")!.SetValue(rim, false);
                Test("Reduced or disabled glass motion stops immediately", () => Require(!(bool)rimType.GetProperty("IsMotionRunning")!.GetValue(rim)!, "Disabled motion retained an active timer."));

                // Two launch modes of a real local executable remain separate records,
                // while the UI presents a single app. These fixture modes are never launched.
                var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                Require(File.Exists(notepad), "The local Notepad fixture executable is unavailable.");
                var sourceHash = FileHash(notepad);
                var modeA = new AppEntry { Id = "ui-mode-fixture-a", Name = "Mode view fixture", LaunchPath = notepad, ExecutablePath = notepad, Arguments = "/A", WorkingDirectory = Path.GetDirectoryName(notepad)! };
                var modeB = new AppEntry { Id = "ui-mode-fixture-b", Name = "MODE VIEW FIXTURE", LaunchPath = notepad, ExecutablePath = notepad, Arguments = "/W", WorkingDirectory = Path.GetDirectoryName(notepad)! };
                var pinsBeforeModes = controller.State.PinnedIds.ToArray();
                controller.State.Apps.InsertRange(0, [modeA, modeB]); controller.State.PinnedIds.AddRange([modeA.Id, modeB.Id]);
                controller.Save(); window.Navigate("Launchpad"); await Frame();
                var modeTiles = Descendants<Button>(window).Where(button => button.DataContext is AppTileViewModel model && new[] { modeA.Id, modeB.Id }.Contains(model.Entry.Id)).ToArray();
                Test("Alternate executable modes occupy one card without erasing arguments or records", () =>
                {
                    Require(modeTiles.Length == 1 && ((AppTileViewModel)modeTiles[0].DataContext).Entry.Id == modeA.Id, "The first ordered representative is not the single visible card.");
                    var modes = modeTiles[0].ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "启动方式")).Items.OfType<MenuItem>().ToArray();
                    Require(modes.Length == 2 && ReferenceEquals(modes[0].Tag, modeA) && ReferenceEquals(modes[1].Tag, modeB), "The launch-mode menu lost its actual variant records.");
                    Require(((string)modes[0].Header).Contains("/A") && ((string)modes[1].Header).Contains("/W"), "The menu did not distinguish preserved arguments.");
                    var saved = new StateStore(dataPath).Load();
                    Require(saved.Apps.Single(entry => entry.Id == modeA.Id).Arguments == "/A" && saved.Apps.Single(entry => entry.Id == modeB.Id).Arguments == "/W", "Presentation grouping destructively merged launch commands.");
                    var expected = ApplicationPresentation.Group(controller.State.Apps.Where(entry => !entry.Hidden && entry.IsAvailable)).Count;
                    Require(System.Windows.Automation.AutomationProperties.GetHelpText(Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用"))) == $"{expected} 个应用", "The master count counts mode records instead of app cards.");
                });
                modeTiles[0].ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "从 Dock 移除")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Frame();
                Test("A grouped application's unpin action removes all mode pins together", () =>
                {
                    Require(pinsBeforeModes.SequenceEqual(controller.State.PinnedIds), "Unpinning a card left a second mode pinned or changed other pins.");
                });
                var modeTile = Descendants<Button>(window).Single(button => button.DataContext is AppTileViewModel model && model.Entry.Id == modeA.Id);
                modeTile.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "放入栏目")).Items.OfType<MenuItem>().Single(item => Equals(item.Header, "个人栏目")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Frame();
                Descendants<Button>(window).Single(button => Equals(button.Tag, "个人栏目")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Test("Manual column placement keeps all modes together and counts one app", () =>
                {
                    Require(modeA.Category == "个人栏目" && modeB.Category == "个人栏目", "Only one mode moved into the manual column.");
                    Require(Descendants<Button>(window).Count(button => button.DataContext is AppTileViewModel) == 1, "The column shows duplicate mode cards.");
                    Require(System.Windows.Automation.AutomationProperties.GetHelpText(Descendants<Button>(window).Single(button => Equals(button.Tag, "个人栏目"))) == "1 个应用", "The column counts mode records instead of apps.");
                });
                typeof(ControlCenterWindow).GetMethod("RemoveApplication", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [modeA]); await Frame();
                Test("Hiding one app card hides all of its modes and keeps source files and records", () =>
                {
                    Require(modeA.Hidden && modeB.Hidden, "A hidden app can return through another mode record.");
                    Require(controller.State.Apps.Contains(modeA) && controller.State.Apps.Contains(modeB) && FileHash(notepad).SequenceEqual(sourceHash), "Hiding a mode card removed a record or changed an executable.");
                });
                Test("Presentation grouping keeps shared hosts and different executables separate", () =>
                {
                    var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Require(ApplicationPresentation.Group([new AppEntry { Name = "Host fixture", ExecutablePath = host, Arguments = "one" }, new AppEntry { Name = "Host fixture", ExecutablePath = host, Arguments = "two" }]).Count == 2, "A shared Shell host collapsed unrelated actions.");
                    Require(ApplicationPresentation.Group([modeA, new AppEntry { Name = modeA.Name, ExecutablePath = host }]).Count == 2, "Matching names collapsed different executables.");
                });
                controller.State.Apps.RemoveAll(entry => entry.Id == modeA.Id || entry.Id == modeB.Id); controller.Save(); window.Navigate("Launchpad"); await Frame();
                Test("WPF completed all typography transitions without a dispatcher exception", () => Require(Errors.Count == 0, "Dispatcher errors were observed."));
                exitCode = Tests.All(t => t.Passed) && Errors.Count == 0 ? 0 : 1;
            }
            catch (Exception error) { Errors.Add(error.GetType().Name + ": " + error.Message); }
            finally
            {
                try { window?.ClosePermanently(); controller?.Dispose(); }
                catch (Exception error) { Errors.Add(error.GetType().Name + ": " + error.Message); exitCode = 1; }
                var report = new
                {
                    passed = exitCode == 0, recordedAtUtc = DateTimeOffset.UtcNow, platform = "Actual Windows WPF x64",
                    durationMs = timer.Elapsed.TotalMilliseconds, nativeFont,
                    testCount = Tests.Count, tests = Tests, snapshots = Snapshots, errors = Errors,
                    systemSettingsChanged = false, systemFontChanged = false, appLaunches = 0,
                    controllerStartCalled = false, nativeWindowsDisabledExceptOwnCaretContract = true,
                    nativeCaretChecks = NativeCaretChecks,
                    interpretation = "Checks actual WPF labels, local preferences, unified navigation, column controls/menus and persistence, native caret/root-clock lifecycle, counts and bounded motion using isolated data. Creation/rename values use the same actions called by their dialogs; text entry is programmatic. External IME composition is not tested. Font-change delivery uses local service injection. Windows typography and caret blink settings are not modified."
                };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Typography UI checks: {Tests.Count(t => t.Passed)}/{Tests.Count} passed; overall={exitCode == 0}");
                application.Shutdown(exitCode);
            }
        };
        application.Run();
        return exitCode;
    }

    private static void CheckFont(ControlCenterWindow window, DesktopFontInfo font)
    {
        var labels = Labels(window).ToArray();
        Require(labels.Length > 0, "No actual application name labels were rendered.");
        foreach (var label in labels)
        {
            Require(Math.Abs(label.FontSize - font.SizeDip) < .01, "An application name ignored the system font size.");
            Require(label.FontFamily.Source.Equals(font.FamilyName, StringComparison.OrdinalIgnoreCase), "An application name ignored the system font family.");
            Require(label.FontWeight.ToOpenTypeWeight() == font.Weight, "An application name ignored the system font weight.");
            Require((label.FontStyle == FontStyles.Italic) == font.Italic, "An application name ignored the system italic style.");
        }
    }

    private static void CheckManualFont(ControlCenterWindow window, double size)
    {
        var labels = Labels(window).ToArray();
        Require(labels.Length > 0, "No actual application labels were rendered.");
        foreach (var label in labels)
        {
            Require(Math.Abs(label.FontSize - size) < .01, "Manual font size did not reach an application label.");
            Require(label.FontWeight.ToOpenTypeWeight() >= 700, "Manual bold did not reach an application label.");
        }
    }

    private static void CheckInterfaceFont(ControlCenterWindow window, DesktopFontInfo font)
    {
        var navigation = Descendants<TextBlock>(window).Where(text => Equals(text.Tag, "NavigationLabel")).ToArray();
        Require(navigation.Length >= 3 && navigation.Count(label => label.Text == "应用库") == 1, "The unified navigation labels were not rendered.");
        foreach (var label in navigation)
        {
            var expected = Equals(Ancestor<Button>(label)?.Tag, "Navigation:Launchpad") ? Math.Max(600, font.Weight) : font.Weight;
            Require(label.FontWeight.ToOpenTypeWeight() == expected, "Navigation ignored the active font weight.");
            Require(label.FontFamily.Source == font.FamilyName, "Navigation ignored the active font family.");
            Require(label.FontSize <= 15 && label.ActualWidth < 110, "Name font size expanded or clipped the navigation.");
        }
        var heading = Descendants<TextBlock>(window).Single(text => Equals(text.Tag, "PageHeading"));
        Require(heading.FontWeight.ToOpenTypeWeight() == Math.Max(600, font.Weight), "Page heading ignored font weight.");
        var all = Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用"));
        Require(Descendants<TextBlock>(all).First().FontWeight.ToOpenTypeWeight() == Math.Max(600, font.Weight), "Selected category ignored font weight.");
        var unselected = Descendants<Button>(window).Single(button => Equals(button.Tag, "个人栏目"));
        Require(Descendants<TextBlock>(unselected).First().FontWeight.ToOpenTypeWeight() == font.Weight, "Category label ignored the active font weight.");
    }

    private static void CheckPrimaryTextColor(ControlCenterWindow window, Color expected)
    {
        var primary = Descendants<TextBlock>(window).Where(text => Equals(text.Tag, "NavigationLabel") || Equals(text.Tag, "PageHeading") || Equals(text.Tag, "ApplicationName") || Equals(text.Tag, "DesktopItemName"))
            .Concat(Descendants<Button>(window).Where(button => Equals(button.Tag, "全部应用") || Equals(button.Tag, "个人栏目")).Select(button => Descendants<TextBlock>(button).First())).ToArray();
        Require(primary.Length >= 8, "Primary labels are missing from the actual visual tree.");
        Require(primary.All(text => text.Foreground is SolidColorBrush brush && brush.Color == expected), "A primary interface label ignored custom text color.");
    }

    private static void CheckLabelBounds(ControlCenterWindow window)
    {
        var tiles = Descendants<Button>(window).Where(t => (t.DataContext is AppTileViewModel or DesktopItem) && t.IsVisible).ToArray();
        Require(tiles.Length > 0, "No application tiles were rendered.");
        foreach (var tile in tiles)
        {
            var name = Descendants<TextBlock>(tile).Single(t => Equals(t.Tag, "ApplicationName") || Equals(t.Tag, "DesktopItemName"));
            Require(name.ActualHeight + .5 >= name.LineHeight * 2, "The name area cannot hold two text lines.");
            var position = name.TranslatePoint(new Point(0, 0), tile);
            Require(position.Y >= -.5 && position.Y + name.ActualHeight <= tile.ActualHeight + .5, "The name extends outside the tile vertically.");
        }
        var grid = ApplicationGrid(window);
        var scroll = Ancestor<ScrollViewer>(grid);
        Require(scroll != null, "The application grid has no viewport.");
        Require(grid.ActualHeight <= scroll!.ViewportHeight + .5, "The selected complete rows exceed the visible viewport.");
    }

    private static Snapshot Inspect(ControlCenterWindow window, string name)
    {
        var labels = Labels(window).ToArray();
        Require(labels.Length > 0, "No application names available for snapshot " + name);
        var grid = ApplicationGrid(window);
        var label = labels[0];
        var result = new Snapshot(name, labels.Length, grid.Columns, label.FontFamily.Source, label.FontSize,
            label.FontWeight.ToOpenTypeWeight(), label.ActualHeight, label.LineHeight, grid.ActualWidth, grid.ActualHeight);
        Snapshots.Add(result); return result;
    }

    private static UniformGrid ApplicationGrid(DependencyObject window) => Descendants<UniformGrid>(window)
        .Single(g => g.IsVisible && g.Children.OfType<Button>().Any(b => b.DataContext is AppTileViewModel or DesktopItem));
    private static IEnumerable<TextBlock> Labels(DependencyObject parent) => Descendants<TextBlock>(parent)
        .Where(t => t.IsVisible && (Equals(t.Tag, "ApplicationName") || Equals(t.Tag, "DesktopItemName")));
    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task Frame()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static async Task WaitForDesktop(ControlCenterWindow window)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Frame();
            if (Descendants<Button>(window).Any(button => button.DataContext is DesktopItem)) return;
        }
        throw new InvalidOperationException("Desktop fixture did not finish rendering.");
    }
    private static AnimationClock? CaretClock(ControlCenterWindow window)
    {
        var element = Descendants<UIElement>(window).FirstOrDefault(element => element.GetType().FullName == "System.Windows.Documents.CaretElement");
        var field = element?.GetType().GetField("_blinkAnimationClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Require(field?.FieldType == typeof(AnimationClock), "The bundled WPF caret clock compatibility contract changed.");
        return field!.GetValue(element) as AnimationClock;
    }
    private static async Task<AnimationClock> WaitForStoppedCaret(ControlCenterWindow window, TextBox search)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await Frame();
            if (search.IsKeyboardFocused && CaretClock(window) is { CurrentState: ClockState.Stopped } clock) return clock;
        }
        throw new InvalidOperationException("The focused native caret root clock did not settle to Stopped.");
    }
    private static void CheckNativeCaret(IntPtr expectedWindow, string scenario)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        Require(GetGUIThreadInfo(GetCurrentThreadId(), ref info), "Native GUI thread information could not be read.");
        Require(info.Caret == expectedWindow, "The native caret window was lost.");
        Require(info.CaretRect.Bottom > info.CaretRect.Top && info.CaretRect.Right >= info.CaretRect.Left, "The native caret rectangle was lost.");
        NativeCaretChecks.Add(new { scenario, caretWindowMatches = info.Caret == expectedWindow,
            caretWidth = info.CaretRect.Right - info.CaretRect.Left, caretHeight = info.CaretRect.Bottom - info.CaretRect.Top,
            systemBlinkMilliseconds = GetCaretBlinkTime() });
    }
    private static async Task<Button> WaitForIcon(ControlCenterWindow window, string id, Func<BitmapSource?, bool> ready)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Frame();
            var tile = Descendants<Button>(window).FirstOrDefault(button => button.IsVisible && button.DataContext is AppTileViewModel vm && vm.Entry.Id == id);
            if (tile?.DataContext is AppTileViewModel model && ready(model.Icon)) return tile;
        }
        throw new InvalidOperationException("The visible tile did not receive the expected icon.");
    }
    private static byte[] IconBytes(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }
    private static byte[] FileHash(string path)
    {
        using var stream = File.OpenRead(path); return System.Security.Cryptography.SHA256.HashData(stream);
    }
    private static async Task TestAsync(string name, Func<Task> action)
    {
        try { await action(); Tests.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Tests.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Test(string name, Action action)
    {
        try { action(); Tests.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Tests.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Require(bool condition, string explanation) { if (!condition) throw new InvalidOperationException(explanation); }
    private static string? Argument(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enabled);
    [DllImport("user32.dll")] private static extern uint GetCaretBlinkTime();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    private sealed record TestResult(string Name, bool Passed, string? Error);
    private sealed record Snapshot(string Name, int VisibleTileCount, int Columns, string Family, double SizeDip, int Weight,
        double NameHeight, double LineHeight, double GridWidth, double GridHeight);
}
