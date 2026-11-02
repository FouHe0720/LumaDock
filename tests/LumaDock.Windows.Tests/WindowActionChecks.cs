using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.Windows.Tests;

internal static class WindowActionChecks
{
    internal static void Run(Action<string, Action> check, Action<bool> require)
    {
        check("Window identities keep distinct packaged apps separate even when their executable matches", () =>
        {
            var first = new AppEntry { ExecutablePath = @"C:\Fixture\Shared.exe", AppUserModelId = "Fixture.Package!First" };
            var other = new RunningWindow(new IntPtr(123), "Second", first.ExecutablePath, "Fixture.Package!Second", 42);
            require(!WindowTracker.Matches(first, other));
            require(WindowTracker.Matches(first, other with { AppUserModelId = first.AppUserModelId }));
        });
        check("Conflicting ordinary desktop AUMIDs never fall through to an identical executable", () =>
        {
            var entry = new AppEntry { ExecutablePath = @"C:\Fixture\Shared.exe", AppUserModelId = "Fixture.Desktop.First" };
            var other = new RunningWindow(new IntPtr(123), "Second", entry.ExecutablePath, "Fixture.Desktop.Second", 42);
            require(!WindowTracker.Matches(entry, other));
            require(WindowTracker.Matches(entry, other with { AppUserModelId = "fixture.desktop.first" }));
        });
        check("Parameterized shared hosts cannot claim an unrelated window by executable alone", () =>
        {
            foreach (var name in new[] { "python.exe", "pythonw.exe", "java.exe", "dotnet.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "electron.exe", "explorer.exe" })
            {
                var entry = new AppEntry { ExecutablePath = @"C:\Fixture\" + name, Arguments = "--fixture first-script" };
                var other = new RunningWindow(new IntPtr(123), "Second hosted application", entry.ExecutablePath, "", 42);
                require(!WindowTracker.Matches(entry, other));
                require(WindowTracker.Matches(new AppEntry { ExecutablePath = entry.ExecutablePath }, other));
                entry.AppUserModelId = "Fixture.Hosted.First";
                require(WindowTracker.Matches(entry, other with { AppUserModelId = entry.AppUserModelId }));
            }
        });
        check("Unknown browser profiles and PWA arguments do not acquire every browser window", () =>
        {
            foreach (var name in new[] { "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe" })
            {
                var entry = new AppEntry { ExecutablePath = @"C:\Fixture\" + name, Arguments = "--profile-directory=Work https://example.invalid/" };
                var other = new RunningWindow(new IntPtr(123), "Another profile", entry.ExecutablePath, "", 42);
                require(!WindowTracker.Matches(entry, other));
                require(WindowTracker.Matches(new AppEntry { ExecutablePath = entry.ExecutablePath }, other));
            }
        });
        check("Window menus read the owned window's current caption and reject a stale process identity", () =>
        {
            using var source = CreateSource();
            var owned = new RunningWindow(source.Handle, "Old document", "", "", (uint)Environment.ProcessId);
            require(SetWindowText(source.Handle, "Current document — first"));
            require(WindowTracker.ReadWindowTitle(owned) == "Current document — first");
            require(SetWindowText(source.Handle, "Current document — renamed"));
            require(WindowTracker.ReadWindowTitle(owned) == "Current document — renamed");
            require(WindowTracker.ReadWindowTitle(owned with { ProcessId = uint.MaxValue }) == "已关闭窗口");
        });
        check("Desktop classification excludes ordinary application and invalid windows", () =>
        {
            using var source = CreateSource();
            require(!WindowTracker.IsDesktopSurface(source.Handle));
            require(!WindowTracker.IsDesktopSurface(IntPtr.Zero));
            var desktop = GetShellWindow();
            if (desktop != IntPtr.Zero) require(WindowTracker.IsDesktopSurface(desktop));
            using var tracker = new WindowTracker(); tracker.Dispose(); require(!tracker.IsDesktopForeground);
        });
        check("Window actions reject empty, stale and recycled process identities without sending a close request", () =>
        {
            using var tracker = new WindowTracker();
            using var source = CreateSource(); var closes = 0;
            source.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
            { if (message == 0x10) { closes++; handled = true; } return IntPtr.Zero; });
            var wrongOwner = new RunningWindow(source.Handle, "Fixture", "", "", uint.MaxValue);
            require(!tracker.ActivateWindow(wrongOwner) && !tracker.MinimizeWindow(wrongOwner) && !tracker.CloseWindow(wrongOwner));
            require(WindowTracker.GetWindowIcon(wrongOwner) == null);
            var empty = wrongOwner with { Handle = IntPtr.Zero, ProcessId = (uint)Environment.ProcessId };
            require(!tracker.ActivateWindow(empty) && !tracker.MinimizeWindow(empty) && !tracker.CloseWindow(empty));
            PumpUntil(() => false, 50); require(closes == 0);
        });
        check("The owned fixture receives normal asynchronous minimize, restore and close requests", () =>
        {
            using var tracker = new WindowTracker(); using var source = CreateSource(); var closes = 0;
            source.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
            { if (message == 0x10) { closes++; handled = true; } return IntPtr.Zero; });
            var owned = new RunningWindow(source.Handle, "Owned action fixture", "", "", (uint)Environment.ProcessId);
            require(tracker.MinimizeWindow(owned));
            require(PumpUntil(() => IsIconic(source.Handle), 1000));
            // Restore is checked independently of the OS's foreground permission.
            // The fixture is the only window targeted and contains no user content.
            var activated = tracker.ActivateWindow(owned);
            require(PumpUntil(() => !IsIconic(source.Handle), 1000));
            if (activated) require(tracker.ForegroundWindow == source.Handle);
            require(tracker.CloseWindow(owned)); require(PumpUntil(() => closes == 1, 1000));
            require(IsWindow(source.Handle)); // WM_CLOSE was handled; the target retains its right to refuse/save.
            tracker.Dispose();
            require(!tracker.MinimizeWindow(owned) && !tracker.CloseWindow(owned) && !tracker.ActivateWindow(owned));
            require(tracker.ForegroundWindow == IntPtr.Zero && tracker.GetWindows(new AppEntry()).Count == 0);
        });
        check("Borrowed native window icons are copied to frozen pixels and remain usable after extraction", () =>
        {
            using var source = CreateSource();
            var icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); require(icon != IntPtr.Zero);
            source.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
            { if (message == 0x7F) { handled = true; return icon; } return IntPtr.Zero; });
            var owned = new RunningWindow(source.Handle, "Owned icon fixture", "", "", (uint)Environment.ProcessId);
            var first = WindowTracker.GetWindowIcon(owned, 64); var second = WindowTracker.GetWindowIcon(owned, 64);
            require(first is { IsFrozen: true, PixelWidth: 64, PixelHeight: 64 } && second is { IsFrozen: true });
            var pixels = new byte[64 * 64 * 4]; first!.CopyPixels(pixels, 64 * 4, 0);
            require(pixels.Any(value => value != 0));
            // A third copy still succeeds: extraction did not destroy the borrowed shared handle.
            require(WindowTracker.GetWindowIcon(owned, 32) is { IsFrozen: true, PixelWidth: 32 });
        });
        check("Unidentified window artwork can use the real frozen Windows default application icon", () =>
        {
            var first = WindowTracker.GetGenericWindowIcon(64); var second = WindowTracker.GetGenericWindowIcon(64);
            require(first is { IsFrozen: true, PixelWidth: 64, PixelHeight: 64 } && ReferenceEquals(first, second));
            var pixels = new byte[64 * 64 * 4]; first!.CopyPixels(pixels, 64 * 4, 0);
            require(pixels.Any(value => value != 0));
        });
    }

    private static HwndSource CreateSource() => new(new HwndSourceParameters("LumaDock owned window fixture")
    { Width = 160, Height = 100, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x10CF0000) });

    private static bool PumpUntil(Func<bool> predicate, int milliseconds)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (predicate() || clock.ElapsedMilliseconds >= milliseconds) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        return predicate();
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
}
