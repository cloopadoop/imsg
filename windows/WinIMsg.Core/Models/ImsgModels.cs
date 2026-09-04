using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIMsg.Core.Models;

public sealed record ImsgChat
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("contact_name")]
    public string? ContactName { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("last_message_at")]
    public string? LastMessageAt { get; init; }

    [JsonPropertyName("unread_count")]
    public int? UnreadCountRaw { get; init; }

    [JsonPropertyName("unread")]
    public JsonElement? UnreadRaw { get; init; }

    [JsonPropertyName("is_unread")]
    public JsonElement? IsUnreadRaw { get; init; }

    [JsonPropertyName("has_unread")]
    public JsonElement? HasUnreadRaw { get; init; }

    [JsonPropertyName("read")]
    public JsonElement? ReadRaw { get; init; }

    [JsonPropertyName("participants")]
    public IReadOnlyList<string> Participants { get; init; } = [];

    [JsonPropertyName("is_group")]
    public bool IsGroup { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public string StableId => Id?.ToString() ?? Guid ?? Identifier ?? DisplayName;

    [JsonIgnore]
    public int UnreadCount => UnreadCountRaw ??
        JsonElementReaders.ReadInt32(UnreadRaw) ??
        ReadExtraInt("unread_count") ??
        ReadExtraInt("unread") ??
        0;

    [JsonIgnore]
    public bool HasUnread
    {
        get
        {
            if (UnreadCount > 0 ||
                JsonElementReaders.ReadBoolean(IsUnreadRaw) ||
                JsonElementReaders.ReadBoolean(HasUnreadRaw) ||
                ReadExtraBoolean("is_unread") ||
                ReadExtraBoolean("has_unread"))
            {
                return true;
            }

            return ReadRaw is not null && !JsonElementReaders.ReadBoolean(ReadRaw);
        }
    }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ContactName))
            {
                return ContactName;
            }

            if (IsGroup && !string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            if (Participants.Count > 0)
            {
                return string.Join(", ", Participants.Take(3));
            }

            var identifierAddress = AddressFromChatIdentifier(Identifier);
            if (!string.IsNullOrWhiteSpace(identifierAddress))
            {
                return identifierAddress;
            }

            return Identifier ?? Guid ?? Name ?? $"Chat {Id}";
        }
    }

    private static string? AddressFromChatIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var parts = identifier.Split(';');
        return parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[^1])
            ? parts[^1].Trim()
            : null;
    }

    private int? ReadExtraInt(string key)
    {
        return Extra is not null && Extra.TryGetValue(key, out var element)
            ? JsonElementReaders.ReadInt32(element)
            : null;
    }

    private bool ReadExtraBoolean(string key)
    {
        return Extra is not null &&
            Extra.TryGetValue(key, out var element) &&
            JsonElementReaders.ReadBoolean(element);
    }
}

