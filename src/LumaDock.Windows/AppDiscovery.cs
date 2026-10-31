using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>Shell discovery runs on a dedicated STA. No installation files are changed.</summary>
public static class AppDiscovery
{
    public static List<AppEntry> Scan() => OnSta(() =>
    {
        var entries = new List<AppEntry>();
        object? scriptShell = null;
        try
        {
            scriptShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                         Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
                ScanFolder(Environment.GetFolderPath(folder), scriptShell, entries,
                    folder is Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.CommonDesktopDirectory ? "Desktop" : "StartMenu",
                    folder is not (Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.CommonDesktopDirectory));
            DiscoverPackaged(entries);
        }
        finally { Release(scriptShell); }
        var merged = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = !string.IsNullOrWhiteSpace(entry.ExecutablePath)
                ? "target:" + Environment.ExpandEnvironmentVariables(entry.ExecutablePath) + "|" + entry.Arguments
                : !string.IsNullOrWhiteSpace(entry.AppUserModelId) ? "aumid:" + entry.AppUserModelId : "file:" + entry.LaunchPath;
            if (merged.TryGetValue(key, out var existing))
            {
                if (string.IsNullOrEmpty(existing.AppUserModelId)) existing.AppUserModelId = entry.AppUserModelId;
                existing.DesktopPaths.AddRange(entry.DesktopPaths.Except(existing.DesktopPaths, StringComparer.OrdinalIgnoreCase));
            }
            else merged.Add(key, entry);
        }
        return merged.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public static List<AppEntry> DesktopEntries() => OnSta(() =>
    {
        var entries = new List<AppEntry>();
        object? scriptShell = null;
        try
        {
            scriptShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            ScanFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), scriptShell, entries, "Desktop", false);
            ScanFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), scriptShell, entries, "Desktop", false);
        }
        finally { Release(scriptShell); }
        return entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    });

    public static AppEntry FromPath(string path) => OnSta(() =>
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("应用路径不存在。", path);
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            return Resolve(path, shell, "Manual");
        }
        finally { Release(shell); }
    });

    private static void ScanFolder(string folder, object? scriptShell, List<AppEntry> entries, string source, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try { entries.Add(Resolve(file, scriptShell, source)); }
                    catch (Exception ex) { Debug.WriteLine($"LumaDock discovery {file}: {ex.Message}"); }
                }
            }
            if (recursive)
                foreach (var directory in Directory.EnumerateDirectories(folder))
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        ScanFolder(directory, scriptShell, entries, source);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Debug.WriteLine(ex.Message); }
    }

    private static AppEntry Resolve(string path, object? scriptShell, string source)
    {
        var result = new AppEntry { Name = Path.GetFileNameWithoutExtension(path), LaunchPath = path,
            ExecutablePath = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : "", Source = source };
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && scriptShell != null)
        {
            object? shortcut = null;
            try
            {
                shortcut = ((dynamic)scriptShell).CreateShortcut(path);
                result.ExecutablePath = (string)((dynamic)shortcut).TargetPath;
                result.Arguments = (string)((dynamic)shortcut).Arguments;
                result.WorkingDirectory = (string)((dynamic)shortcut).WorkingDirectory;
            }
            catch (COMException ex) { Debug.WriteLine(ex.Message); }
            finally { Release(shortcut); }
        }
        // Path identity preserves distinct shortcut arguments and user organizing choices.
        result.Id = StableId("file:" + path.ToUpperInvariant());
        if (source == "Desktop") result.DesktopPaths.Add(path);
        result.Category = GuessCategory(result.Name);
        return result;
    }

    private static void DiscoverPackaged(List<AppEntry> entries)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!) ?? throw new InvalidOperationException("Shell COM 激活失败。");
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder == null) return;
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(index);
                    string name = ((dynamic)item).Name;
                    string id = Convert.ToString(((dynamic)item).ExtendedProperty("System.AppUserModel.ID")) ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    string target = Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath")) ?? "";
                    string args = Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.Arguments")) ?? "";
                    // AppsFolder also lists Win32 entries; keep its stable Shell identifier.
                    entries.Add(new AppEntry { Id = StableId("aumid:" + id), Name = name, AppUserModelId = id,
                        LaunchPath = "shell:AppsFolder\\" + id, ExecutablePath = target, Arguments = args,
                        Source = "AppsFolder", Category = GuessCategory(name) });
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Debug.WriteLine(ex.Message); }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Debug.WriteLine(ex.Message); }
        finally { Release(items); Release(folder); Release(shell); }
    }

    public static string StableId(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    private static string GuessCategory(string name)
    {
        var text = name.ToLowerInvariant();
        if (new[] { "visual", "code", "terminal", "python", "git", "docker", "开发", "powershell" }.Any(text.Contains)) return "开发工具";
        if (new[] { "edge", "chrome", "firefox", "browser", "浏览" }.Any(text.Contains)) return "浏览器";
        if (new[] { "word", "excel", "office", "note", "pdf", "文档", "笔记", "学习" }.Any(text.Contains)) return "办公学习";
        if (new[] { "music", "video", "player", "音乐", "视频", "照片" }.Any(text.Contains)) return "影音娱乐";
        if (new[] { "steam", "game", "游戏", "xbox" }.Any(text.Contains)) return "游戏";
        if (new[] { "设置", "管理", "工具", "控制", "system", "windows", "计算器" }.Any(text.Contains)) return "系统工具";
        return "其他";
    }

    internal static T OnSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return action();
        T? result = default; Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true, Name = "LumaDock Shell discovery" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }

    internal static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
