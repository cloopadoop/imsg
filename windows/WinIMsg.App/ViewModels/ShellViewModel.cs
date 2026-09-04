using WinIMsg.App.Contracts;
using WinIMsg.App.Mvvm;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private string _statusText = "Disconnected";
    private bool _isSettingsVisible;
    private WinIMsgSettings _settings = new();
    private ImsgCapabilities _capabilities = new();

    public ShellViewModel()
    {
        Conversations = new ConversationListViewModel();
        Conversation = new ConversationViewModel();
        SettingsView = new SettingsViewModel();
        SyncStatus = new SyncStatusViewModel();
        ConnectCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
        DisconnectCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
    }

    public ConversationListViewModel Conversations { get; }

    public ConversationViewModel Conversation { get; }

    public SettingsViewModel SettingsView { get; }

    public SyncStatusViewModel SyncStatus { get; }

    public AsyncRelayCommand ConnectCommand { get; private set; }

    public AsyncRelayCommand DisconnectCommand { get; private set; }

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
            }
        }
    }

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (SetProperty(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(SettingsVisibility));
            }
        }
    }

    public Visibility SettingsVisibility => IsSettingsVisible ? Visibility.Visible : Visibility.Collapsed;

    public WinIMsgSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    public ImsgCapabilities Capabilities
    {
        get => _capabilities;
        private set => SetProperty(ref _capabilities, value);
    }

    public void LoadSettings(WinIMsgSettings settings)
    {
        Settings = settings.Normalize();
        SettingsView.Load(Settings);
    }

    public void SetCapabilities(ImsgCapabilities capabilities)
    {
        Capabilities = capabilities;
        Conversation.SetCapabilities(capabilities);
    }

    public void SetStatus(ConnectionState state, string text)
    {
        ConnectionState = state;
        StatusText = text;
    }

    public void ConfigureConnectionCommands(
        Func<CancellationToken, Task> connect,
        Func<CancellationToken, Task> disconnect)
    {
        ConnectCommand = new AsyncRelayCommand(connect);
        DisconnectCommand = new AsyncRelayCommand(disconnect);
        OnPropertyChanged(nameof(ConnectCommand));
        OnPropertyChanged(nameof(DisconnectCommand));
    }
}
