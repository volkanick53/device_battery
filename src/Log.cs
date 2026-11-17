namespace DeviceBattery;

/// <summary>
/// Tiny error log at %LOCALAPPDATA%\DeviceBattery\error.log (kept under ~200 KB). Only unexpected
/// failures are written, so a quiet app leaves no file. Never throws.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string PathOnDisk =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBattery", "error.log");

    public static void Error(string where, Exception ex) => Write($"{where}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = PathOnDisk;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 200_000)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
