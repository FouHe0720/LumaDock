using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Typography.Ui.Tests;

/// <summary>Actual WPF label/layout checks; no app launching, tray, hotkeys or OS setting changes.</summary>
internal static class Program
{
    private static readonly List<TestResult> Tests = [];
    private static readonly List<Snapshot> Snapshots = [];
    private static readonly List<string> Errors = [];
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
                window = new ControlCenterWindow(controller) { ShowActivated = false, ShowInTaskbar = false };
                window.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(window).Handle, false);
                window.Show(); await Frame();

                var defaultSnapshot = Inspect(window, "system-launchpad");
                Test("Launchpad names use the actual Windows icon-title font by default", () =>
                {
                    CheckFont(window, nativeFont);
                    CheckLabelBounds(window);
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
                    var expected = controller.State.Apps.Count(app => !app.Hidden && (app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || app.LaunchPath.Contains(query, StringComparison.OrdinalIgnoreCase)));
                    var all = Descendants<Button>(window).Single(button => Equals(button.Tag, "全部应用"));
                    Require(System.Windows.Automation.AutomationProperties.GetHelpText(all) == $"{expected} 个应用", "Category count does not match the actual search results.");
                    Require(Descendants<TextBlock>(all).Any(text => text.Text == expected.ToString()), "The visible count did not update.");
                });
                search.Clear(); await Frame();

                controller.State.Settings.FollowSystemIconFont = false;
                controller.State.Settings.NameFontSize = 28;
                controller.State.Settings.NameFontBold = true;
                controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                var mediumSnapshot = Inspect(window, "manual-28-bold-launchpad");
                Test("Manual 28 DIP bold names retain two lines inside their tiles", () =>
                {
                    CheckManualFont(window, 28);
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

                window.Navigate("Desktop"); await Frame();
                var desktopSnapshot = Inspect(window, "manual-40-bold-desktop");
                Test("Desktop shortcut names receive the same font and adaptive layout", () =>
                {
                    Require(desktopSnapshot.VisibleTileCount > 0, "No desktop shortcuts were available for this integration check.");
                    CheckManualFont(window, 40);
                    CheckLabelBounds(window);
                });

                controller.State.Settings.FollowSystemIconFont = true;
                controller.ApplySettings(); window.Navigate("Desktop"); await Frame();
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
                    var heading = Descendants<TextBlock>(window).Single(text => text.Text == "名称字体");
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
                window.Navigate("Launchpad"); await Frame();
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
                    controllerStartCalled = false, nativeWindowsDisabledForFocusSafety = true,
                    interpretation = "Checks actual WPF labels, local font preferences, category counts and bounded interactive motion using isolated application data. Windows global typography is not modified."
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

    private static void CheckLabelBounds(ControlCenterWindow window)
    {
        var tiles = Descendants<Button>(window).Where(t => t.DataContext is AppTileViewModel && t.IsVisible).ToArray();
        Require(tiles.Length > 0, "No application tiles were rendered.");
        foreach (var tile in tiles)
        {
            var name = Descendants<TextBlock>(tile).Single(t => Equals(t.Tag, "ApplicationName"));
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
        .Single(g => g.IsVisible && g.Children.OfType<Button>().Any(b => b.DataContext is AppTileViewModel));
    private static IEnumerable<TextBlock> Labels(DependencyObject parent) => Descendants<TextBlock>(parent)
        .Where(t => t.IsVisible && Equals(t.Tag, "ApplicationName"));
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
    private static void Test(string name, Action action)
    {
        try { action(); Tests.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Tests.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Require(bool condition, string explanation) { if (!condition) throw new InvalidOperationException(explanation); }
    private static string? Argument(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enabled);
    private sealed record TestResult(string Name, bool Passed, string? Error);
    private sealed record Snapshot(string Name, int VisibleTileCount, int Columns, string Family, double SizeDip, int Weight,
        double NameHeight, double LineHeight, double GridWidth, double GridHeight);
}
