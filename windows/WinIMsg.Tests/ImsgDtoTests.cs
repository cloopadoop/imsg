using System.Text.Json;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Tests;

public sealed class ImsgDtoTests
{
    [Fact]
    public void ChatPayloadMapsWrappedChatsResult()
    {
        using var document = JsonDocument.Parse(ReadFixture("chats-list.json"));
        var chats = RpcResultMapper.ReadArray<ImsgChat>(document.RootElement.GetProperty("result"), "chats");

        Assert.Single(chats);
        Assert.Equal("42", chats[0].StableId);
        Assert.Equal("Alice", chats[0].DisplayName);
    }

    [Fact]
    public void CliChatReaderParsesContactNamesFromJsonLines()
    {
        var chats = ImsgCliChatReader.ParseChats("""
            {"id":1,"identifier":"iMessage;-;+15551230001","guid":"iMessage;-;+15551230001","name":"+15551230001","contact_name":"Alice","service":"iMessage","last_message_at":"2026-06-24T01:00:00Z","participants":["+15551230001"],"is_group":false}
            {"id":2,"identifier":"iMessage;+;chat-group","guid":"iMessage;+;chat-group","name":"Crew","service":"iMessage","last_message_at":"2026-06-24T01:01:00Z","participants":["+15551230001","+15551230002"],"is_group":true}
            """);

        Assert.Equal(2, chats.Count);
        Assert.Equal("Alice", chats[0].ContactName);
        Assert.Equal("Alice", chats[0].DisplayName);
        Assert.True(chats[1].IsGroup);
    }

    [Fact]
    public void OneToOneDisplayNameDoesNotTrustChatNameWithoutContactName()
    {
        var chat = new ImsgChat
        {
            Id = 1,
            Identifier = "iMessage;-;+15551230001",
            Guid = "iMessage;-;+15551230001",
            Name = "Family Group",
            Participants = ["+15551230001"],
            IsGroup = false
        };

        var group = chat with
        {
            Identifier = "iMessage;+;chat-group",
            Guid = "iMessage;+;chat-group",
            Name = "Family Group",
            Participants = ["+15551230001", "+15551230002"],
            IsGroup = true
        };

        Assert.Equal("+15551230001", chat.DisplayName);
        Assert.Equal("Family Group", group.DisplayName);
    }

    [Fact]
    public void MessagePayloadAcceptsDocumentedAndCurrentAttachmentFields()
    {
        using var document = JsonDocument.Parse(ReadFixture("messages-history.json"));
        var messages = RpcResultMapper.ReadArray<ImsgMessage>(document.RootElement.GetProperty("result"), "messages");

        var message = Assert.Single(messages);
        Assert.Equal("message-guid-100", message.Guid);
        Assert.Equal("/Users/testuser/Library/Messages/Attachments/photo.jpg", message.Attachments[0].RemotePath);
        Assert.Equal(1234, message.Attachments[0].SizeBytes);
        Assert.Equal("/tmp/voice.m4a", message.Attachments[1].RemotePath);
        Assert.Equal(4321, message.Attachments[1].SizeBytes);
        Assert.Equal("👍", message.Reactions[0].Emoji);
    }

    [Fact]
    public void MessagePayloadParsesReactionEventFields()
    {
        var message = JsonSerializer.Deserialize<ImsgMessage>(
            """
            {
              "id": 101,
              "chat_id": 42,
              "guid": "reaction-event-guid",
              "sender": "+15551230001",
              "is_from_me": true,
              "text": "Laughed at \"Test\"",
              "created_at": "2026-06-24T01:00:00Z",
              "is_reaction": true,
              "reaction_type": "laugh",
              "reaction_emoji": "😂",
              "is_reaction_add": true,
              "reacted_to_guid": "parent-message-guid"
            }
            """,
            ImsgJson.Options)!;

        Assert.True(message.IsReaction);
        Assert.True(message.IsReactionEvent);
        Assert.Equal("laugh", message.ReactionType);
        Assert.Equal("😂", message.ReactionEmoji);
        Assert.True(message.IsReactionAdd);
        Assert.Equal("parent-message-guid", message.ReactedToGuid);
    }

    [Fact]
    public void AttachmentDisplayNameUsesFriendlyFileName()
    {
        var attachment = new ImsgAttachment
        {
            Filename = "~/Library/Messages/Attachments/57/07/53BB129.pluginPayloadAttachment",
            OriginalPath = "/Users/testuser/Desktop/Trip Photo.jpg"
        };

        var pluginOnly = new ImsgAttachment
        {
            Filename = "/Users/testuser/Library/Messages/Attachments/57/07/53BB129.pluginPayloadAttachment"
        };

        Assert.Equal("Trip Photo.jpg", attachment.DisplayName);
        Assert.Equal("Attachment", pluginOnly.DisplayName);
    }

    [Fact]
    public void StatusPayloadParsesSipEnabledBasicOnlyMac()
    {
        var status = JsonSerializer.Deserialize<ImsgCapabilities>(ReadFixture("status.json"), ImsgJson.Options)!;

        Assert.True(status.Supports("message.edit"));
        Assert.False(status.SupportsAdvanced("message.edit"));
        Assert.False(status.HasAdvancedBridge);
        Assert.False(status.TypingIndicators);
    }

    [Fact]
    public void StatusPayloadEnablesAdvancedCapabilitiesWhenBridgeIsReady()
    {
        var status = JsonSerializer.Deserialize<ImsgCapabilities>(ReadFixture("status-advanced.json"), ImsgJson.Options)!;

        Assert.True(status.Supports("message.edit"));
        Assert.True(status.SupportsAdvanced("message.edit"));
        Assert.True(status.SupportsAny("group.rename", "group.leave"));
        Assert.True(status.HasAdvancedBridge);
        Assert.True(status.TypingIndicators);
    }

    [Fact]
    public void StatusPayloadParsesSelectorAvailability()
    {
        var status = JsonSerializer.Deserialize<ImsgCapabilities>(
            """
            {
              "rpc_methods": ["message.edit", "message.unsend"],
              "selectors": {
                "editMessage": false,
                "editMessageItem": false,
                "retractMessagePart": true
              }
            }
            """,
            ImsgJson.Options)!;

        Assert.False(status.SupportsSelector("editMessage", "editMessageItem"));
        Assert.True(status.SupportsSelector("retractMessagePart"));
        Assert.True(status.SupportsSelector("selectorNotReportedByThisVersion"));
    }

    private static string ReadFixture(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    }
}
