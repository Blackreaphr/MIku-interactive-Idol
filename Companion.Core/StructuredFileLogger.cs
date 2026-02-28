using System.Text.Json;

namespace Companion.Core;

public sealed class StructuredFileLogger
{
    private readonly object _lock = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public StructuredFileLogger(string directory, int retentionDays = 7)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
        Prune();
    }

    public void Info(string eventName, Dictionary<string, object?>? data = null)
    {
        Write("info", eventName, data);
    }

    public void Warn(string eventName, Dictionary<string, object?>? data = null)
    {
        Write("warn", eventName, data);
    }

    public void Error(string eventName, Exception? ex = null, Dictionary<string, object?>? data = null)
    {
        var payload = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);

        if (ex is not null)
        {
            payload["exceptionType"] = ex.GetType().FullName;
            payload["exceptionMessage"] = ex.Message;
        }

        Write("error", eventName, payload);
    }

    private void Write(string level, string eventName, Dictionary<string, object?>? data)
    {
        var entry = new LogEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = level,
            EventName = eventName,
            Data = data ?? new Dictionary<string, object?>()
        };

        var line = JsonSerializer.Serialize(entry, _jsonOptions);
        var path = GetLogPath(DateTimeOffset.UtcNow);

        lock (_lock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private string GetLogPath(DateTimeOffset whenUtc)
    {
        return Path.Combine(_directory, $"companion-{whenUtc:yyyy-MM-dd}.log");
    }

    private void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.GetFiles(_directory, "companion-*.log"))
            {
                var created = File.GetCreationTimeUtc(file);
                if (created < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Logging must not crash app startup.
        }
    }
}
