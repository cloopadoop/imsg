using System.Collections.Concurrent;
using WinIMsg.App.Services;

namespace WinIMsg.Tests;

public sealed class ShellCoordinationTests
{
    [Fact]
    public async Task CoalescingBackgroundActionAppliesLatestBurstValue()
    {
        var values = new ConcurrentQueue<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CoalescingBackgroundAction<int>(
            "test",
            TimeSpan.FromMilliseconds(25),
            value =>
            {
                values.Enqueue(value);
                completed.TrySetResult();
                return Task.CompletedTask;
            });

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([3], values.ToArray());
    }

    [Fact]
    public async Task CoalescingBackgroundActionSerializesInFlightWorkAndKeepsLatestPendingValue()
    {
        var values = new ConcurrentQueue<int>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxRunning = 0;

        var queue = new CoalescingBackgroundAction<int>(
            "test",
            TimeSpan.Zero,
            async value =>
            {
                var active = Interlocked.Increment(ref running);
                maxRunning = Math.Max(maxRunning, active);
                values.Enqueue(value);

                if (value == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }

                if (value == 3)
                {
                    latestCompleted.TrySetResult();
                }

                Interlocked.Decrement(ref running);
            });

        queue.Enqueue(1);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        queue.Enqueue(2);
        queue.Enqueue(3);
        releaseFirst.TrySetResult();

        await latestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 3], values.ToArray());
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task ShellNotificationDispatcherReleasesGateAfterTimeout()
    {
        var warnings = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new ShellNotificationDispatcher(
            TimeSpan.FromMilliseconds(40),
            warnings.Enqueue);

        Assert.True(dispatcher.TryQueue("first", () => stalled.Task));
        Assert.False(dispatcher.TryQueue("second", () => Task.CompletedTask));

        await WaitUntilAsync(() => warnings.Any(warning => warning.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
        Assert.True(dispatcher.TryQueue("third", () =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(warnings, warning => warning.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("Skipped second", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
