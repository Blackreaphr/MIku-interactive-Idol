using System.Text.Json;
using Companion.App;

namespace Companion.Tests;

public sealed class WindowPipeParsingTests
{
    [Fact]
    public void TryGetInt64Parameter_ParsesValidNumbers()
    {
        using var doc = JsonDocument.Parse("""{ "hwnd": 12345 }""");
        var ok = WindowPipeParsing.TryGetInt64Parameter(doc.RootElement, "hwnd", out var hwnd);

        Assert.True(ok);
        Assert.Equal(12345, hwnd);
    }

    [Fact]
    public void TryGetInt32Parameter_FailsForMissingOrWrongType()
    {
        using var missing = JsonDocument.Parse("""{ }""");
        Assert.False(WindowPipeParsing.TryGetInt32Parameter(missing.RootElement, "x", out _));

        using var wrongType = JsonDocument.Parse("""{ "x": "12" }""");
        Assert.False(WindowPipeParsing.TryGetInt32Parameter(wrongType.RootElement, "x", out _));
    }

    [Fact]
    public void MapWindowActionErrorCode_MapsPolicyAndNativeErrors()
    {
        Assert.Equal("action_denied", WindowPipeParsing.MapWindowActionErrorCode("Denied by MischiefGate."));
        Assert.Equal("not_allowed_process", WindowPipeParsing.MapWindowActionErrorCode("Denied by Allowlist. Process 'foo' is not allowed."));
        Assert.Equal("native_call_failed", WindowPipeParsing.MapWindowActionErrorCode("SetWindowPos failed."));
        Assert.Equal("native_call_failed", WindowPipeParsing.MapWindowActionErrorCode(null));
    }
}
