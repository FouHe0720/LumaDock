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
        object? scriptShell = null, propertyShell = null;
        try
        {
            scriptShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            propertyShell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                         Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
                ScanFolder(Environment.GetFolderPath(folder), scriptShell, propertyShell, entries,
                    folder is Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.CommonDesktopDirectory ? "Desktop" : "StartMenu",
                    folder is not (Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.CommonDesktopDirectory));
            DiscoverPackaged(entries);
        }
        finally { Release(propertyShell); Release(scriptShell); }
        var state = new AppState { Apps = entries };
        Catalog.Deduplicate(state);
        return state.Apps.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public static List<AppEntry> DesktopEntries() => OnSta(() =>
    {
        var entries = new List<AppEntry>();
        object? scriptShell = null, propertyShell = null;
        try
        {
            scriptShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            propertyShell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            ScanFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), scriptShell, propertyShell, entries, "Desktop", false);
            ScanFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), scriptShell, propertyShell, entries, "Desktop", false);
        }
        finally { Release(propertyShell); Release(scriptShell); }
        return entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    });

    public static AppEntry FromPath(string path) => OnSta(() =>
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("应用路径不存在。", path);
        object? shell = null, propertyShell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            propertyShell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            var directory = Path.GetDirectoryName(path);
            var desktop = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                .Select(Environment.GetFolderPath).Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => string.Equals(Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar), directory, StringComparison.OrdinalIgnoreCase));
            return Resolve(path, shell, propertyShell, desktop ? "Desktop" : "Manual");
        }
        finally { Release(propertyShell); Release(shell); }
    });

    private static void ScanFolder(string folder, object? scriptShell, object? propertyShell, List<AppEntry> entries, string source, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try { entries.Add(Resolve(file, scriptShell, propertyShell, source)); }
                    catch (Exception ex) { Debug.WriteLine($"LumaDock discovery {file}: {ex.Message}"); }
                }
            }
            if (recursive)
                foreach (var directory in Directory.EnumerateDirectories(folder))
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        ScanFolder(directory, scriptShell, propertyShell, entries, source);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Debug.WriteLine(ex.Message); }
    }

    private static AppEntry Resolve(string path, object? scriptShell, object? propertyShell, string source)
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
            ReadLinkProperties(path, propertyShell, result);
        }
        // Path identity preserves distinct shortcut arguments and user organizing choices.
        result.Id = StableId("file:" + path.ToUpperInvariant());
        if (source == "Desktop") result.DesktopPaths.Add(path);
        result.Category = Catalog.SuggestCategory(result); result.CategoryRuleVersion = Catalog.CategoryRuleVersion;
        return result;
    }

    private static void ReadLinkProperties(string path, object? shell, AppEntry entry)
    {
        if (shell == null) return;
        object? folder = null, item = null, link = null, targetItem = null;
        try
        {
            folder = ((dynamic)shell).NameSpace(Path.GetDirectoryName(path));
            if (folder == null) return;
            item = ((dynamic)folder).ParseName(Path.GetFileName(path));
            if (item == null) return;
            entry.AppUserModelId = Convert.ToString(((dynamic)item).ExtendedProperty("System.AppUserModel.ID")) ?? "";
            var target = Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath")) ?? "";
            if (string.IsNullOrEmpty(entry.ExecutablePath) && !string.IsNullOrWhiteSpace(target)) entry.ExecutablePath = target;
            if (string.IsNullOrWhiteSpace(entry.AppUserModelId) && (string.IsNullOrWhiteSpace(entry.ExecutablePath) || !Path.IsPathFullyQualified(entry.ExecutablePath)))
            {
                // A PIDL-only packaged-app shortcut may expose a bare AUMID as its target parsing path.
                // Read the target's authoritative Shell property rather than guessing from an exclamation mark.
                link = ((dynamic)item).GetLink;
                targetItem = ((dynamic)link).Target;
                if (targetItem != null) entry.AppUserModelId = Convert.ToString(((dynamic)targetItem).ExtendedProperty("System.AppUserModel.ID")) ?? "";
            }
            if (!string.IsNullOrEmpty(entry.AppUserModelId) && string.Equals(entry.ExecutablePath, entry.AppUserModelId, StringComparison.OrdinalIgnoreCase)) entry.ExecutablePath = "";
            foreach (var candidate in new[] { entry.ExecutablePath, target })
            {
                foreach (var prefix in new[] { "shell:AppsFolder\\", "::{4234d49b-0245-4df3-b780-3893943456e1}\\" })
                    if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    { entry.AppUserModelId = candidate[prefix.Length..]; entry.ExecutablePath = ""; break; }
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Debug.WriteLine(error.Message); }
        finally { Release(targetItem); Release(link); Release(item); Release(folder); }
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
                    var entry = new AppEntry { Id = StableId("aumid:" + id), Name = name, AppUserModelId = id,
                        LaunchPath = "shell:AppsFolder\\" + id, ExecutablePath = target, Arguments = args,
                        Source = "AppsFolder", CategoryRuleVersion = Catalog.CategoryRuleVersion };
                    entry.Category = Catalog.SuggestCategory(entry); entries.Add(entry);
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Debug.WriteLine(ex.Message); }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Debug.WriteLine(ex.Message); }
        finally { Release(items); Release(folder); Release(shell); }
    }

    public static string StableId(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
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
