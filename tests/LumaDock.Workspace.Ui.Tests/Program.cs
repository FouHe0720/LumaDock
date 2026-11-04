using System.Diagnostics;
using System.IO;
using System.Reflection;
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
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Workspace.Ui.Tests;

/// <summary>Own-window native contracts and rendered workspace layout; never moves user windows or files.</summary>
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread] private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests/workspace-ui-results.json");
        var checks = new List<object>(); var errors = new List<string>(); var observations = new Dictionary<string, object>(); var exit = 1;
        var root = Path.GetFullPath(Path.Combine("work", "workspace-ui-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); Log.DirectoryPath = Path.Combine(root, "logs");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { checks.Add(new { name, passed = false }); errors.Add(error.GetBaseException().Message); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
        }
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.GetType().Name); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? controller = null; ControlCenterWindow? window = null; DockWindow? dock = null; Window? cover = null, fixedWindow = null;
            try
            {
                controller = new AppController(Path.Combine(root, "data"), null);
                controller.State.Settings.Animations = false; controller.State.Settings.GlassMotion = false;
                controller.State.Settings.FollowSystemIconFont = false; controller.State.Settings.NameFontFamily = "Microsoft YaHei UI";
                controller.State.Settings.NameFontSize = 15; controller.State.Settings.Columns = 6; controller.State.Settings.LiveGlass = true;
                controller.State.Settings.LiveGlassStyle = "Clear";
                for (var index = 0; index < 32; index++)
                {
                    var path = Path.Combine(root, $"Workspace {index:00}.url"); File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/workspace/" + index + "\r\n");
                    controller.State.Apps.Add(new AppEntry { Id = "workspace-" + index, Name = "布局示例应用 " + index.ToString("00"), LaunchPath = path, Source = "Manual", LaunchUri = "https://example.com/workspace/" + index });
                }
                controller.ApplySettings();
                var desktopRoot = Directory.CreateDirectory(Path.Combine(root, "desktop")).FullName;
                Directory.CreateDirectory(Path.Combine(desktopRoot, "资料")); File.WriteAllText(Path.Combine(desktopRoot, "随手记.txt"), "Owned fixture");
                window = new ControlCenterWindow(controller, desktopFiles: new DesktopFileService(desktopRoot, ""))
                { Width = 1120, Height = 760, Left = 150, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                window.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(window).Handle, false);
                window.Show(); await Frame();
                var handle = new WindowInteropHelper(window).Handle;
                Check("The brand artwork is centered on the actual full window", () =>
                {
                    var logo = Descendants(window).OfType<Image>().Single(element => Equals(element.Tag, "BrandLogo"));
                    var point = logo.TranslatePoint(new Point(), window);
                    Require(logo.Source != null && logo.ActualWidth >= 46 && Math.Abs(point.X + logo.ActualWidth / 2 - window.ActualWidth / 2) <= 1.5, "Brand artwork was missing or aligned to the content column instead of the whole window.");
                });
                Check("The brand line is centered beneath the artwork and remains readable", () =>
                {
                    var logo = Descendants(window).OfType<Image>().Single(element => Equals(element.Tag, "BrandLogo"));
                    var tagline = Descendants(window).OfType<TextBlock>().Single(element => Equals(element.Tag, "BrandTagline"));
                    var line = Descendants(window).OfType<FrameworkElement>().Single(element => Equals(element.Tag, "BrandLine"));
                    var p = tagline.TranslatePoint(new Point(), window); var lp = logo.TranslatePoint(new Point(), window); var bp = line.TranslatePoint(new Point(), window);
                    Require(tagline.Text == "把整个桌面，变成你的主场。" && tagline.FontSize >= 22 && p.Y > lp.Y + logo.ActualHeight - 1 && p.Y + tagline.ActualHeight < 113, "The project introduction is missing, undersized, or not below the artwork.");
                    Require(Math.Abs(p.X + tagline.ActualWidth / 2 - window.ActualWidth / 2) <= 1.5, "The separate project introduction is not centered.");
                    Require(Math.Abs(bp.X + line.ActualWidth / 2 - window.ActualWidth / 2) <= 1.5 && bp.X >= 119 && bp.X + line.ActualWidth <= window.ActualWidth - 119, "The brand line overlaps window controls or is not centered.");
                });
                Check("The normal workspace retains three complete rows of six application cards", () =>
                {
                    var grid = AppGrid(window); Require(grid.Columns == 6 && Cards(window).Length == 18, "1120 by 760 does not show eighteen cards in six columns."); CheckTileBounds(window);
                });
                Check("Sidebar navigation and the explained quick launcher remain available", () =>
                {
                    foreach (var section in new[] { "Launchpad", "Desktop", "Settings", "About" }) Require(Descendants(window).OfType<Button>().Count(button => Equals(button.Tag, "Navigation:" + section)) == 1, "A navigation entry was lost or duplicated.");
                    Require(Descendants(window).OfType<Button>().Any(button => Equals(button.Tag, "QuickLaunchHint")), "The explained quick-launch action was lost.");
                });
                Check("Native edge hit testing exposes all four resizing sides and corners", () =>
                {
                    Require(GetWindowRect(handle, out var r), "No native workspace bounds."); var edge = Math.Max(2, (int)Math.Round(3 * Dpi(handle))); var middleX = (r.Left + r.Right) / 2; var middleY = (r.Top + r.Bottom) / 2;
                    var points = new[] { (r.Left + edge, middleY, 10), (r.Right - edge, middleY, 11), (middleX, r.Top + edge, 12), (middleX, r.Bottom - edge, 15), (r.Left + edge, r.Top + edge, 13), (r.Right - edge, r.Top + edge, 14), (r.Left + edge, r.Bottom - edge, 16), (r.Right - edge, r.Bottom - edge, 17) };
                    var actual = points.Select(point => new { x = point.Item1, y = point.Item2, expected = point.Item3, actual = Hit(handle, point.Item1, point.Item2) }).ToArray(); observations["nativeResizeHits"] = actual;
                    Require(actual.All(point => point.actual == point.expected), "A native side/corner does not return its Windows resize hit code.");
                    Require(Hit(handle, middleX, middleY) is not (>= 10 and <= 17), "The workspace center is incorrectly exposed as a resize border.");
                });
                Check("Native tracking limits preserve readable minimum window dimensions", () =>
                {
                    var info = GetTracking(handle); observations["minimumTrackingSize"] = new { info.MinTrackSize.X, info.MinTrackSize.Y };
                    Require(info.MinTrackSize.X >= Math.Floor(window.MinWidth * Dpi(handle)) && info.MinTrackSize.Y >= Math.Floor(window.MinHeight * Dpi(handle)), "Windows can resize the workspace below its WPF minimum.");
                });
                var maximize = Descendants(window).OfType<Button>().Single(button => Equals(button.Tag, "MaximizeButton"));
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Check("The titlebar maximize action uses the native maximized window state", () =>
                {
                    Require(window.WindowState == WindowState.Maximized, "The maximize button did not maximize the real WPF window.");
                    Require(GetWindowRect(handle, out var r) && Hit(handle, r.Left + 3, (r.Top + r.Bottom) / 2) is not (>= 10 and <= 17), "A maximized window still exposes a resize border.");
                });
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Check("The same titlebar action restores the previous workspace dimensions", () => Require(window.WindowState == WindowState.Normal && Math.Abs(window.ActualWidth - 1120) < 3 && Math.Abs(window.ActualHeight - 760) < 3, "Restore did not retain the pre-maximize size."));
                window.Width = 940; window.Height = 620; await Frame();
                Check("A smaller workspace reduces density without clipping card labels", () => { Require(Cards(window).Length > 0 && Cards(window).Length < 18, "The small window did not adapt its card density."); CheckTileBounds(window); });
                window.Width = 1440; window.Height = 960; await Frame();
                Check("Enlarging the workspace uses the new content area", () => { Require(Cards(window).Length >= 18, "A larger workspace did not regain its card capacity."); CheckTileBounds(window); });
                controller.State.Settings.NameFontSize = 32; controller.State.Settings.NameFontBold = true; controller.ApplySettings(); window.Navigate("Launchpad"); await Frame();
                Check("Large personalized labels retain two complete lines in the new layout", () => { CheckTileBounds(window); Require(Descendants(window).OfType<TextBlock>().Where(t => Equals(t.Tag, "ApplicationName")).All(t => Math.Abs(t.FontSize - 32) < .01 && t.FontWeight.ToOpenTypeWeight() >= 700), "The redesign ignored personalized typography."); });
                window.Navigate("Settings"); await Frame();
                var placement = Descendants(window).OfType<ComboBox>().Single(combo => Equals(combo.Tag, "Setting:摆放方式"));
                placement.SelectedValue = "Free"; await Frame();
                Check("The placement preference switches to free positioning and saves the actual choice", () => Require(new StateStore(Path.Combine(root, "data")).Load().Settings.DockPlacement == "Free", "The visible placement control did not save free mode."));
                Descendants(window).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "恢复位置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Frame();
                Check("The settings restore action returns the Dock to bottom-centered edge placement", () => { var saved = new StateStore(Path.Combine(root, "data")).Load().Settings; Require(saved.DockPlacement == "Edge" && saved.Position == "Bottom" && saved.DockFreeX == .5 && saved.DockFreeY == 1, "The visible restore action did not reset placement state."); });
                window.ClosePermanently(); window = null;

                fixedWindow = new Window { Width = 300, Height = 180, Left = 100, Top = 100, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false };
                WindowBackdrop.Attach(fixedWindow, () => true, () => false, () => true); fixedWindow.Show(); await Frame();
                Check("Non-resizable glass dialogs never inherit the workspace resize border", () =>
                {
                    var h = new WindowInteropHelper(fixedWindow).Handle; Require(GetWindowRect(h, out var r), "No fixed dialog bounds."); Require(Hit(h, r.Left + 3, (r.Top + r.Bottom) / 2) is not (>= 10 and <= 17), "The backdrop made a fixed dialog resizable.");
                });
                fixedWindow.Close(); fixedWindow = null;
                GeometryChecks(Check);

                controller.State.Settings.NameFontSize = 15; controller.State.Settings.NameFontBold = false;
                controller.State.PinnedIds = controller.State.Apps.Take(3).Select(a => a.Id).ToList(); controller.ApplySettings();
                dock = new DockWindow(controller) { ShowActivated = false }; dock.Show(); await Frame();
                var dockHandle = new WindowInteropHelper(dock).Handle;
                Check("The Dock grip requests native movement while app buttons keep their click-and-sort input", () =>
                {
                    var grip = (FrameworkElement)typeof(DockWindow).GetField("positionGrip", Private)!.GetValue(dock)!;
                    var gp = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                    Require(Hit(dockHandle, (int)gp.X, (int)gp.Y) == 2, "The physical grip is not a native drag surface.");
                    var button = Descendants(dock).OfType<Button>().First(element => element.Tag is AppEntry);
                    var bp = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    Require(Hit(dockHandle, (int)bp.X, (int)bp.Y) != 2, "Application input is intercepted by whole-Dock movement.");
                    Require(SendMessage(dockHandle, 0x21, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3 && (GetWindowLongPtr(dockHandle, -20).ToInt64() & 0x08000000) != 0, "Dock movement no longer preserves no-activate semantics.");
                });
                var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.IsPrimary) ?? MonitorService.GetMonitors().First();
                var work = new DockWorkArea(monitor.WorkLeft, monitor.WorkTop, monitor.WorkWidth, monitor.WorkHeight);
                GetWindowRect(dockHandle, out var dockRect);
                var commandFrame = new DockFrame(work.Left + (int)(work.Width * .18), work.Top + (int)(work.Height * .24), dockRect.Right - dockRect.Left, dockRect.Bottom - dockRect.Top);
                var commit = typeof(DockWindow).GetMethod("CommitFreePlacement", Private);
                Check("The drag completion command exists independently of icon reordering", () => Require(commit != null, "There is no free-placement completion command."));
                if (commit != null)
                {
                    commit.Invoke(dock, [commandFrame, monitor]); await Frame();
                    var persisted = new StateStore(Path.Combine(root, "data")).Load();
                    Check("Dropping the Dock saves its monitor and normalized free position", () =>
                    {
                        Require(persisted.Settings.DockPlacement == "Free" && persisted.Settings.MonitorDevice == monitor.DeviceName && double.IsFinite(persisted.Settings.DockFreeX) && double.IsFinite(persisted.Settings.DockFreeY), "The completion command did not persist an actual monitor anchor.");
                        Require(persisted.Settings.DockFreeX is >= 0 and <= 1 && persisted.Settings.DockFreeY is >= 0 and <= 1, "The persisted position escapes its normalized range.");
                    });
                    controller.State.Settings.AutoHide = true; controller.Save(); dock.UpdateSettings(); await Task.Delay(1250); await Frame();
                    Check("Free placement suspends automatic hiding without rewriting the saved auto-hide preference", () =>
                    {
                        Require(controller.State.Settings.AutoHide && new StateStore(Path.Combine(root, "data")).Load().Settings.AutoHide && !(bool)typeof(DockWindow).GetField("tucked", Private)!.GetValue(dock)!, "A freely placed Dock tucked away or erased the user's auto-hide choice.");
                        Require(!((DispatcherTimer)typeof(DockWindow).GetField("hideDelay", Private)!.GetValue(dock)!).IsEnabled, "Free placement kept the hide timer running.");
                    });
                    var expectedAnchor = new DockAnchor(controller.State.Settings.DockFreeX, controller.State.Settings.DockFreeY);
                    dock.Rebuild(); await Frame();
                    Check("A Dock rebuild retains its free anchor and remains on the selected work area", () => CheckDockAnchor(dockHandle, work, expectedAnchor));
                    controller.State.Settings.IconSize = 72; dock.UpdateSettings(); await Frame();
                    Check("Changing icon size keeps the free Dock reachable at its saved anchor", () => CheckDockAnchor(dockHandle, work, expectedAnchor));
                    controller.State.Settings.MonitorDevice = "LumaDock fixture monitor that is disconnected"; dock.Reposition(); await Frame();
                    Check("An unavailable saved monitor falls back to the real primary work area", () => CheckDockBounds(dockHandle, work));
                    Check("Free placement and relayout preserve the ordinary non-topmost Dock layer", () => Require(!dock.Topmost && (GetWindowLongPtr(dockHandle, -20).ToInt64() & 8) == 0, "A moved Dock became native topmost."));
                    cover = new Window { Title = "LumaDock workspace layer fixture", Width = 220, Height = 110, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = Brushes.DimGray, Left = 200, Top = 180 };
                    cover.Show(); await Frame(); var coverHandle = new WindowInteropHelper(cover).Handle;
                    cover.Activate(); SetForegroundWindow(coverHandle); await Frame();
                    var coverIsForeground = GetForegroundWindow() == coverHandle; observations["foregroundCoverVerified"] = coverIsForeground;
                    dock.Reposition(); await Frame();
                    if (coverIsForeground) Check("Repositioning a free Dock preserves a genuinely foreground ordinary cover above it", () => Require(IsAbove(coverHandle, dockHandle) && GetForegroundWindow() == coverHandle, "Dock movement raised it above the foreground cover or stole its focus."));
                    var restore = typeof(DockWindow).GetMethod("RestoreBottomPlacement", Private);
                    Check("The reset command restores bottom docking without clearing application pins", () =>
                    {
                        Require(restore != null, "There is no restore-bottom placement command."); var pinIds = controller.State.PinnedIds.ToArray(); restore!.Invoke(dock, null);
                        var data = new StateStore(Path.Combine(root, "data")).Load(); Require(data.Settings.DockPlacement == "Edge" && data.Settings.Position == "Bottom" && data.PinnedIds.SequenceEqual(pinIds), "The reset command lost pins or did not persist bottom docking.");
                    });
                }
                exit = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception error) { errors.Add(error.GetBaseException().ToString()); Console.WriteLine(error); }
            finally
            {
                window?.ClosePermanently(); dock?.Close(); cover?.Close(); fixedWindow?.Close(); controller?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = exit == 0, recordedAt = DateTimeOffset.Now, checks, errors, observations, onlyOwnedWindowsModified = true, userFilesChanged = false, systemSettingsChanged = false, appLaunches = 0, actualMouseDragTested = false, interpretation = "Actual own-window native hit codes and rendered WPF geometry. Free-placement command, persistence and native bounds are exercised without physical mouse injection. Synthetic multi-monitor geometry does not certify a real mixed-DPI monitor transition." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Workspace UI: {checks.Count} groups, {errors.Count} errors"); app.Shutdown();
            }
        };
        app.Run(); return exit;
    }

    private static void GeometryChecks(Action<string, Action> check)
    {
        check("Free placement resolves normalized corners on a negative-origin monitor", () =>
        {
            var work = new DockWorkArea(-1920, -180, 1920, 1080); var first = DockPlacementGeometry.Resolve(work, 640, 130, 0, 0); var last = DockPlacementGeometry.Resolve(work, 640, 130, 1, 1);
            Require(first.X == -1920 && first.Y == -180 && last.X + last.Width == 0 && last.Y + last.Height == 900, "Negative monitor origins or travel endpoints were lost.");
        });
        check("Oversized and invalid positions remain finite and reachable on a smaller work area", () =>
        {
            var work = new DockWorkArea(2560, 0, 1280, 720); var frame = DockPlacementGeometry.Resolve(work, 4000, 2000, double.NaN, double.PositiveInfinity, 8);
            Require(frame.Width > 0 && frame.Height > 0 && frame.X >= work.Left && frame.Y >= work.Top && frame.X + frame.Width <= work.Left + work.Width && frame.Y + frame.Height <= work.Top + work.Height, "An extreme Dock frame escapes the work area.");
        });
        check("Physical placement preserves relative anchors when monitor and DPI-scaled size change", () =>
        {
            var first = DockPlacementGeometry.Resolve(new(-1920, 0, 1920, 1080), 640, 130, .2, .65);
            var anchor = DockPlacementGeometry.AnchorFor(new(-1920, 0, 1920, 1080), first);
            var moved = DockPlacementGeometry.Resolve(new(0, -1440, 2560, 1440), 960, 195, anchor.X, anchor.Y);
            Require(Math.Abs(moved.X - 320) <= 1 && Math.Abs(moved.Y - (-631)) <= 1, "A changed physical size did not preserve the relative travel anchor.");
        });
    }
    private static void CheckDockBounds(IntPtr handle, DockWorkArea work) { Require(GetWindowRect(handle, out var r), "No Dock bounds."); Require(r.Left >= work.Left - 1 && r.Top >= work.Top - 1 && r.Right <= work.Left + work.Width + 1 && r.Bottom <= work.Top + work.Height + 1, "The Dock is outside the reachable work area."); }
    private static void CheckDockAnchor(IntPtr handle, DockWorkArea work, DockAnchor expected) { CheckDockBounds(handle, work); GetWindowRect(handle, out var r); var actual = DockPlacementGeometry.AnchorFor(work, new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)); Require(Math.Abs(actual.X - expected.X) < .012 && Math.Abs(actual.Y - expected.Y) < .012, "The free position jumped after a Dock layout update."); }
    private static UniformGrid AppGrid(DependencyObject root) => Descendants(root).OfType<UniformGrid>().Single(grid => grid.Children.OfType<Button>().Any(button => button.DataContext is AppTileViewModel));
    private static Button[] Cards(DependencyObject root) => Descendants(root).OfType<Button>().Where(button => button.DataContext is AppTileViewModel).ToArray();
    private static void CheckTileBounds(ControlCenterWindow window)
    {
        var grid = AppGrid(window); var scroll = Ancestor<ScrollViewer>(grid); Require(scroll != null && grid.ActualHeight <= scroll.ViewportHeight + .5, "Complete rows exceed the real viewport.");
        foreach (var card in Cards(window)) { var label = Descendants(card).OfType<TextBlock>().Single(t => Equals(t.Tag, "ApplicationName")); var p = label.TranslatePoint(new Point(), card); Require(label.ActualHeight + .5 >= label.LineHeight * 2 && p.Y >= -.5 && p.Y + label.ActualHeight <= card.ActualHeight + .5, "A two-line application name is clipped."); }
    }
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(120); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item; }
    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject { for (var p = VisualTreeHelper.GetParent(node); p != null; p = VisualTreeHelper.GetParent(p)) if (p is T result) return result; return null; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Dpi(IntPtr handle) => Math.Max(96, GetDpiForWindow(handle)) / 96d;
    private static int Hit(IntPtr handle, int x, int y) => (int)SendMessage(handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)))));
    private static MinMaxInfo GetTracking(IntPtr handle) { var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>()); try { Marshal.StructureToPtr(new MinMaxInfo(), pointer, false); SendMessage(handle, 0x24, IntPtr.Zero, pointer); return Marshal.PtrToStructure<MinMaxInfo>(pointer); } finally { Marshal.FreeHGlobal(pointer); } }
    private static bool IsAbove(IntPtr above, IntPtr below) { for (var h = GetWindow(below, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h == above) return true; return false; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr handle, bool enable);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
}
