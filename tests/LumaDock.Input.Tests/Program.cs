using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using LumaDock.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>(); var failed = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        void Await(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.GetType().Name }); Console.WriteLine("FAIL " + name); }
        }
        static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
        var before = Capture();
        using var service = new InputLanguageService();
        var profiles = service.EnumerateProfiles();
        Check("Actual TSF enabled profile enumeration returns named unique keyboard entries", () =>
        {
            Require(profiles.Count > 0 && profiles.Select(p => p.Id).Distinct().Count() == profiles.Count);
            Require(profiles.All(p => p.Name.Length > 0 && p.LanguageName.Length > 0 && p.ProfileType is 1 or 2));
        });
        Check("Enabled profiles remain stable across repeated COM enumerator creation and release", () =>
        {
            var ids = profiles.Select(p => p.Id).Order().ToArray();
            for (var i = 0; i < 30; i++) Require(service.EnumerateProfiles().Select(p => p.Id).Order().SequenceEqual(ids));
        });
        Check("Mode remains unknown or a native read, never inferred Chinese solely from language", () =>
        {
            service.Refresh(); var snapshot = service.Current;
            Require(snapshot.ProfileName.Length > 0 && snapshot.ProfileText.Length > 0 && snapshot.ModeText.Length > 0);
            Require(!snapshot.ModeAvailable || snapshot.ModeText is "中" or "英");
            Require(snapshot.CanToggleMode == snapshot.ModeAvailable);
        });
        Check("Zero target is rejected before any mode or profile activation", () =>
        {
            Require(!InputLanguageService.IsTargetValid(default));
            Require(!service.ToggleMode(default).Succeeded);
            if (profiles.Count > 0) Require(!service.ActivateProfile(profiles[0], default).Succeeded);
        });
        Check("Native input-indicator supplementation is bounded and only names an installed enabled profile", () =>
        {
            Await(service.RefreshNativeIndicatorAsync());
            var active = service.EnumerateProfiles().Where(item => item.IsActive).ToArray();
            Require(active.Length <= 1);
            if (active.Length == 1) Require(active[0].Name == service.Current.ProfileName);
        });
        Check("Overlapping native input-indicator requests coalesce without changing the foreground input layout", () =>
        {
            Await(Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RefreshNativeIndicatorAsync())));
            Require(before.Layout == GetKeyboardLayout(before.Thread));
        });
        Check("Async mode fallback rejects an invalid target without invoking a taskbar control", () =>
        {
            var task = service.ToggleModeAsync(default); Await(task); Require(!task.Result.Succeeded);
        });
        Check("Captured native window identity checks process and thread, not just a reusable HWND", () =>
        {
            using var own = new HwndSource(new HwndSourceParameters("LumaDock input identity fixture") { Width = 0, Height = 0, WindowStyle = 0 });
            var thread = GetWindowThreadProcessId(own.Handle, out var process);
            var valid = new InputTarget(own.Handle, process, thread);
            Require(InputLanguageService.IsTargetValid(valid));
            Require(!InputLanguageService.IsTargetValid(valid with { ProcessId = process + 1 }));
            Require(!InputLanguageService.IsTargetValid(valid with { ThreadId = thread + 1 }));
            own.Dispose(); Require(!InputLanguageService.IsTargetValid(valid));
        });
        Check("A valid remembered window cannot be enriched from another foreground thread's native indicator", () =>
        {
            using var own = new HwndSource(new HwndSourceParameters("LumaDock inactive input guard fixture") { Width = 0, Height = 0, WindowStyle = 0 });
            var thread = GetWindowThreadProcessId(own.Handle, out var process);
            var remembered = new InputTarget(own.Handle, process, thread);
            Require(InputLanguageService.IsTargetValid(remembered));
            Require(GetForegroundWindow() != own.Handle);
            var guard = typeof(InputLanguageService).GetMethod("IsForegroundTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
            Require(!(bool)guard.Invoke(null, [remembered])!);
            var actual = Capture();
            GetWindowThreadProcessId(actual.Window, out var actualPid);
            Require((bool)guard.Invoke(null, [new InputTarget(actual.Window, actualPid, actual.Thread)])!);
            Require(!(bool)guard.Invoke(null, [new InputTarget(actual.Window, actualPid + 1, actual.Thread)])!);
        });
        Check("No profile-selection request is accepted for an entry absent from enabled TSF enumeration", () =>
        {
            var fabricated = new InputProfileEntry("missing-test-entry", "fixture", "fixture", "x", 1, 0x804, Guid.NewGuid(), Guid.NewGuid(), IntPtr.Zero, false);
            Require(!service.ActivateProfile(fabricated, service.CaptureTarget()).Succeeded);
        });
        Check("Repeated service lifetime releases COM and native WinEvent registrations", () =>
        {
            var start = Process.GetCurrentProcess().HandleCount;
            for (var i = 0; i < 60; i++) { using var fixture = new InputLanguageService(); fixture.Refresh(); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(Process.GetCurrentProcess().HandleCount <= start + 15);
        });
        Check("Disposed service no longer accepts keyboard operations", () =>
        {
            using var fixture = new InputLanguageService(); fixture.Dispose();
            Require(!fixture.Refresh() && fixture.EnumerateProfiles().Count == 0);
            Require(!fixture.ToggleMode(default).Succeeded);
            if (profiles.Count > 0) Require(!fixture.ActivateProfile(profiles[0], default).Succeeded);
        });
        var after = Capture();
        Check("Read-only inspection leaves the initial foreground thread keyboard layout unchanged", () =>
        {
            // Tests do not activate another app; if the user changes windows during the
            // test, the original thread can still be checked without inspecting titles.
            Require(before.Layout == GetKeyboardLayout(before.Thread));
        });
        var index = Array.IndexOf(args, "--report");
        if (index >= 0 && index + 1 < args.Length)
        {
            var file = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new { version = typeof(InputLanguageService).Assembly.GetName().Version?.ToString(), passed = failed == 0,
                utc = DateTimeOffset.UtcNow, checks = results, observations = new { enabledProfileCount = profiles.Count, textServiceCount = profiles.Count(p => p.ProfileType == 1),
                    modeAvailable = service.Current.ModeAvailable, foregroundUnchanged = before.Window == after.Window,
                    nativeProfileConfirmed = service.EnumerateProfiles().Any(item => item.IsActive),
                    scope = "Read-only actual TSF and IMM inspection; only invalid targets are passed to write methods. No live input-mode or keyboard-profile change is tested." } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failed == 0 ? 0 : 1;
    }
    private static (IntPtr Window, uint Thread, IntPtr Layout) Capture()
    {
        var window = GetForegroundWindow(); var thread = GetWindowThreadProcessId(window, out _); return (window, thread, GetKeyboardLayout(thread));
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
}
