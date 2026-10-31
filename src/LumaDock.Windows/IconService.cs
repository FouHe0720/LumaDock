using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>One sleeping STA worker, bounded cache, frozen native Shell icons.</summary>
public sealed class IconService : IDisposable
{
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _cache = new();
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    public IconService()
    {
        _worker = new Thread(() => { foreach (var action in _queue.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "LumaDock icon cache" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public Task<BitmapSource?> GetAsync(AppEntry entry, int size = 64)
    {
        if (_disposed) return Task.FromResult<BitmapSource?>(null);
        size = Math.Clamp(size, 16, 256);
        var path = string.IsNullOrWhiteSpace(entry.AppUserModelId) ? entry.LaunchPath : "shell:AppsFolder\\" + entry.AppUserModelId;
        var key = path + ":" + size;
        // Removing completed entries bounds retained native-image memory without cancelling users' tasks.
        if (_cache.Count > 256)
            foreach (var item in _cache.Where(p => p.Value.IsCompleted).Take(64)) _cache.TryRemove(item.Key, out _);
        return _cache.GetOrAdd(key, _ =>
        {
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _queue.Add(() => { try { completion.SetResult(Load(path, size)); } catch (Exception ex) { Debug.WriteLine(ex.Message); completion.SetResult(null); } }); }
            catch (InvalidOperationException) { completion.SetResult(null); }
            return completion.Task;
        });
    }

    private static BitmapSource? Load(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) == 0 && factory != null &&
                factory.GetImage(new NativeSize { Width = size, Height = size }, 0x100 /* SIIGBF_ICONONLY */, out bitmap) == 0 && bitmap != IntPtr.Zero)
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze(); return image;
            }
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (factory != null) Marshal.FinalReleaseComObject(factory); }

        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 /* SHGFI_ICON */) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
                image.Freeze(); return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        return null; // UI can display an explicit missing-icon state; no invented app icon.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _queue.CompleteAdding(); _cache.Clear();
        // Worker drains already-requested icons; never block the UI shutdown on Shell extensions.
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory? shellItem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
