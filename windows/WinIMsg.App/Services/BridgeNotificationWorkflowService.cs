using WinIMsg.App.Contracts;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record BridgeNotificationProcessingResult(
    ImsgMessage Message,
    bool ShouldShowWindowsNotification,
    bool ShouldCountAsNewUnread,
    bool CachedMessage,
    bool PersistedCursor,
    IReadOnlyList<string> Warnings);

public sealed class BridgeNotificationWorkflowService
{
    private readonly IMessageCache _cache;
    private readonly WatchNotificationService _watchNotifications;
    private readonly object _cursorLock = new();
    private string _watchCursorScope = "default";
    private long? _lastPersistedWatchCursor;

    public BridgeNotificationWorkflowService(
        IMessageCache cache,
        WatchNotificationService watchNotifications)
    {
        _cache = cache;
        _watchNotifications = watchNotifications;
    }

    public void ResetWatchState(
        string scope,
        long? lastSeenRowId,
        DateTimeOffset? subscriptionStartedAtUtc)
    {
        lock (_cursorLock)
        {
            _watchCursorScope = string.IsNullOrWhiteSpace(scope) ? "default" : scope;
            _lastPersistedWatchCursor = lastSeenRowId is > 0 ? lastSeenRowId : null;
        }

        _watchNotifications.Reset(subscriptionStartedAtUtc, lastSeenRowId);
    }

    public async Task<BridgeNotificationProcessingResult> ProcessAsync(
        ImsgMessage message,
        bool notificationsEnabled,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var cached = false;
        if (!message.IsReactionEvent)
        {
            try
            {
                await _cache.UpsertMessagesAsync([message], cancellationToken);
                cached = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Unable to cache watch notification before UI update. {ex.GetType().Name}: {ex.Message}");
            }
        }

        var persistedCursor = await PersistWatchCursorAsync(message, warnings, cancellationToken);
        var decision = _watchNotifications.Evaluate(message, notificationsEnabled);
        return new BridgeNotificationProcessingResult(
            message,
            decision.ShouldShowWindowsNotification,
            decision.ShouldCountAsNewUnread,
            cached,
            persistedCursor,
            warnings);
    }

    private async Task<bool> PersistWatchCursorAsync(
        ImsgMessage message,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (message.Id is not > 0)
        {
            return false;
        }

        string scope;
        lock (_cursorLock)
        {
            if (_lastPersistedWatchCursor is not null && message.Id.Value <= _lastPersistedWatchCursor.Value)
            {
                return false;
            }

            _lastPersistedWatchCursor = message.Id.Value;
            scope = _watchCursorScope;
        }

        try
        {
            await _cache.SaveWatchCursorAsync(scope, message.Id.Value, cancellationToken);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add($"Unable to persist watch cursor row {message.Id.Value}. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
