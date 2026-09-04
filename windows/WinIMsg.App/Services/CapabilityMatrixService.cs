using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record CapabilityMatrixRow(
    string Category,
    string Feature,
    string Status,
    string Methods,
    string Guidance);

public sealed class CapabilityMatrixService
{
    public static readonly IReadOnlyList<string> KnownRpcMethods =
    [
        "chats.list",
        "chats.create",
        "chats.delete",
        "chats.markUnread",
        "messages.history",
        "watch.subscribe",
        "send",
        "send.rich",
        "send.attachment",
        "poll.send",
        "messages.poll.send",
        "tapback",
        "typing",
        "read",
        "message.edit",
        "message.unsend",
        "message.delete",
        "message.notifyAnyways",
        "message.send_status",
        "group.rename",
        "group.setIcon",
        "group.addParticipant",
        "group.removeParticipant",
        "group.leave",
        "handles.check"
    ];

    private static readonly CapabilityDefinition[] Definitions =
    [
        Represented("Baseline", "Chat list", ["chats.list"], "Loads conversations through ImsgBridgeClient.ListChatsAsync."),
        Represented("Baseline", "New chat / group create", ["chats.create"], "Creates new one-to-one or group conversations when upstream returns a usable chat target."),
        Represented("Chat management", "Delete chat", ["chats.delete"], "Deletes the selected conversation from Messages.app after confirmation and refreshes the chat list."),
        Represented("Chat management", "Mark unread", ["chats.markUnread"], "Marks the selected conversation unread when upstream supports chats.markUnread."),
        Represented("Baseline", "Message history", ["messages.history"], "Loads selected conversation history and cache refreshes."),
        Represented("Baseline", "Live watch", ["watch.subscribe"], "Receives live messages and reactions when the bridge supports watch.subscribe."),
        Represented("Baseline", "Text send", ["send"], "Sends text to selected chats and direct draft recipients."),
        Represented("Compose", "Rich send", ["send.rich"], "Uses send.rich for expressive send effects, reply targets, and selected-text formatting. Attachment replies remain blocked until attachment transport is rich-send backed."),
        Represented("Attachments", "Attachment send", ["send"], "Uses baseline send with a file path and optional caption; send.attachment is only a narrow fallback."),
        Represented("Attachments", "Advanced attachment fallback", ["send.attachment"], "Used only as a narrow fallback when baseline send with file is unsupported for the specific attachment shape."),
        Represented("Compose", "Poll send", ["poll.send", "messages.poll.send"], "Sends native Messages polls from selected chats when the advanced bridge advertises poll.send."),
        Represented("Message actions", "Tapback", ["tapback", "message.tapback", "send.tapback", "reaction", "message.reaction"], "Enables tapback controls for actionable non-pending message GUIDs."),
        Represented("Presence", "Typing indicator send", ["typing"], "Automatically sends typing start/stop from the compose box when imsg status reports typing_indicators=true.", static capabilities => capabilities.TypingIndicators),
        Represented("Message actions", "Mark read", ["read", "message.read"], "Marks the selected conversation read when upstream supports read."),
        Represented("Message actions", "Edit sent message", ["message.edit", "edit"], "Edits the selected sent message with selected chat id/identifier/GUID."),
        Represented("Message actions", "Unsend sent message", ["message.unsend", "unsend"], "Unsends the selected sent message with selected chat id/identifier/GUID."),
        Represented("Message actions", "Delete message", ["message.delete", "delete"], "Deletes the selected message on the Mac when upstream supports deletion."),
        Represented("Message actions", "Notify anyway", ["message.notifyAnyways"], "Sends a Focus bypass notification for selected sent messages with an actionable upstream GUID."),
        Represented("Delivery", "Send status", ["message.send_status"], "Polls sent-message status after sending when available."),
        Represented("Group management", "Rename group", ["group.rename"], "Enables group rename for selected chats with a group GUID."),
        Represented("Group management", "Set group icon", ["group.setIcon"], "Uploads a picked image to the Mac, sets it as the selected group icon, and can clear the current icon."),
        Represented("Group management", "Add participant", ["group.addParticipant"], "Adds one participant to a selected group with a group GUID."),
        Represented("Group management", "Remove participant", ["group.removeParticipant"], "Removes one participant from a selected group with a group GUID."),
        Represented("Group management", "Leave group", ["group.leave"], "Leaves the selected group after confirmation."),
        NotRepresented("Identity", "Handle check", ["handles.check"], "Recipient validation/search is local for now; upstream handle checking is not wired.")
    ];

