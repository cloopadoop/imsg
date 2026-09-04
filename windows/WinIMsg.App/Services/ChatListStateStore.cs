using System.Globalization;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

/// <summary>
/// Single owner of the transient chat-row state that overlays what the Mac
/// and the local cache report: live latest-message dates/previews (so rows
/// re-sort and preview instantly) and transient unread markers (live inbound
/// messages not yet acknowledged). Chat-list projections merge exactly one
/// snapshot from here, so ordering/preview/unread can no longer drift apart
/// across ad hoc call sites. UI-thread affinity: all members are called from
/// the dispatcher thread.
/// </summary>
public sealed class ChatListStateStore
{
    private readonly Dictionary<string, string?> _unreadMessageChatIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _latestMessageDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _latestMessagePreviews = new(StringComparer.OrdinalIgnoreCase);

    public int TransientUnreadMessageCount => _unreadMessageChatIds.Count;

    /// <summary>Tracks a live inbound message as unread. Returns false for from-me/reaction/keyless messages.</summary>
    public bool TrackTransientUnread(ImsgMessage message)
    {
        if (message.IsFromMe || message.IsReactionEvent)
        {
            return false;
        }

        var key = UnreadMessageKey(message);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        _unreadMessageChatIds[key] = message.ChatStableId;
        if (!string.IsNullOrWhiteSpace(message.ChatStableId) && message.SortDate is { } sortDate)
        {
            BumpLatestDate(message.ChatStableId, sortDate);
        }

        return true;
    }

    /// <summary>Stamps the live latest date/preview for the message's chat. Returns false for reaction/chatless events.</summary>
    public bool TrackTransientLatestMessage(ImsgMessage message)
    {
        if (message.IsReactionEvent || string.IsNullOrWhiteSpace(message.ChatStableId))
        {
            return false;
        }

        if (message.SortDate is { } sortDate)
        {
            BumpLatestDate(message.ChatStableId, sortDate);
        }

        var preview = BuildMessagePreview(message);
        if (!string.IsNullOrWhiteSpace(preview))
        {
            _latestMessagePreviews[message.ChatStableId] = preview;
        }

        return true;
    }

