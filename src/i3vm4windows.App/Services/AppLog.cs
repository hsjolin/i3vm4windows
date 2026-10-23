using System.IO;

namespace i3vm4windows.App.Services;

/// <summary>
/// A plain-text log the tray menu can open, so the app's decisions —
/// configuration loading, hotkey dispatches, refresh failures — can be
/// inspected without a debugger attached. Logging must never take the
/// app down, so write failures are swallowed.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string _path = "";

    /// <summary>The log file, in the user's local application data.</summary>
    public static string Path
    {
        get
        {
            if (_path.Length == 0)
            {
                var directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "i3vm4windows");
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, "log.txt");
            }

            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>A session header, so a growing file separates runs.</summary>
    public static void Session()
    {
        Write("INFO", $"--- session start, {Environment.OSVersion.VersionString} ---");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
