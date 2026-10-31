namespace LumaDock.Storage;

public static class Log
{
    public static string DirectoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock", "logs");
    private static readonly object Gate = new();
    public static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "lumadock.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {error}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never bring down the desktop. */ }
    }
}
