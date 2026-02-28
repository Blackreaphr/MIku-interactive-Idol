using System.Text.Json;

namespace Companion.Core;

public sealed class JsonSettingsStore<T> where T : new()
{
    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        _path = path;
    }

    public T LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var value = JsonSerializer.Deserialize<T>(json);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch
        {
            // Ignore and create defaults.
        }

        var fresh = new T();
        Save(fresh);
        return fresh;
    }

    public void Save(T settings)
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
