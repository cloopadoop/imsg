using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using WinIMsg.App.Services;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed class MessageAttachmentListItem(
    ImsgMessage message,
    ImsgAttachment attachment,
    Func<ImsgMessage, ImsgAttachment, string>? localPathResolver = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ImsgMessage Message { get; } = message;

    public ImsgAttachment Attachment { get; } = attachment;

    public string DisplayName => DisplayTextFormatter.SingleLine(Attachment.DisplayName, "Attachment");

    public string DetailText
    {
        get
        {
            var details = new List<string>();
            if (Attachment.SizeBytes is > 0)
            {
                details.Add(FormatBytes(Attachment.SizeBytes.Value));
            }

            if (!string.IsNullOrWhiteSpace(Attachment.ConvertedMimeType ?? Attachment.MimeType))
            {
                details.Add(DisplayTextFormatter.SingleLine(Attachment.ConvertedMimeType ?? Attachment.MimeType, string.Empty));
            }

            if (HasLocalFile)
            {
                details.Add("Downloaded");
            }

            return details.Count == 0 ? "Attachment" : string.Join(" - ", details);
        }
    }

    public string? LocalPath => localPathResolver?.Invoke(Message, Attachment);

    public bool HasRemotePath => !string.IsNullOrWhiteSpace(Attachment.RemotePath);

    public bool HasLocalFile => LocalPath is { Length: > 0 } localPath && File.Exists(localPath);

    public bool CanOpen => HasRemotePath || HasLocalFile;

    public bool CanDownload => HasRemotePath && !HasLocalFile;

    public bool CanReveal => HasLocalFile;

    public AttachmentPresentationKind Kind => AttachmentPresentationPolicy.Classify(Attachment, LocalPath);

    public bool CanRenderInline => HasLocalFile && Kind is AttachmentPresentationKind.Image or AttachmentPresentationKind.Audio or AttachmentPresentationKind.Video;

    public BitmapImage? ImageSource
    {
        get
        {
            if (ImageVisibility != Visibility.Visible || LocalPath is not { Length: > 0 } localPath)
            {
                return null;
            }

            return new BitmapImage(new Uri(localPath));
        }
    }

    public MediaSource? MediaSource
    {
        get
        {
            if (MediaVisibility != Visibility.Visible || LocalPath is not { Length: > 0 } localPath)
            {
                return null;
            }

            return MediaSource.CreateFromUri(new Uri(localPath));
        }
    }

    public double MediaHeight => 240;

    public Visibility ImageVisibility => HasLocalFile && Kind == AttachmentPresentationKind.Image
        ? Visibility.Visible
        : Visibility.Collapsed;

    // Video keeps the full transport-controls player; audio renders as a
    // compact voice-message chip driven by VoiceMessagePlaybackService.
    public Visibility MediaVisibility => HasLocalFile && Kind == AttachmentPresentationKind.Video
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AudioVisibility => HasLocalFile && Kind == AttachmentPresentationKind.Audio
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string AudioTitle
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(DisplayName);
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Voice message";
            }

            if (name.StartsWith("Voice Message", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Audio Message", StringComparison.OrdinalIgnoreCase))
            {
                return "Voice message";
            }

            return name;
        }
    }

    public Visibility FileVisibility => CanRenderInline ? Visibility.Collapsed : Visibility.Visible;

    public void RefreshLocalState()
    {
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(HasLocalFile));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(CanRenderInline));
        OnPropertyChanged(nameof(ImageSource));
        OnPropertyChanged(nameof(MediaSource));
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(MediaVisibility));
        OnPropertyChanged(nameof(AudioVisibility));
        OnPropertyChanged(nameof(AudioTitle));
        OnPropertyChanged(nameof(FileVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{size:0.#} {units[unitIndex]}";
    }
}
