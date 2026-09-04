using System.Collections.ObjectModel;
using WinIMsg.App.Mvvm;

namespace WinIMsg.App.ViewModels;

public sealed class ConversationListViewModel : ObservableObject
{
    private readonly List<ChatListItem> _allChats = [];
    private readonly HashSet<string> _messageSearchMatches = new(StringComparer.OrdinalIgnoreCase);
    private string _searchQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private ChatListItem? _selectedChat;
    private ChatListItem? _draftChat;
    private bool _isMessageSearchInProgress;

    public ObservableCollection<ChatListItem> Chats { get; } = [];

    public IReadOnlyList<ChatListItem> AllChats => _allChats;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public bool IsMessageSearchInProgress
    {
        get => _isMessageSearchInProgress;
        set
        {
            if (SetProperty(ref _isMessageSearchInProgress, value))
            {
                UpdateSearchStatus();
            }
        }
    }

    public ChatListItem? SelectedChat
    {
        get => _selectedChat;
        set => SetProperty(ref _selectedChat, value);
    }

    public void ReplaceChats(IEnumerable<ChatListItem> chats)
    {
        var selectedStableId = SelectedChat?.StableId;
        _allChats.Clear();
        _allChats.AddRange(chats);
        ApplyFilter();
        SelectedChat = string.IsNullOrWhiteSpace(selectedStableId)
            ? null
            : Chats.FirstOrDefault(chat => chat.ContainsStableId(selectedStableId));
        OnPropertyChanged(nameof(AllChats));
    }

    public void ReplaceChat(ChatListItem original, ChatListItem replacement)
    {
        if (original.IsNewMessageDraft)
        {
            return;
        }

        var index = _allChats.FindIndex(chat => SharesStableId(chat, original));
        if (index < 0)
        {
            return;
        }

        var selectedStableId = SelectedChat?.StableId;
        _allChats[index] = replacement;
        ApplyFilter();
        SelectedChat = string.IsNullOrWhiteSpace(selectedStableId)
            ? null
            : Chats.FirstOrDefault(chat => chat.ContainsStableId(selectedStableId));
        OnPropertyChanged(nameof(AllChats));
    }

    public ChatListItem StartNewMessageDraft()
    {
        _draftChat = ChatListItem.NewMessageDraft(string.Empty);
        SelectedChat = _draftChat;
        ApplyFilter();
        return _draftChat;
    }

    public ChatListItem UpdateNewMessageDraftRecipients(string recipients)
    {
        _draftChat = ChatListItem.NewMessageDraft(recipients);
        if (SelectedChat?.IsNewMessageDraft == true)
        {
            SelectedChat = _draftChat;
        }

        ApplyFilter();
        return _draftChat;
    }

    public void ClearNewMessageDraft(ChatListItem? nextSelectedChat = null)
    {
        _draftChat = null;
        if (nextSelectedChat is not null)
        {
            SelectedChat = nextSelectedChat;
        }
        else if (SelectedChat?.IsNewMessageDraft == true)
        {
            SelectedChat = null;
        }

        ApplyFilter();
        if (nextSelectedChat is not null)
        {
            SelectedChat = Chats.FirstOrDefault(chat => chat.ContainsStableId(nextSelectedChat.StableId)) ?? nextSelectedChat;
        }
    }

    public void SetMessageSearchMatches(IEnumerable<string> stableIds)
    {
        _messageSearchMatches.Clear();
        foreach (var stableId in stableIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            _messageSearchMatches.Add(stableId);
        }

        ApplyFilter();
    }

    public bool ContainsMessageSearchMatch(string stableId) => _messageSearchMatches.Contains(stableId);

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        var source = _draftChat is null
            ? _allChats
            : new[] { _draftChat }.Concat(_allChats);
        var matches = string.IsNullOrWhiteSpace(query)
            ? source
            : source.Where(chat => MatchesChatSearch(chat, query)).ToList();

        Chats.ReplaceWith(SortForDisplay(matches));
        UpdateSearchStatus();
    }

    private static IReadOnlyList<ChatListItem> SortForDisplay(IEnumerable<ChatListItem> chats)
    {
        return chats
            .Select((chat, index) => new { Chat = chat, Index = index })
            .OrderByDescending(item => item.Chat.IsNewMessageDraft)
            .ThenByDescending(item => item.Chat.EffectiveLatestMessageDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Chat)
            .ToList();
    }

    private bool MatchesChatSearch(ChatListItem chat, string query)
    {
        return ContainsSearchText(chat.DisplayName, query) ||
            ContainsSearchText(chat.Detail, query) ||
            chat.Chats.Any(source =>
                ContainsSearchText(source.Identifier, query) ||
                ContainsSearchText(source.Service, query) ||
                source.Participants.Any(participant => ContainsSearchText(participant, query))) ||
            chat.StableIds.Any(stableId => _messageSearchMatches.Contains(stableId));
    }

    private void UpdateSearchStatus()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchStatus = string.Empty;
            return;
        }

        var contentMatchCount = _allChats.Count(chat => chat.StableIds.Any(stableId => _messageSearchMatches.Contains(stableId)));
        var contentText = IsMessageSearchInProgress
            ? " - searching messages"
            : contentMatchCount == 0
                ? string.Empty
                : $" - {contentMatchCount} content";
        SearchStatus = $"{Chats.Count} of {_allChats.Count} chats match{contentText}";
    }

    private static bool ContainsSearchText(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source) &&
            source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SharesStableId(ChatListItem left, ChatListItem right)
    {
        return left.StableIds.Any(right.ContainsStableId);
    }
}
