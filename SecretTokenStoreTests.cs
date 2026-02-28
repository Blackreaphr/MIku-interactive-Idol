using System.IO;
using Companion.Core;

namespace Companion.Tests;

public sealed class SecretTokenStoreTests
{
    [Fact]
    public void LoadOrCreate_ReturnsStableTokenAcrossReads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CompanionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var tokenPath = Path.Combine(root, "token.txt");
        var store = new SecretTokenStore(tokenPath);

        var first = store.LoadOrCreate();
        var second = store.LoadOrCreate();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }
}