public sealed record ImsgMessage
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("chat_id")]
    public long? ChatId { get; init; }

    [JsonPropertyName("chat_identifier")]
    public string? ChatIdentifier { get; init; }

    [JsonPropertyName("chat_guid")]
    public string? ChatGuid { get; init; }

    [JsonPropertyName("chat_name")]
    public string? ChatName { get; init; }

    [JsonPropertyName("sender")]
    public string? Sender { get; init; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("is_from_me")]
    public bool IsFromMe { get; init; }

    [JsonPropertyName("date")]
    public JsonElement? Date { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("date_created")]
    public string? DateCreated { get; init; }

    [JsonPropertyName("timestamp")]
    public JsonElement? Timestamp { get; init; }

    [JsonPropertyName("time")]
    public JsonElement? Time { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<ImsgAttachment> Attachments { get; init; } = [];

    [JsonPropertyName("reactions")]
    public IReadOnlyList<ImsgReaction> Reactions { get; init; } = [];

    [JsonPropertyName("is_reaction")]
    public bool IsReaction { get; init; }

    [JsonPropertyName("reaction_type")]
    public string? ReactionType { get; init; }

    [JsonPropertyName("reaction_emoji")]
    public string? ReactionEmoji { get; init; }

    [JsonPropertyName("is_reaction_add")]
    public bool? IsReactionAdd { get; init; }

    [JsonPropertyName("reacted_to_guid")]
    public string? ReactedToGuid { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public PendingSendRetryInfo? PendingSendRetry { get; init; }

    [JsonIgnore]
    public string ChatStableId => ChatId?.ToString() ?? ChatGuid ?? ChatIdentifier ?? string.Empty;

    [JsonIgnore]
    public bool IsReactionEvent => IsReaction || !string.IsNullOrWhiteSpace(ReactedToGuid);

    [JsonIgnore]
    public string? MessageActionId => !string.IsNullOrWhiteSpace(Guid) ? Guid : MessageId;

    [JsonIgnore]
    public DateTimeOffset? SortDate => ReadMessageDate();

    [JsonIgnore]
    public string DateText
    {
        get
        {
            var sortDate = SortDate;
            if (sortDate is not null)
            {
                return sortDate.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            }

            return ReadFirstDateText() ?? string.Empty;
        }
    }

    [JsonIgnore]
    public string DisplaySender => IsFromMe ? "Me" : SenderName ?? Sender ?? string.Empty;

    private DateTimeOffset? ReadMessageDate()
    {
        return JsonElementReaders.ReadDateTimeOffset(Date) ??
            ReadDateString(CreatedAt) ??
            ReadDateString(DateCreated) ??
            JsonElementReaders.ReadDateTimeOffset(Timestamp) ??
            JsonElementReaders.ReadDateTimeOffset(Time) ??
            ReadExtraDate("created_at") ??
            ReadExtraDate("date_created") ??
            ReadExtraDate("date_sent") ??
            ReadExtraDate("timestamp") ??
            ReadExtraDate("time");
    }

    private string? ReadFirstDateText()
    {
        return JsonElementReaders.ReadString(Date) ??
            CreatedAt ??
            DateCreated ??
            JsonElementReaders.ReadString(Timestamp) ??
            JsonElementReaders.ReadString(Time) ??
            ReadExtraString("created_at") ??
            ReadExtraString("date_created") ??
            ReadExtraString("date_sent") ??
            ReadExtraString("timestamp") ??
            ReadExtraString("time");
    }

    private DateTimeOffset? ReadExtraDate(string key)
    {
        return Extra is not null && Extra.TryGetValue(key, out var element)
            ? JsonElementReaders.ReadDateTimeOffset(element)
            : null;
    }

    private string? ReadExtraString(string key)
    {
        return Extra is not null && Extra.TryGetValue(key, out var element)
            ? JsonElementReaders.ReadString(element)
            : null;
    }

    private static DateTimeOffset? ReadDateString(string? value)
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

public sealed record ImsgAttachment
{
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("transfer_name")]
    public string? TransferName { get; init; }

    [JsonPropertyName("is_sticker")]
    public bool IsSticker { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("uti")]
    public string? Uti { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("original_path")]
    public string? OriginalPath { get; init; }

    [JsonPropertyName("converted_path")]
    public string? ConvertedPath { get; init; }

    [JsonPropertyName("converted_mime_type")]
    public string? ConvertedMimeType { get; init; }

    [JsonPropertyName("byte_size")]
    public long? ByteSize { get; init; }

    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; init; }

    [JsonPropertyName("missing")]
    public bool Missing { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public string? RemotePath => ConvertedPath ?? Path ?? OriginalPath;

    [JsonIgnore]
    public long? SizeBytes => ByteSize ?? TotalBytes;

    [JsonIgnore]
    public string DisplayName => SelectDisplayName(Filename, TransferName, OriginalPath, Path, ConvertedPath) ?? "Attachment";

    private static string? SelectDisplayName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var name = ExtractFileName(candidate);
            if (string.IsNullOrWhiteSpace(name) || IsMessagesPluginPayloadName(name))
            {
                continue;
            }

            return name;
        }

        return null;
    }

    private static string? ExtractFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('"', '\'');
        var separatorIndex = trimmed.LastIndexOfAny(['/', '\\']);
        if (separatorIndex >= 0 && separatorIndex < trimmed.Length - 1)
        {
            trimmed = trimmed[(separatorIndex + 1)..];
        }

        trimmed = trimmed.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool IsMessagesPluginPayloadName(string value)
    {
        return value.Equals("pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ImsgReaction
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }

    [JsonPropertyName("sender")]
    public string? Sender { get; init; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; init; }

    [JsonPropertyName("is_from_me")]
    public bool IsFromMe { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }
}

public sealed record RichTextFormattingRange(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("styles")] IReadOnlyList<string> Styles);

public sealed record PendingSendRetryInfo(
    string Kind,
    string? Text = null,
    string? Effect = null,
    string? EffectLabel = null,
    string? ReplyTo = null,
    string? ReplySummary = null,
    IReadOnlyList<RichTextFormattingRange>? TextFormatting = null,
    string? PollQuestion = null,
    IReadOnlyList<string>? PollOptions = null,
    IReadOnlyList<string>? AttachmentPaths = null)
{
    public const string AttachmentKind = "attachment";

    public const string RichKind = "rich";

    public const string PollKind = "poll";

    public static PendingSendRetryInfo Attachments(
        string? text,
        IReadOnlyList<string> attachmentPaths) =>
        new(
            AttachmentKind,
            Text: text,
            AttachmentPaths: attachmentPaths.ToList());

    public static PendingSendRetryInfo Rich(
        string text,
        string? effect,
        string? effectLabel,
        string? replyTo,
        string? replySummary,
        IReadOnlyList<RichTextFormattingRange> textFormatting) =>
        new(
            RichKind,
            Text: text,
            Effect: effect,
            EffectLabel: effectLabel,
            ReplyTo: replyTo,
            ReplySummary: replySummary,
            TextFormatting: textFormatting.ToList());

    public static PendingSendRetryInfo Poll(
        string question,
        IReadOnlyList<string> options,
        string? replyTo = null) =>
        new(
            PollKind,
            PollQuestion: question,
            PollOptions: options.ToList(),
            ReplyTo: replyTo);
}
