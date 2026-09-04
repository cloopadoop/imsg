using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using WinIMsg.App.Services;

namespace WinIMsg.App.Controls;

public sealed partial class ComposeBar : UserControl
{
    private bool _isVoiceRecording;
    private bool _canSendAttachment;

    public ComposeBar()
    {
        InitializeComponent();
        EmojiButton.Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = "\uE76E"
        };
        PendingAttachmentList.ItemsSource = PendingAttachments;
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
    }

    public ObservableCollection<PendingComposeAttachment> PendingAttachments { get; } = [];

    public TextBox TextBox => ComposeBox;

    public string Text
    {
        get => ComposeBox.Text;
        set => ComposeBox.Text = value;
    }

    public event RoutedEventHandler? AttachRequested;

    public event EventHandler<ComposeAttachmentFilesRequestedEventArgs>? AttachmentFilesRequested;

    public event EventHandler? PendingAttachmentsChanged;

    public event RoutedEventHandler? EmojiRequested;

    public event RoutedEventHandler? VoiceRequested;

    public event RoutedEventHandler? VoiceCancelRequested;

    public event RoutedEventHandler? SendRequested;

    public event RoutedEventHandler? PollRequested;

    public event EventHandler<ComposeSendEffectRequestedEventArgs>? SendEffectRequested;

    public event EventHandler<ComposeTextFormattingRequestedEventArgs>? TextFormattingRequested;

    public event EventHandler? ReplyCanceled;

    public IReadOnlyList<string> PendingAttachmentPaths => PendingAttachments
        .Select(attachment => attachment.FilePath)
        .ToList();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public static IReadOnlyList<string> NormalizeAttachmentFilePaths(IEnumerable<string?> filePaths)
    {
        return filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> ReadStorageItemFilePathsAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var items = await dataView.GetStorageItemsAsync();
        return NormalizeAttachmentFilePaths(items
            .OfType<StorageFile>()
            .Select(static file => file.Path));
    }

    public bool AddPendingAttachment(string filePath)
    {
        if (PendingAttachments.Any(attachment => string.Equals(attachment.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        PendingAttachments.Add(new PendingComposeAttachment(filePath));
        return true;
    }

    public void ClearPendingAttachments()
    {
        PendingAttachments.Clear();
    }

    public void SetPendingAttachments(IEnumerable<string?> filePaths)
    {
        PendingAttachments.Clear();
        foreach (var filePath in NormalizeAttachmentFilePaths(filePaths))
        {
            PendingAttachments.Add(new PendingComposeAttachment(filePath));
        }
    }

    public void FocusComposer(FocusState focusState = FocusState.Programmatic)
    {
        ComposeBox.Focus(focusState);
    }

    public void InsertText(string text)
    {
        var selectionStart = Math.Clamp(ComposeBox.SelectionStart, 0, ComposeBox.Text.Length);
        var selectionLength = Math.Clamp(ComposeBox.SelectionLength, 0, ComposeBox.Text.Length - selectionStart);
        ComposeBox.Text = ComposeBox.Text
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, text);
        ComposeBox.SelectionStart = selectionStart + text.Length;
        FocusComposer();
    }

    public void ShowFlyoutAtEmojiButton(FlyoutBase flyout)
    {
        flyout.ShowAt(EmojiButton);
    }

    public void SetActionState(bool canSend, bool canSendAttachment)
    {
        _canSendAttachment = canSendAttachment;
        SendButton.IsEnabled = canSend;
        AttachButton.IsEnabled = canSendAttachment;
        // The voice button doubles as the stop control while recording, so it
        // must stay clickable even if attachment capability recalculates.
        VoiceButton.IsEnabled = canSendAttachment || _isVoiceRecording;
    }

    public void SetVoiceRecordingState(bool isRecording, string statusText = "")
    {
        _isVoiceRecording = isRecording;
        VoiceRecordingBorder.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        VoiceRecordingTextBlock.Text = statusText;
        VoiceButtonIcon.Glyph = isRecording ? "" : "";
        ToolTipService.SetToolTip(VoiceButton, isRecording ? "Stop and attach voice message" : "Record voice message");
        VoiceButton.IsEnabled = _canSendAttachment || isRecording;
    }

    public void UpdateVoiceRecordingStatus(string statusText)
    {
        VoiceRecordingTextBlock.Text = statusText;
    }

    public void SetAdvancedComposeState(bool canSendRich, bool canSendPoll)
    {
        RichTextMenuItem.Text = canSendRich
            ? "Format selected text"
            : "Format selected text - requires send.rich";
        RichTextMenuItem.IsEnabled = canSendRich;
        SendEffectMenuItem.Text = canSendRich
            ? "Send effect"
            : "Send effect - requires send.rich";
        SendEffectMenuItem.IsEnabled = canSendRich;
        PollMenuItem.Text = canSendPoll
            ? "Poll..."
            : "Poll - requires poll.send and imsg launch";
        PollMenuItem.IsEnabled = canSendPoll;
        ReplyTargetMenuItem.Text = canSendRich
            ? "Reply target - planned"
            : "Reply target - requires send.rich";
    }

    public void SetReplyTarget(string? summary)
    {
        var hasReplyTarget = !string.IsNullOrWhiteSpace(summary);
        ReplyTargetTextBlock.Text = hasReplyTarget ? $"Replying to {summary}" : string.Empty;
        ReplyTargetBorder.Visibility = hasReplyTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAttachClicked(object sender, RoutedEventArgs e) => AttachRequested?.Invoke(this, e);

    private void OnEmojiClicked(object sender, RoutedEventArgs e) => EmojiRequested?.Invoke(this, e);

    private void OnVoiceClicked(object sender, RoutedEventArgs e) => VoiceRequested?.Invoke(this, e);

    private void OnVoiceCancelClicked(object sender, RoutedEventArgs e) => VoiceCancelRequested?.Invoke(this, e);

    private void OnSendClicked(object sender, RoutedEventArgs e) => SendRequested?.Invoke(this, e);

    private void OnPollClicked(object sender, RoutedEventArgs e) => PollRequested?.Invoke(this, e);

    private void OnSendEffectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string effect, Text: string label })
        {
            return;
        }

        SendEffectRequested?.Invoke(this, new ComposeSendEffectRequestedEventArgs(effect, label));
    }

    private void OnTextFormattingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string style, Text: string label })
        {
            return;
        }

        TextFormattingRequested?.Invoke(
            this,
            new ComposeTextFormattingRequestedEventArgs(
                style,
                label,
                Math.Clamp(ComposeBox.SelectionStart, 0, ComposeBox.Text.Length),
                Math.Clamp(ComposeBox.SelectionLength, 0, ComposeBox.Text.Length)));
    }

    private void OnReplyCancelClicked(object sender, RoutedEventArgs e) => ReplyCanceled?.Invoke(this, EventArgs.Empty);

    private void OnAttachmentDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Attach";
        e.Handled = true;
    }

    private async void OnAttachmentDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        IReadOnlyList<string> filePaths;
        try
        {
            filePaths = await ReadStorageItemFilePathsAsync(e.DataView);
        }
        catch
        {
            return;
        }

        if (filePaths.Count > 0)
        {
            AttachmentFilesRequested?.Invoke(
                this,
                new ComposeAttachmentFilesRequestedEventArgs(filePaths, ComposeAttachmentFileSource.Dropped));
        }
    }

    private void OnPendingMediaLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaPlayerElement element)
        {
            ApplyPendingMediaSource(element);
        }
    }

    private void OnPendingMediaDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is MediaPlayerElement element && element.IsLoaded)
        {
            ApplyPendingMediaSource(element);
        }
    }

    // A MediaPlayerElement destroyed while it still owns a MediaPlayer
    // fail-fasts inside Microsoft.UI.Xaml; detach before teardown, so the
    // source is managed here instead of through a XAML binding.
    private void OnPendingMediaUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaPlayerElement element)
        {
            return;
        }

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

    private static void ApplyPendingMediaSource(MediaPlayerElement element)
    {
        element.Source = (element.DataContext as PendingComposeAttachment)?.MediaSource;
    }

    private void OnRemovePendingAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingComposeAttachment attachment })
        {
            PendingAttachments.Remove(attachment);
        }
    }

    private void OnPendingAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PendingAttachmentScrollViewer.Visibility = PendingAttachments.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        PendingAttachmentsChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ComposeAttachmentFilesRequestedEventArgs(
    IReadOnlyList<string> filePaths,
    ComposeAttachmentFileSource source) : EventArgs
{
    public IReadOnlyList<string> FilePaths { get; } = filePaths;

    public ComposeAttachmentFileSource Source { get; } = source;
}

