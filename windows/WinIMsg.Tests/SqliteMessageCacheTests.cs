using Microsoft.Data.Sqlite;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Tests;

public sealed class SqliteMessageCacheTests
{
    [Fact]
    public async Task CachePersistsChatsAndMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        var chat = new ImsgChat
        {
            Id = 7,
            Identifier = "iMessage;-;+1555",
            Guid = "iMessage;-;+1555",
            ContactName = "Alice",
            Participants = ["+1555"]
        };
        var message = new ImsgMessage
        {
            Guid = "guid-1",
            ChatId = 7,
            ChatIdentifier = chat.Identifier,
            Text = "hello",
            IsFromMe = true
        };

        await cache.UpsertChatsAsync([chat]);
        await cache.UpsertMessagesAsync([message]);

        Assert.Equal("Alice", Assert.Single(await cache.GetChatsAsync()).DisplayName);
        Assert.Equal("hello", Assert.Single(await cache.GetMessagesAsync("7")).Text);

        await cache.ClearAsync();

        Assert.Empty(await cache.GetChatsAsync());
        Assert.Empty(await cache.GetMessagesAsync("7"));
    }

    [Fact]
    public async Task CacheBackfillsCreatedAtDateValuesForExistingRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "cache.db");
        Directory.CreateDirectory(root);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE messages (
                    guid TEXT PRIMARY KEY NOT NULL,
                    chat_stable_id TEXT NOT NULL,
                    chat_id INTEGER NULL,
                    text TEXT NULL,
                    is_from_me INTEGER NOT NULL,
                    date_value TEXT NULL,
                    payload_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO messages (guid, chat_stable_id, chat_id, text, is_from_me, date_value, payload_json, updated_at)
                VALUES ('old', '7', 7, 'old', 0, '', '{"guid":"old","chat_id":7,"text":"old","created_at":"2026-06-20T10:00:00Z"}', '2026-06-20T10:00:00Z');
                INSERT INTO messages (guid, chat_stable_id, chat_id, text, is_from_me, date_value, payload_json, updated_at)
                VALUES ('new', '7', 7, 'new', 0, '', '{"guid":"new","chat_id":7,"text":"new","created_at":"2026-06-23T22:14:25.887Z"}', '2026-06-23T22:14:25Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var cache = new SqliteMessageCache(databasePath);
        await cache.InitializeAsync();

        Assert.Equal(["old", "new"], (await cache.GetMessagesAsync("7")).Select(message => message.Text));

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync();
        var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM messages WHERE date_value IS NOT NULL AND date_value <> ''";
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReconcileMessagesForFetchedWindowDeletesOnlyMissingRowsInsideFetchedRange()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        await cache.UpsertMessagesAsync([
            Message("older", 7, "older", "2026-06-24T09:00:00Z"),
            Message("present-a", 7, "present a", "2026-06-24T10:00:00Z"),
            Message("deleted-inside-window", 7, "deleted", "2026-06-24T10:01:00Z"),
            Message("present-b", 7, "present b", "2026-06-24T10:02:00Z"),
            Message("newer", 7, "newer", "2026-06-24T10:03:00Z")
        ]);

        var removed = await cache.ReconcileMessagesForFetchedWindowAsync(
            "7",
            [
                Message("present-a", 7, "present a", "2026-06-24T10:00:00Z"),
                Message("present-b", 7, "present b", "2026-06-24T10:02:00Z")
            ]);

        var remaining = await cache.GetMessagesAsync("7");

        Assert.Equal(1, removed);
        Assert.Equal(["older", "present-a", "present-b", "newer"], remaining.Select(message => message.Guid));
    }

    [Fact]
    public async Task ReconcileCompleteHistoryDeletesMissingRowsEvenWithSingleFetchedMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        await cache.UpsertMessagesAsync([
            Message("deleted-old", 7, "deleted old", "2026-06-24T09:00:00Z"),
            Message("remaining", 7, "remaining", "2026-06-24T10:00:00Z"),
            Message("deleted-new", 7, "deleted new", "2026-06-24T11:00:00Z")
        ]);

        var removed = await cache.ReconcileMessagesForFetchedWindowAsync(
            "7",
            [Message("remaining", 7, "remaining", "2026-06-24T10:00:00Z")],
            fetchedAllAvailableHistory: true);

        var remaining = await cache.GetMessagesAsync("7");

        Assert.Equal(2, removed);
        Assert.Equal(["remaining"], remaining.Select(message => message.Guid));
    }

    [Fact]
    public async Task ReconcileCompleteEmptyHistoryDeletesCachedRowsForChat()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        await cache.UpsertMessagesAsync([
            Message("deleted-a", 7, "deleted a", "2026-06-24T09:00:00Z"),
            Message("deleted-b", 7, "deleted b", "2026-06-24T10:00:00Z"),
            Message("other-chat", 8, "other", "2026-06-24T10:00:00Z")
        ]);

        var removed = await cache.ReconcileMessagesForFetchedWindowAsync(
            "7",
            [],
            fetchedAllAvailableHistory: true);

        Assert.Equal(2, removed);
        Assert.Empty(await cache.GetMessagesAsync("7"));
        Assert.Equal(["other-chat"], (await cache.GetMessagesAsync("8")).Select(message => message.Guid));
    }

    [Fact]
    public async Task RecentAndOlderMessagePagesUseStableDateAndRowIdCursor()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        await cache.UpsertMessagesAsync(Enumerable.Range(1, 6)
            .Select(index => Message($"m{index}", 7, index.ToString(), $"2026-06-24T10:{index:00}:00Z") with { Id = index }));

        var recent = await cache.GetRecentMessagesAsync("7", 3);
        var older = await cache.GetMessagesBeforeAsync("7", recent[0].DateText, recent[0].Id, 2);

        Assert.Equal(["4", "5", "6"], recent.Select(message => message.Text));
        Assert.Equal(["2", "3"], older.Select(message => message.Text));
    }

    [Fact]
    public async Task SearchIndexCoversChatsMessagesUpdatesAndReconciledDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();
        var chat = new ImsgChat
        {
            Id = 7,
            Identifier = "iMessage;-;+15551234567",
            Guid = "iMessage;-;+15551234567",
            ContactName = "Alice Appleseed",
            Participants = ["+15551234567"]
        };

        await cache.UpsertChatsAsync([chat]);
        await cache.UpsertMessagesAsync([
            Message("receipt", 7, "dinner receipt", "2026-06-24T10:00:00Z"),
            Message("mutable", 7, "oldbudget", "2026-06-24T10:01:00Z")
        ]);

        Assert.Contains("7", await cache.SearchChatStableIdsByMessageContentAsync("Alice"));
        Assert.Contains("7", await cache.SearchChatStableIdsByMessageContentAsync("receipt"));

        await cache.UpsertMessagesAsync([Message("mutable", 7, "updated invoice", "2026-06-24T10:01:00Z")]);
        Assert.DoesNotContain("7", await cache.SearchChatStableIdsByMessageContentAsync("oldbudget"));
        Assert.Contains("7", await cache.SearchChatStableIdsByMessageContentAsync("invoice"));

        await cache.ReconcileMessagesForFetchedWindowAsync(
            "7",
            [Message("mutable", 7, "updated invoice", "2026-06-24T10:01:00Z")],
            fetchedAllAvailableHistory: true);

        Assert.DoesNotContain("7", await cache.SearchChatStableIdsByMessageContentAsync("receipt"));
    }

    [Fact]
    public async Task WatchCursorPersistsPerScopeAndDoesNotMoveBackward()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-tests", Guid.NewGuid().ToString("N"));
        var cache = new SqliteMessageCache(Path.Combine(root, "cache.db"));
        await cache.InitializeAsync();

        await cache.SaveWatchCursorAsync("profile:one", 20);
        await cache.SaveWatchCursorAsync("profile:two", 7);
        await cache.SaveWatchCursorAsync("profile:one", 18);
        await cache.SaveWatchCursorAsync("profile:one", 25);

        Assert.Equal(25, await cache.GetWatchCursorAsync("profile:one"));
        Assert.Equal(7, await cache.GetWatchCursorAsync("profile:two"));
        Assert.Null(await cache.GetWatchCursorAsync("profile:missing"));

        await cache.ClearAsync();

        Assert.Null(await cache.GetWatchCursorAsync("profile:one"));
    }

    [Fact]
    public void ImsgMessageDateTextUsesCreatedAtWhenDateIsAbsent()
    {
        var message = System.Text.Json.JsonSerializer.Deserialize<ImsgMessage>(
            """{"guid":"message-guid","created_at":"2026-06-23T22:14:25.887Z"}""",
            ImsgJson.Options);

        Assert.NotNull(message);
        Assert.Equal("2026-06-23T22:14:25.8870000+00:00", message!.DateText);
    }

    private static ImsgMessage Message(string guid, long chatId, string text, string createdAt) => new()
    {
        Guid = guid,
        ChatId = chatId,
        Text = text,
        CreatedAt = createdAt
    };
}