    public void TrackTransientLatestMessage(IEnumerable<string> stableIds, string? preview, DateTimeOffset timestamp)
    {
        foreach (var stableId in stableIds.Where(static stableId => !string.IsNullOrWhiteSpace(stableId)))
        {
            BumpLatestDate(stableId, timestamp);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                _latestMessagePreviews[stableId] = preview;
            }
        }
    }

    public void StampQueuedSend(
        IEnumerable<string> stableIds,
        string? text,
        IReadOnlyList<string> attachmentPaths,
        DateTimeOffset queuedAt)
    {
        var preview = !string.IsNullOrWhiteSpace(text)
            ? text
            : attachmentPaths.Count switch
            {
                0 => null,
                1 => Path.GetFileName(attachmentPaths[0]),
                _ => $"{attachmentPaths.Count} attachments"
            };
        foreach (var stableId in stableIds)
        {
            _latestMessageDates[stableId] = queuedAt;
            if (!string.IsNullOrWhiteSpace(preview))
            {
                _latestMessagePreviews[stableId] = preview;
            }
        }
    }

    /// <summary>
    /// Reconciles transient unread against a fresh Mac chat snapshot: when the
    /// Mac reports a chat fully read (phone/other-device reads sync into
    /// chat.db), any transient markers for that chat are stale and must drop,
    /// otherwise badges stay high after the user reads elsewhere. Only call
    /// this with rows freshly fetched from the bridge, never with reprojected
    /// in-memory rows. Returns true when anything was removed.
    /// </summary>
    public bool ReconcileTransientUnread(IEnumerable<ImsgChat> freshChats)
    {
        var readStableIds = freshChats
            .Where(static chat => chat.UnreadCount == 0 && !chat.HasUnread && !string.IsNullOrWhiteSpace(chat.StableId))
            .Select(static chat => chat.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (readStableIds.Count == 0 || _unreadMessageChatIds.Count == 0)
        {
            return false;
        }

        var staleKeys = _unreadMessageChatIds
            .Where(pair => pair.Value is { Length: > 0 } stableId && readStableIds.Contains(stableId))
            .Select(static pair => pair.Key)
            .ToList();
        foreach (var key in staleKeys)
        {
            _unreadMessageChatIds.Remove(key);
        }

        return staleKeys.Count > 0;
    }

    /// <summary>Clears transient unread for a chat row. Returns true when anything was removed.</summary>
    public bool ClearTransientUnreadForChat(ChatListItem chat)
    {
        var keys = _unreadMessageChatIds
            .Where(pair => chat.ContainsStableId(pair.Value))
            .Select(static pair => pair.Key)
            .ToList();
        foreach (var key in keys)
        {
            _unreadMessageChatIds.Remove(key);
        }

        return keys.Count > 0;
    }

    public bool ClearAllTransientUnread()
    {
        if (_unreadMessageChatIds.Count == 0)
        {
            return false;
        }

        _unreadMessageChatIds.Clear();
        return true;
    }

    public IReadOnlyDictionary<string, int> BuildTransientUnreadCountsByStableId()
    {
        if (_unreadMessageChatIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return _unreadMessageChatIds.Values
            .Where(static stableId => !string.IsNullOrWhiteSpace(stableId))
            .GroupBy(static stableId => stableId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> MergeLatestMessagePreviews(IReadOnlyDictionary<string, string> cachedPreviews)
    {
        if (_latestMessagePreviews.Count == 0)
        {
            return cachedPreviews;
        }

        var merged = new Dictionary<string, string>(cachedPreviews, StringComparer.OrdinalIgnoreCase);
        foreach (var (stableId, preview) in _latestMessagePreviews)
        {
            if (!string.IsNullOrWhiteSpace(stableId) && !string.IsNullOrWhiteSpace(preview))
            {
                merged[stableId] = preview;
            }
        }

        return merged;
    }

    public IReadOnlyDictionary<string, DateTimeOffset> MergeLatestMessageDates(
        IReadOnlyDictionary<string, DateTimeOffset> cachedDates)
    {
        if (_latestMessageDates.Count == 0)
        {
            return cachedDates;
        }

        var merged = new Dictionary<string, DateTimeOffset>(cachedDates, StringComparer.OrdinalIgnoreCase);
        foreach (var (stableId, transientDate) in _latestMessageDates)
        {
            if (!merged.TryGetValue(stableId, out var cachedDate) || transientDate > cachedDate)
            {
                merged[stableId] = transientDate;
            }
        }

        return merged;
    }

    /// <summary>Advances a chat row's LastMessageAt when a live message is newer than the Mac's snapshot.</summary>
    public ImsgChat ApplyTransientLatestMessageDate(ImsgChat chat)
    {
        if (!_latestMessageDates.TryGetValue(chat.StableId, out var transientDate))
        {
            return chat;
        }

        var currentDate = ReadChatDate(chat.LastMessageAt);
        return currentDate is not null && currentDate.Value >= transientDate
            ? chat
            : chat with { LastMessageAt = transientDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
    }

    public static string BuildMessagePreview(ImsgMessage message)
    {
        var body = DisplayTextFormatter.MessageText(message.Text, string.Empty);
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return message.Attachments.Count == 0
            ? string.Empty
            : string.Join(", ", message.Attachments.Select(attachment => DisplayTextFormatter.SingleLine(attachment.DisplayName, "Attachment")));
    }

    private void BumpLatestDate(string stableId, DateTimeOffset candidate)
    {
        _latestMessageDates[stableId] = _latestMessageDates.TryGetValue(stableId, out var existing) && existing >= candidate
            ? existing
            : candidate;
    }

    private static string UnreadMessageKey(ImsgMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Guid))
        {
            return message.Guid!;
        }

        return string.Join(
            ':',
            message.ChatStableId,
            message.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            message.DateText,
            message.Text ?? string.Empty);
    }

    private static DateTimeOffset? ReadChatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }
}
