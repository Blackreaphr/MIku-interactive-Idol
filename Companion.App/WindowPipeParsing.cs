using System.Text.Json;

namespace Companion.App;

internal static class WindowPipeParsing
{
    public static bool TryGetInt32Parameter(JsonElement container, string property, out int value)
    {
        value = 0;
        if (!TryGetProperty(container, property, out var element))
        {
            return false;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    public static bool TryGetInt64Parameter(JsonElement container, string property, out long value)
    {
        value = 0;
        if (!TryGetProperty(container, property, out var element))
        {
            return false;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    public static string MapWindowActionErrorCode(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "native_call_failed";
        }

        if (error.StartsWith("Denied by Allowlist", StringComparison.OrdinalIgnoreCase))
        {
            return "not_allowed_process";
        }

        if (error.StartsWith("Denied by", StringComparison.OrdinalIgnoreCase))
        {
            return "action_denied";
        }

        return "native_call_failed";
    }

    private static bool TryGetProperty(JsonElement container, string property, out JsonElement element)
    {
        element = default;
        if (container.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var candidate in container.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                element = candidate.Value;
                return true;
            }
        }

        return false;
    }
}
