using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LumaDock.Windows;

public sealed record DesktopItem(string Path, string Name, bool IsDirectory, bool IsCommonDesktop);
public sealed record DesktopScanError(string Path, string Message);
public sealed record DesktopSnapshot(IReadOnlyList<DesktopItem> Items, IReadOnlyList<DesktopScanError> Errors);
public sealed record DesktopCopyFailure(string Path, string Message);
public sealed record DesktopCopyResult(IReadOnlyList<DesktopItem> Items, IReadOnlyList<DesktopCopyFailure> Failures, bool Cancelled = false);

/// <summary>A live top-level view of the real desktop directories, independent of the application catalog.</summary>
public sealed class DesktopFileService
{
    public string UserDesktopDirectory { get; }
    public string CommonDesktopDirectory { get; }
    public IReadOnlyList<string> Roots { get; }

    // Explicit roots are useful for isolated tests. An empty common root disables that source.
    public DesktopFileService(string? userDesktopDirectory = null, string? commonDesktopDirectory = null)
    {
        UserDesktopDirectory = NormalizeRoot(userDesktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var common = commonDesktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        CommonDesktopDirectory = string.IsNullOrWhiteSpace(common) ? "" : NormalizeRoot(common);
        Roots = new[] { UserDesktopDirectory, CommonDesktopDirectory }.Where(path => path.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public DesktopSnapshot Scan()
    {
        var items = new List<DesktopItem>();
        var errors = new List<DesktopScanError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            try
            {
                // Enumerate one level only: junctions, folders and their contents remain untouched.
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    try
                    {
                        var fullPath = System.IO.Path.GetFullPath(path);
                        var attributes = File.GetAttributes(fullPath);
                        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                            System.IO.Path.GetFileName(fullPath).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || !seen.Add(fullPath)) continue;
                        items.Add(Describe(fullPath, (attributes & FileAttributes.Directory) != 0));
                    }
                    catch (Exception error) when (FileError(error)) { errors.Add(new(path, ErrorText(error))); }
                }
            }
            catch (Exception error) when (FileError(error)) { errors.Add(new(root, ErrorText(error))); }
        }
        return new(items.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.IsCommonDesktop).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), errors);
    }

    public DesktopItem CreateFolder(string? name = null)
    {
        var requested = name ?? "新建文件夹";
        ValidateName(requested);
        EnsureWritableRoot();
        for (var index = 0; index < 10000; index++)
        {
            var path = System.IO.Path.Combine(UserDesktopDirectory, UniqueName(requested, index, false));
            // Directory.CreateDirectory accepts an existing directory. The native create is atomic
            // and lets two simultaneous requests reserve different names without returning old data.
            if (CreateDirectoryW(path, IntPtr.Zero)) return Describe(path, true);
            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) continue;
            throw new IOException("无法在桌面新建文件夹。", new Win32Exception(error));
        }
        throw new IOException("同名项目过多，请换一个名称。");
    }

    public DesktopItem CreateTextFile(string? name = null)
    {
        var requested = name ?? "新建文本文档.txt";
        ValidateName(requested);
        if (!requested.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) requested += ".txt";
        ValidateName(requested);
        EnsureWritableRoot();
        using var reservation = ReserveFile(requested, out var destination);
        return Describe(destination, false);
    }

    /// <summary>Renames one actual desktop child. No destination, original content or catalog entry is overwritten.</summary>
    public DesktopItem Rename(DesktopItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateName(newName);
        var source = ValidateChild(item.Path);
        var directory = System.IO.Path.GetDirectoryName(source)!;
        var destination = System.IO.Path.Combine(directory, newName);
        var attributes = File.GetAttributes(source);
        if (string.Equals(source, destination, StringComparison.Ordinal)) return Describe(source, (attributes & FileAttributes.Directory) != 0);
        // Without MOVEFILE_REPLACE_EXISTING, this also supports case-only renames on Windows.
        if (!MoveFileExW(source, destination, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(error is 80 or 183 ? "已有同名项目，请换一个名称。" : "无法重命名此桌面项目。", new Win32Exception(error));
        }
        return Describe(destination, (attributes & FileAttributes.Directory) != 0);
    }

    /// <summary>Copies files only. It never moves originals or follows directory links.</summary>
    public DesktopCopyResult CopyItems(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = new List<DesktopItem>();
        var failures = new List<DesktopCopyFailure>();
        var cancelled = false;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            foreach (var candidate in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? destination = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = System.IO.Path.GetFullPath(candidate);
                    var attributes = File.GetAttributes(source);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        failures.Add(new(candidate, "文件夹请通过“打开桌面目录”在资源管理器中复制。"));
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        failures.Add(new(candidate, "链接文件请在资源管理器中复制。"));
                        continue;
                    }
                    var name = System.IO.Path.GetFileName(source);
                    ValidateName(name);
                    EnsureWritableRoot();
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = ReserveFile(name, out destination))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                        }
                    }
                    items.Add(Describe(destination, false));
                    destination = null;
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception error) when (FileError(error)) { failures.Add(new(candidate, ErrorText(error))); }
                finally
                {
                    // Only a failed output successfully reserved by this invocation can be removed.
                    // All original paths are read-only inputs and are never passed to Delete.
                    if (destination != null)
                    {
                        try { File.Delete(destination); }
                        catch (Exception error) when (FileError(error)) { failures.Add(new(destination, "未能清理未完成的副本，请在桌面检查该文件。")); }
                    }
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return new(items, failures, cancelled);
    }

    public DesktopDirectoryWatcher CreateWatcher() => new(Roots);

    private DesktopItem Describe(string path, bool isDirectory) => new(path, System.IO.Path.GetFileName(path), isDirectory,
        !string.IsNullOrEmpty(CommonDesktopDirectory) && !string.Equals(UserDesktopDirectory, CommonDesktopDirectory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(System.IO.Path.GetDirectoryName(path), CommonDesktopDirectory, StringComparison.OrdinalIgnoreCase));

    private string ValidateChild(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        ValidateName(System.IO.Path.GetFileName(fullPath));
        if (!Roots.Contains(System.IO.Path.GetDirectoryName(fullPath) ?? "", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("只能操作桌面中的项目。", nameof(path));
        return fullPath;
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("无法确定桌面目录。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    private void EnsureWritableRoot()
    {
        if (!Directory.Exists(UserDesktopDirectory)) throw new DirectoryNotFoundException("桌面目录暂时不可用，请先在资源管理器中检查。");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(character => char.IsControl(character) || System.IO.Path.GetInvalidFileNameChars().Contains(character)) || name.Length > 240)
            throw new ArgumentException("名称不能为空，也不能包含 \\ / : * ? \" < > | 或以空格、句点结尾。", nameof(name));
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("此名称由 Windows 保留，请换一个名称。", nameof(name));
    }

    private FileStream ReserveFile(string name, out string destination)
    {
        for (var index = 0; index < 10000; index++)
        {
            var path = System.IO.Path.Combine(UserDesktopDirectory, UniqueName(name, index, true));
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                destination = path;
                return stream;
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("同名项目过多，请换一个名称。");
    }

    private static string UniqueName(string name, int index, bool file)
    {
        if (index == 0) return name;
        var extension = file ? System.IO.Path.GetExtension(name) : "";
        var stem = file ? System.IO.Path.GetFileNameWithoutExtension(name) : name;
        return $"{stem} ({index + 1}){extension}";
    }

    internal static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    internal static string ErrorText(Exception error) => error switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => "没有访问权限，请在资源管理器中检查。",
        FileNotFoundException or DirectoryNotFoundException => "项目或桌面目录已移动、删除或暂时不可用。",
        ArgumentException => error.Message.Split(Environment.NewLine)[0],
        NotSupportedException => "此文件路径暂不支持，请在资源管理器中处理。",
        _ => "文件正在使用，或暂时无法访问。请重试或在资源管理器中检查。"
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileExW(string source, string destination, uint flags);
}

/// <summary>Sleeping, top-level directory notifications. Callers coalesce changes on their own UI dispatcher.</summary>
public sealed class DesktopDirectoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private int _disposed;
    public event EventHandler? Changed;
    public IReadOnlyList<DesktopScanError> Errors { get; }

    public DesktopDirectoryWatcher(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var errors = new List<DesktopScanError>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                    InternalBufferSize = 8192
                };
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnChange;
                watcher.Changed += OnChange;
                watcher.Error += OnError;
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (DesktopFileService.FileError(error))
            {
                if (watcher != null) { _watchers.Remove(watcher); watcher.Dispose(); }
                errors.Add(new(root, DesktopFileService.ErrorText(error)));
            }
        }
        Errors = errors;
    }

    private void OnChange(object sender, FileSystemEventArgs args) => RaiseChanged();
    // Overflow is a request for one complete rescan, never for polling or trusting a partial event list.
    private void OnError(object sender, ErrorEventArgs args) => RaiseChanged();
    private void RaiseChanged()
    {
        if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
