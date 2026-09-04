using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Input;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.System;
using PhoneNumbers;
using WinIMsg.App.Controls;
using WinIMsg.App.Contracts;
using WinIMsg.App.Mvvm;
using WinIMsg.App.Services;
using WinIMsg.App.ViewModels;
using WinIMsg.Core;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.Core.Settings;
using WinIMsg.Core.Ssh;

namespace WinIMsg.App.Views;

public sealed partial class MainPage : Page
{
    private readonly AppDataPaths _paths = new();
    private readonly JsonSettingsStore _settingsStore;
    private readonly SqliteMessageCache _cache;
    private readonly IMessageCache _messageCache;
    private readonly IImsgClient _bridge = new BridgeImsgClientAdapter();
    private readonly DedicatedBridgeChannel _sendChannel;
    private readonly DedicatedBridgeChannel _actionChannel;
    private readonly DedicatedBridgeChannel _nicetyChannel;
    private readonly ImsgCliChatReader _chatReader = new();
    private readonly SshFileTransferService _fileTransfer = new();
    private readonly MacHostActionService _macHostActions = new();
    private readonly SetupChecklistService _setupChecklistService;
    private readonly SupportBundleService _supportBundleService;
    private readonly MessageSendService _messageSendService;
    private readonly MessageSendWorkflowService _messageSendWorkflowService;
    private readonly MessageActionWorkflowService _messageActionWorkflowService;
    private readonly AttachmentSendWorkflowService _attachmentSendWorkflowService;
    private readonly AttachmentService _attachmentService;
    private readonly BridgeConnectionService _connectionService;
    private readonly ConversationDataService _conversationDataService;
    private readonly ConversationSyncService _conversationSyncService;
    private readonly ConversationHistoryFetchService _historyFetchService;
    private readonly ReactionHistoryRefreshCoordinator _reactionHistoryRefreshCoordinator;
    private readonly WatchNotificationService _watchNotifications = new();
    private readonly BridgeNotificationWorkflowService _bridgeNotificationWorkflowService;
    private readonly INotificationPublisher _notifications = new AppNotificationService();
    private readonly IFilePickerService _filePicker;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowShellService _windowShell;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _resumeReconnectGate = new(1, 1);
    private readonly SemaphoreSlim _sendQueueGate = new(1, 1);
    private readonly HashSet<string> _cacheSyncExternallySatisfiedChatIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedSendKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChatListStateStore _chatListState = new();
    private readonly Dictionary<string, List<ImsgMessage>> _queuedPendingMessagesByStableId = new(StringComparer.OrdinalIgnoreCase);
    // Watch notifications, post-send refreshes, and background sync can all
    // request a chat projection at nearly the same time. WinUI's bound list
    // cannot safely be replaced by overlapping async projections.
    private readonly SemaphoreSlim _chatProjectionGate = new(1, 1);
    private readonly ObservableCollection<string> _statusTaskItems = [];
    private readonly ComposeDraftStore _composeDraftStore = new();
    private readonly List<RichTextFormattingRange> _pendingTextFormatting = [];
    private readonly object _taskbarUnreadIconLock = new();
    private readonly List<string> _recipientSuggestions = [];
    private readonly AppLogService _log;
    private readonly ShellViewModel _shell = new();
    private CoalescingBackgroundAction<TrayUnreadBadgeUpdate>? _trayUnreadBadgeQueue;
    private CoalescingBackgroundAction<UnreadBadgeUpdate>? _taskbarUnreadBadgeQueue;
    private CoalescingBackgroundAction<bool>? _transientReprojectQueue;
    private readonly VoiceRecordingService _voiceRecording = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _voiceRecordingTimer;
    private readonly WebCompanionService _webCompanion;
    private ShellNotificationDispatcher? _shellNotificationDispatcher;

    private WinIMsgSettings _appSettings = new();
    private ImsgBridgeSettings _settings = new();
    private ImsgCapabilities _capabilities = new();
    private ChatListItem? _selectedChat;
    private CancellationTokenSource? _historyLoadCancellation;
    private CancellationTokenSource? _cacheSyncCancellation;
    private CancellationTokenSource? _chatSearchCancellation;
    private CancellationTokenSource? _bridgeReconnectCancellation;
    private CancellationTokenSource? _typingStopCancellation;
    private ScrollViewer? _messageScrollViewer;
    private bool _refreshingProfiles;
    private bool _programmaticMessageScroll;
    private bool _updatingSettingsNav;
    private bool _loadingSettingsForm;
    private bool _settingsFormInitialized;
    private bool _foregroundHistoryLoading;
    private bool _isResizingConversationSplit;
    private bool _olderCachedHistoryLoading;
    private bool _olderCachedHistoryExhausted = true;
    private bool _scrollToLatestAfterLayout;
    private bool _faceTimeLinkAvailable;
    private double _splitDragStartX;
    private double _splitDragStartWidth;
    private string _loadedSettingsProfileId = "default";
    private string? _mediaOverlayLocalPath;
    private string? _contactsSettingsStatusOverride;
    private string? _setupChecklistStatusOverride;
    private string? _pendingSendEffect;
    private string? _pendingSendEffectLabel;
    private string? _pendingReplyToGuid;
    private string? _pendingReplySummary;
    private string? _lastCommittedDraftRecipientSuggestion;
    private ImsgChat? _typingIndicatorChat;
    private bool _userDisconnectedBridge = true;
    private bool _restoringComposeDraft;
    private bool _typingIndicatorActive;
    private System.Drawing.Icon? _taskbarUnreadIcon;
    private int _lastUnreadBadgeCount = -1;
    private int _transientSyncBarDepth;
    private string? _trayBadgeIconDirectory;
    private static readonly Uri DefaultTrayIconUri = new("ms-appx:///Assets/MessagesTray.ico");
    private static readonly InputSystemCursor HorizontalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly string[] EmojiFallbackItems = ["😀", "😂", "❤️", "👍", "🙏", "🔥", "🎉", "🤦"];
    private static readonly TimeSpan BackgroundSyncPerChatTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BackgroundSyncFallbackTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BackgroundSyncLatestMessageTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SelectedHistoryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SelectedHistoryFallbackTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReactionHistoryRefreshDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TypingIndicatorInactivityTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TypingIndicatorRequestTimeout = TimeSpan.FromSeconds(4);
    private const int InitialVisibleMessageWindow = 80;
    private const int OlderVisibleMessagePageSize = 80;
    private const int CacheSyncHistoryLimit = 50;
    private const int BackgroundSyncMaxActiveChats = 250;
    private const int CacheSyncFallbackHistoryLimit = 10;
    private const int CacheSyncLatestMessageFallbackHistoryLimit = 1;
    private const int ChatListFetchLimit = 10000;
    private const int SelectedHistoryLimit = 500;
    private const int SelectedHistoryFallbackLimit = 50;
    private const double MinConversationListWidth = 240;
    private const double MaxConversationListWidth = 560;
    private const double MinConversationPaneWidth = 420;
    private const string AppDisplayName = AppIdentityService.DisplayName;
    private const byte VirtualKeyLeftWindows = 0x5B;
    private const byte VirtualKeyPeriod = 0xBE;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const uint KeyEventKeyUp = 0x0002;

    public ICommand TrayOpenCommand { get; }

