using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>
/// Keeps taskbar-overlapping glass reachable while the desktop or this Dock is
/// foreground. A working window immediately restores the ordinary Dock band.
/// Start and dispose on the thread owning the Dock and its message loop.
/// </summary>
public sealed class DockLayerService : IDisposable
{
    private readonly IntPtr dock;
    private readonly Func<DockFrame?> visibleGlassBounds;
    private readonly WinEventCallback callback;
    private readonly HashSet<IntPtr> taskbars = [];
    private IntPtr foregroundHook, taskbarHook, locationHook, shellWindow;
    private uint shellProcess;
    private bool disposed, refreshing, taskbarsDirty = true;
    private const uint PositionFlags = 0x0013; // NOACTIVATE | NOMOVE | NOSIZE.

    public DockLayerService(IntPtr dock, Func<DockFrame?> visibleGlassBounds)
    {
        this.dock = dock;
        this.visibleGlassBounds = visibleGlassBounds ?? throw new ArgumentNullException(nameof(visibleGlassBounds));
        callback = OnWinEvent;
    }

    public bool IsObserving => !disposed && foregroundHook != IntPtr.Zero && IsOwnedWindow();
    public bool IsElevated => IsOwnedWindow() && (GetWindowLongPtr(dock, -20).ToInt64() & 8) != 0;

    public bool Start()
    {
        if (disposed || !IsOwnedWindow()) return false;
        if (foregroundHook != IntPtr.Zero) return true;
        // Do not skip this process: its management window must also demote the Dock.
        // Out-of-context events are delivered on this installing thread's message loop.
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
        if (foregroundHook == IntPtr.Zero) { Refresh(); return false; }
        taskbarHook = SetWinEventHook(0x8000, 0x8004, IntPtr.Zero, callback, 0, 0, 0);
        locationHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, callback, 0, 0, 0);
        Refresh(); return true;
    }

    public bool Refresh()
    {
        if (disposed || !IsOwnedWindow()) return false;
        if (refreshing) return true;
        refreshing = true;
        try
        {
            // Always read the current foreground, not an already queued event's HWND.
            var foreground = GetForegroundWindow();
            if (IsObserving && IsWindowVisible(dock) && ReadShellIdentity()
                && (foreground == dock || IsShellForeground(foreground))
                && visibleGlassBounds() is { Width: > 0, Height: > 0 } glass)
            {
                RefreshTaskbars();
                var elevated = IsElevated;
                var overlap = false; var covered = false;
                foreach (var bar in taskbars)
                {
                    if (!IsWindowVisible(bar) || !GetWindowRect(bar, out var bounds) || !Intersects(glass, bounds)) continue;
                    overlap = true;
                    if (elevated && IsAboveDock(bar)) covered = true;
                }
                if (overlap)
                {
                    // Avoid producing our own stream of reorder events at rest.
                    return elevated && !covered || SetWindowPos(dock, new IntPtr(-1), 0, 0, 0, 0, PositionFlags);
                }
            }
            return NativeWindowHelper.ApplyDockLayer(dock);
        }
        catch (Exception error)
        {
            // A closed visual or unavailable observer must never leave a sticky topmost Dock.
            Debug.WriteLine("Dock desktop-layer update failed: " + error.Message);
            return NativeWindowHelper.ApplyDockLayer(dock);
        }
        finally { refreshing = false; }
    }

    private bool IsOwnedWindow() => dock != IntPtr.Zero && IsWindow(dock)
        && GetWindowThreadProcessId(dock, out var process) != 0 && process == (uint)Environment.ProcessId;

    private bool ReadShellIdentity()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out var process) == 0 || process == 0)
        {
            shellWindow = IntPtr.Zero; shellProcess = 0; taskbars.Clear(); taskbarsDirty = true; return false;
        }
        if (shell != shellWindow || process != shellProcess)
        {
            shellWindow = shell; shellProcess = process; taskbarsDirty = true;
        }
        return true;
    }

    private bool IsShellForeground(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground)) return false;
        var root = GetAncestor(foreground, 2); // GA_ROOT; never classify an owned flyout as its taskbar owner.
        if (root == IntPtr.Zero || GetWindowThreadProcessId(root, out var process) == 0 || process != shellProcess) return false;
        var name = WindowClass(root);
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private bool IsTaskbar(IntPtr window)
    {
        var name = WindowClass(window);
        return (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            && GetWindowThreadProcessId(window, out var process) != 0 && process == shellProcess;
    }

    private void RefreshTaskbars()
    {
        if (!taskbarsDirty) return;
        taskbarsDirty = false; taskbars.Clear();
        foreach (var name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var previous = IntPtr.Zero;
            // Only enumerate matching top-level taskbars, not all windows or processes.
            for (var count = 0; count < 64; count++)
            {
                var bar = FindWindowEx(IntPtr.Zero, previous, name, null);
                if (bar == IntPtr.Zero || bar == previous) break;
                if (IsTaskbar(bar)) taskbars.Add(bar);
                previous = bar;
            }
        }
    }

    private bool IsAboveDock(IntPtr other)
    {
        var window = GetWindow(dock, 3); // GW_HWNDPREV walks toward higher Z order.
        for (var count = 0; window != IntPtr.Zero && count < 512; count++)
        {
            if (window == other) return true;
            var previous = GetWindow(window, 3);
            if (previous == window) break;
            window = previous;
        }
        return false;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (disposed || refreshing) return;
        if (eventType == 3) { Refresh(); return; }
        if (window == IntPtr.Zero || window == dock) return;
        // Explorer may report its Z-order change against OBJID_CLIENT rather
        // than OBJID_WINDOW. Only a previously verified taskbar gets this path;
        // our own reorder events never recursively promote the Dock.
        if (eventType == 0x8004 && taskbars.Contains(window)) { Refresh(); return; }
        if (objectId != 0 || childId != 0) return;
        // Location events are very frequent globally. Only known taskbars need work.
        if (eventType == 0x800B)
        {
            if (taskbars.Contains(window)) Refresh();
            return;
        }
        if (!taskbars.Contains(window) && (!ReadShellIdentity() || !IsTaskbar(window))) return;
        taskbarsDirty = true; Refresh();
    }

    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); return name.ToString();
    }

    private static bool Intersects(DockFrame glass, Rect taskbar) =>
        glass.X < taskbar.Right && (long)glass.X + glass.Width > taskbar.Left
        && glass.Y < taskbar.Bottom && (long)glass.Y + glass.Height > taskbar.Top;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var hook in new[] { foregroundHook, taskbarHook, locationHook })
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        foregroundHook = taskbarHook = locationHook = IntPtr.Zero; taskbars.Clear();
        if (IsOwnedWindow()) NativeWindowHelper.ApplyDockLayer(dock);
        GC.KeepAlive(callback);
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
}
