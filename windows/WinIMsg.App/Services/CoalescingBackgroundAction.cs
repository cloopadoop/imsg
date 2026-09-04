namespace WinIMsg.App.Services;

public sealed class CoalescingBackgroundAction<T>
{
    private readonly object _sync = new();
    private readonly Func<T, Task> _action;
    private readonly TimeSpan _debounce;
    private readonly Action<string>? _logWarning;
    private readonly Action<string>? _logInfo;
    private readonly string _name;
    private bool _hasPending;
    private bool _workerRunning;
    private T? _pending;

    public CoalescingBackgroundAction(
        string name,
        TimeSpan debounce,
        Func<T, Task> action,
        Action<string>? logWarning = null,
        Action<string>? logInfo = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? "background action" : name;
        _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
        _action = action;
        _logWarning = logWarning;
        _logInfo = logInfo;
    }

    public void Enqueue(T value)
    {
        lock (_sync)
        {
            _pending = value;
            _hasPending = true;
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                if (_debounce > TimeSpan.Zero)
                {
                    await Task.Delay(_debounce).ConfigureAwait(false);
                }

                T value;
                lock (_sync)
                {
                    if (!_hasPending)
                    {
                        _workerRunning = false;
                        return;
                    }

                    value = _pending!;
                    _pending = default;
                    _hasPending = false;
                }

                try
                {
                    await _action(value).ConfigureAwait(false);
                    _logInfo?.Invoke($"{_name} completed.");
                }
                catch (Exception ex)
                {
                    _logWarning?.Invoke($"{_name} failed. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"{_name} worker failed. {ex.GetType().Name}: {ex.Message}");
            lock (_sync)
            {
                _workerRunning = false;
            }
        }
    }
}
