using System.IO;
using System.Text.Json;
using i3vm4windows.Core;

namespace i3vm4windows.App.Services;

/// <summary>Loads and saves <see cref="AppState"/> as JSON under %APPDATA%.</summary>
internal static class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string StateFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "i3vm4windows",
            "state.json");

    public static AppState? Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return null;

            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFilePath), Options);
        }
        catch
        {
            // A corrupt state file must never prevent startup.
            return null;
        }
    }

    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Persistence is best effort; tiling keeps working without it.
        }
    }
}
