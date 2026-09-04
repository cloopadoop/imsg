using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public interface INotificationPublisher
{
    void ShowInboundMessage(ImsgMessage message, bool showMessageContent);
}

public sealed class AppNotificationService : INotificationPublisher
{
    public void ShowInboundMessage(ImsgMessage message, bool showMessageContent)
    {
        var title = DisplayTextFormatter.SingleLine(string.IsNullOrWhiteSpace(message.ChatName) ? message.DisplaySender : message.ChatName, "Message");
        var bodyText = DisplayTextFormatter.MessageText(message.Text, string.Empty);
        var body = showMessageContent
            ? DisplayTextFormatter.SingleLine(string.IsNullOrWhiteSpace(bodyText) ? AttachmentFallback(message) : bodyText, "Attachment")
            : "New message";
        var notification = new AppNotificationBuilder()
            .AddArgument("chat", message.ChatStableId)
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        AppNotificationManager.Default.Show(notification);
    }

    private static string AttachmentFallback(ImsgMessage message)
    {
        return message.Attachments.Count == 0
            ? "Attachment"
            : string.Join(", ", message.Attachments.Select(attachment => DisplayTextFormatter.SingleLine(attachment.DisplayName, "Attachment")));
    }
}
