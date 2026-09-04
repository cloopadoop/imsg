using System.IO.Pipes;
using System.Text.Json;

namespace WinIMsg.App.Services;

internal static class SingleInstanceManager
{
    private const string MutexName = @"Local\WinIMsg.Singleton";
    private const string PipeName = "WinIMsg.Commands";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCancellation;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return createdNew;
    }

    public static void StartListening(Action<string[]> onCommand)
    {
        _listenerCancellation ??= new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(onCommand, _listenerCancellation.Token));
    }

    public static bool SignalPrimary(string[] args)
    {
        var normalizedArgs = args.Length == 0 ? ["--open"] : args;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 500);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(normalizedArgs));
                return true;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    public static void Stop()
    {
        try
        {
            _listenerCancellation?.Cancel();
            _listenerCancellation?.Dispose();
            _listenerCancellation = null;
        }
        catch
        {
            // Best effort shutdown.
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release failures during app teardown.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static async Task ListenLoopAsync(Action<string[]> onCommand, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var payload = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                onCommand(JsonSerializer.Deserialize<string[]>(payload) ?? []);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }
}
