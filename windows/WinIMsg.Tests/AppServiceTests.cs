using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.UI.Xaml;
using WinIMsg.App.Contracts;
using WinIMsg.App.Mvvm;
using WinIMsg.App.Services;
using WinIMsg.App.ViewModels;
using WinIMsg.Core;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.Core.Ssh;

namespace WinIMsg.Tests;

public sealed class AppServiceTests
{
    [Fact]
    public void MessageWindowProjectorStartsAtNewestAndExpandsOlder()
    {
        var projector = new MessageWindowProjector<int>(initialVisibleCount: 3, pageSize: 2);

        projector.ReplaceAll(Enumerable.Range(1, 10), resetVisibleWindow: true);

        Assert.Equal([8, 9, 10], projector.VisibleItems);
        Assert.True(projector.CanLoadOlder);
        Assert.Equal([6, 7, 8, 9, 10], projector.LoadOlder());
        Assert.Equal([4, 5, 6, 7, 8, 9, 10], projector.LoadOlder());
        Assert.True(projector.CanLoadOlder);
    }

    [Fact]
    public async Task SelectedConversationLoadYieldsCachedMessagesBeforeRemoteRefresh()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.Chats.Add(chat);
        cache.MessagesByStableId[chat.StableId] = [Message("cached", 10, "old text")];

        var client = new FakeImsgClient { IsConnected = true };
        client.HistoryByChatId[10] = [Message("remote", 10, "new text")];
        var service = new ConversationDataService(cache, client);

        var results = new List<ConversationLoadResult>();
        await foreach (var result in service.LoadSelectedConversationAsync(ChatListItem.From(chat), 50))
        {
            results.Add(result);
        }

