using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Companion.App;
using Companion.Core;

namespace Companion.Tests;

public sealed class PipeControlServerTests
{
    [Fact]
    public async Task RejectsUnauthorizedRequests()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CompanionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var logger = new StructuredFileLogger(Path.Combine(tempRoot, "logs"));
        var pipeName = $"companion.test.{Guid.NewGuid():N}";
        const string expectedToken = "expected-token";

        await using var server = new PipeControlServer(
            pipeName,
            expectedToken,
            static (request, _) => Task.FromResult(PipeControlResponse.Success(request.Id, new { ok = true })),
            logger);
        server.Start();

        using var response = await SendRequestAsync(
            pipeName,
            new
            {
                id = "1",
                token = "wrong-token",
                method = "ping",
                @params = new { }
            });

        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unauthorized", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SupportsMethodDispatchAndUnknownMethodErrors()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CompanionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var logger = new StructuredFileLogger(Path.Combine(tempRoot, "logs"));
        var pipeName = $"companion.test.{Guid.NewGuid():N}";
        const string expectedToken = "expected-token";

        await using var server = new PipeControlServer(
            pipeName,
            expectedToken,
            static (request, _) =>
            {
                if (string.Equals(request.Method, "ping", StringComparison.Ordinal))
                {
                    return Task.FromResult(PipeControlResponse.Success(request.Id, new { pong = true }));
                }

                return Task.FromResult(
                    PipeControlResponse.Failure(request.Id, "unknown_method", "Unknown method."));
            },
            logger);
        server.Start();

        using var pingResponse = await SendRequestAsync(
            pipeName,
            new
            {
                id = "2",
                token = expectedToken,
                method = "ping",
                @params = new { }
            });
        Assert.True(pingResponse.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(pingResponse.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());

        using var unknownResponse = await SendRequestAsync(
            pipeName,
            new
            {
                id = "3",
                token = expectedToken,
                method = "does_not_exist",
                @params = new { }
            });
        Assert.False(unknownResponse.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_method", unknownResponse.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<JsonDocument> SendRequestAsync(string pipeName, object payload)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(3000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        var requestJson = JsonSerializer.Serialize(payload);
        await writer.WriteLineAsync(requestJson);

        var responseLine = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrWhiteSpace(responseLine));
        return JsonDocument.Parse(responseLine!);
    }
}
