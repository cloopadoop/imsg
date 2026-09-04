using System.Collections.ObjectModel;
using WinIMsg.App.Contracts;
using WinIMsg.App.Mvvm;
using WinIMsg.App.Services;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed class ConversationViewModel : ObservableObject
{
    private readonly MessageWindowProjector<MessageListItem> _messageWindow;
    private ImsgCapabilities _capabilities = new();
    private Func<ImsgMessage, ImsgAttachment, string>? _attachmentLocalPathResolver;
    private ConversationTarget? _target;
    private bool _isLoading;
    private bool _isPinnedAwayFromLatest;
    private bool _isNewMessageDraft;
    private string _title = "No chat selected";
    private string _inlineStatus = "Select a conversation.";
    private string _composeText = string.Empty;
    private string _draftRecipientText = string.Empty;

    public ConversationViewModel(int initialVisibleMessages = 80, int olderPageSize = 80)
    {
        _messageWindow = new MessageWindowProjector<MessageListItem>(initialVisibleMessages, olderPageSize);
    }

    public ObservableCollection<MessageListItem> Messages { get; } = [];

    public ObservableCollection<string> DraftRecipientChips { get; } = [];

    public ConversationTarget? Target
    {
        get => _target;
        private set => SetProperty(ref _target, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(LoadingVisibility));
            }
        }
    }

    public bool IsPinnedAwayFromLatest
    {
        get => _isPinnedAwayFromLatest;
        set => SetProperty(ref _isPinnedAwayFromLatest, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public bool IsNewMessageDraft
    {
        get => _isNewMessageDraft;
        private set
        {
            if (SetProperty(ref _isNewMessageDraft, value))
            {
                OnPropertyChanged(nameof(TitleVisibility));
                OnPropertyChanged(nameof(DraftHeaderVisibility));
            }
        }
    }

    public string DraftRecipientText
    {
        get => _draftRecipientText;
        set
        {
            if (SetProperty(ref _draftRecipientText, value))
            {
                RefreshDraftRecipientChips();
            }
        }
    }

    public string InlineStatus
    {
        get => _inlineStatus;
        set
        {
            if (SetProperty(ref _inlineStatus, value))
            {
                OnPropertyChanged(nameof(InlineStatusVisibility));
            }
        }
    }

    public string ComposeText
    {
        get => _composeText;
        set => SetProperty(ref _composeText, value);
    }

    public bool HasSelectedConversation => Target is not null;

    public bool CanLoadOlder => _messageWindow.CanLoadOlder;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TitleVisibility => IsNewMessageDraft ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DraftHeaderVisibility => IsNewMessageDraft ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DraftRecipientChipsVisibility => DraftRecipientChips.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility InlineStatusVisibility => string.IsNullOrWhiteSpace(InlineStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public IReadOnlyList<MessageListItem> AllMessages => _messageWindow.AllItems;

    public void SelectConversation(ConversationTarget target)
    {
        var targetChanged = Target is null ||
            !string.Equals(Target.StableId, target.StableId, StringComparison.OrdinalIgnoreCase);
        IsNewMessageDraft = false;
        DraftRecipientText = string.Empty;
        Target = target;
        Title = target.DisplayName;
        InlineStatus = string.Empty;
        IsPinnedAwayFromLatest = false;
        if (targetChanged)
        {
            ClearMessages();
        }

        OnPropertyChanged(nameof(HasSelectedConversation));
    }

    public void StartNewMessageDraft()
    {
        Target = null;
        IsNewMessageDraft = true;
        Title = "New Message";
        InlineStatus = string.Empty;
        IsLoading = false;
        IsPinnedAwayFromLatest = false;
        ClearMessages();
        OnPropertyChanged(nameof(HasSelectedConversation));
    }

    private void RefreshDraftRecipientChips()
    {
        var recipients = ChatListItem.ParseRecipientText(DraftRecipientText);
        DraftRecipientChips.ReplaceWith(recipients);
        OnPropertyChanged(nameof(DraftRecipientChipsVisibility));
    }

    public void ClearSelection()
    {
        Target = null;
        IsNewMessageDraft = false;
        DraftRecipientText = string.Empty;
        Title = "No chat selected";
        InlineStatus = "Select a conversation.";
        ClearMessages();
        OnPropertyChanged(nameof(HasSelectedConversation));
    }

    public void ClearMessages()
    {
        _messageWindow.ReplaceAll([], resetVisibleWindow: true);
        Messages.Clear();
        OnPropertyChanged(nameof(CanLoadOlder));
        OnPropertyChanged(nameof(AllMessages));
    }

    public void ReplaceMessages(
        IEnumerable<ImsgMessage> messages,
        string? fallbackService = null,
        bool resetVisibleWindow = false,
        Func<ImsgMessage, bool>? hasFailedAttachmentDownload = null)
    {
        var projected = ProjectMessages(messages, fallbackService, hasFailedAttachmentDownload).ToList();
        ReplaceWindow(
            RetainLiveTailNewerThan(projected),
            resetVisibleWindow);
    }

    // A history fetch is a snapshot that may have started before the newest
    // live watch messages reached chat.db; replacing the window with it would
    // silently drop messages that already arrived and rendered (observed live:
    // a send's post-refresh erased a message received seconds later). Keep any
    // existing items strictly newer than the fetched window's newest row.
    private IReadOnlyList<MessageListItem> RetainLiveTailNewerThan(List<MessageListItem> projected)
    {
        if (projected.Count == 0)
        {
            return projected;
        }

        var newestFetched = projected
            .Select(static item => item.Message.SortDate)
            .Where(static date => date is not null)
            .DefaultIfEmpty()
            .Max();
        if (newestFetched is null)
        {
            return projected;
        }

        var fetchedIdentities = projected
            .Select(static item => MessageIdentity(item.Message))
            .Where(static identity => identity is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = _messageWindow.AllItems
            .Where(item =>
                item.Message.SortDate is { } date &&
                date > newestFetched.Value &&
                (MessageIdentity(item.Message) is not { } identity || !fetchedIdentities.Contains(identity)))
            .ToList();
        if (retained.Count == 0)
        {
            return projected;
        }

        return SortItems(projected.Concat(retained).ToList());
    }

    public void ResetToLatest()
    {
        Messages.ReplaceWith(_messageWindow.ResetToLatest());
        IsPinnedAwayFromLatest = false;
        OnPropertyChanged(nameof(CanLoadOlder));
    }

    public void LoadOlderMessages()
    {
        Messages.ReplaceWith(_messageWindow.LoadOlder());
        OnPropertyChanged(nameof(CanLoadOlder));
    }

    public void SetCapabilities(ImsgCapabilities capabilities)
    {
        _capabilities = capabilities;
        foreach (var item in _messageWindow.AllItems)
        {
            item.SetCapabilities(capabilities);
        }
    }

    public void SetAttachmentLocalPathResolver(Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver)
    {
        _attachmentLocalPathResolver = attachmentLocalPathResolver;
        foreach (var item in _messageWindow.AllItems)
        {
            item.SetAttachmentLocalPathResolver(attachmentLocalPathResolver);
        }
    }

    public MessageListItem AddPendingMessage(ImsgMessage pendingMessage)
    {
        var pendingItem = MessageListItem.Pending(pendingMessage, _capabilities, _attachmentLocalPathResolver);
        ReplaceWindow(_messageWindow.AllItems.Concat([pendingItem]), resetVisibleWindow: true);
        return pendingItem;
    }

    public MessageListItem AddOrGetPendingMessage(ImsgMessage pendingMessage)
    {
        var existing = _messageWindow.AllItems.FirstOrDefault(item =>
            string.Equals(item.Message.Guid, pendingMessage.Guid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return AddPendingMessage(pendingMessage);
    }

    public void AppendOrReplaceMessage(
        ImsgMessage message,
        string? fallbackService = null,
        bool resetVisibleWindow = false,
        int growVisibleWindowBy = 0,
        Func<ImsgMessage, bool>? hasFailedAttachmentDownload = null)
    {
        var projected = ProjectMessages([message], fallbackService, hasFailedAttachmentDownload).FirstOrDefault();
        if (projected is null)
        {
            return;
        }

        var all = _messageWindow.AllItems.ToList();
        var existingIndex = FindMatchingMessageIndex(all, projected.Message);
        if (existingIndex >= 0)
        {
            all[existingIndex] = projected;
        }
        else
        {
            all.Add(projected);
        }

        RemoveFailedSendsSupersededBy(all, projected);
        ReplaceWindow(SortItems(all), resetVisibleWindow, growVisibleWindowBy);
    }

    public void AppendOrReplaceMessages(
        IEnumerable<ImsgMessage> messages,
        string? fallbackService = null,
        bool resetVisibleWindow = false,
        int growVisibleWindowBy = 0,
        Func<ImsgMessage, bool>? hasFailedAttachmentDownload = null)
    {
        var projected = ProjectMessages(messages, fallbackService, hasFailedAttachmentDownload).ToList();
        if (projected.Count == 0)
        {
            return;
        }

        var all = _messageWindow.AllItems.ToList();
        foreach (var replacement in projected)
        {
            var existingIndex = FindMatchingMessageIndex(all, replacement.Message);
            if (existingIndex >= 0)
            {
                all[existingIndex] = replacement;
            }
            else
            {
                all.Add(replacement);
            }

            RemoveFailedSendsSupersededBy(all, replacement);
        }

        ReplaceWindow(SortItems(all), resetVisibleWindow, growVisibleWindowBy);
    }

    public int PrependOlderMessages(
        IEnumerable<ImsgMessage> messages,
        string? fallbackService = null,
        Func<ImsgMessage, bool>? hasFailedAttachmentDownload = null)
    {
        var beforeCount = _messageWindow.AllItems.Count;
        var projected = ProjectMessages(messages, fallbackService, hasFailedAttachmentDownload).ToList();
        if (projected.Count == 0)
        {
            return 0;
        }

        var all = projected.Concat(_messageWindow.AllItems).ToList();
        var sorted = SortItems(all);
        var addedCount = CountNewMessages(sorted, _messageWindow.AllItems);
        ReplaceWindow(sorted, resetVisibleWindow: false, growVisibleWindowBy: addedCount);
        return Math.Max(0, _messageWindow.AllItems.Count - beforeCount);
    }

    public void RefreshAttachmentState(Func<ImsgMessage, bool> hasFailedAttachmentDownload)
    {
        foreach (var item in _messageWindow.AllItems)
        {
            item.HasFailedAttachmentDownload = hasFailedAttachmentDownload(item.Message);
            item.RefreshAttachmentLocalState();
        }
    }

    public void RefreshAttachmentState(string messageGuid, Func<ImsgMessage, bool> hasFailedAttachmentDownload)
    {
        foreach (var item in _messageWindow.AllItems)
        {
            if (!string.Equals(item.Message.Guid, messageGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.HasFailedAttachmentDownload = hasFailedAttachmentDownload(item.Message);
            item.RefreshAttachmentLocalState();
            return;
        }
    }

    public void MarkPendingMessageSent(MessageListItem pendingItem, string? sentGuid, string syncingStatus)
    {
        var updated = false;
        var all = _messageWindow.AllItems.Select(item =>
        {
            if (!ReferenceEquals(item, pendingItem) && item != pendingItem)
            {
                return item;
            }

            updated = true;
            var message = string.IsNullOrWhiteSpace(sentGuid)
                ? pendingItem.Message
                : pendingItem.Message with { Guid = sentGuid };
            return CreateMessageListItem(message, item.HasFailedAttachmentDownload, isPending: false, isFailed: false,
                deliveryStatus: string.IsNullOrWhiteSpace(sentGuid) ? syncingStatus : string.Empty);
        }).ToList();

        if (updated)
        {
            ReplaceWindow(all, resetVisibleWindow: false);
        }
    }

    public void AddPostSendPlaceholder(MessageListItem pendingItem, string? messageGuid, string deliveryStatus, bool isFailed)
    {
        var message = string.IsNullOrWhiteSpace(messageGuid)
            ? pendingItem.Message
            : pendingItem.Message with { Guid = messageGuid };

        var placeholder = CreateMessageListItem(
            message,
            pendingItem.HasFailedAttachmentDownload,
            isPending: false,
            isFailed: isFailed,
            deliveryStatus: deliveryStatus);
        var all = _messageWindow.AllItems.ToList();
        var existingIndex = FindMatchingMessageIndex(all, message);
        if (existingIndex >= 0)
        {
            all[existingIndex] = placeholder;
        }
        else
        {
            all.Add(placeholder);
        }

        ReplaceWindow(all, resetVisibleWindow: true);
    }

    public bool HasObservedSentMessage(string text, string? sentGuid, DateTimeOffset sendStartedAt)
    {
        return _messageWindow.AllItems.Any(item => IsObservedSentMessage(item.Message, text, sentGuid, sendStartedAt));
    }

    public void UpdateMessageDeliveryStatus(string messageGuid, string deliveryStatus)
    {
        var updated = false;
        var all = _messageWindow.AllItems.Select(item =>
        {
            if (!string.Equals(item.Message.Guid, messageGuid, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            updated = true;
            return CreateMessageListItem(item.Message, item.HasFailedAttachmentDownload, item.IsPending, item.IsFailed, deliveryStatus);
        }).ToList();

        if (updated)
        {
            ReplaceWindow(all, resetVisibleWindow: false);
        }
    }

    public void MarkPendingMessageFailed(MessageListItem pendingItem, string error)
    {
        var failedItem = CreateMessageListItem(
            pendingItem.Message,
            pendingItem.HasFailedAttachmentDownload,
            isPending: false,
            isFailed: true,
            deliveryStatus: $"Not sent: {error}",
            capabilities: pendingItem.Capabilities);

        var updated = false;
        var all = _messageWindow.AllItems.Select(item =>
        {
            if (!ReferenceEquals(item, pendingItem) && item != pendingItem)
            {
                return item;
            }

            updated = true;
            return failedItem;
        }).ToList();

        if (!updated)
        {
            all.Add(failedItem);
        }

        ReplaceWindow(all, resetVisibleWindow: false);
    }

    public bool RemoveTransientMessage(MessageListItem message)
    {
        var all = _messageWindow.AllItems.ToList();
        var removed = all.RemoveAll(item =>
            ReferenceEquals(item, message) ||
            (!string.IsNullOrWhiteSpace(message.Message.Guid) &&
                string.Equals(item.Message.Guid, message.Message.Guid, StringComparison.OrdinalIgnoreCase))) > 0;
        if (removed)
        {
            ReplaceWindow(all, resetVisibleWindow: false);
        }

        return removed;
    }

    // A send that times out client-side can still deliver on the Mac later.
    // When the delivered message is observed (watch echo or history refresh),
    // drop any failed transient bubble carrying the same outbound text so the
    // send is not shown twice.
    private static void RemoveFailedSendsSupersededBy(List<MessageListItem> all, MessageListItem observed)
    {
        var message = observed.Message;
        if (!message.IsFromMe ||
            observed.IsPending ||
            observed.IsFailed ||
            string.IsNullOrWhiteSpace(message.Text) ||
            message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        all.RemoveAll(item =>
            !ReferenceEquals(item, observed) &&
            item.IsFailed &&
            item.Message.IsFromMe &&
            item.Message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(item.Message.Text?.Trim(), message.Text!.Trim(), StringComparison.Ordinal));
    }

    private static bool IsObservedSentMessage(ImsgMessage message, string text, string? sentGuid, DateTimeOffset sendStartedAt)
    {
        if (message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sentGuid) &&
            string.Equals(message.Guid, sentGuid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sentGuid) || !message.IsFromMe)
        {
            return false;
        }

        if (!string.Equals(message.Text?.Trim(), text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var messageDate = message.SortDate;
        return messageDate is null || messageDate.Value >= sendStartedAt.AddMinutes(-5);
    }

    private void ReplaceWindow(IEnumerable<MessageListItem> items, bool resetVisibleWindow, int growVisibleWindowBy = 0)
    {
        _messageWindow.ReplaceAll(
            CoalesceExistingItems(items).Where(static item => item.HasDisplayContent),
            resetVisibleWindow);
        if (growVisibleWindowBy > 0)
        {
            _messageWindow.GrowVisibleWindow(growVisibleWindowBy);
        }

        Messages.ReplaceWith(_messageWindow.VisibleItems);
        OnPropertyChanged(nameof(CanLoadOlder));
        OnPropertyChanged(nameof(AllMessages));
    }

    private IReadOnlyList<MessageListItem> CoalesceExistingItems(IEnumerable<MessageListItem> items)
    {
        var existingByIdentity = new Dictionary<string, Queue<MessageListItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _messageWindow.AllItems)
        {
            var identity = MessageIdentity(existing.Message);
            if (identity is null)
            {
                continue;
            }

            if (!existingByIdentity.TryGetValue(identity, out var matches))
            {
                matches = new Queue<MessageListItem>();
                existingByIdentity[identity] = matches;
            }

            matches.Enqueue(existing);
        }

        var merged = new List<MessageListItem>();
        foreach (var replacement in items)
        {
            var identity = MessageIdentity(replacement.Message);
            if (identity is not null &&
                existingByIdentity.TryGetValue(identity, out var matches) &&
                matches.Count > 0)
            {
                var existing = matches.Dequeue();
                existing.RefreshFrom(replacement);
                merged.Add(existing);
                continue;
            }

            merged.Add(replacement);
        }

        return merged;
    }

    private MessageListItem CreateMessageListItem(
        ImsgMessage message,
        bool hasFailedAttachmentDownload,
        bool isPending,
        bool isFailed,
        string deliveryStatus,
        ImsgCapabilities? capabilities = null)
    {
        return MessageListItem.From(
                message,
                capabilities ?? _capabilities,
                hasFailedAttachmentDownload,
                _attachmentLocalPathResolver)
            .SetTransientState(isPending, isFailed, deliveryStatus);
    }

    private IEnumerable<MessageListItem> ProjectMessages(
        IEnumerable<ImsgMessage> messages,
        string? fallbackService,
        Func<ImsgMessage, bool>? hasFailedAttachmentDownload)
    {
        return SortMessages(messages.Select(message => ApplyFallbackService(message, fallbackService)))
            .Select(message => MessageListItem.From(
                message,
                _capabilities,
                hasFailedAttachmentDownload?.Invoke(message) == true,
                _attachmentLocalPathResolver))
            .Where(static item => item.HasDisplayContent);
    }

    private static IReadOnlyList<ImsgMessage> SortMessages(IEnumerable<ImsgMessage> messages)
    {
        return messages
            .Select((message, index) => new
            {
                Message = message,
                SortDate = message.SortDate,
                Index = index
            })
            .OrderBy(item => item.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Message.Id ?? long.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Message)
            .ToList();
    }

    private static IReadOnlyList<MessageListItem> SortItems(IEnumerable<MessageListItem> items)
    {
        return items
            .Select((item, index) => new
            {
                Item = item,
                SortDate = item.Message.SortDate,
                Index = index
            })
            .OrderBy(item => item.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Item.Message.Id ?? long.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Item)
            .ToList();
    }

    private static int FindMatchingMessageIndex(IReadOnlyList<MessageListItem> items, ImsgMessage message)
    {
        var identity = MessageIdentity(message);
        if (identity is null)
        {
            return -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(MessageIdentity(items[index].Message), identity, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int CountNewMessages(
        IEnumerable<MessageListItem> candidates,
        IEnumerable<MessageListItem> existing)
    {
        var existingIdentities = existing
            .Select(static item => MessageIdentity(item.Message))
            .Where(static identity => identity is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var candidate in candidates)
        {
            var identity = MessageIdentity(candidate.Message);
            if (identity is null || existingIdentities.Add(identity))
            {
                added++;
            }
        }

        return added;
    }

    private static string? MessageIdentity(ImsgMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Guid))
        {
            return $"guid:{message.Guid}";
        }

        return message.Id is null ? null : $"id:{message.Id.Value}";
    }

    private static ImsgMessage ApplyFallbackService(ImsgMessage message, string? fallbackService)
    {
        return string.IsNullOrWhiteSpace(message.Service) && !string.IsNullOrWhiteSpace(fallbackService)
            ? message with { Service = fallbackService }
            : message;
    }
}
