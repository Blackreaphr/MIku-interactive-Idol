using System.IO;
using Companion.Core;

namespace Companion.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void LoadOrCreate_CreatesDefaultsWhenMissingAndPersists()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "settings.json");
        var store = new JsonSettingsStore<CompanionSettings>(path);

        var initial = store.LoadOrCreate();
        Assert.NotNull(initial);
        Assert.True(File.Exists(path));

        initial.CaptureMinIntervalMs = 333;
        store.Save(initial);

        var reloaded = store.LoadOrCreate();
        Assert.Equal(333, reloaded.CaptureMinIntervalMs);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CompanionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path!;
    }
}
