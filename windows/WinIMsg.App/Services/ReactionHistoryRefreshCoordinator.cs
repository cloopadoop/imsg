using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record ReactionHistoryRefreshQueueResult(bool Queued, Task? RefreshTask);

public sealed class ReactionHistoryRefreshCoordinator(
    TimeSpan debounce,
    TimeSpan refreshTimeout,
    Func<ChatListItem, bool> isCurrentSelection,
    Func<ChatListItem, CancellationToken, Task> refreshSelectedHistoryAsync)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _queuedChatStableIds = new(StringComparer.OrdinalIgnoreCase);

    public ReactionHistoryRefreshQueueResult TryQueueSelectedRefresh(
        ImsgMessage message,
        ChatListItem? selectedChat,
        Action<string>? warningSink = null)
    {
        if (!message.IsReactionEvent ||
            selectedChat is null ||
            !selectedChat.ContainsStableId(message.ChatStableId))
        {
            return new ReactionHistoryRefreshQueueResult(false, null);
        }

        var stableId = selectedChat.StableId;
        lock (_gate)
        {
            if (!_queuedChatStableIds.Add(stableId))
            {
                return new ReactionHistoryRefreshQueueResult(false, null);
            }
        }

        var refreshTask = RefreshAfterDebounceAsync(selectedChat, stableId, warningSink);
        return new ReactionHistoryRefreshQueueResult(true, refreshTask);
    }

    private async Task RefreshAfterDebounceAsync(
        ChatListItem chat,
        string stableId,
        Action<string>? warningSink)
    {
        try
        {
            await Task.Delay(debounce);
            if (!isCurrentSelection(chat))
            {
                return;
            }

            using var timeout = new CancellationTokenSource(refreshTimeout);
            await refreshSelectedHistoryAsync(chat, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            warningSink?.Invoke("Reaction history refresh timed out or was canceled.");
        }
        catch (Exception ex)
        {
            warningSink?.Invoke($"Reaction history refresh failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _queuedChatStableIds.Remove(stableId);
            }
        }
    }
}
