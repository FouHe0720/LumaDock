using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.Windows;

internal static class Program
{
    private static readonly Color Blue = Color.FromRgb(36, 96, 180), Orange = Color.FromRgb(228, 70, 38);
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--underlay") return Underlay(args[1]);
        var reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/tests/sampler-results.json");
        var readyPath = Path.GetFullPath("work/sampler-tests/" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
        var results = new List<object>(); var errors = new List<string>(); var failed = 0; var code = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.GetType().Name); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            Process? child = null; Window? marker = null;
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--underlay"); start.ArgumentList.Add(readyPath); child = Process.Start(start)!;
                for (var attempt = 0; attempt < 60 && !File.Exists(readyPath); attempt++) await Task.Delay(50);
                if (!File.Exists(readyPath)) throw new InvalidOperationException("Test underlay did not render");
                var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(readyPath))!;
                marker = Window(Orange, ready.Left + 50, ready.Top + 40, 160, 100);
                WindowBackdrop.Attach(marker, () => true, () => false);
                marker.Show(); await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(200);
                var own = new WindowInteropHelper(marker).Handle;
                GetWindowRect(own, out var rect); var left = (rect.Left + rect.Right) / 2 - 24; var top = (rect.Top + rect.Bottom) / 2 - 24;
                if (!GetWindowDisplayAffinity(own, out var original)) throw new InvalidOperationException("Cannot read marker affinity");
                var foreign = new IntPtr(ready.Handle); GetWindowDisplayAffinity(foreign, out var foreignBefore);
                async Task Test(string name, Func<Task> check)
                {
                    try { await check(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
                    catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
                }
                await Test("An own marker is actually visible before sampling", () =>
                {
                    var dc = GetDC(IntPtr.Zero); uint pixel; try { pixel = GetPixel(dc, left + 24, top + 24); } finally { ReleaseDC(IntPtr.Zero, dc); }
                    Require(Near((byte)pixel, Orange.R) && Near((byte)(pixel >> 8), Orange.G) && Near((byte)(pixel >> 16), Orange.B), "Known orange marker is not present onscreen");
                    return Task.CompletedTask;
                });
                await Test("Sampling excludes the own window and returns the known blue underlay rather than black", async () =>
                {
                    var frame = await Task.Run(() => BackdropSampler.Capture(left, top, 48, 48, new[] { own }));
                    Require(frame != null, BackdropSampler.LastFailure ?? "No frame");
                    CheckBlue(frame!);
                    Require(GetWindowDisplayAffinity(own, out var affinity) && affinity == original, "Own affinity was not restored");
                });
                await Test("The sampler never changes a foreign process window", async () =>
                {
                    var frame = await Task.Run(() => BackdropSampler.Capture(left, top, 48, 48, new[] { own, foreign }));
                    Require(frame == null, "Foreign HWND was accepted");
                    Require(GetWindowDisplayAffinity(own, out var ownAfter) && ownAfter == original, "Foreign rejection changed own affinity");
                    Require(GetWindowDisplayAffinity(foreign, out var foreignAfter) && foreignAfter == foreignBefore, "Foreign affinity changed");
                });
                await Test("Concurrent consumers serialize exclusion leases and restore the marker", async () =>
                {
                    var frames = await Task.WhenAll(Task.Run(() => BackdropSampler.Capture(left, top, 48, 48, new[] { own })), Task.Run(() => BackdropSampler.Capture(left, top, 48, 48, new[] { own })));
                    foreach (var frame in frames) { Require(frame != null, BackdropSampler.LastFailure ?? "No concurrent frame"); CheckBlue(frame!); }
                    Require(GetWindowDisplayAffinity(own, out var affinity) && affinity == original, "Concurrent capture left changed affinity");
                });
                await Test("Invalid or excessive capture regions fail without touching window affinity", () =>
                {
                    Require(BackdropSampler.Capture(left, top, -1, 48, new[] { own }) == null, "Negative region accepted");
                    Require(BackdropSampler.Capture(left, top, int.MaxValue, int.MaxValue, new[] { own }) == null, "Oversized region accepted");
                    Require(GetWindowDisplayAffinity(own, out var affinity) && affinity == original, "Rejected geometry changed affinity");
                    return Task.CompletedTask;
                });
                await Test("Repeated on-demand samples do not accumulate GDI objects", async () =>
                {
                    var baseline = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                    for (var i = 0; i < 10; i++) { var frame = await Task.Run(() => BackdropSampler.Capture(left, top, 48, 48, new[] { own })); Require(frame != null, BackdropSampler.LastFailure ?? "Repeated capture failed"); }
                    var after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                    Require(after <= baseline + 2, $"GDI objects grew from {baseline} to {after}");
                });
                code = failed == 0 && errors.Count == 0 ? 0 : 1;
            }
            catch (Exception error) { failed++; errors.Add(error.GetType().Name + ":" + error.Message); }
            finally
            {
                marker?.Close();
                if (child != null) { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(500)) child.Kill(); } child.Dispose(); }
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = code == 0, measuredAtUtc = DateTimeOffset.UtcNow, platform = Environment.OSVersion.VersionString,
                    tests = results, errors, failed, screenPixelsSaved = false, foreignWindowChanged = false, systemSettingsChanged = false,
                    interpretation = "Temporary own-process windows and a separate known-color test underlay verify local RAM sampling and affinity restoration. No desktop pixels are saved." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Sampler native checks: {(code == 0 ? "PASS" : "FAIL")}; report: {reportPath}");
                app.Shutdown(code);
            }
        };
        app.Run(); return code;
    }
    private static int Underlay(string readyPath)
    {
        var app = new Application(); var m = MonitorService.GetMonitors().First(m => m.IsPrimary);
        var window = Window(Blue, m.WorkLeft + 40, m.WorkTop + 50, 280, 180);
        window.ContentRendered += (_, _) => File.WriteAllText(readyPath, JsonSerializer.Serialize(new Ready(new WindowInteropHelper(window).Handle.ToInt64(), (int)window.Left, (int)window.Top)));
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) }; timeout.Tick += (_, _) => { timeout.Stop(); window.Close(); }; timeout.Start();
        app.Run(window); return 0;
    }
    private static Window Window(Color color, double left, double top, double width, double height)
    {
        var window = new Window { Title = "LumaDock temporary color probe", Left = left, Top = top, Width = width, Height = height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            Background = new SolidColorBrush(color), Content = new Border { Background = new SolidColorBrush(color) } };
        window.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(window).Handle, false);
        return window;
    }
    private static void CheckBlue(CapturedBackdrop frame)
    {
        Require(frame.Width == 48 && frame.Height == 48 && frame.Stride == 192, "Unexpected buffer geometry");
        for (var index = 0; index < frame.Pixels.Length; index += 4)
            Require(Near(frame.Pixels[index], Blue.B) && Near(frame.Pixels[index + 1], Blue.G) && Near(frame.Pixels[index + 2], Blue.R) && frame.Pixels[index + 3] == 255, "Frame contains own-window color, black or a nonopaque alpha channel");
    }
    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) < 12;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Ready(long Handle, int Left, int Top);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
