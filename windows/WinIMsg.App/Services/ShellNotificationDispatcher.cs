using System.Diagnostics;

namespace WinIMsg.App.Services;

public sealed class ShellNotificationDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _logWarning;
    private readonly Action<string>? _logInfo;

    public ShellNotificationDispatcher(
        TimeSpan timeout,
        Action<string>? logWarning = null,
        Action<string>? logInfo = null)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(4) : timeout;
        _logWarning = logWarning;
        _logInfo = logInfo;
    }

    public bool TryQueue(string name, Func<Task> action)
    {
        if (!_gate.Wait(0))
        {
            _logWarning?.Invoke($"Skipped {name} because a previous shell notification is still running.");
            return false;
        }

        _ = Task.Run(() => RunAsync(name, action));
        return true;
    }

    private async Task RunAsync(string name, Func<Task> action)
    {
        var started = Stopwatch.StartNew();
        Task actionTask;
        try
        {
            actionTask = Task.Run(action);
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"{name} failed to start. {ex.GetType().Name}: {ex.Message}");
            _gate.Release();
            return;
        }

        try
        {
            var completed = await Task.WhenAny(actionTask, Task.Delay(_timeout)).ConfigureAwait(false);
            if (ReferenceEquals(completed, actionTask))
            {
                await actionTask.ConfigureAwait(false);
                _logInfo?.Invoke($"{name} completed. elapsedMs={started.ElapsedMilliseconds}");
            }
            else
            {
                _ = actionTask.ContinueWith(
                    task => _logWarning?.Invoke($"{name} failed after timeout. {task.Exception?.GetBaseException().GetType().Name}: {task.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                _logWarning?.Invoke($"{name} timed out after {_timeout.TotalSeconds:0.#}s; future shell notifications will continue.");
            }
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"{name} failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
