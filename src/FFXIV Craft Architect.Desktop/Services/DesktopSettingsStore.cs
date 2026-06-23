using System.Text.Json;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DesktopSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIV_Craft_Architect",
            "Desktop"))
    {
    }

    public DesktopSettingsStore(string rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Desktop settings root is required.", nameof(rootDirectory))
            : rootDirectory;
        SettingsPath = Path.Combine(RootDirectory, "desktop-settings.json");
    }

    public string RootDirectory { get; }

    public string SettingsPath { get; }

    public string LastStatus { get; private set; } = "Desktop settings not loaded.";

    public DesktopSettingsProfile Load()
    {
        if (!File.Exists(SettingsPath))
        {
            LastStatus = "Desktop settings file has not been created yet.";
            return DesktopSettingsProfile.Default;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var profile = JsonSerializer.Deserialize<DesktopSettingsProfile>(json, JsonOptions);
            if (profile == null)
            {
                LastStatus = "Desktop settings file was empty; defaults applied.";
                return DesktopSettingsProfile.Default;
            }

            LastStatus = $"Desktop settings loaded from {SettingsPath}.";
            return profile.Normalize();
        }
        catch (JsonException ex)
        {
            LastStatus = $"Desktop settings file could not be parsed; defaults applied. {ex.Message}";
            return DesktopSettingsProfile.Default;
        }
        catch (IOException ex)
        {
            LastStatus = $"Desktop settings file could not be read; defaults applied. {ex.Message}";
            return DesktopSettingsProfile.Default;
        }
    }

    public void Save(DesktopSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Directory.CreateDirectory(RootDirectory);
        var json = JsonSerializer.Serialize(profile.Normalize(), JsonOptions);
        File.WriteAllText(SettingsPath, json);
        LastStatus = $"Desktop settings saved to {SettingsPath}.";
    }
}

public sealed class DesktopSettingsProfile
{
    public static DesktopSettingsProfile Default { get; } = new();

    public string Region { get; set; } = "North America";

    public string DataCenter { get; set; } = "Aether";

    public string? World { get; set; }

    public DesktopSettingsProfile Normalize()
    {
        return new DesktopSettingsProfile
        {
            Region = string.IsNullOrWhiteSpace(Region) ? "North America" : Region.Trim(),
            DataCenter = string.IsNullOrWhiteSpace(DataCenter) ? "Aether" : DataCenter.Trim(),
            World = string.IsNullOrWhiteSpace(World) ? null : World.Trim()
        };
    }

    public string FormatContext() =>
        string.IsNullOrWhiteSpace(World)
            ? $"{DataCenter} data-center scope"
            : $"{DataCenter} / {World}";
}
