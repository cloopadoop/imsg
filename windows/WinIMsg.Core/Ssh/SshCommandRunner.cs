using System.Diagnostics;
using WinIMsg.Core.Models;

namespace WinIMsg.Core.Ssh;

public sealed class SshCommandRunner
{
    public async Task<ProcessResult> RunImsgCommandAsync(
        ImsgBridgeSettings settings,
        IReadOnlyList<string> imsgArguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = SshProcessFactory.Create(settings, imsgArguments);
        return await RunAsync(startInfo, settings.RequestTimeout, cancellationToken);
    }

    public async Task<ProcessResult> RunShellCommandAsync(
        ImsgBridgeSettings settings,
        string shellCommand,
        CancellationToken cancellationToken = default)
    {
        var startInfo = SshProcessFactory.CreateRemoteCommand(settings, ["sh", "-lc", QuoteRemoteShellArgument(shellCommand)]);
        return await RunAsync(startInfo, settings.RequestTimeout, cancellationToken);
    }

    internal static string QuoteRemoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask, false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(null, await BestEffort(stdoutTask), await BestEffort(stderrTask), true);
        }
    }

    private static async Task<string> BestEffort(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
