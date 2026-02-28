using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.App;

public sealed record PipeControlRequest(string Id, string Method, JsonElement Params);

public sealed class PipeControlError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }
}

public sealed class PipeControlResponse
{
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PipeControlError? Error { get; init; }

    public static PipeControlResponse Success(string id, object? result)
    {
        return new PipeControlResponse
        {
            Id = id,
            Ok = true,
            Result = result,
            Error = null
        };
    }

    public static PipeControlResponse Failure(string id, string code, string message, object? details = null)
    {
        return new PipeControlResponse
        {
            Id = id,
            Ok = false,
            Result = null,
            Error = new PipeControlError
            {
                Code = code,
                Message = message,
                Details = details
            }
        };
    }
}
