using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class WatchNotificationService
{
    private readonly HashSet<string> _notifiedMessageKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _subscriptionStartedAtUtc;
    private long? _lastSeenRowId;

    public void Reset(DateTimeOffset? subscriptionStartedAtUtc, long? lastSeenRowId = null)
    {
        _subscriptionStartedAtUtc = subscriptionStartedAtUtc;
        _lastSeenRowId = lastSeenRowId is > 0 ? lastSeenRowId : null;
        _notifiedMessageKeys.Clear();
    }

    public WatchNotificationDecision Evaluate(ImsgMessage message, bool notificationsEnabled)
    {
        if (message.Id is > 0 && _lastSeenRowId is not null && message.Id <= _lastSeenRowId.Value)
        {
            return new WatchNotificationDecision(false, false);
        }

        if (message.Id is > 0)
        {
            _lastSeenRowId = _lastSeenRowId is null ? message.Id : Math.Max(_lastSeenRowId.Value, message.Id.Value);
        }

        if (message.IsFromMe || message.IsReactionEvent)
        {
            return new WatchNotificationDecision(false, false);
        }

        var key = NotificationKey(message);
        if (string.IsNullOrWhiteSpace(key) || !_notifiedMessageKeys.Add(key))
        {
            return new WatchNotificationDecision(false, false);
        }

        // Watch replays messages delivered to the Mac while this client was
        // disconnected (sleep/wake, network loss). Those were usually already
        // read on another device, so only messages dated at or after the
        // current subscription window may count as new unread. A null
        // subscription start means the window is not established yet
        // (mid-connect backlog replay) and nothing should count as new.
        if (_subscriptionStartedAtUtc is null)
        {
            return new WatchNotificationDecision(false, false);
        }

        // SortDate covers every timestamp shape the bridge emits; RPC watch
        // events carry only created_at, so reading message.Date alone would
        // leave replayed backlog undated and wrongly counted as current.
        var messageTime = message.SortDate;
        var isCurrent = messageTime is not null &&
            messageTime.Value >= _subscriptionStartedAtUtc.Value.AddMinutes(-1);

        return new WatchNotificationDecision(notificationsEnabled && isCurrent, isCurrent);
    }

    public bool ShouldShowWindowsNotification(ImsgMessage message, bool notificationsEnabled) =>
        Evaluate(message, notificationsEnabled).ShouldShowWindowsNotification;

    private static string NotificationKey(ImsgMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Guid))
        {
            return message.Guid!;
        }

        return string.Join(
            ':',
            message.ChatStableId,
            message.Id?.ToString() ?? string.Empty,
            message.DateText,
            message.Text ?? string.Empty);
    }
}

public sealed record WatchNotificationDecision(
    bool ShouldShowWindowsNotification,
    bool ShouldCountAsNewUnread);
