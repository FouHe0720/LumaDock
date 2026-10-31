using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaDock.Windows;

/// <summary>The user's desktop icon-label font, expressed in WPF device-independent pixels.</summary>
public sealed record DesktopFontInfo(string FamilyName, double SizeDip, int Weight, bool Italic, bool Underline, bool Strikeout);

/// <summary>
/// Reads the Windows icon-title font without changing any system setting. Create and
/// dispose on the UI thread; Changed is delivered on that thread. No polling is used.
/// </summary>
public sealed class DesktopFontService : IDisposable
{
    private const uint GetIconTitleLogFont = 0x001F;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private int refreshQueued;
    private volatile bool disposed;

    public DesktopFontInfo Current { get; private set; }
    public event EventHandler? Changed;

    public DesktopFontService()
    {
        Current = Read();
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
    }

    /// <summary>Also call after WM_SETTINGCHANGE, or before showing a previously hidden window.</summary>
    public bool Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        var latest = Read();
        if (latest == Current) return false;
        Current = latest;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public static DesktopFontInfo Read()
    {
        var font = new LogFont { FaceName = "" };
        var size = (uint)Marshal.SizeOf<LogFont>();
        double dpi = 96;
        bool read;
        try
        {
            // Query at 96 DPI so WPF performs the per-monitor scaling exactly once.
            read = SystemParametersInfoForDpi(GetIconTitleLogFont, size, ref font, 0, 96);
        }
        catch (EntryPointNotFoundException) { read = false; } // Windows 10 before 1607.

        if (!read)
        {
            // The ordinary API returns metrics at the process's system DPI.
            read = SystemParametersInfo(GetIconTitleLogFont, size, ref font, 0);
            try { dpi = GetDpiForSystem(); }
            catch (EntryPointNotFoundException) { dpi = ReadLegacySystemDpi(); }
            if (dpi <= 0) dpi = 96;
        }
        if (!read) return WpfFallback();

        double height = font.Height < 0 ? -(double)font.Height : CharacterHeight(font);
        if (height <= 0 || !double.IsFinite(height)) return WpfFallback();
        return new DesktopFontInfo(
            string.IsNullOrWhiteSpace(font.FaceName) ? "Segoe UI" : font.FaceName.Trim(),
            height * 96 / dpi,
            font.Weight == 0 ? 400 : Math.Clamp(font.Weight, 1, 999),
            font.Italic != 0, font.Underline != 0, font.Strikeout != 0);
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Accessibility or UserPreferenceCategory.Desktop
            or UserPreferenceCategory.General or UserPreferenceCategory.Icon
            or UserPreferenceCategory.Window or UserPreferenceCategory.VisualStyle)) return;
        if (disposed || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (!disposed) Refresh();
        }));
    }

    private static DesktopFontInfo WpfFallback()
    {
        var decorations = System.Windows.SystemFonts.IconFontTextDecorations;
        return new DesktopFontInfo(System.Windows.SystemFonts.IconFontFamily.Source,
            System.Windows.SystemFonts.IconFontSize, System.Windows.SystemFonts.IconFontWeight.ToOpenTypeWeight(),
            System.Windows.SystemFonts.IconFontStyle == System.Windows.FontStyles.Italic,
            decorations.Any(decoration => decoration.Location == System.Windows.TextDecorationLocation.Underline),
            decorations.Any(decoration => decoration.Location == System.Windows.TextDecorationLocation.Strikethrough));
    }

    private static double CharacterHeight(LogFont font)
    {
        // Positive lfHeight describes a whole GDI cell, not the em-height used by WPF.
        // Most settings tools supply a negative height. Measure the less common cell
        // form once instead of making its internal leading part of the WPF font size.
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return font.Height;
        var handle = CreateFontIndirect(ref font);
        var previous = IntPtr.Zero;
        try
        {
            if (handle == IntPtr.Zero) return font.Height;
            previous = SelectObject(dc, handle);
            return GetTextMetrics(dc, out var metrics) ? Math.Max(1, metrics.Height - metrics.InternalLeading) : font.Height;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            DeleteDC(dc);
            if (handle != IntPtr.Zero) DeleteObject(handle);
        }
    }

    private static double ReadLegacySystemDpi()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 96;
        try { return GetDeviceCaps(dc, 90 /* LOGPIXELSY */); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        Changed = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, Strikeout, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetric
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public ushort FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoForDpi(uint action, uint parameter, ref LogFont value, uint flags, uint dpi);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref LogFont value, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontIndirect(ref LogFont font);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTextMetrics(IntPtr dc, out TextMetric metrics);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
