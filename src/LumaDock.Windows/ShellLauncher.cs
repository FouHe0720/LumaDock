using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LumaDock.Core;

namespace LumaDock.Windows;

public static class ShellLauncher
{
    public static bool Launch(AppEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.AppUserModelId) &&
            (string.IsNullOrWhiteSpace(entry.LaunchPath) || entry.LaunchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)))
        {
            // Shell namespace activation supports both packaged and registered Win32 AppsFolder items.
            using var activation = Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.AppUserModelId}") { UseShellExecute = true });
            return true;
        }
        if (string.IsNullOrWhiteSpace(entry.LaunchPath)) throw new InvalidOperationException("此应用没有有效的启动路径。请重新扫描或手动添加。");
        if (!File.Exists(entry.LaunchPath)) throw new FileNotFoundException("应用或快捷方式已移动，请重新添加。", entry.LaunchPath);
        // A .lnk contains its arguments and working directory; do not append them a second time.
        var shortcut = Path.GetExtension(entry.LaunchPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(entry.LaunchPath) { UseShellExecute = true,
            Arguments = shortcut ? "" : entry.Arguments,
            WorkingDirectory = Directory.Exists(entry.WorkingDirectory) ? entry.WorkingDirectory : Path.GetDirectoryName(entry.LaunchPath) ?? "" };
        using var process = Process.Start(info);
        return true; // ShellExecute can succeed without returning a process (DDE/packaged activation).
    }
}
