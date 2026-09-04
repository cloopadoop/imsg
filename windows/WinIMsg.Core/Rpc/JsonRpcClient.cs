using System.Collections.Concurrent;
using System.Text.Json;

namespace WinIMsg.Core.Rpc;

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly TimeSpan _defaultTimeout;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private Exception? _closeReason;
    private long _nextId;

    public JsonRpcClient(TextReader reader, TextWriter writer, TimeSpan? defaultTimeout = null)
    {
        _reader = reader;
        _writer = writer;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public event EventHandler<string>? ProtocolError;

    public event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_readerTask is not null)
        {
            return;
        }

        _readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCancellation.Token), CancellationToken.None);
    }

    public async Task<T> InvokeAsync<T>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeRawAsync(method, parameters, cancellationToken);
        return result.Deserialize<T>(ImsgJson.Options)
               ?? throw new JsonRpcException("The JSON-RPC response result could not be deserialized.");
    }

    public async Task<JsonElement> InvokeRawAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_readerTask is null)
        {
            Start(cancellationToken);
        }

        ThrowIfClosed();

        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingRequest();
        if (!_pending.TryAdd(id, pending))
        {
            throw new JsonRpcException($"Could not register JSON-RPC request {id}.");
        }

        try
        {
            ThrowIfClosed();

            var payload = JsonSerializer.Serialize(
                new JsonRpcRequest(id, method, parameters),
                ImsgJson.Options);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(payload);
                await _writer.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_defaultTimeout);
            return await pending.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for imsg RPC method '{method}'.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _readerCancellation?.Cancel();
            if (_readerTask is not null)
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Best effort shutdown; process disposal owns the underlying streams.
        }
        finally
        {
            _readerCancellation?.Dispose();
            _sendLock.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? closeReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (line is null)
                {
                    closeReason = new EndOfStreamException("The imsg RPC stream ended.");
                    _closeReason = closeReason;
                    FailAllPending(closeReason);
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleLine(line);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            closeReason = ex;
            _closeReason = ex;
            FailAllPending(ex);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && closeReason is not null)
            {
                ConnectionClosed?.Invoke(this, new JsonRpcConnectionClosedEventArgs(closeReason));
            }
        }
    }

    private void HandleLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var idElement))
        {
            if (!TryReadId(idElement, out var id) || !_pending.TryRemove(id, out var pending))
            {
                ProtocolError?.Invoke(this, $"Received response for unknown request id: {idElement.GetRawText()}");
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                pending.Fail(JsonRpcRemoteException.From(error.Clone()));
                return;
            }

            pending.Complete(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }

        if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString();
            if (!string.IsNullOrWhiteSpace(method))
            {
                NotificationReceived?.Invoke(
                    this,
                    new JsonRpcNotification(
                        method,
                        root.TryGetProperty("params", out var parameters) ? parameters.Clone() : default));
            }

            return;
        }

        ProtocolError?.Invoke(this, $"Received unrecognized JSON-RPC line: {line}");
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.Fail(exception);
            }
        }
    }

    private void ThrowIfClosed()
    {
        if (_closeReason is not null)
        {
            throw _closeReason;
        }
    }

    private static bool TryReadId(JsonElement idElement, out long id)
    {
        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out id))
        {
            return true;
        }

        if (idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), out id))
        {
            return true;
        }

        id = 0;
        return false;
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonElement> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonElement> Task => _completion.Task;

        public void Complete(JsonElement result) => _completion.TrySetResult(result);

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}

public sealed class JsonRpcConnectionClosedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

internal sealed record JsonRpcRequest(long Id, string Method, object? Params)
{
    public string Jsonrpc { get; init; } = "2.0";
}
