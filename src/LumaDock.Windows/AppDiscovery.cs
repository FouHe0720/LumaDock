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
    public static List<AppEntry> Scan() => ScanSnapshot().Entries.ToList();

    public static void MergeSnapshot(AppState state, AppDiscoverySnapshot snapshot)
    {
        foreach (var app in state.Apps.Where(a => a.LaunchPath.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(a.LaunchPath)))
            app.LaunchUri = ReadInternetShortcutTarget(app.LaunchPath);
        ApplicationAvailability.Apply(state, snapshot);
        Catalog.Merge(state, snapshot.Entries);
        ApplicationAvailability.Apply(state, snapshot);
    }

    public static AppDiscoverySnapshot ScanSnapshot() => OnSta(() =>
    {
        var entries = new List<AppEntry>();
        var appsFolderComplete = false;
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
            appsFolderComplete = DiscoverPackaged(entries);
        }
        finally { Release(propertyShell); Release(scriptShell); }
        var registeredIds = entries.Where(e => e.Source == "AppsFolder" && !string.IsNullOrWhiteSpace(e.AppUserModelId)).Select(e => e.AppUserModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = new AppDiscoverySnapshot(entries, registeredIds, appsFolderComplete);
        var nonApplicationCount = entries.Count(entry => !ApplicationAvailability.IsApplicationEntry(entry));
        var unavailableCount = entries.Count(entry => ApplicationAvailability.IsApplicationEntry(entry) && ApplicationAvailability.Evaluate(entry, evidence) == false);
        entries = entries.Where(entry => ApplicationAvailability.IsApplicationEntry(entry) && ApplicationAvailability.Evaluate(entry, evidence) != false).ToList();
        var state = new AppState { Apps = entries };
        Catalog.Deduplicate(state);
        return new AppDiscoverySnapshot(state.Apps.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), registeredIds, appsFolderComplete, unavailableCount, nonApplicationCount);
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
        var evidence = new AppDiscoverySnapshot([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
        return entries.Where(entry => ApplicationAvailability.IsApplicationEntry(entry) && ApplicationAvailability.Evaluate(entry, evidence) != false)
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
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
            var entry = Resolve(path, shell, propertyShell, desktop ? "Desktop" : "Manual");
            if (!ApplicationAvailability.IsApplicationEntry(entry)) throw new InvalidDataException("请选择应用程序快捷方式，而不是文件或文件夹入口。");
            var available = ApplicationAvailability.Evaluate(entry, new AppDiscoverySnapshot([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false));
            if (available == false) throw new FileNotFoundException("快捷方式指向的程序不存在，请重新选择。", path);
            return entry;
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
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
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
            var installerTarget = ReadInstallerTarget(path);
            if (installerTarget.Length > 0) result.ExecutablePath = installerTarget;
        }
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)) result.LaunchUri = ReadInternetShortcutTarget(path);
        // Path identity preserves distinct shortcut arguments and user organizing choices.
        result.Id = StableId("file:" + path.ToUpperInvariant());
        if (source == "Desktop") result.DesktopPaths.Add(path);
        result.Category = ""; result.CategoryRuleVersion = Catalog.CategoryRuleVersion;
        return result;
    }

    public static string ReadInternetShortcutTarget(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 65536) return "";
            var inShortcut = false;
            foreach (var sourceLine in File.ReadLines(path))
            {
                var line = sourceLine.Trim();
                if (line.StartsWith('[')) { inShortcut = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
                var equals = line.IndexOf('=');
                if (inShortcut && equals >= 0 && line[..equals].Trim().Equals("URL", StringComparison.OrdinalIgnoreCase)) return AppIdentity.NormalizeLaunchUri(line[(equals + 1)..]);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Debug.WriteLine(error.Message); }
        return "";
    }

    private static string ReadInstallerTarget(string path)
    {
        // Only inspect advertised shortcut/component metadata. Do not resolve,
        // configure, provide, install or repair Windows Installer components.
        var product = new StringBuilder(39); var feature = new StringBuilder(256); var component = new StringBuilder(39);
        if (MsiGetShortcutTarget(path, product, feature, component) != 0) return "";
        uint capacity = 32768; var target = new StringBuilder((int)capacity);
        var state = MsiGetComponentPath(product.ToString(), component.ToString(), target, ref capacity);
        var value = target.ToString();
        return state is 3 or 4 && Path.IsPathFullyQualified(value) && File.Exists(value)
            && new[] { ".exe", ".com", ".bat", ".cmd", ".msc", ".cpl" }.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase) ? value : "";
    }

    [DllImport("msi.dll", EntryPoint = "MsiGetShortcutTargetW", CharSet = CharSet.Unicode)] private static extern uint MsiGetShortcutTarget(string path, StringBuilder product, StringBuilder feature, StringBuilder component);
    [DllImport("msi.dll", EntryPoint = "MsiGetComponentPathW", CharSet = CharSet.Unicode)] private static extern int MsiGetComponentPath(string product, string component, StringBuilder path, ref uint characters);

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
            if ((string.IsNullOrEmpty(entry.ExecutablePath) || Path.GetExtension(entry.ExecutablePath).Equals(".ico", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(target)) entry.ExecutablePath = target;
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

    private static bool DiscoverPackaged(List<AppEntry> entries)
    {
        object? shell = null, folder = null, items = null;
        var complete = true;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!) ?? throw new InvalidOperationException("Shell COM 激活失败。");
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder == null) return false;
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
                    entry.LaunchUri = AppIdentity.NormalizeLaunchUri(target);
                    if (entry.LaunchUri.Length == 0) entry.LaunchUri = AppIdentity.NormalizeLaunchUri(id);
                    entries.Add(entry);
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { complete = false; Debug.WriteLine(ex.Message); }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { complete = false; Debug.WriteLine(ex.Message); }
        finally { Release(items); Release(folder); Release(shell); }
        return complete;
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
