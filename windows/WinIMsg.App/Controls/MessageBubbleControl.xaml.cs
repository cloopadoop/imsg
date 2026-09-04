using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinIMsg.App.Services;
using WinIMsg.App.ViewModels;

namespace WinIMsg.App.Controls;

public sealed partial class MessageBubbleControl : UserControl
{
    private static readonly InputSystemCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private static readonly TapbackOption[] TapbackOptions =
    [
        new("love", "❤️", "Love"),
        new("like", "👍", "Like"),
        new("dislike", "👎", "Dislike"),
        new("laugh", "😂", "Laugh"),
        new("emphasize", "‼️", "Emphasize"),
        new("question", "❓", "Question")
    ];

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(MessageListItem),
        typeof(MessageBubbleControl),
        new PropertyMetadata(null));

    public MessageBubbleControl()
    {
        InitializeComponent();
    }

    public MessageListItem? Message
    {
        get => (MessageListItem?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public event EventHandler<TapbackRequestedEventArgs>? TapbackRequested;

    public event EventHandler<MessageListItem>? ReplyRequested;

    public event EventHandler<MessageListItem>? EditRequested;

    public event EventHandler<MessageListItem>? UnsendRequested;

    public event EventHandler<MessageListItem>? DeleteRequested;

    public event EventHandler<MessageListItem>? NotifyAnywaysRequested;

    public event EventHandler<MessageListItem>? RetrySendRequested;

    public event EventHandler<MessageListItem>? OpenAttachmentRequested;

    public event EventHandler<MessageListItem>? DownloadAttachmentRequested;

    public event EventHandler<MessageListItem>? RevealAttachmentRequested;

    public event EventHandler<MessageListItem>? RetryAttachmentDownloadRequested;

    public event EventHandler<MessageAttachmentListItem>? AttachmentOpenRequested;

    public event EventHandler<MessageLinkPreviewItem>? LinkOpenRequested;

    private void OnMessageFlyoutOpening(object sender, object e)
    {
        var message = Message;
        if (message is null)
        {
            return;
        }

        TapbackMenuItem.Visibility = message.MessageActionVisibility;
        TapbackMenuItem.IsEnabled = message.CanTapback;

        ReplyMenuItem.Visibility = message.ReplyVisibility;
        ReplyMenuItem.IsEnabled = message.CanReply;

        EditMenuItem.Visibility = message.SentMessageActionVisibility;
        EditMenuItem.IsEnabled = message.CanEdit;

        UnsendMenuItem.Visibility = message.SentMessageActionVisibility;
        UnsendMenuItem.IsEnabled = message.CanUnsend;

        NotifyAnywaysMenuItem.Visibility = message.SentMessageActionVisibility;
        NotifyAnywaysMenuItem.IsEnabled = message.CanNotifyAnyways;

        DeleteMenuItem.Visibility = message.MessageActionVisibility;
        DeleteMenuItem.IsEnabled = message.CanDelete;

        RetrySendMenuItem.Visibility = message.RetrySendVisibility;
        RetrySendMenuItem.IsEnabled = message.CanRetrySend;

        AttachmentMenuSeparator.Visibility = message.AttachmentMenuVisibility;

        OpenAttachmentMenuItem.Visibility = message.AttachmentMenuVisibility;
        OpenAttachmentMenuItem.IsEnabled = message.CanOpenAttachment;

        DownloadAttachmentMenuItem.Visibility = message.AttachmentDownloadVisibility;
        DownloadAttachmentMenuItem.IsEnabled = message.CanDownloadAttachment;

        RevealAttachmentMenuItem.Visibility = message.AttachmentRevealVisibility;
        RevealAttachmentMenuItem.IsEnabled = message.CanRevealAttachment;

        RetryAttachmentDownloadMenuItem.Visibility = message.RetryAttachmentDownloadVisibility;
        RetryAttachmentDownloadMenuItem.IsEnabled = message.CanRetryAttachmentDownload;
    }

    private void OnTapbackClicked(object sender, RoutedEventArgs e)
    {
        if (Message is not { } message)
        {
            return;
        }

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Top,
            ShowMode = FlyoutShowMode.Standard
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Thickness(6)
        };

        foreach (var option in TapbackOptions)
        {
            var isCurrent = string.Equals(
                message.CurrentUserTapbackReaction,
                option.Reaction,
                StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = option.Glyph,
                MinWidth = 42,
                MinHeight = 38,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(19),
                FontSize = 18,
                Background = isCurrent
                    ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212))
                    : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Foreground = isCurrent
                    ? new SolidColorBrush(Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                BorderBrush = isCurrent
                    ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212))
                    : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(isCurrent ? 2 : 1)
            };
            ToolTipService.SetToolTip(button, isCurrent ? $"Remove {option.Label}" : option.Label);
            button.Click += (_, _) =>
            {
                flyout.Hide();
                TapbackRequested?.Invoke(
                    this,
                    new TapbackRequestedEventArgs(message, new TapbackChoice(option.Reaction, isCurrent)));
            };
            panel.Children.Add(button);
        }

        flyout.Content = panel;
        flyout.ShowAt(this);
    }

    private void OnEditClicked(object sender, RoutedEventArgs e) => Raise(EditRequested);

    private void OnReplyClicked(object sender, RoutedEventArgs e) => Raise(ReplyRequested);

    private void OnUnsendClicked(object sender, RoutedEventArgs e) => Raise(UnsendRequested);

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => Raise(DeleteRequested);

    private void OnNotifyAnywaysClicked(object sender, RoutedEventArgs e) => Raise(NotifyAnywaysRequested);

    private void OnRetrySendClicked(object sender, RoutedEventArgs e) => Raise(RetrySendRequested);

    private void OnOpenAttachmentClicked(object sender, RoutedEventArgs e) => Raise(OpenAttachmentRequested);

    private void OnDownloadAttachmentClicked(object sender, RoutedEventArgs e) => Raise(DownloadAttachmentRequested);

    private void OnRevealAttachmentClicked(object sender, RoutedEventArgs e) => Raise(RevealAttachmentRequested);

    private void OnRetryAttachmentDownloadClicked(object sender, RoutedEventArgs e) => Raise(RetryAttachmentDownloadRequested);

    private readonly Dictionary<MediaPlayerElement, (MessageAttachmentListItem Item, PropertyChangedEventHandler Handler)> _mediaSubscriptions = new();

    private void OnAttachmentMediaLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaPlayerElement element)
        {
            BindAttachmentMedia(element);
        }
    }

    private void OnAttachmentMediaDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is MediaPlayerElement element && element.IsLoaded)
        {
            BindAttachmentMedia(element);
        }
    }

    // Destroying a MediaPlayerElement that still owns a MediaPlayer fail-fasts
    // inside Microsoft.UI.Xaml (stowed exception 0x8000ffff raised from
    // ~MediaPlayerElement/CloseMediaPlayer), which crashed the app whenever
    // recycled bubbles with media previews were discarded. The player must be
    // detached and disposed before teardown, so the media source is managed
    // here instead of through a XAML binding.
    private void OnAttachmentMediaUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaPlayerElement element)
        {
            return;
        }

        UnsubscribeAttachmentMedia(element);
        try
        {
            var player = element.MediaPlayer;
            element.Source = null;
            element.SetMediaPlayer(null);
            player?.Dispose();
        }
        catch
        {
            // Best-effort teardown; the element is leaving the visual tree.
        }
    }

    private void BindAttachmentMedia(MediaPlayerElement element)
    {
        var item = element.DataContext as MessageAttachmentListItem;
        if (_mediaSubscriptions.TryGetValue(element, out var existing))
        {
            if (ReferenceEquals(existing.Item, item))
            {
                ApplyAttachmentMedia(element, item);
                return;
            }

            UnsubscribeAttachmentMedia(element);
        }

        if (item is not null)
        {
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    args.PropertyName is nameof(MessageAttachmentListItem.MediaSource)
                        or nameof(MessageAttachmentListItem.MediaVisibility))
                {
                    ApplyAttachmentMedia(element, item);
                }
            };
            item.PropertyChanged += handler;
            _mediaSubscriptions[element] = (item, handler);
        }

        ApplyAttachmentMedia(element, item);
    }

    private static void ApplyAttachmentMedia(MediaPlayerElement element, MessageAttachmentListItem? item)
    {
        element.Source = item?.MediaSource;
    }

    private void UnsubscribeAttachmentMedia(MediaPlayerElement element)
    {
        if (_mediaSubscriptions.Remove(element, out var entry))
        {
            entry.Item.PropertyChanged -= entry.Handler;
        }
    }

    private sealed record VoiceChipParts(FontIcon Icon, TextBlock TimeText, ProgressBar Progress, MessageAttachmentListItem Item);

    private readonly Dictionary<string, VoiceChipParts> _voiceChips = new(StringComparer.OrdinalIgnoreCase);
    private bool _voicePlaybackHooked;

    private void OnVoiceChipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border chip ||
            chip.DataContext is not MessageAttachmentListItem item ||
            item.LocalPath is not { Length: > 0 } key)
        {
            return;
        }

        if (chip.FindName("VoiceChipIcon") is not FontIcon icon ||
            chip.FindName("VoiceChipTimeText") is not TextBlock timeText ||
            chip.FindName("VoiceChipProgress") is not ProgressBar progress)
        {
            return;
        }

        _voiceChips[key] = new VoiceChipParts(icon, timeText, progress, item);
        if (!_voicePlaybackHooked)
        {
            VoiceMessagePlaybackService.Instance.StateChanged += OnVoicePlaybackStateChanged;
            _voicePlaybackHooked = true;
        }

        ApplyVoiceChipState(VoiceMessagePlaybackService.Instance.CurrentState());
    }

    private void OnVoiceChipUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: MessageAttachmentListItem { LocalPath: { Length: > 0 } key } })
        {
            _voiceChips.Remove(key);
        }

        if (_voiceChips.Count == 0 && _voicePlaybackHooked)
        {
            VoiceMessagePlaybackService.Instance.StateChanged -= OnVoicePlaybackStateChanged;
            _voicePlaybackHooked = false;
        }
    }

    private void OnVoicePlayClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageAttachmentListItem { LocalPath: { Length: > 0 } path } })
        {
            VoiceMessagePlaybackService.Instance.TogglePlayback(path, path);
        }
    }

    private void OnVoicePlaybackStateChanged(object? sender, VoicePlaybackState state)
    {
        _ = DispatcherQueue.TryEnqueue(() => ApplyVoiceChipState(state));
    }

    private void ApplyVoiceChipState(VoicePlaybackState state)
    {
        foreach (var (key, parts) in _voiceChips)
        {
            var isCurrent = state.Key is not null && string.Equals(state.Key, key, StringComparison.OrdinalIgnoreCase);
            if (!isCurrent)
            {
                // Only reset chips the event is actually about; a position tick
                // for one chip must not clear another chip's paused state.
                if (state.Key is null || string.Equals(state.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Icon.Glyph = "";
                    parts.TimeText.Text = string.Empty;
                    parts.Progress.Value = 0;
                }

                continue;
            }

            parts.Icon.Glyph = state.IsPlaying ? "" : "";
            var duration = state.Duration > TimeSpan.Zero ? state.Duration : TimeSpan.Zero;
            parts.TimeText.Text = duration > TimeSpan.Zero
                ? $"{VoiceRecordingService.FormatElapsed(state.Position)} / {VoiceRecordingService.FormatElapsed(duration)}"
                : VoiceRecordingService.FormatElapsed(state.Position);
            parts.Progress.Maximum = duration > TimeSpan.Zero ? duration.TotalSeconds : 1;
            parts.Progress.Value = Math.Clamp(state.Position.TotalSeconds, 0, parts.Progress.Maximum);
        }
    }

    private void OnAttachmentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageAttachmentListItem attachment })
        {
            AttachmentOpenRequested?.Invoke(this, attachment);
            e.Handled = true;
        }
    }

    private void OnLinkPreviewTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Message?.LinkPreview is { } linkPreview)
        {
            LinkOpenRequested?.Invoke(this, linkPreview);
            e.Handled = true;
        }
    }

    private void OnInteractivePreviewPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = HandCursor;
    }

    private void OnInteractivePreviewPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
    }

    private void Raise(EventHandler<MessageListItem>? handler)
    {
        if (Message is not null)
        {
            handler?.Invoke(this, Message);
        }
    }

    private sealed record TapbackOption(string Reaction, string Glyph, string Label);
}

public sealed class TapbackRequestedEventArgs(MessageListItem message, TapbackChoice choice) : EventArgs
{
    public MessageListItem Message { get; } = message;

    public TapbackChoice Choice { get; } = choice;
}
