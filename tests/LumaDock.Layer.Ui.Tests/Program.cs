using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Layer.Ui.Tests;

/// <summary>Real owned HWNDs and taskbar hit testing, without mouse injection or modifying Explorer.</summary>
internal static class Program
{
    private static readonly List<object> checks = [];
    private static readonly List<string> errors = [];
    private static readonly Dictionary<string, object> observations = [];
    private static int exit = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--foreground-cover")) return ForegroundCover(args);
        var reportIndex = Array.IndexOf(args, "--report");
        var report = Path.GetFullPath(reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : "artifacts/tests/layer-ui-results.json");
        var interactive = args.Contains("--interactive-desktop");
        var root = Path.GetFullPath(Path.Combine("work", "layer-ui-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); Log.DirectoryPath = Path.Combine(root, "logs");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? controller = null; DockWindow? dock = null; ControlCenterWindow? center = null;
            Window? cover = null; Window? probe = null; Process? child = null;
            try
            {
                var bar = ActualTaskbar();
                var monitor = MonitorService.GetMonitors().First(m => Intersects(new(m.Left, m.Top, m.Width, m.Height), bar));
                observations["actualTaskbarBounds"] = bar;
                observations["monitorDpi"] = Native.GetDpiForWindow(Native.FindWindow("Shell_TrayWnd", null));
                controller = new AppController(Path.Combine(root, "data"), null);
                var settings = controller.State.Settings;
                settings.IconSize = 16; settings.AutoHide = false; settings.LiveGlass = false; settings.LiquidGlass = false;
                settings.DockPlacement = "Free"; settings.DockFreeBoundsVersion = 1; settings.MonitorDevice = monitor.DeviceName;
                settings.Position = "Bottom"; settings.DockFreeX = .5; settings.DockFreeY = .5;
                if (bar.Width >= bar.Height) settings.DockFreeY = bar.Y < monitor.Top + monitor.Height / 2 ? 0 : 1;
                else settings.DockFreeX = bar.X < monitor.Left + monitor.Width / 2 ? 0 : 1;
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
                controller.State.Apps = [new AppEntry { Id = "owned-layer-fixture", Name = "Layer fixture", LaunchPath = path, ExecutablePath = path, IsAvailable = true }];
                controller.State.PinnedIds = ["owned-layer-fixture"];
                cover = new Window { Title = "LumaDock owned ordinary foreground fixture", Width = 420, Height = 220,
                    WindowStyle = WindowStyle.None, Background = Brushes.SlateGray, ShowInTaskbar = true };
                cover.Show(); await Frame();
                dock = new DockWindow(controller) { Title = "LumaDock taskbar input fixture" };
                dock.Show(); await Frame();
                var handle = new WindowInteropHelper(dock).Handle;
                var move = Named(dock, "拖动 Dock"); var resize = Named(dock, "缩放 Dock");
                Check("The actual compact Dock places both grips inside the real taskbar strip", () =>
                {
                    Require(Contains(bar, Centre(move)) && Contains(bar, Centre(resize)), "The fixture grips did not reach the actual taskbar area.");
                    Require(!dock.Topmost, "The WPF window requests permanent topmost.");
                });
                await Activate(handle);
                Check("An explicitly activated Dock automatically rises above the overlapping taskbar", () =>
                    Require(Topmost(handle), "The actual Dock's foreground observer did not elevate the overlap."));
                Check("The overlapping movement grip is the native mouse target", () =>
                    Require(Target(Centre(move)) == handle, "Windows still directs the movement grip to another HWND."));
                Check("The overlapping resize grip is the native mouse target", () =>
                    Require(Target(Centre(resize)) == handle, "Windows still directs the resize grip to another HWND."));
                observations["activatedDockOverlapHitVerified"] = true;

                var activation = typeof(DockWindow).GetMethod("PointerActivation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Missing deliberate-pointer activation adapter.");
                Check("Intentional movement and resize use foreground capture activation", () =>
                {
                    Require((int)activation.Invoke(dock, [Centre(move)])! == 1 && (int)activation.Invoke(dock, [Centre(resize)])! == 1,
                        "The manipulation grips remain on the background-only capture path.");
                });
                Check("Application clicks keep their no-activation launch path", () =>
                {
                    var button = Descendants(dock).OfType<Button>().Single(b => b.Tag is AppEntry);
                    Require((int)activation.Invoke(dock, [Centre(button)])! == 3, "An application button would activate the Dock before launch.");
                });

                cover.Show(); await Activate(new WindowInteropHelper(cover).Handle);
                Check("A same-process ordinary foreground window immediately demotes the Dock without relayout", () =>
                {
                    Require(!Topmost(handle) && Native.GetForegroundWindow() == new WindowInteropHelper(cover).Handle,
                        "The foreground event skipped this process, delayed indefinitely, or stole focus.");
                    Require(IsAbove(new WindowInteropHelper(cover).Handle, handle), "The ordinary work window is behind the Dock.");
                });
                await Activate(handle);
                Check("Returning to the activated overlapping Dock restores mouse reachability", () =>
                    Require(Topmost(handle) && Target(Centre(move)) == handle, "The overlap did not become interactive again."));
                var childReady = Path.Combine(root, "external-cover.json");
                var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.CurrentDirectory };
                if (string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    childStart.ArgumentList.Add(typeof(Program).Assembly.Location);
                foreach (var value in new[] { "--foreground-cover", childReady, Environment.ProcessId.ToString() }) childStart.ArgumentList.Add(value);
                child = Process.Start(childStart) ?? throw new InvalidOperationException("The owned external cover did not start.");
                var childWait = Stopwatch.StartNew();
                while (!File.Exists(childReady) && !child.HasExited && childWait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                Require(File.Exists(childReady), "No owned external foreground fixture handle was reported.");
                var childHandle = new IntPtr(long.Parse(File.ReadAllText(childReady)));
                Require(Native.GetWindowThreadProcessId(childHandle, out var childOwner) != 0 && childOwner == child.Id,
                    "The external foreground fixture handle did not belong to the child created here.");
                await Activate(childHandle);
                Check("A separately owned process automatically demotes the Dock without relayout", () =>
                    Require(!Topmost(handle) && Native.GetForegroundWindow() == childHandle && IsAbove(childHandle, handle),
                        "A real external foreground event left the Dock topmost or stole focus."));
                observations["ownedExternalProcessForegroundVerified"] = true;
                await Activate(handle);
                center = new ControlCenterWindow(controller);
                center.Show(); await Activate(new WindowInteropHelper(center).Handle);
                Check("The real LumaDock management window demotes its Dock without calling Reposition", () =>
                    Require(!Topmost(handle) && Native.GetForegroundWindow() == new WindowInteropHelper(center).Handle,
                        "Opening the product's own management window left its Dock topmost."));

                await Activate(handle);
                dock.Hide(); await Frame();
                Check("Hiding the actual Dock removes its transient topmost state", () =>
                    Require(!Native.IsWindowVisible(handle) && !Topmost(handle), "A hidden Dock retained its elevated state."));
                dock.Show(); await Activate(handle);
                Check("Showing and activating the overlapping Dock restores the layer observer", () =>
                    Require(Topmost(handle), "The actual Dock did not recover after hiding."));
                var edgeX = settings.DockFreeX; var edgeY = settings.DockFreeY;
                settings.DockFreeX = .5; settings.DockFreeY = .5; dock.UpdateSettings(); await Frame();
                Check("An active Dock away from the taskbar remains in the ordinary band", () =>
                    Require(!Topmost(handle), "Foreground activation promoted a Dock with no taskbar overlap."));
                settings.DockFreeX = edgeX; settings.DockFreeY = edgeY; dock.UpdateSettings(); await Activate(handle);

                if (interactive)
                {
                    var ready = report + ".desktop-ready.json";
                    Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
                    File.WriteAllText(ready, JsonSerializer.Serialize(new { processId = Environment.ProcessId, dock = handle.ToInt64(), taskbar = bar,
                        instructions = "Return to the actual Windows desktop without activating another application; no automated desktop action is performed by this test." }));
                    var until = Stopwatch.StartNew();
                    while (!GenuineShellForeground() && until.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
                    Require(GenuineShellForeground(), "No genuine Shell foreground was observed within the interactive timeout.");
                    await Frame();
                    Check("The genuine Shell foreground preserves native grip hit targets above its taskbar", () =>
                        Require(Topmost(handle) && Target(Centre(move)) == handle && Target(Centre(resize)) == handle,
                            "The genuine desktop overlap is still intercepted by the taskbar."));
                    observations["realShellForegroundVerified"] = true;
                }

                dock.Close(); await Frame();
                Check("Closing the actual Dock destroys its native interaction surface", () =>
                    Require(!Native.IsWindow(handle), "The closed Dock left a native window behind."));
                dock = null;

                probe = new Window { Title = "LumaDock owned layer lifecycle fixture", WindowStyle = WindowStyle.None,
                    Width = 220, Height = Math.Max(20, Math.Min(32, bar.Height)), Background = Brushes.DarkSlateBlue,
                    ShowInTaskbar = false, ShowActivated = false, Left = bar.X + Math.Max(0, (bar.Width - 220) / 2), Top = bar.Y + 2 };
                probe.Show(); await Frame();
                var probeHandle = new WindowInteropHelper(probe).Handle;
                NativeWindowHelper.ConfigureDock(probeHandle);
                var callbackCalls = 0;
                var service = new DockLayerService(probeHandle, () => { callbackCalls++; return FrameOf(probeHandle); });
                Check("The public layer service installs one observer and allows idempotent Start", () =>
                    Require(service.Start() && service.Start() && service.IsObserving, "Layer observation could not start."));
                await Activate(probeHandle);
                Check("The public service promotes only a visible real taskbar overlap", () =>
                    Require(service.IsElevated && callbackCalls > 0, "The public service did not observe its own active overlap."));
                service.Dispose(); service.Dispose(); await Frame();
                Check("Dispose removes observation and native elevation idempotently", () =>
                    Require(!service.IsObserving && !service.IsElevated, "The public service retained a hook or elevation after disposal."));
                var callsAfterDispose = callbackCalls;
                await Activate(new WindowInteropHelper(cover).Handle); await Activate(probeHandle);
                Check("Disposed observers never call visual geometry again", () =>
                    Require(callbackCalls == callsAfterDispose && !Topmost(probeHandle), "An event reached a disposed layer service."));

                using (var invalid = new DockLayerService(IntPtr.Zero, () => throw new InvalidOperationException("Invalid HWND must not request geometry.")))
                    Check("An invalid HWND fails closed without observation or elevation", () =>
                        Require(!invalid.Start() && !invalid.IsObserving && !invalid.IsElevated && !invalid.Refresh(), "An invalid target acquired an observer."));
                using (var unavailable = new DockLayerService(probeHandle, () => null))
                {
                    unavailable.Start(); await Activate(probeHandle); unavailable.Refresh();
                    Check("Missing visible geometry cannot promote a window", () => Require(!unavailable.IsElevated, "Unavailable glass bounds promoted the Dock."));
                }
                using (var throwing = new DockLayerService(probeHandle, () => throw new InvalidOperationException("Owned visual fixture unavailable.")))
                {
                    throwing.Start(); await Activate(probeHandle);
                    Native.SetWindowPos(probeHandle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                    throwing.Refresh();
                    Check("A failed visual callback clears a previously elevated native state", () => Require(!throwing.IsElevated, "Geometry failure left sticky topmost state."));
                }
                observations["invalidTargetFailsClosed"] = true;
                observations["hookInstallationFailureInjected"] = false;
                exit = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception error) { errors.Add(error.GetBaseException().ToString()); Console.WriteLine(error); }
            finally
            {
                dock?.Close(); center?.ClosePermanently(); cover?.Close(); probe?.Close(); controller?.Dispose();
                if (child is { HasExited: false }) { child.CloseMainWindow(); if (!child.WaitForExit(2000)) child.Kill(); }
                child?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = exit == 0, recordedAt = DateTimeOffset.Now, checks, errors, observations,
                    interactiveDesktopRequested = interactive, realShellForegroundTested = interactive && observations.ContainsKey("realShellForegroundVerified"),
                    pointerActivationContractTested = true, actualMouseInputTested = false, actualTaskbarSettingsModified = false,
                    onlyOwnedWindowsActivated = !interactive, personalFilesModified = false, systemSettingsChanged = false,
                    interpretation = "Native layering and WindowFromPoint use the actual product Dock with an explicitly activated owned HWND. The non-interactive run does not activate the Windows desktop or inject mouse input. Invalid-target fallback is exercised; an OS hook installation failure is not injected." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Layer UI checks: {checks.Count - errors.Count}/{checks.Count}; passed={exit == 0}");
                app.Shutdown(exit);
            }
        };
        app.Run(); return exit;
    }

    private static int ForegroundCover(string[] args)
    {
        var index = Array.IndexOf(args, "--foreground-cover"); var ready = args[index + 1]; var parent = uint.Parse(args[index + 2]);
        var app = new Application();
        var window = new Window { Title = "LumaDock owned external foreground fixture", Width = 420, Height = 220,
            WindowStyle = WindowStyle.None, Background = Brushes.DarkSlateGray, ShowInTaskbar = false, ShowActivated = false };
        window.Loaded += (_, _) => File.WriteAllText(ready, new WindowInteropHelper(window).Handle.ToInt64().ToString());
        window.Activated += (_, _) => Native.AllowSetForegroundWindow(parent);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) => { timeout.Stop(); window.Close(); }; timeout.Start();
        app.Run(window); return 0;
    }

    private static void Check(string name, Action check) { try { check(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); } catch (Exception error) { checks.Add(new { name, passed = false }); errors.Add(error.GetBaseException().Message); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(120); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task Activate(IntPtr window) { Native.SetForegroundWindow(window); await Frame(); Require(Native.GetForegroundWindow() == window, "The test could not activate its own HWND."); }
    private static bool Topmost(IntPtr window) => (Native.GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;
    private static IntPtr Target(Point point) => Native.GetAncestor(Native.WindowFromPoint(new() { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }), 2);
    private static FrameworkElement Named(DependencyObject root, string name) => Descendants(root).OfType<FrameworkElement>().Single(e => AutomationProperties.GetName(e) == name);
    private static Point Centre(FrameworkElement element) => element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static DockFrame FrameOf(IntPtr handle) { Require(Native.GetWindowRect(handle, out var rect), "No native frame."); return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top); }
    private static bool Contains(DockFrame frame, Point point) => point.X >= frame.X && point.X < frame.X + frame.Width && point.Y >= frame.Y && point.Y < frame.Y + frame.Height;
    private static bool Intersects(DockFrame a, DockFrame b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 && a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    private static bool IsAbove(IntPtr above, IntPtr below) { for (var window = Native.GetWindow(below, 3); window != IntPtr.Zero; window = Native.GetWindow(window, 3)) if (window == above) return true; return false; }
    private static DockFrame ActualTaskbar()
    {
        var shell = Native.GetShellWindow(); Native.GetWindowThreadProcessId(shell, out var shellProcess);
        foreach (var name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var bar = Native.FindWindow(name, null);
            if (bar != IntPtr.Zero && Native.IsWindowVisible(bar) && Native.GetWindowThreadProcessId(bar, out var owner) != 0 && owner == shellProcess)
            { var frame = FrameOf(bar); if (frame.Width >= 24 && frame.Height >= 24) return frame; }
        }
        throw new InvalidOperationException("This test needs a visible real Explorer taskbar at least 24 pixels thick.");
    }
    private static bool GenuineShellForeground()
    {
        var foreground = Native.GetAncestor(Native.GetForegroundWindow(), 2); var shell = Native.GetShellWindow();
        if (foreground == IntPtr.Zero || shell == IntPtr.Zero || !Native.IsWindowVisible(foreground)) return false;
        Native.GetWindowThreadProcessId(foreground, out var foregroundProcess); Native.GetWindowThreadProcessId(shell, out var shellProcess);
        var name = new StringBuilder(128); Native.GetClassName(foreground, name, name.Capacity);
        return foregroundProcess == shellProcess && name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint process);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string name, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    }
}
