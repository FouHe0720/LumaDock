using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>One sleeping STA worker, versioned bounded cache and frozen real application images.</summary>
public sealed class IconService : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> cache = new();
    private readonly object cacheGate = new();
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread worker;
    private volatile bool disposed;
    private sealed record Request(string LaunchPath, string ExecutablePath, string AppUserModelId, string CustomIconPath, int Size);
    private const uint IconOnly = 0x4, BiggerSizeOk = 0x1;

    public IconService()
    {
        worker = new Thread(() =>
        {
            try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
            finally { queue.Dispose(); }
        }) { IsBackground = true, Name = "LumaDock icon cache" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }

    public Task<BitmapSource?> GetAsync(AppEntry entry, int size = 64)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (disposed) return Task.FromResult<BitmapSource?>(null);
        var request = new Request(entry.LaunchPath ?? "", entry.ExecutablePath ?? "", entry.AppUserModelId ?? "", Catalog.NormalizeIconPath(entry.CustomIconPath), Math.Clamp(size, 16, 256));
        var key = string.Join('\n', request.Size.ToString(), request.AppUserModelId,
            Version(request.LaunchPath), Version(request.ExecutablePath), Version(request.CustomIconPath));
        Lazy<Task<BitmapSource?>> selected;
        lock (cacheGate)
        {
            if (disposed) return Task.FromResult<BitmapSource?>(null);
            if (!cache.TryGetValue(key, out selected!))
            {
                if (cache.Count >= 256)
                    foreach (var item in cache.Where(p => p.Value.IsValueCreated && p.Value.Value.IsCompleted).Take(64)) cache.TryRemove(item.Key, out _);
                // Evicting a pending entry only releases the cache reference; its
                // already-returned task remains valid and is drained normally.
                while (cache.Count >= 256 && cache.FirstOrDefault() is { Key: not null } oldest) cache.TryRemove(oldest.Key, out _);
                selected = cache.GetOrAdd(key, _ => new Lazy<Task<BitmapSource?>>(() => QueueLoad(request), LazyThreadSafetyMode.ExecutionAndPublication));
            }
        }
        var task = selected.Value;
        void RemoveFailed(Task<BitmapSource?> completed)
        {
            if (completed.IsCompletedSuccessfully && completed.Result != null) return;
            ((ICollection<KeyValuePair<string, Lazy<Task<BitmapSource?>>>>)cache).Remove(new(key, selected));
        }
        if (task.IsCompleted) RemoveFailed(task);
        else _ = task.ContinueWith(RemoveFailed, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Task<BitmapSource?> QueueLoad(Request request)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Add(() =>
            {
                try { completion.TrySetResult(Load(request)); }
                catch (Exception error) { Debug.WriteLine(error.Message); completion.TrySetResult(null); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(null); }
        return completion.Task;
    }

    private static string Version(string path)
    {
        if (!LocalPath(path)) return path;
        try
        {
            if (Directory.Exists(path))
            {
                // A directory's last-write time changes whenever children are added.
                // Its Shell icon normally does not; only desktop.ini customizations
                // participate in the cache version, keeping desktop refreshes cheap.
                var customization = new FileInfo(Path.Combine(path, "desktop.ini"));
                return customization.Exists
                    ? path + "|directory|" + customization.LastWriteTimeUtc.Ticks + "|" + customization.Length
                    : path + "|directory";
            }
            var file = new FileInfo(path);
            return file.Exists ? path + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length : path + "|missing";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return path + "|unavailable"; }
    }

    private static bool LocalPath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && !path.StartsWith("\\\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal)
        && path.Length > 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    private static BitmapSource? Load(Request request)
    {
        var custom = request.CustomIconPath;
        if (LocalPath(custom) && File.Exists(custom))
        {
            try
            {
                var extension = Path.GetExtension(custom).ToLowerInvariant();
                var chosen = extension switch
                {
                    ".png" or ".jpg" or ".jpeg" => LoadPicture(custom, request.Size),
                    ".ico" => LoadIconFile(custom, request.Size),
                    ".dll" => ExtractResource(custom, request.Size),
                    ".exe" or ".lnk" => ShellImage(custom, request.Size),
                    _ => null
                };
                if (chosen != null) return chosen;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or COMException or FileFormatException)
            { Debug.WriteLine("Custom icon unavailable: " + error.GetType().Name); }
        }
        // File shortcuts retain their own icon, even after scanning merges an AppsFolder identity.
        var candidates = new List<string>();
        if (LocalPath(request.LaunchPath) && (File.Exists(request.LaunchPath) || Directory.Exists(request.LaunchPath))) candidates.Add(request.LaunchPath);
        if (!string.IsNullOrWhiteSpace(request.AppUserModelId)) candidates.Add("shell:AppsFolder\\" + request.AppUserModelId);
        if (request.LaunchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) candidates.Add(request.LaunchPath);
        if (LocalPath(request.ExecutablePath) && (File.Exists(request.ExecutablePath) || Directory.Exists(request.ExecutablePath))) candidates.Add(request.ExecutablePath);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (ShellImage(candidate, request.Size) is { } image) return image; }
            catch (Exception error) when (error is COMException or ArgumentException or NotSupportedException or IOException) { Debug.WriteLine(error.GetType().Name); }
        }
        return null;
    }

    private static BitmapSource? ShellImage(string path, int size)
    {
        IShellItemImageFactory? factory = null; IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) == 0 && factory != null &&
                factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly | BiggerSizeOk, out bitmap) == 0 && bitmap != IntPtr.Zero)
            {
                // WPF/WIC preserves this HBITMAP's alpha. Do not color-key black pixels or crop artwork.
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze(); return image;
            }
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (factory != null) Marshal.FinalReleaseComObject(factory); }
        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 /* SHGFI_ICON, not SIIGBF_ICONONLY */) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                // HICON import interprets legacy icon transparency masks as well.
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
                image.Freeze(); return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        return null;
    }

    private static BitmapSource? LoadPicture(string path, int size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32 * 1024 * 1024) throw new IOException("Icon file exceeds the supported size.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 65536 || frame.PixelHeight > 65536) throw new FileFormatException("Icon dimensions exceed the supported size.");
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
        if (width >= height) image.DecodePixelWidth = Math.Min(size, width); else image.DecodePixelHeight = Math.Min(size, height);
        image.EndInit(); image.Freeze(); return image;
    }

    private static BitmapSource? LoadIconFile(string path, int size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 16 * 1024 * 1024) throw new IOException("Icon file exceeds the supported size.");
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frames = decoder.Frames.Where(frame => frame.PixelWidth is > 0 and <= 1024 && frame.PixelHeight is > 0 and <= 1024).ToArray();
        var selected = frames.Where(frame => Math.Max(frame.PixelWidth, frame.PixelHeight) >= size).OrderBy(frame => Math.Max(frame.PixelWidth, frame.PixelHeight)).ThenByDescending(frame => frame.Format.BitsPerPixel).FirstOrDefault()
            ?? frames.OrderByDescending(frame => Math.Max(frame.PixelWidth, frame.PixelHeight)).ThenByDescending(frame => frame.Format.BitsPerPixel).FirstOrDefault();
        if (selected == null) return null;
        BitmapSource chosen = selected;
        if (Math.Max(chosen.PixelWidth, chosen.PixelHeight) > size)
        {
            var scale = size / (double)Math.Max(chosen.PixelWidth, chosen.PixelHeight);
            chosen = new TransformedBitmap(chosen, new ScaleTransform(scale, scale));
        }
        // Detach from the decoder/stream while preserving alpha and aspect ratio.
        var converted = new FormatConvertedBitmap(chosen, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, converted.PixelWidth * 4);
        image.Freeze(); return image;
    }

    private static BitmapSource? ExtractResource(string path, int size)
    {
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out large, out small, (uint)(size | 16 << 16)) < 0 || large == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return image;
        }
        finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero && small != large) DestroyIcon(small); }
    }

    public void Dispose()
    {
        lock (cacheGate)
        {
            if (disposed) return; disposed = true;
            queue.CompleteAdding(); cache.Clear();
        }
        // Existing tasks drain on the STA; never block shutdown on Shell extensions.
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory? shellItem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHDefExtractIcon(string path, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
