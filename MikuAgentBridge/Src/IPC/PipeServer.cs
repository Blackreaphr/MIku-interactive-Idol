using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikuAgentBridge.IPC;

public interface ICommandRouter
{
    Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken);
}

public sealed class PipeServer : IAsyncDisposable
{
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _pipeName;
    private readonly string _expectedToken;
    private readonly ICommandRouter _router;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<Exception, string> _logError;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly List<Task> _clientTasks = new();

    private Task? _acceptTask;
    private bool _started;

    public PipeServer(
        string pipeName,
        string expectedToken,
        ICommandRouter router,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<Exception, string> logError)
    {
        _pipeName = pipeName;
        _expectedToken = expectedToken;
        _router = router;
        _logInfo = logInfo;
        _logWarn = logWarn;
        _logError = logError;
    }

    public void Start()
    {
        lock (_sync)
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

        lock (_sync)
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
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        await Task.WhenAll(clientTasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var task = HandleClientAsync(server, cancellationToken);
                TrackClientTask(task);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server.Dispose();
                _logError(ex, "pipe_accept_failed");
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using (stream)
        {
            while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                byte[]? payload;
                try
                {
                    payload = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (payload is null)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logError(ex, "pipe_read_failed");
                    break;
                }

                BridgeResponse response;
                try
                {
                    response = await ProcessRequestAsync(payload, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logError(ex, "pipe_dispatch_failed");
                    response = BridgeResponse.Failure(string.Empty, "internal_error", "Internal server error.");
                }

                try
                {
                    await WriteFrameAsync(stream, response, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logError(ex, "pipe_write_failed");
                    break;
                }
            }
        }
    }

    private async Task<BridgeResponse> ProcessRequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logWarn($"Rejected malformed JSON frame: {ex.Message}");
            return BridgeResponse.Failure(string.Empty, "invalid_request", "Malformed JSON frame.");
        }

        if (request is null)
        {
            return BridgeResponse.Failure(string.Empty, "invalid_request", "Request body missing.");
        }

        var id = request.Id ?? string.Empty;

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return BridgeResponse.Failure(id, "invalid_request", "Request 'id' is required.");
        }

        if (!string.Equals(request.Token, _expectedToken, StringComparison.Ordinal))
        {
            return BridgeResponse.Failure(id, "unauthorized", "Invalid token.");
        }

        if (string.IsNullOrWhiteSpace(request.Cmd))
        {
            return BridgeResponse.Failure(id, "invalid_request", "Request 'cmd' is required.");
        }

        return await _router.HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void TrackClientTask(Task task)
    {
        lock (_sync)
        {
            _clientTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    _logError(completed.Exception, "pipe_client_failed");
                }

                lock (_sync)
                {
                    _clientTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerBytes = await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new InvalidDataException("Truncated frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid frame length: {length}");
        }

        var payload = new byte[length];
        var payloadBytes = await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes != payload.Length)
        {
            throw new InvalidDataException("Truncated frame payload.");
        }

        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, BridgeResponse response, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header.AsMemory(0, header.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }

        return offset;
    }
}
