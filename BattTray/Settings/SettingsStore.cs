using System.Diagnostics;
using System.Text.Json;

namespace BattTray.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> under %APPDATA%.
/// </summary>
/// <remarks>
/// Every failure path falls back to defaults rather than surfacing an error: a tray app
/// that refuses to start because its preferences file is unreadable has traded a cosmetic
/// problem for a total one.
/// </remarks>
internal static class SettingsStore
{
    static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattTray");

    static readonly string FilePath = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            using var stream = File.OpenRead(FilePath);
            var loaded = JsonSerializer.Deserialize(stream, AppSettingsContext.Default.AppSettings);
            return (loaded ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Settings load failed, using defaults: {ex}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var stream = File.Create(FilePath);
            JsonSerializer.Serialize(stream, settings, AppSettingsContext.Default.AppSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Settings save failed: {ex}");
        }
    }
}