    public static IReadOnlyList<CapabilityMatrixRow> Build(ImsgCapabilities capabilities)
    {
        var knownMethods = capabilities.RpcMethods.Count > 0;
        return Definitions
            .Select(definition => ToRow(definition, capabilities, knownMethods))
            .ToList();
    }

    public static string Summary(ImsgCapabilities capabilities)
    {
        if (capabilities.RpcMethods.Count == 0)
        {
            return "imsg status did not advertise rpc_methods. legacy baseline actions remain permissive until a command fails; advanced actions stay disabled unless their exact methods are advertised.";
        }

        var rows = Build(capabilities);
        var available = rows.Count(row => row.Status == "Available" || row.Status == "Assumed");
        var missing = rows.Count(row => row.Status == "Unavailable");
        var advertisedNotWired = rows.Count(row => row.Status == "Advertised, not wired");
        return $"{available} represented capability row(s) available, {missing} unavailable, {advertisedNotWired} advertised but not wired.";
    }

    private static CapabilityMatrixRow ToRow(
        CapabilityDefinition definition,
        ImsgCapabilities capabilities,
        bool knownMethods)
    {
        var advertised = definition.Methods.Where(capabilities.Supports).ToArray();
        if (definition.IsRepresented)
        {
            if (advertised.Length > 0 && definition.IsAvailableWhen(capabilities))
            {
                return new CapabilityMatrixRow(
                    definition.Category,
                    definition.Feature,
                    "Available",
                    MethodText(definition.Methods),
                    definition.Guidance);
            }

            if (advertised.Length > 0)
            {
                return new CapabilityMatrixRow(
                    definition.Category,
                    definition.Feature,
                    "Unavailable",
                    MethodText(definition.Methods),
                    $"{definition.Guidance} Run imsg launch on the Mac and verify imsg status --json reports the required feature flag.");
            }

            if (!knownMethods && definition.Category == "Baseline")
            {
                return new CapabilityMatrixRow(
                    definition.Category,
                    definition.Feature,
                    "Assumed",
                    MethodText(definition.Methods),
                    $"{definition.Guidance} rpc_methods is missing, so legacy baseline status is treated as permissive until a command fails.");
            }

            return new CapabilityMatrixRow(
                definition.Category,
                definition.Feature,
                "Unavailable",
                MethodText(definition.Methods),
                MissingGuidance(definition));
        }

        return new CapabilityMatrixRow(
            definition.Category,
            definition.Feature,
            advertised.Length > 0 ? "Advertised, not wired" : "Not implemented",
            MethodText(definition.Methods),
            advertised.Length > 0
                ? $"{definition.Guidance} Upstream advertises {string.Join(", ", advertised)}, so the app keeps it visible but disabled until Windows has a safe workflow."
                : definition.Guidance);
    }

    private static string MissingGuidance(CapabilityDefinition definition)
    {
        if (definition.Category == "Baseline")
        {
            return $"Install or update imsg and verify imsg status --json advertises {MethodText(definition.Methods)}.";
        }

        return $"Run setup checklist and verify imsg status --json advertises {MethodText(definition.Methods)} before this control can enable.";
    }

    private static string MethodText(IReadOnlyList<string> methods) => string.Join(", ", methods);

    private static CapabilityDefinition Represented(
        string category,
        string feature,
        IReadOnlyList<string> methods,
        string guidance,
        Func<ImsgCapabilities, bool>? isAvailableWhen = null) =>
        new(category, feature, methods, guidance, IsRepresented: true, isAvailableWhen ?? AlwaysAvailable);

    private static CapabilityDefinition NotRepresented(
        string category,
        string feature,
        IReadOnlyList<string> methods,
        string guidance) =>
        new(category, feature, methods, guidance, IsRepresented: false, AlwaysAvailable);

    private static bool AlwaysAvailable(ImsgCapabilities capabilities) => true;

    private sealed record CapabilityDefinition(
        string Category,
        string Feature,
        IReadOnlyList<string> Methods,
        string Guidance,
        bool IsRepresented,
        Func<ImsgCapabilities, bool> IsAvailableWhen);
}