    public ICommand TrayExitCommand { get; }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public MainPage()
    {
        TrayOpenCommand = new RelayCommand(RestoreFromTray);
        TrayExitCommand = new RelayCommand(ExitFromTray);
        InitializeComponent();
        InitializePhoneRegionPicker();
        InitializeRemoteAccessModePicker();
        var app = (App)Application.Current;
        _windowShell = app.WindowShell;
        _filePicker = new WinUiFilePickerService(() => app.MainWindowHandle);
        _dialogs = new WinUiDialogService(() => XamlRoot);
        DataContext = _shell;
        StatusTaskListView.ItemsSource = _statusTaskItems;
        app.RegisterMainPage(this);
        _paths.EnsureCreated();
        _log = new AppLogService(_paths.Logs);
        _log.Info("WinIMsg UI initialized.");
        _shellNotificationDispatcher = new ShellNotificationDispatcher(
            TimeSpan.FromSeconds(4),
            _log.Warning,
            _log.Info);
        _taskbarUnreadBadgeQueue = new CoalescingBackgroundAction<UnreadBadgeUpdate>(
            "Taskbar unread badge update",
            TimeSpan.FromMilliseconds(75),
            update =>
            {
                ApplyTaskbarUnreadBadge(update.WindowHandle, update.UnreadCount);
                return Task.CompletedTask;
            },
            _log.Warning);
        _trayUnreadBadgeQueue = new CoalescingBackgroundAction<TrayUnreadBadgeUpdate>(
            "Tray unread badge update",
            TimeSpan.FromMilliseconds(75),
            update =>
            {
                var iconUri = update.UnreadCount <= 0
                    ? DefaultTrayIconUri
                    : GetTrayUnreadBadgeIconUri(update.UnreadCount);
                if (!DispatcherQueue.TryEnqueue(() => ApplyTrayUnreadBadgeIcon(iconUri, update.UnreadCount)))
                {
                    _log.Warning("Unable to enqueue tray unread badge update on the UI dispatcher.");
                }

                return Task.CompletedTask;
            },
            _log.Warning);
        _transientReprojectQueue = new CoalescingBackgroundAction<bool>(
            "Transient chat list reprojection",
            TimeSpan.FromMilliseconds(250),
            _ =>
            {
                if (!DispatcherQueue.TryEnqueue(ReprojectTransientUnreadRowsNow))
                {
                    _log.Warning("Unable to enqueue transient chat list reprojection on the UI dispatcher.");
                }

                return Task.CompletedTask;
            },
            _log.Warning);
        _settingsStore = new JsonSettingsStore(_paths.SettingsPath);
        _cache = new SqliteMessageCache(_paths.DatabasePath);
        _messageCache = new MessageCacheAdapter(_cache);
        _setupChecklistService = new SetupChecklistService(_bridge, _paths, _macHostActions);
        _supportBundleService = new SupportBundleService(_paths, _log);
        // The imsg rpc server answers strictly in order, so sends, message
        // actions, and slow background niceties each get their own SSH+rpc
        // channel; nothing can queue ahead of a user's send anymore.
        _sendChannel = new DedicatedBridgeChannel("send", _bridge, () => _settings, _log.Info, _log.Warning);
        _actionChannel = new DedicatedBridgeChannel("action", _bridge, () => _settings, _log.Info, _log.Warning);
        _nicetyChannel = new DedicatedBridgeChannel("nicety", _bridge, () => _settings, _log.Info, _log.Warning);
        _messageSendService = new MessageSendService(_sendChannel);
        _messageSendWorkflowService = new MessageSendWorkflowService(_messageSendService);
        _messageActionWorkflowService = new MessageActionWorkflowService(_actionChannel);
        _attachmentService = new AttachmentService(_paths, _fileTransfer, _macHostActions);
        _attachmentSendWorkflowService = new AttachmentSendWorkflowService(_attachmentService, _messageSendService);
        _webCompanion = new WebCompanionService(
            _log,
            _messageCache,
            _messageSendService,
            _actionChannel,
            _attachmentSendWorkflowService,
            () => _settings,
            () => _appSettings,
            () => _capabilities,
            () => _bridge.IsConnected,
            () => _lastUnreadBadgeCount,
            MarkChatReadFromCompanionAsync,
            (message, attachment) => _attachmentService.GetLocalPath(message, attachment),
            RefreshChatFromCompanionAsync);
        var macIMessageHealthService = new MacIMessageHealthService();
        _connectionService = new BridgeConnectionService(_bridge, macIMessageHealthService.ProbeAfterConnectionFailureAsync);
        _conversationDataService = new ConversationDataService(_messageCache, _bridge);
        _conversationSyncService = new ConversationSyncService(_messageCache, _bridge);
        _historyFetchService = new ConversationHistoryFetchService(_bridge);
        _reactionHistoryRefreshCoordinator = new ReactionHistoryRefreshCoordinator(
            ReactionHistoryRefreshDebounce,
            SelectedHistoryTimeout,
            IsSelectedChat,
            RefreshHistoryAsync);
        _bridgeNotificationWorkflowService = new BridgeNotificationWorkflowService(_messageCache, _watchNotifications);
        _shell.Conversation.SetAttachmentLocalPathResolver(ResolveAttachmentLocalPath);
        _shell.ConfigureConnectionCommands(_ => ConnectAsync(), _ => DisconnectActiveBridgeAsync());
        _bridge.NotificationReceived += OnBridgeNotification;
        _bridge.ConnectionClosed += OnBridgeConnectionClosed;
        _attachmentService.DownloadStateChanged += OnAttachmentDownloadStateChanged;
        ChatListPane.ItemsSource = _shell.Conversations.Chats;
        MessageListView.ItemsSource = _shell.Conversation.Messages;
        MessageListView.Loaded += OnMessageListLoaded;
        MessageListView.LayoutUpdated += OnMessageListLayoutUpdated;
        ComposeBar.TextBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnComposeBoxKeyDown), handledEventsToo: true);
        ComposeBar.TextBox.TextChanged += OnComposeTextChanged;
        ComposeBar.AttachmentFilesRequested += OnComposeAttachmentFilesRequested;
        ComposeBar.PendingAttachmentsChanged += OnComposePendingAttachmentsChanged;
        ComposeBar.PollRequested += OnComposePollRequested;
        ComposeBar.SendEffectRequested += OnComposeSendEffectRequested;
        ComposeBar.TextFormattingRequested += OnComposeTextFormattingRequested;
        ComposeBar.ReplyCanceled += OnComposeReplyCanceled;
        ComposeBar.VoiceCancelRequested += OnVoiceCancelClicked;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _voiceRecordingTimer?.Stop();
            _voiceRecording.Dispose();
            _webCompanion.Stop();
            _ = ResetBridgeChannelsAsync();
            _historyLoadCancellation?.Cancel();
            _historyLoadCancellation?.Dispose();
            _cacheSyncCancellation?.Cancel();
            _cacheSyncCancellation?.Dispose();
            _chatSearchCancellation?.Cancel();
            _chatSearchCancellation?.Dispose();
            _bridgeReconnectCancellation?.Cancel();
            _bridgeReconnectCancellation?.Dispose();
            ResetTypingIndicatorState();
            lock (_taskbarUnreadIconLock)
            {
                _taskbarUnreadIcon?.Dispose();
                _taskbarUnreadIcon = null;
            }
            DisposeTrayIcon();
        };
        _log.Info("WinIMsg startup constructor completed.");
    }

    public bool ShouldMinimizeToTray => _appSettings.MinimizeToTray;

    public void ConnectFromTray()
    {
        _ = ConnectAsync();
    }

    public void HandleSystemResume()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(HandleSystemResume);
            return;
        }

        _ = RecoverBridgeAfterSystemResumeAsync();
    }

    public void SelectChat(string stableId)
    {
        var item = _shell.Conversations.AllChats.FirstOrDefault(candidate => candidate.ContainsStableId(stableId));
        if (item is null)
        {
            return;
        }

        SaveActiveComposeDraft();
        ChatListPane.SelectedItem = item;
        _shell.Conversations.SelectedChat = item;
        _ = SelectChatAsync(item);
    }

    public void AuthorizeWebCompanionLaunch(string nonce)
    {
        if (!_webCompanion.AuthorizeBootstrapNonce(nonce))
        {
            _log.Warning("Rejected an invalid web companion launch nonce.");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await Task.Yield();
        await InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        var startup = Stopwatch.StartNew();
        try
        {
            _log.Info("Startup load started.");
            _appSettings = _settingsStore.Load();
            _settings = _appSettings.ToBridgeSettings();
            _settingsStore.Save(_appSettings);
            _shell.LoadSettings(_appSettings);
            ApplyConversationListWidth(_appSettings.ConversationListWidth);
            _log.Info($"Startup settings loaded. elapsedMs={startup.ElapsedMilliseconds}");

            RefreshTrayIcon();
            _log.Info($"Startup tray initialized. elapsedMs={startup.ElapsedMilliseconds}");

            _log.Info($"Startup deferred profile/settings UI initialization. elapsedMs={startup.ElapsedMilliseconds}");

            await _messageCache.InitializeAsync();
            _log.Info($"Startup cache initialized. elapsedMs={startup.ElapsedMilliseconds}");
            ApplyWebCompanionState();

            await LoadCachedChatsAsync();
            _log.Info($"Startup cached chats loaded. elapsedMs={startup.ElapsedMilliseconds}");

            if (_settings.IsConfigured)
            {
                _ = ConnectAsync(saveSettings: false);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Startup load failed.", ex);
            SetConnectionStatus(ConnectionState.Failed, "Startup failed. Open Settings > Diagnostics for logs.");
        }
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_bridge.IsConnected)
        {
            await ConnectAsync();
            return;
        }

        await RefreshChatsAsync();
    }

    private async void OnSyncCacheClicked(object sender, RoutedEventArgs e) => await SyncFullCacheAsync(showCompletion: true);

    private async void OnClearCacheClicked(object sender, RoutedEventArgs e) => await ClearLocalCacheAsync();

    private void OnSettingsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettingsNav)
        {
            return;
        }

        if (SettingsNavList.SelectedItem is ListViewItem { Tag: string tag })
        {
            ShowSettingsSection(tag);
        }
    }

    private void OnProfileListItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImsgBridgeProfile profile)
        {
            ShowProfileDetail(profile.Id);
        }
    }

    private void OnBackToProfilesClicked(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("Profiles");
    }

    private void OnSaveGeneralClicked(object sender, RoutedEventArgs e)
    {
        SaveGeneralSettings();
        SetStatus("General settings saved.");
    }

    private void OnSaveProfileClicked(object sender, RoutedEventArgs e)
    {
        SaveProfileFromForm();
        SetStatus("Profile saved.");
    }

    private void OnGeneralSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettingsForm)
        {
            SetGeneralSettingsDirty(true);
        }
    }

    private void OnGeneralSettingsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSettingsForm && _settingsFormInitialized)
        {
            SetGeneralSettingsDirty(true);
        }
    }

    private void OnGeneralSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSettingsForm && _settingsFormInitialized)
        {
            SetGeneralSettingsDirty(true);
        }
    }

    private void OnProfileSettingsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSettingsForm && _settingsFormInitialized)
        {
            SetProfileSettingsDirty(true);
            UpdateRemoteAccessModelStatusFromForm();
        }
    }

    private void OnProfileSettingsNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_loadingSettingsForm && _settingsFormInitialized)
        {
            SetProfileSettingsDirty(true);
            UpdateRemoteAccessModelStatusFromForm();
        }
    }

    private void OnProfileSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSettingsForm && _settingsFormInitialized)
        {
            SetProfileSettingsDirty(true);
            UpdateRemoteAccessModelStatusFromForm();
        }
    }

    private async void OnConnectProfileClicked(object sender, RoutedEventArgs e)
    {
        SaveProfileFromForm(makeActive: true);
        await ConnectAsync(saveSettings: false);
    }

    private void OnAdvancedInstructionsClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://docs.openclaw.ai/channels/imessage") { UseShellExecute = true });
    }

    private void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _log.EnsureLogFile();
            Process.Start(new ProcessStartInfo(_log.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Unable to open log file.", ex);
            SetStatus($"Unable to open log file: {ex.Message}");
        }
    }

    private void OnOpenLogsFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_log.LogDirectory);
            Process.Start(new ProcessStartInfo(_log.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Unable to open logs folder.", ex);
            SetStatus($"Unable to open logs folder: {ex.Message}");
        }
    }

    private async void OnPromptMacPermissionsClicked(object sender, RoutedEventArgs e)
    {
        if (!_settings.IsConfigured)
        {
            SetStatus("Configure a Mac profile before prompting permissions.");
            SetContactsSettingsStatus("Configure a Mac profile before prompting Contacts and Full Disk Access permissions.");
            return;
        }

        PromptMacPermissionsButton.IsEnabled = false;
        SetStatus("Prompting Mac permissions...");
        SetContactsSettingsStatus("Attempting to open Contacts, Full Disk Access, and Automation privacy panes on the Mac. If nothing appears, unlock the Mac and use the Privacy & Security pane you opened manually.");
        _log.Info("Prompting Mac permissions... started.");
        try
        {
            using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
            await _macHostActions.PromptPermissionsAsync(_settings, timeout.Token);
            ApplyCapabilityState();
            SetStatus("Mac privacy request sent.");
            SetContactsSettingsStatus("Sent privacy pane open requests to the Mac for Contacts, Full Disk Access, and Automation. If System Settings did not come forward, use the Privacy & Security pane you opened manually. Because win-imsg reaches imsg through SSH, the process to allow is usually Remote Login/OpenSSH, sshd, or /usr/libexec/sshd-keygen-wrapper rather than imsg itself.");
            _log.Info("Prompting Mac permissions... completed.");
        }
        catch (Exception ex)
        {
            _log.Error("Prompting Mac permissions... failed.", ex);
            var message = ex is OperationCanceledException
                ? $"Mac permission prompt timed out after {FormatDuration(_settings.RequestTimeout)}."
                : ex.Message;
            SetStatus(message);
            SetContactsSettingsStatus($"{message} Since macOS may suppress opening System Settings from SSH, use the Privacy & Security pane on the Mac and enable Contacts and Full Disk Access for Remote Login/OpenSSH, sshd, or /usr/libexec/sshd-keygen-wrapper if listed.");
        }
        finally
        {
            PromptMacPermissionsButton.IsEnabled = _settings.IsConfigured;
        }
    }

    private async void OnRunSetupChecklistClicked(object sender, RoutedEventArgs e)
    {
        RunSetupChecklistButton.IsEnabled = false;
        SetStatus("Running setup checklist...");
        SetSetupChecklistStatus($"Running setup checklist...\n\n{SetupChecklistService.BuildCommandPreview(_settings)}");
        _log.Info("Setup checklist started.");
        await Task.Yield();

        try
        {
            var normalized = _settings.Normalize();
            var candidateCount = Math.Max(1, normalized.CandidateAddresses.Count);
            var timeoutSeconds = Math.Clamp((candidateCount * 2 + 1) * normalized.RequestTimeoutSeconds, 30, 600);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var report = await _setupChecklistService.RunAsync(normalized, timeout.Token);
            SetSetupChecklistStatus(SetupChecklistService.FormatForSettings(report, normalized));
            _log.Info($"Setup checklist completed. {report.Summary}");
            SetStatus(report.HasFailures
                ? "Setup checklist found required fixes. Open Settings > Diagnostics for details."
                : "Setup checklist completed.");
        }
        catch (OperationCanceledException)
        {
            var message = "Setup checklist timed out before all checks completed.";
            SetSetupChecklistStatus(message);
            SetStatus(message);
            _log.Warning(message);
        }
        catch (Exception ex)
        {
            _log.Error("Setup checklist failed.", ex);
            SetSetupChecklistStatus($"Setup checklist failed: {ex.Message}. Details were written to {_log.LogFilePath}.");
            SetStatus("Setup checklist failed. Open Settings > Diagnostics for logs.");
        }
        finally
        {
            RunSetupChecklistButton.IsEnabled = true;
        }
    }

    private async void OnExportSupportBundleClicked(object sender, RoutedEventArgs e)
    {
        ExportSupportBundleButton.IsEnabled = false;
        SetStatus("Exporting support bundle...");
        _log.Info("Support bundle export started.");

        try
        {
            var request = new SupportBundleExportRequest(
                _appSettings,
                _settings,
                _capabilities,
                SetupChecklistTextBlock.Text,
                ContactNamesTextBlock.Text,
                CacheSyncStatusText.Text,
                StatusTextBlock.Text);
            var result = await _supportBundleService.ExportAsync(request);
            SetSetupChecklistStatus(
                $"Support bundle exported:\n{result.BundlePath}\n\nIncluded files:\n{string.Join(Environment.NewLine, result.Entries.Select(entry => $"- {entry}"))}");
            SetStatus("Support bundle exported.");
        }
        catch (Exception ex)
        {
            _log.Error("Support bundle export failed.", ex);
            SetSetupChecklistStatus($"Support bundle export failed: {ex.Message}. Details were written to {_log.LogFilePath}.");
            SetStatus("Support bundle export failed. Open Settings > Diagnostics for logs.");
        }
        finally
        {
            ExportSupportBundleButton.IsEnabled = true;
        }
    }

    private async void OnCheckContactsClicked(object sender, RoutedEventArgs e)
    {
        CheckContactsButton.IsEnabled = false;
        SetStatus("Checking contact names...");
        var source = _bridge.IsConnected ? "rpc+cli" : _settings.IsConfigured ? "cli+cache" : "cache";
        _log.Info($"Contact check started. source={source}");
        try
        {
            MacContactsAuthorizationResult? authorization = null;
            string? requestNote = null;
            IReadOnlyList<ImsgChat> cliChats = [];
            if (_settings.IsConfigured)
            {
                using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
                authorization = await _macHostActions.CheckContactsAuthorizationAsync(_settings, timeout.Token);
                cliChats = await _chatReader.ListChatsAsync(_settings, ChatListFetchLimit, timeout.Token);

                if (authorization.IsNotDetermined)
                {
                    var probeAddress = SelectContactProbeAddress(cliChats);
                    if (!string.IsNullOrWhiteSpace(probeAddress))
                    {
                        requestNote = await RequestContactsAccessWithProbeAsync(_settings, probeAddress);
                        using var afterRequestTimeout = new CancellationTokenSource(_settings.RequestTimeout);
                        authorization = await _macHostActions.CheckContactsAuthorizationAsync(_settings, afterRequestTimeout.Token);
                        requestNote = DescribeContactsRequestOutcome(requestNote, authorization);
                        cliChats = await _chatReader.ListChatsAsync(_settings, ChatListFetchLimit, afterRequestTimeout.Token);
                    }
                    else
                    {
                        requestNote = "No usable chat handle was available to run imsg nickname --local for a Contacts prompt.";
                    }
                }
            }

            IReadOnlyList<ImsgChat> chats;
            if (_bridge.IsConnected)
            {
                using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
                chats = await _bridge.ListChatsAsync(limit: ChatListFetchLimit, cancellationToken: timeout.Token);
                chats = ContactIdentityService.MergeExplicitContactNamesByChatIdentity(chats, cliChats);
                await _messageCache.UpsertChatsAsync(chats, timeout.Token);
                await ReplaceChatsAsync(chats, timeout.Token, reconcileTransientUnread: true);
            }
            else
            {
                chats = await _messageCache.GetChatsAsync();
                chats = ContactIdentityService.MergeExplicitContactNamesByChatIdentity(chats, cliChats);
                await ReplaceChatsAsync(chats);
            }

            SetContactsSettingsStatus(ContactIdentityService.BuildContactNamesDiagnostic(chats, cliChats, authorization, requestNote));
            _log.Info($"Contact check completed. source={source}; chats={chats.Count}; resolved={chats.Count(ContactIdentityService.HasResolvedDisplayName)}");
            SetStatus(_bridge.IsConnected ? "Connected" : "Ready");
        }
        catch (Exception ex)
        {
            _log.Error("Contact check failed.", ex);
            SetContactsSettingsStatus($"Unable to check contact names: {ex.Message}. Details were written to {_log.LogFilePath}.");
            SetStatus("Contact check failed. Open Settings > Diagnostics for logs.");
        }
        finally
        {
            CheckContactsButton.IsEnabled = true;
        }
    }

    private async void OnEmojiButtonClicked(object sender, RoutedEventArgs e)
    {
        ComposeBar.FocusComposer();
        await Task.Delay(125);
        if (!TryOpenNativeEmojiPicker())
        {
            ShowEmojiFallback();
        }
    }

    private void OnEmojiMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && !string.IsNullOrEmpty(item.Text))
        {
            InsertComposeText(item.Text);
        }
    }

    private void OnJumpToLatestClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedChat is null)
        {
            return;
        }

        SetChatPinnedAwayFromLatest(_selectedChat.StableId, isPinned: false);
        _shell.Conversation.ResetToLatest();
        ScrollMessagesToLatest(forceLatest: true);
    }

    private void OnConversationSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ConversationSplitter.Cursor = HorizontalResizeCursor;
        ConversationSplitterLine.Opacity = 1;
    }

    private void OnConversationSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingConversationSplit)
        {
            ConversationSplitter.Cursor = null;
            ConversationSplitterLine.Opacity = 0.55;
        }
    }

    private void OnConversationSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingConversationSplit = true;
        _splitDragStartX = e.GetCurrentPoint(ConversationShellGrid).Position.X;
        _splitDragStartWidth = ConversationShellGrid.ColumnDefinitions[0].ActualWidth;
        ConversationSplitterLine.Opacity = 1;
        ConversationSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnConversationSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingConversationSplit)
        {
            return;
        }

        var currentX = e.GetCurrentPoint(ConversationShellGrid).Position.X;
        var nextWidth = _splitDragStartWidth + currentX - _splitDragStartX;
        ApplyConversationListWidth(nextWidth);
        e.Handled = true;
    }

    private void OnConversationSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingConversationSplit)
        {
            return;
        }

        _isResizingConversationSplit = false;
        ConversationSplitter.Cursor = null;
        ConversationSplitterLine.Opacity = 0.55;
        ConversationSplitter.ReleasePointerCapture(e.Pointer);

        var width = ConversationShellGrid.ColumnDefinitions[0].ActualWidth;
        _appSettings = _appSettings with { ConversationListWidth = width };
        SaveAppSettings();
        e.Handled = true;
    }

    private void ApplyConversationListWidth(double requestedWidth)
    {
        var availableWidth = ConversationShellGrid.ActualWidth > 0
            ? ConversationShellGrid.ActualWidth
            : MinConversationPaneWidth + MaxConversationListWidth;
        var maxWidth = Math.Min(MaxConversationListWidth, Math.Max(MinConversationListWidth, availableWidth - MinConversationPaneWidth));
        var width = Math.Clamp(requestedWidth, MinConversationListWidth, maxWidth);
        ConversationShellGrid.ColumnDefinitions[0].Width = new GridLength(width);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        SaveGeneralSettings();
        SetStatus("Settings saved.");
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        EnsureSettingsFormInitialized();
        SetSettingsVisible(true);
        if (SettingsNavList.SelectedIndex < 0)
        {
            SettingsNavList.SelectedIndex = 0;
        }

        ShowSettingsSection("General");
    }

    private void OnCloseSettingsClicked(object sender, RoutedEventArgs e)
    {
        CancelSettingsEdits();
    }

    private void OnCancelSettingsClicked(object sender, RoutedEventArgs e)
    {
        CancelSettingsEdits();
    }

    private void RestoreFromTray()
    {
        ((App)Application.Current).RestoreMainWindow();
    }

    private void ExitFromTray()
    {
        DisposeTrayIcon();
        ((App)Application.Current).ExitApplication();
    }

    private void OnTrayOpenClicked(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
    }

    private async void OnTrayConnectClicked(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).RestoreMainWindow();
        await ConnectAsync();
    }

    private void OnTrayExitClicked(object sender, RoutedEventArgs e)
    {
        ExitFromTray();
    }

    private async void OnChatSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _shell.Conversations.SearchQuery = ChatListPane.SearchText;
        UpdateChatSearchStatusFromViewModel();
        await RefreshChatSearchAsync();
    }

    private async void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProfiles || ProfilePicker.SelectedValue is not string profileId)
        {
            return;
        }

        await SwitchProfileAsync(profileId, reconnectIfConnected: _bridge.IsConnected);
    }

    private void OnAddProfileClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentSettingsContext(refreshPickers: false);
        var profile = _shell.SettingsView.AddProfile(_appSettings.Profiles.Count + 1);
        _appSettings = _shell.SettingsView.Settings;
        SaveAppSettings();
        LoadSettingsIntoForm();
        RefreshProfilePickers();
        ShowProfileDetail(profile.Id);
        SetStatus("Profile added.");
    }

    private async void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_appSettings.Profiles.Count <= 1)
        {
            return;
        }

        var deletedProfile = _appSettings.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _loadedSettingsProfileId, StringComparison.OrdinalIgnoreCase))
            ?? _appSettings.ActiveProfile;

        if (!await ConfirmAsync("Delete Profile", $"Delete the profile \"{deletedProfile.Name}\"?"))
        {
            return;
        }

        var deletedWasActive = string.Equals(_appSettings.ActiveProfileId, deletedProfile.Id, StringComparison.OrdinalIgnoreCase);
        _appSettings = _shell.SettingsView.DeleteProfile(deletedProfile.Id);
        SaveAppSettings();
        if (deletedWasActive)
        {
            await DisconnectActiveBridgeAsync();
            _capabilities = new ImsgCapabilities();
            _selectedChat = null;
            _shell.Conversation.ClearSelection();
            JumpToLatestButton.Visibility = Visibility.Collapsed;
        }

        LoadSettingsIntoForm();
        RefreshProfilePickers();
        ShowSettingsSection("Profiles");
        ApplyCapabilityState();
        SetStatus("Profile deleted.");
    }

    private async void OnChatClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChatListItem item)
        {
            await OpenChatListItemAsync(item);
        }
    }

    private async void OnChatContextOpenRequested(object? sender, ChatListContextActionEventArgs e)
    {
        await OpenChatListItemAsync(e.Chat);
    }

    private async void OnChatContextRefreshRequested(object? sender, ChatListContextActionEventArgs e)
    {
        await RefreshChatListItemAsync(e.Chat);
    }

    private async void OnChatContextMarkReadRequested(object? sender, ChatListContextActionEventArgs e)
    {
        await MarkChatReadAsync(e.Chat);
    }

    private async void OnChatMassMarkReadRequested(object? sender, ChatListMassActionEventArgs e)
    {
        await MarkChatsReadAsync(e.Chats);
    }

    private void OnChatContextCopyNameRequested(object? sender, ChatListContextActionEventArgs e)
    {
        CopyTextToClipboard(e.Chat.DisplayName);
        SetStatus("Conversation name copied.");
    }

    private void OnChatContextCopyAddressRequested(object? sender, ChatListContextActionEventArgs e)
    {
        var address = BuildChatAddressText(e.Chat);
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("This conversation does not expose an address.");
            return;
        }

        CopyTextToClipboard(address);
        SetStatus("Conversation address copied.");
    }

    private void OnChatContextCopyGuidRequested(object? sender, ChatListContextActionEventArgs e)
    {
        var guid = e.Chat.Chat.Guid;
        if (string.IsNullOrWhiteSpace(guid))
        {
            SetStatus("This conversation does not expose a chat GUID.");
            return;
        }

        CopyTextToClipboard(guid);
        SetStatus("Conversation GUID copied.");
    }

    private async Task OpenChatListItemAsync(ChatListItem item)
    {
        SaveActiveComposeDraft();
        if (item.IsNewMessageDraft)
        {
            SelectNewMessageDraft();
            return;
        }

        item = ClearEmptyNewMessageDraftBeforeSelecting(item);
        ChatListPane.SelectedItem = item;
        _shell.Conversations.SelectedChat = item;
        if (!string.IsNullOrWhiteSpace(ChatListPane.SearchText))
        {
            ChatListPane.SearchText = string.Empty;
        }

        await SelectChatAsync(item);
    }

    private ChatListItem ClearEmptyNewMessageDraftBeforeSelecting(ChatListItem nextSelection)
    {
        if (!_shell.Conversation.IsNewMessageDraft)
        {
            return nextSelection;
        }

        var hasRecipients = ParseDraftRecipients(_shell.Conversation.DraftRecipientText).Count > 0;
        var hasText = !string.IsNullOrWhiteSpace(ComposeBar.Text);
        var hasAttachments = ComposeBar.PendingAttachmentPaths.Count > 0;
        if (hasRecipients || hasText || hasAttachments)
        {
            return nextSelection;
        }

        ClearComposeDraft(ChatListItem.NewMessageDraftStableId);
        _shell.Conversations.ClearNewMessageDraft(nextSelection);
        return _shell.Conversations.SelectedChat is { IsNewMessageDraft: false } selected
            ? selected
            : nextSelection;
    }

    private async Task RefreshChatListItemAsync(ChatListItem item)
    {
        if (item.IsNewMessageDraft)
        {
            SelectNewMessageDraft();
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before refreshing a conversation.");
            return;
        }

        await RunUiActionAsync("Refreshing conversation...", async token =>
        {
            await RefreshHistoryAsync(item, token);
        }, SelectedHistoryTimeout);
    }

    private static void CopyTextToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string BuildChatAddressText(ChatListItem chat)
    {
        return string.Join(", ", chat.Chats
            .SelectMany(ChatAddressCandidates)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ChatAddressCandidates(ImsgChat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.Identifier))
        {
            yield return chat.Identifier;
        }

        foreach (var participant in chat.Participants)
        {
            if (!string.IsNullOrWhiteSpace(participant))
            {
                yield return participant;
            }
        }
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e) => await SendCurrentMessageAsync();

    private async void OnComposeBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.V && IsControlKeyDown())
        {
            if (await TryQueueClipboardAttachmentFilesAsync())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (IsControlKeyDown() || IsShiftKeyDown())
        {
            if (!e.Handled)
            {
                e.Handled = true;
                InsertComposeText(Environment.NewLine);
            }

            return;
        }

        e.Handled = true;
        await SendCurrentMessageAsync();
    }

    private void OnComposeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_restoringComposeDraft && _pendingTextFormatting.Count > 0)
        {
            _pendingTextFormatting.Clear();
        }

        SaveActiveComposeDraft();
        UpdateTypingIndicatorFromCompose();
    }

    private void OnComposePendingAttachmentsChanged(object? sender, EventArgs e)
    {
        SaveActiveComposeDraft();
    }

    private void UpdateTypingIndicatorFromCompose()
    {
        if (!_appSettings.SendTypingIndicators)
        {
            return;
        }

        if (_restoringComposeDraft)
        {
            return;
        }

        if (_selectedChat is null ||
            string.IsNullOrWhiteSpace(ComposeBar.Text) ||
            !CapabilityActionPolicy.CanSendTyping(hasSelectedChat: true, connected: _bridge.IsConnected, _capabilities))
        {
            _ = StopTypingIndicatorAsync();
            return;
        }

        StartOrRefreshTypingIndicator(_selectedChat);
    }

    private void StartOrRefreshTypingIndicator(ChatListItem chat)
    {
        if (!HasTypingTarget(chat.Chat))
        {
            return;
        }

        var target = chat.Chat;
        if (_typingIndicatorActive &&
            _typingIndicatorChat is not null &&
            !IsSameTypingTarget(_typingIndicatorChat, target))
        {
            var previous = _typingIndicatorChat;
            _typingIndicatorActive = false;
            _typingIndicatorChat = null;
            _ = SendTypingIndicatorSignalAsync(previous, isTyping: false);
        }

        if (!_typingIndicatorActive)
        {
            _typingIndicatorActive = true;
            _typingIndicatorChat = target;
            _ = SendTypingIndicatorSignalAsync(target, isTyping: true);
        }
        else
        {
            _typingIndicatorChat = target;
        }

        ArmTypingIndicatorAutoStop();
    }

    private void ArmTypingIndicatorAutoStop()
    {
        _typingStopCancellation?.Cancel();
        _typingStopCancellation?.Dispose();
        _typingStopCancellation = new CancellationTokenSource();
        var source = _typingStopCancellation;
        _ = StopTypingIndicatorAfterInactivityAsync(source);
    }

    private async Task StopTypingIndicatorAfterInactivityAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TypingIndicatorInactivityTimeout, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!source.IsCancellationRequested)
        {
            await StopTypingIndicatorAsync(source);
        }
    }

    private async Task StopTypingIndicatorAsync(CancellationTokenSource? matchingSource = null)
    {
        if (matchingSource is not null && !ReferenceEquals(matchingSource, _typingStopCancellation))
        {
            return;
        }

        var target = _typingIndicatorChat;
        var wasActive = _typingIndicatorActive;
        ResetTypingIndicatorState();
        if (!wasActive || target is null || !_bridge.IsConnected)
        {
            return;
        }

        await SendTypingIndicatorSignalAsync(target, isTyping: false);
    }

    private void ResetTypingIndicatorState()
    {
        _typingStopCancellation?.Cancel();
        _typingStopCancellation?.Dispose();
        _typingStopCancellation = null;
        _typingIndicatorActive = false;
        _typingIndicatorChat = null;
    }

    private async Task SendTypingIndicatorSignalAsync(ImsgChat target, bool isTyping)
    {
        if (!HasTypingTarget(target) ||
            !CapabilityActionPolicy.CanSendTyping(hasSelectedChat: true, connected: _bridge.IsConnected, _capabilities))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TypingIndicatorRequestTimeout);
            await _nicetyChannel.SetTypingAsync(target.Id, target.Identifier, isTyping, target.Guid, timeout.Token);
        }
        catch (Exception ex)
        {
            _log.Warning($"Typing indicator {(isTyping ? "start" : "stop")} failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool HasTypingTarget(ImsgChat chat) =>
        chat.Id is not null ||
        !string.IsNullOrWhiteSpace(chat.Identifier) ||
        !string.IsNullOrWhiteSpace(chat.Guid);

    private static bool IsSameTypingTarget(ImsgChat left, ImsgChat right)
    {
        if (!string.IsNullOrWhiteSpace(left.StableId) &&
            string.Equals(left.StableId, right.StableId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (left.Id is not null && right.Id is not null && left.Id == right.Id)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.Guid) &&
            string.Equals(left.Guid, right.Guid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.Identifier) &&
            string.Equals(left.Identifier, right.Identifier, StringComparison.OrdinalIgnoreCase);
    }

    private void OnNewChatClicked(object sender, RoutedEventArgs e)
    {
        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before starting a new conversation.");
            return;
        }

        SelectNewMessageDraft();
    }

    private void OnDraftRecipientsTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!_shell.Conversation.IsNewMessageDraft)
        {
            return;
        }

        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = FindRecipientSuggestions(sender.Text);
        }
    }

    private void OnDraftRecipientSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is not string suggestion)
        {
            return;
        }

        CommitDraftRecipient(sender, suggestion);
    }

    private void OnDraftRecipientQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (e.ChosenSuggestion is string suggestion)
        {
            var alreadyCommittedBySuggestionChosen =
                string.IsNullOrWhiteSpace(sender.Text) &&
                string.Equals(_lastCommittedDraftRecipientSuggestion, suggestion, StringComparison.OrdinalIgnoreCase);
            if (!alreadyCommittedBySuggestionChosen)
            {
                CommitDraftRecipient(sender, suggestion);
            }

            _lastCommittedDraftRecipientSuggestion = null;
            return;
        }

        var query = (e.QueryText ?? sender.Text).Trim();
        if (query.Length > 0)
        {
            CommitDraftRecipient(sender, query);
        }
    }

    private void OnDraftRecipientChipRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string recipient)
        {
            return;
        }

        var recipients = ParseDraftRecipients(_shell.Conversation.DraftRecipientText)
            .Where(item => !string.Equals(item, recipient, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _shell.Conversation.DraftRecipientText = string.Join(", ", recipients);
        OnDraftRecipientTextCommitted();
    }

    private void CommitDraftRecipient(AutoSuggestBox sender, string recipient)
    {
        var cleanRecipient = recipient.Trim();
        if (cleanRecipient.Length == 0)
        {
            return;
        }

        var recipients = ParseDraftRecipients(_shell.Conversation.DraftRecipientText).ToList();
        if (!recipients.Any(item => string.Equals(item, cleanRecipient, StringComparison.OrdinalIgnoreCase)))
        {
            recipients.Add(cleanRecipient);
        }

        _shell.Conversation.DraftRecipientText = string.Join(", ", recipients);
        sender.Text = string.Empty;
        sender.ItemsSource = _recipientSuggestions.Take(12).ToList();
        _lastCommittedDraftRecipientSuggestion = cleanRecipient;
        OnDraftRecipientTextCommitted();
    }

    private void OnDraftRecipientTextCommitted()
    {
        if (!_shell.Conversation.IsNewMessageDraft)
        {
            return;
        }

        var draft = _shell.Conversations.UpdateNewMessageDraftRecipients(_shell.Conversation.DraftRecipientText);
        _shell.Conversations.SelectedChat = draft;
        ChatListPane.SelectedItem = draft;
        UpdateDraftRecipientStatus();
        SaveActiveComposeDraft();
    }

    private async void OnVoiceButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_voiceRecording.IsRecording)
        {
            await StopVoiceRecordingAsync(discard: false);
            return;
        }

        await StartVoiceRecordingAsync();
    }

    private async void OnVoiceCancelClicked(object? sender, RoutedEventArgs e)
    {
        await StopVoiceRecordingAsync(discard: true);
    }

    private async Task StartVoiceRecordingAsync()
    {
        var filePath = VoiceRecordingService.BuildRecordingFilePath(_paths.VoiceMessages, DateTimeOffset.Now);
        try
        {
            await _voiceRecording.StartAsync(filePath);
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("Microphone access is blocked. Allow microphone access for desktop apps in Windows Settings > Privacy & security > Microphone.");
            return;
        }
        catch (Exception ex)
        {
            _log.Warning($"Voice recording failed to start. {ex.GetType().Name}: {ex.Message}");
            SetStatus("Could not start voice recording. Check that a microphone is connected.");
            return;
        }

        ComposeBar.SetVoiceRecordingState(true, "Recording voice message - 0:00");
        SetStatus("Recording voice message...");
        if (_voiceRecordingTimer is null)
        {
            _voiceRecordingTimer = DispatcherQueue.CreateTimer();
            _voiceRecordingTimer.Interval = TimeSpan.FromMilliseconds(500);
            _voiceRecordingTimer.Tick += (_, _) =>
            {
                if (_voiceRecording.IsRecording)
                {
                    ComposeBar.UpdateVoiceRecordingStatus(
                        $"Recording voice message - {VoiceRecordingService.FormatElapsed(_voiceRecording.Elapsed)}");
                }
            };
        }

        _voiceRecordingTimer.Start();
        _log.Info("Voice recording started.");
    }

    private async Task StopVoiceRecordingAsync(bool discard)
    {
        if (!_voiceRecording.IsRecording)
        {
            return;
        }

        _voiceRecordingTimer?.Stop();
        var tooShort = VoiceRecordingService.IsTooShort(_voiceRecording.Elapsed);
        string? filePath = null;
        try
        {
            filePath = await _voiceRecording.StopAsync(discard || tooShort);
        }
        catch (Exception ex)
        {
            _log.Warning($"Voice recording failed to stop cleanly. {ex.GetType().Name}: {ex.Message}");
        }

        ComposeBar.SetVoiceRecordingState(false);
        if (discard)
        {
            SetStatus("Voice message discarded.");
            _log.Info("Voice recording discarded.");
            return;
        }

        if (tooShort || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            SetStatus(tooShort
                ? "Voice message was too short to attach."
                : "Voice recording did not produce a playable file.");
            return;
        }

        ComposeBar.AddPendingAttachment(filePath);
        ComposeBar.FocusComposer();
        SetStatus("Voice message ready to send.");
        _log.Info("Voice recording attached to composer.");
    }

    private async void OnComposePollRequested(object sender, RoutedEventArgs e)
    {
        await SendPollForSelectedChatAsync();
    }

    private void OnComposeSendEffectRequested(object? sender, ComposeSendEffectRequestedEventArgs e)
    {
        _pendingSendEffect = e.Effect;
        _pendingSendEffectLabel = e.Label;
        ComposeBar.FocusComposer();
        SetStatus($"{e.Label} effect will be used for the next text message.");
    }

    private void OnComposeTextFormattingRequested(object? sender, ComposeTextFormattingRequestedEventArgs e)
    {
        if (!CapabilityActionPolicy.CanSendRich(_selectedChat is not null, _bridge.IsConnected, _capabilities))
        {
            SetStatus("Text formatting requires imsg advanced bridge support and send.rich.");
            return;
        }

        if (e.Length <= 0)
        {
            SetStatus("Select text in the message box before applying formatting.");
            ComposeBar.FocusComposer();
            return;
        }

        _pendingTextFormatting.Add(new RichTextFormattingRange(e.Start, e.Length, [e.Style]));
        ComposeBar.FocusComposer();
        SetStatus($"{e.Label} formatting will be used for the next text message.");
    }

    private void OnComposeReplyCanceled(object? sender, EventArgs e)
    {
        ClearPendingReplyTarget();
        ComposeBar.FocusComposer();
        SetStatus("Reply canceled.");
    }

    private async void OnMarkReadClicked(object sender, RoutedEventArgs e)
    {
        await MarkSelectedChatReadAsync();
    }

    private async void OnMarkUnreadClicked(object sender, RoutedEventArgs e)
    {
        await MarkSelectedChatUnreadAsync();
    }

    private async void OnFaceTimeLinkClicked(object sender, RoutedEventArgs e)
    {
        await CreateFaceTimeLinkForSelectedChatAsync();
    }

    private async void OnDeleteChatClicked(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedChatAsync();
    }

    private async void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        await RenameSelectedGroupAsync();
    }

    private async void OnSetGroupIconClicked(object sender, RoutedEventArgs e)
    {
        await SetSelectedGroupIconAsync();
    }

    private async void OnClearGroupIconClicked(object sender, RoutedEventArgs e)
    {
        await ClearSelectedGroupIconAsync();
    }

    private async void OnAddParticipantClicked(object sender, RoutedEventArgs e)
    {
        await AddParticipantToSelectedGroupAsync();
    }

    private async void OnRemoveParticipantClicked(object sender, RoutedEventArgs e)
    {
        await RemoveParticipantFromSelectedGroupAsync();
    }

    private async void OnLeaveGroupClicked(object sender, RoutedEventArgs e)
    {
        await LeaveSelectedGroupAsync();
    }

    private Task SendCurrentMessageAsync()
    {
        var request = TryCapturePendingSendRequest();
        if (request is null)
        {
            return Task.CompletedTask;
        }

        if (!_queuedSendKeys.Add(request.IdempotencyKey))
        {
            SetStatus("That message is already queued.");
            return Task.CompletedTask;
        }

        PrioritizePendingSendRequest(request);
        ApplyOptimisticQueuedSendState(request);
        _ = ProcessQueuedSendRequestAsync(request);
        return Task.CompletedTask;
    }

    private void PrioritizePendingSendRequest(PendingSendRequest request)
    {
        var canceledWork = false;
        if (_cacheSyncCancellation is not null)
        {
            _cacheSyncCancellation.Cancel();
            canceledWork = true;
            _log.Info("Cache sync canceled to prioritize outbound send.");
        }

        if (_chatSearchCancellation is not null)
        {
            _chatSearchCancellation.Cancel();
            canceledWork = true;
            _log.Info("Chat search canceled to prioritize outbound send.");
        }

        if (_historyLoadCancellation is not null)
        {
            _historyLoadCancellation.Cancel();
            canceledWork = true;
            _log.Info("Conversation history load canceled to prioritize outbound send.");
        }

        if (canceledWork)
        {
            SetStatus("Prioritizing send...");
        }
    }

    private async Task ProcessQueuedSendRequestAsync(PendingSendRequest request)
    {
        try
        {
            if (_sendQueueGate.CurrentCount == 0)
            {
                SetStatus("Queued...");
            }

            await _sendQueueGate.WaitAsync();
            try
            {
                await ProcessPendingSendRequestAsync(request);
            }
            finally
            {
                _sendQueueGate.Release();
            }
        }
        finally
        {
            _queuedSendKeys.Remove(request.IdempotencyKey);
        }
    }

    private void ApplyOptimisticQueuedSendState(PendingSendRequest request)
    {
        if (request.Chat is null)
        {
            return;
        }

        if (request.PendingMessage is not null)
        {
            AddQueuedPendingMessage(request.Chat, request.PendingMessage);
            AppendQueuedPendingMessagesForChat(request.Chat);
        }

        _chatListState.StampQueuedSend(request.Chat.StableIds, request.Text, request.AttachmentPaths, DateTimeOffset.UtcNow);

        _ = ReplaceChatsAsync(_shell.Conversations.AllChats.SelectMany(static item => item.Chats));
    }

    private PendingSendRequest? TryCapturePendingSendRequest()
    {
        if (_shell.Conversation.IsNewMessageDraft)
        {
            return TryCaptureNewMessageDraftSendRequest();
        }

        var attachmentPaths = ComposeBar.PendingAttachmentPaths;
        if (_selectedChat is null || (string.IsNullOrWhiteSpace(ComposeBar.Text) && attachmentPaths.Count == 0))
        {
            return null;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Send blocked because the bridge is not connected.");
            return null;
        }

        var chat = _selectedChat;
        var text = ComposeBar.Text.Trim();
        if (attachmentPaths.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(_pendingReplyToGuid) ||
                !string.IsNullOrWhiteSpace(_pendingSendEffect) ||
                _pendingTextFormatting.Count > 0)
            {
                SetStatus("Rich compose options currently apply to text sends only. Remove attachments or cancel reply/effect/formatting before sending.");
                return null;
            }

            ComposeBar.Text = string.Empty;
            ComposeBar.ClearPendingAttachments();
            return PendingSendRequest.FromAttachment(chat, attachmentPaths, text);
        }

        if (text.Length > 0)
        {
            var effect = _pendingSendEffect;
            var effectLabel = _pendingSendEffectLabel;
            var replyTo = _pendingReplyToGuid;
            var replySummary = _pendingReplySummary;
            var textFormatting = _pendingTextFormatting.ToList();
            ClearPendingSendEffect();
            ClearPendingReplyTarget();
            ClearPendingTextFormatting();
            ComposeBar.Text = string.Empty;
            return PendingSendRequest.FromText(chat, text, effect, effectLabel, replyTo, replySummary, textFormatting);
        }

        return null;
    }

    private PendingSendRequest? TryCaptureNewMessageDraftSendRequest()
    {
        var recipients = ParseDraftRecipients(_shell.Conversation.DraftRecipientText);
        var text = ComposeBar.Text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (recipients.Count == 0)
        {
            SetStatus("Add a recipient before sending.");
            DraftRecipientsInputBox.Focus(FocusState.Programmatic);
            return null;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Draft send blocked because the bridge is not connected.");
            return null;
        }

        ComposeBar.Text = string.Empty;
        return PendingSendRequest.FromNewDraft(recipients, text);
    }

    private async Task ProcessPendingSendRequestAsync(PendingSendRequest request)
    {
        await StopTypingIndicatorAsync();
        switch (request.Kind)
        {
            case PendingSendRequestKind.NewDraft:
                await SendNewMessageDraftAsync(request);
                break;
            case PendingSendRequestKind.Attachment:
                await SendAttachmentFilesAsync(request.Chat!, request.AttachmentPaths, request.Text);
                break;
            case PendingSendRequestKind.Text:
                if (!string.IsNullOrWhiteSpace(request.Effect) ||
                    !string.IsNullOrWhiteSpace(request.ReplyTo) ||
                    request.TextFormatting.Count > 0)
                {
                    await SendRichTextToChatAsync(
                        request.Chat!,
                        request.Text,
                        request.Effect,
                        request.EffectLabel,
                        request.ReplyTo,
                        request.ReplySummary,
                        request.TextFormatting);
                    return;
                }

                await SendTextToChatAsync(request.Chat!, request.Text, request.PendingMessage);
                break;
        }
    }

    private void ClearPendingSendEffect()
    {
        _pendingSendEffect = null;
        _pendingSendEffectLabel = null;
    }

    private void ClearPendingReplyTarget()
    {
        _pendingReplyToGuid = null;
        _pendingReplySummary = null;
        ComposeBar.SetReplyTarget(null);
    }

    private void ClearPendingTextFormatting()
    {
        _pendingTextFormatting.Clear();
    }

    private async Task SendTextToChatAsync(ChatListItem chat, string text, ImsgMessage? pendingMessage = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Send blocked because the bridge is not connected.");
            return;
        }

        SetStatus("Sending...");
        _log.Info($"Send started. chatIdPresent={chat.Chat.Id.HasValue}; identifierPresent={!string.IsNullOrWhiteSpace(chat.Chat.Identifier)}; service={chat.Chat.Service ?? "unknown"}; textLength={text.Length}");

        var outcome = await _messageSendWorkflowService.SendTextAsync(
            _shell.Conversation,
            chat,
            text,
            pollSendStatus: false,
            requestTimeout: _settings.RequestTimeout,
            isSelectedChat: IsSelectedChat,
            refreshHistoryAsync: RefreshHistoryAsync,
            selectedConversationChanged: () => ScrollMessagesToLatest(forceLatest: true),
            pendingMessage: pendingMessage,
            refreshAfterSend: false);

        if (pendingMessage is not null)
        {
            if (IsSelectedChat(chat) || outcome.IsFailed)
            {
                RemoveQueuedPendingMessage(chat, pendingMessage);
            }
            else
            {
                // Stamp the sent GUID onto the kept overlay so later observed
                // checks match by identity instead of text (identical rapid
                // sends must not cross-match).
                PromoteQueuedPendingMessageGuid(chat, pendingMessage, outcome.SentGuid);
                _log.Info("Keeping off-screen pending send overlay until selected history observes the sent message.");
            }
        }

        if (outcome.IsFailed)
        {
            _log.Error($"Send failed. chatIdPresent={chat.Chat.Id.HasValue}; identifierPresent={!string.IsNullOrWhiteSpace(chat.Chat.Identifier)}; service={chat.Chat.Service ?? "unknown"}; error={outcome.ErrorMessage}");
            return;
        }

        _log.Info($"Send RPC completed. service={outcome.OutgoingService}; targetGuidPresent={!string.IsNullOrWhiteSpace(chat.Chat.Guid)}; guidReturned={!string.IsNullOrWhiteSpace(outcome.SentGuid)}; unconfirmed={outcome.IsUnconfirmed}");
        TrackTransientLatestMessage(chat, text);
        QueuePostSendHistoryRefresh(chat);
        if (outcome.SendStatusError is not null)
        {
            _log.Warning($"Send-status polling failed after a successful send. {outcome.SendStatusError.GetType().Name}: {outcome.SendStatusError.Message}");
        }

        ApplyCapabilityState();
        if (!string.IsNullOrWhiteSpace(outcome.StatusMessage))
        {
            SetStatus(outcome.StatusMessage);
            return;
        }

        SetStatus(_bridge.IsConnected ? "Connected" : "Ready");
    }

    private async Task SendRichTextToChatAsync(
        ChatListItem chat,
        string text,
        string? effect,
        string? effectLabel,
        string? replyTo,
        string? replySummary,
        IReadOnlyList<RichTextFormattingRange> textFormatting)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Rich send blocked because the bridge is not connected.");
            return;
        }

        if (!CapabilityActionPolicy.CanSendRich(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Send effects require imsg advanced bridge support. Run setup checklist and verify send.rich is advertised after imsg launch.");
            return;
        }

        var hasFormatting = textFormatting.Count > 0;
        var pendingItem = _shell.Conversation.AddPendingMessage(CreatePendingRichMessage(
            chat,
            text,
            effect,
            effectLabel,
            replyTo,
            replySummary,
            textFormatting));
        ScrollMessagesToLatest(forceLatest: true);
        SetStatus(BuildRichSendBusyStatus(effectLabel, replyTo, hasFormatting));
        _log.Info($"Rich send started. chatIdPresent={chat.Chat.Id.HasValue}; chatGuidPresent={!string.IsNullOrWhiteSpace(chat.Chat.Guid)}; effect={effect ?? "none"}; replyToPresent={!string.IsNullOrWhiteSpace(replyTo)}; formattingRanges={textFormatting.Count}; textLength={text.Length}");

        using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
        try
        {
            var result = await _messageSendService.SendRichAsync(
                ConversationTarget.FromChat(chat.Chat, chat.DisplayName),
                text,
                effect,
                replyTo,
                textFormatting,
                timeout.Token);

            if (IsSelectedChat(chat))
            {
                _shell.Conversation.MarkPendingMessageSent(
                    pendingItem,
                    result.MessageGuid,
                    MessageSendWorkflowService.SentSyncingDeliveryStatus);
            }

            await RefreshHistoryAsync(chat, timeout.Token);
            if (IsSelectedChat(chat) && !string.IsNullOrWhiteSpace(result.MessageGuid))
            {
                _shell.Conversation.AddPostSendPlaceholder(
                    pendingItem,
                    result.MessageGuid,
                    MessageSendWorkflowService.SentSyncingDeliveryStatus,
                    isFailed: false);
            }

            SetStatus(BuildRichSendCompletedStatus(effectLabel, replyTo, hasFormatting));
            _log.Info($"Rich send completed. guidReturned={!string.IsNullOrWhiteSpace(result.MessageGuid)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            var sendError = MessageSendService.BuildSendFailureMessage(ex, _settings.RequestTimeout);
            if (IsSelectedChat(chat))
            {
                _shell.Conversation.MarkPendingMessageFailed(pendingItem, sendError);
            }

            SetStatus(sendError);
            _log.Error($"Rich send failed. effect={effect ?? "none"}; replyToPresent={!string.IsNullOrWhiteSpace(replyTo)}; formattingRanges={textFormatting.Count}; error={sendError}");
        }
        finally
        {
            ApplyCapabilityState();
        }
    }

    private static string BuildRichSendBusyStatus(string? effectLabel, string? replyTo, bool hasFormatting)
    {
        if (!string.IsNullOrWhiteSpace(effectLabel) && !string.IsNullOrWhiteSpace(replyTo))
        {
            return $"Replying with {effectLabel} effect...";
        }

        if (!string.IsNullOrWhiteSpace(effectLabel))
        {
            return $"Sending with {effectLabel} effect...";
        }

        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            return hasFormatting ? "Sending formatted reply..." : "Sending reply...";
        }

        return "Sending formatted message...";
    }

    private static string BuildRichSendCompletedStatus(string? effectLabel, string? replyTo, bool hasFormatting)
    {
        if (!string.IsNullOrWhiteSpace(effectLabel) && !string.IsNullOrWhiteSpace(replyTo))
        {
            return $"Sent reply with {effectLabel} effect from Mac; syncing local history.";
        }

        if (!string.IsNullOrWhiteSpace(effectLabel))
        {
            return $"Sent from Mac with {effectLabel} effect; syncing local history.";
        }

        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            return hasFormatting
                ? "Sent formatted reply from Mac; syncing local history."
                : "Sent reply from Mac; syncing local history.";
        }

        return "Sent formatted message from Mac; syncing local history.";
    }

    private async void OnAttachClicked(object sender, RoutedEventArgs e)
    {
        if (!CanQueueComposeAttachments(ComposeAttachmentFileSource.Picked))
        {
            return;
        }

        var filePaths = await _filePicker.PickFilesAsync();
        if (filePaths.Count == 0)
        {
            return;
        }

        QueueComposeAttachments(filePaths, ComposeAttachmentFileSource.Picked);
    }

    private void OnComposeAttachmentFilesRequested(object? sender, ComposeAttachmentFilesRequestedEventArgs e)
    {
        QueueComposeAttachments(e.FilePaths, e.Source);
    }

    private async Task<bool> TryQueueClipboardAttachmentFilesAsync()
    {
        DataPackageView dataView;
        try
        {
            dataView = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            _log.Error("Clipboard read failed while checking pasted attachments.", ex);
            SetStatus("Unable to read files from the Clipboard.");
            return true;
        }

        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return false;
        }

        IReadOnlyList<string> filePaths;
        try
        {
            filePaths = await WinIMsg.App.Controls.ComposeBar.ReadStorageItemFilePathsAsync(dataView);
        }
        catch (Exception ex)
        {
            _log.Error("Clipboard file extraction failed while checking pasted attachments.", ex);
            SetStatus("Unable to attach files from the Clipboard.");
            return true;
        }

        if (filePaths.Count == 0)
        {
            return false;
        }

        QueueComposeAttachments(filePaths, ComposeAttachmentFileSource.Pasted);
        return true;
    }

    private bool QueueComposeAttachments(
        IReadOnlyList<string> filePaths,
        ComposeAttachmentFileSource source)
    {
        var normalized = WinIMsg.App.Controls.ComposeBar.NormalizeAttachmentFilePaths(filePaths);
        if (normalized.Count == 0)
        {
            return false;
        }

        if (!CanQueueComposeAttachments(source))
        {
            return false;
        }

        var addedCount = 0;
        string? firstAddedPath = null;
        foreach (var filePath in normalized)
        {
            if (ComposeBar.AddPendingAttachment(filePath))
            {
                addedCount++;
                firstAddedPath ??= filePath;
            }
        }

        ComposeBar.FocusComposer();
        SetStatus(BuildComposeAttachmentQueuedStatus(source, addedCount, firstAddedPath));
        _log.Info($"Compose attachments queued. source={source}; requested={normalized.Count}; added={addedCount}; duplicate={normalized.Count - addedCount}");
        return addedCount > 0;
    }

    private bool CanQueueComposeAttachments(ComposeAttachmentFileSource source)
    {
        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning($"Attachment {source.ToString().ToLowerInvariant()} blocked because the bridge is not connected.");
            return false;
        }

        if (_selectedChat is null)
        {
            SetStatus(_shell.Conversation.IsNewMessageDraft
                ? "Attachment send for a new draft needs a created chat first. Select an existing chat before attaching files."
                : "Select a conversation before attaching files.");
            return false;
        }

        if (!CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Attachment send is unavailable for this connection. Open Settings > Diagnostics for capability details.");
            return false;
        }

        return true;
    }

    private static string BuildComposeAttachmentQueuedStatus(
        ComposeAttachmentFileSource source,
        int addedCount,
        string? firstAddedPath)
    {
        var duplicateStatus = source switch
        {
            ComposeAttachmentFileSource.Pasted => "Pasted attachments were already queued.",
            ComposeAttachmentFileSource.Dropped => "Dropped attachments were already queued.",
            _ => "Selected attachments were already queued."
        };

        return addedCount switch
        {
            0 => duplicateStatus,
            1 => $"{Path.GetFileName(firstAddedPath)} attached.",
            _ => $"{addedCount} attachments attached."
        };
    }

    private void SaveActiveComposeDraft()
    {
        if (_restoringComposeDraft)
        {
            return;
        }

        var key = CurrentComposeDraftKey();

        var state = new ComposeDraftState(
            Text: ComposeBar.Text,
            AttachmentPaths: ComposeBar.PendingAttachmentPaths,
            DraftRecipientText: string.Equals(_composeDraftStore.ActiveKey ?? key, ChatListItem.NewMessageDraftStableId, StringComparison.OrdinalIgnoreCase)
                ? _shell.Conversation.DraftRecipientText
                : null);

        _composeDraftStore.SaveActive(key, state);
    }

    private void RestoreComposeDraft(string key, string? fallbackDraftRecipientText = null)
    {
        if (_voiceRecording.IsRecording)
        {
            _ = StopVoiceRecordingAsync(discard: true);
        }

        _restoringComposeDraft = true;
        try
        {
            ClearPendingSendEffect();
            ClearPendingReplyTarget();
            ClearPendingTextFormatting();
            var state = _composeDraftStore.Activate(key, fallbackDraftRecipientText);

            ComposeBar.Text = state.Text;
            ComposeBar.SetPendingAttachments(state.AttachmentPaths);
            if (string.Equals(key, ChatListItem.NewMessageDraftStableId, StringComparison.OrdinalIgnoreCase))
            {
                _shell.Conversation.DraftRecipientText = state.DraftRecipientText ?? string.Empty;
            }
        }
        finally
        {
            _restoringComposeDraft = false;
        }
    }

    private void ClearComposeDraft(string key)
    {
        var wasActive = _composeDraftStore.IsActive(key);
        _composeDraftStore.Clear(key);
        if (wasActive)
        {
            _restoringComposeDraft = true;
            try
            {
                ComposeBar.Text = string.Empty;
                ComposeBar.ClearPendingAttachments();
            }
            finally
            {
                _restoringComposeDraft = false;
            }
        }
    }

    private string? CurrentComposeDraftKey()
    {
        if (_shell.Conversation.IsNewMessageDraft)
        {
            return ChatListItem.NewMessageDraftStableId;
        }

        return _selectedChat?.StableId;
    }

    private async Task<bool> SendAttachmentFilesAsync(ChatListItem chat, IReadOnlyList<string> filePaths, string? text = null)
    {
        AttachmentBatchSendResult? result = null;
        var pendingItem = _shell.Conversation.AddPendingMessage(CreatePendingAttachmentMessage(chat, filePaths, text));
        ScrollMessagesToLatest(forceLatest: true);
        var status = filePaths.Count == 1
            ? (string.IsNullOrWhiteSpace(text) ? "Sending attachment..." : "Sending message with attachment...")
            : $"Sending {filePaths.Count} attachments...";
        await RunUiActionAsync(status, async token =>
        {
            result = await _attachmentSendWorkflowService.SendAsync(
                _settings,
                chat,
                filePaths,
                text,
                supportsAdvancedAttachmentFallback: _capabilities.Supports("send.attachment"),
                new Progress<AttachmentSendProgress>(progress => SetStatus(progress.StatusText)),
                token);

            foreach (var failedFile in result.Files.Where(file => !file.Sent))
            {
                _log.Warning($"Attachment send failed. file={failedFile.DisplayName}; stage={failedFile.FailureStage}; error={failedFile.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(result.TextFallbackError))
            {
                _log.Error($"Attachment text fallback failed. error={result.TextFallbackError}");
            }

            if (result.TextSentWithoutAttachment)
            {
                _log.Info("Attachment transfer failed before remote send; sent text without the attachment.");
            }

            if (IsSelectedChat(chat))
            {
                if (result.HasFailures)
                {
                    _shell.Conversation.MarkPendingMessageFailed(pendingItem, result.StatusMessage);
                }
                else if (result.HasUnconfirmedRemoteSends)
                {
                    _shell.Conversation.MarkPendingMessageSent(
                        pendingItem,
                        sentGuid: null,
                        syncingStatus: result.StatusMessage);
                }
                else
                {
                    _shell.Conversation.MarkPendingMessageSent(
                        pendingItem,
                        sentGuid: null,
                        syncingStatus: "Sent from Mac; syncing local history.");
                }
            }

            if (result.AnyRemoteChangeLikely)
            {
                await RefreshHistoryAsync(chat, token);
            }

            if (result.HasFailures && IsSelectedChat(chat))
            {
                _shell.Conversation.AddPostSendPlaceholder(
                    pendingItem,
                    pendingItem.Message.Guid,
                    result.StatusMessage,
                    isFailed: true);
            }
        }, CompositeBridgeTimeout(filePaths.Count * 2 + (string.IsNullOrWhiteSpace(text) ? 0 : 1)));

        if (result is null && IsSelectedChat(chat))
        {
            _shell.Conversation.MarkPendingMessageFailed(
                pendingItem,
                "Attachment send timed out or was canceled before a result was available.");
        }

        if (result is not null)
        {
            SetStatus(result.StatusMessage);
        }

        return result?.AnyRemoteChangeLikely == true;
    }

    private static ImsgMessage CreatePendingAttachmentMessage(ChatListItem chat, IReadOnlyList<string> filePaths, string? text)
    {
        return new ImsgMessage
        {
            Guid = $"pending:{Guid.NewGuid():N}",
            ChatId = chat.Chat.Id,
            ChatIdentifier = chat.Chat.Identifier,
            ChatGuid = chat.Chat.Guid,
            ChatName = chat.DisplayName,
            Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            Service = chat.Chat.Service,
            IsFromMe = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            PendingSendRetry = PendingSendRetryInfo.Attachments(
                string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                filePaths),
            Attachments = filePaths
                .Select(static filePath => new ImsgAttachment
                {
                    Filename = Path.GetFileName(filePath),
                    OriginalPath = filePath,
                    Path = filePath,
                    ByteSize = File.Exists(filePath) ? new FileInfo(filePath).Length : null
                })
                .ToList()
        };
    }

    private static ImsgMessage CreatePendingRichMessage(
        ChatListItem chat,
        string text,
        string? effect,
        string? effectLabel,
        string? replyTo,
        string? replySummary,
        IReadOnlyList<RichTextFormattingRange> textFormatting)
    {
        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(replySummary))
        {
            metadata.Add($"Replying to: {replySummary}");
        }

        if (!string.IsNullOrWhiteSpace(effectLabel))
        {
            metadata.Add($"Effect: {effectLabel}");
        }

        if (textFormatting.Count > 0)
        {
            metadata.Add("Formatted text");
        }

        return new ImsgMessage
        {
            Guid = $"pending:rich:{Guid.NewGuid():N}",
            ChatId = chat.Chat.Id,
            ChatIdentifier = chat.Chat.Identifier,
            ChatGuid = chat.Chat.Guid,
            ChatName = chat.DisplayName,
            Text = metadata.Count == 0
                ? text
                : $"{text}{Environment.NewLine}[{string.Join("; ", metadata)}]",
            Service = chat.Chat.Service,
            IsFromMe = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            PendingSendRetry = PendingSendRetryInfo.Rich(text, effect, effectLabel, replyTo, replySummary, textFormatting)
        };
    }

    private async Task SendPollForSelectedChatAsync()
    {
        var chat = _selectedChat;
        if (chat is null)
        {
            SetStatus("Select a conversation before sending a poll.");
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Poll send blocked because the bridge is not connected.");
            return;
        }

        if (!CapabilityActionPolicy.CanSendPoll(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Polls require imsg advanced bridge support. Run setup checklist and verify poll.send is advertised after imsg launch.");
            return;
        }

        var request = await _dialogs.PromptPollAsync();
        if (request is null)
        {
            return;
        }

        await SendPollToChatAsync(chat, request);
    }

    private async Task SendPollToChatAsync(ChatListItem chat, PollComposeRequest request, string? replyTo = null)
    {
        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Poll send blocked because the bridge is not connected.");
            return;
        }

        if (!CapabilityActionPolicy.CanSendPoll(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Polls require imsg advanced bridge support. Run setup checklist and verify poll.send is advertised after imsg launch.");
            return;
        }

        var pendingItem = _shell.Conversation.AddPendingMessage(CreatePendingPollMessage(chat, request, replyTo));
        ScrollMessagesToLatest(forceLatest: true);
        SetStatus("Sending poll...");
        _log.Info($"Poll send started. chatIdPresent={chat.Chat.Id.HasValue}; chatGuidPresent={!string.IsNullOrWhiteSpace(chat.Chat.Guid)}; optionCount={request.Options.Count}");

        using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
        try
        {
            var result = await _messageSendService.SendPollAsync(
                ConversationTarget.FromChat(chat.Chat, chat.DisplayName),
                request.Question,
                request.Options,
                replyTo,
                timeout.Token);

            if (IsSelectedChat(chat))
            {
                _shell.Conversation.MarkPendingMessageSent(
                    pendingItem,
                    result.MessageGuid,
                    MessageSendWorkflowService.SentSyncingDeliveryStatus);
            }

            await RefreshHistoryAsync(chat, timeout.Token);
            if (IsSelectedChat(chat) && !string.IsNullOrWhiteSpace(result.MessageGuid))
            {
                _shell.Conversation.AddPostSendPlaceholder(
                    pendingItem,
                    result.MessageGuid,
                    MessageSendWorkflowService.SentSyncingDeliveryStatus,
                    isFailed: false);
            }

            SetStatus("Poll sent from Mac; syncing local history.");
            _log.Info($"Poll send completed. guidReturned={!string.IsNullOrWhiteSpace(result.MessageGuid)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            var sendError = MessageSendService.BuildSendFailureMessage(ex, _settings.RequestTimeout);
            if (IsSelectedChat(chat))
            {
                _shell.Conversation.MarkPendingMessageFailed(pendingItem, sendError);
            }

            SetStatus(sendError);
            _log.Error($"Poll send failed. error={sendError}");
        }
        finally
        {
            ApplyCapabilityState();
        }
    }

    private static ImsgMessage CreatePendingPollMessage(ChatListItem chat, PollComposeRequest request, string? replyTo)
    {
        return new ImsgMessage
        {
            Guid = $"pending:poll:{Guid.NewGuid():N}",
            ChatId = chat.Chat.Id,
            ChatIdentifier = chat.Chat.Identifier,
            ChatGuid = chat.Chat.Guid,
            ChatName = chat.DisplayName,
            Text = $"Poll: {request.Question}{Environment.NewLine}{string.Join(Environment.NewLine, request.Options.Select(static option => $"- {option}"))}",
            Service = chat.Chat.Service,
            IsFromMe = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            PendingSendRetry = PendingSendRetryInfo.Poll(request.Question, request.Options, replyTo)
        };
    }

    private async void OnTapbackMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await SendTapbackAsync(message);
        }
    }

    private async void OnEditMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await EditMessageAsync(message);
        }
    }

    private async void OnUnsendMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await UnsendMessageAsync(message);
        }
    }

    private async void OnDeleteMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await DeleteMessageAsync(message);
        }
    }

    private async void OnNotifyAnywaysMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await NotifyAnywaysAsync(message);
        }
    }

    private async void OnOpenAttachmentMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await OpenAttachmentAsync(message);
        }
    }

    private async void OnDownloadAttachmentMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await DownloadAttachmentAsync(message);
        }
    }

    private async void OnRevealAttachmentMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await RevealAttachmentAsync(message);
        }
    }

    private async void OnRetryAttachmentDownloadMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MenuMessage(sender) is { } message)
        {
            await RetryAttachmentDownloadAsync(message);
        }
    }

    private async void OnMessageTapbackRequested(object? sender, TapbackRequestedEventArgs args)
    {
        await SendTapbackAsync(args.Message, args.Choice);
    }

    private void OnMessageReplyRequested(object? sender, MessageListItem message)
    {
        BeginReplyToMessage(message);
    }

    private async void OnMessageEditRequested(object? sender, MessageListItem message)
    {
        await EditMessageAsync(message);
    }

    private async void OnMessageUnsendRequested(object? sender, MessageListItem message)
    {
        await UnsendMessageAsync(message);
    }

    private async void OnMessageDeleteRequested(object? sender, MessageListItem message)
    {
        await DeleteMessageAsync(message);
    }

    private async void OnMessageNotifyAnywaysRequested(object? sender, MessageListItem message)
    {
        await NotifyAnywaysAsync(message);
    }

    private async void OnMessageRetrySendRequested(object? sender, MessageListItem message)
    {
        await RetryFailedSendAsync(message);
    }

    private async void OnMessageOpenAttachmentRequested(object? sender, MessageListItem message)
    {
        await OpenAttachmentAsync(message);
    }

    private async void OnMessageDownloadAttachmentRequested(object? sender, MessageListItem message)
    {
        await DownloadAttachmentAsync(message);
    }

    private async void OnMessageRevealAttachmentRequested(object? sender, MessageListItem message)
    {
        await RevealAttachmentAsync(message);
    }

    private async void OnMessageRetryAttachmentDownloadRequested(object? sender, MessageListItem message)
    {
        await RetryAttachmentDownloadAsync(message);
    }

    private async Task RetryFailedSendAsync(MessageListItem message)
    {
        if (!message.CanRetrySend)
        {
            return;
        }

        var chat = _selectedChat;
        if (chat is null || !chat.ContainsStableId(message.Message.ChatStableId))
        {
            SetStatus("Select the original conversation before retrying this message.");
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetConnectionStatus(ConnectionState.Failed, "Not connected. Reconnect the active Mac profile and retry.");
            _log.Warning("Retry send blocked because the bridge is not connected.");
            return;
        }

        var retry = message.Message.PendingSendRetry;
        if (retry is { Kind: PendingSendRetryInfo.AttachmentKind })
        {
            if (!CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: true, connected: true, capabilities: _capabilities))
            {
                SetStatus("Attachment retries require baseline imsg send support. Run setup checklist and verify send is advertised after imsg launch.");
                return;
            }

            var retryAttachmentPaths = retry.AttachmentPaths?
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            var retryText = retry.Text?.Trim() ?? string.Empty;
            if (retryAttachmentPaths.Count == 0)
            {
                SetStatus("Attachment retry does not have any local file paths left to send.");
                return;
            }

            _shell.Conversation.RemoveTransientMessage(message);
            await SendAttachmentFilesAsync(chat, retryAttachmentPaths, retryText);
            return;
        }

        if (retry is { Kind: PendingSendRetryInfo.RichKind })
        {
            if (!CapabilityActionPolicy.CanSendRich(hasSelectedChat: true, connected: true, capabilities: _capabilities))
            {
                SetStatus("Rich retries require imsg advanced bridge support. Run setup checklist and verify send.rich is advertised after imsg launch.");
                return;
            }

            var richText = retry.Text?.Trim() ?? string.Empty;
            if (richText.Length == 0)
            {
                return;
            }

            _shell.Conversation.RemoveTransientMessage(message);
            await SendRichTextToChatAsync(
                chat,
                richText,
                retry.Effect,
                retry.EffectLabel,
                retry.ReplyTo,
                retry.ReplySummary,
                retry.TextFormatting ?? []);
            return;
        }

        if (retry is { Kind: PendingSendRetryInfo.PollKind })
        {
            if (!CapabilityActionPolicy.CanSendPoll(hasSelectedChat: true, connected: true, capabilities: _capabilities))
            {
                SetStatus("Poll retries require imsg advanced bridge support. Run setup checklist and verify poll.send is advertised after imsg launch.");
                return;
            }

            var question = retry.PollQuestion?.Trim() ?? string.Empty;
            var options = retry.PollOptions?.Where(static option => !string.IsNullOrWhiteSpace(option)).ToList() ?? [];
            if (string.IsNullOrWhiteSpace(question) || options.Count < 2)
            {
                return;
            }

            _shell.Conversation.RemoveTransientMessage(message);
            await SendPollToChatAsync(chat, new PollComposeRequest(question, options), retry.ReplyTo);
            return;
        }

        var text = message.Text.Trim();
        var attachmentPaths = PendingLocalAttachmentPaths(message);
        if (text.Length == 0 && attachmentPaths.Count == 0)
        {
            return;
        }

        _shell.Conversation.RemoveTransientMessage(message);
        if (attachmentPaths.Count > 0)
        {
            await SendAttachmentFilesAsync(chat, attachmentPaths, text);
            return;
        }

        await SendTextToChatAsync(chat, text);
    }

    private void BeginReplyToMessage(MessageListItem message)
    {
        var chat = _selectedChat;
        if (chat is null || !chat.ContainsStableId(message.Message.ChatStableId))
        {
            SetStatus("Select the original conversation before replying to this message.");
            return;
        }

        if (!CapabilityActionPolicy.CanReply(message.Message, _capabilities))
        {
            SetStatus("Replies require imsg advanced bridge support and send.rich.");
            return;
        }

        var messageGuid = message.Message.MessageActionId;
        if (string.IsNullOrWhiteSpace(messageGuid))
        {
            SetStatus("This message does not expose a reply target GUID.");
            return;
        }

        _pendingReplyToGuid = messageGuid;
        _pendingReplySummary = BuildReplySummary(message);
        ComposeBar.SetReplyTarget(_pendingReplySummary);
        ComposeBar.FocusComposer();
        SetStatus("Reply target selected.");
    }

    private static string BuildReplySummary(MessageListItem message)
    {
        var sender = DisplayTextFormatter.SingleLine(message.Message.DisplaySender, string.Empty, maxLength: 32);
        var body = DisplayTextFormatter.SingleLine(message.Text, string.Empty, maxLength: 80);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = DisplayTextFormatter.SingleLine(message.AttachmentSummary, "message", maxLength: 80);
        }

        return string.IsNullOrWhiteSpace(sender) ? body : $"{sender}: {body}";
    }

    private static IReadOnlyList<string> PendingLocalAttachmentPaths(MessageListItem message)
    {
        if (message.Message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) != true)
        {
            return [];
        }

        return message.Message.Attachments
            .Select(FirstExistingLocalAttachmentPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void OnMessageAttachmentOpenRequested(object? sender, MessageAttachmentListItem attachment)
    {
        await PreviewAttachmentAsync(attachment);
    }

    private void OnMessageLinkOpenRequested(object? sender, MessageLinkPreviewItem linkPreview)
    {
        Process.Start(new ProcessStartInfo(linkPreview.Uri.ToString()) { UseShellExecute = true });
    }

    private async Task<bool> ConnectAsync(bool saveSettings = true, CancellationToken cancellationToken = default)
    {
        if (_bridgeReconnectCancellation is { IsCancellationRequested: false } pendingReconnect &&
            !cancellationToken.Equals(pendingReconnect.Token))
        {
            pendingReconnect.Cancel();
        }

        if (!_connectGate.Wait(0))
        {
            _log.Info("Connect request ignored because another connection attempt is already in progress.");
            return false;
        }

        var connected = false;
        try
        {
            if (saveSettings)
            {
                SaveCurrentSettingsContext();
            }

            if (!_settings.IsConfigured)
            {
                SetConnectionStatus(ConnectionState.Failed, "Enter at least one Mac target address before connecting.");
                return false;
            }

            var syncAfterConnect = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CompositeBridgeTimeout(operationCount: 5));
            try
            {
                var watchCursorScope = BuildWatchCursorScope(_appSettings.ActiveProfileId);
                var watchCursor = await _messageCache.GetWatchCursorAsync(watchCursorScope, timeout.Token);
                // Stamp the subscription window at connect start so backlog
                // events replayed while the connection is still completing are
                // date-filtered instead of counted as new unread.
                _bridgeNotificationWorkflowService.ResetWatchState(
                    watchCursorScope,
                    watchCursor,
                    subscriptionStartedAtUtc: DateTimeOffset.UtcNow);

                _log.Info("Connection probe started.");
                var progress = new Progress<BridgeConnectionProgress>(ApplyConnectionProgress);
                var result = await _connectionService.ConnectAsync(
                    _settings,
                    _appSettings.ActiveProfile.Name,
                    progress,
                    beforeConnectAsync: PrimeContactsBeforeRpcAsync,
                    cancellationToken: timeout.Token,
                    watchSinceRowId: watchCursor);

                if (!result.IsSuccess)
                {
                    _capabilities = result.Capabilities;
                    _faceTimeLinkAvailable = false;
                    ApplyCapabilityState();
                    SetConnectionStatus(ConnectionState.Failed, result.Message);
                    ApplyMacIMessageHealthIssue(result.MacHealthIssue);
                    if (result.MacHealthIssue is not null)
                    {
                        _log.Warning(result.MacHealthIssue.DiagnosticDetail);
                    }
                    _log.Warning($"Connection failed. {result.Message}");
                    return false;
                }

                _capabilities = result.Capabilities;
                ApplyMacIMessageHealthIssue(null);
                ApplyCapabilityState();
                if (result.ConnectedSettings is not null)
                {
                    _settings = result.ConnectedSettings;
                }

                _userDisconnectedBridge = false;
                // Drop any stale dedicated channels so their next use dials
                // the freshly connected profile settings, then pre-dial the
                // send channel so the first send has no session-startup cost.
                await ResetBridgeChannelsAsync();
                _ = WarmUpSendChannelAsync();
                if (ShouldAttemptBridgeRepair(_capabilities))
                {
                    _ = RepairAdvancedBridgeAsync();
                }
                _log.Info($"Connected to active profile. advanced={_capabilities.HasAdvancedBridge}; watchCursor={result.WatchSinceRowId?.ToString() ?? "none"}");
                _bridgeNotificationWorkflowService.ResetWatchState(
                    watchCursorScope,
                    result.WatchSinceRowId ?? watchCursor,
                    result.SubscriptionStartedAtUtc ?? DateTimeOffset.UtcNow);
                await RefreshChatsAfterConnectAsync(timeout.Token);
                SetSettingsVisible(false);
                _ = RefreshFaceTimeLinkAvailabilityAsync();
                syncAfterConnect = _appSettings.SyncCacheInBackground;
                SetConnectionStatus(ConnectionState.Connected, result.Message);
                _log.Info("Connection completed.");
                connected = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _capabilities = new ImsgCapabilities();
                _faceTimeLinkAvailable = false;
                ApplyCapabilityState();
                var message = ex is OperationCanceledException
                    ? $"Connection timed out after {FormatDuration(CompositeBridgeTimeout(operationCount: 5))}."
                    : ex.Message;
                SetConnectionStatus(ConnectionState.Failed, message);
                _log.Error("Connection failed.", ex);
            }

            if (syncAfterConnect)
            {
                _ = SyncFullCacheAsync(showCompletion: false);
            }

            return connected;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void ApplyMacIMessageHealthIssue(MacIMessageHealthIssue? issue)
    {
        MacIMessageHealthInfoBar.IsOpen = issue is not null;
        if (issue is null)
        {
            return;
        }

        MacIMessageHealthInfoBar.Title = issue.Title;
        MacIMessageHealthInfoBar.Message = issue.UserMessage;
    }

    private async void OnMacHealthRetryClicked(object sender, RoutedEventArgs e)
    {
        MacIMessageHealthInfoBar.IsOpen = false;
        await ConnectAsync(saveSettings: false);
    }

    private void OnMacHealthDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        OnSettingsClicked(sender, e);
        ShowSettingsSection("Diagnostics");
    }

    private async Task RefreshChatsAsync(CancellationToken cancellationToken = default)
    {
        if (!_bridge.IsConnected)
        {
            await LoadCachedChatsAsync();
            return;
        }

        SetTransientSyncBar(true);
        try
        {
            var chats = await _bridge.ListChatsAsync(limit: ChatListFetchLimit, cancellationToken: cancellationToken);
            await _messageCache.UpsertChatsAsync(chats, cancellationToken);
            await ReplaceChatsAsync(await EnrichChatsWithContactNamesAsync(chats, cancellationToken), cancellationToken, reconcileTransientUnread: true);
            WarnIfChatFetchLimitReached(chats.Count, ChatListFetchLimit);
        }
        finally
        {
            SetTransientSyncBar(false);
        }
    }

    private async Task RefreshChatsAfterConnectAsync(CancellationToken cancellationToken)
    {
        var refreshTimeout = TimeSpan.FromSeconds(Math.Clamp(_settings.RequestTimeout.TotalSeconds, 15, 45));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(refreshTimeout);
        var refreshTask = RefreshChatsAsync(timeout.Token);
        var completed = await Task.WhenAny(refreshTask, Task.Delay(refreshTimeout, cancellationToken));
        if (completed == refreshTask)
        {
            await refreshTask;
            _log.Info("Initial chat refresh after connect completed.");
            return;
        }

        timeout.Cancel();
        ResetTransientSyncBar();
        SetStatus("Connected. Initial chat refresh is still finishing in the background.");
        _log.Warning($"Initial chat refresh after connect exceeded {FormatDuration(refreshTimeout)}; leaving bridge connected and clearing the transient sync bar.");
        _ = refreshTask.ContinueWith(task =>
        {
            if (task.IsFaulted && task.Exception is not null)
            {
                _log.Error("Background initial chat refresh after connect failed.", task.Exception.GetBaseException());
            }
            else if (task.IsCanceled)
            {
                _log.Info("Background initial chat refresh after connect canceled.");
            }
            else
            {
                _log.Info("Background initial chat refresh after connect completed.");
            }
        }, TaskScheduler.Default);
    }

    private void WarnIfChatFetchLimitReached(int chatCount, int limit)
    {
        if (chatCount < limit)
        {
            return;
        }

        var message = $"Loaded {chatCount:N0} chats, which reached the current fetch limit. Some older threads may still be hidden until imsg adds chat pagination.";
        _log.Warning(message);
        SetStatus(message);
    }

    private async Task SelectChatAsync(ChatListItem item)
    {
        await StopTypingIndicatorAsync();
        SaveActiveComposeDraft();
        _selectedChat = item;
        _olderCachedHistoryLoading = false;
        _olderCachedHistoryExhausted = true;
        item = MarkChatReadLocally(item);
        _ = MarkChatReadOnBridgeAsync(item, refreshChats: false);
        _shell.Conversations.SelectedChat = item;
        _shell.Conversation.SelectConversation(ConversationTarget.FromChat(item.Chat, item.DisplayName));
        RestoreComposeDraft(item.StableId);
        SetMessageLoading(true);
        ApplyCapabilityState();
        SetChatPinnedAwayFromLatest(item.StableId, false);

        _historyLoadCancellation?.Cancel();
        _historyLoadCancellation?.Dispose();
        _historyLoadCancellation = new CancellationTokenSource();
        var token = _historyLoadCancellation.Token;

        try
        {
            _foregroundHistoryLoading = true;
            await RefreshHistoryAsync(item, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"Selected chat history refresh failed. chatIdPresent={item.Chat.Id.HasValue}; service={item.Chat.Service ?? "unknown"}", ex);
            SetStatus("Unable to refresh selected chat history. Showing cached messages if available.");
        }
        finally
        {
            _foregroundHistoryLoading = false;
            if (!token.IsCancellationRequested)
            {
                SetMessageLoading(false);
            }
        }
    }

    private void SelectNewMessageDraft()
    {
        _ = StopTypingIndicatorAsync();
        SaveActiveComposeDraft();
        _historyLoadCancellation?.Cancel();
        _selectedChat = null;
        var draft = _shell.Conversations.SelectedChat?.IsNewMessageDraft == true
            ? _shell.Conversations.SelectedChat
            : _shell.Conversations.StartNewMessageDraft();
        var fallbackDraftRecipientText = string.Join(", ", draft.Chat.Participants);
        _shell.Conversations.SelectedChat = draft;
        ChatListPane.SelectedItem = draft;
        if (!string.IsNullOrWhiteSpace(ChatListPane.SearchText))
        {
            ChatListPane.SearchText = string.Empty;
        }

        _shell.Conversation.StartNewMessageDraft();
        RestoreComposeDraft(ChatListItem.NewMessageDraftStableId, fallbackDraftRecipientText);
        draft = _shell.Conversations.UpdateNewMessageDraftRecipients(_shell.Conversation.DraftRecipientText);
        _shell.Conversations.SelectedChat = draft;
        ChatListPane.SelectedItem = draft;
        SetMessageLoading(false);
        JumpToLatestButton.Visibility = Visibility.Collapsed;
        SetStatus("New message.");
        ApplyCapabilityState();
        DraftRecipientsInputBox.Focus(FocusState.Programmatic);
    }

    private async Task SendNewMessageDraftAsync(PendingSendRequest request)
    {
        var recipients = request.Recipients;
        var text = request.Text;
        var directRecipient = recipients.Count == 1 ? recipients[0] : null;
        SetStatus(recipients.Count == 1 ? "Sending..." : "Creating group...");
        _log.Info($"Draft send started. recipientCount={recipients.Count}; textLength={text.Length}");

        using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
        try
        {
            MessageSendWorkflowResult result;
            if (directRecipient is not null)
            {
                result = await _messageSendService.SendDirectTextWithStatusAsync(
                    directRecipient,
                    text,
                    "auto",
                    pollSendStatus: _capabilities.Supports("message.send_status"),
                    region: _appSettings.PhoneNumberRegion,
                    timeout.Token);
            }
            else
            {
                var createResult = await _messageSendService.CreateChatAsync(
                    recipients,
                    name: null,
                    text,
                    timeout.Token);
                result = new MessageSendWorkflowResult(createResult, null, null);
            }

            SetStatus(directRecipient is not null ? "Sent." : "Group created.");
            await RefreshChatsAfterDraftSendAsync(timeout.Token);

            var sentChat = directRecipient is not null
                ? FindSentDraftChat(result.SendResult.RawResult, directRecipient)
                : FindCreatedDraftChat(result.SendResult.RawResult, recipients);
            if (sentChat is not null)
            {
                ClearComposeDraft(ChatListItem.NewMessageDraftStableId);
                _shell.Conversations.ClearNewMessageDraft();
                var replacement = _shell.Conversations.Chats.FirstOrDefault(chat => chat.ContainsStableId(sentChat.StableId)) ?? sentChat;
                ChatListPane.SelectedItem = replacement;
                _shell.Conversations.SelectedChat = replacement;
                await SelectChatAsync(replacement);
                SetStatus(directRecipient is not null ? "Sent." : "Group created.");
            }
            else
            {
                SetStatus(recipients.Count == 1
                    ? "Sent from Mac; refresh chats if the new conversation does not appear."
                    : "Group created from Mac; refresh chats if the new conversation does not appear.");
            }

            if (result.SendStatusError is not null)
            {
                _log.Warning($"Direct send-status polling failed after send. {result.SendStatusError.GetType().Name}: {result.SendStatusError.Message}");
            }

            _log.Info($"Draft send completed. recipientCount={recipients.Count}; guidReturned={!string.IsNullOrWhiteSpace(result.SendResult.MessageGuid)}; chatGuidReturned={!string.IsNullOrWhiteSpace(MessageSendService.ReadReturnedChatGuid(result.SendResult.RawResult))}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !timeout.IsCancellationRequested)
        {
            RestoreQueuedTextIfComposeIsEmpty(text);
            var sendError = MessageSendService.BuildSendFailureMessage(ex, _settings.RequestTimeout);
            _log.Error($"Draft send failed. recipientCount={recipients.Count}; error={sendError}");
            SetStatus(sendError);
        }
        catch (OperationCanceledException)
        {
            RestoreQueuedTextIfComposeIsEmpty(text);
            var sendError = MessageSendService.BuildSendFailureMessage(new OperationCanceledException(), _settings.RequestTimeout);
            _log.Error($"Draft send timed out. recipientCount={recipients.Count}; error={sendError}");
            SetStatus(sendError);
        }
        finally
        {
            ApplyCapabilityState();
        }
    }

    private async Task RefreshChatsAfterDraftSendAsync(CancellationToken cancellationToken)
    {
        var refreshTimeout = TimeSpan.FromSeconds(Math.Clamp(_settings.RequestTimeout.TotalSeconds, 15, 45));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(refreshTimeout);
        var refreshTask = RefreshChatsAsync(timeout.Token);
        var completed = await Task.WhenAny(refreshTask, Task.Delay(refreshTimeout, cancellationToken));
        if (completed == refreshTask)
        {
            await refreshTask;
            return;
        }

        timeout.Cancel();
        ResetTransientSyncBar();
        _log.Warning($"Post-send chat refresh exceeded {FormatDuration(refreshTimeout)}; leaving send result visible and refreshing in the background.");
        _ = refreshTask.ContinueWith(task =>
        {
            if (task.IsFaulted && task.Exception is not null)
            {
                _log.Error("Background post-send chat refresh failed.", task.Exception.GetBaseException());
            }
            else if (task.IsCanceled)
            {
                _log.Info("Background post-send chat refresh canceled.");
            }
            else
            {
                _log.Info("Background post-send chat refresh completed.");
            }
        }, TaskScheduler.Default);
    }

    private void RestoreQueuedTextIfComposeIsEmpty(string text)
    {
        if (string.IsNullOrWhiteSpace(ComposeBar.Text))
        {
            ComposeBar.Text = text;
        }
    }

    private async Task RefreshHistoryAsync(ChatListItem item, CancellationToken cancellationToken)
    {
        await foreach (var result in _conversationDataService.LoadSelectedConversationAsync(
            item,
            SelectedHistoryLimit,
            LoadSelectedHistoryWithFallbackAsync,
            cancellationToken,
            cachedInitialLimit: InitialVisibleMessageWindow + OlderVisibleMessagePageSize))
        {
            if (!IsSelectedChat(item))
            {
                continue;
            }

            var shouldShowLatest = !_shell.Conversation.IsPinnedAwayFromLatest;
            var resetVisibleWindow = result.Source == ConversationLoadSource.Cache || shouldShowLatest;
            _shell.Conversation.ReplaceMessages(
                result.Messages,
                item.Chat.Service,
                resetVisibleWindow,
                HasFailedAttachmentDownload);
            AppendQueuedPendingMessagesForChat(item);
            _olderCachedHistoryExhausted = !result.MayHaveOlderHistory;

            QueueVisibleAutoDownloadAttachments();
            SetMessageLoading(result.IsLoading && result.Messages.Count == 0);

            if (result.Source == ConversationLoadSource.Remote)
            {
                MarkCacheSyncExternallySatisfied(item);
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SetStatus(IsTimeoutLikeHistoryError(result.Error)
                    ? "Live refresh timed out. Showing cached history."
                    : "Unable to refresh selected chat history. Showing cached messages if available.");
                _log.Warning($"Selected chat history refresh fell back to cache. {result.Error}");
            }

            if (shouldShowLatest)
            {
                ScrollMessagesToLatest(forceLatest: true);
            }
            else
            {
                JumpToLatestButton.Visibility = Visibility.Visible;
            }
        }

        SetMessageLoading(false);
    }

    private void AddQueuedPendingMessage(ChatListItem chat, ImsgMessage pendingMessage)
    {
        foreach (var stableId in chat.StableIds)
        {
            if (!_queuedPendingMessagesByStableId.TryGetValue(stableId, out var messages))
            {
                messages = [];
                _queuedPendingMessagesByStableId[stableId] = messages;
            }

            if (!messages.Any(message => string.Equals(message.Guid, pendingMessage.Guid, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add(pendingMessage);
            }
        }
    }

    private void RemoveQueuedPendingMessage(ChatListItem chat, ImsgMessage pendingMessage)
    {
        foreach (var stableId in chat.StableIds)
        {
            if (!_queuedPendingMessagesByStableId.TryGetValue(stableId, out var messages))
            {
                continue;
            }

            messages.RemoveAll(message => string.Equals(message.Guid, pendingMessage.Guid, StringComparison.OrdinalIgnoreCase));
            if (messages.Count == 0)
            {
                _queuedPendingMessagesByStableId.Remove(stableId);
            }
        }
    }

    private void AppendQueuedPendingMessagesForChat(ChatListItem chat)
    {
        if (!IsSelectedChat(chat))
        {
            return;
        }

        foreach (var pendingMessage in GetQueuedPendingMessagesForChat(chat))
        {
            if (HasObservedQueuedPendingMessage(pendingMessage))
            {
                RemoveQueuedPendingMessage(chat, pendingMessage);
                continue;
            }

            _shell.Conversation.AddOrGetPendingMessage(pendingMessage);
        }
    }

    private void PromoteQueuedPendingMessageGuid(ChatListItem chat, ImsgMessage pendingMessage, string? sentGuid)
    {
        if (string.IsNullOrWhiteSpace(sentGuid) || string.IsNullOrWhiteSpace(pendingMessage.Guid))
        {
            return;
        }

        foreach (var stableId in chat.StableIds)
        {
            if (!_queuedPendingMessagesByStableId.TryGetValue(stableId, out var messages))
            {
                continue;
            }

            for (var index = 0; index < messages.Count; index++)
            {
                if (string.Equals(messages[index].Guid, pendingMessage.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    messages[index] = messages[index] with { Guid = sentGuid };
                }
            }
        }
    }

    private bool HasObservedQueuedPendingMessage(ImsgMessage pendingMessage)
    {
        if (string.IsNullOrWhiteSpace(pendingMessage.Text))
        {
            return false;
        }

        // Once the overlay carries the real sent GUID, observed checks match
        // by identity only; the text path remains solely for sends whose RPC
        // never returned a GUID.
        var sentGuid = pendingMessage.Guid is { Length: > 0 } guid &&
            !guid.StartsWith("pending:", StringComparison.OrdinalIgnoreCase)
            ? guid
            : null;
        var sendStartedAt = pendingMessage.SortDate ?? DateTimeOffset.MinValue;
        return _shell.Conversation.HasObservedSentMessage(pendingMessage.Text, sentGuid, sendStartedAt);
    }

    private IReadOnlyList<ImsgMessage> GetQueuedPendingMessagesForChat(ChatListItem chat)
    {
        var messages = new List<ImsgMessage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stableId in chat.StableIds)
        {
            if (!_queuedPendingMessagesByStableId.TryGetValue(stableId, out var pendingMessages))
            {
                continue;
            }

            foreach (var message in pendingMessages)
            {
                if (!string.IsNullOrWhiteSpace(message.Guid) && seen.Add(message.Guid))
                {
                    messages.Add(message);
                }
            }
        }

        return messages;
    }

    private void QueuePostSendHistoryRefresh(ChatListItem chat)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                if (IsSelectedChat(chat))
                {
                    using var timeout = new CancellationTokenSource(SelectedHistoryFallbackTimeout);
                    await RefreshHistoryAsync(chat, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Warning($"Post-send history refresh failed. {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private async Task<ConversationRemoteHistoryResult> LoadSelectedHistoryWithFallbackAsync(
        ChatListItem item,
        int requestedLimit,
        CancellationToken cancellationToken)
    {
        if (item.Chat.Id is null)
        {
            return new ConversationRemoteHistoryResult([], 0);
        }

        var result = await _historyFetchService.FetchSelectedAsync(
            item.Chat.Id.Value,
            requestedLimit,
            new SelectedHistoryFetchOptions(
                SelectedHistoryTimeout,
                SelectedHistoryFallbackLimit,
                SelectedHistoryFallbackTimeout),
            cancellationToken);

        foreach (var warning in result.Warnings)
        {
            _log.Warning($"{warning} service={item.Chat.Service ?? "unknown"}");
        }

        return result.History;
    }

    private async Task LoadCachedChatsAsync()
    {
        var cached = await _messageCache.GetChatsAsync();
        _log.Info($"Cached chat load read {cached.Count} rows.");
        await ReplaceChatsAsync(cached);
    }

    private async Task<IReadOnlyList<ImsgChat>> EnrichChatsWithContactNamesAsync(
        IEnumerable<ImsgChat> chats,
        CancellationToken cancellationToken = default)
    {
        var chatList = chats.Select(_chatListState.ApplyTransientLatestMessageDate).ToList();
        if (chatList.Count == 0)
        {
            return chatList;
        }

        var enriched = chatList;
        if (_settings.IsConfigured)
        {
            try
            {
                var cliChats = await _chatReader.ListChatsAsync(_settings, ChatListFetchLimit, cancellationToken);
                enriched = ContactIdentityService.MergeExplicitContactNamesByChatIdentity(chatList, cliChats);
                if (enriched.Any(chat => !string.IsNullOrWhiteSpace(chat.ContactName)))
                {
                    await _messageCache.UpsertChatsAsync(enriched, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warning($"CLI contact-name enrichment failed: {ex.Message}");
            }
        }

        return enriched;
    }

    private async Task PrimeContactsBeforeRpcAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var authorization = await _macHostActions.CheckContactsAuthorizationAsync(settings, cancellationToken);
            if (!authorization.IsNotDetermined)
            {
                _log.Info($"Contacts preflight before RPC. state={authorization.State}; raw={authorization.RawValue}");
                return;
            }

            var chats = await _chatReader.ListChatsAsync(settings, limit: 50, cancellationToken);
            var probeAddress = SelectContactProbeAddress(chats);
            if (string.IsNullOrWhiteSpace(probeAddress))
            {
                _log.Warning("Contacts preflight found notDetermined authorization but no usable chat handle for imsg nickname --local.");
                return;
            }

            var note = await RequestContactsAccessWithProbeAsync(settings, probeAddress);
            var after = await _macHostActions.CheckContactsAuthorizationAsync(settings, cancellationToken);
            note = DescribeContactsRequestOutcome(note, after);
            _log.Info($"Contacts preflight before RPC completed. state={after.State}; raw={after.RawValue}; note={note}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warning($"Contacts preflight before RPC failed non-fatally: {ex.Message}");
        }
    }

    private async Task<string> RequestContactsAccessWithProbeAsync(ImsgBridgeSettings settings, string probeAddress)
    {
        var requestSettings = settings with
        {
            RequestTimeoutSeconds = Math.Clamp(Math.Min(settings.RequestTimeoutSeconds, 20), 5, 300)
        };

        try
        {
            using var requestTimeout = new CancellationTokenSource(requestSettings.RequestTimeout);
            await _macHostActions.RequestContactsAccessAsync(
                requestSettings,
                probeAddress,
                _appSettings.PhoneNumberRegion,
                requestTimeout.Token);
            return $"Ran imsg nickname --local against {DisplayTextFormatter.SingleLine(probeAddress, string.Empty, 32)} to request Contacts access.";
        }
        catch (Exception ex)
        {
            _log.Warning($"imsg Contacts request probe failed. addressPresent={!string.IsNullOrWhiteSpace(probeAddress)}; error={ex.Message}");
            return $"Tried imsg nickname --local to request Contacts access, but macOS/imsg did not complete the request: {ex.Message}";
        }
    }

    private static string DescribeContactsRequestOutcome(string requestNote, MacContactsAuthorizationResult authorization)
    {
        if (authorization.IsAuthorized)
        {
            return $"{requestNote} Contacts authorization is now {authorization.State}.";
        }

        if (authorization.IsDeniedOrRestricted)
        {
            return $"{requestNote} Contacts authorization is {authorization.State}; macOS denied the SSH-launched request without granting access. Approve Contacts in macOS Privacy & Security for the process macOS lists, or run the imsg command from an interactive Mac Terminal session to trigger a GUI prompt.";
        }

        if (authorization.IsNotDetermined)
        {
            return $"{requestNote} Contacts authorization is still notDetermined; macOS did not present or complete a Contacts prompt for the SSH-launched imsg process.";
        }

        return $"{requestNote} Contacts authorization could not be confirmed: {authorization.Detail}.";
    }

    private static string? SelectContactProbeAddress(IEnumerable<ImsgChat> chats)
    {
        foreach (var chat in chats)
        {
            var participant = chat.Participants.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(participant))
            {
                return participant.Trim();
            }

            var identifier = ExtractAddressFromChatIdentifier(chat.Identifier);
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return identifier;
            }
        }

        return null;
    }

    private static string? ExtractAddressFromChatIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var trimmed = identifier.Trim();
        foreach (var prefix in new[] { "iMessage;-;", "iMessage;+;", "SMS;-;", "SMS;+;", "any;-;", "any;+;" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..];
            }
        }

        return trimmed.Contains(";+;", StringComparison.Ordinal)
            ? null
            : trimmed;
    }

    private void ShowSettingsSection(string section)
    {
        var showProfiles = string.Equals(section, "Profiles", StringComparison.OrdinalIgnoreCase);
        GeneralSettingsPanel.Visibility = showProfiles ? Visibility.Collapsed : Visibility.Visible;
        ProfilesSettingsPanel.Visibility = showProfiles ? Visibility.Visible : Visibility.Collapsed;
        ProfileDetailPanel.Visibility = Visibility.Collapsed;

        if (showProfiles)
        {
            RefreshProfilePickers();
            _updatingSettingsNav = true;
            SettingsNavList.SelectedIndex = 1;
            _updatingSettingsNav = false;
        }
        else
        {
            _updatingSettingsNav = true;
            SettingsNavList.SelectedIndex = 0;
            _updatingSettingsNav = false;
        }
    }

    private void ShowProfileDetail(string profileId)
    {
        var profile = _appSettings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        _shell.SettingsView.SetEditingProfile(profile.Id);
        LoadProfileIntoForm(profile);
        RefreshProfilePickers();
        GeneralSettingsPanel.Visibility = Visibility.Collapsed;
        ProfilesSettingsPanel.Visibility = Visibility.Collapsed;
        ProfileDetailPanel.Visibility = Visibility.Visible;
        _updatingSettingsNav = true;
        SettingsNavList.SelectedIndex = 1;
        _updatingSettingsNav = false;
    }

    private void CancelSettingsEdits()
    {
        LoadSettingsIntoForm();
        RefreshProfilePickers();
        SetSettingsVisible(false);
    }

    private void LoadProfileIntoForm(ImsgBridgeProfile profile)
    {
        _loadingSettingsForm = true;
        try
        {
            SetProfileFormFields(profile);
        }
        finally
        {
            _loadingSettingsForm = false;
            SetProfileSettingsDirty(false);
        }
    }

    private void SetProfileFormFields(ImsgBridgeProfile profile)
    {
        _loadedSettingsProfileId = profile.Id;
        _shell.SettingsView.SetEditingProfile(profile.Id);
        ProfileNameBox.Text = profile.Name;
        PrimaryTargetAddressBox.Text = profile.PrimaryTargetAddress;
        FallbackTargetAddressesBox.Text = string.Join(Environment.NewLine, profile.CandidateAddresses.Skip(1));
        UserBox.Text = profile.MacUser;
        PortBox.Value = profile.SshPort;
        IdentityFileBox.Text = profile.IdentityFile ?? string.Empty;
        SelectRemoteAccessMode(profile.RemoteAccessMode);
        ImsgPathBox.Text = profile.ImsgPath;
        RemoteAttachmentRootBox.Text = profile.RemoteAttachmentRoot;
        UpdateRemoteAccessModelStatus(profile);
    }

    private void SetGeneralSettingsDirty(bool isDirty)
    {
        _shell.SettingsView.SetGeneralDirty(isDirty);
        if (SaveGeneralButton is not null)
        {
            SaveGeneralButton.IsEnabled = isDirty;
        }
    }

    private void SetProfileSettingsDirty(bool isDirty)
    {
        _shell.SettingsView.SetProfileDirty(isDirty);
        if (SaveProfileButton is not null)
        {
            SaveProfileButton.IsEnabled = isDirty;
        }
    }

    private void InsertComposeText(string text)
    {
        ComposeBar.InsertText(text);
    }

    private static bool TryOpenNativeEmojiPicker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            keybd_event(VirtualKeyLeftWindows, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualKeyPeriod, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualKeyPeriod, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event(VirtualKeyLeftWindows, 0, KeyEventKeyUp, UIntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsControlKeyDown()
    {
        return IsKeyDown(VirtualKeyControl) ||
            IsKeyDown(VirtualKeyLeftControl) ||
            IsKeyDown(VirtualKeyRightControl);
    }

    private static bool IsShiftKeyDown()
    {
        return IsKeyDown(VirtualKeyShift) ||
            IsKeyDown(VirtualKeyLeftShift) ||
            IsKeyDown(VirtualKeyRightShift);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    private void ShowEmojiFallback()
    {
        var flyout = new MenuFlyout();
        foreach (var emoji in EmojiFallbackItems)
        {
            var item = new MenuFlyoutItem { Text = emoji };
            item.Click += OnEmojiMenuClicked;
            flyout.Items.Add(item);
        }

        ComposeBar.ShowFlyoutAtEmojiButton(flyout);
    }

    private async Task SyncFullCacheAsync(bool showCompletion)
    {
        if (_cacheSyncCancellation is not null)
        {
            _cacheSyncCancellation.Cancel();
            SetStatus("Canceling cache sync...");
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before syncing the local cache.");
            return;
        }

        _cacheSyncCancellation = new CancellationTokenSource();
        _transientSyncBarDepth = 0;
        CacheSyncProgressBar.Visibility = Visibility.Visible;
        CacheSyncInlineTextBlock.Visibility = Visibility.Visible;
        CacheSyncProgressBar.IsIndeterminate = true;
        CacheSyncProgressBar.Value = 0;
        CacheSyncInlineTextBlock.Text = "Syncing...";
        SyncCacheButton.Content = "Cancel sync";
        SyncCacheButton.IsEnabled = true;
        CacheSyncStatusText.Text = "Syncing chats...";
        SetStatus("Syncing cache...");
        _log.Info("Cache sync started.");

        ImsgBridgeClient? syncBridge = null;

        try
        {
            var token = _cacheSyncCancellation.Token;
            syncBridge = await CreateCacheSyncBridgeAsync(token);
            var chats = await syncBridge.ListChatsAsync(limit: ChatListFetchLimit, cancellationToken: token);
            await _messageCache.UpsertChatsAsync(chats, token);
            await ReplaceChatsAsync(await EnrichChatsWithContactNamesAsync(chats, token), token, reconcileTransientUnread: true);
            WarnIfChatFetchLimitReached(chats.Count, ChatListFetchLimit);

            var syncableChats = ConversationSyncService.OrderChatsForSync(chats.Where(chat => chat.Id is not null)).ToList();
            _log.Info($"Cache sync chat list loaded on background bridge. total={chats.Count}; syncable={syncableChats.Count}");
            CacheSyncProgressBar.IsIndeterminate = syncableChats.Count == 0;

            // Per-chat history fetches stay on the dedicated sync bridge so
            // thousands of background RPCs never queue ahead of interactive
            // sends/actions on the main pipe.
            var syncHistoryFetchService = new ConversationHistoryFetchService(new BridgeImsgClientAdapter(syncBridge));
            var progress = new Progress<SyncProgressSnapshot>(ApplyCacheSyncProgress);
            var syncedChatProgress = new Progress<ConversationSyncedChat>(ApplySyncedChatToSelectedConversation);
            var syncResult = await _conversationSyncService.SyncAsync(
                syncableChats,
                new ConversationSyncOptions(
                    HistoryLimit: CacheSyncHistoryLimit,
                    FailureBackoff: TimeSpan.FromMinutes(5),
                    ExternallySatisfiedFetchedLimit: SelectedHistoryLimit)
                {
                    GetSelectedStableId = () => _selectedChat?.StableId,
                    TryTakeExternallySatisfied = TakeCacheSyncExternallySatisfied,
                    // Automatic post-connect sweeps stay bounded to recent
                    // activity; the manual Sync now button walks everything.
                    MaxActiveChats = showCompletion ? null : BackgroundSyncMaxActiveChats
                },
                (chat, limit, fetchToken) => LoadCacheSyncHistoryWithFallbackAsync(syncHistoryFetchService, chat, limit, fetchToken),
                progress,
                syncedChatProgress,
                token);

            var cappedText = syncResult.CappedOutChats > 0
                ? $" Older chats ({syncResult.CappedOutChats}) sync on demand or via Sync now."
                : string.Empty;
            CacheSyncStatusText.Text = syncResult.TotalChats == 0
                ? "No chats were available to sync."
                : syncResult.FailedChats == 0
                    ? $"Synced {syncResult.SyncedChats} chats{FormatPartialSyncText(syncResult.PartialChats)}{FormatSkippedSyncText(syncResult.SkippedChats)}.{cappedText}"
                    : $"Synced {syncResult.SyncedChats}/{syncResult.TotalChats} chats{FormatPartialSyncText(syncResult.PartialChats)}{FormatSkippedSyncText(syncResult.SkippedChats)}; {syncResult.FailedChats} failed. First failure: {syncResult.Failures[0].DisplayName}: {syncResult.Failures[0].Error}{cappedText}";
            CacheSyncInlineTextBlock.Text = syncResult.TotalChats == 0
                ? "No chats"
                : syncResult.FailedChats == 0
                    ? FormatCacheSyncInlineCompletion(syncResult.PartialChats, syncResult.SkippedChats)
                    : $"{Math.Round((double)(syncResult.SyncedChats + syncResult.SkippedChats) / syncResult.TotalChats * 100)}% complete · {syncResult.FailedChats} failed";
            if (showCompletion)
            {
                SetStatus(syncResult.FailedChats == 0 ? "Cache sync complete." : "Cache sync completed with failures.");
            }
            _log.Info($"Cache sync completed. synced={syncResult.SyncedChats}; partial={syncResult.PartialChats}; skipped={syncResult.SkippedChats}; total={syncResult.TotalChats}; failed={syncResult.FailedChats}");
        }
        catch (OperationCanceledException)
        {
            CacheSyncStatusText.Text = "Cache sync canceled.";
            SetStatus("Cache sync canceled.");
            _log.Info("Cache sync canceled.");
        }
        catch (Exception ex)
        {
            _log.Error("Cache sync failed.", ex);
            CacheSyncStatusText.Text = $"{ex.Message}. Details were written to {_log.LogFilePath}.";
            SetStatus("Cache sync failed. Open Settings > Diagnostics for logs.");
        }
        finally
        {
            if (syncBridge is not null)
            {
                await syncBridge.DisposeAsync();
            }

            _cacheSyncCancellation.Dispose();
            _cacheSyncCancellation = null;
            CacheSyncProgressBar.IsIndeterminate = false;
            CacheSyncProgressBar.Visibility = Visibility.Collapsed;
            CacheSyncInlineTextBlock.Visibility = Visibility.Collapsed;
            CacheSyncInlineTextBlock.Text = string.Empty;
            SyncCacheButton.Content = "Sync now";
            SyncCacheButton.IsEnabled = _bridge.IsConnected;
        }
    }

    private async Task<ImsgBridgeClient> CreateCacheSyncBridgeAsync(CancellationToken cancellationToken)
    {
        var bridge = new ImsgBridgeClient();
        try
        {
            await bridge.ConnectAsync(_settings, cancellationToken);
            return bridge;
        }
        catch
        {
            await bridge.DisposeAsync();
            throw;
        }
    }

    private async Task<ConversationSyncHistoryResult> LoadCacheSyncHistoryWithFallbackAsync(
        ConversationHistoryFetchService historyFetchService,
        ImsgChat chat,
        int requestedLimit,
        CancellationToken cancellationToken)
    {
        var result = await historyFetchService.FetchBackgroundSyncAsync(
            chat,
            requestedLimit,
            new BackgroundHistoryFetchOptions(
                BackgroundSyncPerChatTimeout,
                CacheSyncFallbackHistoryLimit,
                BackgroundSyncFallbackTimeout,
                CacheSyncLatestMessageFallbackHistoryLimit,
                BackgroundSyncLatestMessageTimeout),
            cancellationToken);

        foreach (var warning in result.Warnings)
        {
            _log.Warning($"{warning} service={chat.Service ?? "unknown"}");
        }

        return result.History;
    }

    private void ApplyCacheSyncProgress(SyncProgressSnapshot snapshot)
    {
        _shell.SyncStatus.Update(snapshot, FormatCacheSyncProgressText(snapshot));
        UpdateCacheSyncProgress(
            snapshot.ActiveChatName ?? "chat",
            snapshot.CompletedCount,
            snapshot.TotalCount,
            snapshot.Elapsed,
            snapshot.FailedCount);
    }

    private void ApplySyncedChatToSelectedConversation(ConversationSyncedChat syncedChat)
    {
        if (_foregroundHistoryLoading ||
            _selectedChat is null ||
            !_selectedChat.ContainsStableId(syncedChat.Chat.StableId))
        {
            return;
        }

        var wasAtLatest = IsMessageListAtLatest();
        if (_selectedChat.IsMerged)
        {
            _shell.Conversation.AppendOrReplaceMessages(
                syncedChat.Messages,
                syncedChat.Chat.Service,
                resetVisibleWindow: wasAtLatest,
                hasFailedAttachmentDownload: HasFailedAttachmentDownload);
        }
        else
        {
            _shell.Conversation.ReplaceMessages(
                syncedChat.Messages,
                syncedChat.Chat.Service,
                resetVisibleWindow: wasAtLatest,
                hasFailedAttachmentDownload: HasFailedAttachmentDownload);
        }

        if (wasAtLatest)
        {
            ScrollMessagesToLatest(forceLatest: true);
        }
        else
        {
            JumpToLatestButton.Visibility = Visibility.Visible;
        }
    }

    private void MarkCacheSyncExternallySatisfied(ChatListItem item)
    {
        foreach (var stableId in item.StableIds)
        {
            MarkCacheSyncExternallySatisfied(stableId);
        }
    }

    private void MarkCacheSyncExternallySatisfied(string stableId)
    {
        lock (_cacheSyncExternallySatisfiedChatIds)
        {
            _cacheSyncExternallySatisfiedChatIds.Add(stableId);
        }
    }

    private bool TakeCacheSyncExternallySatisfied(string stableId)
    {
        lock (_cacheSyncExternallySatisfiedChatIds)
        {
            return _cacheSyncExternallySatisfiedChatIds.Remove(stableId);
        }
    }

    private static string FormatPartialSyncText(int partialChats)
    {
        return partialChats == 0 ? string.Empty : $" ({partialChats} partial)";
    }

    private static string FormatSkippedSyncText(int skippedChats)
    {
        return skippedChats == 0 ? string.Empty : $" ({skippedChats} current)";
    }

    private static string FormatCacheSyncInlineCompletion(int partialChats, int skippedChats)
    {
        var parts = new List<string> { "100% complete" };
        if (partialChats > 0)
        {
            parts.Add($"{partialChats} partial");
        }

        if (skippedChats > 0)
        {
            parts.Add($"{skippedChats} current");
        }

        return string.Join(" · ", parts);
    }

    private void UpdateCacheSyncProgress(string chatName, int completedChats, int totalChats, TimeSpan elapsed, int failedChats)
    {
        if (totalChats <= 0)
        {
            CacheSyncProgressBar.Value = 0;
            CacheSyncInlineTextBlock.Text = "0%";
            CacheSyncStatusText.Text = "No chats were available to sync.";
            return;
        }

        var percent = Math.Clamp((double)completedChats / totalChats, 0, 1);
        var percentText = $"{percent * 100:0}%";
        var etaText = FormatSyncEta(EstimateRemainingTime(completedChats, totalChats, elapsed));
        var failureText = failedChats == 0 ? string.Empty : $" · {failedChats} failed";

        CacheSyncProgressBar.Value = percent * 100;
        CacheSyncInlineTextBlock.Text = $"{percentText} · {completedChats}/{totalChats}{etaText}{failureText}";
        CacheSyncStatusText.Text = $"Syncing {chatName} ({completedChats}/{totalChats}, {percentText}{etaText}{failureText})";
    }

    private static string FormatCacheSyncProgressText(SyncProgressSnapshot snapshot)
    {
        if (snapshot.TotalCount <= 0)
        {
            return "No chats were available to sync.";
        }

        var etaText = FormatSyncEta(snapshot.EstimatedRemaining);
        var failureText = snapshot.FailedCount == 0 ? string.Empty : $" · {snapshot.FailedCount} failed";
        return $"{snapshot.Percent:0}% · {snapshot.CompletedCount}/{snapshot.TotalCount}{etaText}{failureText}";
    }

    // Early per-chat averages over thousands of chats produce absurd
    // multi-hour ETAs; show an estimate only once it is meaningful.
    private static string FormatSyncEta(TimeSpan? estimatedRemaining)
    {
        if (estimatedRemaining is null)
        {
            return string.Empty;
        }

        return estimatedRemaining.Value > TimeSpan.FromMinutes(90)
            ? " · syncing most recent first"
            : $" · ETA {FormatDuration(estimatedRemaining.Value)}";
    }

    private static TimeSpan? EstimateRemainingTime(int completedChats, int totalChats, TimeSpan elapsed)
    {
        if (completedChats <= 0 || totalChats <= completedChats || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var averageTicks = elapsed.Ticks / completedChats;
        return TimeSpan.FromTicks(averageTicks * (totalChats - completedChats));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(1, duration.Seconds)}s";
    }

    private async Task ClearLocalCacheAsync()
    {
        if (!await ConfirmAsync("Clear Local Cache", "Clear cached chats, messages, and downloaded attachments from this PC?"))
        {
            return;
        }

        await _messageCache.ClearAsync();
        ClearAttachmentsDirectory();
        _shell.Conversations.ReplaceChats([]);
        ClearTransientUnreadMessages();
        UpdateUnreadBadges();
        _selectedChat = null;
        _shell.Conversations.SelectedChat = null;
        _shell.Conversation.ClearSelection();
        ChatListPane.SelectedItem = null;
        JumpToLatestButton.Visibility = Visibility.Collapsed;
        SetInlineStatus("Local cache cleared.");
        CacheSyncStatusText.Text = "Local cache cleared.";
        SetStatus("Local cache cleared.");
    }

    private void OnBridgeNotification(object? sender, JsonRpcNotification notification)
    {
        if (!notification.TryGetMessage(out var message))
        {
            return;
        }

        _ = HandleBridgeMessageNotificationAsync(message);
    }

    private void OnBridgeConnectionClosed(object? sender, JsonRpcConnectionClosedEventArgs args)
    {
        ResetTypingIndicatorState();
        if (_userDisconnectedBridge || !_settings.IsConfigured)
        {
            return;
        }

        var reason = args.Exception is null
            ? "RPC stream ended."
            : $"{args.Exception.GetType().Name}: {args.Exception.Message}";
        _log.Warning($"Bridge RPC stream closed unexpectedly. {reason}");
        if (!DispatcherQueue.TryEnqueue(() => QueueBridgeReconnect(reason)))
        {
            _log.Warning("Unable to enqueue bridge reconnect on the UI dispatcher.");
        }
    }

    private void QueueBridgeReconnect(string reason)
    {
        if (_userDisconnectedBridge || !_settings.IsConfigured)
        {
            return;
        }

        if (_bridgeReconnectCancellation is { IsCancellationRequested: false })
        {
            return;
        }

        _bridgeReconnectCancellation?.Dispose();
        _bridgeReconnectCancellation = new CancellationTokenSource();
        _capabilities = new ImsgCapabilities();
        ApplyCapabilityState();
        SetConnectionStatus(ConnectionState.Degraded, $"Bridge disconnected; automatic reconnect queued. {reason}");
        _ = ReconnectBridgeAsync(_bridgeReconnectCancellation);
    }

    private async Task RecoverBridgeAfterSystemResumeAsync()
    {
        if (_userDisconnectedBridge || !_settings.IsConfigured)
        {
            return;
        }

        if (!_resumeReconnectGate.Wait(0))
        {
            _log.Info("System resume bridge recovery ignored because another resume recovery is already running.");
            return;
        }

        try
        {
            _log.Info("System resume detected; resetting bridge session before reconnect.");
            _bridgeReconnectCancellation?.Cancel();
            ResetTypingIndicatorState();
            _capabilities = new ImsgCapabilities();
            _faceTimeLinkAvailable = false;
            ApplyCapabilityState();
            SetConnectionStatus(ConnectionState.Reconnecting, "System resumed; reconnecting to Mac bridge...");

            try
            {
                await ResetBridgeChannelsAsync();
                await _bridge.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _log.Warning($"System resume bridge reset failed before reconnect. {ex.GetType().Name}: {ex.Message}");
            }

            var connected = await ConnectAsync(saveSettings: false);
            if (!connected && !_bridge.IsConnected)
            {
                SetConnectionStatus(ConnectionState.Failed, "System resumed, but the Mac bridge did not reconnect. Use Connect to retry.");
            }
        }
        finally
        {
            _resumeReconnectGate.Release();
        }
    }

    private async Task ReconnectBridgeAsync(CancellationTokenSource reconnectSource)
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15)
        };

        try
        {
            for (var index = 0; index < delays.Length; index++)
            {
                await Task.Delay(delays[index], reconnectSource.Token);
                if (_userDisconnectedBridge || reconnectSource.IsCancellationRequested)
                {
                    return;
                }

                if (_bridge.IsConnected)
                {
                    _log.Info("Automatic bridge reconnect stopped because another connection path restored the bridge.");
                    return;
                }

                SetConnectionStatus(ConnectionState.Reconnecting, $"Reconnecting to Mac bridge (attempt {index + 1}/{delays.Length})...");
                var connected = await ConnectAsync(saveSettings: false, reconnectSource.Token);
                if (connected || _bridge.IsConnected)
                {
                    return;
                }
            }

            if (!_userDisconnectedBridge && !reconnectSource.IsCancellationRequested)
            {
                SetConnectionStatus(ConnectionState.Failed, "Bridge disconnected and automatic reconnect did not recover. Use Connect to retry.");
            }
        }
        catch (OperationCanceledException)
        {
            // Manual disconnect or page shutdown cancelled the reconnect loop.
        }
        catch (Exception ex)
        {
            _log.Error("Automatic bridge reconnect failed.", ex);
            if (!_userDisconnectedBridge)
            {
                SetConnectionStatus(ConnectionState.Failed, "Bridge reconnect failed. Open Settings > Diagnostics for logs.");
            }
        }
        finally
        {
            if (ReferenceEquals(_bridgeReconnectCancellation, reconnectSource))
            {
                _bridgeReconnectCancellation = null;
            }

            reconnectSource.Dispose();
        }
    }

    private async Task HandleBridgeMessageNotificationAsync(ImsgMessage message)
    {
        var started = Stopwatch.StartNew();
        var result = await _bridgeNotificationWorkflowService.ProcessAsync(
            message,
            _appSettings.EnableNotifications);
        foreach (var warning in result.Warnings)
        {
            _log.Warning(warning);
        }

        if (!DispatcherQueue.TryEnqueue(() => ApplyBridgeMessageNotification(result)))
        {
            _log.Warning("Unable to enqueue watch notification on the UI dispatcher.");
            return;
        }

        _log.Info($"Watch notification queued. fromMe={message.IsFromMe}; reaction={message.IsReactionEvent}; chat={message.ChatStableId}; elapsedMs={started.ElapsedMilliseconds}");
    }

    private void ApplyBridgeMessageNotification(BridgeNotificationProcessingResult result)
    {
        var started = Stopwatch.StartNew();
        var message = result.Message;
        try
        {
            if (message.IsReactionEvent)
            {
                QueueSelectedReactionHistoryRefresh(message);
                return;
            }

            if (_selectedChat is not null && _selectedChat.ContainsStableId(message.ChatStableId))
            {
                var wasAtLatest = IsMessageListAtLatest();
                _shell.Conversation.AppendOrReplaceMessage(
                    message,
                    ServiceForMessage(_selectedChat, message),
                    resetVisibleWindow: wasAtLatest,
                    growVisibleWindowBy: wasAtLatest ? 0 : 1,
                    HasFailedAttachmentDownload);
                if (wasAtLatest)
                {
                    ScrollMessagesToLatest(forceLatest: true);
                }
                else
                {
                    JumpToLatestButton.Visibility = Visibility.Visible;
                }
            }

            QueueAutoDownloadAttachments([message]);

            _webCompanion.PublishMessageEvent(message);
            TrackTransientLatestMessage(message);
            if (result.ShouldCountAsNewUnread)
            {
                TrackTransientUnread(message);
            }

            if (!result.ShouldShowWindowsNotification)
            {
                return;
            }

            QueueInboundShellNotification(
                InboundNotificationIdentityPolicy.WithTrustedChatTitle(message, _shell.Conversations.AllChats),
                ((App)Application.Current).MainWindowHandle);
        }
        catch (Exception ex)
        {
            _log.Error("Watch notification UI update failed.", ex);
            SetStatus("Live message update failed. Open Settings > Diagnostics for logs.");
        }
        finally
        {
            _log.Info($"Watch notification UI update completed. fromMe={message.IsFromMe}; reaction={message.IsReactionEvent}; selected={_selectedChat?.ContainsStableId(message.ChatStableId) == true}; elapsedMs={started.ElapsedMilliseconds}");
        }
    }

    private void QueueSelectedReactionHistoryRefresh(ImsgMessage message)
    {
        _reactionHistoryRefreshCoordinator.TryQueueSelectedRefresh(
            message,
            _selectedChat,
            warning => _log.Warning(warning));
    }

    private void QueueInboundShellNotification(ImsgMessage message, nint windowHandle)
    {
        _log.Info($"Inbound shell notification queued. chat={message.ChatStableId}; windowHandle={windowHandle}; attachments={message.Attachments.Count}");
        _shellNotificationDispatcher?.TryQueue(
            $"Inbound shell notification chat={message.ChatStableId}",
            () =>
            {
                _notifications.ShowInboundMessage(message, _appSettings.ShowMessageContentInNotifications);
                _windowShell.FlashTaskbar(windowHandle);
                return Task.CompletedTask;
            });
    }

    private void OnAttachmentDownloadStateChanged(object? sender, string messageGuid)
    {
        _ = DispatcherQueue.TryEnqueue(() => RefreshAttachmentDownloadState(messageGuid));
    }

    private async Task ReplaceChatsAsync(
        IEnumerable<ImsgChat> chats,
        CancellationToken cancellationToken = default,
        bool reconcileTransientUnread = false)
    {
        await _chatProjectionGate.WaitAsync(cancellationToken);
        try
        {
            if (reconcileTransientUnread)
            {
                // Fresh Mac rows are authoritative for read state (phone reads
                // sync into chat.db); stale transient markers must not keep the
                // badge high. The projection below rebuilds counts, and this
                // method ends with UpdateUnreadBadges.
                _chatListState.ReconcileTransientUnread(chats);
            }

            var selectedStableId = _selectedChat?.StableId ??
                (ChatListPane.SelectedItem as ChatListItem)?.StableId;
            var chatList = chats
                .Select(_chatListState.ApplyTransientLatestMessageDate)
                .ToList();
            var latestMessagePreviews = await _messageCache.GetLatestMessageTextByChatStableIdAsync(cancellationToken);
            var effectiveLatestMessagePreviews = _chatListState.MergeLatestMessagePreviews(latestMessagePreviews);
            var latestMessageDates = await _messageCache.GetLatestMessageDateByChatStableIdAsync(cancellationToken);
            var effectiveLatestMessageDates = _chatListState.MergeLatestMessageDates(latestMessageDates);
            var transientUnreadCounts = _chatListState.BuildTransientUnreadCountsByStableId();
            var projectionStarted = Stopwatch.StartNew();
            var chatItems = await Task.Run(
                () => ChatListItem.FromChats(
                    chatList,
                    _appSettings.MergeChatsByParticipants,
                    _appSettings.PhoneNumberRegion,
                    effectiveLatestMessagePreviews,
                    effectiveLatestMessageDates,
                    transientUnreadCounts),
                cancellationToken);

            _shell.Conversations.SelectedChat = _selectedChat ?? ChatListPane.SelectedItem as ChatListItem;
            _shell.Conversations.ReplaceChats(chatItems);
            RefreshRecipientSuggestions(chatItems);
            _log.Info($"Chat list projected. source={chatList.Count}; rows={chatItems.Count}; merged={chatItems.Count(item => item.IsMerged)}; elapsedMs={projectionStarted.ElapsedMilliseconds}");
            UpdateChatSearchStatusFromViewModel();
            RestoreChatSelection(selectedStableId);
            UpdateUnreadBadges();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info("Chat list projection canceled because a newer refresh or shutdown took priority.");
        }
        finally
        {
            _chatProjectionGate.Release();
        }
    }

    private async Task RefreshChatSearchAsync()
    {
        _chatSearchCancellation?.Cancel();

        var query = _shell.Conversations.SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _shell.Conversations.IsMessageSearchInProgress = false;
            _shell.Conversations.SetMessageSearchMatches([]);
            UpdateChatSearchStatusFromViewModel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _chatSearchCancellation = cancellation;
        _shell.Conversations.IsMessageSearchInProgress = true;
        UpdateChatSearchStatusFromViewModel();

        try
        {
            await Task.Delay(250, cancellation.Token);
            var messageMatches = await _messageCache.SearchChatStableIdsByMessageContentAsync(query, cancellationToken: cancellation.Token);
            if (!ReferenceEquals(_chatSearchCancellation, cancellation))
            {
                return;
            }

            _shell.Conversations.SetMessageSearchMatches(messageMatches);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warning($"Cached message search failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_chatSearchCancellation, cancellation))
            {
                _shell.Conversations.IsMessageSearchInProgress = false;
                UpdateChatSearchStatusFromViewModel();
                _chatSearchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void UpdateChatSearchStatusFromViewModel()
    {
        var status = _shell.Conversations.SearchStatus;
        ChatListPane.SetSearchStatus(status, isVisible: !string.IsNullOrWhiteSpace(status));
    }

    private void RestoreChatSelection(string? selectedStableId)
    {
        if (!string.IsNullOrWhiteSpace(selectedStableId))
        {
            var replacementSelection = _shell.Conversations.Chats.FirstOrDefault(chat =>
                chat.ContainsStableId(selectedStableId));
            if (replacementSelection is not null)
            {
                _selectedChat = replacementSelection.IsNewMessageDraft ? null : replacementSelection;
                _shell.Conversations.SelectedChat = replacementSelection;
                ChatListPane.SelectedItem = replacementSelection;
            }
        }
    }

    private ChatListItem? FindSentDraftChat(JsonElement sendResult, string recipient)
    {
        var returnedChatGuid = MessageSendService.ReadReturnedChatGuid(sendResult);
        if (!string.IsNullOrWhiteSpace(returnedChatGuid))
        {
            var chatByGuid = _shell.Conversations.Chats.FirstOrDefault(chat =>
                chat.Chats.Any(source =>
                    string.Equals(source.Guid, returnedChatGuid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source.Identifier, returnedChatGuid, StringComparison.OrdinalIgnoreCase)));
            if (chatByGuid is not null)
            {
                return chatByGuid;
            }
        }

        return _shell.Conversations.Chats.FirstOrDefault(chat =>
            !chat.IsNewMessageDraft &&
            chat.Chats.Any(source =>
                string.Equals(source.Identifier, recipient, StringComparison.OrdinalIgnoreCase) ||
                source.Participants.Any(participant => string.Equals(participant, recipient, StringComparison.OrdinalIgnoreCase))));
    }

    private ChatListItem? FindCreatedDraftChat(JsonElement sendResult, IReadOnlyList<string> recipients)
    {
        var returnedChatGuid = MessageSendService.ReadReturnedChatGuid(sendResult);
        if (!string.IsNullOrWhiteSpace(returnedChatGuid))
        {
            var chatByGuid = _shell.Conversations.Chats.FirstOrDefault(chat =>
                chat.Chats.Any(source =>
                    string.Equals(source.Guid, returnedChatGuid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source.Identifier, returnedChatGuid, StringComparison.OrdinalIgnoreCase)));
            if (chatByGuid is not null)
            {
                return chatByGuid;
            }
        }

        var normalizedRecipients = recipients
            .Where(static recipient => !string.IsNullOrWhiteSpace(recipient))
            .Select(static recipient => recipient.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedRecipients.Count == 0)
        {
            return null;
        }

        return _shell.Conversations.Chats.FirstOrDefault(chat =>
            !chat.IsNewMessageDraft &&
            chat.Chats.Any(source =>
            {
                var participants = source.Participants
                    .Where(static participant => !string.IsNullOrWhiteSpace(participant))
                    .Select(static participant => participant.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return normalizedRecipients.All(participants.Contains);
            }));
    }

    private static IReadOnlyList<string> ParseDraftRecipients(string recipients)
    {
        return ChatListItem.ParseRecipientText(recipients);
    }

    private IReadOnlyList<string> FindRecipientSuggestions(string text)
    {
        var fragment = CurrentRecipientFragment(text);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return _recipientSuggestions.Take(12).ToList();
        }

        return _recipientSuggestions
            .Where(suggestion => suggestion.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
    }

    private static string CurrentRecipientFragment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lastSeparator = Math.Max(text.LastIndexOf(','), text.LastIndexOf(';'));
        return lastSeparator < 0
            ? text.Trim()
            : text[(lastSeparator + 1)..].Trim();
    }

    private void UpdateDraftRecipientStatus()
    {
        if (!_shell.Conversation.IsNewMessageDraft)
        {
            return;
        }

        var recipients = ParseDraftRecipients(_shell.Conversation.DraftRecipientText);
        _shell.Conversation.InlineStatus = recipients.Count > 1
            ? "Group creation uses imsg chat-create and creates iMessage chats only."
            : string.Empty;
    }

    private void RefreshRecipientSuggestions(IEnumerable<ChatListItem> chats)
    {
        _recipientSuggestions.Clear();
        _recipientSuggestions.AddRange(RecipientSuggestionBuilder.Build(chats));
        DraftRecipientsInputBox.ItemsSource = _recipientSuggestions.Take(12).ToList();
    }

    private void ResetVisibleMessagesToLatest()
    {
        _shell.Conversation.ResetToLatest();
    }

    private bool CanLoadOlderVisibleMessages =>
        _shell.Conversation.CanLoadOlder ||
        (_selectedChat is not null && !_olderCachedHistoryExhausted && !_olderCachedHistoryLoading);

    private async Task LoadOlderVisibleMessagesAsync()
    {
        if (!CanLoadOlderVisibleMessages)
        {
            return;
        }

        var oldScrollableHeight = _messageScrollViewer?.ScrollableHeight ?? 0;
        var oldVerticalOffset = _messageScrollViewer?.VerticalOffset ?? 0;
        if (_shell.Conversation.CanLoadOlder)
        {
            _shell.Conversation.LoadOlderMessages();
        }
        else
        {
            await LoadOlderCachedHistoryPageAsync();
        }

        QueueVisibleAutoDownloadAttachments();
        MessageListView.UpdateLayout();

        if (_messageScrollViewer is not null)
        {
            var addedHeight = Math.Max(0, _messageScrollViewer.ScrollableHeight - oldScrollableHeight);
            _messageScrollViewer.ChangeView(null, oldVerticalOffset + addedHeight, null, disableAnimation: true);
        }
    }

    private async Task LoadOlderCachedHistoryPageAsync()
    {
        if (_selectedChat is null || _olderCachedHistoryLoading || _olderCachedHistoryExhausted)
        {
            return;
        }

        var cursor = GetOldestLoadedMessageCursor();
        if (cursor is null)
        {
            _olderCachedHistoryExhausted = true;
            return;
        }

        var selected = _selectedChat;
        _olderCachedHistoryLoading = true;
        try
        {
            var page = await _conversationDataService.LoadOlderCachedMessagesAsync(
                selected,
                cursor.Value.DateValue,
                cursor.Value.MessageRowId,
                OlderVisibleMessagePageSize,
                _historyLoadCancellation?.Token ?? CancellationToken.None);
            if (!IsSelectedChat(selected))
            {
                return;
            }

            if (page.Count == 0)
            {
                _olderCachedHistoryExhausted = true;
                return;
            }

            var added = _shell.Conversation.PrependOlderMessages(
                page,
                selected.Chat.Service,
                HasFailedAttachmentDownload);
            _olderCachedHistoryExhausted = added == 0 || page.Count < OlderVisibleMessagePageSize;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _olderCachedHistoryExhausted = true;
            _log.Warning($"Older cached history page load failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _olderCachedHistoryLoading = false;
        }
    }

    private (string DateValue, long? MessageRowId)? GetOldestLoadedMessageCursor()
    {
        var oldest = _shell.Conversation.AllMessages.FirstOrDefault()?.Message;
        if (oldest is null || string.IsNullOrWhiteSpace(oldest.DateText))
        {
            return null;
        }

        return (oldest.DateText, oldest.Id);
    }

    private bool HasFailedAttachmentDownload(ImsgMessage message)
    {
        return _attachmentService.HasFailedDownload(message);
    }

    private string ResolveAttachmentLocalPath(ImsgMessage message, ImsgAttachment attachment)
    {
        if (message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var localPath = FirstExistingLocalAttachmentPath(attachment);
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                return localPath;
            }
        }

        return _attachmentService.GetLocalPath(message, attachment);
    }

    private static string? FirstExistingLocalAttachmentPath(ImsgAttachment attachment)
    {
        foreach (var candidate in new[] { attachment.OriginalPath, attachment.Path, attachment.ConvertedPath })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void TrackTransientUnread(ImsgMessage message)
    {
        if (!_chatListState.TrackTransientUnread(message))
        {
            return;
        }

        ReprojectTransientUnreadRows();
        UpdateUnreadBadges();
    }

    private void TrackTransientLatestMessage(ImsgMessage message)
    {
        if (_chatListState.TrackTransientLatestMessage(message))
        {
            ReprojectTransientUnreadRows();
        }
    }

    private void TrackTransientLatestMessage(ChatListItem chat, string preview)
    {
        _chatListState.TrackTransientLatestMessage(
            chat.StableIds,
            DisplayTextFormatter.MessageText(preview, string.Empty),
            DateTimeOffset.UtcNow);
        ReprojectTransientUnreadRows();
    }

    // Watch flurries can deliver many messages per second; reprojecting the
    // full chat list per event stacked 100-250 ms projections back to back,
    // so requests coalesce through a short debounce instead.
    private void ReprojectTransientUnreadRows() => _transientReprojectQueue?.Enqueue(true);

    private void ReprojectTransientUnreadRowsNow()
    {
        var sourceChats = _shell.Conversations.AllChats
            .SelectMany(static item => item.Chats)
            .GroupBy(static chat => chat.StableId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (sourceChats.Count == 0)
        {
            return;
        }

        _ = ReplaceChatsAsync(sourceChats);
    }

    private void ClearTransientUnreadForChat(ChatListItem chat)
    {
        if (_chatListState.ClearTransientUnreadForChat(chat))
        {
            UpdateUnreadBadges();
        }
    }

    private void ClearTransientUnreadMessages()
    {
        if (_chatListState.ClearAllTransientUnread())
        {
            UpdateUnreadBadges();
        }
    }

    private void UpdateUnreadBadges()
    {
        // Row UnreadCount already merges transient markers; adding the
        // transient total again double-counted live messages.
        var unreadCount = _shell.Conversations.AllChats.Sum(static chat => chat.UnreadCount);
        if (unreadCount == _lastUnreadBadgeCount)
        {
            return;
        }

        _lastUnreadBadgeCount = unreadCount;
        QueueTrayUnreadBadgeUpdate(unreadCount);
        QueueTaskbarUnreadBadgeUpdate(((App)Application.Current).MainWindowHandle, unreadCount);
        _webCompanion.PublishUnreadCount(unreadCount);
    }

    private void QueueTrayUnreadBadgeUpdate(int unreadCount)
    {
        _trayUnreadBadgeQueue?.Enqueue(new TrayUnreadBadgeUpdate(unreadCount));
    }

    private void ApplyTrayUnreadBadgeIcon(Uri iconUri, int unreadCount)
    {
        var started = Stopwatch.StartNew();
        try
        {
            TrayIcon.IconSource = new BitmapImage(iconUri);
            ApplyTrayIdentity(forceCreate: true);
            _log.Info($"Tray unread badge UI update completed. unread={unreadCount}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Tray unread badge update failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void QueueTaskbarUnreadBadgeUpdate(nint windowHandle, int unreadCount)
    {
        _taskbarUnreadBadgeQueue?.Enqueue(new UnreadBadgeUpdate(windowHandle, unreadCount));
    }

    private void ApplyTaskbarUnreadBadge(nint windowHandle, int unreadCount)
    {
        try
        {
            var windowBadgeIconPath = unreadCount <= 0 ? null : GetTaskbarUnreadBadgeIconPath(unreadCount);
            ((App)Application.Current).SetMainWindowIcon(windowBadgeIconPath);
            lock (_taskbarUnreadIconLock)
            {
                _taskbarUnreadIcon?.Dispose();
                _taskbarUnreadIcon = null;
                _windowShell.SetUnreadBadge(
                    windowHandle,
                    0,
                    0);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Taskbar unread badge update failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Uri GetTrayUnreadBadgeIconUri(int unreadCount)
    {
        _trayBadgeIconDirectory ??= Path.Combine(_paths.Root, "badge-icons");
        Directory.CreateDirectory(_trayBadgeIconDirectory);

        var badgeText = BadgeText(unreadCount);
        var safeBadgeText = badgeText.Replace("+", "plus", StringComparison.Ordinal);
        var iconPath = Path.Combine(_trayBadgeIconDirectory, $"messages-tray-unread-v4-{safeBadgeText}.ico");
        if (!File.Exists(iconPath))
        {
            SaveTrayUnreadBadgeIcon(iconPath, unreadCount);
        }

        return new Uri(iconPath);
    }

    private string GetTaskbarUnreadBadgeIconPath(int unreadCount)
    {
        _trayBadgeIconDirectory ??= Path.Combine(_paths.Root, "badge-icons");
        Directory.CreateDirectory(_trayBadgeIconDirectory);

        var badgeText = BadgeText(unreadCount);
        var safeBadgeText = badgeText.Replace("+", "plus", StringComparison.Ordinal);
        var iconPath = Path.Combine(_trayBadgeIconDirectory, $"messages-taskbar-unread-v2-{safeBadgeText}.ico");
        if (!File.Exists(iconPath))
        {
            SaveTaskbarUnreadBadgeIcon(iconPath, unreadCount);
        }

        return iconPath;
    }

    private void RefreshTrayIcon()
    {
        try
        {
            ApplyTrayIdentity(forceCreate: true);
        }
        catch (Exception ex)
        {
            _log.Warning($"Tray icon refresh failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyTrayIdentity(bool forceCreate)
    {
        TrayIcon.CustomName = AppDisplayName;
        TrayIcon.ToolTipText = AppDisplayName;
        TrayIcon.TrayToolTip = new TextBlock
        {
            Text = AppDisplayName,
            Padding = new Thickness(8, 4, 8, 4)
        };
        TrayIcon.LeftClickCommand = TrayOpenCommand;
        if (forceCreate)
        {
            TrayIcon.ForceCreate();
        }
    }

    private static void SaveTrayUnreadBadgeIcon(string iconPath, int unreadCount)
    {
        using var bitmap = CreateUnreadBadgeBitmap(unreadCount, "MessagesTray.ico", includeBaseIcon: true, includeText: true);
        SaveBitmapAsPngIcon(bitmap, iconPath);
    }

    private static void SaveTaskbarUnreadBadgeIcon(string iconPath, int unreadCount)
    {
        using var bitmap = CreateUnreadBadgeBitmap(unreadCount, "Messages.ico", includeBaseIcon: true, includeText: false);
        SaveBitmapAsPngIcon(bitmap, iconPath);
    }

    private static System.Drawing.Bitmap CreateUnreadBadgeBitmap(
        int unreadCount,
        string baseIconFileName,
        bool includeBaseIcon,
        bool includeText)
    {
        const int iconSize = 64;
        var badgeOffset = includeBaseIcon ? 3 : 0;
        var badgeSize = includeBaseIcon
            ? (BadgeText(unreadCount).Length > 2 ? 22 : 19)
            : 18;
        var baseIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", baseIconFileName);

        var bitmap = new System.Drawing.Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (includeBaseIcon && File.Exists(baseIconPath))
        {
            using var baseIcon = new System.Drawing.Icon(baseIconPath, iconSize, iconSize);
            graphics.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, iconSize, iconSize));
        }

        var text = BadgeText(unreadCount);
        var badgeRect = new System.Drawing.Rectangle(
            iconSize - badgeSize + badgeOffset,
            -badgeOffset,
            badgeSize,
            badgeSize);

        using var badgeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(215, 0, 21));
        using var borderPen = new System.Drawing.Pen(System.Drawing.Color.White, includeBaseIcon ? 2.25f : 1.75f);
        graphics.FillEllipse(badgeBrush, badgeRect);
        graphics.DrawEllipse(borderPen, badgeRect);

        if (includeText)
        {
            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var font = new System.Drawing.Font("Segoe UI", text.Length > 2 ? 7.5f : 9.25f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            graphics.DrawString(text, font, textBrush, badgeRect, format);
        }

        return bitmap;
    }

    private static void SaveBitmapAsPngIcon(System.Drawing.Bitmap bitmap, string iconPath)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        using var stream = File.Create(iconPath);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)64);
        writer.Write((byte)64);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(pngBytes.Length);
        writer.Write(22);
        writer.Write(pngBytes);
    }

    private static string BadgeText(int unreadCount) =>
        unreadCount > 99 ? "99+" : unreadCount.ToString(CultureInfo.InvariantCulture);

    private static bool IsTimeoutLikeHistoryError(string error) =>
        error.Contains("task was canceled", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static string? ServiceForMessage(ChatListItem chat, ImsgMessage message)
    {
        return chat.Chats.FirstOrDefault(source => string.Equals(source.StableId, message.ChatStableId, StringComparison.OrdinalIgnoreCase))?.Service
            ?? message.Service
            ?? chat.Chat.Service;
    }

    private bool IsSelectedChat(ChatListItem item)
    {
        return _selectedChat is not null &&
            (_selectedChat.ContainsStableId(item.StableId) || item.ContainsStableId(_selectedChat.StableId));
    }

    private void RefreshMessageCapabilities()
    {
        _shell.Conversation.SetCapabilities(_capabilities);
        _shell.Conversation.RefreshAttachmentState(HasFailedAttachmentDownload);
    }

    private void OnMessageListLoaded(object sender, RoutedEventArgs e)
    {
        if (_messageScrollViewer is not null)
        {
            return;
        }

        _messageScrollViewer = FindVisualChild<ScrollViewer>(MessageListView);
        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ViewChanged += OnMessageScrollChanged;
        }
    }

    private async void OnMessageScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_selectedChat is null ||
            _messageScrollViewer is null ||
            _programmaticMessageScroll ||
            _scrollToLatestAfterLayout)
        {
            return;
        }

        if (_messageScrollViewer.VerticalOffset <= 48 && CanLoadOlderVisibleMessages)
        {
            _programmaticMessageScroll = true;
            try
            {
                await LoadOlderVisibleMessagesAsync();
            }
            finally
            {
                _programmaticMessageScroll = false;
            }
        }

        var atLatest = IsMessageListAtLatest();
        JumpToLatestButton.Visibility = atLatest ? Visibility.Collapsed : Visibility.Visible;
        SetChatPinnedAwayFromLatest(_selectedChat.StableId, !atLatest);
    }

    private void ScrollMessagesToLatest(bool forceLatest = false)
    {
        if (_shell.Conversation.Messages.Count == 0)
        {
            JumpToLatestButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!forceLatest &&
            _selectedChat is not null &&
            _appSettings.ChatsPinnedAwayFromLatest.Contains(_selectedChat.StableId, StringComparer.OrdinalIgnoreCase))
        {
            JumpToLatestButton.Visibility = Visibility.Visible;
            return;
        }

        _scrollToLatestAfterLayout = true;
        RunProgrammaticMessageScroll(ScrollMessagesToLatestOnce);
    }

    private void ScrollMessagesToLatestOnce()
    {
        if (_shell.Conversation.Messages.Count == 0)
        {
            JumpToLatestButton.Visibility = Visibility.Collapsed;
            return;
        }

        MessageListView.ScrollIntoView(_shell.Conversation.Messages[^1]);
        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ChangeView(null, _messageScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }

        JumpToLatestButton.Visibility = Visibility.Collapsed;
    }

    private void OnMessageListLayoutUpdated(object? sender, object e)
    {
        if (!_scrollToLatestAfterLayout)
        {
            return;
        }

        _scrollToLatestAfterLayout = false;
        RunProgrammaticMessageScroll(ScrollMessagesToLatestOnce);
    }

    private void RunProgrammaticMessageScroll(Action action)
    {
        _programmaticMessageScroll = true;
        try
        {
            action();
        }
        finally
        {
            _programmaticMessageScroll = false;
        }
    }

    private bool IsMessageListAtLatest()
    {
        return _messageScrollViewer is null ||
            _messageScrollViewer.ScrollableHeight <= 0 ||
            _messageScrollViewer.VerticalOffset >= _messageScrollViewer.ScrollableHeight - 24;
    }

    private void SetChatPinnedAwayFromLatest(string stableId, bool isPinned)
    {
        if (_selectedChat is not null &&
            string.Equals(_selectedChat.StableId, stableId, StringComparison.OrdinalIgnoreCase))
        {
            _shell.Conversation.IsPinnedAwayFromLatest = isPinned;
        }

        var pinned = _appSettings.ChatsPinnedAwayFromLatest.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = isPinned ? pinned.Add(stableId) : pinned.Remove(stableId);
        if (!changed)
        {
            return;
        }

        _appSettings = _appSettings with { ChatsPinnedAwayFromLatest = pinned.ToList() };
        _appSettings = _appSettings.Normalize();
        _settingsStore.Save(_appSettings);
    }

    private void SaveCurrentSettingsContext(bool refreshPickers = true)
    {
        if (ProfileDetailPanel.Visibility == Visibility.Visible && _shell.SettingsView.IsProfileDirty)
        {
            SaveProfileFromForm(refreshPickers);
        }
        else if (_shell.SettingsView.IsGeneralDirty)
        {
            SaveGeneralSettings(refreshPickers);
        }
    }

    private void SaveGeneralSettings(bool refreshPickers = true)
    {
        _appSettings = _shell.SettingsView.SaveGeneral(
            AutoDownloadAttachmentsBox.IsChecked == true,
            StartWithWindowsBox.IsChecked == true,
            MinimizeToTrayBox.IsChecked == true,
            EnableNotificationsBox.IsChecked == true,
            ShowNotificationContentBox.IsChecked == true,
            BackgroundCacheSyncBox.IsChecked == true,
            MergeChatsByParticipantsBox.IsChecked == true,
            SelectedPhoneNumberRegion(),
            SendTypingIndicatorsBox.IsChecked == true,
            EnableWebCompanionBox.IsChecked == true);
        SaveAppSettings();
        StartupRegistration.Apply(_appSettings.StartWithWindows);
        ApplyWebCompanionState();
        SetGeneralSettingsDirty(false);

        if (refreshPickers)
        {
            LoadSettingsIntoForm();
            RefreshProfilePickers();
        }
    }

    private void SaveProfileFromForm(bool refreshPickers = true, bool makeActive = false)
    {
        var profile = BuildProfileFromForm();
        _appSettings = _shell.SettingsView.SaveProfile(profile, makeActive);
        SaveAppSettings();
        SetProfileSettingsDirty(false);

        if (refreshPickers)
        {
            LoadSettingsIntoForm();
            RefreshProfilePickers();
        }
    }

    private void SaveAppSettings()
    {
        _appSettings = _appSettings.Normalize();
        _settings = PreserveConnectedTargetAddress(_appSettings.ToBridgeSettings());
        _settingsStore.Save(_appSettings);
        _shell.LoadSettings(_appSettings);
        ApplyCapabilityState();
    }

    private ImsgBridgeSettings PreserveConnectedTargetAddress(ImsgBridgeSettings nextSettings)
    {
        if (!_bridge.IsConnected || string.IsNullOrWhiteSpace(_settings.TargetAddress))
        {
            return nextSettings;
        }

        return nextSettings.CandidateAddresses.Contains(_settings.TargetAddress, StringComparer.OrdinalIgnoreCase)
            ? nextSettings.ForTargetAddress(_settings.TargetAddress)
            : nextSettings;
    }

    private static bool ProfilesMatch(ImsgBridgeSettings left, ImsgBridgeSettings right)
    {
        var normalizedLeft = left.Normalize();
        var normalizedRight = right.Normalize();
        return string.Equals(normalizedLeft.TargetAddress, normalizedRight.TargetAddress, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedLeft.MacUser, normalizedRight.MacUser, StringComparison.Ordinal) &&
            normalizedLeft.SshPort == normalizedRight.SshPort &&
            string.Equals(normalizedLeft.IdentityFile, normalizedRight.IdentityFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedLeft.ImsgPath, normalizedRight.ImsgPath, StringComparison.Ordinal);
    }

    private ImsgBridgeProfile BuildProfileFromForm()
    {
        var existing = _appSettings.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _loadedSettingsProfileId, StringComparison.OrdinalIgnoreCase))
            ?? _appSettings.ActiveProfile;

        return existing with
        {
            Name = ProfileNameBox.Text,
            TargetAddresses = [PrimaryTargetAddressBox.Text, .. BridgeTargetAddressList.ParseMultiline(FallbackTargetAddressesBox.Text)],
            MacUser = UserBox.Text,
            SshPort = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value,
            IdentityFile = IdentityFileBox.Text,
            RemoteAccessMode = SelectedRemoteAccessMode(),
            ImsgPath = ImsgPathBox.Text,
            RemoteAttachmentRoot = RemoteAttachmentRootBox.Text
        };
    }

    private void LoadSettingsIntoForm()
    {
        var profile = _appSettings.ActiveProfile;
        _loadingSettingsForm = true;
        try
        {
            SetProfileFormFields(profile);
            AutoDownloadAttachmentsBox.IsChecked = _appSettings.AutoDownloadAttachments;
            StartWithWindowsBox.IsChecked = _appSettings.StartWithWindows;
            MinimizeToTrayBox.IsChecked = _appSettings.MinimizeToTray;
            EnableNotificationsBox.IsChecked = _appSettings.EnableNotifications;
            ShowNotificationContentBox.IsChecked = _appSettings.ShowMessageContentInNotifications;
            SendTypingIndicatorsBox.IsChecked = _appSettings.SendTypingIndicators;
            EnableWebCompanionBox.IsChecked = _appSettings.EnableWebCompanion;
            UpdateWebCompanionStatusText();
            BackgroundCacheSyncBox.IsChecked = _appSettings.SyncCacheInBackground;
            MergeChatsByParticipantsBox.IsChecked = _appSettings.MergeChatsByParticipants;
            SelectPhoneNumberRegion(_appSettings.PhoneNumberRegion);
            UpdateSettingsStatusText();
            _settingsFormInitialized = true;
        }
        finally
        {
            _loadingSettingsForm = false;
            SetGeneralSettingsDirty(false);
            SetProfileSettingsDirty(false);
        }
    }

    private void InitializePhoneRegionPicker()
    {
        PhoneNumberRegionBox.ItemsSource = BuildPhoneRegionOptions();
        PhoneNumberRegionBox.SelectedValue = "AUTO";
    }

    private void InitializeRemoteAccessModePicker()
    {
        RemoteAccessModeBox.ItemsSource = RemoteAccessModelService.Options;
        RemoteAccessModeBox.SelectedValue = RemoteAccessModes.LocalNetworkSsh;
    }

    private static IReadOnlyList<PhoneRegionOption> BuildPhoneRegionOptions()
    {
        var phoneUtil = PhoneNumberUtil.GetInstance();
        var regions = new Dictionary<string, PhoneRegionOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var supportedRegion in phoneUtil.GetSupportedRegions())
        {
            var code = supportedRegion?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code) || code.Length != 2 || !code.All(char.IsLetter))
            {
                continue;
            }

            var countryCode = phoneUtil.GetCountryCodeForRegion(code);
            if (countryCode <= 0)
            {
                continue;
            }

            regions[code] = new PhoneRegionOption(code, RegionName(code), countryCode);
        }

        return new[] { new PhoneRegionOption("AUTO", "Auto detect", null) }
            .Concat(regions.Values
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(option => option.Code, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string RegionName(string code)
    {
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    private string SelectedPhoneNumberRegion()
    {
        return PhoneNumberRegionBox.SelectedValue is string code && !string.IsNullOrWhiteSpace(code)
            ? code
            : "AUTO";
    }

    private void SelectPhoneNumberRegion(string? region)
    {
        var normalized = string.IsNullOrWhiteSpace(region) ? "AUTO" : region.Trim().ToUpperInvariant();
        var options = PhoneNumberRegionBox.ItemsSource as IReadOnlyList<PhoneRegionOption> ?? [];
        PhoneNumberRegionBox.SelectedValue = options.Any(option => string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : "AUTO";
    }

    private string SelectedRemoteAccessMode()
    {
        return RemoteAccessModeBox.SelectedValue is string code && !string.IsNullOrWhiteSpace(code)
            ? RemoteAccessModes.Normalize(code)
            : RemoteAccessModes.LocalNetworkSsh;
    }

    private void SelectRemoteAccessMode(string? mode)
    {
        var normalized = RemoteAccessModes.Normalize(mode);
        var options = RemoteAccessModeBox.ItemsSource as IReadOnlyList<RemoteAccessModelOption> ?? [];
        RemoteAccessModeBox.SelectedValue = options.Any(option => string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : RemoteAccessModes.LocalNetworkSsh;
    }

    private void UpdateRemoteAccessModelStatusFromForm()
    {
        UpdateRemoteAccessModelStatus(BuildProfileFromForm());
    }

    private void UpdateRemoteAccessModelStatus(ImsgBridgeProfile profile)
    {
        RemoteAccessStatusTextBlock.Text = RemoteAccessModelService.BuildSettingsSummary(profile);
    }

    private void RefreshProfilePickers(bool includeSettingsList = true)
    {
        _refreshingProfiles = true;
        var profiles = _appSettings.Profiles.ToList();
        ProfilePicker.ItemsSource = null;
        ProfilePicker.ItemsSource = profiles;
        ProfilePicker.SelectedValue = _appSettings.ActiveProfileId;
        ProfilePicker.Visibility = profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (includeSettingsList)
        {
            ProfileListView.ItemsSource = null;
            ProfileListView.ItemsSource = profiles;
            ProfileListView.SelectedItem = profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, _loadedSettingsProfileId, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault(profile => string.Equals(profile.Id, _appSettings.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
            DeleteProfileButton.IsEnabled = profiles.Count > 1;
        }

        _refreshingProfiles = false;
    }

    private void EnsureSettingsFormInitialized()
    {
        if (_settingsFormInitialized)
        {
            return;
        }

        var started = Stopwatch.StartNew();
        LoadSettingsIntoForm();
        RefreshProfilePickers();
        _log.Info($"Settings form initialized on demand. elapsedMs={started.ElapsedMilliseconds}");
    }

    private async Task SwitchProfileAsync(string profileId, bool reconnectIfConnected)
    {
        if (string.Equals(profileId, _appSettings.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var shouldReconnect = reconnectIfConnected && _connectionService.IsConnected;
        if (shouldReconnect)
        {
            await DisconnectActiveBridgeAsync();
        }

        SaveCurrentSettingsContext(refreshPickers: false);
        _appSettings = _shell.SettingsView.SelectActiveProfile(profileId);
        SaveAppSettings();
        LoadSettingsIntoForm();
        RefreshProfilePickers();
        _selectedChat = null;
        _shell.Conversation.ClearSelection();
        JumpToLatestButton.Visibility = Visibility.Collapsed;
        SetStatus($"Profile selected: {_appSettings.ActiveProfile.Name}.");

        if (shouldReconnect)
        {
            await ConnectAsync(saveSettings: false);
        }
    }

    private void ApplyCapabilityState()
    {
        var connected = _bridge.IsConnected;
        var canSend = CapabilityActionPolicy.CanSend(
            _selectedChat is not null || _shell.Conversation.IsNewMessageDraft,
            connected,
            _capabilities);
        var canSendAttachment = CapabilityActionPolicy.CanSendAttachment(_selectedChat is not null, connected, _capabilities);
        _shell.ConnectionState = connected ? ConnectionState.Connected : ConnectionState.Disconnected;
        _shell.SetCapabilities(_capabilities);

        RefreshButton.IsEnabled = connected || _settings.IsConfigured;
        ChatListPane.CanCreateNewChat = connected;
        ComposeBar.SetActionState(canSend, canSendAttachment);
        ComposeBar.SetAdvancedComposeState(
            CapabilityActionPolicy.CanSendRich(_selectedChat is not null, connected, _capabilities),
            CapabilityActionPolicy.CanSendPoll(_selectedChat is not null, connected, _capabilities));
        SyncCacheButton.IsEnabled = connected || _cacheSyncCancellation is not null;
        PromptMacPermissionsButton.IsEnabled = _settings.IsConfigured;
        ApplyConversationActionState();
        RefreshMessageCapabilities();
        UpdateInlineStatus();
        UpdateSettingsStatusText();
    }

    private void ApplyConversationActionState()
    {
        var connected = _bridge.IsConnected;
        var hasSelectedChat = _selectedChat is not null;
        var hasGroupGuid = HasSelectedGroupGuid();

        ChatActionsButton.IsEnabled = hasSelectedChat && connected;
        MarkReadMenuItem.IsEnabled = CapabilityActionPolicy.CanMarkRead(hasSelectedChat, connected, _capabilities);
        MarkUnreadMenuItem.IsEnabled = CapabilityActionPolicy.CanMarkUnread(hasSelectedChat, connected, _capabilities);
        DeleteChatMenuItem.IsEnabled = CapabilityActionPolicy.CanDeleteChat(hasSelectedChat, connected, _capabilities);
        FaceTimeLinkMenuItem.IsEnabled = CapabilityActionPolicy.CanCreateFaceTimeLink(
            hasSelectedChat,
            connected,
            _faceTimeLinkAvailable);
        RenameGroupMenuItem.IsEnabled = CapabilityActionPolicy.CanRenameGroup(hasGroupGuid, connected, _capabilities);
        SetGroupIconMenuItem.IsEnabled = CapabilityActionPolicy.CanSetGroupIcon(hasGroupGuid, connected, _capabilities);
        ClearGroupIconMenuItem.IsEnabled = CapabilityActionPolicy.CanSetGroupIcon(hasGroupGuid, connected, _capabilities);
        AddParticipantMenuItem.IsEnabled = CapabilityActionPolicy.CanAddParticipant(hasGroupGuid, connected, _capabilities);
        RemoveParticipantMenuItem.IsEnabled = CapabilityActionPolicy.CanRemoveParticipant(hasGroupGuid, connected, _capabilities);
        LeaveGroupMenuItem.IsEnabled = CapabilityActionPolicy.CanLeaveGroup(hasGroupGuid, connected, _capabilities);
    }

    private async Task RefreshFaceTimeLinkAvailabilityAsync()
    {
        _faceTimeLinkAvailable = false;
        ApplyConversationActionState();
        if (!_bridge.IsConnected || !_settings.IsConfigured)
        {
            _log.Info("FaceTime link creation is unavailable until the active Mac profile is connected.");
            return;
        }

        var probedSettings = _settings;
        using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
        try
        {
            var available = await _macHostActions.CheckFaceTimeLinkAvailabilityAsync(probedSettings, timeout.Token);
            if (!ProfilesMatch(probedSettings, _settings))
            {
                return;
            }

            _faceTimeLinkAvailable = available;
            _log.Info(available
                ? "FaceTime link creation probe passed for the active Mac profile."
                : "FaceTime link creation probe failed for the active Mac profile.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !timeout.IsCancellationRequested)
        {
            _faceTimeLinkAvailable = false;
            _log.Warning($"FaceTime link availability probe failed. {ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _faceTimeLinkAvailable = false;
            _log.Warning($"FaceTime link availability probe timed out after {FormatDuration(_settings.RequestTimeout)}.");
        }
        finally
        {
            ApplyConversationActionState();
        }
    }

    private async Task MarkSelectedChatReadAsync()
    {
        if (_selectedChat is null)
        {
            return;
        }

        await MarkChatReadAsync(_selectedChat);
    }

    private async Task MarkChatReadAsync(ChatListItem chat)
    {
        if (chat.IsNewMessageDraft)
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before marking a conversation read.");
            return;
        }

        MarkChatReadLocally(chat);
        await RunUiActionAsync("Marking conversation read...", async token =>
        {
            await MarkChatReadOnBridgeAsync(chat, token);
            await RefreshChatsAsync(token);
        });
    }

    private async Task MarkChatsReadAsync(IReadOnlyList<ChatListItem> chats)
    {
        var targets = chats.Where(static chat => !chat.IsNewMessageDraft).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before marking conversations read.");
            return;
        }

        foreach (var chat in targets)
        {
            MarkChatReadLocally(chat);
        }

        var label = targets.Count == 1
            ? "Marking conversation read..."
            : $"Marking {targets.Count} conversations read...";
        await RunUiActionAsync(label, async token =>
        {
            foreach (var chat in targets)
            {
                await MarkChatReadOnBridgeAsync(chat, token);
            }

            await RefreshChatsAsync(token);
        });
    }

    private ChatListItem MarkChatReadLocally(ChatListItem chat)
    {
        var wasSelected = _selectedChat is not null && _selectedChat.ContainsStableId(chat.StableId);
        var wasListSelected = ChatListPane.SelectedItem is ChatListItem selectedItem && selectedItem.ContainsStableId(chat.StableId);
        ClearTransientUnreadForChat(chat);
        if (!chat.HasUnread)
        {
            return chat;
        }

        var readChat = chat.WithUnreadCleared();
        _shell.Conversations.ReplaceChat(chat, readChat);
        if (wasSelected)
        {
            _selectedChat = readChat;
            _shell.Conversations.SelectedChat = readChat;
        }

        if (wasListSelected)
        {
            ChatListPane.SelectedItem = readChat;
        }

        UpdateUnreadBadges();
        return readChat;
    }

    private async Task MarkChatReadOnBridgeAsync(ChatListItem chat, bool refreshChats)
    {
        if (chat.IsNewMessageDraft ||
            !_bridge.IsConnected ||
            !CapabilityActionPolicy.CanMarkRead(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
            await MarkChatReadOnBridgeAsync(chat, timeout.Token);
            if (refreshChats)
            {
                await RefreshChatsAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning($"Background mark-read failed. chatIdPresent={chat.Chat.Id.HasValue}; error={ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _log.Warning($"Background mark-read timed out after {FormatDuration(_settings.RequestTimeout)}. chatIdPresent={chat.Chat.Id.HasValue}");
        }
    }

    private async Task MarkChatReadOnBridgeAsync(ChatListItem chat, CancellationToken cancellationToken)
    {
        foreach (var sourceChat in chat.Chats.Where(HasChatTarget))
        {
            await _nicetyChannel.MarkReadAsync(sourceChat.Id, sourceChat.Identifier, messageGuid: null, sourceChat.Guid, cancellationToken);
        }
    }

    private async Task MarkSelectedChatUnreadAsync()
    {
        if (_selectedChat is null)
        {
            return;
        }

        await MarkChatUnreadAsync(_selectedChat);
    }

    private async Task MarkChatUnreadAsync(ChatListItem chat)
    {
        if (chat.IsNewMessageDraft)
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before marking a conversation unread.");
            return;
        }

        if (!CapabilityActionPolicy.CanMarkUnread(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Mark unread requires imsg chats.markUnread support.");
            return;
        }

        await RunUiActionAsync("Marking conversation unread...", async token =>
        {
            foreach (var sourceChat in chat.Chats.Where(HasChatTarget))
            {
                await _nicetyChannel.MarkUnreadAsync(sourceChat.Id, sourceChat.Identifier, sourceChat.Guid, token);
            }

            await RefreshChatsAsync(token);
        });
    }

    private async Task DeleteSelectedChatAsync()
    {
        if (_selectedChat is not { } chat || chat.IsNewMessageDraft)
        {
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetStatus("Connect before deleting a conversation.");
            return;
        }

        if (!CapabilityActionPolicy.CanDeleteChat(hasSelectedChat: true, connected: true, capabilities: _capabilities))
        {
            SetStatus("Delete conversation requires imsg chats.delete support.");
            return;
        }

        if (!await ConfirmAsync("Delete Conversation", $"Delete \"{chat.DisplayName}\" from Messages.app on the Mac?"))
        {
            return;
        }

        var targets = chat.Chats.Where(HasChatTarget).ToList();
        if (targets.Count == 0)
        {
            SetStatus("The selected conversation does not expose a deletable chat target.");
            return;
        }

        await RunUiActionAsync("Deleting conversation...", async token =>
        {
            foreach (var sourceChat in targets)
            {
                await _actionChannel.DeleteChatAsync(sourceChat.Id, sourceChat.Identifier, sourceChat.Guid, token);
            }

            await RefreshChatsAsync(token);
            _selectedChat = null;
            _shell.Conversations.SelectedChat = null;
            _shell.Conversation.ClearSelection();
            ChatListPane.SelectedItem = null;
            JumpToLatestButton.Visibility = Visibility.Collapsed;
        }, CompositeBridgeTimeout(targets.Count + 1));
    }

    private async Task CreateFaceTimeLinkForSelectedChatAsync()
    {
        if (_selectedChat is null)
        {
            return;
        }

        if (!_faceTimeLinkAvailable)
        {
            SetStatus("FaceTime link creation is unavailable for this Mac profile. Run setup checklist and confirm FaceTime/System Events automation on the Mac.");
            return;
        }

        string? link = null;
        var completed = false;
        await RunUiActionAsync("Creating FaceTime link...", async token =>
        {
            link = await _macHostActions.CreateFaceTimeLinkAsync(_settings, token);
            completed = true;
            _faceTimeLinkAvailable = true;
        }, CompositeBridgeTimeout(2));

        if (!completed)
        {
            _faceTimeLinkAvailable = false;
            ApplyConversationActionState();
        }

        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        var currentText = ComposeBar.Text.TrimEnd();
        ComposeBar.Text = currentText.Length == 0
            ? link
            : $"{currentText}{Environment.NewLine}{link}";
        ComposeBar.FocusComposer();
        SetStatus("FaceTime link added to the message box.");
    }

    private async Task RenameSelectedGroupAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        var name = await PromptTextAsync("Rename Group", "Name", _selectedChat?.DisplayName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunUiActionAsync("Renaming group...", async token =>
        {
            await _actionChannel.RenameGroupAsync(chatGuid, name, token);
            await RefreshChatsAsync(token);
        });
    }

    private async Task SetSelectedGroupIconAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        if (!CapabilityActionPolicy.CanSetGroupIcon(hasGroupGuid: true, connected: _bridge.IsConnected, capabilities: _capabilities))
        {
            SetStatus("Set group icon requires imsg group.setIcon support.");
            return;
        }

        var localPath = await _filePicker.PickSingleFileAsync();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        if (AttachmentPresentationPolicy.ClassifyLocalFile(localPath) != AttachmentPresentationKind.Image)
        {
            SetStatus("Group icon must be an image file.");
            return;
        }

        await RunUiActionAsync("Setting group icon...", async token =>
        {
            var remotePath = await _attachmentService.UploadAsync(_settings, localPath, cancellationToken: token);
            await _actionChannel.SetGroupIconAsync(chatGuid, remotePath, token);
            await RefreshChatsAsync(token);
        }, CompositeBridgeTimeout(2));
    }

    private async Task ClearSelectedGroupIconAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        if (!CapabilityActionPolicy.CanSetGroupIcon(hasGroupGuid: true, connected: _bridge.IsConnected, capabilities: _capabilities))
        {
            SetStatus("Clear group icon requires imsg group.setIcon support.");
            return;
        }

        if (!await ConfirmAsync("Clear Group Icon", "Clear the selected group's icon?"))
        {
            return;
        }

        await RunUiActionAsync("Clearing group icon...", async token =>
        {
            await _actionChannel.SetGroupIconAsync(chatGuid, remoteFilePath: null, cancellationToken: token);
            await RefreshChatsAsync(token);
        });
    }

    private async Task AddParticipantToSelectedGroupAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        var address = await PromptTextAsync("Add Participant", "Phone number or email", string.Empty);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await RunUiActionAsync("Adding participant...", async token =>
        {
            await _actionChannel.AddParticipantAsync(chatGuid, address, token);
            await RefreshChatsAsync(token);
        });
    }

    private async Task RemoveParticipantFromSelectedGroupAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        var address = await PromptTextAsync("Remove Participant", "Phone number or email", string.Empty);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await RunUiActionAsync("Removing participant...", async token =>
        {
            await _actionChannel.RemoveParticipantAsync(chatGuid, address, token);
            await RefreshChatsAsync(token);
        });
    }

    private async Task LeaveSelectedGroupAsync()
    {
        var chatGuid = SelectedGroupGuid();
        if (chatGuid is null)
        {
            SetStatus("The selected conversation does not expose a group GUID.");
            return;
        }

        if (!await ConfirmAsync("Leave Group", "Leave the selected group conversation?"))
        {
            return;
        }

        await RunUiActionAsync("Leaving group...", async token =>
        {
            await _actionChannel.LeaveGroupAsync(chatGuid, token);
            await RefreshChatsAsync(token);
        });
    }

    private bool HasSelectedGroupGuid()
    {
        return SelectedGroupGuid() is not null;
    }

    private string? SelectedGroupGuid()
    {
        if (_selectedChat?.Chat is not { } chat || !chat.IsGroup || string.IsNullOrWhiteSpace(chat.Guid))
        {
            return null;
        }

        return chat.Guid;
    }

    private static bool HasChatTarget(ImsgChat chat) =>
        chat.Id is not null ||
        !string.IsNullOrWhiteSpace(chat.Identifier) ||
        !string.IsNullOrWhiteSpace(chat.Guid);

    private async Task SendTapbackAsync(MessageListItem message, TapbackChoice? choice = null)
    {
        var messageActionId = message.Message.MessageActionId;
        var chat = _selectedChat;
        if (chat is null || string.IsNullOrWhiteSpace(messageActionId))
        {
            return;
        }

        if (!CapabilityActionPolicy.CanTapback(message.Message, _capabilities))
        {
            SetStatus("Tapbacks require an actionable message GUID and imsg tapback support.");
            return;
        }

        var tapback = choice ?? await PromptTapbackAsync();
        if (tapback is null)
        {
            return;
        }

        var reactionKind = TapbackReactionPolicy.NormalizeKind(tapback.Value.Reaction);
        if (string.IsNullOrWhiteSpace(reactionKind))
        {
            SetStatus("Unsupported tapback reaction. Use love, like, dislike, laugh, emphasize, or question.");
            return;
        }

        string? actionStatus = null;
        await RunUiActionAsync("Sending tapback...", async token =>
        {
            actionStatus = await _messageActionWorkflowService.SendTapbackAsync(
                chat,
                message.Message,
                tapback.Value,
                _capabilities,
                token);
            await RefreshHistoryAsync(chat, token);
        });
        if (!string.IsNullOrWhiteSpace(actionStatus))
        {
            SetStatus(actionStatus);
        }
    }

    private async Task EditMessageAsync(MessageListItem message)
    {
        var messageActionId = message.Message.MessageActionId;
        var chat = _selectedChat;
        if (chat is null || string.IsNullOrWhiteSpace(messageActionId))
        {
            return;
        }

        if (!CapabilityActionPolicy.CanEdit(message.Message, _capabilities))
        {
            SetStatus("Only sent, non-reaction messages with an upstream GUID can be edited.");
            return;
        }

        var text = await PromptTextAsync("Edit Message", "Text", ComposeBar.Text.Trim().Length == 0 ? message.Text : ComposeBar.Text.Trim());
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunUiActionAsync("Editing message...", async token =>
        {
            await _messageActionWorkflowService.EditMessageAsync(
                chat,
                message.Message,
                text,
                _capabilities,
                token);
            if (IsSelectedChat(chat))
            {
                await RefreshHistoryAsync(chat, token);
            }
        });
    }

    private async Task UnsendMessageAsync(MessageListItem message)
    {
        var messageActionId = message.Message.MessageActionId;
        var chat = _selectedChat;
        if (chat is null || string.IsNullOrWhiteSpace(messageActionId))
        {
            return;
        }

        if (!CapabilityActionPolicy.CanUnsend(message.Message, _capabilities))
        {
            SetStatus("Only sent, non-reaction messages with an upstream GUID can be unsent.");
            return;
        }

        if (!await ConfirmAsync("Unsend Message", "Unsend the selected message?"))
        {
            return;
        }

        await RunUiActionAsync("Unsending message...", async token =>
        {
            await _messageActionWorkflowService.UnsendMessageAsync(
                chat,
                message.Message,
                _capabilities,
                token);
            if (IsSelectedChat(chat))
            {
                await RefreshHistoryAsync(chat, token);
            }
        });
    }

    private async Task DeleteMessageAsync(MessageListItem message)
    {
        var messageActionId = message.Message.MessageActionId;
        var chat = _selectedChat;
        if (chat is null || string.IsNullOrWhiteSpace(messageActionId))
        {
            return;
        }

        if (!CapabilityActionPolicy.CanDelete(message.Message, _capabilities))
        {
            SetStatus("Delete requires an actionable non-reaction message GUID and imsg delete support.");
            return;
        }

        if (!await ConfirmAsync("Delete Message", "Delete the selected message locally on the Mac?"))
        {
            return;
        }

        await RunUiActionAsync("Deleting message...", async token =>
        {
            await _messageActionWorkflowService.DeleteMessageAsync(
                chat,
                message.Message,
                _capabilities,
                token);
            if (IsSelectedChat(chat))
            {
                await RefreshHistoryAsync(chat, token);
            }
        });
    }

    private async Task NotifyAnywaysAsync(MessageListItem message)
    {
        var messageActionId = message.Message.MessageActionId;
        var chat = _selectedChat;
        if (chat is null || string.IsNullOrWhiteSpace(messageActionId))
        {
            return;
        }

        if (!CapabilityActionPolicy.CanNotifyAnyways(message.Message, _capabilities))
        {
            SetStatus("Notify Anyway requires a sent message with an upstream GUID and imsg notifyAnyways support.");
            return;
        }

        if (!await ConfirmAsync("Notify Anyway", "Send a Focus bypass notification for this message?"))
        {
            return;
        }

        string? actionStatus = null;
        await RunUiActionAsync("Sending Notify Anyway...", async token =>
        {
            actionStatus = await _messageActionWorkflowService.NotifyAnywaysAsync(
                chat,
                message.Message,
                _capabilities,
                token);
            if (IsSelectedChat(chat))
            {
                await RefreshHistoryAsync(chat, token);
            }
        });
        if (!string.IsNullOrWhiteSpace(actionStatus))
        {
            SetStatus(actionStatus);
        }
    }

    private async Task OpenAttachmentAsync(MessageListItem message)
    {
        if (message.PrimaryAttachment is not { } attachment)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        await OpenAttachmentAsync(attachment);
    }

    private async Task OpenAttachmentAsync(MessageAttachmentListItem attachment)
    {
        if (attachment.Message.Guid is null || !attachment.CanOpen)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        await RunUiActionAsync("Opening attachment...", async token =>
        {
            var localPath = await EnsureAttachmentDownloadedAsync(attachment.Message, attachment.Attachment, token);
            Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
        });
    }

    private async Task PreviewAttachmentAsync(MessageAttachmentListItem attachment)
    {
        if (attachment.Message.Guid is null || !attachment.CanOpen)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        var alreadyDownloaded = attachment.HasLocalFile;
        await RunUiActionAsync(attachment.HasLocalFile ? "Opening attachment preview..." : "Downloading attachment...", async token =>
        {
            var localPath = await EnsureAttachmentDownloadedAsync(attachment.Message, attachment.Attachment, token);
            attachment.RefreshLocalState();
            if (!alreadyDownloaded)
            {
                return;
            }

            ShowMediaOverlay(attachment, localPath);
        });

        if (!alreadyDownloaded)
        {
            SetStatus("Attachment downloaded. Click it again to preview.");
        }
    }

    private async Task DownloadAttachmentAsync(MessageListItem message)
    {
        if (message.PrimaryAttachment is not { } attachment || !attachment.HasRemotePath)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        await RunUiActionAsync("Downloading attachment...", async token =>
        {
            await EnsureAttachmentDownloadedAsync(attachment.Message, attachment.Attachment, token);
            attachment.RefreshLocalState();
        });
    }

    private async Task RevealAttachmentAsync(MessageListItem message)
    {
        if (message.PrimaryAttachment is not { } attachment)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        var localPath = _attachmentService.GetLocalPath(attachment.Message, attachment.Attachment);
        if (!File.Exists(localPath))
        {
            await DownloadAttachmentAsync(message);
        }

        if (File.Exists(localPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{localPath}\"") { UseShellExecute = true });
        }
    }

    private async Task RetryAttachmentDownloadAsync(MessageListItem message)
    {
        if (message.PrimaryAttachment is not { } attachment || !attachment.HasRemotePath)
        {
            SetStatus("Select a message with an attachment.");
            return;
        }

        var localPath = _attachmentService.GetLocalPath(attachment.Message, attachment.Attachment);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }

        await DownloadAttachmentAsync(message);
    }

    private void QueueAutoDownloadAttachments(IEnumerable<ImsgMessage> messages)
    {
        _attachmentService.QueueAutoDownload(
            _settings,
            messages,
            enabled: _appSettings.AutoDownloadAttachments,
            isConnected: _bridge.IsConnected,
            onFailure: ex => _log.Warning($"Attachment autodownload failed. {ex.GetType().Name}: {ex.Message}"));
    }

    private void QueueVisibleAutoDownloadAttachments()
    {
        QueueAutoDownloadAttachments(_shell.Conversation.Messages.Select(item => item.Message));
    }

    private async Task<string> EnsureAttachmentDownloadedAsync(ImsgMessage message, ImsgAttachment attachment, CancellationToken cancellationToken)
    {
        var localPath = _attachmentService.GetLocalPath(message, attachment);
        if (File.Exists(localPath))
        {
            if (!string.IsNullOrWhiteSpace(message.Guid) && !string.IsNullOrWhiteSpace(attachment.RemotePath))
            {
                _attachmentService.MarkDownloadSucceeded(message.Guid, attachment.RemotePath, forceNotify: true);
            }

            return localPath;
        }

        if (string.IsNullOrWhiteSpace(attachment.RemotePath))
        {
            throw new InvalidOperationException("Attachment does not expose a remote path.");
        }

        return await _attachmentService.DownloadAsync(_settings, message, attachment, cancellationToken: cancellationToken);
    }

    private void RefreshAttachmentDownloadState(string messageGuid)
    {
        _shell.Conversation.RefreshAttachmentState(messageGuid, HasFailedAttachmentDownload);
    }

    private void ShowMediaOverlay(MessageAttachmentListItem attachment, string localPath)
    {
        _mediaOverlayLocalPath = localPath;
        MediaOverlayTitle.Text = attachment.DisplayName;
        MediaOverlaySubtitle.Text = attachment.DetailText;
        MediaOverlayImage.Source = null;
        MediaOverlayImage.Visibility = Visibility.Collapsed;
        MediaOverlayPlayer.Source = null;
        MediaOverlayPlayer.Visibility = Visibility.Collapsed;
        MediaOverlayFilePanel.Visibility = Visibility.Collapsed;

        switch (AttachmentPresentationPolicy.Classify(attachment.Attachment, localPath))
        {
            case AttachmentPresentationKind.Image:
                MediaOverlayImage.Source = new BitmapImage(new Uri(localPath));
                MediaOverlayImage.Visibility = Visibility.Visible;
                break;
            case AttachmentPresentationKind.Audio:
                MediaOverlayPlayer.Height = 96;
                MediaOverlayPlayer.Source = MediaSource.CreateFromUri(new Uri(localPath));
                MediaOverlayPlayer.Visibility = Visibility.Visible;
                break;
            case AttachmentPresentationKind.Video:
                MediaOverlayPlayer.Height = 560;
                MediaOverlayPlayer.Source = MediaSource.CreateFromUri(new Uri(localPath));
                MediaOverlayPlayer.Visibility = Visibility.Visible;
                break;
            default:
                MediaOverlayFileText.Text = attachment.DisplayName;
                MediaOverlayFilePanel.Visibility = Visibility.Visible;
                break;
        }

        MediaOverlay.Visibility = Visibility.Visible;
    }

    private void CloseMediaOverlay()
    {
        MediaOverlayPlayer.Source = null;
        MediaOverlayImage.Source = null;
        MediaOverlay.Visibility = Visibility.Collapsed;
        _mediaOverlayLocalPath = null;
    }

    private void OnMediaOverlayCloseClicked(object sender, RoutedEventArgs e)
    {
        CloseMediaOverlay();
    }

    private void OnMediaOverlayBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        CloseMediaOverlay();
    }

    private void OnMediaOverlayContentTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnMediaOverlayOpenExternalClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_mediaOverlayLocalPath) && File.Exists(_mediaOverlayLocalPath))
        {
            Process.Start(new ProcessStartInfo(_mediaOverlayLocalPath) { UseShellExecute = true });
        }
    }

    private void ClearAttachmentsDirectory()
    {
        var attachmentsPath = Path.GetFullPath(_paths.Attachments);
        var rootPath = Path.GetFullPath(_paths.Root);
        if (!attachmentsPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attachment cache path is outside the app data root.");
        }

        if (Directory.Exists(attachmentsPath))
        {
            Directory.Delete(attachmentsPath, recursive: true);
        }

        Directory.CreateDirectory(attachmentsPath);
    }

    private async Task RunUiActionAsync(string busyStatus, Func<CancellationToken, Task> action, TimeSpan? actionTimeout = null)
    {
        SetStatus(busyStatus);
        _log.Info($"{busyStatus} started.");
        var effectiveTimeout = actionTimeout ?? _settings.RequestTimeout;
        using var timeout = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await action(timeout.Token);
            ApplyCapabilityState();
            SetStatus(_bridge.IsConnected ? "Connected" : "Ready");
            _log.Info($"{busyStatus} completed.");
        }
        catch (Exception ex)
        {
            _log.Error($"{busyStatus} failed.", ex);
            SetStatus(ex is OperationCanceledException
                ? $"{busyStatus.TrimEnd('.', ' ')} timed out after {FormatDuration(effectiveTimeout)}."
                : ex.Message);
        }
    }

    private TimeSpan CompositeBridgeTimeout(int operationCount)
    {
        var seconds = _settings.RequestTimeout.TotalSeconds * Math.Max(operationCount, 1);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 300));
    }

    private async Task DisconnectActiveBridgeAsync(CancellationToken cancellationToken = default)
    {
        _userDisconnectedBridge = true;
        _bridgeReconnectCancellation?.Cancel();
        await StopTypingIndicatorAsync();
        await ResetBridgeChannelsAsync();
        var progress = new Progress<BridgeConnectionProgress>(ApplyConnectionProgress);
        await _connectionService.DisconnectAsync(progress);
        _capabilities = new ImsgCapabilities();
        _faceTimeLinkAvailable = false;
        ApplyCapabilityState();
        SetConnectionStatus(ConnectionState.Disconnected, "Disconnected");
    }

    // Reading a conversation in the web companion clears its unread state the
    // same way opening it natively does: local clear immediately (badge/tray
    // update and SSE unread event), Mac-side read RPC in the background.
    private Task<bool> MarkChatReadFromCompanionAsync(string stableId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var chat = _shell.Conversations.AllChats.FirstOrDefault(candidate => candidate.ContainsStableId(stableId));
                    if (chat is null || (!chat.HasUnread && _chatListState.TransientUnreadMessageCount == 0))
                    {
                        completion.TrySetResult(false);
                        return;
                    }

                    var readChat = MarkChatReadLocally(chat);
                    _ = MarkChatReadOnBridgeAsync(readChat, refreshChats: false);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Companion mark-read failed. {ex.GetType().Name}: {ex.Message}");
                    completion.TrySetResult(false);
                }
            }))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private Task RefreshChatFromCompanionAsync(string stableId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var chat = _shell.Conversations.AllChats.FirstOrDefault(candidate => candidate.ContainsStableId(stableId));
                    if (chat is not null && _bridge.IsConnected)
                    {
                        await RefreshHistoryAsync(chat, CancellationToken.None);
                    }

                    await RefreshChatsAsync(CancellationToken.None);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Companion refresh failed. {ex.GetType().Name}: {ex.Message}");
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The WinUI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private void ApplyWebCompanionState()
    {
        try
        {
            if (!_appSettings.EnableWebCompanion)
            {
                _webCompanion.Stop();
                UpdateWebCompanionStatusText();
                return;
            }

            if (string.IsNullOrWhiteSpace(_appSettings.WebCompanionToken))
            {
                _appSettings = _appSettings with { WebCompanionToken = WebCompanionService.GenerateToken() };
                _settingsStore.Save(_appSettings);
                _shell.SettingsView.ReplaceSettings(_appSettings);
            }

            if (!_webCompanion.IsRunning || _webCompanion.Port != _appSettings.WebCompanionPort)
            {
                _webCompanion.Start(_appSettings.WebCompanionPort, _appSettings.WebCompanionToken!);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Web companion failed to start. {ex.GetType().Name}: {ex.Message}");
            SetStatus($"Web companion failed to start on port {_appSettings.WebCompanionPort}. See Diagnostics.");
        }

        UpdateWebCompanionStatusText();
    }

    private void UpdateWebCompanionStatusText()
    {
        if (WebCompanionStatusTextBlock is null)
        {
            return;
        }

        WebCompanionStatusTextBlock.Text = _webCompanion.IsRunning
            ? $"Running at {_webCompanion.CompanionUrl} - open in a browser or paste as the Ferdium service URL."
            : "Off. Enables a localhost-only, token-protected page for browsers and Ferdium.";
    }

    private async Task ResetBridgeChannelsAsync()
    {
        await _sendChannel.ResetAsync();
        await _actionChannel.ResetAsync();
        await _nicetyChannel.ResetAsync();
    }

    private async Task WarmUpSendChannelAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(_settings.RequestTimeout);
            await _sendChannel.WarmUpAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _log.Warning($"Send channel warm-up failed; it will dial on first send. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldAttemptBridgeRepair(ImsgCapabilities capabilities) =>
        CapabilityActionPolicy.ShouldAttemptBridgeRepair(capabilities);

    private async Task RepairAdvancedBridgeAsync()
    {
        try
        {
            SetStatus("Advanced bridge is offline; relaunching Messages on the Mac to repair it...");
            _log.Info("Advanced bridge not ready with SIP disabled; running imsg launch to re-inject the helper.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var runner = new SshCommandRunner();
            var launch = await runner.RunImsgCommandAsync(_settings, ["launch", "--json"], timeout.Token);
            if (!launch.Succeeded)
            {
                _log.Warning($"Bridge repair failed at imsg launch. {launch.ErrorSummary}");
                SetStatus("Advanced bridge repair failed; tapbacks and read receipts stay disabled. See Diagnostics.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
            var status = await runner.RunImsgCommandAsync(_settings, ["status", "--json"], timeout.Token);
            if (!status.Succeeded)
            {
                _log.Warning($"Bridge repair status re-probe failed. {status.ErrorSummary}");
                return;
            }

            var capabilities = System.Text.Json.JsonSerializer.Deserialize<ImsgCapabilities>(
                status.StandardOutput,
                WinIMsg.Core.Rpc.ImsgJson.Options);
            if (capabilities is null)
            {
                return;
            }

            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    _capabilities = capabilities;
                    ApplyCapabilityState();
                    SetStatus(capabilities.HasAdvancedBridge
                        ? "Advanced bridge repaired; tapbacks and read receipts are available."
                        : "Advanced bridge is still offline after repair. See Diagnostics.");
                    _log.Info($"Bridge repair completed. advanced={capabilities.HasAdvancedBridge}");
                }))
            {
                _log.Warning("Unable to apply bridge repair result on the UI dispatcher.");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Bridge repair failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildWatchCursorScope(string? profileId)
    {
        return string.IsNullOrWhiteSpace(profileId) ? "profile:default" : $"profile:{profileId.Trim()}";
    }

    private void ApplyConnectionProgress(BridgeConnectionProgress progress)
    {
        SetConnectionStatus(progress.State, progress.Message);
    }

    private void SetConnectionStatus(ConnectionState state, string message)
    {
        _shell.SetStatus(state, message);
        SetStatusVisual(message);
    }

    private void SetStatus(string message)
    {
        _shell.StatusText = message;
        SetStatusVisual(message);
    }

    private void SetStatusVisual(string message)
    {
        StatusTextBlock.Text = message;
        ToolTipService.SetToolTip(ConnectionStatusIcon, message);
        AddStatusTaskItem(message);

        var color = message.Contains("connected", StringComparison.OrdinalIgnoreCase)
            ? Colors.LimeGreen
            : message.Contains("checking", StringComparison.OrdinalIgnoreCase)
                || message.Contains("sending", StringComparison.OrdinalIgnoreCase)
                || message.Contains("uploading", StringComparison.OrdinalIgnoreCase)
                || message.Contains("downloading", StringComparison.OrdinalIgnoreCase)
                || message.Contains("sync", StringComparison.OrdinalIgnoreCase)
                    ? Colors.DodgerBlue
                    : message.Contains("unable", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("required", StringComparison.OrdinalIgnoreCase)
                            ? Colors.OrangeRed
                            : Colors.Gray;

        ConnectionStatusDot.Fill = new SolidColorBrush(color);
    }

    private void AddStatusTaskItem(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var text = $"{DateTime.Now:t}  {message.Trim()}";
        if (_statusTaskItems.Count > 0 && string.Equals(_statusTaskItems[0], text, StringComparison.Ordinal))
        {
            return;
        }

        _statusTaskItems.Insert(0, text);
        while (_statusTaskItems.Count > 20)
        {
            _statusTaskItems.RemoveAt(_statusTaskItems.Count - 1);
        }
    }

    private void SetSettingsVisible(bool isVisible)
    {
        _shell.IsSettingsVisible = isVisible;
    }

    private void SetMessageLoading(bool isLoading)
    {
        _shell.Conversation.IsLoading = isLoading;
    }

    private void SetTransientSyncBar(bool isVisible)
    {
        if (isVisible)
        {
            _transientSyncBarDepth++;
        }
        else
        {
            _transientSyncBarDepth = Math.Max(0, _transientSyncBarDepth - 1);
        }

        if (_cacheSyncCancellation is not null)
        {
            return;
        }

        var show = _transientSyncBarDepth > 0;
        CacheSyncProgressBar.IsIndeterminate = show;
        CacheSyncProgressBar.Value = 0;
        CacheSyncProgressBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CacheSyncInlineTextBlock.Visibility = Visibility.Collapsed;
        CacheSyncInlineTextBlock.Text = string.Empty;
    }

    private void ResetTransientSyncBar()
    {
        _transientSyncBarDepth = 0;
        if (_cacheSyncCancellation is not null)
        {
            return;
        }

        CacheSyncProgressBar.IsIndeterminate = false;
        CacheSyncProgressBar.Value = 0;
        CacheSyncProgressBar.Visibility = Visibility.Collapsed;
        CacheSyncInlineTextBlock.Visibility = Visibility.Collapsed;
        CacheSyncInlineTextBlock.Text = string.Empty;
    }

    public void DisposeTrayIcon()
    {
        _windowShell.DisposeTrayIcon(TrayIcon);
    }

    private readonly record struct TrayUnreadBadgeUpdate(int UnreadCount);

    private readonly record struct UnreadBadgeUpdate(nint WindowHandle, int UnreadCount);

    private void UpdateInlineStatus()
    {
        if (_shell.Conversation.IsNewMessageDraft)
        {
            SetInlineStatus(string.Empty);
            return;
        }

        if (_selectedChat is null)
        {
            SetInlineStatus("Select a conversation.");
            return;
        }

        if (!_bridge.IsConnected)
        {
            SetInlineStatus("Showing cached history.");
            return;
        }

        SetInlineStatus(string.Empty);
    }

    private void SetInlineStatus(string message)
    {
        _shell.Conversation.InlineStatus = message;
    }

    private void UpdateSettingsStatusText()
    {
        if (_cacheSyncCancellation is null)
        {
            CacheSyncStatusText.Text = _appSettings.SyncCacheInBackground
                ? "Background sync will refresh chat history after a successful connection."
                : "Use Sync now to refresh the local message cache without opening each chat.";
        }

        ContactNamesTextBlock.Text = _contactsSettingsStatusOverride ??
            "Contact names come from the Mac's Contacts through imsg. Use Check contact names for a full report and repair guidance.";

        SetupChecklistTextBlock.Text = _setupChecklistStatusOverride ??
            "Run setup checklist to verify every Windows and Mac prerequisite step by step, with each Mac command shown before it runs.";

        LogPathTextBlock.Text = _log.LogFilePath;

        AdvancedFeatureTextBlock.Text = _capabilities.HasAdvancedBridge
            ? "Advanced bridge enabled. Right-click a message for tapbacks, edit, unsend, and more."
            : "Tapbacks, edit, unsend, and read receipts need the Mac bridge. win-imsg repairs it automatically when the Mac is capable; the capability matrix below shows what this Mac supports.";

        CapabilityMatrixSummaryTextBlock.Text = CapabilityMatrixService.Summary(_capabilities);
        CapabilityMatrixItemsControl.ItemsSource = CapabilityMatrixService.Build(_capabilities);
    }

    private static MessageListItem? MenuMessage(object sender)
    {
        return sender is FrameworkElement { Tag: MessageListItem message } ? message : null;
    }

    private void SetContactsSettingsStatus(string message)
    {
        _contactsSettingsStatusOverride = message;
        ContactNamesTextBlock.Text = message;
    }

    private void SetSetupChecklistStatus(string message)
    {
        _setupChecklistStatusOverride = message;
        SetupChecklistTextBlock.Text = message;
    }

    private async Task<TapbackChoice?> PromptTapbackAsync()
    {
        return await _dialogs.PromptTapbackAsync();
    }

    private async Task<string?> PromptTextAsync(string title, string header, string? initialValue)
    {
        return await _dialogs.PromptTextAsync(title, header, initialValue);
    }

    private async Task<NewChatRequest?> PromptNewChatAsync()
    {
        return await _dialogs.PromptNewChatAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        return await _dialogs.ConfirmAsync(title, message);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private enum PendingSendRequestKind
    {
        Text,
        Attachment,
        NewDraft
    }

    private sealed record PendingSendRequest(
        PendingSendRequestKind Kind,
        string IdempotencyKey,
        ChatListItem? Chat,
        IReadOnlyList<string> Recipients,
        string Text,
        IReadOnlyList<string> AttachmentPaths,
        string? Effect,
        string? EffectLabel,
        string? ReplyTo,
        string? ReplySummary,
        IReadOnlyList<RichTextFormattingRange> TextFormatting,
        ImsgMessage? PendingMessage)
    {
        public static PendingSendRequest FromText(
            ChatListItem chat,
            string text,
            string? effect,
            string? effectLabel,
            string? replyTo,
            string? replySummary,
            IReadOnlyList<RichTextFormattingRange> textFormatting)
        {
            var canUseQueuedPendingMessage =
                string.IsNullOrWhiteSpace(effect) &&
                string.IsNullOrWhiteSpace(replyTo) &&
                textFormatting.Count == 0;
            return new(
                PendingSendRequestKind.Text,
                BuildIdempotencyKey(),
                chat,
                [],
                text,
                [],
                effect,
                effectLabel,
                replyTo,
                replySummary,
                textFormatting,
                canUseQueuedPendingMessage ? MessageSendWorkflowService.CreatePendingMessage(chat, text) : null);
        }

        public static PendingSendRequest FromAttachment(ChatListItem chat, IReadOnlyList<string> attachmentPaths, string text)
        {
            return new(
                PendingSendRequestKind.Attachment,
                BuildIdempotencyKey(),
                chat,
                [],
                text,
                attachmentPaths,
                null,
                null,
                null,
                null,
                [],
                null);
        }

        public static PendingSendRequest FromNewDraft(IReadOnlyList<string> recipients, string text)
        {
            return new(
                PendingSendRequestKind.NewDraft,
                BuildIdempotencyKey(),
                null,
                recipients,
                text,
                [],
                null,
                null,
                null,
                null,
                [],
                null);
        }

        private static string BuildIdempotencyKey() => Guid.NewGuid().ToString("N");
    }

    private sealed record PhoneRegionOption(string Code, string Name, int? CountryCallingCode)
    {
        public string Label => string.Equals(Code, "AUTO", StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name} (+{CountryCallingCode}, {Code})";
    }

}
