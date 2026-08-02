using System.IO;
namespace CopyTool.Host;

/// <summary>
/// The host has no console and no tray icon, so this file is the only way to see
/// what it did. Appends are best-effort — logging must never break a copy.
/// </summary>
public static class HostLog
{
    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyTool");

    private static readonly string Path_ = Path.Combine(Directory, "host.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path_, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
