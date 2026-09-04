using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIMsg.App.ViewModels;

namespace WinIMsg.App.Controls;

public sealed partial class ChatListPane : UserControl
{
    private object? _singleSelectionBeforeMassSelect;

    public ChatListPane()
    {
        InitializeComponent();
        ChatListView.KeyDown += OnChatListKeyDown;
    }

    public object? ItemsSource
    {
        get => ChatListView.ItemsSource;
        set => ChatListView.ItemsSource = value;
    }

    public object? SelectedItem
    {
        get => IsMassSelectMode ? _singleSelectionBeforeMassSelect : ChatListView.SelectedItem;
        set
        {
            if (IsMassSelectMode)
            {
                _singleSelectionBeforeMassSelect = value;
                return;
            }

            ChatListView.SelectedItem = value;
        }
    }

    public bool IsMassSelectMode => SelectChatsButton.IsChecked == true;

    public string SearchText
    {
        get => ChatSearchBox.Text;
        set => ChatSearchBox.Text = value;
    }

    public bool CanCreateNewChat
    {
        get => NewChatButton.IsEnabled;
        set => NewChatButton.IsEnabled = value;
    }

    public event TextChangedEventHandler? SearchTextChanged;

    public event ItemClickEventHandler? ChatItemClick;

    public event RoutedEventHandler? NewChatRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextOpenRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextRefreshRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextMarkReadRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextCopyNameRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextCopyAddressRequested;

    public event EventHandler<ChatListContextActionEventArgs>? ChatContextCopyGuidRequested;

    public event EventHandler<ChatListMassActionEventArgs>? MassMarkReadRequested;

    public void SetSearchStatus(string text, bool isVisible)
    {
        ChatSearchStatusTextBlock.Text = text;
        ChatSearchStatusTextBlock.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => SearchTextChanged?.Invoke(sender, e);

    private void OnChatItemClick(object sender, ItemClickEventArgs e) => ChatItemClick?.Invoke(sender, e);

    private void OnNewChatClicked(object sender, RoutedEventArgs e) => NewChatRequested?.Invoke(sender, e);

    private void OnOpenChatMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextOpenRequested);

    private void OnRefreshChatMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextRefreshRequested);

    private void OnMarkChatReadMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextMarkReadRequested);

    private void OnCopyChatNameMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextCopyNameRequested);

    private void OnCopyChatAddressMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextCopyAddressRequested);

    private void OnCopyChatGuidMenuClicked(object sender, RoutedEventArgs e) =>
        RaiseChatContextAction(sender, ChatContextCopyGuidRequested);

    private void RaiseChatContextAction(
        object sender,
        EventHandler<ChatListContextActionEventArgs>? handler)
    {
        if (sender is FrameworkElement { Tag: ChatListItem chat })
        {
            handler?.Invoke(this, new ChatListContextActionEventArgs(chat));
        }
    }

    public void ExitMassSelectMode() => SelectChatsButton.IsChecked = false;

    private void OnSelectChatsChecked(object sender, RoutedEventArgs e)
    {
        _singleSelectionBeforeMassSelect = ChatListView.SelectedItem;
        ChatListView.SelectedItem = null;
        ChatListView.IsItemClickEnabled = false;
        ChatListView.SelectionMode = ListViewSelectionMode.Multiple;
        MassActionBar.Visibility = Visibility.Visible;
        UpdateMassActionState();
    }

    private void OnSelectChatsUnchecked(object sender, RoutedEventArgs e)
    {
        ChatListView.SelectionMode = ListViewSelectionMode.Single;
        ChatListView.IsItemClickEnabled = true;
        MassActionBar.Visibility = Visibility.Collapsed;
        ChatListView.SelectedItem = _singleSelectionBeforeMassSelect;
        _singleSelectionBeforeMassSelect = null;
    }

    private void OnChatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsMassSelectMode)
        {
            UpdateMassActionState();
        }
    }

    private void OnMassSelectAllClicked(object sender, RoutedEventArgs e)
    {
        if (!IsMassSelectMode)
        {
            return;
        }

        ChatListView.SelectAll();
        UpdateMassActionState();
    }

    private void OnMassMarkReadClicked(object sender, RoutedEventArgs e)
    {
        var chats = SelectedMassActionChats();
        ExitMassSelectMode();
        if (chats.Count > 0)
        {
            MassMarkReadRequested?.Invoke(this, new ChatListMassActionEventArgs(chats));
        }
    }

    private void OnChatListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (IsMassSelectMode && args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            ExitMassSelectMode();
        }
    }

    private IReadOnlyList<ChatListItem> SelectedMassActionChats() => ChatListView.SelectedItems
        .OfType<ChatListItem>()
        .Where(static chat => !chat.IsNewMessageDraft)
        .ToList();

    private void UpdateMassActionState()
    {
        var count = SelectedMassActionChats().Count;
        MassActionCountTextBlock.Text = count == 1 ? "1 selected" : $"{count} selected";
        MassMarkReadButton.IsEnabled = count > 0;
    }
}

public sealed class ChatListMassActionEventArgs(IReadOnlyList<ChatListItem> chats) : EventArgs
{
    public IReadOnlyList<ChatListItem> Chats { get; } = chats;
}

public sealed class ChatListContextActionEventArgs(ChatListItem chat) : EventArgs
{
    public ChatListItem Chat { get; } = chat;
}
