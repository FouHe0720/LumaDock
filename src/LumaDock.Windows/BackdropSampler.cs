using System.Runtime.InteropServices;

namespace LumaDock.Windows;

/// <summary>An opaque, top-down BGRA32 snapshot in physical screen coordinates. Pixels stay in local process memory.</summary>
public sealed record CapturedBackdrop(int Left, int Top, int Width, int Height, int Stride, byte[] Pixels);

/// <summary>
/// On-demand desktop sampling with a short, serialized exclusion lease on this process's
/// own windows. No window is hidden or cloaked, no foreign window is changed, no pixels
/// are saved to disk, and no background capture loop is started.
/// </summary>
public static class BackdropSampler
{
    private static readonly object CaptureGate = new();
    private const uint ExcludeFromCapture = 0x11;
    private const long MaximumPixels = 8_000_000;
    private static string? lastFailure;
    public static string? LastFailure => Volatile.Read(ref lastFailure);

    /// <summary>May be called on a worker thread. Caller snapshots its known HWNDs first; visible own-process popups are added automatically.</summary>
    public static CapturedBackdrop? Capture(int left, int top, int width, int height, IReadOnlyList<IntPtr> ownWindowHandles)
    {
        ArgumentNullException.ThrowIfNull(ownWindowHandles);
        lock (CaptureGate)
        {
            lastFailure = null;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return Fail("Capture exclusion requires Windows 10 2004 or later");
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return Fail("Capture region exceeds the bounded pixel budget");
            if (DwmIsCompositionEnabled(out var composed) < 0 || !composed) return Fail("Desktop composition is unavailable");

            var screenLeft = GetSystemMetrics(76); var screenTop = GetSystemMetrics(77);
            var screenWidth = GetSystemMetrics(78); var screenHeight = GetSystemMetrics(79);
            long clippedLeft = Math.Max((long)left, screenLeft), clippedTop = Math.Max((long)top, screenTop);
            long clippedRight = Math.Min((long)left + width, (long)screenLeft + screenWidth);
            long clippedBottom = Math.Min((long)top + height, (long)screenTop + screenHeight);
            if (clippedRight <= clippedLeft || clippedBottom <= clippedTop) return Fail("Capture region is outside the virtual display");
            left = (int)clippedLeft; top = (int)clippedTop;
            width = (int)(clippedRight - clippedLeft); height = (int)(clippedBottom - clippedTop);

            var handles = ownWindowHandles.Where(h => h != IntPtr.Zero).ToHashSet();
            // WPF tooltips and popups are not necessarily in Application.Windows.
            if (!EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var processId);
                if (processId == Environment.ProcessId && IsWindowVisible(window)) handles.Add(window);
                return true;
            }, IntPtr.Zero)) return Fail("Cannot enumerate own-process windows");
            var previous = new List<(IntPtr Window, uint Affinity)>();
            foreach (var window in handles)
            {
                if (!OwnTopLevelWindow(window)) return Fail("A supplied window is no longer an own-process top-level HWND");
                if (!GetWindowDisplayAffinity(window, out var affinity)) return Fail("Cannot read an own window's display affinity");
                previous.Add((window, affinity));
            }

            var changed = new List<(IntPtr Window, uint Affinity)>();
            CapturedBackdrop? result = null;
            var restored = true;
            try
            {
                foreach (var item in previous)
                {
                    if (!OwnTopLevelWindow(item.Window)) throw new CaptureFailure("An own window closed before exclusion");
                    if (item.Affinity == ExcludeFromCapture) continue;
                    Require(SetWindowDisplayAffinity(item.Window, ExcludeFromCapture), "Excluding own window");
                    changed.Add(item);
                    if (!GetWindowDisplayAffinity(item.Window, out var applied) || applied != ExcludeFromCapture)
                        throw new CaptureFailure("Own window did not accept capture exclusion");
                }
                if (DwmFlush() < 0) throw new CaptureFailure("Desktop composition did not synchronize");
                var unprotectedWindowAppeared = false;
                if (!EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var processId);
                    if (processId == Environment.ProcessId && IsWindowVisible(window) && !handles.Contains(window)) unprotectedWindowAppeared = true;
                    return true;
                }, IntPtr.Zero) || unprotectedWindowAppeared) throw new CaptureFailure("Own window topology changed during sampling");
                result = ReadPixels(left, top, width, height);
            }
            catch (Exception error) when (error is CaptureFailure or OutOfMemoryException or OverflowException)
            { lastFailure = error.Message; }
            finally
            {
                for (var index = changed.Count - 1; index >= 0; index--)
                {
                    var item = changed[index];
                    // Never touch a reused handle which now belongs to another process.
                    if (!IsWindow(item.Window)) continue;
                    if (!OwnTopLevelWindow(item.Window)) { restored = false; continue; }
                    if (!SetWindowDisplayAffinity(item.Window, item.Affinity)
                        && !SetWindowDisplayAffinity(item.Window, item.Affinity)) restored = false;
                }
                if (!restored) lastFailure = "An own window affinity could not be restored";
            }
            return restored ? result : null;
        }
    }

    private static CapturedBackdrop? Fail(string reason) { lastFailure = reason; return null; }
    private static bool OwnTopLevelWindow(IntPtr window)
    {
        if (!IsWindow(window) || GetAncestor(window, 2) != window) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }
    private static CapturedBackdrop ReadPixels(int left, int top, int width, int height)
    {
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero); Require(screen != IntPtr.Zero, "Opening screen DC");
            memory = CreateCompatibleDC(screen); Require(memory != IntPtr.Zero, "Creating capture DC");
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            Require(bitmap != IntPtr.Zero && bits != IntPtr.Zero, "Allocating capture DIB");
            oldBitmap = SelectObject(memory, bitmap);
            Require(oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1), "Selecting capture DIB");
            Require(BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020u | 0x40000000u | 0x80000000u), "Sampling desktop pixels");
            Require(GdiFlush(), "Synchronizing capture DIB");
            var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // BI_RGB's unused high byte is not a valid WPF alpha channel.
            for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            return new CapturedBackdrop(left, top, width, height, stride, pixels);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1) && memory != IntPtr.Zero) SelectObject(memory, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }
    private static void Require(bool value, string operation)
    { if (!value) throw new CaptureFailure($"{operation} failed ({Marshal.GetLastWin32Error()})"); }
    private sealed class CaptureFailure(string message) : Exception(message);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
}
