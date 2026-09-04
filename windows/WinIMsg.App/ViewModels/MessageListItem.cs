using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WinIMsg.App.Services;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed class MessageListItem : INotifyPropertyChanged
{
    private ImsgMessage _message;
    private ImsgCapabilities _capabilities;
    private bool _isPending;
    private bool _isFailed;
    private bool _hasFailedAttachmentDownload;
    private string _deliveryStatus = string.Empty;
    private Func<ImsgMessage, ImsgAttachment, string>? _attachmentLocalPathResolver;
    private IReadOnlyList<MessageAttachmentListItem> _attachmentPreviews = [];

    public MessageListItem(
        ImsgMessage message,
        ImsgCapabilities capabilities,
        Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver = null)
    {
        _message = message;
        _capabilities = capabilities;
        _attachmentLocalPathResolver = attachmentLocalPathResolver;
        RebuildAttachmentPreviews();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImsgMessage Message
    {
        get => _message;
        private set
        {
            if (EqualityComparer<ImsgMessage>.Default.Equals(_message, value))
            {
                return;
            }

            _message = value;
            RebuildAttachmentPreviews();
            NotifyMessageProperties();
        }
    }

    public ImsgCapabilities Capabilities
    {
        get => _capabilities;
        private set
        {
            if (EqualityComparer<ImsgCapabilities>.Default.Equals(_capabilities, value))
            {
                return;
            }

            _capabilities = value;
            NotifyCapabilityProperties();
        }
    }

    public bool IsPending
    {
        get => _isPending;
        set
        {
            if (_isPending == value)
            {
                return;
            }

            _isPending = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeliveryProgressVisibility));
            OnPropertyChanged(nameof(HasPrimaryDisplayContent));
            OnPropertyChanged(nameof(HasDisplayContent));
            OnPropertyChanged(nameof(ItemVisibility));
        }
    }

    public bool IsFailed
    {
        get => _isFailed;
        set
        {
            if (_isFailed == value)
            {
                return;
            }

            _isFailed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRetrySend));
            OnPropertyChanged(nameof(RetrySendVisibility));
        }
    }

    public bool HasFailedAttachmentDownload
    {
        get => _hasFailedAttachmentDownload;
        set
        {
            if (_hasFailedAttachmentDownload == value)
            {
                return;
            }

            _hasFailedAttachmentDownload = value;
            OnPropertyChanged();
            NotifyAttachmentActionProperties();
        }
    }

    public string DeliveryStatus
    {
        get => _deliveryStatus;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_deliveryStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            _deliveryStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeliveryStatusVisibility));
            OnPropertyChanged(nameof(HasPrimaryDisplayContent));
            OnPropertyChanged(nameof(HasDisplayContent));
            OnPropertyChanged(nameof(ItemVisibility));
        }
    }

    public string Header
    {
        get
        {
            var sender = DisplayTextFormatter.SingleLine(Message.DisplaySender, Message.IsFromMe ? "Me" : string.Empty);
            var date = FormatTimestamp(Message.SortDate, Message.DateText);
            return string.IsNullOrWhiteSpace(date)
                ? sender
                : string.IsNullOrWhiteSpace(sender)
                    ? date
                    : $"{sender}  {date}";
        }
    }

    public string Text => Message.IsReactionEvent
        ? string.Empty
        : DisplayTextFormatter.MessageText(Message.Text, string.Empty);

    public Visibility TextVisibility => string.IsNullOrWhiteSpace(Text)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public MessageLinkPreviewItem? LinkPreview => Message.IsReactionEvent
        ? null
        : LinkPreviewPolicy.Extract(Message, _attachmentLocalPathResolver);

    public Visibility LinkPreviewVisibility => LinkPreview is null ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<MessageAttachmentListItem> AttachmentPreviews => _attachmentPreviews;

    public MessageAttachmentListItem? PrimaryAttachment => AttachmentPreviews.FirstOrDefault(attachment => attachment.CanOpen);

    public string AttachmentSummary => AttachmentPreviews.Count == 0
        ? string.Empty
        : string.Join(", ", AttachmentPreviews.Select(attachment => attachment.DisplayName));

    public Visibility AttachmentVisibility => string.IsNullOrWhiteSpace(AttachmentSummary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility MessageActionVisibility => HasActionableServerGuid(Message) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SentMessageActionVisibility => Message.IsFromMe && HasActionableServerGuid(Message)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AttachmentMenuVisibility => HasAttachmentActions ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AttachmentDownloadVisibility => HasDownloadableAttachment ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AttachmentRevealVisibility => HasDownloadedAttachment ? Visibility.Visible : Visibility.Collapsed;

    public bool CanRetryAttachmentDownload => HasAttachmentActions && HasFailedAttachmentDownload;

    public Visibility RetryAttachmentDownloadVisibility => CanRetryAttachmentDownload ? Visibility.Visible : Visibility.Collapsed;

    public bool CanRetrySend =>
        IsFailed &&
        Message.IsFromMe &&
        !IsPending &&
        Message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true &&
        (Message.PendingSendRetry is not null ||
            !string.IsNullOrWhiteSpace(Text) ||
            AttachmentPreviews.Count > 0);

    public Visibility RetrySendVisibility => CanRetrySend ? Visibility.Visible : Visibility.Collapsed;

    public string AttachmentUnavailableText => HasFailedAttachmentDownload
        ? "Attachment placeholder shown. The host Mac has not downloaded the file yet, so win-imsg cannot retrieve it until Messages finishes downloading it there."
        : string.Empty;

    public Visibility AttachmentUnavailableVisibility => HasFailedAttachmentDownload
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ReactionSummary => Message.IsReactionEvent || Message.Reactions.Count == 0
        ? string.Empty
        : string.Join(" ", Message.Reactions
            .Select(reaction => DisplayTextFormatter.SingleLine(reaction.Emoji ?? reaction.Type, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public Visibility ReactionVisibility => string.IsNullOrWhiteSpace(ReactionSummary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string? CurrentUserTapbackReaction => Message.IsReactionEvent
        ? null
        : Message.Reactions
        .Where(static reaction => reaction.IsFromMe)
        .Select(NormalizeTapbackReaction)
        .FirstOrDefault(static reaction => !string.IsNullOrWhiteSpace(reaction));

    public Visibility DeliveryProgressVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeliveryStatusVisibility => string.IsNullOrWhiteSpace(DeliveryStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool HasPrimaryDisplayContent =>
        !Message.IsReactionEvent &&
        (!string.IsNullOrWhiteSpace(Text) ||
            AttachmentPreviews.Count > 0 ||
            LinkPreview is not null ||
            IsPending ||
            !string.IsNullOrWhiteSpace(DeliveryStatus));

    public bool HasDisplayContent => HasPrimaryDisplayContent;

    public Visibility ItemVisibility => HasDisplayContent ? Visibility.Visible : Visibility.Collapsed;

    public bool CanTapback => CapabilityActionPolicy.CanTapback(Message, Capabilities);

    public bool CanReply => CapabilityActionPolicy.CanReply(Message, Capabilities);

    public Visibility ReplyVisibility => CanReply ? Visibility.Visible : Visibility.Collapsed;

    public bool CanEdit => CapabilityActionPolicy.CanEdit(Message, Capabilities);

    public bool CanUnsend => CapabilityActionPolicy.CanUnsend(Message, Capabilities);

    public bool CanDelete => CapabilityActionPolicy.CanDelete(Message, Capabilities);

    public bool CanNotifyAnyways => CapabilityActionPolicy.CanNotifyAnyways(Message, Capabilities);

    public bool HasAttachmentActions => PrimaryAttachment is not null;

    public bool HasDownloadableAttachment => AttachmentPreviews.Any(attachment => attachment.CanDownload);

    public bool HasDownloadedAttachment => AttachmentPreviews.Any(attachment => attachment.CanReveal);

    public bool CanOpenAttachment => HasAttachmentActions;

    public bool CanDownloadAttachment => HasDownloadableAttachment;

    public bool CanRevealAttachment => HasDownloadedAttachment;

    public HorizontalAlignment BubbleAlignment => Message.IsFromMe
        ? HorizontalAlignment.Right
        : HorizontalAlignment.Left;

    public Brush BubbleBrush => Message.IsFromMe
        ? new SolidColorBrush(IsCarrierMessage ? Colors.LimeGreen : Colors.DodgerBlue)
        : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    public Brush PrimaryTextBrush => Message.IsFromMe
        ? new SolidColorBrush(Colors.White)
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public Brush SecondaryTextBrush => Message.IsFromMe
        ? new SolidColorBrush(Colors.White) { Opacity = 0.78 }
        : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private bool IsCarrierMessage => IsCarrierService(Message.Service) ||
        Message.Extra?.Any(IsCarrierServiceField) == true;

    private static bool IsCarrierServiceField(KeyValuePair<string, JsonElement> field)
    {
        var key = field.Key;
        if (field.Value.ValueKind is JsonValueKind.True &&
            (key.Contains("sms", StringComparison.OrdinalIgnoreCase) ||
             key.Contains("rcs", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!key.Contains("service", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("sms", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("rcs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return field.Value.ValueKind == JsonValueKind.String &&
            IsCarrierService(field.Value.GetString());
    }

    private static bool IsCarrierService(string? service)
    {
        return !string.IsNullOrWhiteSpace(service) &&
            (service.Contains("sms", StringComparison.OrdinalIgnoreCase) ||
             service.Contains("rcs", StringComparison.OrdinalIgnoreCase) ||
             service.Contains("text message", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp, string fallback)
    {
        if (timestamp is null)
        {
            return DisplayTextFormatter.SingleLine(fallback, string.Empty);
        }

        var local = timestamp.Value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today)
        {
            return local.ToString("t", CultureInfo.CurrentCulture);
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday {local.ToString("t", CultureInfo.CurrentCulture)}";
        }

        return local.ToString("g", CultureInfo.CurrentCulture);
    }

    public static MessageListItem From(
        ImsgMessage message,
        ImsgCapabilities? capabilities = null,
        bool hasFailedAttachmentDownload = false,
        Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver = null)
    {
        return new(message, capabilities ?? new ImsgCapabilities(), attachmentLocalPathResolver)
        {
            HasFailedAttachmentDownload = hasFailedAttachmentDownload
        };
    }

    public static MessageListItem Pending(
        ImsgMessage message,
        ImsgCapabilities? capabilities = null,
        Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver = null)
    {
        return new MessageListItem(message, capabilities ?? new ImsgCapabilities(), attachmentLocalPathResolver)
        {
            IsPending = true,
            DeliveryStatus = "Sending..."
        };
    }

    public void RefreshFrom(MessageListItem replacement)
    {
        var deliveryStatus = string.IsNullOrWhiteSpace(replacement.DeliveryStatus) && !string.IsNullOrWhiteSpace(DeliveryStatus)
            ? DeliveryStatus
            : replacement.DeliveryStatus;
        Message = replacement.Message;
        Capabilities = replacement.Capabilities;
        HasFailedAttachmentDownload = replacement.HasFailedAttachmentDownload;
        IsPending = replacement.IsPending;
        IsFailed = replacement.IsFailed;
        DeliveryStatus = deliveryStatus;
    }

    public void SetCapabilities(ImsgCapabilities capabilities)
    {
        Capabilities = capabilities;
    }

    public void SetAttachmentLocalPathResolver(Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver)
    {
        _attachmentLocalPathResolver = attachmentLocalPathResolver;
        RebuildAttachmentPreviews();
        NotifyAttachmentProperties();
    }

    public void RefreshAttachmentLocalState()
    {
        foreach (var attachment in AttachmentPreviews)
        {
            attachment.RefreshLocalState();
        }

        OnPropertyChanged(nameof(LinkPreview));
        OnPropertyChanged(nameof(LinkPreviewVisibility));
        OnPropertyChanged(nameof(HasPrimaryDisplayContent));
        OnPropertyChanged(nameof(HasDisplayContent));
        OnPropertyChanged(nameof(ItemVisibility));
        NotifyAttachmentActionProperties();
    }

    public MessageListItem SetTransientState(bool isPending, bool isFailed, string deliveryStatus)
    {
        IsPending = isPending;
        IsFailed = isFailed;
        DeliveryStatus = deliveryStatus;
        return this;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void NotifyMessageProperties()
    {
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(TextVisibility));
        OnPropertyChanged(nameof(LinkPreview));
        OnPropertyChanged(nameof(LinkPreviewVisibility));
        OnPropertyChanged(nameof(HasPrimaryDisplayContent));
        OnPropertyChanged(nameof(HasDisplayContent));
        OnPropertyChanged(nameof(ItemVisibility));
        OnPropertyChanged(nameof(AttachmentPreviews));
        OnPropertyChanged(nameof(PrimaryAttachment));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(AttachmentVisibility));
        NotifyAttachmentActionProperties();
        OnPropertyChanged(nameof(ReactionSummary));
        OnPropertyChanged(nameof(ReactionVisibility));
        OnPropertyChanged(nameof(CurrentUserTapbackReaction));
        OnPropertyChanged(nameof(HasPrimaryDisplayContent));
        OnPropertyChanged(nameof(HasDisplayContent));
        OnPropertyChanged(nameof(ItemVisibility));
        OnPropertyChanged(nameof(CanTapback));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(ReplyVisibility));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanUnsend));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanNotifyAnyways));
        OnPropertyChanged(nameof(CanRetrySend));
        OnPropertyChanged(nameof(RetrySendVisibility));
        OnPropertyChanged(nameof(MessageActionVisibility));
        OnPropertyChanged(nameof(SentMessageActionVisibility));
        OnPropertyChanged(nameof(BubbleAlignment));
        OnPropertyChanged(nameof(BubbleBrush));
        OnPropertyChanged(nameof(PrimaryTextBrush));
        OnPropertyChanged(nameof(SecondaryTextBrush));
    }

    private void NotifyCapabilityProperties()
    {
        OnPropertyChanged(nameof(Capabilities));
        OnPropertyChanged(nameof(CanTapback));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(ReplyVisibility));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanUnsend));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanNotifyAnyways));
        OnPropertyChanged(nameof(CanRetrySend));
        OnPropertyChanged(nameof(RetrySendVisibility));
    }

    private void RebuildAttachmentPreviews()
    {
        if (Message.IsReactionEvent)
        {
            _attachmentPreviews = [];
            return;
        }

        _attachmentPreviews = Message.Attachments
            .Where(AttachmentPresentationPolicy.IsDisplayable)
            .Select(attachment => new MessageAttachmentListItem(Message, attachment, _attachmentLocalPathResolver))
            .ToList();
    }

    private void NotifyAttachmentProperties()
    {
        OnPropertyChanged(nameof(AttachmentPreviews));
        OnPropertyChanged(nameof(PrimaryAttachment));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(AttachmentVisibility));
        OnPropertyChanged(nameof(HasPrimaryDisplayContent));
        OnPropertyChanged(nameof(HasDisplayContent));
        OnPropertyChanged(nameof(ItemVisibility));
        NotifyAttachmentActionProperties();
    }

    private void NotifyAttachmentActionProperties()
    {
        OnPropertyChanged(nameof(AttachmentMenuVisibility));
        OnPropertyChanged(nameof(AttachmentDownloadVisibility));
        OnPropertyChanged(nameof(AttachmentRevealVisibility));
        OnPropertyChanged(nameof(CanRetryAttachmentDownload));
        OnPropertyChanged(nameof(RetryAttachmentDownloadVisibility));
        OnPropertyChanged(nameof(CanRetrySend));
        OnPropertyChanged(nameof(RetrySendVisibility));
        OnPropertyChanged(nameof(AttachmentUnavailableText));
        OnPropertyChanged(nameof(AttachmentUnavailableVisibility));
        OnPropertyChanged(nameof(HasAttachmentActions));
        OnPropertyChanged(nameof(HasDownloadableAttachment));
        OnPropertyChanged(nameof(HasDownloadedAttachment));
        OnPropertyChanged(nameof(CanOpenAttachment));
        OnPropertyChanged(nameof(CanDownloadAttachment));
        OnPropertyChanged(nameof(CanRevealAttachment));
    }

    private static bool HasActionableServerGuid(ImsgMessage message)
    {
        return !message.IsReactionEvent &&
            !string.IsNullOrWhiteSpace(message.MessageActionId) &&
            !message.MessageActionId.StartsWith("pending:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeTapbackReaction(ImsgReaction reaction)
    {
        var normalizedKind = TapbackReactionPolicy.NormalizeKind(reaction.Type) ??
            TapbackReactionPolicy.NormalizeKind(reaction.Emoji);
        if (!string.IsNullOrWhiteSpace(normalizedKind))
        {
            return normalizedKind;
        }

        var value = DisplayTextFormatter.SingleLine(reaction.Type ?? reaction.Emoji, string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value switch
        {
            "love" or "heart" or "liked" or "loved" or "❤" or "❤️" or "💙" or "💚" or "💛" or "💜" => "love",
            "like" or "thumbsup" or "thumbs_up" or "thumbs up" or "+1" or "👍" => "like",
            "dislike" or "thumbsdown" or "thumbs_down" or "thumbs down" or "-1" or "👎" => "dislike",
            "laugh" or "laughed" or "haha" or "ha ha" or "😂" or "🤣" => "laugh",
            "emphasize" or "emphasized" or "!!" or "‼" or "‼️" or "❗" or "❕" => "emphasize",
            "question" or "questioned" or "?" or "??" or "❓" or "❔" => "question",
            _ => value
        };
    }
}
