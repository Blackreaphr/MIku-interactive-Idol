using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikuAgentBridge.IPC;

public sealed class BridgeRequest
{
    public string Id { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string Cmd { get; set; } = string.Empty;

    public JsonElement Args { get; set; }
}

public sealed class BridgeError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }
}

public sealed class BridgeResponse
{
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeError? Error { get; init; }

    public static BridgeResponse Success(string id, object? data)
    {
        return new BridgeResponse
        {
            Id = id,
            Ok = true,
            Data = data,
            Error = null
        };
    }

    public static BridgeResponse Failure(string id, string code, string message, object? details = null)
    {
        return new BridgeResponse
        {
            Id = id,
            Ok = false,
            Data = null,
            Error = new BridgeError
            {
                Code = code,
                Message = message,
                Details = details
            }
        };
    }
}
