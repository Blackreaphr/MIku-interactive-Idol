using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core;

namespace Companion.App;

public sealed class PipeControlServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly string _expectedToken;
    private readonly Func<PipeControlRequest, CancellationToken, Task<PipeControlResponse>> _dispatchAsync;
    private readonly StructuredFileLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<Task> _clientTasks = new();
    private Task? _acceptTask;
    private bool _started;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PipeControlServer(
        string pipeName,
        string expectedToken,
        Func<PipeControlRequest, CancellationToken, Task<PipeControlResponse>> dispatchAsync,
        StructuredFileLogger logger)
    {
        _pipeName = pipeName;
        _expectedToken = expectedToken;
        _dispatchAsync = dispatchAsync;
        _logger = logger;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? acceptTask;
        Task[] clientTasks;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _cts.Cancel();
            acceptTask = _acceptTask;
            clientTasks = _clientTasks.ToArray();
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        await Task.WhenAll(clientTasks);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct);
                var clientTask = HandleClientAsync(server, ct);
                TrackClientTask(clientTask);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server.Dispose();
                _logger.Error("pipe_accept_failed", ex);
                try
                {
                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };

            while (server.IsConnected && !ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                var started = Stopwatch.StartNew();
                var (response, method, outcome) = await ProcessRequestLineAsync(line, ct);
                started.Stop();

                _logger.Info(
                    "pipe_request",
                    new Dictionary<string, object?>
                    {
                        ["method"] = method,
                        ["outcome"] = outcome,
                        ["latencyMs"] = Math.Round(started.Elapsed.TotalMilliseconds, 2)
                    });

                try
                {
                    var json = JsonSerializer.Serialize(response, JsonOptions);
                    await writer.WriteLineAsync(json);
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
    }

    private async Task<(PipeControlResponse Response, string Method, string Outcome)> ProcessRequestLineAsync(
        string line,
        CancellationToken ct)
    {
        string id = string.Empty;
        var method = "unknown";

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Request must be a JSON object.");
            }

            if (!TryGetString(root, "id", out id) || string.IsNullOrWhiteSpace(id))
            {
                return Invalid("Request 'id' is required.");
            }

            if (!TryGetString(root, "token", out var token))
            {
                return (PipeControlResponse.Failure(id, "unauthorized", "Missing token."), method, "unauthorized");
            }

            if (!TryGetString(root, "method", out method) || string.IsNullOrWhiteSpace(method))
            {
                return (
                    PipeControlResponse.Failure(id, "invalid_request", "Request 'method' is required."),
                    method,
                    "invalid_request");
            }

            if (!string.Equals(token, _expectedToken, StringComparison.Ordinal))
            {
                return (PipeControlResponse.Failure(id, "unauthorized", "Invalid token."), method, "unauthorized");
            }

            JsonElement parameters;
            if (TryGetProperty(root, "params", out var paramsElement))
            {
                parameters = paramsElement.Clone();
            }
            else
            {
                parameters = CreateEmptyJsonObject();
            }

            var request = new PipeControlRequest(id, method, parameters);
            var response = await _dispatchAsync(request, ct);
            return (response, method, response.Ok ? "ok" : response.Error?.Code ?? "error");
        }
        catch (JsonException ex)
        {
            _logger.Error("pipe_invalid_json", ex);
            return Invalid("Malformed JSON.");
        }
        catch (Exception ex)
        {
            _logger.Error("pipe_dispatch_failed", ex, new Dictionary<string, object?> { ["method"] = method });
            return (
                PipeControlResponse.Failure(id, "internal_error", "Internal server error."),
                method,
                "internal_error");
        }

        (PipeControlResponse Response, string Method, string Outcome) Invalid(string message)
        {
            return (
                PipeControlResponse.Failure(id, "invalid_request", message),
                method,
                "invalid_request");
        }
    }

    private void TrackClientTask(Task task)
    {
        lock (_lock)
        {
            _clientTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    _logger.Error("pipe_client_failed", completed.Exception);
                }

                lock (_lock)
                {
                    _clientTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool TryGetString(JsonElement container, string property, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(container, property, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
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

    private static JsonElement CreateEmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
