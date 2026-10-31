using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LumaDock.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>();
        var passed = true;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Title = "LumaDock backdrop test", Width = 320, Height = 180, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false, ShowActivated = false, AllowsTransparency = false,
            Content = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) }
        };
        bool dark = true, savePower = false;
        WindowBackdrop.Attach(window, () => dark, () => savePower);
        app.Startup += (_, _) => window.Show();
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var native = WindowBackdrop.GetStatus(window);
                var hwnd = new WindowInteropHelper(window).Handle;
                var nativeStyle = GetWindowLongPtr(hwnd, -16).ToInt64();
                var isLayered = (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00080000) != 0;
                var transparentTarget = HwndSource.FromHwnd(hwnd)?.CompositionTarget.BackgroundColor.A == 0;
                var nativeMatches = !native.AcrylicActive || native.AppliedBackdrop == 3 && native.HResult == 0 && transparentTarget;
                if (isLayered || !nativeMatches) throw new InvalidOperationException("Acrylic HWND state is inconsistent.");
                results.Add(new { name = "Non-layered native backdrop state", passed = true, native, transparentTarget, isLayered, nativeStyle = nativeStyle.ToString("X"), hasNativeCaption = (nativeStyle & 0x00C00000) != 0 });

                savePower = true;
                var disabled = !WindowBackdrop.Refresh(window, dark, savePower);
                var fallback = WindowBackdrop.GetStatus(window);
                var opaqueFallback = window.Background is SolidColorBrush brush && brush.Color.A == 255;
                if (!disabled || !opaqueFallback || fallback.AppliedBackdrop != 1)
                    throw new InvalidOperationException("Power saver did not produce an opaque fallback.");
                results.Add(new { name = "Power saver opaque fallback", passed = true, native = fallback, opaqueFallback });

                dark = false; savePower = false;
                WindowBackdrop.Refresh(window, dark, savePower);
                var restored = WindowBackdrop.GetStatus(window);
                if (restored.AcrylicActive != native.AcrylicActive || restored.AcrylicActive && restored.AppliedBackdrop != 3)
                    throw new InvalidOperationException("Theme/material restore failed.");
                results.Add(new { name = "Light theme backdrop restore", passed = true, native = restored });
                window.WindowState = WindowState.Maximized;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GetWindowRect(hwnd, out var bounds);
                var monitors = MonitorService.GetMonitors();
                var nearest = monitors.OrderBy(m => Math.Abs(bounds.Left - m.WorkLeft) + Math.Abs(bounds.Top - m.WorkTop)).First();
                if (bounds.Bottom > nearest.WorkTop + nearest.WorkHeight + 8 || bounds.Right > nearest.WorkLeft + nearest.WorkWidth + 8)
                    throw new InvalidOperationException("Maximized window covers the taskbar work area.");
                results.Add(new { name = "Maximize respects monitor work area", passed = true,
                    bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom } });
                window.WindowState = WindowState.Normal;
                await Task.Delay(250);
            }
            catch (Exception ex) { passed = false; results.Add(new { name = "Backdrop probe", passed = false, error = ex.GetType().Name, message = ex.Message }); }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run();
        var report = new { passed, platform = Environment.OSVersion.VersionString, supported = WindowBackdrop.IsSupported, nativeAttributesOnly = true, visualCaptureRequired = true, systemSettingsChanged = false, tests = results };
        var path = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/tests/backdrop-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Backdrop native checks: {(passed ? "PASS" : "FAIL")}; report: {path}");
        return passed ? 0 : 1;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

}
