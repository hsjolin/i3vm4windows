using System.Diagnostics;
using System.IO;

namespace i3vm4windows.App.Services;

/// <summary>
/// Starts the programs a configuration asks for, like i3's exec: the
/// first token names the program and the rest are its arguments; a
/// quoted first token carries a path with spaces. Windows environment
/// variables (%USERPROFILE%, ...) and a leading ~ expand to the user
/// profile.
/// </summary>
internal static class Launcher
{
    public static void Start(string? command)
    {
        if (command is null)
            return;

        command = Environment.ExpandEnvironmentVariables(command);
        if (command.StartsWith('~') &&
            (command.Length == 1 || command[1] is '/' or '\\'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            command = home + command[1..].Replace('/', '\\');
        }

        var trimmed = command.Trim();
        string fileName;
        string arguments;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
            {
                AppLog.Warn($"unterminated program path in {command}");
                return;
            }

            fileName = trimmed[1..end];
            arguments = trimmed[(end + 1)..].Trim();

            // A quoted first token carries a path with spaces — but when
            // the program and its arguments ended up inside the same
            // quotes and only the program part exists as a file, that
            // part is the program and the rest are its arguments.
            var space = fileName.IndexOf(' ');
            if (space > 0 && !File.Exists(fileName))
            {
                var program = fileName[..space];
                if (File.Exists(program) || File.Exists(program + ".exe"))
                {
                    arguments = $"{fileName[(space + 1)..]} {arguments}".Trim();
                    fileName = program;
                }
            }
        }
        else
        {
            var split = trimmed.IndexOf(' ');
            fileName = split < 0 ? trimmed : trimmed[..split];
            arguments = split < 0 ? "" : trimmed[(split + 1)..].Trim();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            AppLog.Warn($"cannot start {fileName}: {error.Message}");
        }
    }
}
