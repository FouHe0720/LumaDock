using System.Text.Json;
using LumaDock.Core;

namespace LumaDock.Storage;

public sealed class StateStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string? RecoveryMessage { get; private set; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private bool recovered;

    public AppState Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(ConfigPath)) return new();
        try { return Read(ConfigPath); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            // Future schema is never overwritten by an older application.
            if (ex is InvalidDataException) throw;
            var damaged = ConfigPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            File.Copy(ConfigPath, damaged);
            recovered = true;
            try
            {
                var backup = Read(ConfigPath + ".bak");
                RecoveryMessage = "配置损坏，已保留原文件并从上次备份恢复。"; return backup;
            }
            catch (Exception backupEx) when (backupEx is JsonException or IOException or InvalidDataException or ArgumentException)
            { RecoveryMessage = "配置损坏，已保留原文件并恢复默认设置。"; return new(); }
        }
    }

    public static AppState Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("配置超过 16 MB 限制。");
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options) ?? throw new JsonException("空配置");
        Catalog.Normalize(state); return state;
    }

    public void Save(AppState state)
    {
        lock (gate)
        {
            Catalog.Normalize(state); Directory.CreateDirectory(DirectoryPath);
            var tmp = ConfigPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(fs, state, Options); fs.Flush(true); }
            if (recovered) { File.Move(tmp, ConfigPath, true); recovered = false; }
            else if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, ConfigPath + ".bak", true);
            else File.Move(tmp, ConfigPath);
        }
    }

    public static void Export(AppState state, string path) => File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
}
