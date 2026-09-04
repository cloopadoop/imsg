using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinIMsg.App.Services;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed record ChatListItem(
    ImsgChat Chat,
    IReadOnlyList<ImsgChat>? SourceChats = null,
    IReadOnlyDictionary<string, string>? LatestMessagePreviewsByStableId = null,
    IReadOnlyDictionary<string, DateTimeOffset>? LatestMessageDatesByStableId = null,
    IReadOnlyDictionary<string, int>? TransientUnreadCountsByStableId = null)
{
    public const string NewMessageDraftStableId = "win-imsg:new-message-draft";
    private static readonly Regex RecipientConjunctionSeparator = new(
        @"\s+(?:and|&)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Windows.UI.Text.FontWeight NormalWeight = new() { Weight = 400 };
    private static readonly Windows.UI.Text.FontWeight SemiBoldWeight = new() { Weight = 600 };
    private static readonly Windows.UI.Text.FontWeight BoldWeight = new() { Weight = 700 };
    // Brushes are DependencyObjects with UI-thread affinity, so they are
    // created lazily on first binding read (always the UI thread) rather than
    // in static initializers, which can run on projection worker threads.
    private static Brush? _sharedAvatarBrush;
    private static Brush? _unreadRowBrush;
    private static Brush? _transparentRowBrush;
    private static Brush? _unreadTimestampBrush;
    private static Brush? _readTimestampBrush;

    public IReadOnlyList<ImsgChat> Chats => SourceChats is { Count: > 0 } ? SourceChats : [Chat];

    public string StableId => Chat.StableId;

    public IReadOnlyList<string> StableIds => Chats
        .Select(static chat => chat.StableId)
        .Where(static stableId => !string.IsNullOrWhiteSpace(stableId))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool IsMerged => Chats.Count > 1;

    public bool IsNewMessageDraft => string.Equals(StableId, NewMessageDraftStableId, StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsNewMessageDraft ? "New Message" : Chat.DisplayName;

    public string AvatarText => BuildAvatarText(DisplayName);

    public string TimestampText => EffectiveLatestMessageDate is { } timestamp ? FormatTimestamp(timestamp) : string.Empty;

    public int UnreadCount => Chats.Sum(static chat => chat.UnreadCount) + TransientUnreadCount;

    public int TransientUnreadCount => TransientUnreadCountsByStableId is null
        ? 0
        : Chats.Sum(chat => TransientUnreadCountsByStableId.TryGetValue(chat.StableId, out var count) ? count : 0);

    public bool HasUnread => UnreadCount > 0 || Chats.Any(static chat => chat.HasUnread);

    public string UnreadBadgeText => UnreadCount > 0 ? Math.Min(UnreadCount, 99).ToString(CultureInfo.CurrentCulture) : string.Empty;

    public Visibility UnreadBadgeVisibility => HasUnread ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnreadDotVisibility => HasUnread ? Visibility.Visible : Visibility.Collapsed;

    public Brush AvatarBackgroundBrush => _sharedAvatarBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212));

    public Brush RowBackgroundBrush => HasUnread
        ? _unreadRowBrush ??= new SolidColorBrush(ColorHelper.FromArgb(24, 0, 120, 212))
        : _transparentRowBrush ??= new SolidColorBrush(Colors.Transparent);

    public Brush TimestampBrush => HasUnread
        ? _unreadTimestampBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0, 95, 184))
        : _readTimestampBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 96, 96, 96));

    public Windows.UI.Text.FontWeight TitleFontWeight => HasUnread ? BoldWeight : SemiBoldWeight;

    public Windows.UI.Text.FontWeight DetailFontWeight => HasUnread ? SemiBoldWeight : NormalWeight;

    public string Detail
    {
        get
        {
            if (IsNewMessageDraft)
            {
                return Chat.Participants.Count == 0
                    ? "No recipients"
                    : string.Join(", ", Chat.Participants.Take(3));
            }

            var latestMessagePreview = LatestMessagePreview;
            if (!string.IsNullOrWhiteSpace(latestMessagePreview))
            {
                return latestMessagePreview;
            }

            var detail = Chat.IsGroup
                ? $"{Chat.Participants.Count} people"
                : DisplayTextFormatter.SingleLine(Chat.Identifier ?? Chat.Service, string.Empty, maxLength: 160);
            return detail;
        }
    }

    public string LatestMessagePreview
    {
        get
        {
            foreach (var chat in Chats
                .Select((chat, index) => new { Chat = chat, Index = index, SortDate = ReadDate(chat.LastMessageAt) })
                .OrderByDescending(item => item.SortDate ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Index)
                .Select(item => item.Chat))
            {
                if (LatestMessagePreviewsByStableId is not null &&
                    LatestMessagePreviewsByStableId.TryGetValue(chat.StableId, out var cachedPreview) &&
                    !string.IsNullOrWhiteSpace(cachedPreview))
                {
                    return DisplayTextFormatter.SingleLine(cachedPreview, string.Empty, maxLength: 160);
                }

                var preview = ReadLatestMessagePreview(chat);
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    return preview;
                }
            }

            return string.Empty;
        }
    }

    public static ChatListItem From(
        ImsgChat chat,
        IReadOnlyDictionary<string, string>? latestMessagePreviewsByStableId = null,
        IReadOnlyDictionary<string, int>? transientUnreadCountsByStableId = null) =>
        new(
            chat,
            LatestMessagePreviewsByStableId: latestMessagePreviewsByStableId,
            TransientUnreadCountsByStableId: transientUnreadCountsByStableId);

    public static ChatListItem NewMessageDraft(string recipients)
    {
        var recipientList = ParseRecipientText(recipients);
        return new ChatListItem(new ImsgChat
        {
            Identifier = NewMessageDraftStableId,
            Guid = NewMessageDraftStableId,
            Name = "New Message",
            Service = "iMessage",
            Participants = recipientList,
            IsGroup = recipientList.Count > 1
        });
    }

    public static IReadOnlyList<ChatListItem> FromChats(
        IEnumerable<ImsgChat> chats,
        bool mergeByParticipants,
        string? phoneNumberRegion = null,
        IReadOnlyDictionary<string, string>? latestMessagePreviewsByStableId = null,
        IReadOnlyDictionary<string, DateTimeOffset>? latestMessageDatesByStableId = null,
        IReadOnlyDictionary<string, int>? transientUnreadCountsByStableId = null)
    {
        var orderedChats = chats
            .Select((chat, index) => new { Chat = chat, Index = index, SortDate = EffectiveDate(chat, latestMessageDatesByStableId) })
            .OrderByDescending(item => item.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Chat)
            .ToList();

        if (!mergeByParticipants)
        {
            return orderedChats.Select(chat => new ChatListItem(
                chat,
                LatestMessagePreviewsByStableId: latestMessagePreviewsByStableId,
                LatestMessageDatesByStableId: latestMessageDatesByStableId,
                TransientUnreadCountsByStableId: transientUnreadCountsByStableId)).ToList();
        }

        var keyedGroups = new Dictionary<string, List<ImsgChat>>(StringComparer.OrdinalIgnoreCase);
        var unkeyed = new List<ImsgChat>();
        foreach (var chat in orderedChats)
        {
            var key = ParticipantMergeKeyBuilder.Build(chat, phoneNumberRegion);
            if (string.IsNullOrWhiteSpace(key))
            {
                unkeyed.Add(chat);
                continue;
            }

            if (!keyedGroups.TryGetValue(key, out var group))
            {
                group = [];
                keyedGroups[key] = group;
            }

            group.Add(chat);
        }

        var merged = keyedGroups.Values
            .Select(group =>
            {
                var sources = group
                    .Select((chat, index) => new { Chat = chat, Index = index, SortDate = EffectiveDate(chat, latestMessageDatesByStableId) })
                    .OrderByDescending(item => item.SortDate ?? DateTimeOffset.MinValue)
                    .ThenBy(item => item.Index)
                    .Select(item => item.Chat)
                    .ToList();
                return new ChatListItem(sources[0], sources, latestMessagePreviewsByStableId, latestMessageDatesByStableId, transientUnreadCountsByStableId);
            })
            .Concat(unkeyed.Select(chat => new ChatListItem(
                chat,
                LatestMessagePreviewsByStableId: latestMessagePreviewsByStableId,
                LatestMessageDatesByStableId: latestMessageDatesByStableId,
                TransientUnreadCountsByStableId: transientUnreadCountsByStableId)))
            .Select((item, index) => new { Item = item, Index = index, SortDate = item.EffectiveLatestMessageDate })
            .OrderByDescending(item => item.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Item)
            .ToList();

        return merged;
    }

    public bool ContainsStableId(string? stableId)
    {
        return !string.IsNullOrWhiteSpace(stableId) &&
            Chats.Any(chat => string.Equals(chat.StableId, stableId, StringComparison.OrdinalIgnoreCase));
    }

    public DateTimeOffset? EffectiveLatestMessageDate => Chats
        .Select(chat => EffectiveDate(chat, LatestMessageDatesByStableId))
        .Where(static date => date is not null)
        .DefaultIfEmpty()
        .Max();

    private static DateTimeOffset? EffectiveDate(
        ImsgChat chat,
        IReadOnlyDictionary<string, DateTimeOffset>? latestMessageDatesByStableId)
    {
        var chatDate = ReadDate(chat.LastMessageAt);
        if (latestMessageDatesByStableId is null ||
            !latestMessageDatesByStableId.TryGetValue(chat.StableId, out var cachedDate))
        {
            return chatDate;
        }

        return chatDate is null || cachedDate > chatDate.Value ? cachedDate : chatDate;
    }

    private static string BuildAvatarText(string displayName)
    {
        var trimmed = DisplayTextFormatter.SingleLine(displayName, string.Empty, maxLength: 80);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "?";
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(TakeInitial(parts[0]), TakeInitial(parts[^1])).ToUpperInvariant();
        }

        return TakeInitial(trimmed).ToUpperInvariant();
    }

    private static string TakeInitial(string value)
    {
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                return character.ToString();
            }
        }

        return "?";
    }

    private static string FormatTimestamp(string? value)
    {
        var timestamp = ReadDate(value);
        if (timestamp is null)
        {
            return string.Empty;
        }

        return FormatTimestamp(timestamp.Value);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today)
        {
            return local.ToString("t", CultureInfo.CurrentCulture);
        }

        if (local.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    public static IReadOnlyList<string> ParseRecipientText(string recipients)
    {
        var normalized = RecipientConjunctionSeparator.Replace(recipients ?? string.Empty, ",");
        return normalized
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static recipient => !string.IsNullOrWhiteSpace(recipient))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ChatListItem WithUnreadCleared()
    {
        if (IsNewMessageDraft)
        {
            return this;
        }

        var chats = Chats.Select(ClearUnread).ToList();
        return new ChatListItem(
            chats[0],
            chats,
            LatestMessagePreviewsByStableId,
            LatestMessageDatesByStableId,
            TransientUnreadCountsByStableId: null);
    }

    private static DateTimeOffset? ReadDate(string? value)
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

    private static ImsgChat ClearUnread(ImsgChat chat)
    {
        IDictionary<string, JsonElement>? extra = null;
        if (chat.Extra is not null)
        {
            extra = new Dictionary<string, JsonElement>(chat.Extra, StringComparer.OrdinalIgnoreCase);
            extra.Remove("unread_count");
            extra.Remove("unread");
            extra.Remove("is_unread");
            extra.Remove("has_unread");
            extra.Remove("read");
        }

        return chat with
        {
            UnreadCountRaw = 0,
            UnreadRaw = null,
            IsUnreadRaw = null,
            HasUnreadRaw = null,
            ReadRaw = null,
            Extra = extra
        };
    }

    private static string ReadLatestMessagePreview(ImsgChat chat)
    {
        if (chat.Extra is null)
        {
            return string.Empty;
        }

        foreach (var key in LatestMessagePreviewKeys)
        {
            if (!chat.Extra.TryGetValue(key, out var element))
            {
                continue;
            }

            var preview = ReadPreviewElement(element);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                return DisplayTextFormatter.SingleLine(preview, string.Empty, maxLength: 160);
            }
        }

        return string.Empty;
    }

    private static string ReadPreviewElement(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var key in LatestMessageObjectPreviewKeys)
            {
                if (element.TryGetProperty(key, out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return nested.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static readonly string[] LatestMessagePreviewKeys =
    [
        "last_message_text",
        "last_message",
        "latest_message_text",
        "latest_message",
        "message_preview",
        "preview",
        "snippet",
        "body",
        "text"
    ];

    private static readonly string[] LatestMessageObjectPreviewKeys =
    [
        "text",
        "body",
        "preview",
        "snippet"
    ];

}
