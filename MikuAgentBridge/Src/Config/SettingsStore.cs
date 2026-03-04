using System.Text.Json;

namespace MikuAgentBridge.Config;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public Settings LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var value = JsonSerializer.Deserialize<Settings>(json);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch
        {
            // Ignore and regenerate defaults.
        }

        var settings = new Settings();
        Save(settings);
        return settings;
    }

    public void Save(Settings settings)
    {
        EnsureParentDirectory();

        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(_path, json);
    }

    private void EnsureParentDirectory()
    {
        var parent = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        Directory.CreateDirectory(parent);
    }
}