public enum ComposeAttachmentFileSource
{
    Picked,
    Pasted,
    Dropped
}

public sealed class ComposeSendEffectRequestedEventArgs(string effect, string label) : EventArgs
{
    public string Effect { get; } = effect;

    public string Label { get; } = label;
}

public sealed class ComposeTextFormattingRequestedEventArgs(
    string style,
    string label,
    int start,
    int length) : EventArgs
{
    public string Style { get; } = style;

    public string Label { get; } = label;

    public int Start { get; } = start;

    public int Length { get; } = length;
}

public sealed class PendingComposeAttachment
{
    public PendingComposeAttachment(string filePath)
    {
        FilePath = filePath;
        Kind = AttachmentPresentationPolicy.ClassifyLocalFile(filePath);
    }

    public string FilePath { get; set; }

    public AttachmentPresentationKind Kind { get; }

    public string DisplayName
    {
        get
        {
            var fileName = Path.GetFileName(FilePath);
            return string.IsNullOrWhiteSpace(fileName) ? FilePath : fileName;
        }
    }

    public string KindText => Kind switch
    {
        AttachmentPresentationKind.Image => "Image",
        AttachmentPresentationKind.Audio => "Audio",
        AttachmentPresentationKind.Video => "Video",
        _ => "Attachment"
    };

    public BitmapImage? ImageSource => Kind == AttachmentPresentationKind.Image && File.Exists(FilePath)
        ? new BitmapImage(new Uri(FilePath))
        : null;

    public MediaSource? MediaSource => Kind == AttachmentPresentationKind.Video && File.Exists(FilePath)
        ? MediaSource.CreateFromUri(new Uri(FilePath))
        : null;

    public Visibility ImagePreviewVisibility => Kind == AttachmentPresentationKind.Image
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility VideoPreviewVisibility => Kind == AttachmentPresentationKind.Video
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AudioPreviewVisibility => Kind == AttachmentPresentationKind.Audio
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FilePreviewVisibility => Kind == AttachmentPresentationKind.File
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MediaRemoveButtonVisibility => Kind is AttachmentPresentationKind.Image or AttachmentPresentationKind.Audio or AttachmentPresentationKind.Video
        ? Visibility.Visible
        : Visibility.Collapsed;
}
