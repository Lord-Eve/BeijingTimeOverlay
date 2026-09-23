using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeijingTimeOverlay;

internal sealed class OverlaySettings
{
    public int? Left { get; set; }

    public int? Top { get; set; }

    public bool TopMost { get; set; } = true;

    public bool ClickThrough { get; set; }

    public bool HideWhenFullscreen { get; set; }

    public bool StartWithWindows { get; set; }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BeijingTimeOverlay",
        "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions) ?? new OverlaySettings();
            }
        }
        catch (IOException)
        {
            // A broken or temporarily locked settings file should not prevent the clock from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // User profile policy can deny access to roaming settings; use defaults and keep the clock available.
        }
        catch (JsonException)
        {
            // Treat malformed user settings as a first-run configuration.
        }

        return new OverlaySettings();
    }

    public static void Save(OverlaySettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // Losing a preference should not crash a tray utility.
        }
        catch (UnauthorizedAccessException)
        {
            // The clock remains usable even if its preferences cannot be persisted.
        }
    }
}