        Assert.Equal([ConversationLoadSource.Cache, ConversationLoadSource.Remote], results.Select(result => result.Source));
        Assert.Equal("old text", Assert.Single(results[0].Messages).Text);
        Assert.Equal("new text", Assert.Single(results[1].Messages).Text);
        Assert.Equal(chat.StableId, Assert.Single(cache.SuccessfulSyncs).StableId);
        Assert.Equal("remote", Assert.Single(cache.UpsertedMessages).Guid);
        Assert.Equal([true], client.HistoryConvertAttachmentsFlags);
    }

    [Fact]
    public async Task SelectedConversationLoadReadsBoundedNewestCachedPageBeforeRemoteRefresh()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.MessagesByStableId[chat.StableId] = Enumerable.Range(1, 10)
            .Select(index => MessageAt($"cached-{index}", 10, index.ToString(), $"2026-06-22T10:{index:00}:00Z") with { Id = index })
            .ToList();
        var client = new FakeImsgClient { IsConnected = false };
        var service = new ConversationDataService(cache, client);

        var results = new List<ConversationLoadResult>();
        await foreach (var result in service.LoadSelectedConversationAsync(
            ChatListItem.From(chat),
            remoteLimit: 500,
            cachedInitialLimit: 3))
        {
            results.Add(result);
        }

        var cached = Assert.Single(results);
        Assert.Equal(ConversationLoadSource.Cache, cached.Source);
        Assert.Equal(["8", "9", "10"], cached.Messages.Select(message => message.Text));
        Assert.True(cached.MayHaveOlderHistory);
    }

    [Fact]
    public async Task ConversationDataServiceLoadsOlderCachedPageBeforeCursor()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.MessagesByStableId[chat.StableId] = Enumerable.Range(1, 10)
            .Select(index => MessageAt($"cached-{index}", 10, index.ToString(), $"2026-06-22T10:{index:00}:00Z") with { Id = index })
            .ToList();
        var service = new ConversationDataService(cache, new FakeImsgClient());

        var older = await service.LoadOlderCachedMessagesAsync(
            ChatListItem.From(chat),
            "2026-06-22T10:08:00.0000000+00:00",
            beforeMessageRowId: 8,
            pageSize: 2);

        Assert.Equal(["6", "7"], older.Select(message => message.Text));
    }

    [Fact]
    public async Task ConversationDataServiceProjectsCachedLatestMessagePreviewsIntoChatRows()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.Chats.Add(chat);
        cache.MessagesByStableId[chat.StableId] =
        [
            MessageAt("older", 10, "Older cached text", "2026-06-22T10:00:00Z"),
            MessageAt("newer", 10, "Newest cached text", "2026-06-22T10:05:00Z")
        ];
        var service = new ConversationDataService(cache, new FakeImsgClient());

        var item = Assert.Single(await service.LoadCachedChatsAsync());

        Assert.Equal("Newest cached text", item.Detail);
    }

    [Fact]
    public async Task SelectedConversationLoadMergesMessagesFromDuplicateParticipantChats()
    {
        var cache = new FakeMessageCache();
        var olderChat = Chat(10, "older-guid", "Alice", "2026-06-22T10:00:00Z") with { Participants = ["+15135550100"] };
        var newerChat = Chat(11, "newer-guid", "Alice", "2026-06-22T11:00:00Z") with { Participants = ["(513) 555-0100"] };
        var merged = Assert.Single(ChatListItem.FromChats([olderChat, newerChat], mergeByParticipants: true, phoneNumberRegion: "US"));
        cache.MessagesByStableId[olderChat.StableId] = [Message("cached-older", 10, "cached older")];
        cache.MessagesByStableId[newerChat.StableId] = [Message("cached-newer", 11, "cached newer")];

        var client = new FakeImsgClient { IsConnected = true };
        client.HistoryByChatId[10] = [Message("remote-older", 10, "remote older")];
        client.HistoryByChatId[11] = [Message("remote-newer", 11, "remote newer")];
        var service = new ConversationDataService(cache, client);

        var results = new List<ConversationLoadResult>();
        await foreach (var result in service.LoadSelectedConversationAsync(merged, 50))
        {
            results.Add(result);
        }

        Assert.Equal([ConversationLoadSource.Cache, ConversationLoadSource.Remote], results.Select(result => result.Source));
        Assert.Equal(["cached newer", "cached older"], results[0].Messages.Select(message => message.Text).Order());
        Assert.Equal(["remote newer", "remote older"], results[1].Messages.Select(message => message.Text).Order());
        Assert.Equal([11, 10], client.HistoryCallOrder);
        Assert.Equal(["10", "11"], cache.SuccessfulSyncs.Select(sync => sync.StableId).Order());
    }

    [Fact]
    public async Task SelectedConversationLoadSortsMergedCachedAndRemoteMessagesByDate()
    {
        var cache = new FakeMessageCache();
        var olderChat = Chat(10, "older-guid", "Alice", "2026-06-22T10:00:00Z") with { Participants = ["+15135550100"] };
        var newerChat = Chat(11, "newer-guid", "Alice", "2026-06-22T11:00:00Z") with { Participants = ["(513) 555-0100"] };
        var merged = Assert.Single(ChatListItem.FromChats([olderChat, newerChat], mergeByParticipants: true, phoneNumberRegion: "US"));
        cache.MessagesByStableId[olderChat.StableId] = [MessageAt("cached-newer", 10, "cached newer", "2026-06-23T12:00:00Z")];
        cache.MessagesByStableId[newerChat.StableId] = [MessageAt("cached-older", 11, "cached older", "2026-06-22T12:00:00Z")];

        var client = new FakeImsgClient { IsConnected = true };
        client.HistoryByChatId[10] = [MessageAt("remote-newer", 10, "remote newer", "2026-06-23T12:00:00Z")];
        client.HistoryByChatId[11] = [MessageAt("remote-older", 11, "remote older", "2026-06-22T12:00:00Z")];
        var service = new ConversationDataService(cache, client);

        var results = new List<ConversationLoadResult>();
        await foreach (var result in service.LoadSelectedConversationAsync(merged, 50))
        {
            results.Add(result);
        }

        Assert.Equal(["cached older", "cached newer"], results[0].Messages.Select(message => message.Text));
        Assert.Equal(["remote older", "remote newer"], results[1].Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task SelectedConversationLoadUsesRemoteLoaderAndRecordsFetchedLimit()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.MessagesByStableId[chat.StableId] = [Message("cached", 10, "old text")];
        var client = new FakeImsgClient { IsConnected = true };
        var service = new ConversationDataService(cache, client);

        var results = new List<ConversationLoadResult>();
        await foreach (var result in service.LoadSelectedConversationAsync(
            ChatListItem.From(chat),
            500,
            (_, _, _) => Task.FromResult(new ConversationRemoteHistoryResult([Message("remote", 10, "recent text")], 50))))
        {
            results.Add(result);
        }

        Assert.Equal([ConversationLoadSource.Cache, ConversationLoadSource.Remote], results.Select(result => result.Source));
        Assert.Empty(client.HistoryCallOrder);
        Assert.Equal((chat.StableId, 50), Assert.Single(cache.SuccessfulSyncs));
    }

    [Fact]
    public async Task SelectedConversationLoadDoesNotWaitForDelayedRemoteHistoryBeforeYieldingCache()
    {
        var cache = new FakeMessageCache();
        var chat = Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z");
        cache.MessagesByStableId[chat.StableId] = [Message("cached", 10, "cached text")];
        var client = new FakeImsgClient { IsConnected = true };
        var remoteCompletion = new TaskCompletionSource<ConversationRemoteHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ConversationDataService(cache, client);

        await using var enumerator = service.LoadSelectedConversationAsync(
            ChatListItem.From(chat),
            500,
            (_, _, _) => remoteCompletion.Task).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ConversationLoadSource.Cache, enumerator.Current.Source);
        Assert.Equal("cached text", Assert.Single(enumerator.Current.Messages).Text);

        var remoteMove = enumerator.MoveNextAsync().AsTask();
        Assert.False(remoteMove.IsCompleted);

        remoteCompletion.SetResult(new ConversationRemoteHistoryResult([Message("remote", 10, "remote text")], 50));

        Assert.True(await remoteMove);
        Assert.Equal(ConversationLoadSource.Remote, enumerator.Current.Source);
        Assert.Equal("remote text", Assert.Single(enumerator.Current.Messages).Text);
    }

    [Fact]
    public void ConversationViewModelStartsAtNewestAndLoadsOlderOnCommand()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        var messages = Enumerable.Range(1, 10)
            .Select(index => Message($"m{index}", 10, index.ToString()))
            .ToList();

        viewModel.ReplaceMessages(messages, resetVisibleWindow: true);

        Assert.Equal(["8", "9", "10"], viewModel.Messages.Select(item => item.Message.Text));
        Assert.True(viewModel.CanLoadOlder);

        viewModel.LoadOlderMessages();

        Assert.Equal(["6", "7", "8", "9", "10"], viewModel.Messages.Select(item => item.Message.Text));
    }

    [Fact]
    public void ConversationViewModelPrependsOlderCachePageAndGrowsVisibleWindow()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 5, olderPageSize: 2);
        viewModel.ReplaceMessages(
            Enumerable.Range(6, 5).Select(index => MessageAt($"m{index}", 10, index.ToString(), $"2026-06-22T10:{index:00}:00Z") with { Id = index }),
            resetVisibleWindow: true);

        var added = viewModel.PrependOlderMessages(
            [
                MessageAt("m4", 10, "4", "2026-06-22T10:04:00Z") with { Id = 4 },
                MessageAt("m5", 10, "5", "2026-06-22T10:05:00Z") with { Id = 5 }
            ]);

        Assert.Equal(2, added);
        Assert.Equal(["4", "5", "6", "7", "8", "9", "10"], viewModel.Messages.Select(item => item.Message.Text));
    }

    [Fact]
    public void ConversationViewModelClearsMessageWindowWhenSelectedConversationChanges()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        viewModel.SelectConversation(ConversationTarget.FromChat(Chat(10, "alice-guid", "Alice", "2026-06-22T10:00:00Z"), "Alice"));
        viewModel.ReplaceMessages([Message("alice-message", 10, "old thread")], resetVisibleWindow: true);

        viewModel.SelectConversation(ConversationTarget.FromChat(Chat(11, "bob-guid", "Bob", "2026-06-22T11:00:00Z"), "Bob"));

        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.AllMessages);
        Assert.Equal("Bob", viewModel.Title);
    }

    [Fact]
    public void ConversationViewModelFiltersNonDisplayableRowsBeforeWindowing()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        var blank = Message("blank", 10, string.Empty);
        var reactionOnly = Message("reaction-only", 10, string.Empty) with
        {
            Reactions = [new ImsgReaction { Emoji = "love" }]
        };
        var reactionEvent = Message("reaction-event", 10, "Laughed at \"one\"") with
        {
            IsReaction = true,
            ReactionType = "laugh",
            ReactionEmoji = "😂",
            IsReactionAdd = true,
            ReactedToGuid = "m1"
        };

        viewModel.ReplaceMessages(
            [
                Message("m1", 10, "one"),
                blank,
                Message("m2", 10, "two"),
                reactionOnly,
                reactionEvent,
                Message("m3", 10, "three"),
                Message("m4", 10, "four")
            ],
            resetVisibleWindow: true);

        Assert.Equal(["two", "three", "four"], viewModel.Messages.Select(item => item.Text));
        Assert.DoesNotContain(viewModel.AllMessages, item => item.Message.Guid == blank.Guid);
        Assert.DoesNotContain(viewModel.AllMessages, item => item.Message.Guid == reactionOnly.Guid);
        Assert.DoesNotContain(viewModel.AllMessages, item => item.Message.Guid == reactionEvent.Guid);
    }

    [Fact]
    public void ConversationViewModelIgnoresAppendedNonDisplayableRows()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        viewModel.ReplaceMessages([Message("m1", 10, "one")], resetVisibleWindow: true);

        viewModel.AppendOrReplaceMessage(Message("blank", 10, string.Empty), resetVisibleWindow: true);

        Assert.Equal(["one"], viewModel.Messages.Select(item => item.Text));
    }

    [Fact]
    public void ConversationViewModelAppendsPinnedAwayMessageWithoutResettingWindow()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        var messages = Enumerable.Range(1, 5)
            .Select(index => Message($"m{index}", 10, index.ToString()))
            .ToList();
        viewModel.ReplaceMessages(messages, resetVisibleWindow: true);

        viewModel.AppendOrReplaceMessage(Message("m6", 10, "6"), resetVisibleWindow: false, growVisibleWindowBy: 1);

        Assert.Equal(["3", "4", "5", "6"], viewModel.Messages.Select(item => item.Message.Text));
    }

    [Fact]
    public void ConversationViewModelAppendsAtLatestByKeepingNewestWindow()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        var messages = Enumerable.Range(1, 5)
            .Select(index => Message($"m{index}", 10, index.ToString()))
            .ToList();
        viewModel.ReplaceMessages(messages, resetVisibleWindow: true);

        viewModel.AppendOrReplaceMessage(Message("m6", 10, "6"), resetVisibleWindow: true);

        Assert.Equal(["4", "5", "6"], viewModel.Messages.Select(item => item.Message.Text));
    }

    [Fact]
    public void ConversationViewModelRefreshesMatchingRemoteMessagesInPlace()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 3, olderPageSize: 2);
        viewModel.ReplaceMessages(
            [Message("m1", 10, "cached one"), Message("m2", 10, "cached two")],
            resetVisibleWindow: true);
        var first = viewModel.Messages[0];
        var second = viewModel.Messages[1];
        var collectionChangeCount = 0;
        var propertyChanges = new List<string?>();
        viewModel.Messages.CollectionChanged += (_, _) => collectionChangeCount++;
        second.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);

        viewModel.ReplaceMessages(
            [Message("m1", 10, "cached one"), Message("m2", 10, "remote two")],
            resetVisibleWindow: true);

        Assert.Same(first, viewModel.Messages[0]);
        Assert.Same(second, viewModel.Messages[1]);
        Assert.Equal(0, collectionChangeCount);
        Assert.Equal("remote two", second.Text);
        Assert.Contains(nameof(MessageListItem.Text), propertyChanges);
    }

    [Fact]
    public void DraftRecipientParserSplitsHumanGroupSeparators()
    {
        var recipients = ChatListItem.ParseRecipientText(
            "testuser@example.com and taylor@example.com\n+15551230003 & +15551230004");

        Assert.Equal(
            ["testuser@example.com", "taylor@example.com", "+15551230003", "+15551230004"],
            recipients);
    }

    [Fact]
    public void NewMessageDraftMarksAndSeparatedRecipientsAsGroup()
    {
        var draft = ChatListItem.NewMessageDraft("testuser@example.com and taylor@example.com");

        Assert.True(draft.Chat.IsGroup);
        Assert.Equal("New Message", draft.DisplayName);
        Assert.Equal("NM", draft.AvatarText);
        Assert.Equal(["testuser@example.com", "taylor@example.com"], draft.Chat.Participants);
    }

    [Fact]
    public void ChatListDetailHidesMergedThreadCount()
    {
        var first = Chat(1, "first", "First", "2026-06-22T12:00:00Z");
        var second = Chat(2, "second", "Second", "2026-06-22T12:01:00Z");
        var item = new ChatListItem(first, [first, second]);

        Assert.DoesNotContain("threads", item.Detail);
        Assert.DoesNotContain("·", item.Detail);
    }

    [Fact]
    public void ChatListDetailUsesLatestMessagePreviewWhenProvided()
    {
        var chat = Chat(1, "first", "Alice", "2026-06-22T12:00:00Z") with
        {
            Extra = new Dictionary<string, JsonElement>
            {
                ["last_message"] = Json("\"See you soon\"")
            }
        };

        var item = ChatListItem.From(chat);

        Assert.Equal("See you soon", item.Detail);
        Assert.Equal("See you soon", item.LatestMessagePreview);
    }

    [Fact]
    public void ChatListDetailUsesNewestPreviewAcrossMergedRows()
    {
        var older = Chat(1, "older", "Alice", "2026-06-22T12:00:00Z") with
        {
            Extra = new Dictionary<string, JsonElement>
            {
                ["last_message"] = Json("\"Older text\"")
            }
        };
        var newer = Chat(2, "newer", "Alice", "2026-06-22T13:00:00Z") with
        {
            Extra = new Dictionary<string, JsonElement>
            {
                ["latest_message"] = Json("""{"text":"Newest text"}""")
            }
        };

        var item = new ChatListItem(newer, [older, newer]);

        Assert.Equal("Newest text", item.Detail);
    }

    [Fact]
    public void ChatListUnreadRowsUseBoldTitleAndVisibleBadge()
    {
        var chat = Chat(1, "first", "Alice", "2026-06-22T12:00:00Z") with
        {
            UnreadCountRaw = 2
        };

        var item = ChatListItem.From(chat);

        Assert.True(item.HasUnread);
        Assert.Equal(700, item.TitleFontWeight.Weight);
        Assert.Equal(Visibility.Visible, item.UnreadBadgeVisibility);
        Assert.Equal(Visibility.Visible, item.UnreadDotVisibility);
        Assert.Equal("2", item.UnreadBadgeText);
        Assert.Equal("A", item.AvatarText);
    }

    [Fact]
    public void ChatListUnreadRowsIncludeTransientUnreadCounts()
    {
        var chat = Chat(1, "first", "Alice", "2026-06-22T12:00:00Z");

        var item = ChatListItem.From(
            chat,
            transientUnreadCountsByStableId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [chat.StableId] = 3
            });

        Assert.True(item.HasUnread);
        Assert.Equal(3, item.UnreadCount);
        Assert.Equal("3", item.UnreadBadgeText);
        Assert.Equal(Visibility.Visible, item.UnreadDotVisibility);
        Assert.Equal(700, item.TitleFontWeight.Weight);
    }

    [Fact]
    public void ChatListItemWithUnreadClearedRemovesPersistedAndTransientUnreadState()
    {
        var chat = Chat(1, "first", "Alice", "2026-06-22T12:00:00Z") with
        {
            UnreadCountRaw = 2,
            Extra = new Dictionary<string, JsonElement>
            {
                ["has_unread"] = Json("true"),
                ["preview"] = Json("\"Hello\"")
            }
        };
        var item = ChatListItem.From(
            chat,
            transientUnreadCountsByStableId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [chat.StableId] = 1
            });

        var read = item.WithUnreadCleared();

        Assert.False(read.HasUnread);
        Assert.Equal(0, read.UnreadCount);
        Assert.Equal(Visibility.Collapsed, read.UnreadDotVisibility);
        Assert.Equal("Hello", read.Detail);
        Assert.False(read.Chat.Extra?.ContainsKey("has_unread"));
    }

    [Fact]
    public void ConversationListReplaceChatUpdatesSelectedUnreadRowInPlace()
    {
        var unread = ChatListItem.From(Chat(1, "first", "Alice", "2026-06-22T12:00:00Z") with
        {
            UnreadCountRaw = 1
        });
        var other = ChatListItem.From(Chat(2, "second", "Bob", "2026-06-22T11:00:00Z"));
        var viewModel = new ConversationListViewModel();
        viewModel.ReplaceChats([unread, other]);
        viewModel.SelectedChat = unread;

        var read = unread.WithUnreadCleared();
        viewModel.ReplaceChat(unread, read);

        Assert.Same(read, viewModel.AllChats[0]);
        Assert.Same(read, viewModel.Chats[0]);
        Assert.Same(read, viewModel.SelectedChat);
        Assert.False(viewModel.SelectedChat.HasUnread);
    }

    [Fact]
    public void ConversationListAutosortsWhenReplacementHasNewerLatestTimestamp()
    {
        var alice = ChatListItem.From(Chat(1, "alice", "Alice", "2026-06-22T12:00:00Z"));
        var bob = ChatListItem.From(Chat(2, "bob", "Bob", "2026-06-22T11:00:00Z"));
        var viewModel = new ConversationListViewModel();
        viewModel.ReplaceChats([alice, bob]);

        var updatedBob = ChatListItem.From(Chat(2, "bob", "Bob", "2026-06-22T13:00:00Z"));
        viewModel.ReplaceChat(bob, updatedBob);

        Assert.Same(updatedBob, viewModel.Chats[0]);
        Assert.Same(alice, viewModel.Chats[1]);
    }

    [Fact]
    public void ChatListFromChatsSortsByEffectiveLatestMessageTimestamp()
    {
        var older = Chat(1, "older", "Older", "2026-06-22T12:00:00Z");
        var newer = Chat(2, "newer", "Newer", "2026-06-22T13:00:00Z");

        var rows = ChatListItem.FromChats(
            [
                older with { LastMessageAt = "2026-06-22T14:00:00Z" },
                newer
            ],
            mergeByParticipants: true);

        Assert.Equal("Older", rows[0].DisplayName);
    }

    [Fact]
    public void ChatListFromChatsSortsMergedRowsByNewestSourceTimestamp()
    {
        var oldPrimary = Chat(1, "old-primary", "Alice", "2026-06-22T10:00:00Z") with
        {
            Participants = ["+15551230001"]
        };
        var newSource = Chat(2, "new-source", "Alice", "2026-06-22T14:00:00Z") with
        {
            Participants = ["+15551230001"]
        };
        var separate = Chat(3, "separate", "Bob", "2026-06-22T13:00:00Z") with
        {
            Participants = ["+15551230002"]
        };

        var rows = ChatListItem.FromChats([oldPrimary, separate, newSource], mergeByParticipants: true, phoneNumberRegion: "US");

        Assert.Equal("Alice", rows[0].DisplayName);
        Assert.True(rows[0].IsMerged);
    }

    [Fact]
    public void ChatListFromChatsSortsByCachedLatestMessageTimestampWhenChatRowIsStale()
    {
        var staleChat = Chat(1, "stale", "Alice", "2026-06-22T10:00:00Z");
        var normalChat = Chat(2, "normal", "Bob", "2026-06-22T12:00:00Z");
        var latestDates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            [staleChat.StableId] = DateTimeOffset.Parse("2026-06-22T14:00:00Z")
        };

        var rows = ChatListItem.FromChats(
            [normalChat, staleChat],
            mergeByParticipants: false,
            latestMessageDatesByStableId: latestDates);

        Assert.Equal("Alice", rows[0].DisplayName);
        Assert.Equal(DateTimeOffset.Parse("2026-06-22T14:00:00Z"), rows[0].EffectiveLatestMessageDate);
    }

    [Fact]
    public void ChatListTimestampUsesEffectiveLatestMessageDate()
    {
        var older = Chat(1, "older", "Alice", "2026-06-22T10:00:00Z");
        var newer = Chat(2, "newer", "Alice", "2026-06-23T14:00:00Z");

        var item = new ChatListItem(older, [older, newer]);

        Assert.Equal("6/23/2026", item.TimestampText);
    }

    [Fact]
    public void AttachmentDetailUsesAsciiSeparator()
    {
        var attachment = new MessageAttachmentListItem(
            Message("message", 10, string.Empty),
            new ImsgAttachment { Filename = "photo.jpg", MimeType = "image/jpeg", ByteSize = 2048 });

        Assert.Equal("2 KB - image/jpeg", attachment.DetailText);
    }

    [Fact]
    public void ConversationViewModelTracksPendingSendTransitions()
    {
        var viewModel = new ConversationViewModel();
        var pending = viewModel.AddPendingMessage(Message("pending:1", 10, "hello"));

        Assert.True(Assert.Single(viewModel.Messages).IsPending);

        viewModel.MarkPendingMessageSent(pending, "sent-guid", "syncing");
        var sent = Assert.Single(viewModel.Messages);
        Assert.False(sent.IsPending);
        Assert.Equal("sent-guid", sent.Message.Guid);

        viewModel.UpdateMessageDeliveryStatus("sent-guid", "delivered");

        Assert.Equal("delivered", Assert.Single(viewModel.Messages).DeliveryStatus);
    }

    [Fact]
    public void FailedPendingTextMessageCanBeRemovedForRetry()
    {
        var viewModel = new ConversationViewModel();
        var pending = viewModel.AddPendingMessage(Message("pending:1", 10, "hello") with { IsFromMe = true });

        viewModel.MarkPendingMessageFailed(pending, "network failed");

        var failed = Assert.Single(viewModel.Messages);
        Assert.True(failed.IsFailed);
        Assert.True(failed.CanRetrySend);
        Assert.Equal(Visibility.Visible, failed.RetrySendVisibility);

        Assert.True(viewModel.RemoveTransientMessage(failed));
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public void FailedPendingAttachmentMessageCanBeRemovedForRetry()
    {
        var viewModel = new ConversationViewModel();
        var pending = viewModel.AddPendingMessage(Message("pending:attachment", 10, string.Empty) with
        {
            IsFromMe = true,
            PendingSendRetry = PendingSendRetryInfo.Attachments("caption", [@"C:\Temp\photo.jpg"]),
            Attachments = [new ImsgAttachment { Filename = "photo.jpg", OriginalPath = @"C:\Temp\photo.jpg" }]
        });

        viewModel.MarkPendingMessageFailed(pending, "upload failed");

        var failed = Assert.Single(viewModel.Messages);
        Assert.True(failed.IsFailed);
        Assert.True(failed.CanRetrySend);
        Assert.Equal(PendingSendRetryInfo.AttachmentKind, failed.Message.PendingSendRetry?.Kind);
        Assert.Equal([@"C:\Temp\photo.jpg"], failed.Message.PendingSendRetry?.AttachmentPaths);
        Assert.Equal(Visibility.Visible, failed.RetrySendVisibility);

        Assert.True(viewModel.RemoveTransientMessage(failed));
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public void FailedPendingRichMessageCanBeRemovedForRetry()
    {
        var viewModel = new ConversationViewModel();
        var pending = viewModel.AddPendingMessage(Message("pending:rich:1", 10, "hello") with
        {
            IsFromMe = true,
            PendingSendRetry = PendingSendRetryInfo.Rich(
                "hello",
                "impact",
                "Impact",
                "reply-guid",
                "Alice: earlier",
                [new RichTextFormattingRange(0, 5, ["bold"])])
        });

        viewModel.MarkPendingMessageFailed(pending, "network failed");

        var failed = Assert.Single(viewModel.Messages);
        Assert.True(failed.IsFailed);
        Assert.True(failed.CanRetrySend);
        Assert.Equal(Visibility.Visible, failed.RetrySendVisibility);

        Assert.True(viewModel.RemoveTransientMessage(failed));
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public void FailedPendingPollMessageCanBeRemovedForRetry()
    {
        var viewModel = new ConversationViewModel();
        var pending = viewModel.AddPendingMessage(Message("pending:poll:1", 10, "Poll: Dinner?") with
        {
            IsFromMe = true,
            PendingSendRetry = PendingSendRetryInfo.Poll("Dinner?", ["Pizza", "Sushi"])
        });

        viewModel.MarkPendingMessageFailed(pending, "network failed");

        var failed = Assert.Single(viewModel.Messages);
        Assert.True(failed.IsFailed);
        Assert.True(failed.CanRetrySend);
        Assert.Equal(Visibility.Visible, failed.RetrySendVisibility);

        Assert.True(viewModel.RemoveTransientMessage(failed));
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public void ComposeAttachmentPathNormalizationTrimsBlanksAndDuplicates()
    {
        var paths = WinIMsg.App.Controls.ComposeBar.NormalizeAttachmentFilePaths(
            [
                "  C:\\Temp\\photo.jpg  ",
                "c:\\temp\\PHOTO.jpg",
                null,
                "",
                "C:\\Temp\\clip.mov"
            ]);

        Assert.Equal(["C:\\Temp\\photo.jpg", "C:\\Temp\\clip.mov"], paths);
    }

    [Fact]
    public void PollComposeRequestNormalizesOptionsAndRequiresTwoChoices()
    {
        var request = PollComposeRequest.Create(
            "  Dinner?  ",
            $" Pizza {Environment.NewLine}Sushi{Environment.NewLine}pizza{Environment.NewLine}");

        Assert.NotNull(request);
        Assert.Equal("Dinner?", request.Question);
        Assert.Equal(["Pizza", "Sushi"], request.Options);
        Assert.Null(PollComposeRequest.Create("Dinner?", "Pizza"));
        Assert.Null(PollComposeRequest.Create(" ", $"Pizza{Environment.NewLine}Sushi"));
    }

    [Fact]
    public void ConversationViewModelRefreshesAttachmentRetryState()
    {
        var viewModel = new ConversationViewModel();
        var message = Message("with-attachment", 10, "photo") with
        {
            Attachments = [new ImsgAttachment { Path = "/Users/testuser/Pictures/photo.jpg" }]
        };
        viewModel.ReplaceMessages([message], resetVisibleWindow: true, hasFailedAttachmentDownload: _ => false);

        var item = Assert.Single(viewModel.Messages);
        var propertyChanges = new List<string?>();
        item.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);
        var collectionChangeCount = 0;
        viewModel.Messages.CollectionChanged += (_, _) => collectionChangeCount++;

        Assert.False(item.HasFailedAttachmentDownload);

        viewModel.RefreshAttachmentState("with-attachment", _ => true);

        Assert.True(item.HasFailedAttachmentDownload);
        Assert.Same(item, Assert.Single(viewModel.Messages));
        Assert.Equal(0, collectionChangeCount);
        Assert.Contains(nameof(MessageListItem.HasFailedAttachmentDownload), propertyChanges);
        Assert.Contains(nameof(MessageListItem.CanRetryAttachmentDownload), propertyChanges);
        Assert.Contains(nameof(MessageListItem.RetryAttachmentDownloadVisibility), propertyChanges);
    }

    [Fact]
    public void ConversationViewModelSkipsAttachmentRefreshWhenRetryStateIsUnchanged()
    {
        var viewModel = new ConversationViewModel();
        var message = Message("with-attachment", 10, "photo") with
        {
            Attachments = [new ImsgAttachment { Path = "/Users/testuser/Pictures/photo.jpg" }]
        };
        viewModel.ReplaceMessages([message], resetVisibleWindow: true, hasFailedAttachmentDownload: _ => false);

        var collectionChangeCount = 0;
        viewModel.Messages.CollectionChanged += (_, _) => collectionChangeCount++;

        viewModel.RefreshAttachmentState("with-attachment", _ => false);

        Assert.Equal(0, collectionChangeCount);
    }

    [Fact]
    public void ObservableCollectionReplaceWithSkipsEqualSequencesAndPrependsOlderItems()
    {
        var collection = new ObservableCollection<int>([8, 9, 10]);
        var changes = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => changes.Add(args.Action);

        collection.ReplaceWith([8, 9, 10]);

        Assert.Empty(changes);

        collection.ReplaceWith([6, 7, 8, 9, 10]);

        Assert.Equal([6, 7, 8, 9, 10], collection);
        Assert.Equal([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void DisplayTextFormatterStripsMessagesAttachmentPlaceholderMojibake()
    {
        Assert.Equal(
            "I share here because trust you for tact",
            DisplayTextFormatter.MessageText("\u00EF\u00BF\u00BCI share here because trust you for tact"));
    }

    [Fact]
    public void DisplayTextFormatterRepairsSmartQuoteMojibake()
    {
        Assert.Equal(
            "Laughed at “Test”",
            DisplayTextFormatter.MessageText("Laughed at \u00E2\u20AC\u0153Test\u00E2\u20AC\u009D"));
    }

    [Fact]
    public void LinkPreviewUsesDownloadedMessagesPluginPayloadImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previewPath = Path.Combine(root, "preview.png");
        File.WriteAllBytes(previewPath, [0x89, 0x50, 0x4E, 0x47]);
        var message = Message("link-message", 10, "https://example.com/story") with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    Path = "/Users/testuser/Library/Messages/Attachments/example/pluginPayloadAttachment",
                    MimeType = "image/png"
                }
            ]
        };

        var item = MessageListItem.From(message, attachmentLocalPathResolver: (_, _) => previewPath);

        Assert.NotNull(item.LinkPreview);
        Assert.Equal(Visibility.Visible, item.LinkPreview!.PreviewImageVisibility);
    }

    [Fact]
    public void AttachmentServiceTracksFailedDownloadStateAndUsesMessageGuidPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var service = new AttachmentService(new AppDataPaths(root), new SshFileTransferService());
        var message = Message("message-guid", 10, "photo") with
        {
            Attachments = [new ImsgAttachment { Path = "/Users/testuser/Pictures/photo.jpg" }]
        };
        var attachment = Assert.Single(message.Attachments);

        var localPath = service.GetLocalPath(message, attachment);
        var stateChangeCount = 0;
        service.DownloadStateChanged += (_, _) => stateChangeCount++;

        Assert.True(service.MarkDownloadFailed(message.Guid!, attachment.RemotePath!, notify: false));
        Assert.Equal(0, stateChangeCount);
        Assert.False(service.MarkDownloadFailed(message.Guid!, attachment.RemotePath!, forceNotify: true));
        Assert.Equal(1, stateChangeCount);

        Assert.Contains(Path.Combine("attachments", "message-guid"), localPath);
        Assert.True(service.HasFailedDownload(message));

        Assert.True(service.MarkDownloadSucceeded(message.Guid!, attachment.RemotePath!));
        Assert.Equal(2, stateChangeCount);
        Assert.False(service.MarkDownloadSucceeded(message.Guid!, attachment.RemotePath!));
        Assert.Equal(2, stateChangeCount);

        Assert.False(service.HasFailedDownload(message));
    }

    [Fact]
    public void AttachmentServiceUsesConvertedAttachmentFileNameForLocalCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var service = new AttachmentService(new AppDataPaths(root), new SshFileTransferService());
        var message = Message("message-guid", 10, "audio") with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "Audio Message.caf",
                    MimeType = "audio/x-caf",
                    Path = "/Users/testuser/Library/Messages/Attachments/Audio Message.caf",
                    ConvertedPath = "/Users/testuser/Library/Caches/imsg/converted-attachments/Audio Message.m4a",
                    ConvertedMimeType = "audio/mp4"
                }
            ]
        };

        var localPath = service.GetLocalPath(message, Assert.Single(message.Attachments));

        Assert.EndsWith("Audio Message.m4a", localPath);
    }

    [Fact]
    public void MessageListItemHidesSentOnlyActionsForReceivedMessages()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = true,
            RpcMethods = ["tapback", "message.edit", "message.unsend", "message.delete", "message.notifyAnyways"]
        };
        var inbound = Message("server-guid", 10, "received") with { IsFromMe = false };
        var outbound = inbound with { IsFromMe = true };
        var item = MessageListItem.From(inbound, capabilities);
        var sentItem = MessageListItem.From(outbound, capabilities);

        Assert.Equal(Visibility.Visible, item.MessageActionVisibility);
        Assert.Equal(Visibility.Collapsed, item.SentMessageActionVisibility);
        Assert.True(item.CanTapback);
        Assert.False(item.CanEdit);
        Assert.False(item.CanUnsend);
        Assert.False(item.CanNotifyAnyways);
        Assert.True(sentItem.CanNotifyAnyways);
    }

    [Fact]
    public void MessageListItemSuppressesReactionEventRowsAndActions()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = true,
            RpcMethods = ["tapback", "message.edit", "message.unsend", "message.delete", "message.notifyAnyways"]
        };
        var reactionEvent = Message("reaction-event-guid", 10, "Laughed at \"Test\"") with
        {
            IsFromMe = true,
            IsReaction = true,
            ReactionType = "laugh",
            ReactionEmoji = "😂",
            IsReactionAdd = true,
            ReactedToGuid = "parent-guid",
            Reactions = [new ImsgReaction { Emoji = "😂", IsFromMe = true }]
        };

        var item = MessageListItem.From(reactionEvent, capabilities);

        Assert.Equal(string.Empty, item.Text);
        Assert.Equal(string.Empty, item.ReactionSummary);
        Assert.Null(item.CurrentUserTapbackReaction);
        Assert.Equal(Visibility.Collapsed, item.ItemVisibility);
        Assert.Equal(Visibility.Collapsed, item.MessageActionVisibility);
        Assert.Equal(Visibility.Collapsed, item.SentMessageActionVisibility);
        Assert.False(item.CanTapback);
        Assert.False(item.CanEdit);
        Assert.False(item.CanUnsend);
        Assert.False(item.CanDelete);
        Assert.False(item.CanNotifyAnyways);
    }

    [Fact]
    public void MessageListItemNormalizesCurrentUserTapbackKinds()
    {
        var liked = MessageListItem.From(Message("liked-guid", 10, "hello") with
        {
            Reactions = [new ImsgReaction { Type = "liked", IsFromMe = true }]
        });
        var emphasis = MessageListItem.From(Message("emphasis-guid", 10, "hello") with
        {
            Reactions = [new ImsgReaction { Type = "emphasis", IsFromMe = true }]
        });

        Assert.Equal("like", liked.CurrentUserTapbackReaction);
        Assert.Equal("emphasize", emphasis.CurrentUserTapbackReaction);
    }

    [Theory]
    [InlineData("liked", "like")]
    [InlineData("loved", "love")]
    [InlineData("emphasis", "emphasize")]
    [InlineData("remove-emphasis", "emphasize")]
    [InlineData("??", "question")]
    public void TapbackReactionPolicyNormalizesStandardAliases(string raw, string expected)
    {
        Assert.Equal(expected, TapbackReactionPolicy.NormalizeKind(raw));
    }

    [Fact]
    public void MessageListItemOnlyShowsAttachmentActionsForUsableAttachments()
    {
        var withoutAttachment = MessageListItem.From(Message("plain-guid", 10, "plain"));
        var pluginOnly = MessageListItem.From(Message("plugin-guid", 10, "rich link") with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    Filename = "/Users/testuser/Library/Messages/Attachments/57/07/pluginPayloadAttachment",
                    Path = "/Users/testuser/Library/Messages/Attachments/57/07/pluginPayloadAttachment"
                }
            ]
        });
        var image = Message("image-guid", 10, "photo") with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "IMG_3027.png",
                    MimeType = "image/png",
                    Path = "/Users/testuser/Library/Messages/Attachments/IMG_3027.png"
                }
            ]
        };
        var imageItem = MessageListItem.From(image);

        Assert.Equal(Visibility.Collapsed, withoutAttachment.AttachmentMenuVisibility);
        Assert.Equal(Visibility.Collapsed, pluginOnly.AttachmentMenuVisibility);
        Assert.Equal(Visibility.Visible, imageItem.AttachmentMenuVisibility);
        Assert.True(imageItem.CanOpenAttachment);
    }

    [Fact]
    public void MessageListItemCollapsesRowsWithNoDisplayableContent()
    {
        var blank = MessageListItem.From(Message("blank-guid", 10, string.Empty));
        var text = MessageListItem.From(Message("text-guid", 10, "hello"));
        var reactionOnly = MessageListItem.From(Message("reaction-guid", 10, string.Empty) with
        {
            Reactions = [new ImsgReaction { Emoji = "❤️" }]
        });
        var reactionEvent = MessageListItem.From(Message("reaction-event-guid", 10, "Laughed at \"hello\"") with
        {
            IsReaction = true,
            ReactedToGuid = "text-guid"
        });
        var textWithReaction = MessageListItem.From(Message("text-reaction-guid", 10, "hello") with
        {
            Reactions = [new ImsgReaction { Emoji = "❤️" }]
        });

        Assert.Equal(Visibility.Collapsed, blank.ItemVisibility);
        Assert.Equal(Visibility.Visible, text.ItemVisibility);
        Assert.Equal(Visibility.Collapsed, reactionOnly.ItemVisibility);
        Assert.Equal(Visibility.Collapsed, reactionEvent.ItemVisibility);
        Assert.Equal(Visibility.Visible, textWithReaction.ItemVisibility);
    }

    [Fact]
    public void MessageListItemCreatesLocalLinkPreviewFromUrlText()
    {
        var item = MessageListItem.From(Message("link-guid", 10, "https://maps.app.goo.gl/UuatD92VsDcTdbLB7"));

        Assert.Equal(Visibility.Visible, item.LinkPreviewVisibility);
        Assert.NotNull(item.LinkPreview);
        Assert.Equal("maps.app.goo.gl", item.LinkPreview!.Title);
    }

    [Fact]
    public void MessageAttachmentListItemPromotesDownloadedImagesToInlinePreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var localFile = Path.Combine(root, "IMG_3027.png");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(localFile, [0x89, 0x50, 0x4E, 0x47]);
        var message = Message("image-guid", 10, "photo") with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "IMG_3027.png",
                    MimeType = "image/png",
                    Path = "/Users/testuser/Library/Messages/Attachments/IMG_3027.png"
                }
            ]
        };

        var item = MessageListItem.From(message, attachmentLocalPathResolver: (_, _) => localFile);
        var attachment = Assert.Single(item.AttachmentPreviews);

        Assert.Equal(AttachmentPresentationKind.Image, attachment.Kind);
        Assert.True(attachment.HasLocalFile);
        Assert.Equal(Visibility.Visible, attachment.ImageVisibility);
        Assert.Equal(Visibility.Collapsed, attachment.FileVisibility);
        Assert.True(item.CanRevealAttachment);
    }

    [Fact]
    public void MessageAttachmentListItemPlaysConvertedCafAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var localFile = Path.Combine(root, "Audio Message.m4a");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(localFile, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20]);
        var message = Message("caf-guid", 10, string.Empty) with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "Audio Message.caf",
                    MimeType = "audio/x-caf",
                    Path = "/Users/testuser/Library/Messages/Attachments/Audio Message.caf",
                    ConvertedPath = "/Users/testuser/Library/Caches/imsg/converted-attachments/Audio Message.m4a",
                    ConvertedMimeType = "audio/mp4"
                }
            ]
        };

        var item = MessageListItem.From(message, attachmentLocalPathResolver: (_, _) => localFile);
        var attachment = Assert.Single(item.AttachmentPreviews);

        Assert.Equal(AttachmentPresentationKind.Audio, attachment.Kind);
        Assert.Equal(Visibility.Visible, attachment.AudioVisibility);
        Assert.Equal(Visibility.Collapsed, attachment.MediaVisibility);
        Assert.Equal(Visibility.Collapsed, attachment.FileVisibility);
        Assert.Equal("Voice message", attachment.AudioTitle);
    }

    [Fact]
    public void MessageAttachmentListItemDoesNotInlineHeicImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var localFile = Path.Combine(root, "IMG_2580.HEIC");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(localFile, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63]);
        var message = Message("heic-guid", 10, string.Empty) with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "IMG_2580.HEIC",
                    MimeType = "image/heic",
                    Uti = "public.heic",
                    Path = "/Users/testuser/Library/Messages/Attachments/IMG_2580.HEIC"
                }
            ]
        };

        var item = MessageListItem.From(message, attachmentLocalPathResolver: (_, _) => localFile);
        var attachment = Assert.Single(item.AttachmentPreviews);

        Assert.Equal(AttachmentPresentationKind.File, attachment.Kind);
        Assert.Equal(Visibility.Collapsed, attachment.ImageVisibility);
        Assert.Equal(Visibility.Visible, attachment.FileVisibility);
    }

    [Fact]
    public void MessageAttachmentListItemTreatsMessagesGroupPhotoAsImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N"));
        var localFile = Path.Combine(root, "GroupPhotoImage");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(localFile, [0x89, 0x50, 0x4E, 0x47]);
        var message = Message("group-photo-guid", 10, string.Empty) with
        {
            Attachments =
            [
                new ImsgAttachment
                {
                    TransferName = "GroupPhotoImage",
                    OriginalPath = "/Users/testuser/Library/Messages/Attachments/GroupPhotoImage",
                    TotalBytes = 359852
                }
            ]
        };

        var item = MessageListItem.From(message, attachmentLocalPathResolver: (_, _) => localFile);
        var attachment = Assert.Single(item.AttachmentPreviews);

        Assert.Equal(AttachmentPresentationKind.Image, attachment.Kind);
        Assert.Equal(Visibility.Visible, attachment.ImageVisibility);
        Assert.Equal(Visibility.Visible, item.ItemVisibility);
    }

    [Fact]
    public void CapabilityActionPolicyGatesMessageActions()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = true,
            RpcMethods = ["tapback", "message.edit", "message.unsend", "message.delete", "message.notifyAnyways"]
        };
        var inbound = Message("server-guid", 10, "hello") with { IsFromMe = false };
        var outbound = inbound with { IsFromMe = true };
        var pending = outbound with { Guid = "pending:1" };
        var reactionEvent = outbound with
        {
            Guid = "reaction-guid",
            IsReaction = true,
            ReactedToGuid = inbound.Guid
        };

        Assert.True(CapabilityActionPolicy.CanTapback(inbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanNotifyAnyways(inbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanEdit(inbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanEdit(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanUnsend(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanDelete(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanNotifyAnyways(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanDelete(pending, capabilities));
        Assert.False(CapabilityActionPolicy.CanNotifyAnyways(pending, capabilities));
        Assert.False(CapabilityActionPolicy.CanTapback(reactionEvent, capabilities));
        Assert.False(CapabilityActionPolicy.CanEdit(reactionEvent, capabilities));
        Assert.False(CapabilityActionPolicy.CanUnsend(reactionEvent, capabilities));
        Assert.False(CapabilityActionPolicy.CanDelete(reactionEvent, capabilities));
        Assert.False(CapabilityActionPolicy.CanNotifyAnyways(reactionEvent, capabilities));
    }

    [Fact]
    public void CapabilityActionPolicyDisablesEditWhenStatusSelectorIsUnavailable()
    {
        var capabilities = JsonSerializer.Deserialize<ImsgCapabilities>(
            """
            {
              "v2_ready": true,
              "advanced_features": true,
              "rpc_methods": ["message.edit", "message.unsend"],
              "selectors": {
                "editMessage": false,
                "editMessageItem": false,
                "retractMessagePart": true
              }
            }
            """,
            ImsgJson.Options)!;
        var outbound = Message("server-guid", 10, "hello") with { IsFromMe = true };

        Assert.False(CapabilityActionPolicy.CanEdit(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanUnsend(outbound, capabilities));
    }

    [Fact]
    public void MessageActionTargetResolverUsesMessageOwningSourceInMergedChat()
    {
        var primary = Chat(1, "primary-guid", "Alice primary", "2026-06-22T12:00:00Z") with
        {
            Identifier = "iMessage;-;+15550000001"
        };
        var secondary = Chat(2, "secondary-guid", "Alice secondary", "2026-06-22T12:01:00Z") with
        {
            Identifier = "iMessage;-;+15550000002"
        };
        var merged = new ChatListItem(primary, [primary, secondary]);
        var message = Message("message-guid", 2, "hello") with
        {
            ChatIdentifier = "iMessage;-;+15550000002",
            ChatGuid = "secondary-guid"
        };

        var target = MessageActionTargetResolver.Resolve(merged, message);

        Assert.Equal(2, target.Id);
        Assert.Equal("secondary-guid", target.Guid);
        Assert.Equal("iMessage;-;+15550000002", target.Identifier);
    }

    [Fact]
    public void MessageActionTargetResolverFallsBackToMessageChatFields()
    {
        var selected = ChatListItem.From(Chat(1, "selected-guid", "Selected", "2026-06-22T12:00:00Z"));
        var message = Message("message-guid", 99, "hello") with
        {
            ChatIdentifier = "iMessage;-;+15559999999",
            ChatGuid = "message-chat-guid"
        };

        var target = MessageActionTargetResolver.Resolve(selected, message);

        Assert.Equal(99, target.Id);
        Assert.Equal("message-chat-guid", target.Guid);
        Assert.Equal("iMessage;-;+15559999999", target.Identifier);
    }

    [Fact]
    public async Task MessageActionWorkflowSendsTapbackToResolvedSourceChat()
    {
        var client = new FakeImsgClient();
        var service = new MessageActionWorkflowService(client);
        var capabilities = new ImsgCapabilities { V2Ready = true, RpcMethods = ["tapback"] };
        var primary = Chat(1, "primary-guid", "Alice", "2026-06-22T12:00:00Z") with
        {
            Identifier = "iMessage;-;+15550000001"
        };
        var secondary = Chat(2, "secondary-guid", "Alice secondary", "2026-06-22T12:01:00Z") with
        {
            Identifier = "iMessage;-;+15550000002"
        };
        var merged = new ChatListItem(primary, [primary, secondary]);
        var message = Message("message-guid", 2, "hello") with
        {
            ChatIdentifier = "iMessage;-;+15550000002",
            ChatGuid = "secondary-guid"
        };

        var status = await service.SendTapbackAsync(
            merged,
            message,
            new TapbackChoice("liked", Remove: false),
            capabilities);

        Assert.Equal("Tapback sent.", status);
        Assert.Equal(1, client.TapbackCalls);
        Assert.Equal(2, client.LastTapbackChatId);
        Assert.Equal("iMessage;-;+15550000002", client.LastTapbackIdentifier);
        Assert.Equal("secondary-guid", client.LastTapbackChatGuid);
        Assert.Equal("message-guid", client.LastTapbackMessageGuid);
        Assert.Equal("like", client.LastTapbackReaction);
        Assert.False(client.LastTapbackRemove);
    }

    [Fact]
    public async Task MessageActionWorkflowRejectsReactionEventBeforeRpc()
    {
        var client = new FakeImsgClient();
        var service = new MessageActionWorkflowService(client);
        var capabilities = new ImsgCapabilities { RpcMethods = ["message.edit"] };
        var selected = ChatListItem.From(Chat(1, "selected-guid", "Selected", "2026-06-22T12:00:00Z"));
        var reaction = Message("reaction-guid", 1, "Loved \"hello\"") with
        {
            IsFromMe = true,
            IsReaction = true,
            ReactedToGuid = "message-guid"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EditMessageAsync(selected, reaction, "edited", capabilities));

        Assert.Contains("non-reaction", ex.Message);
        Assert.Equal(0, client.EditMessageCalls);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void CapabilityActionPolicyGatesFaceTimeLinkOnSelectionConnectionAndProbe(
        bool hasSelectedChat,
        bool connected,
        bool faceTimeAvailable,
        bool expected)
    {
        Assert.Equal(expected, CapabilityActionPolicy.CanCreateFaceTimeLink(
            hasSelectedChat,
            connected,
            faceTimeAvailable));
    }

    [Fact]
    public void CapabilityActionPolicyAcceptsRpcMethodAliases()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = true,
            RpcMethods = ["message.tapback", "edit", "unsend", "delete", "notify-anyways"]
        };
        var outbound = Message("server-guid", 10, "hello") with { IsFromMe = true };

        Assert.True(CapabilityActionPolicy.CanTapback(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanEdit(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanUnsend(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanDelete(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanNotifyAnyways(outbound, capabilities));
        Assert.True(CapabilityActionPolicy.CanSendRich(true, true, capabilities with
        {
            V2Ready = true,
            RpcMethods = [.. capabilities.RpcMethods, "send.rich"]
        }));
        Assert.True(CapabilityActionPolicy.CanReply(outbound, capabilities with
        {
            V2Ready = true,
            RpcMethods = [.. capabilities.RpcMethods, "send.rich"]
        }));
        Assert.True(CapabilityActionPolicy.CanSendPoll(true, true, capabilities with
        {
            V2Ready = true,
            RpcMethods = [.. capabilities.RpcMethods, "poll.send"]
        }));
        Assert.True(CapabilityActionPolicy.CanSendTyping(true, true, capabilities with
        {
            TypingIndicators = true,
            RpcMethods = [.. capabilities.RpcMethods, "typing"]
        }));
        Assert.True(CapabilityActionPolicy.CanMarkUnread(true, true, capabilities with
        {
            RpcMethods = [.. capabilities.RpcMethods, "chats.markUnread"]
        }));
        Assert.True(CapabilityActionPolicy.CanDeleteChat(true, true, capabilities with
        {
            RpcMethods = [.. capabilities.RpcMethods, "chats.delete"]
        }));
        Assert.True(CapabilityActionPolicy.CanSetGroupIcon(true, true, capabilities with
        {
            RpcMethods = [.. capabilities.RpcMethods, "group.setIcon"]
        }));
    }

    [Fact]
    public void CapabilityActionPolicyDisablesUnsupportedAdvancedMethods()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = true,
            RpcMethods = ["send"]
        };
        var outbound = Message("server-guid", 10, "hello") with { IsFromMe = true };

        Assert.False(CapabilityActionPolicy.CanTapback(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanEdit(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanUnsend(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanDelete(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanNotifyAnyways(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanMarkRead(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanMarkUnread(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanDeleteChat(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSetGroupIcon(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSendRich(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanReply(outbound, capabilities));
        Assert.False(CapabilityActionPolicy.CanReply(outbound with { Guid = "pending:rich:1" }, capabilities with
        {
            V2Ready = true,
            RpcMethods = ["send", "send.rich"]
        }));
        Assert.False(CapabilityActionPolicy.CanSendRich(true, true, new ImsgCapabilities
        {
            RpcMethods = ["send", "send.rich"]
        }));
        Assert.False(CapabilityActionPolicy.CanSendPoll(true, true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSendPoll(true, true, new ImsgCapabilities
        {
            RpcMethods = ["send", "poll.send"]
        }));
        Assert.False(CapabilityActionPolicy.CanSendTyping(true, true, capabilities with
        {
            RpcMethods = ["typing"]
        }));
        Assert.False(CapabilityActionPolicy.CanSendTyping(true, false, capabilities with
        {
            TypingIndicators = true,
            RpcMethods = ["typing"]
        }));
    }

    [Fact]
    public void CapabilityActionPolicyDisablesTapbackWhenMethodIsAdvertisedWithoutAdvancedBridge()
    {
        var capabilities = new ImsgCapabilities
        {
            V2Ready = false,
            RpcMethods = ["tapback"]
        };
        var inbound = Message("server-guid", 10, "hello") with { IsFromMe = false };

        Assert.False(CapabilityActionPolicy.CanTapback(inbound, capabilities));
    }

    [Fact]
    public void CapabilityMatrixCoversKnownUpstreamRpcMethods()
    {
        var expected = new[]
        {
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
        };
        var rows = CapabilityMatrixService.Build(new ImsgCapabilities());
        var rowMethods = rows
            .SelectMany(row => row.Methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, CapabilityMatrixService.KnownRpcMethods);
        Assert.All(expected, method => Assert.Contains(method, rowMethods));
    }

    [Fact]
    public void CapabilityMatrixKeepsBaselinePermissiveWhenRpcMethodListIsMissing()
    {
        var rows = CapabilityMatrixService.Build(new ImsgCapabilities());

        Assert.Equal("Assumed", Assert.Single(rows, row => row.Feature == "Text send").Status);
        Assert.Equal("Assumed", Assert.Single(rows, row => row.Feature == "Chat list").Status);
        Assert.Equal("Unavailable", Assert.Single(rows, row => row.Feature == "Tapback").Status);
        Assert.Contains("legacy baseline", CapabilityMatrixService.Summary(new ImsgCapabilities()));
    }

    [Fact]
    public void CapabilityMatrixShowsAdvertisedUnsupportedMethodsWithoutEnablingThem()
    {
        var capabilities = new ImsgCapabilities
        {
            TypingIndicators = true,
            RpcMethods = ["send", "send.rich", "poll.send", "typing", "chats.delete", "chats.markUnread", "message.notifyAnyways", "group.setIcon", "handles.check"]
        };
        var rows = CapabilityMatrixService.Build(capabilities);

        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Text send").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Rich send").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Poll send").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Typing indicator send").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Delete chat").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Mark unread").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Notify anyway").Status);
        Assert.Equal("Available", Assert.Single(rows, row => row.Feature == "Set group icon").Status);
        Assert.Equal("Advertised, not wired", Assert.Single(rows, row => row.Feature == "Handle check").Status);
    }

    [Fact]
    public void CapabilityMatrixRequiresTypingFeatureFlag()
    {
        var rows = CapabilityMatrixService.Build(new ImsgCapabilities
        {
            RpcMethods = ["typing"]
        });

        Assert.Equal("Unavailable", Assert.Single(rows, row => row.Feature == "Typing indicator send").Status);
    }

    [Fact]
    public void IImsgClientDoesNotExposeUnsupportedRpcWorkflows()
    {
        var methodNames = typeof(IImsgClient)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("SendTextAsync", methodNames);
        Assert.Contains("CreateChatAsync", methodNames);
        Assert.Contains("DeleteChatAsync", methodNames);
        Assert.Contains("MarkUnreadAsync", methodNames);
        Assert.Contains("SendAttachmentAsync", methodNames);
        Assert.Contains("SendPollAsync", methodNames);
        Assert.Contains("SendRichAsync", methodNames);
        Assert.Contains("SetTypingAsync", methodNames);
        Assert.Contains("SetGroupIconAsync", methodNames);
        Assert.Contains("NotifyAnywaysAsync", methodNames);
        Assert.DoesNotContain("SendTypingAsync", methodNames);
        Assert.DoesNotContain("CheckHandlesAsync", methodNames);
    }

    [Fact]
    public void WatchNotificationServiceFiltersDuplicatesFromMeAndPreSubscriptionMessages()
    {
        var service = new WatchNotificationService();
        var started = DateTimeOffset.UtcNow;
        service.Reset(started);
        var inbound = Message("inbound-guid", 10, "hello") with
        {
            IsFromMe = false,
            Date = Json($"\"{started:O}\"")
        };
        var fromMe = inbound with { Guid = "from-me-guid", IsFromMe = true };
        var reactionEvent = inbound with
        {
            Guid = "reaction-guid",
            IsReaction = true,
            ReactionType = "love",
            ReactionEmoji = "❤️",
            ReactedToGuid = inbound.Guid
        };
        var old = inbound with
        {
            Guid = "old-guid",
            Date = Json($"\"{started.AddMinutes(-5):O}\"")
        };

        Assert.True(service.ShouldShowWindowsNotification(inbound, notificationsEnabled: true));
        Assert.False(service.ShouldShowWindowsNotification(inbound, notificationsEnabled: true));
        Assert.False(service.ShouldShowWindowsNotification(fromMe, notificationsEnabled: true));
        Assert.False(service.ShouldShowWindowsNotification(reactionEvent, notificationsEnabled: true));
        Assert.False(service.ShouldShowWindowsNotification(old, notificationsEnabled: true));
        Assert.False(service.ShouldShowWindowsNotification(inbound with { Guid = "disabled-guid" }, notificationsEnabled: false));
    }

    [Fact]
    public void WatchNotificationServiceSuppressesRowsAtOrBeforePersistedCursor()
    {
        var service = new WatchNotificationService();
        var started = DateTimeOffset.UtcNow;
        service.Reset(started, lastSeenRowId: 100);
        var replayed = Message("replayed", 10, "old") with
        {
            Id = 100,
            IsFromMe = false,
            Date = Json($"\"{started:O}\"")
        };
        var current = replayed with { Id = 101, Guid = "current" };

        Assert.False(service.ShouldShowWindowsNotification(replayed, notificationsEnabled: true));
        Assert.True(service.ShouldShowWindowsNotification(current, notificationsEnabled: true));
    }

    [Fact]
    public void WatchNotificationServiceOnlyCountsCurrentInboundMessagesAsNewUnread()
    {
        var service = new WatchNotificationService();
        var started = DateTimeOffset.UtcNow;
        service.Reset(started, lastSeenRowId: 100);
        var replayed = Message("replayed", 10, "old") with
        {
            Id = 100,
            IsFromMe = false,
            Date = Json($"\"{started:O}\"")
        };
        var old = replayed with
        {
            Id = 101,
            Guid = "old",
            Date = Json($"\"{started.AddMinutes(-5):O}\"")
        };
        var notificationsDisabled = replayed with
        {
            Id = 102,
            Guid = "disabled",
            Date = Json($"\"{started:O}\"")
        };

        var replayedDecision = service.Evaluate(replayed, notificationsEnabled: true);
        var oldDecision = service.Evaluate(old, notificationsEnabled: true);
        var disabledDecision = service.Evaluate(notificationsDisabled, notificationsEnabled: false);

        Assert.False(replayedDecision.ShouldShowWindowsNotification);
        Assert.False(replayedDecision.ShouldCountAsNewUnread);
        Assert.False(oldDecision.ShouldShowWindowsNotification);
        Assert.False(oldDecision.ShouldCountAsNewUnread);
        Assert.False(disabledDecision.ShouldShowWindowsNotification);
        Assert.True(disabledDecision.ShouldCountAsNewUnread);
    }

    [Fact]
    public void WatchNotificationServiceDoesNotCountBacklogAsUnreadWithoutSubscriptionWindow()
    {
        var service = new WatchNotificationService();
        service.Reset(subscriptionStartedAtUtc: null, lastSeenRowId: 100);
        var replayed = Message("backlog", 10, "read on phone") with
        {
            Id = 101,
            IsFromMe = false,
            Date = Json($"\"{DateTimeOffset.UtcNow.AddMinutes(-30):O}\"")
        };

        var decision = service.Evaluate(replayed, notificationsEnabled: true);

        Assert.False(decision.ShouldShowWindowsNotification);
        Assert.False(decision.ShouldCountAsNewUnread);
    }

    [Fact]
    public void ConversationViewModelObservedSentMessageMatchesByGuidWhenKnown()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 5, olderPageSize: 2);
        var delivered = MessageAt("real-guid", 10, "same text", "2026-07-10T21:00:00Z") with { IsFromMe = true, Id = 1 };
        viewModel.ReplaceMessages([delivered], resetVisibleWindow: true);

        Assert.True(viewModel.HasObservedSentMessage("same text", "real-guid", DateTimeOffset.MinValue));
        Assert.False(viewModel.HasObservedSentMessage("same text", "other-guid", DateTimeOffset.MinValue));
        Assert.True(viewModel.HasObservedSentMessage("same text", null, delivered.SortDate!.Value.AddMinutes(-1)));
    }

    [Fact]
    public void ConversationViewModelKeepsLiveMessagesNewerThanAStaleHistoryRefresh()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 10, olderPageSize: 5);
        var testing = MessageAt("m-testing", 440, "testing", "2026-07-10T21:14:44Z") with { Id = 1, IsFromMe = true };
        var testAgain = MessageAt("m-again", 440, "test again", "2026-07-10T21:14:53Z") with { Id = 2, IsFromMe = true };
        var muchBetter = MessageAt("m-better", 440, "much better", "2026-07-10T21:14:58Z") with { Id = 3, IsFromMe = true };
        viewModel.ReplaceMessages([testing, testAgain], resetVisibleWindow: true);

        // Live watch delivers the two newest messages while a history refresh
        // (snapshotted before they reached chat.db) is still in flight.
        viewModel.AppendOrReplaceMessage(muchBetter);
        var wowee = MessageAt("m-wowee", 440, "Wowee", "2026-07-10T21:15:03Z") with { Id = 4, IsFromMe = true };
        viewModel.AppendOrReplaceMessage(wowee);

        // The stale refresh lands: it contains everything up to "much better"
        // but not "Wowee". The live tail must survive the replacement.
        viewModel.ReplaceMessages([testing, testAgain, muchBetter], resetVisibleWindow: true);

        Assert.Equal(
            ["testing", "test again", "much better", "Wowee"],
            viewModel.AllMessages.Select(item => item.Message.Text));
    }

    [Fact]
    public void ConversationViewModelRemovesFailedSendWhenDeliveredEchoArrives()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 5, olderPageSize: 2);
        var pendingMessage = Message("pending:abc", 10, "sending test message") with { IsFromMe = true };
        var pendingItem = viewModel.AddOrGetPendingMessage(pendingMessage);
        viewModel.MarkPendingMessageFailed(pendingItem, "Send timed out after 30 seconds.");

        var echo = MessageAt("real-guid", 10, "sending test message", "2026-07-09T20:00:00Z") with
        {
            IsFromMe = true,
            Id = 999
        };
        viewModel.AppendOrReplaceMessage(echo);

        var guids = viewModel.AllMessages.Select(item => item.Message.Guid).ToList();
        Assert.Contains("real-guid", guids);
        Assert.DoesNotContain("pending:abc", guids);
    }

    [Fact]
    public void ConversationViewModelKeepsFailedSendWhenDifferentTextArrives()
    {
        var viewModel = new ConversationViewModel(initialVisibleMessages: 5, olderPageSize: 2);
        var pendingMessage = Message("pending:abc", 10, "first message") with { IsFromMe = true };
        var pendingItem = viewModel.AddOrGetPendingMessage(pendingMessage);
        viewModel.MarkPendingMessageFailed(pendingItem, "Send timed out after 30 seconds.");

        var unrelatedEcho = MessageAt("real-guid", 10, "different message", "2026-07-09T20:00:00Z") with
        {
            IsFromMe = true,
            Id = 999
        };
        var inbound = MessageAt("inbound-guid", 10, "first message", "2026-07-09T20:01:00Z") with
        {
            IsFromMe = false,
            Id = 1000
        };
        viewModel.AppendOrReplaceMessage(unrelatedEcho);
        viewModel.AppendOrReplaceMessage(inbound);

        Assert.Contains("pending:abc", viewModel.AllMessages.Select(item => item.Message.Guid));
    }

    [Fact]
    public void ChatListStateStoreMergesTransientStateWithNewestWins()
    {
        var store = new ChatListStateStore();
        var inbound = MessageAt("m1", 42, "hello there", "2026-07-10T20:00:00Z") with { Id = 1, IsFromMe = false };

        Assert.True(store.TrackTransientUnread(inbound));
        Assert.True(store.TrackTransientLatestMessage(inbound));
        Assert.False(store.TrackTransientUnread(inbound with { IsFromMe = true, Guid = "m2" }));
        Assert.Equal(1, store.TransientUnreadMessageCount);
        Assert.Equal(1, store.BuildTransientUnreadCountsByStableId()["42"]);

        // Transient date wins only when newer than the cached one.
        var cachedDates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["42"] = DateTimeOffset.Parse("2026-07-10T21:00:00Z")
        };
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T21:00:00Z"), store.MergeLatestMessageDates(cachedDates)["42"]);
        var olderCache = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["42"] = DateTimeOffset.Parse("2026-07-10T19:00:00Z")
        };
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T20:00:00Z"), store.MergeLatestMessageDates(olderCache)["42"]);

        // Transient previews override cached previews.
        var previews = store.MergeLatestMessagePreviews(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["42"] = "stale preview"
        });
        Assert.Equal("hello there", previews["42"]);

        // A stale Mac chat row is advanced to the live latest date.
        var staleChat = Chat(42, "chat-guid", "Chat", "2026-07-10T18:00:00Z");
        var applied = store.ApplyTransientLatestMessageDate(staleChat);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T20:00:00Z"), DateTimeOffset.Parse(applied.LastMessageAt!));

        // Clearing a chat's unread removes only that chat's markers.
        Assert.True(store.ClearTransientUnreadForChat(ChatListItem.From(staleChat)));
        Assert.Equal(0, store.TransientUnreadMessageCount);
        Assert.False(store.ClearAllTransientUnread());
    }

    [Fact]
    public void ChatListStateStoreReconcilesTransientUnreadAgainstFreshMacRows()
    {
        var store = new ChatListStateStore();
        var inbound = MessageAt("m1", 42, "hello", "2026-07-10T20:00:00Z") with { Id = 1, IsFromMe = false };
        Assert.True(store.TrackTransientUnread(inbound));
        Assert.Equal(1, store.TransientUnreadMessageCount);

        // A fresh Mac row still reporting unread keeps the marker.
        var stillUnread = Chat(42, "chat-guid", "Chat", "2026-07-10T20:00:00Z") with { UnreadCountRaw = 1 };
        Assert.False(store.ReconcileTransientUnread([stillUnread]));
        Assert.Equal(1, store.TransientUnreadMessageCount);

        // Once the Mac reports the chat read (phone read synced), the stale
        // transient marker drops so badges cannot stay high.
        var nowRead = Chat(42, "chat-guid", "Chat", "2026-07-10T20:00:00Z");
        Assert.True(store.ReconcileTransientUnread([nowRead]));
        Assert.Equal(0, store.TransientUnreadMessageCount);
    }

    [Fact]
    public void BridgeRepairRunsOnlyWhenCapableButNotReady()
    {
        var capableButDown = new ImsgCapabilities
        {
            V2Ready = false,
            BridgeVersionRaw = Json("0"),
            AdvancedFeaturesRaw = Json("true"),
            Sip = Json("\"disabled\""),
            RpcMethods = ["tapback", "read"]
        };
        Assert.True(CapabilityActionPolicy.ShouldAttemptBridgeRepair(capableButDown));

        var ready = capableButDown with { V2Ready = true, BridgeVersionRaw = Json("2") };
        Assert.False(CapabilityActionPolicy.ShouldAttemptBridgeRepair(ready));

        var sipEnabled = capableButDown with { Sip = Json("\"enabled\"") };
        Assert.False(CapabilityActionPolicy.ShouldAttemptBridgeRepair(sipEnabled));

        var noAdvancedMethods = capableButDown with { RpcMethods = ["send"] };
        Assert.False(CapabilityActionPolicy.ShouldAttemptBridgeRepair(noAdvancedMethods));
    }

    [Fact]
    public async Task ConversationSyncBoundsBackgroundSweepAndReportsCappedChats()
    {
        var cache = new FakeMessageCache();
        var service = new ConversationSyncService(cache, new FakeImsgClient { IsConnected = true });
        var chats = Enumerable.Range(1, 10)
            .Select(index => Chat(index, $"guid-{index}", $"Chat {index}", $"2026-07-{index:00}T10:00:00Z"))
            .ToList();
        var fetched = new List<string>();

        var result = await service.SyncAsync(
            chats,
            new ConversationSyncOptions(HistoryLimit: 5, FailureBackoff: TimeSpan.FromMinutes(5))
            {
                MaxActiveChats = 3
            },
            (chat, limit, _) =>
            {
                fetched.Add(chat.StableId);
                return Task.FromResult(new ConversationSyncHistoryResult([], limit, IsPartial: false));
            });

        Assert.Equal(3, result.TotalChats);
        Assert.Equal(7, result.CappedOutChats);
        Assert.Equal(3, fetched.Count);
        // Recency-ordered: the newest three chats are the ones synced.
        Assert.Contains("10", fetched[0]);
    }

    [Fact]
    public void CapabilitiesDisableBridgeActionsWhenInjectionIsDown()
    {
        // Live incident shape: Mac reports advanced_features=true (capable)
        // while bridge_version=0/v2_ready=false (helper not injected), which
        // made tapback/read/typing hang. Capable-but-not-ready must gate off.
        var capableButNotReady = new ImsgCapabilities
        {
            V2Ready = false,
            AdvancedFeaturesRaw = Json("true"),
            BridgeVersionRaw = Json("0"),
            RpcMethods = ["tapback", "read", "typing"]
        };
        Assert.False(capableButNotReady.HasAdvancedBridge);

        var ready = capableButNotReady with { V2Ready = true, BridgeVersionRaw = Json("2") };
        Assert.True(ready.HasAdvancedBridge);

        var legacyContractWithoutBridgeVersion = new ImsgCapabilities
        {
            AdvancedFeaturesRaw = Json("true")
        };
        Assert.True(legacyContractWithoutBridgeVersion.HasAdvancedBridge);
    }

    [Fact]
    public void HeicAttachmentsClassifyAsImageOnceTranscodedJpegExists()
    {
        var heicAttachment = new ImsgAttachment
        {
            TransferName = "IMG_5127.HEIC",
            MimeType = "image/heic",
            Filename = "~/Library/Messages/Attachments/ab/IMG_5127.HEIC"
        };

        Assert.Equal(
            AttachmentPresentationKind.File,
            AttachmentPresentationPolicy.Classify(heicAttachment, @"C:\cache\IMG_5127.HEIC"));
        Assert.Equal(
            AttachmentPresentationKind.Image,
            AttachmentPresentationPolicy.Classify(heicAttachment, @"C:\cache\IMG_5127.HEIC.jpg"));
    }

    [Fact]
    public void HeicImageTranscoderIdentifiesHeicPathsAndDerivesJpegSiblings()
    {
        Assert.True(HeicImageTranscoder.IsHeicPath(@"C:\cache\IMG_5127.HEIC"));
        Assert.True(HeicImageTranscoder.IsHeicPath(@"C:\cache\photo.heif"));
        Assert.False(HeicImageTranscoder.IsHeicPath(@"C:\cache\IMG_5127.HEIC.jpg"));
        Assert.False(HeicImageTranscoder.IsHeicPath(@"C:\cache\photo.png"));
        Assert.False(HeicImageTranscoder.IsHeicPath(null));
        Assert.Equal(@"C:\cache\IMG_5127.HEIC.jpg", HeicImageTranscoder.GetTranscodedPath(@"C:\cache\IMG_5127.HEIC"));
    }

    [Fact]
    public void SettingsPersistSendTypingIndicatorsOptIn()
    {
        var defaults = new WinIMsgSettings().Normalize();
        Assert.False(defaults.SendTypingIndicators);

        var optedIn = (defaults with { SendTypingIndicators = true }).Normalize();
        Assert.True(optedIn.SendTypingIndicators);
    }

    [Fact]
    public void WebCompanionSettingsDefaultOffWithSanePortAndFreshTokens()
    {
        var defaults = new WinIMsgSettings().Normalize();
        Assert.False(defaults.EnableWebCompanion);
        Assert.Equal(8321, defaults.WebCompanionPort);
        Assert.Null(defaults.WebCompanionToken);

        var clamped = (defaults with { WebCompanionPort = 80 }).Normalize();
        Assert.Equal(1024, clamped.WebCompanionPort);

        var tokenA = WebCompanionService.GenerateToken();
        var tokenB = WebCompanionService.GenerateToken();
        Assert.Equal(32, tokenA.Length);
        Assert.NotEqual(tokenA, tokenB);
    }

    [Fact]
    public void VoiceRecordingServiceBuildsTimestampedM4aPathsAndDurationPolicy()
    {
        var timestamp = new DateTimeOffset(2026, 7, 9, 20, 45, 3, TimeSpan.FromHours(-4));
        var path = VoiceRecordingService.BuildRecordingFilePath(@"C:\data\voice-messages", timestamp);

        Assert.Equal(@"C:\data\voice-messages\Voice Message 2026-07-09 20-45-03.m4a", path);
        Assert.True(VoiceRecordingService.IsTooShort(TimeSpan.FromMilliseconds(300)));
        Assert.False(VoiceRecordingService.IsTooShort(TimeSpan.FromSeconds(2)));
        Assert.Equal("0:07", VoiceRecordingService.FormatElapsed(TimeSpan.FromSeconds(7)));
        Assert.Equal("1:23", VoiceRecordingService.FormatElapsed(TimeSpan.FromSeconds(83)));
        Assert.Equal("1:00:05", VoiceRecordingService.FormatElapsed(TimeSpan.FromSeconds(3605)));
        Assert.Equal("0:00", VoiceRecordingService.FormatElapsed(TimeSpan.FromSeconds(-4)));
    }

    [Fact]
    public void WatchNotificationServiceDatesRpcWatchEventsByCreatedAt()
    {
        var service = new WatchNotificationService();
        var reconnectedAt = DateTimeOffset.UtcNow;
        service.Reset(reconnectedAt, lastSeenRowId: 100);
        var replayedRpcShape = Message("rpc-replay", 10, "read on phone hours ago") with
        {
            Id = 150,
            IsFromMe = false,
            Date = null,
            CreatedAt = $"{reconnectedAt.AddHours(-3):O}"
        };
        var liveRpcShape = replayedRpcShape with
        {
            Id = 151,
            Guid = "rpc-live",
            CreatedAt = $"{reconnectedAt.AddSeconds(2):O}"
        };
        var undated = replayedRpcShape with
        {
            Id = 152,
            Guid = "rpc-undated",
            CreatedAt = null
        };

        var replayDecision = service.Evaluate(replayedRpcShape, notificationsEnabled: true);
        var liveDecision = service.Evaluate(liveRpcShape, notificationsEnabled: true);
        var undatedDecision = service.Evaluate(undated, notificationsEnabled: true);

        Assert.False(replayDecision.ShouldCountAsNewUnread);
        Assert.False(replayDecision.ShouldShowWindowsNotification);
        Assert.True(liveDecision.ShouldCountAsNewUnread);
        Assert.True(liveDecision.ShouldShowWindowsNotification);
        Assert.False(undatedDecision.ShouldCountAsNewUnread);
        Assert.False(undatedDecision.ShouldShowWindowsNotification);
    }

    [Fact]
    public void WatchNotificationServiceDoesNotCountSleepWakeReplayAsUnreadAfterReconnect()
    {
        var service = new WatchNotificationService();
        var reconnectedAt = DateTimeOffset.UtcNow;
        service.Reset(reconnectedAt, lastSeenRowId: 100);
        var sleptThrough = Message("slept-through", 10, "already handled on phone") with
        {
            Id = 150,
            IsFromMe = false,
            Date = Json($"\"{reconnectedAt.AddHours(-2):O}\"")
        };
        var live = sleptThrough with
        {
            Id = 151,
            Guid = "live",
            Date = Json($"\"{reconnectedAt.AddSeconds(5):O}\"")
        };

        var replayDecision = service.Evaluate(sleptThrough, notificationsEnabled: true);
        var liveDecision = service.Evaluate(live, notificationsEnabled: true);

        Assert.False(replayDecision.ShouldCountAsNewUnread);
        Assert.False(replayDecision.ShouldShowWindowsNotification);
        Assert.True(liveDecision.ShouldCountAsNewUnread);
        Assert.True(liveDecision.ShouldShowWindowsNotification);
    }

    [Fact]
    public async Task BridgeNotificationWorkflowCachesInboundMessagePersistsCursorAndGatesDuplicates()
    {
        var cache = new FakeMessageCache();
        var service = new BridgeNotificationWorkflowService(cache, new WatchNotificationService());
        var started = DateTimeOffset.UtcNow;
        service.ResetWatchState("profile:default", lastSeenRowId: 100, started);
        var inbound = Message("inbound-guid", 10, "hello") with
        {
            Id = 101,
            IsFromMe = false,
            Date = Json($"\"{started:O}\"")
        };

        var first = await service.ProcessAsync(inbound, notificationsEnabled: true);
        var duplicate = await service.ProcessAsync(inbound, notificationsEnabled: true);

        Assert.True(first.CachedMessage);
        Assert.True(first.PersistedCursor);
        Assert.True(first.ShouldShowWindowsNotification);
        Assert.True(first.ShouldCountAsNewUnread);
        Assert.Empty(first.Warnings);
        Assert.Equal("inbound-guid", cache.UpsertedMessages[0].Guid);
        Assert.Equal(101, cache.WatchCursors["profile:default"]);
        Assert.False(duplicate.PersistedCursor);
        Assert.False(duplicate.ShouldShowWindowsNotification);
        Assert.False(duplicate.ShouldCountAsNewUnread);
    }

    [Fact]
    public async Task BridgeNotificationWorkflowPersistsReactionCursorWithoutCachingOrNotifying()
    {
        var cache = new FakeMessageCache();
        var service = new BridgeNotificationWorkflowService(cache, new WatchNotificationService());
        var started = DateTimeOffset.UtcNow;
        service.ResetWatchState("profile:default", lastSeenRowId: null, started);
        var reactionEvent = Message("reaction-guid", 10, "Loved \"hello\"") with
        {
            Id = 102,
            IsFromMe = false,
            IsReaction = true,
            ReactionType = "love",
            ReactionEmoji = "â¤ï¸",
            ReactedToGuid = "inbound-guid",
            Date = Json($"\"{started:O}\"")
        };

        var result = await service.ProcessAsync(reactionEvent, notificationsEnabled: true);

        Assert.False(result.CachedMessage);
        Assert.True(result.PersistedCursor);
        Assert.False(result.ShouldShowWindowsNotification);
        Assert.Empty(cache.UpsertedMessages);
        Assert.Equal(102, cache.WatchCursors["profile:default"]);
    }

    [Fact]
    public async Task ConversationHistoryFetchServiceRetriesSelectedHistoryWithRecentSliceAfterTimeout()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.HistoryHandler = (_, limit, _) =>
        {
            if (limit == 500)
            {
                throw new TimeoutException("slow");
            }

            return Task.FromResult<IReadOnlyList<ImsgMessage>>([Message("recent", 7, "recent")]);
        };
        var service = new ConversationHistoryFetchService(client);

        var result = await service.FetchSelectedAsync(
            7,
            requestedLimit: 500,
            new SelectedHistoryFetchOptions(
                TimeSpan.FromMilliseconds(1),
                FallbackLimit: 50,
                TimeSpan.FromSeconds(1)));

        Assert.Equal([500, 50], client.HistoryRequestedLimits);
        Assert.Equal(50, result.History.FetchedLimit);
        Assert.Equal("recent", Assert.Single(result.History.Messages).Guid);
        Assert.Contains("timed out", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task ConversationHistoryFetchServiceFallsBackToLatestBackgroundSliceAfterRepeatedTimeouts()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.HistoryHandler = (_, limit, _) =>
        {
            if (limit is 50 or 10)
            {
                throw new TaskCanceledException("slow");
            }

            return Task.FromResult<IReadOnlyList<ImsgMessage>>([Message("latest", 8, "latest")]);
        };
        var service = new ConversationHistoryFetchService(client);

        var result = await service.FetchBackgroundSyncAsync(
            Chat(8, "chat-guid", "Alice", "2026-06-22T10:00:00Z"),
            requestedLimit: 50,
            new BackgroundHistoryFetchOptions(
                TimeSpan.FromMilliseconds(1),
                FallbackLimit: 10,
                TimeSpan.FromMilliseconds(1),
                LatestFallbackLimit: 1,
                TimeSpan.FromSeconds(1)));

        Assert.Equal([50, 10, 1], client.HistoryRequestedLimits);
        Assert.True(result.History.IsPartial);
        Assert.Equal(1, result.History.FetchedLimit);
        Assert.Equal("latest", Assert.Single(result.History.Messages).Guid);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public async Task ReactionHistoryRefreshCoordinatorCoalescesSelectedReactionRefreshes()
    {
        var selected = ChatListItem.From(Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z"));
        var enteredRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCalls = 0;
        var coordinator = new ReactionHistoryRefreshCoordinator(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            chat => chat.StableId == selected.StableId,
            async (_, cancellationToken) =>
            {
                refreshCalls++;
                enteredRefresh.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
            });
        var reaction = Message("reaction-guid", 10, "Loved \"hello\"") with
        {
            IsReaction = true,
            ReactedToGuid = "target-guid"
        };

        var queued = coordinator.TryQueueSelectedRefresh(reaction, selected);
        await enteredRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = coordinator.TryQueueSelectedRefresh(reaction, selected);

        Assert.True(queued.Queued);
        Assert.NotNull(queued.RefreshTask);
        Assert.False(duplicate.Queued);

        releaseRefresh.SetResult();
        await queued.RefreshTask!;

        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task ReactionHistoryRefreshCoordinatorSkipsRefreshWhenSelectionChanges()
    {
        var selected = ChatListItem.From(Chat(10, "chat-guid", "Alice", "2026-06-22T10:00:00Z"));
        var warnings = new List<string>();
        var refreshCalls = 0;
        var coordinator = new ReactionHistoryRefreshCoordinator(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            _ => false,
            (_, _) =>
            {
                refreshCalls++;
                return Task.CompletedTask;
            });
        var reaction = Message("reaction-guid", 10, "Loved \"hello\"") with
        {
            IsReaction = true,
            ReactedToGuid = "target-guid"
        };

        var queued = coordinator.TryQueueSelectedRefresh(reaction, selected, warnings.Add);

        Assert.True(queued.Queued);
        Assert.NotNull(queued.RefreshTask);
        await queued.RefreshTask!;
        Assert.Equal(0, refreshCalls);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ComposeDraftStoreRestoresAttachmentStateByActiveDraft()
    {
        var store = new ComposeDraftStore();
        store.Activate("chat-a");
        store.SaveActive(
            currentKey: null,
            new ComposeDraftState("photo", [@"C:\Temp\photo.jpg"], DraftRecipientText: null));
        store.Activate("chat-b");
        store.SaveActive(
            currentKey: null,
            new ComposeDraftState("doc", [@"C:\Temp\doc.pdf"], DraftRecipientText: null));

        var restored = store.Activate("chat-a");

        Assert.Equal("photo", restored.Text);
        Assert.Equal([@"C:\Temp\photo.jpg"], restored.AttachmentPaths);
        Assert.Null(restored.DraftRecipientText);
    }

    [Fact]
    public void ComposeDraftStoreUsesFallbackRecipientsAndRemovesEmptyDrafts()
    {
        var store = new ComposeDraftStore();

        var emptyDraft = store.Activate(ChatListItem.NewMessageDraftStableId, "+15551230001");
        store.SaveActive(
            currentKey: null,
            new ComposeDraftState("hello", [@"C:\Temp\photo.jpg"], "+15551230001"));
        store.SaveActive(
            currentKey: null,
            ComposeDraftState.Empty());

        Assert.Equal("+15551230001", emptyDraft.DraftRecipientText);
        Assert.False(store.Contains(ChatListItem.NewMessageDraftStableId));
    }

    [Fact]
    public void SettingsViewModelSavesGeneralSettingsAndCanReloadToCancel()
    {
        var original = new WinIMsgSettings
        {
            AutoDownloadAttachments = true,
            MinimizeToTray = true,
            EnableNotifications = true
        }.Normalize();
        var viewModel = new SettingsViewModel();
        viewModel.Load(original);

        viewModel.MarkGeneralDirty();
        var saved = viewModel.SaveGeneral(
            autoDownloadAttachments: false,
            startWithWindows: true,
            minimizeToTray: false,
            enableNotifications: false,
            showMessageContentInNotifications: false,
            syncCacheInBackground: true,
            mergeChatsByParticipants: false,
            phoneNumberRegion: "gb");

        Assert.False(saved.AutoDownloadAttachments);
        Assert.True(saved.StartWithWindows);
        Assert.False(saved.ShowMessageContentInNotifications);
        Assert.False(saved.MergeChatsByParticipants);
        Assert.Equal("GB", saved.PhoneNumberRegion);
        Assert.False(viewModel.IsGeneralDirty);

        viewModel.Load(original);

        Assert.True(viewModel.Settings.AutoDownloadAttachments);
        Assert.False(viewModel.Settings.StartWithWindows);
        Assert.True(viewModel.Settings.MergeChatsByParticipants);
        Assert.Equal("AUTO", viewModel.Settings.PhoneNumberRegion);
    }

    [Fact]
    public void SettingsViewModelSavesAddsDeletesAndSwitchesProfiles()
    {
        var first = ImsgBridgeProfile.Default() with { Id = "first", Name = "First", TargetAddresses = ["first.example.invalid"] };
        var second = ImsgBridgeProfile.Default() with { Id = "second", Name = "Second", TargetAddresses = ["second.example.invalid"] };
        var viewModel = new SettingsViewModel();
        viewModel.Load(new WinIMsgSettings { ActiveProfileId = "first", Profiles = [first, second] });

        var editedSecond = second with
        {
            Name = "Updated",
            TargetAddresses = ["updated.example.invalid"],
            RemoteAccessMode = RemoteAccessModes.TailnetSsh
        };
        var saved = viewModel.SaveProfile(editedSecond, makeActive: true);

        Assert.Equal("second", saved.ActiveProfileId);
        Assert.Equal("Updated", saved.ActiveProfile.Name);
        Assert.Equal(["updated.example.invalid"], saved.ActiveProfile.CandidateAddresses);
        Assert.Equal(RemoteAccessModes.TailnetSsh, saved.ActiveProfile.RemoteAccessMode);
        Assert.False(viewModel.IsProfileDirty);

        var added = viewModel.AddProfile(sequence: 3);

        Assert.Equal(3, viewModel.Settings.Profiles.Count);
        Assert.Equal(added.Id, viewModel.Settings.ActiveProfileId);

        viewModel.DeleteProfile(added.Id);

        Assert.Equal(2, viewModel.Settings.Profiles.Count);
        Assert.Equal("first", viewModel.SelectActiveProfile("first").ActiveProfileId);
    }

    [Fact]
    public void RemoteAccessModeNormalizesUnknownValuesToLocalNetwork()
    {
        var profile = ImsgBridgeProfile.Default() with
        {
            RemoteAccessMode = "unexpected",
            TargetAddresses = ["test-mac.example.invalid"]
        };

        var normalized = profile.Normalize();

        Assert.Equal(RemoteAccessModes.LocalNetworkSsh, normalized.RemoteAccessMode);
        Assert.Equal(RemoteAccessModes.LocalNetworkSsh, normalized.ToBridgeSettings(false, true).RemoteAccessMode);
    }

    [Fact]
    public void RemoteAccessModelServiceWarnsWhenTailnetModeHasNoTailnetTarget()
    {
        var settings = Settings() with
        {
            TargetAddresses = ["test-mac.example.invalid"],
            RemoteAccessMode = RemoteAccessModes.TailnetSsh
        };

        var check = RemoteAccessModelService.Check(settings);

        Assert.Equal(SetupCheckStatus.Warning, check.Status);
        Assert.Contains("No target looks like a Tailscale address", check.Detail);
    }

    [Fact]
    public void RemoteAccessModelServiceAcceptsTailnetTargets()
    {
        var settings = Settings() with
        {
            TargetAddresses = ["test-mac.tail123.ts.net"],
            RemoteAccessMode = RemoteAccessModes.TailnetSsh
        };

        var check = RemoteAccessModelService.Check(settings);

        Assert.Equal(SetupCheckStatus.Passed, check.Status);
        Assert.Contains("Tailscale", check.Detail);
    }

    [Fact]
    public void RemoteAccessModelServiceWarnsForDirectInternetSsh()
    {
        var settings = Settings() with
        {
            TargetAddresses = ["messages.example.com"],
            RemoteAccessMode = RemoteAccessModes.DirectInternetSsh
        };

        var check = RemoteAccessModelService.Check(settings);

        Assert.Equal(SetupCheckStatus.Warning, check.Status);
        Assert.Contains("avoid storing passwords", check.Detail);
        // The long-form security guidance moved out of the settings-pane
        // status text and lives in the option detail shown by the checklist.
        Assert.Contains("Not recommended", RemoteAccessModelService.OptionFor(RemoteAccessModes.DirectInternetSsh).Detail);
    }

    [Fact]
    public async Task MessageSendUsesChatGuidAndRejectsMismatchedResultTarget()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", "expected-guid", "iMessage", "Alice", false);
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_guid":"expected-guid"}""");

        var result = await service.SendTextAsync(target, "hello", "iMessage");

        Assert.Equal(42, client.LastSendChatId);
        Assert.Equal("iMessage;-;+1555", client.LastSendIdentifier);
        Assert.Equal("expected-guid", client.LastSendChatGuid);
        Assert.Equal("sent-guid", result.MessageGuid);

        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_guid":"wrong-guid"}""");
        await Assert.ThrowsAsync<ImsgSendResultException>(() => service.SendTextAsync(target, "hello", "iMessage"));
    }

    [Fact]
    public async Task MessageSendRejectsMismatchedReturnedChatIdOrIdentifier()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", null, "iMessage", "Alice", false);

        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_id":42,"chat_identifier":"iMessage;-;+1555"}""");
        var result = await service.SendTextAsync(target, "hello", "iMessage");

        Assert.Equal("sent-guid", result.MessageGuid);

        client.SendTextResult = Json("""{"ok":true,"guid":"wrong-chat","chat_id":43}""");
        var chatIdError = await Assert.ThrowsAsync<ImsgSendResultException>(() => service.SendTextAsync(target, "hello", "iMessage"));
        Assert.Contains("Target mismatch", chatIdError.Message);

        client.SendTextResult = Json("""{"ok":true,"guid":"wrong-identifier","chat_identifier":"iMessage;-;+1666"}""");
        var identifierError = await Assert.ThrowsAsync<ImsgSendResultException>(() => service.SendTextAsync(target, "hello", "iMessage"));
        Assert.Contains("chat_identifier", identifierError.Message);
    }

    [Fact]
    public async Task MessageSendDirectUsesRecipientToInsteadOfChatIdentifier()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid"}""");

        var result = await service.SendDirectTextAsync("+15551234567", "hello", "auto", "US");

        Assert.Equal("sent-guid", result.MessageGuid);
        Assert.Equal("+15551234567", client.LastDirectRecipient);
        Assert.Null(client.LastSendChatId);
        Assert.Null(client.LastSendIdentifier);
        Assert.Null(client.LastSendChatGuid);
    }

    [Fact]
    public async Task MessageSendFileUsesBaselineSendFileWithCaptionAndChatGuid()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", "expected-guid", "iMessage", "Alice", false);
        client.SendFileResult = Json("""{"ok":true,"guid":"file-guid","chat_guid":"expected-guid"}""");

        var result = await service.SendFileAsync(target, "/Users/testuser/Library/Messages/Attachments/imsg/photo.jpg", "caption", "imessage");

        Assert.Equal("file-guid", result.MessageGuid);
        Assert.Equal(42, client.LastSendFileChatId);
        Assert.Equal("iMessage;-;+1555", client.LastSendFileIdentifier);
        Assert.Equal("expected-guid", client.LastSendFileChatGuid);
        Assert.Equal("/Users/testuser/Library/Messages/Attachments/imsg/photo.jpg", client.LastSendFilePath);
        Assert.Equal("caption", client.LastSendFileText);
        Assert.Equal("imessage", client.LastSendFileService);

        client.SendFileResult = Json("""{"ok":true,"guid":"file-guid","chat_guid":"wrong-guid"}""");
        await Assert.ThrowsAsync<ImsgSendResultException>(() =>
            service.SendFileAsync(target, "/Users/testuser/Library/Messages/Attachments/imsg/photo.jpg", null, "imessage"));
    }

    [Fact]
    public async Task AttachmentSendWorkflowSendsTextFallbackAndContinuesAfterUploadFailure()
    {
        var upload = new FakeAttachmentUploadService();
        upload.UploadFailures[@"C:\tmp\missing.jpg"] = new InvalidOperationException("Unable to upload attachment: sftp failed");
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true,"guid":"text-guid","chat_guid":"chat-guid"}""");
        client.SendFileResult = Json("""{"ok":true,"guid":"file-guid","chat_guid":"chat-guid"}""");
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\missing.jpg", @"C:\tmp\photo.jpg"],
            "caption text",
            supportsAdvancedAttachmentFallback: false);

        Assert.Equal(2, result.AttemptedAttachments);
        Assert.Equal(1, result.SentAttachments);
        Assert.Equal(1, result.FailedAttachments);
        Assert.True(result.TextSentWithoutAttachment);
        Assert.False(result.TextSentWithAttachment);
        Assert.True(result.AnyRemoteChangeLikely);
        Assert.Equal(AttachmentSendFailureStage.Upload, result.Files[0].FailureStage);
        Assert.Equal(["caption text"], client.SentTexts);
        Assert.Equal(1, client.SendFileCalls);
        Assert.Equal("/remote/photo.jpg", client.LastSendFilePath);
        Assert.Null(client.LastSendFileText);
    }

    [Fact]
    public async Task AttachmentSendWorkflowClassifiesLocalStagingFailures()
    {
        var upload = new FakeAttachmentUploadService();
        upload.UploadFailures[@"C:\tmp\missing.jpg"] = new FileNotFoundException("Attachment file was not found.", @"C:\tmp\missing.jpg");
        var client = new FakeImsgClient { IsConnected = true };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\missing.jpg"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.Equal(AttachmentSendFailureStage.Staging, result.Files[0].FailureStage);
        Assert.Contains("staging failed", result.Files[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("staging failed", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.AnyRemoteChangeLikely);
    }

    [Fact]
    public async Task AttachmentSendWorkflowDoesNotDuplicateTextAfterRemoteSendFailure()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient
        {
            IsConnected = true,
            SendFileException = new TimeoutException("send timed out")
        };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings { RequestTimeoutSeconds = 30 },
            chat,
            [@"C:\tmp\photo.jpg"],
            "caption text",
            supportsAdvancedAttachmentFallback: false);

        Assert.Equal(0, result.FailedAttachments);
        Assert.Equal(1, result.UnconfirmedAttachments);
        Assert.False(result.TextSentWithoutAttachment);
        Assert.False(result.TextSentWithAttachment);
        Assert.Equal(0, client.SendTextCalls);
        Assert.Equal(AttachmentSendFailureStage.RemoteSendTimeout, result.Files[0].FailureStage);
        Assert.Contains("may still finish on the Mac", result.Files[0].ErrorMessage);
        Assert.Contains("still pending on the Mac", result.StatusMessage);
        Assert.False(result.HasFailures);
        Assert.True(result.HasUnconfirmedRemoteSends);
        Assert.True(result.AnyRemoteChangeLikely);
    }

    [Fact]
    public async Task AttachmentSendWorkflowClassifiesUnsupportedBaselineFileSend()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient
        {
            IsConnected = true,
            SendFileException = JsonRpcRemoteException.From(Json("""{"code":-32602,"message":"invalid parameter: file"}"""))
        };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\voice.m4a"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.Equal(1, result.FailedAttachments);
        Assert.Equal(AttachmentSendFailureStage.UnsupportedMethod, result.Files[0].FailureStage);
        Assert.Contains("Unsupported attachment send method", result.Files[0].ErrorMessage);
        Assert.Equal(1, client.SendFileCalls);
        Assert.Equal(0, client.SendAttachmentCalls);
    }

    [Fact]
    public async Task AttachmentSendWorkflowClassifiesTargetMismatch()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient { IsConnected = true };
        client.SendFileResult = Json("""{"ok":true,"guid":"file-guid","chat_guid":"wrong-guid"}""");
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "expected-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\photo.jpg"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.Equal(1, result.FailedAttachments);
        Assert.Equal(AttachmentSendFailureStage.TargetMismatch, result.Files[0].FailureStage);
        Assert.Contains("target mismatch", result.Files[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachmentSendWorkflowCleansUploadedFileAfterConfirmedSend()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient { IsConnected = true };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\photo.jpg"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.False(result.HasFailures);
        Assert.Equal(["/remote/photo.jpg"], upload.DeletedUploads);
    }

    [Fact]
    public async Task AttachmentSendWorkflowKeepsUploadedFileAfterUnconfirmedSend()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient { IsConnected = true, SendFileException = new TimeoutException("timed out") };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\photo.jpg"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.True(result.HasUnconfirmedRemoteSends);
        Assert.Empty(upload.DeletedUploads);
    }

    [Fact]
    public async Task AttachmentSendWorkflowIgnoresCleanupFailureAfterConfirmedSend()
    {
        var upload = new FakeAttachmentUploadService();
        upload.DeleteFailures["/remote/photo.jpg"] = new InvalidOperationException("rm failed");
        var client = new FakeImsgClient { IsConnected = true };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\photo.jpg"],
            text: null,
            supportsAdvancedAttachmentFallback: false);

        Assert.False(result.HasFailures);
        Assert.Equal(["/remote/photo.jpg"], upload.DeletedUploads);
    }

    [Fact]
    public void SshFileTransferBuildsRemoteAttachmentCleanupCommand()
    {
        var command = SshFileTransferService.BuildDeleteUploadedAttachmentCommand("/Users/testuser/.win-imsg/attachments/id/photo's.jpg");

        Assert.Contains("rm -f", command);
        Assert.Contains("rmdir", command);
        Assert.Contains("photo", command);
    }

    [Fact]
    public async Task AttachmentSendWorkflowKeepsAdvancedAttachmentFallbackForUnsupportedBaselineFileSend()
    {
        var upload = new FakeAttachmentUploadService();
        var client = new FakeImsgClient
        {
            IsConnected = true,
            SendFileException = JsonRpcRemoteException.From(Json("""{"code":-32602,"message":"invalid parameter: file"}"""))
        };
        var workflow = new AttachmentSendWorkflowService(upload, new MessageSendService(client));
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendAsync(
            new ImsgBridgeSettings(),
            chat,
            [@"C:\tmp\voice.m4a"],
            text: null,
            supportsAdvancedAttachmentFallback: true);

        Assert.Equal(1, result.SentAttachments);
        Assert.Equal(1, client.SendFileCalls);
        Assert.Equal(1, client.SendAttachmentCalls);
        Assert.Equal("/remote/voice.m4a", client.LastSendAttachmentPath);
        Assert.True(client.LastSendAttachmentAudio);
    }

    [Fact]
    public void CapabilityPolicyKeepsBaselineAttachmentSendEnabledForKnownSendMethod()
    {
        var capabilities = new ImsgCapabilities { RpcMethods = ["send"] };

        Assert.True(CapabilityActionPolicy.CanSend(hasSendTarget: true, connected: true, capabilities));
        Assert.True(CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: true, connected: true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: false, connected: true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: true, connected: false, capabilities));
    }

    [Fact]
    public void CapabilityPolicyDisablesAttachmentSendWhenKnownMethodsOmitBaselineSend()
    {
        var capabilities = new ImsgCapabilities { RpcMethods = ["messages.history", "send.attachment"] };

        Assert.False(CapabilityActionPolicy.CanSend(hasSendTarget: true, connected: true, capabilities));
        Assert.False(CapabilityActionPolicy.CanSendAttachment(hasSelectedChat: true, connected: true, capabilities));
    }

    [Fact]
    public async Task MessageSendCreateChatUsesAddressesAndReadsMessageGuid()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        client.SendTextResult = Json("""{"ok":true,"chat_guid":"group-guid","message_guid":"created-message-guid"}""");

        var result = await service.CreateChatAsync(["+15551230001", "+15551230002"], null, "hello group");

        Assert.Equal(["+15551230001", "+15551230002"], client.LastCreateChatAddresses);
        Assert.Equal("created-message-guid", result.MessageGuid);
    }

    [Fact]
    public async Task MessageSendPollUsesSelectedChatTargetAndReadsGuid()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        client.SendPollResult = Json("""{"ok":true,"guid":"poll-guid","chat_guid":"chat-guid"}""");
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", "chat-guid", "iMessage", "Alice", false);

        var result = await service.SendPollAsync(target, "Dinner?", ["Pizza", "Sushi"], "parent-guid");

        Assert.Equal("poll-guid", result.MessageGuid);
        Assert.Equal(42, client.LastSendPollChatId);
        Assert.Equal("iMessage;-;+1555", client.LastSendPollIdentifier);
        Assert.Equal("chat-guid", client.LastSendPollChatGuid);
        Assert.Equal("Dinner?", client.LastSendPollQuestion);
        Assert.Equal(["Pizza", "Sushi"], client.LastSendPollOptions);
        Assert.Equal("parent-guid", client.LastSendPollReplyTo);
    }

    [Fact]
    public async Task MessageSendRichUsesSelectedChatTargetAndEffect()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new MessageSendService(client);
        client.SendRichResult = Json("""{"ok":true,"guid":"rich-guid","chat_guid":"chat-guid"}""");
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", "chat-guid", "iMessage", "Alice", false);

        var formatting = new[]
        {
            new RichTextFormattingRange(0, 4, ["bold"])
        };

        var result = await service.SendRichAsync(target, "boom", "confetti", "parent-guid", formatting);

        Assert.Equal("rich-guid", result.MessageGuid);
        Assert.Equal(42, client.LastSendRichChatId);
        Assert.Equal("iMessage;-;+1555", client.LastSendRichIdentifier);
        Assert.Equal("chat-guid", client.LastSendRichChatGuid);
        Assert.Equal("boom", client.LastSendRichText);
        Assert.Equal("confetti", client.LastSendRichEffect);
        Assert.Equal("parent-guid", client.LastSendRichReplyTo);
        Assert.Equal(formatting, client.LastSendRichFormatting);
    }

    [Fact]
    public async Task MessageSendWorkflowReturnsSendStatusSummary()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_guid":"expected-guid"}""");
        client.SendStatusResult = Json("""{"send_state":"sent","ok":true}""");
        var service = new MessageSendService(client);
        var target = new ConversationTarget("stable", 42, "iMessage;-;+1555", "expected-guid", "iMessage", "Alice", false);

        var result = await service.SendTextWithStatusAsync(target, "hello", "iMessage", pollSendStatus: true);

        Assert.Equal("sent-guid", result.SendResult.MessageGuid);
        Assert.Equal("sent", result.SendStatusSummary);
        Assert.Null(result.SendStatusError);
    }

    [Fact]
    public void MessageSendStatusSummaryDoesNotTreatZeroErrorAsFailure()
    {
        var status = Json("""{"send_state":"pending","ok":true,"status_fields":{"is_finished":false,"error":0}}""");

        var summary = MessageSendService.ReadSendStatusSummary(status);

        Assert.Equal("delivery pending", summary);
    }

    [Fact]
    public void MessageSendStatusSummaryIncludesNonZeroError()
    {
        var status = Json("""{"send_state":"failed","ok":true,"status_fields":{"is_finished":true,"error":33}}""");

        var summary = MessageSendService.ReadSendStatusSummary(status);

        Assert.Equal("failed - error 33", summary);
    }

    [Fact]
    public async Task MessageSendWorkflowFailsWhenSendStatusReportsFailed()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_guid":"chat-guid"}""");
        client.SendStatusResult = Json("""{"send_state":"failed","ok":true,"status_fields":{"is_finished":true,"error":33}}""");
        var workflow = new MessageSendWorkflowService(new MessageSendService(client));
        var conversation = new ConversationViewModel();
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendTextAsync(
            conversation,
            chat,
            "hello",
            pollSendStatus: true,
            requestTimeout: TimeSpan.FromSeconds(5),
            isSelectedChat: _ => true,
            refreshHistoryAsync: (_, _) => Task.CompletedTask);

        Assert.True(result.IsFailed);
        Assert.Contains("send_state=failed", result.ErrorMessage);
        Assert.Contains("error=33", result.ErrorMessage);
        var item = Assert.Single(conversation.Messages);
        Assert.True(item.IsFailed);
        Assert.Contains("Messages is signed in", item.DeliveryStatus);
    }

    [Fact]
    public async Task MessageSendWorkflowMarksPendingFailedOnTargetMismatch()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_id":43}""");
        var workflow = new MessageSendWorkflowService(new MessageSendService(client));
        var conversation = new ConversationViewModel();
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendTextAsync(
            conversation,
            chat,
            "hello",
            pollSendStatus: true,
            requestTimeout: TimeSpan.FromSeconds(5),
            isSelectedChat: _ => true,
            refreshHistoryAsync: (_, _) => Task.CompletedTask);

        var item = Assert.Single(conversation.Messages);
        Assert.True(result.IsFailed);
        Assert.True(item.IsFailed);
        Assert.Contains("Target mismatch", result.ErrorMessage);
        Assert.Contains("Target mismatch", item.DeliveryStatus);
    }

    [Fact]
    public async Task MessageSendWorkflowMarksUnconfirmedWhenNoGuidAppearsInHistory()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true}""");
        var workflow = new MessageSendWorkflowService(new MessageSendService(client));
        var conversation = new ConversationViewModel();
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));

        var result = await workflow.SendTextAsync(
            conversation,
            chat,
            "hello",
            pollSendStatus: true,
            requestTimeout: TimeSpan.FromSeconds(5),
            isSelectedChat: _ => true,
            refreshHistoryAsync: (_, _) => Task.CompletedTask);

        Assert.True(result.IsUnconfirmed);
        var item = Assert.Single(conversation.Messages);
        Assert.True(item.IsFailed);
        Assert.Equal(MessageSendWorkflowService.SendUnconfirmedDeliveryStatus, item.DeliveryStatus);
    }

    [Fact]
    public async Task MessageSendWorkflowDoesNotMutateConversationAfterSelectedChatChanges()
    {
        var client = new FakeImsgClient { IsConnected = true };
        client.SendTextResult = Json("""{"ok":true,"guid":"sent-guid","chat_guid":"chat-guid"}""");
        var workflow = new MessageSendWorkflowService(new MessageSendService(client));
        var conversation = new ConversationViewModel();
        var chat = ChatListItem.From(Chat(42, "chat-guid", "Alice", "2026-06-22T12:00:00Z"));
        var selected = true;
        client.BeforeSendTextReturns = () => selected = false;

        var result = await workflow.SendTextAsync(
            conversation,
            chat,
            "hello",
            pollSendStatus: true,
            requestTimeout: TimeSpan.FromSeconds(5),
            isSelectedChat: _ => selected,
            refreshHistoryAsync: (_, _) =>
            {
                selected = false;
                return Task.CompletedTask;
            });

        Assert.False(result.IsFailed);
        var item = Assert.Single(conversation.Messages);
        Assert.True(item.Message.Guid?.StartsWith("pending:", StringComparison.OrdinalIgnoreCase));
        Assert.True(item.IsPending);
    }

    [Fact]
    public async Task BackgroundSyncRunsOneChatAtATimeMostRecentFirstAndRecordsFailures()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var newer = Chat(2, "newer", "Newer", "2026-06-22T12:00:00Z");
        var older = Chat(1, "older", "Older", "2026-06-21T12:00:00Z");
        client.HistoryByChatId[2] = [Message("newer-message", 2, "newer")];
        client.HistoryErrors[1] = new InvalidOperationException("boom");
        var service = new ConversationSyncService(cache, client);

        var result = await service.SyncAsync([older, newer], 25, TimeSpan.FromMinutes(5));

        Assert.Equal([2, 1], client.HistoryCallOrder);
        Assert.Equal("2", Assert.Single(cache.SuccessfulSyncs).StableId);
        Assert.Equal("1", Assert.Single(cache.FailedSyncs).StableId);
        Assert.Equal(1, result.FailedChats);
    }

    [Fact]
    public async Task BackgroundSyncPrioritizesSelectedChatBeforeGlobalOrder()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var newest = Chat(3, "newest", "Newest", "2026-06-22T12:00:00Z");
        var selected = Chat(2, "selected", "Selected", "2026-06-22T11:00:00Z");
        var oldest = Chat(1, "oldest", "Oldest", "2026-06-22T10:00:00Z");
        client.HistoryByChatId[1] = [Message("oldest-message", 1, "oldest")];
        client.HistoryByChatId[2] = [Message("selected-message", 2, "selected")];
        client.HistoryByChatId[3] = [Message("newest-message", 3, "newest")];
        var service = new ConversationSyncService(cache, client);

        await service.SyncAsync(
            [oldest, newest, selected],
            new ConversationSyncOptions(25, TimeSpan.FromMinutes(5), SelectedStableId: selected.StableId));

        Assert.Equal([2, 3, 1], client.HistoryCallOrder);
    }

    [Fact]
    public async Task BackgroundSyncSkipsExternallySatisfiedChatAndRecordsFetchedLimit()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var chat = Chat(4, "satisfied", "Satisfied", "2026-06-22T12:00:00Z");
        var service = new ConversationSyncService(cache, client);
        var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chat.StableId };

        var result = await service.SyncAsync(
            [chat],
            new ConversationSyncOptions(25, TimeSpan.FromMinutes(5), ExternallySatisfiedFetchedLimit: 500)
            {
                TryTakeExternallySatisfied = stableId => satisfied.Remove(stableId)
            });

        Assert.Empty(client.HistoryCallOrder);
        Assert.Equal((chat.StableId, 500), Assert.Single(cache.SuccessfulSyncs));
        Assert.Equal(1, result.SyncedChats);
    }

    [Fact]
    public async Task BackgroundSyncDoesNotReconcilePartialHistoryPages()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var chat = Chat(4, "partial", "Partial", "2026-06-22T12:00:00Z");
        var service = new ConversationSyncService(cache, client);

        var result = await service.SyncAsync(
            [chat],
            new ConversationSyncOptions(25, TimeSpan.FromMinutes(5)),
            historyLoader: (_, _, _) => Task.FromResult(new ConversationSyncHistoryResult(
                [Message("partial-message", 4, "partial")],
                FetchedLimit: 25,
                IsPartial: true)));

        Assert.Equal(1, result.SyncedChats);
        Assert.Equal(1, result.PartialChats);
        Assert.Single(cache.UpsertedMessages);
        Assert.Empty(cache.ReconciledWindows);
        Assert.Equal((chat.StableId, 25), Assert.Single(cache.SuccessfulSyncs));
    }

    [Fact]
    public async Task BackgroundSyncMarksShortSuccessfulHistoryAsCompleteForReconciliation()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var chat = Chat(4, "complete", "Complete", "2026-06-22T12:00:00Z");
        var service = new ConversationSyncService(cache, client);

        var result = await service.SyncAsync(
            [chat],
            new ConversationSyncOptions(25, TimeSpan.FromMinutes(5)),
            historyLoader: (_, _, _) => Task.FromResult(new ConversationSyncHistoryResult(
                [Message("remaining-message", 4, "remaining")],
                FetchedLimit: 25,
                IsPartial: false)));

        Assert.Equal(1, result.SyncedChats);
        var reconciled = Assert.Single(cache.ReconciledWindows);
        Assert.Equal(chat.StableId, reconciled.StableId);
        Assert.True(reconciled.FetchedAllAvailableHistory);
    }

    [Fact]
    public async Task BackgroundSyncSkipsChatsStillInFailureBackoff()
    {
        var cache = new FakeMessageCache();
        var client = new FakeImsgClient { IsConnected = true };
        var chat = Chat(4, "backoff", "Backoff", "2026-06-22T12:00:00Z");
        cache.SyncStates[chat.StableId] = new ChatSyncState
        {
            StableId = chat.StableId,
            LastSuccessfulSyncAt = null,
            DeepestFetchedLimit = 0,
            LastError = "previous failure",
            RetryAfter = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        var service = new ConversationSyncService(cache, client);

        var result = await service.SyncAsync([chat], 25, TimeSpan.FromMinutes(5));

        Assert.Empty(client.HistoryCallOrder);
        Assert.Equal(1, result.SkippedChats);
    }

    [Fact]
    public async Task BridgeConnectionServiceConnectsSubscribesAndReportsProgress()
    {
        var client = new FakeImsgClient();
        var capabilities = new ImsgCapabilities { RpcMethods = ["send"] };
        client.ProbeResult = ImsgProbeResult.Success("imsg 1.0.0", capabilities, "{}");
        var service = new BridgeConnectionService(client);
        var progress = new List<BridgeConnectionProgress>();

        var result = await service.ConnectAsync(
            Settings(),
            "Mac mini",
            new CapturingProgress<BridgeConnectionProgress>(progress.Add));

        Assert.True(result.IsSuccess);
        Assert.True(client.IsConnected);
        Assert.Same(capabilities, result.Capabilities);
        Assert.NotNull(result.SubscriptionStartedAtUtc);
        Assert.Equal(1, client.ProbeCalls);
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal(1, client.SubscribeCalls);
        Assert.Equal(
            [ConnectionState.Probing, ConnectionState.Connecting, ConnectionState.Connecting, ConnectionState.Connected],
            progress.Select(item => item.State));
    }

    [Fact]
    public async Task BridgeConnectionServicePassesPersistedWatchCursorToSubscribe()
    {
        var client = new FakeImsgClient();
        var service = new BridgeConnectionService(client);

        var result = await service.ConnectAsync(
            Settings(),
            "Mac mini",
            watchSinceRowId: 12345);

        Assert.True(result.IsSuccess);
        Assert.Equal(12345, client.LastSubscribeSinceRowId);
        Assert.Equal(12345, result.WatchSinceRowId);
    }

    [Fact]
    public async Task BridgeConnectionServiceTriesProfileTargetAddressesInOrder()
    {
        var client = new FakeImsgClient();
        client.ProbeResultsByHost["test-mac.example.invalid"] = ImsgProbeResult.Failed("DNS failed.", "", "no host");
        client.ProbeResultsByHost["192.0.2.10"] = ImsgProbeResult.Success("imsg 1.0.0", new ImsgCapabilities(), "{}");
        var service = new BridgeConnectionService(client);
        var settings = Settings() with
        {
            TargetAddresses = ["test-mac.example.invalid", "192.0.2.10"]
        };

        var result = await service.ConnectAsync(settings, "Mac mini");

        Assert.True(result.IsSuccess);
        Assert.Equal(["test-mac.example.invalid", "192.0.2.10"], client.ProbedHosts);
        Assert.Equal(["192.0.2.10"], client.ConnectedHosts);
        Assert.Equal("192.0.2.10", result.ConnectedSettings?.TargetAddress);
    }

    [Fact]
    public async Task BridgeConnectionServiceStopsWhenProbeFails()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Failed("Full Disk Access required.", "", "denied")
        };
        var service = new BridgeConnectionService(client);
        var progress = new List<BridgeConnectionProgress>();

        var result = await service.ConnectAsync(
            Settings(),
            "Mac mini",
            new Progress<BridgeConnectionProgress>(progress.Add));

        Assert.False(result.IsSuccess);
        Assert.False(client.IsConnected);
        Assert.Equal("Full Disk Access required.", result.Message);
        Assert.Equal(1, client.ProbeCalls);
        Assert.Equal(0, client.ConnectCalls);
        Assert.Equal(0, client.SubscribeCalls);
        Assert.Equal([ConnectionState.Probing, ConnectionState.Failed], progress.Select(item => item.State));
    }

    [Fact]
    public async Task BridgeConnectionServiceSurfacesRepeatedMacAuthKitFailure()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Failed("Unable to read chats.", "", "failed")
        };
        var issue = new MacIMessageHealthIssue(
            "iMessage on the Mac needs attention",
            "Open Messages on the Mac or restart it, then retry.",
            "AuthKit attestation failed repeatedly.",
            12);
        var service = new BridgeConnectionService(
            client,
            (_, _) => Task.FromResult<MacIMessageHealthIssue?>(issue));

        var result = await service.ConnectAsync(Settings(), "Mac mini");

        Assert.False(result.IsSuccess);
        Assert.Same(issue, result.MacHealthIssue);
        Assert.Equal(issue.UserMessage, result.Message);
    }

    [Theory]
    [InlineData("0\n", false)]
    [InlineData("2\n", false)]
    [InlineData("3\n", true)]
    [InlineData("44\n", true)]
    [InlineData("not-a-count", false)]
    public void MacIMessageHealthRequiresRepeatedAttestationFailures(string output, bool expectedIssue)
    {
        var issue = MacIMessageHealthService.ClassifyAttestationFailureCount(output);

        Assert.Equal(expectedIssue, issue is not null);
        if (issue is not null)
        {
            Assert.Contains("device token", issue.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(MacIMessageHealthService.MissingCertificateAttestation, issue.DiagnosticDetail);
        }
    }

    [Fact]
    public async Task BridgeConnectionServiceStopsWhenBaselineChatListProbeFails()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Success(
                "imsg 1.2.3",
                new ImsgCapabilities { BasicFeatures = Json("true"), RpcMethods = ["send"] },
                "{}",
                ImsgProbeCommandResult.Failed(
                    "imsg chats --limit 1 --json",
                    "PhoneNumberKit_PhoneNumberKit.bundle is missing."))
        };
        var service = new BridgeConnectionService(client);
        var progress = new List<BridgeConnectionProgress>();

        var result = await service.ConnectAsync(
            Settings(),
            "Mac mini",
            new Progress<BridgeConnectionProgress>(progress.Add));

        Assert.False(result.IsSuccess);
        Assert.False(client.IsConnected);
        Assert.Contains("imsg chats --limit 1 --json failed before RPC connect", result.Message);
        Assert.Contains("PhoneNumberKit_PhoneNumberKit.bundle is missing", result.Message);
        Assert.Equal(1, client.ProbeCalls);
        Assert.Equal(0, client.ConnectCalls);
        Assert.Equal(0, client.SubscribeCalls);
        Assert.Equal([ConnectionState.Probing, ConnectionState.Failed], progress.Select(item => item.State));
    }

    [Fact]
    public async Task BridgeConnectionServiceDisconnectsThroughClient()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new BridgeConnectionService(client);
        var progress = new List<BridgeConnectionProgress>();

        await service.DisconnectAsync(new Progress<BridgeConnectionProgress>(progress.Add));

        Assert.False(client.IsConnected);
        Assert.Equal(1, client.DisconnectCalls);
        Assert.Equal([ConnectionState.Reconnecting, ConnectionState.Disconnected], progress.Select(item => item.State));
    }

    [Fact]
    public async Task BridgeConnectionServiceCanReconnectAfterDisconnect()
    {
        var client = new FakeImsgClient { IsConnected = true };
        var service = new BridgeConnectionService(client);

        await service.DisconnectAsync();
        var result = await service.ConnectAsync(Settings(), "Mac mini");

        Assert.True(result.IsSuccess);
        Assert.True(client.IsConnected);
        Assert.Equal(1, client.DisconnectCalls);
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal(1, client.SubscribeCalls);
    }

    [Fact]
    public async Task SetupChecklistSkipsMacProbesUntilProfileHasTarget()
    {
        var client = new FakeImsgClient();
        var service = new SetupChecklistService(
            client,
            TestPaths(),
            toolProbe: FakeLocalToolProbe.AllAvailable());

        var report = await service.RunAsync(new ImsgBridgeSettings());

        Assert.Contains(report.Checks, check => check.Key == "profile" && check.Status == SetupCheckStatus.Failed);
        Assert.Contains(report.Checks, check => check.Key == "profile.remoteAccess" && check.Status == SetupCheckStatus.Skipped);
        Assert.Contains(report.Checks, check => check.Key == "mac.probe" && check.Status == SetupCheckStatus.Skipped);
        Assert.Contains(report.Checks, check => check.Key == "mac.contacts" && check.Status == SetupCheckStatus.Skipped);
        Assert.Equal(0, client.ProbeCalls);
    }

    [Fact]
    public async Task SetupChecklistReportsFallbackTargetFailureAndSuccessfulImsgProbe()
    {
        var client = new FakeImsgClient();
        var privateIp = string.Join('.', "192", "168", "50", "10");
        var unreachableHost = string.Join('.', "unreachable-test-mac", "local");
        client.ProbeResultsByHost[unreachableHost] = ImsgProbeResult.Failed("DNS failed.", "", "no host");
        client.ProbeResultsByHost[privateIp] = ImsgProbeResult.Success(
            "imsg 1.2.3",
            new ImsgCapabilities { Version = "1.2.3", BasicFeatures = Json("true"), RpcMethods = ["send"] },
            "{}",
            ImsgProbeCommandResult.Success(
                "imsg chats --limit 1 --json",
                "Read Messages chat database successfully; imsg returned 1 chat row(s)."));
        var service = new SetupChecklistService(
            client,
            TestPaths(),
            toolProbe: FakeLocalToolProbe.AllAvailable(),
            contactsProbe: (_, _) => Task.FromResult(new MacContactsAuthorizationResult("authorized", "3", "3\tauthorized")));
        var settings = Settings() with
        {
            TargetAddresses = [unreachableHost, privateIp]
        };

        var report = await service.RunAsync(settings);

        Assert.Contains(report.Checks, check => check.Key == "profile.remoteAccess" && check.Status == SetupCheckStatus.Passed);
        var macProbe = Assert.Single(report.Checks, check => check.Key == "mac.probe");
        Assert.Equal(SetupCheckStatus.Passed, macProbe.Status);
        Assert.Contains(privateIp, macProbe.Detail);
        Assert.Contains($"{unreachableHost}: DNS failed.", macProbe.Detail);
        Assert.Contains(report.Checks, check => check.Key == "mac.basic" && check.Status == SetupCheckStatus.Passed);
        Assert.Contains(report.Checks, check => check.Key == "mac.messages" && check.Status == SetupCheckStatus.Passed);
        Assert.Contains(report.Checks, check => check.Key == "mac.contacts" && check.Status == SetupCheckStatus.Passed);
        Assert.Equal([unreachableHost, privateIp], client.ProbedHosts);
    }

    [Fact]
    public async Task SetupChecklistTreatsDeniedContactsAsSshContextFailure()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Success("imsg 1.2.3", new ImsgCapabilities { RpcMethods = ["send"] }, "{}")
        };
        var service = new SetupChecklistService(
            client,
            TestPaths(),
            toolProbe: FakeLocalToolProbe.AllAvailable(),
            contactsProbe: (_, _) => Task.FromResult(new MacContactsAuthorizationResult("denied", "2", "2\tdenied")));

        var report = await service.RunAsync(Settings());

        var contacts = Assert.Single(report.Checks, check => check.Key == "mac.contacts");
        Assert.Equal(SetupCheckStatus.Failed, contacts.Status);
        Assert.Contains("Remote Login/OpenSSH/sshd", contacts.Detail);
        Assert.Contains("PPPC", contacts.Detail);
        Assert.Contains("TCC repair script", contacts.Detail);
        Assert.Contains("[fail] Mac Contacts authorization", SetupChecklistService.FormatForSettings(report));
    }

    [Fact]
    public async Task SetupChecklistReportsMessagesDatabaseFailureSeparately()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Success(
                "imsg 1.2.3",
                new ImsgCapabilities { BasicFeatures = Json("true"), RpcMethods = ["send"] },
                "{}",
                ImsgProbeCommandResult.Failed(
                    "imsg chats --limit 1 --json",
                    "Unable to read Messages chat list/history through the SSH-launched context: unable to open database file"))
        };
        var service = new SetupChecklistService(
            client,
            TestPaths(),
            toolProbe: FakeLocalToolProbe.AllAvailable(),
            contactsProbe: (_, _) => Task.FromResult(new MacContactsAuthorizationResult("authorized", "3", "3\tauthorized")));

        var report = await service.RunAsync(Settings());

        var messages = Assert.Single(report.Checks, check => check.Key == "mac.messages");
        Assert.Equal(SetupCheckStatus.Failed, messages.Status);
        Assert.Contains("imsg chats --limit 1 --json failed", messages.Detail);
        Assert.Contains("Full Disk Access", messages.Detail);
        Assert.Contains("Remote Login/OpenSSH/sshd", messages.Detail);
    }

    [Fact]
    public async Task SetupChecklistFormattedReportKeepsFirstRunGuideAndCommandPreview()
    {
        var client = new FakeImsgClient
        {
            ProbeResult = ImsgProbeResult.Success(
                "imsg 1.2.3",
                new ImsgCapabilities { BasicFeatures = Json("true"), RpcMethods = ["send"] },
                "{}",
                ImsgProbeCommandResult.Success(
                    "imsg chats --limit 1 --json",
                    "Read Messages chat database successfully; imsg returned 1 chat row(s)."))
        };
        var service = new SetupChecklistService(
            client,
            TestPaths(),
            toolProbe: FakeLocalToolProbe.AllAvailable(),
            contactsProbe: (_, _) => Task.FromResult(new MacContactsAuthorizationResult("authorized", "3", "3\tauthorized")));

        var report = await service.RunAsync(Settings());
        var formatted = SetupChecklistService.FormatForSettings(report, Settings());

        Assert.Contains("First-run path:", formatted);
        Assert.Contains("Mac commands this checklist probes:", formatted);
        Assert.Contains("imsg chats --limit 1 --json", formatted);
        Assert.Contains("sh -lc '/usr/bin/swift -e <CNContactStore authorizationStatus probe>'", formatted);
        Assert.Contains("PPPC", formatted);
        Assert.Contains("sshd-keygen-wrapper", formatted);
    }

    [Fact]
    public void ContactDiagnosticSeparatesCliContactNameProofFromMergedDisplay()
    {
        var mergedChats = new[]
        {
            Chat(1, "alice-guid", "Alice", "2026-06-22T12:00:00Z") with
            {
                Identifier = "iMessage;-;+15551230001",
                ContactName = "Alice",
                Participants = ["+15551230001"]
            },
            Chat(2, "raw-guid", "+15551230002", "2026-06-22T12:01:00Z") with
            {
                Identifier = "iMessage;-;+15551230002",
                ContactName = null,
                Participants = ["+15551230002"]
            }
        };
        var imsgChatsJson = new[]
        {
            mergedChats[0],
            mergedChats[1]
        };
        var authorization = new MacContactsAuthorizationResult("authorized", "3", "3\tauthorized");

        var diagnostic = ContactIdentityService.BuildContactNamesDiagnostic(mergedChats, imsgChatsJson, authorization);

        Assert.Contains("imsg chats --json returned contact_name on 1/2 inspected chats before cache/RPC merge", diagnostic);
        Assert.Contains("CLI one-to-one contact_name: 1/2", diagnostic);
        Assert.Contains("One-to-one exact contact_name: 1/2", diagnostic);
        Assert.Contains("one-to-one raw handles: 1/2", diagnostic);
        Assert.Contains("CLI contact_name examples: Alice", diagnostic);
        Assert.Contains("CNContactStore authorization probe returned authorized (raw=3)", diagnostic);
        Assert.Contains("1 chats still look like phone numbers", diagnostic);
    }

    [Fact]
    public void ContactDiagnosticReportsGroupNamesSeparatelyFromOneToOneNames()
    {
        var chats = new[]
        {
            Chat(1, "raw-guid", "Family Group", "2026-06-22T12:00:00Z") with
            {
                Identifier = "iMessage;-;+15551230001",
                ContactName = null,
                Participants = ["+15551230001"],
                IsGroup = false
            },
            Chat(2, "group-guid", "Family Group", "2026-06-22T12:01:00Z") with
            {
                Identifier = "iMessage;+;chat-group",
                ContactName = null,
                Participants = ["+15551230001", "+15551230002"],
                IsGroup = true
            }
        };
        var authorization = new MacContactsAuthorizationResult("authorized", "3", "3\tauthorized");

        var diagnostic = ContactIdentityService.BuildContactNamesDiagnostic(chats, chats, authorization);

        Assert.Contains("One-to-one exact contact_name: 0/1", diagnostic);
        Assert.Contains("group display names/participants: 1/1", diagnostic);
        Assert.Contains("Group names are never used as one-to-one contact names", diagnostic);
        Assert.Contains("+15551230001", diagnostic);
    }

    [Fact]
    public async Task UiBoundaryFakesCoverDialogAndFilePickerContracts()
    {
        var dialogs = new FakeDialogService { ConfirmResult = true, TextResult = "edited" };
        dialogs.PollResult = new PollComposeRequest("Dinner?", ["Pizza", "Sushi"]);
        var picker = new FakeFilePickerService { Path = @"C:\Temp\photo.jpg", Paths = [@"C:\Temp\photo.jpg", @"C:\Temp\clip.mov"] };

        Assert.True(await dialogs.ConfirmAsync("Confirm", "message"));
        Assert.Equal("edited", await dialogs.PromptTextAsync("Edit", "Text", null));
        Assert.Equal(dialogs.PollResult, await dialogs.PromptPollAsync());
        Assert.Equal(@"C:\Temp\photo.jpg", await picker.PickSingleFileAsync());
        Assert.Equal([@"C:\Temp\photo.jpg", @"C:\Temp\clip.mov"], await picker.PickFilesAsync());

        picker.Path = null;
        picker.Paths = [];

        Assert.Null(await picker.PickSingleFileAsync());
        Assert.Empty(await picker.PickFilesAsync());
    }

    [Fact]
    public async Task BridgeConnectionServicePropagatesCancellation()
    {
        var client = new FakeImsgClient { ConnectException = new OperationCanceledException("canceled") };
        var service = new BridgeConnectionService(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ConnectAsync(Settings(), "Mac mini"));
    }

    [Fact]
    public void ConversationListViewModelFiltersByChatFieldsAndRestoresOnEmptyQuery()
    {
        var viewModel = new ConversationListViewModel();
        viewModel.ReplaceChats([
            ChatListItem.From(Chat(1, "alice-guid", "Alice", "2026-06-22T12:00:00Z")),
            ChatListItem.From(Chat(2, "bob-guid", "Bob", "2026-06-22T12:00:00Z"))
        ]);

        viewModel.SearchQuery = "alice";

        Assert.Equal("Alice", Assert.Single(viewModel.Chats).DisplayName);
        Assert.Equal("1 of 2 chats match", viewModel.SearchStatus);

        viewModel.SearchQuery = string.Empty;

        Assert.Equal(2, viewModel.Chats.Count);
        Assert.Equal(string.Empty, viewModel.SearchStatus);
    }

    [Fact]
    public void ConversationListViewModelIncludesCachedMessageMatches()
    {
        var viewModel = new ConversationListViewModel();
        viewModel.ReplaceChats([
            ChatListItem.From(Chat(1, "alice-guid", "Alice", "2026-06-22T12:00:00Z")),
            ChatListItem.From(Chat(2, "bob-guid", "Bob", "2026-06-22T12:00:00Z"))
        ]);

        viewModel.SearchQuery = "receipt";
        viewModel.SetMessageSearchMatches(["2"]);

        var match = Assert.Single(viewModel.Chats);
        Assert.Equal("Bob", match.DisplayName);
        Assert.Equal("1 of 2 chats match - 1 content", viewModel.SearchStatus);
    }

    [Fact]
    public void ConversationListViewModelMatchesMergedRowsByAnySourceStableId()
    {
        var viewModel = new ConversationListViewModel();
        var olderChat = Chat(10, "older-guid", "Alice", "2026-06-22T10:00:00Z") with { Participants = ["+15135550100"] };
        var newerChat = Chat(11, "newer-guid", "Alice", "2026-06-22T11:00:00Z") with { Participants = ["513-555-0100"] };
        var merged = Assert.Single(ChatListItem.FromChats([olderChat, newerChat], mergeByParticipants: true, phoneNumberRegion: "US"));
        viewModel.ReplaceChats([
            merged,
            ChatListItem.From(Chat(12, "bob-guid", "Bob", "2026-06-22T12:00:00Z"))
        ]);

        viewModel.SearchQuery = "receipt";
        viewModel.SetMessageSearchMatches(["10"]);

        var match = Assert.Single(viewModel.Chats);
        Assert.Same(merged, match);
        Assert.True(match.IsMerged);
        Assert.Equal("1 of 2 chats match - 1 content", viewModel.SearchStatus);
    }

    [Fact]
    public void ChatListItemMergesLocalNumbersUsingConfiguredRegion()
    {
        var international = Chat(10, "gb-guid", "London", "2026-06-22T10:00:00Z") with { Participants = ["+442079460123"] };
        var local = Chat(11, "local-guid", "London", "2026-06-22T11:00:00Z") with { Participants = ["020 7946 0123"] };

        var gbMerged = ChatListItem.FromChats([international, local], mergeByParticipants: true, phoneNumberRegion: "GB");
        var usSeparated = ChatListItem.FromChats([international, local], mergeByParticipants: true, phoneNumberRegion: "US");

        Assert.Single(gbMerged);
        Assert.Equal(2, usSeparated.Count);
    }

    [Fact]
    public void ChatListItemDoesNotTreatCountryPrefixesAsGloballyDisposable()
    {
        var usNumber = Chat(10, "us-guid", "US", "2026-06-22T10:00:00Z") with { Participants = ["+15135550100"] };
        var nonUsNumber = Chat(11, "non-us-guid", "Other", "2026-06-22T11:00:00Z") with { Participants = ["+5135550100"] };

        var rows = ChatListItem.FromChats([usNumber, nonUsNumber], mergeByParticipants: true, phoneNumberRegion: "US");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ChatListItemMergesPlainInternationalTokensWithoutRegionalRewrite()
    {
        var compact = Chat(10, "compact-guid", "Compact", "2026-06-22T10:00:00Z") with { Participants = ["+15135550100"] };
        var punctuated = Chat(11, "punctuated-guid", "Punctuated", "2026-06-22T11:00:00Z") with { Participants = ["+1 (513) 555-0100"] };

        var rows = ChatListItem.FromChats([compact, punctuated], mergeByParticipants: true, phoneNumberRegion: "GB");

        Assert.Single(rows);
    }

    [Fact]
    public void ParticipantMergeKeyKeepsAlphanumericChatIdentifiersOutOfPhoneFallback()
    {
        var chat = Chat(10, "chat-guid", "Group", "2026-06-22T10:00:00Z") with
        {
            Identifier = "chat157875022300917023",
            Participants = []
        };

        Assert.Equal("chat157875022300917023", ParticipantMergeKeyBuilder.Build(chat, "US"));
    }

    [Fact]
    public void ContactIdentityMergeReplacesRawCachedContactNameFromExactDirectSource()
    {
        var cached = Chat(1, "direct-guid", "+15551230001", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = "+15551230001",
            Participants = ["+15551230001"],
            IsGroup = false
        };
        var cli = cached with { ContactName = "Alice Appleseed" };

        var merged = Assert.Single(ContactIdentityService.MergeExplicitContactNamesByChatIdentity([cached], [cli]));

        Assert.Equal("Alice Appleseed", merged.ContactName);
        Assert.Equal("Alice Appleseed", merged.DisplayName);
    }

    [Fact]
    public void ContactIdentityMergeDoesNotCopyGroupContactNameOntoDirectChat()
    {
        var direct = Chat(1, "shared-guid", "Family Group", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = null,
            Participants = ["+15551230001"],
            IsGroup = false
        };
        var groupSource = direct with
        {
            ContactName = "Family Group",
            Participants = ["+15551230001", "+15551230002"],
            IsGroup = true
        };

        var merged = Assert.Single(ContactIdentityService.MergeExplicitContactNamesByChatIdentity([direct], [groupSource]));

        Assert.Null(merged.ContactName);
        Assert.Equal("+15551230001", merged.DisplayName);
    }

    [Fact]
    public void ContactIdentityMergeBuildsUnnamedGroupNameFromParticipantContactNames()
    {
        var alice = Chat(1, "alice-guid", "+15551230001", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = "Alice Appleseed",
            Participants = ["+15551230001"],
            IsGroup = false
        };
        var bob = Chat(2, "bob-guid", "+15551230002", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230002",
            ContactName = "Bob Banana",
            Participants = ["+15551230002"],
            IsGroup = false
        };
        var group = Chat(3, "group-guid", "+15551230001, +15551230002", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;+;+15551230001,+15551230002",
            ContactName = null,
            Participants = ["+15551230001", "+15551230002"],
            IsGroup = true
        };

        var merged = Assert.Single(ContactIdentityService.MergeExplicitContactNamesByChatIdentity([group], [alice, bob]));

        Assert.Null(merged.ContactName);
        Assert.Equal("Alice Appleseed, Bob Banana", merged.Name);
        Assert.Equal("Alice Appleseed, Bob Banana", merged.DisplayName);
    }

    [Fact]
    public void ContactIdentityMergeKeepsNamedGroupDisplayName()
    {
        var alice = Chat(1, "alice-guid", "+15551230001", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = "Alice Appleseed",
            Participants = ["+15551230001"],
            IsGroup = false
        };
        var group = Chat(3, "group-guid", "4 kids", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;+;+15551230001,+15551230002",
            ContactName = null,
            Participants = ["+15551230001", "+15551230002"],
            IsGroup = true
        };

        var merged = Assert.Single(ContactIdentityService.MergeExplicitContactNamesByChatIdentity([group], [alice]));

        Assert.Null(merged.ContactName);
        Assert.Equal("4 kids", merged.Name);
        Assert.Equal("4 kids", merged.DisplayName);
    }

    [Fact]
    public void RecipientSuggestionsDoNotTrustOneToOneChatNameAsPersonName()
    {
        var direct = Chat(1, "direct-guid", "Family Group", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = null,
            Participants = ["+15551230001"],
            IsGroup = false
        };

        var suggestions = RecipientSuggestionBuilder.Build([ChatListItem.From(direct)]);

        Assert.Contains("+15551230001", suggestions);
        Assert.DoesNotContain("Family Group", suggestions);
        Assert.DoesNotContain("iMessage;-;+15551230001", suggestions);
    }

    [Fact]
    public void InboundNotificationTitleUsesTrustedChatRowOrSenderFallback()
    {
        var direct = Chat(1, "direct-guid", "Family Group", "2026-06-22T10:00:00Z") with
        {
            Identifier = "iMessage;-;+15551230001",
            ContactName = null,
            Participants = ["+15551230001"],
            IsGroup = false
        };
        var message = Message("message-guid", 1, "Hello") with
        {
            ChatName = "Family Group",
            SenderName = "Alice"
        };

        var matched = InboundNotificationIdentityPolicy.WithTrustedChatTitle(message, [ChatListItem.From(direct)]);
        var unmatched = InboundNotificationIdentityPolicy.WithTrustedChatTitle(message with { ChatId = 99 }, [ChatListItem.From(direct)]);

        Assert.Equal("+15551230001", matched.ChatName);
        Assert.Null(unmatched.ChatName);
        Assert.Equal("Alice", unmatched.DisplaySender);
    }

    [Fact]
    public void ConversationListViewModelPreservesSelectionByStableIdAcrossRefresh()
    {
        var viewModel = new ConversationListViewModel();
        var firstBob = ChatListItem.From(Chat(2, "bob-guid", "Bob", "2026-06-22T12:00:00Z"));
        viewModel.ReplaceChats([
            ChatListItem.From(Chat(1, "alice-guid", "Alice", "2026-06-22T12:00:00Z")),
            firstBob
        ]);
        viewModel.SelectedChat = firstBob;

        viewModel.ReplaceChats([
            ChatListItem.From(Chat(2, "bob-guid-updated", "Bob Updated", "2026-06-22T13:00:00Z")),
            ChatListItem.From(Chat(3, "carol-guid", "Carol", "2026-06-22T12:00:00Z"))
        ]);

        Assert.NotSame(firstBob, viewModel.SelectedChat);
        Assert.Equal("2", viewModel.SelectedChat?.StableId);
        Assert.Equal("Bob Updated", viewModel.SelectedChat?.DisplayName);
    }

    [Fact]
    public void ConversationListViewModelClearsDraftAndSelectsClickedChatAtomically()
    {
        var viewModel = new ConversationListViewModel();
        var alice = ChatListItem.From(Chat(1, "alice-guid", "Alice", "2026-06-22T12:00:00Z"));
        var bob = ChatListItem.From(Chat(2, "bob-guid", "Bob", "2026-06-22T11:00:00Z"));
        viewModel.ReplaceChats([alice, bob]);
        viewModel.StartNewMessageDraft();

        viewModel.ClearNewMessageDraft(bob);

        Assert.DoesNotContain(viewModel.Chats, chat => chat.IsNewMessageDraft);
        Assert.Same(bob, viewModel.SelectedChat);
        Assert.Equal("Bob", viewModel.SelectedChat?.DisplayName);
    }

    private static ImsgChat Chat(long id, string guid, string name, string lastMessageAt) => new()
    {
        Id = id,
        Guid = guid,
        Identifier = $"iMessage;-;{name}",
        Name = name,
        ContactName = name,
        Service = "iMessage",
        LastMessageAt = lastMessageAt
    };

    private static ImsgMessage Message(string guid, long chatId, string text) => new()
    {
        Guid = guid,
        ChatId = chatId,
        ChatIdentifier = $"chat-{chatId}",
        Text = text,
        Date = Json($"\"{DateTimeOffset.UtcNow:O}\"")
    };

    private static ImsgMessage MessageAt(string guid, long chatId, string text, string date) => new()
    {
        Guid = guid,
        ChatId = chatId,
        ChatIdentifier = $"chat-{chatId}",
        Text = text,
        Date = Json($"\"{date}\"")
    };

    private static ImsgBridgeSettings Settings() => new()
    {
        TargetAddresses = ["test-mac.example.invalid"],
        MacUser = "testuser"
    };

    private static AppDataPaths TestPaths() =>
        new(Path.Combine(Path.GetTempPath(), "WinIMsgTests", Guid.NewGuid().ToString("N")));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeLocalToolProbe : ILocalToolProbe
    {
        private readonly Dictionary<string, LocalToolProbeResult> _results;

        private FakeLocalToolProbe(Dictionary<string, LocalToolProbeResult> results)
        {
            _results = results;
        }

        public static FakeLocalToolProbe AllAvailable() => new(new(StringComparer.OrdinalIgnoreCase)
        {
            ["ssh.exe"] = LocalToolProbeResult.Available(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            ["sftp.exe"] = LocalToolProbeResult.Available(@"C:\Windows\System32\OpenSSH\sftp.exe"),
            ["scp.exe"] = LocalToolProbeResult.Available(@"C:\Windows\System32\OpenSSH\scp.exe")
        });

        public Task<LocalToolProbeResult> ProbeAsync(
            string executableName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results.TryGetValue(executableName, out var result)
                ? result
                : LocalToolProbeResult.Missing($"{executableName} missing"));
        }
    }

    private sealed class FakeAttachmentUploadService : IAttachmentUploadService
    {
        public Dictionary<string, Exception> UploadFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> DeleteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Uploads { get; } = [];

        public List<string> DeletedUploads { get; } = [];

        public Task<string> UploadAsync(
            ImsgBridgeSettings settings,
            string localPath,
            IProgress<FileTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Uploads.Add(localPath);
            if (UploadFailures.TryGetValue(localPath, out var exception))
            {
                throw exception;
            }

            progress?.Report(new FileTransferProgress("upload", localPath, $"/remote/{Path.GetFileName(localPath)}", 50, 100, IsComplete: false));
            progress?.Report(new FileTransferProgress("upload", localPath, $"/remote/{Path.GetFileName(localPath)}", 100, 100, IsComplete: true));
            return Task.FromResult($"/remote/{Path.GetFileName(localPath)}");
        }

        public Task DeleteUploadedAsync(
            ImsgBridgeSettings settings,
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            DeletedUploads.Add(remotePath);
            if (DeleteFailures.TryGetValue(remotePath, out var exception))
            {
                throw exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeImsgClient : IImsgClient
    {
        public event EventHandler<JsonRpcNotification>? NotificationReceived;

        public event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed;

        public void RaiseConnectionClosed(Exception? exception = null)
        {
            ConnectionClosed?.Invoke(this, new JsonRpcConnectionClosedEventArgs(exception));
        }

        public bool IsConnected { get; set; }

        public ImsgProbeResult ProbeResult { get; set; } =
            ImsgProbeResult.Success("imsg 0.0.0", new ImsgCapabilities(), "{}");

        public Dictionary<string, ImsgProbeResult> ProbeResultsByHost { get; } = [];

        public Exception? ProbeException { get; set; }

        public Exception? ConnectException { get; set; }

        public Exception? SubscribeException { get; set; }

        public int ProbeCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        public int SubscribeCalls { get; private set; }

        public long? LastSubscribeSinceRowId { get; private set; }

        public int DisconnectCalls { get; private set; }

        public List<string> ProbedHosts { get; } = [];

        public List<string> ConnectedHosts { get; } = [];

        public Dictionary<long, IReadOnlyList<ImsgMessage>> HistoryByChatId { get; } = [];

        public Dictionary<long, Exception> HistoryErrors { get; } = [];

        public Func<long, int, CancellationToken, Task<IReadOnlyList<ImsgMessage>>>? HistoryHandler { get; set; }

        public List<long> HistoryCallOrder { get; } = [];

        public List<int> HistoryRequestedLimits { get; } = [];

        public List<bool> HistoryConvertAttachmentsFlags { get; } = [];

        public JsonElement SendTextResult { get; set; } = Json("""{"ok":true}""");

        public JsonElement SendFileResult { get; set; } = Json("""{"ok":true}""");

        public JsonElement SendAttachmentResult { get; set; } = Json("""{"ok":true}""");

        public JsonElement SendPollResult { get; set; } = Json("""{"ok":true}""");

        public JsonElement SendRichResult { get; set; } = Json("""{"ok":true}""");

        public JsonElement SendStatusResult { get; set; } = Json("""{"status":"sent"}""");

        public Action? BeforeSendTextReturns { get; set; }

        public Exception? SendTextException { get; set; }

        public Exception? SendFileException { get; set; }

        public Exception? SendAttachmentException { get; set; }

        public Exception? SendPollException { get; set; }

        public Exception? SendRichException { get; set; }

        public int SendTextCalls { get; private set; }

        public int SendFileCalls { get; private set; }

        public int SendAttachmentCalls { get; private set; }

        public int SendPollCalls { get; private set; }

        public int SendRichCalls { get; private set; }

        public long? LastSendChatId { get; private set; }

        public string? LastSendIdentifier { get; private set; }

        public string? LastSendChatGuid { get; private set; }

        public List<string> SentTexts { get; } = [];

        public string? LastDirectRecipient { get; private set; }

        public long? LastSendFileChatId { get; private set; }

        public string? LastSendFileIdentifier { get; private set; }

        public string? LastSendFileChatGuid { get; private set; }

        public string? LastSendFilePath { get; private set; }

        public string? LastSendFileText { get; private set; }

        public string? LastSendFileService { get; private set; }

        public long? LastSendAttachmentChatId { get; private set; }

        public string? LastSendAttachmentIdentifier { get; private set; }

        public string? LastSendAttachmentChatGuid { get; private set; }

        public string? LastSendAttachmentPath { get; private set; }

        public bool? LastSendAttachmentAudio { get; private set; }

        public long? LastSendPollChatId { get; private set; }

        public string? LastSendPollIdentifier { get; private set; }

        public string? LastSendPollChatGuid { get; private set; }

        public string? LastSendPollQuestion { get; private set; }

        public IReadOnlyList<string>? LastSendPollOptions { get; private set; }

        public string? LastSendPollReplyTo { get; private set; }

        public long? LastSendRichChatId { get; private set; }

        public string? LastSendRichIdentifier { get; private set; }

        public string? LastSendRichChatGuid { get; private set; }

        public string? LastSendRichText { get; private set; }

        public string? LastSendRichEffect { get; private set; }

        public string? LastSendRichReplyTo { get; private set; }

        public IReadOnlyList<RichTextFormattingRange>? LastSendRichFormatting { get; private set; }

        public int TapbackCalls { get; private set; }

        public long? LastTapbackChatId { get; private set; }

        public string? LastTapbackIdentifier { get; private set; }

        public string? LastTapbackChatGuid { get; private set; }

        public string? LastTapbackMessageGuid { get; private set; }

        public string? LastTapbackReaction { get; private set; }

        public bool? LastTapbackRemove { get; private set; }

        public int EditMessageCalls { get; private set; }

        public IReadOnlyList<string>? LastCreateChatAddresses { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ImsgProbeResult> ProbeAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            ProbedHosts.Add(settings.TargetAddress);
            if (ProbeException is not null)
            {
                throw ProbeException;
            }

            return Task.FromResult(ProbeResultsByHost.TryGetValue(settings.TargetAddress, out var result)
                ? result
                : ProbeResult);
        }

        public Task ConnectAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            ConnectedHosts.Add(settings.TargetAddress);
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ImsgChat>> ListChatsAsync(int limit = 10000, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImsgChat>>([]);

        public Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
            long chatId,
            int limit = 100,
            bool includeAttachments = true,
            bool convertAttachments = false,
            bool includeReactions = true,
            CancellationToken cancellationToken = default)
        {
            HistoryCallOrder.Add(chatId);
            HistoryRequestedLimits.Add(limit);
            HistoryConvertAttachmentsFlags.Add(convertAttachments);
            if (HistoryHandler is not null)
            {
                return HistoryHandler(chatId, limit, cancellationToken);
            }

            if (HistoryErrors.TryGetValue(chatId, out var error))
            {
                throw error;
            }

            return Task.FromResult(HistoryByChatId.GetValueOrDefault(chatId) ?? []);
        }

        public Task<JsonElement> SubscribeAsync(long? chatId = null, long? sinceRowId = null, bool includeAttachments = true, bool includeReactions = true, CancellationToken cancellationToken = default)
        {
            SubscribeCalls++;
            LastSubscribeSinceRowId = sinceRowId;
            if (SubscribeException is not null)
            {
                throw SubscribeException;
            }

            return Task.FromResult(Json("""{"ok":true}"""));
        }

        public Task<JsonElement> SendTextAsync(
            long? chatId,
            string? chatIdentifier,
            string text,
            string transport = "auto",
            string? service = null,
            string? chatGuid = null,
            CancellationToken cancellationToken = default)
        {
            SendTextCalls++;
            SentTexts.Add(text);
            LastSendChatId = chatId;
            LastSendIdentifier = chatIdentifier;
            LastSendChatGuid = chatGuid;
            BeforeSendTextReturns?.Invoke();
            if (SendTextException is not null)
            {
                throw SendTextException;
            }

            return Task.FromResult(SendTextResult);
        }

        public Task<JsonElement> SendDirectTextAsync(
            string recipient,
            string text,
            string transport = "auto",
            string? service = null,
            string? region = null,
            CancellationToken cancellationToken = default)
        {
            LastDirectRecipient = recipient;
            BeforeSendTextReturns?.Invoke();
            return Task.FromResult(SendTextResult);
        }

        public Task<JsonElement> SendFileAsync(
            long? chatId,
            string? chatIdentifier,
            string remoteFilePath,
            string? text = null,
            string transport = "auto",
            string? service = null,
            string? chatGuid = null,
            CancellationToken cancellationToken = default)
        {
            SendFileCalls++;
            LastSendFileChatId = chatId;
            LastSendFileIdentifier = chatIdentifier;
            LastSendFileChatGuid = chatGuid;
            LastSendFilePath = remoteFilePath;
            LastSendFileText = text;
            LastSendFileService = service;
            if (SendFileException is not null)
            {
                throw SendFileException;
            }

            return Task.FromResult(SendFileResult);
        }

        public Task<JsonElement> CreateChatAsync(
            IReadOnlyList<string> addresses,
            string? name = null,
            string? text = null,
            CancellationToken cancellationToken = default)
        {
            LastCreateChatAddresses = addresses.ToList();
            BeforeSendTextReturns?.Invoke();
            return Task.FromResult(SendTextResult);
        }

        public Task<JsonElement> DeleteChatAsync(long? chatId, string? chatIdentifier, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> MarkUnreadAsync(long? chatId, string? chatIdentifier, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> SendAttachmentAsync(long? chatId, string? chatIdentifier, string remoteFilePath, bool audio, string? replyTo, string? chatGuid = null, CancellationToken cancellationToken = default)
        {
            SendAttachmentCalls++;
            LastSendAttachmentChatId = chatId;
            LastSendAttachmentIdentifier = chatIdentifier;
            LastSendAttachmentChatGuid = chatGuid;
            LastSendAttachmentPath = remoteFilePath;
            LastSendAttachmentAudio = audio;
            if (SendAttachmentException is not null)
            {
                throw SendAttachmentException;
            }

            return Task.FromResult(SendAttachmentResult);
        }

        public Task<JsonElement> SendPollAsync(
            long? chatId,
            string? chatIdentifier,
            string question,
            IReadOnlyList<string> options,
            string? replyTo = null,
            string? chatGuid = null,
            CancellationToken cancellationToken = default)
        {
            SendPollCalls++;
            LastSendPollChatId = chatId;
            LastSendPollIdentifier = chatIdentifier;
            LastSendPollChatGuid = chatGuid;
            LastSendPollQuestion = question;
            LastSendPollOptions = options.ToList();
            LastSendPollReplyTo = replyTo;
            if (SendPollException is not null)
            {
                throw SendPollException;
            }

            return Task.FromResult(SendPollResult);
        }

        public Task<JsonElement> SendRichAsync(
            long? chatId,
            string? chatIdentifier,
            string text,
            string? effect = null,
            string? replyTo = null,
            IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
            string? chatGuid = null,
            CancellationToken cancellationToken = default)
        {
            SendRichCalls++;
            LastSendRichChatId = chatId;
            LastSendRichIdentifier = chatIdentifier;
            LastSendRichChatGuid = chatGuid;
            LastSendRichText = text;
            LastSendRichEffect = effect;
            LastSendRichReplyTo = replyTo;
            LastSendRichFormatting = textFormatting?.ToList();
            if (SendRichException is not null)
            {
                throw SendRichException;
            }

            return Task.FromResult(SendRichResult);
        }

        public Task<JsonElement> SetTypingAsync(long? chatId, string? chatIdentifier, bool typing, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> TapbackAsync(long? chatId, string? chatIdentifier, string messageGuid, string reaction, bool remove, string? chatGuid = null, CancellationToken cancellationToken = default)
        {
            TapbackCalls++;
            LastTapbackChatId = chatId;
            LastTapbackIdentifier = chatIdentifier;
            LastTapbackChatGuid = chatGuid;
            LastTapbackMessageGuid = messageGuid;
            LastTapbackReaction = reaction;
            LastTapbackRemove = remove;
            return Task.FromResult(Json("""{"ok":true}"""));
        }

        public Task<JsonElement> MarkReadAsync(long? chatId, string? chatIdentifier, string? messageGuid = null, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> EditMessageAsync(long? chatId, string? chatIdentifier, string messageGuid, string text, string? chatGuid = null, CancellationToken cancellationToken = default)
        {
            EditMessageCalls++;
            return Task.FromResult(Json("""{"ok":true}"""));
        }

        public Task<JsonElement> UnsendMessageAsync(long? chatId, string? chatIdentifier, string messageGuid, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> DeleteMessageAsync(long? chatId, string? chatIdentifier, string messageGuid, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> NotifyAnywaysAsync(long? chatId, string? chatIdentifier, string messageGuid, string? chatGuid = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(SendStatusResult);

        public Task<JsonElement> RenameGroupAsync(string chatGuid, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> SetGroupIconAsync(string chatGuid, string? remoteFilePath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> AddParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> RemoveParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public Task<JsonElement> LeaveGroupAsync(string chatGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(Json("""{"ok":true}"""));

        public void RaiseNotification(JsonRpcNotification notification) => NotificationReceived?.Invoke(this, notification);
    }

    private sealed class FakeDialogService : IUserDialogService
    {
        public bool ConfirmResult { get; set; }

        public TapbackChoice? TapbackResult { get; set; }

        public string? TextResult { get; set; }

        public PollComposeRequest? PollResult { get; set; }

        public NewChatRequest? NewChatResult { get; set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmResult);

        public Task<TapbackChoice?> PromptTapbackAsync() => Task.FromResult(TapbackResult);

        public Task<string?> PromptTextAsync(string title, string header, string? initialValue) => Task.FromResult(TextResult);

        public Task<PollComposeRequest?> PromptPollAsync() => Task.FromResult(PollResult);

        public Task<NewChatRequest?> PromptNewChatAsync() => Task.FromResult(NewChatResult);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public string? Path { get; set; }

        public IReadOnlyList<string> Paths { get; set; } = [];

        public Task<string?> PickSingleFileAsync(CancellationToken cancellationToken = default) => Task.FromResult(Path);

        public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Paths);
    }

    private sealed class CapturingProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed class FakeMessageCache : IMessageCache
    {
        public List<ImsgChat> Chats { get; } = [];

        public Dictionary<string, IReadOnlyList<ImsgMessage>> MessagesByStableId { get; } = [];

        public Dictionary<string, ChatSyncState> SyncStates { get; } = [];

        public Dictionary<string, long> WatchCursors { get; } = [];

        public List<ImsgMessage> UpsertedMessages { get; } = [];

        public List<(string StableId, IReadOnlyList<ImsgMessage> Messages, bool FetchedAllAvailableHistory)> ReconciledWindows { get; } = [];

        public List<(string StableId, int Limit)> SuccessfulSyncs { get; } = [];

        public List<(string StableId, string Error, DateTimeOffset RetryAfter)> FailedSyncs { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertChatsAsync(IEnumerable<ImsgChat> chats, CancellationToken cancellationToken = default)
        {
            Chats.Clear();
            Chats.AddRange(chats);
            return Task.CompletedTask;
        }

        public Task UpsertMessagesAsync(IEnumerable<ImsgMessage> messages, CancellationToken cancellationToken = default)
        {
            UpsertedMessages.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<int> ReconcileMessagesForFetchedWindowAsync(
            string chatStableId,
            IEnumerable<ImsgMessage> fetchedMessages,
            bool fetchedAllAvailableHistory = false,
            CancellationToken cancellationToken = default)
        {
            ReconciledWindows.Add((chatStableId, fetchedMessages.ToList(), fetchedAllAvailableHistory));
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<ImsgChat>> GetChatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImsgChat>>(Chats);

        public Task<IReadOnlyList<ImsgMessage>> GetMessagesAsync(string chatStableId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MessagesByStableId.GetValueOrDefault(chatStableId) ?? []);

        public Task<IReadOnlyList<ImsgMessage>> GetRecentMessagesAsync(string chatStableId, int limit, CancellationToken cancellationToken = default)
        {
            var messages = MessagesByStableId.GetValueOrDefault(chatStableId) ?? [];
            return Task.FromResult<IReadOnlyList<ImsgMessage>>(messages.TakeLast(Math.Max(0, limit)).ToList());
        }

        public Task<IReadOnlyDictionary<string, string>> GetLatestMessageTextByChatStableIdAsync(CancellationToken cancellationToken = default)
        {
            var previews = MessagesByStableId
                .Select(pair => new
                {
                    StableId = pair.Key,
                    Message = pair.Value
                        .Where(static message => !string.IsNullOrWhiteSpace(message.Text))
                        .OrderByDescending(static message => message.SortDate ?? DateTimeOffset.MinValue)
                        .ThenByDescending(static message => message.Id ?? 0)
                        .FirstOrDefault()
                })
                .Where(static pair => pair.Message is not null)
                .ToDictionary(
                    static pair => pair.StableId,
                    static pair => pair.Message!.Text!,
                    StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(previews);
        }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestMessageDateByChatStableIdAsync(CancellationToken cancellationToken = default)
        {
            var dates = MessagesByStableId
                .Select(pair => new
                {
                    StableId = pair.Key,
                    Message = pair.Value
                        .Where(static message => message.SortDate is not null)
                        .OrderByDescending(static message => message.SortDate ?? DateTimeOffset.MinValue)
                        .ThenByDescending(static message => message.Id ?? 0)
                        .FirstOrDefault()
                })
                .Where(static pair => pair.Message is not null)
                .ToDictionary(
                    static pair => pair.StableId,
                    static pair => pair.Message!.SortDate!.Value,
                    StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(dates);
        }

        public Task<IReadOnlyList<ImsgMessage>> GetMessagesBeforeAsync(
            string chatStableId,
            string beforeDateValue,
            long? beforeMessageRowId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var beforeDate = DateTimeOffset.TryParse(beforeDateValue, out var parsed)
                ? parsed
                : DateTimeOffset.MaxValue;
            var messages = (MessagesByStableId.GetValueOrDefault(chatStableId) ?? [])
                .Where(message =>
                    message.SortDate is not null &&
                    (message.SortDate.Value < beforeDate ||
                        (message.SortDate.Value == beforeDate &&
                            beforeMessageRowId is not null &&
                            (message.Id ?? 0) < beforeMessageRowId.Value)))
                .TakeLast(Math.Max(0, limit))
                .ToList();
            return Task.FromResult<IReadOnlyList<ImsgMessage>>(messages);
        }

        public Task<IReadOnlySet<string>> SearchChatStableIdsByMessageContentAsync(string query, int limit = 5000, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<IReadOnlyDictionary<string, ChatSyncState>> GetChatSyncStatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ChatSyncState>>(SyncStates);

        public Task<long?> GetWatchCursorAsync(string scope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(WatchCursors.TryGetValue(scope, out var cursor) ? cursor : (long?)null);
        }

        public Task SaveWatchCursorAsync(string scope, long rowId, CancellationToken cancellationToken = default)
        {
            WatchCursors[scope] = WatchCursors.TryGetValue(scope, out var existing)
                ? Math.Max(existing, rowId)
                : rowId;
            return Task.CompletedTask;
        }

        public Task MarkChatSyncSucceededAsync(string stableId, int deepestFetchedLimit, CancellationToken cancellationToken = default)
        {
            SuccessfulSyncs.Add((stableId, deepestFetchedLimit));
            return Task.CompletedTask;
        }

        public Task MarkChatSyncFailedAsync(string stableId, string error, DateTimeOffset retryAfter, CancellationToken cancellationToken = default)
        {
            FailedSyncs.Add((stableId, error, retryAfter));
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Chats.Clear();
            MessagesByStableId.Clear();
            SyncStates.Clear();
            WatchCursors.Clear();
            return Task.CompletedTask;
        }
    }
}
