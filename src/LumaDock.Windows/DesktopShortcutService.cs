using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>Creates private transfer copies. Explorer owns destination naming and collision handling.</summary>
public static class DesktopShortcutService
{
    private static readonly string TransferRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock", "Transfers");

    public static string PrepareTransfer(AppEntry app) => AppDiscovery.OnSta(() =>
    {
        // Every drag owns a directory. Never expose an original shortcut/program as a movable source.
        Directory.CreateDirectory(TransferRoot);
        CleanOldTransfers();
        var directory = Path.Combine(TransferRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return WriteShortcut(app, directory); }
        catch { try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    });

    /// <summary>Writes a new shortcut only. Existing files, including destination collisions, are never overwritten.</summary>
    public static string WriteShortcut(AppEntry app, string directory)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("快捷方式文件夹不存在。");
        return AppDiscovery.OnSta(() => WriteOnSta(app, Path.GetFullPath(directory)));
    }

    private static string WriteOnSta(AppEntry app, string directory)
    {
        var extension = Path.GetExtension(app.LaunchPath).ToLowerInvariant();
        var original = extension is ".lnk" or ".url" or ".appref-ms" && File.Exists(app.LaunchPath);
        var suffix = original ? extension : ".lnk";
        var destination = ReserveName(directory, SafeName(app.Name), suffix);
        try
        {
            if (original)
            {
                // The reserved destination belongs only to this call; an original is read with shared access.
                using var input = File.OpenRead(app.LaunchPath);
                using var output = new FileStream(destination, FileMode.Truncate, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            else
            {
                object? shellLink = null;
                IntPtr itemId = IntPtr.Zero;
                try
                {
                    shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
                    var link = (IShellLinkW)shellLink;
                    if (!string.IsNullOrWhiteSpace(app.AppUserModelId) && app.LaunchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                    {
                        Marshal.ThrowExceptionForHR(SHParseDisplayName("shell:AppsFolder\\" + app.AppUserModelId, IntPtr.Zero, out itemId, 0, out _));
                        link.SetIDList(itemId);
                    }
                    else
                    {
                        if (!File.Exists(app.LaunchPath)) throw new FileNotFoundException("应用已移动，请重新添加。");
                        link.SetPath(Path.GetFullPath(app.LaunchPath));
                        link.SetArguments(app.Arguments ?? "");
                        if (Directory.Exists(app.WorkingDirectory)) link.SetWorkingDirectory(app.WorkingDirectory);
                    }
                    link.SetDescription(app.Name ?? "");
                    var icon = app.CustomIconPath;
                    if (!string.IsNullOrWhiteSpace(icon) && File.Exists(icon) && new[] { ".ico", ".exe", ".dll" }.Contains(Path.GetExtension(icon).ToLowerInvariant())) link.SetIconLocation(icon, 0);
                    ((IPersistFile)shellLink).Save(destination, true);
                }
                finally { if (itemId != IntPtr.Zero) Marshal.FreeCoTaskMem(itemId); AppDiscovery.Release(shellLink); }
            }
            return destination;
        }
        catch { File.Delete(destination); throw; }
    }

    private static string ReserveName(string directory, string name, string extension)
    {
        for (var i = 0; i < 10000; i++)
        {
            var destination = Path.Combine(directory, name + (i == 0 ? "" : $" ({i + 1})") + extension);
            try { using var reservation = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None); return destination; }
            catch (IOException) when (File.Exists(destination)) { }
        }
        throw new IOException("同名快捷方式过多，请换个名称。");
    }

    private static string SafeName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "应用").Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100].TrimEnd();
        if (string.IsNullOrWhiteSpace(clean)) clean = "应用";
        var stem = clean.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) clean = "应用 " + clean;
        return clean;
    }

    private static void CleanOldTransfers()
    {
        // Some Shell targets finish asynchronously. Keep all recent data for seven days,
        // then remove only this service's GUID directories, never linked directories or user sources.
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(TransferRoot).Take(256))
            {
                var info = new DirectoryInfo(directory);
                if (!Guid.TryParseExact(info.Name, "N", out _) || info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7) || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var files = info.GetFiles();
                if (info.GetDirectories().Length != 0 || files.Any(file => (file.Attributes & FileAttributes.ReparsePoint) != 0)) continue;
                foreach (var file in files) file.Delete();
                info.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr itemId, uint attributes, out uint attributesOut);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemId);
        void SetIDList(IntPtr itemId);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxDirectory);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxArguments);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
