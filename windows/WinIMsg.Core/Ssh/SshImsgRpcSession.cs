using System.Diagnostics;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Core.Ssh;

public sealed class SshImsgRpcSession : IAsyncDisposable
{
    private readonly Process _process;

    private SshImsgRpcSession(Process process, JsonRpcClient rpc)
    {
        _process = process;
        Rpc = rpc;
    }

    public JsonRpcClient Rpc { get; }

    public bool IsRunning => !_process.HasExited;

    public static Task<SshImsgRpcSession> StartAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var startInfo = SshProcessFactory.Create(settings, ["rpc"]);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // stderr must be drained: ssh/imsg diagnostics left in the pipe fill
        // its buffer and can stall the whole session once it is full.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is not null)
                {
                }
            }
            catch
            {
                // The process ended or the pipe closed; nothing to drain.
            }
        }, CancellationToken.None);

        var rpc = new JsonRpcClient(process.StandardOutput, process.StandardInput, settings.RequestTimeout);
        rpc.Start(cancellationToken);
        return Task.FromResult(new SshImsgRpcSession(process, rpc));
    }

    public async ValueTask DisposeAsync()
    {
        await Rpc.DisposeAsync();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort process cleanup.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
