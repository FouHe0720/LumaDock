using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Ui.Tests;

/// <summary>Real Windows WPF lifecycle test. No tray, global hotkey, app launches or system changes.</summary>
internal static class Program
{
    private static readonly List<string> Errors = [];
    private static int exitCode = 1;
    private static int rendered, shown, hidden, closed, searches, cycles;
    private static bool animationReopenChecked;
    private static readonly List<object> samples = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var reportPath = Path.GetFullPath(Argument(args, "--report") ?? "artifacts/tests/ui-results.json");
        var data = Path.GetFullPath(Argument(args, "--data-dir") ?? Path.Combine("work", "ui-tests", "run-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(data); Log.DirectoryPath = Path.Combine(data, "logs");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.GetType().Name); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Errors.Add(e.Exception.GetType().Name); e.SetObserved(); };
        app.Startup += async (_, _) =>
        {
            var clock = Stopwatch.StartNew();
            AppController? controller = null;
            ControlCenterWindow? window = null;
            long heapBefore = 0, heapAfter = 0;
            int realApps = 0;
            try
            {
                controller = new AppController(data, null);
                controller.State.Settings.Animations = false;
                controller.State.Settings.CloseToTray = true;
                await controller.RefreshAppsAsync(); // Read-only scan, one time; saves only the isolated sandbox.
                realApps = ApplicationPresentation.Group(controller.State.Apps.Where(entry => !entry.Hidden && entry.IsAvailable)).Count;
                Require(realApps > 0, "DiscoveryEmpty");
                Require(controller.State.Categories.Count == 0 && controller.State.Apps.All(entry => entry.Category.Length == 0), "FreshProfileHasAutomaticColumns");
                var fixtureDesktop = Directory.CreateDirectory(Path.Combine(data, "fixture-desktop")).FullName;
                Directory.CreateDirectory(Path.Combine(fixtureDesktop, "桌面生命周期文件夹"));
                File.WriteAllText(Path.Combine(fixtureDesktop, "桌面生命周期文档.txt"), "Owned lifecycle fixture");
                File.WriteAllText(Path.Combine(fixtureDesktop, "桌面生命周期快捷方式.url"), "[InternetShortcut]\r\nURL=https://example.com/\r\n");
                var desktopFiles = new DesktopFileService(fixtureDesktop, "");
                window = NewWindow(controller, desktopFiles);
                window.Show(); await Frame();
                Require(window.IsVisible, "InitialShow");
                window.Navigate("Settings"); await Frame();
                window.Navigate("Desktop"); await WaitForDesktop(window);
                Require(window.CurrentSection == "Desktop", "DesktopViewNotRestored");
                Require(Descendants<Button>(window).Count(button => Equals(button.Tag, "Navigation:Launchpad")) == 1
                    && Descendants<Button>(window).Count(button => Equals(button.Tag, "Navigation:Desktop")) == 1 && !Descendants<Button>(window).Any(button => Equals(button.Tag, "Dock 已固定")), "DuplicateDefaultNavigation");
                var desktopPaths = Descendants<Button>(window).Select(button => button.DataContext).OfType<DesktopItem>().Select(item => item.Path).ToArray();
                Require(desktopPaths.Length == 3 && desktopPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == desktopPaths.Length && desktopPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(desktopFiles.Scan().Items.Select(item => item.Path)), "DuplicateOrMissingDesktopItem");
                window.Navigate("Launchpad"); await Frame();
                var firstPageIds = Descendants<Button>(window).Select(button => button.DataContext).OfType<AppTileViewModel>().Select(model => model.Entry.Id).ToArray();
                Require(firstPageIds.Length > 0 && firstPageIds.Distinct().Count() == firstPageIds.Length, "DuplicateVisibleApplication");
                window.HideSmoothly(); await Frame();
                heapBefore = GC.GetTotalMemory(true); samples.Add(Sample("warmBeforeCycles"));
                var target = controller.State.Apps.First(a => !a.Hidden && a.IsAvailable).Name;
                for (var cycle = 0; cycle < 100; cycle++)
                {
                    window.Navigate("Launchpad");
                    await Frame(); Require(window.IsVisible, "CycleShow");
                    var search = Descendants<TextBox>(window).FirstOrDefault();
                    Require(search != null, "SearchControlMissing");
                    search!.Text = target; await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var tiles = Descendants<Button>(window).Select(b => b.DataContext).OfType<AppTileViewModel>().ToList();
                    Require(tiles.Count > 0 && tiles.All(t => t.Name.Contains(target, StringComparison.CurrentCultureIgnoreCase)), "SearchResultMismatch");
                    searches++; search.Text = "";
                    window.Navigate(cycle % 2 == 0 ? "Settings" : "Desktop");
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.HideSmoothly(); await Frame(); Require(!window.IsVisible, "CycleHide");
                    cycles++;
                    if (cycles is 25 or 50 or 100) samples.Add(Sample("cycle" + cycles));
                }
                // A second activation during the closing transition must keep the
                // window visible. Acrylic must also retain a non-layered HWND.
                controller.State.Settings.Animations = true;
                window.Navigate("Launchpad"); await Frame();
                Require(window.Opacity == 1 && (GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64() & 0x80000) == 0, "AnimatedWindowBecameLayered");
                window.HideSmoothly(); await Task.Delay(30);
                window.Navigate("Launchpad"); await Task.Delay(220); await Frame();
                Require(window.IsVisible, "ReactivationLostToPendingHide");
                window.HideSmoothly(); await Task.Delay(220); await Frame();
                Require(!window.IsVisible, "AnimatedHideDidNotFinish");
                animationReopenChecked = true;
                controller.State.Settings.Animations = false;
                window.ClosePermanently(); window = null; await Frame();
                // A closed WPF Window cannot be reopened. Exercise new window recreation three times.
                for (var index = 0; index < 3; index++)
                {
                    window = NewWindow(controller, desktopFiles); window.Show(); await Frame();
                    Require(window.IsVisible, "RecreatedShow"); window.ClosePermanently(); window = null; await Frame();
                }
                controller.Dispose(); controller = null;
                await Task.Delay(200); await Frame();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                heapAfter = GC.GetTotalMemory(true); samples.Add(Sample("afterDisposeAndGc"));
                Require(cycles == 100 && searches == 100 && rendered > 0 && shown >= 104 && hidden >= 101 && closed == 4, "LifecycleEvidenceMissing");
                Require(Errors.Count == 0, "DispatcherExceptionsObserved");
                exitCode = 0;
            }
            catch (Exception error) { Errors.Add(error.GetType().Name + ":" + error.Message); }
            finally
            {
                try { window?.ClosePermanently(); controller?.Dispose(); } catch (Exception error) { Errors.Add(error.GetType().Name); exitCode = 1; }
                var report = new
                {
                    passed = exitCode == 0, platform = "Windows WPF x64", executedAtUtc = DateTimeOffset.UtcNow,
                    durationMs = clock.Elapsed.TotalMilliseconds, realApplicationCount = realApps,
                    cycles, searches, contentRenderedEvents = rendered, visibleTrueEvents = shown, visibleFalseEvents = hidden, closedEvents = closed,
                    animationsEnabledDuring100Cycles = false, animationReopenChecked, showActivatedRequested = false, nativeWindowsDisabledForFocusSafety = true,
                    systemSettingsChanged = false, appLaunches = 0, controllerStartCalled = false,
                    managedHeapBeforeCyclesBytes = heapBefore, managedHeapAfterDisposeAndGcBytes = heapAfter,
                    managedHeapDeltaBytes = heapAfter - heapBefore, processSamples = samples, errors = Errors,
                    interpretation = "100 short UI open/search/navigate/hide cycles plus three window recreations. Forced-GC managed heap is retained heap, not private working set. This is not proof of leak freedom, game compatibility or overnight stability."
                };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"UI cycles={cycles}, searches={searches}, rendered={rendered}, close={closed}, passed={exitCode == 0}");
                app.Shutdown(exitCode);
            }
        };
        app.Run(); return exitCode;
    }

    private static ControlCenterWindow NewWindow(AppController controller, DesktopFileService desktopFiles)
    {
        var window = new ControlCenterWindow(controller, desktopFiles: desktopFiles) { ShowActivated = false, ShowInTaskbar = false };
        // Disable only this test HWND: prevents Navigate.Activate from interrupting the user's foreground app.
        window.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(window).Handle, false);
        window.ContentRendered += (_, _) => rendered++;
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) shown++; else hidden++; };
        window.Closed += (_, _) => closed++;
        return window;
    }
    private static async Task Frame()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(20); // Allow real WPF composition/visibility work rather than testing only setters.
    }
    private static async Task WaitForDesktop(ControlCenterWindow window)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Frame();
            if (Descendants<Button>(window).Count(button => button.DataContext is DesktopItem) == 3) return;
        }
        throw new InvalidOperationException("DesktopFixtureDidNotRender");
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
    private static object Sample(string stage)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { stage, privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64, handles = process.HandleCount, managedHeapBytes = GC.GetTotalMemory(false) };
    }
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static string? Argument(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enabled);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
