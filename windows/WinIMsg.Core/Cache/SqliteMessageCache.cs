using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Core.Cache;

public sealed class SqliteMessageCache
{
    private readonly string _databasePath;

    public SqliteMessageCache(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // WAL lets UI reads proceed while background sync writes; NORMAL
        // synchronous is the recommended pairing for WAL durability/speed.
        var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragmas.ExecuteNonQueryAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chats (
                stable_id TEXT PRIMARY KEY NOT NULL,
                chat_id INTEGER NULL,
                display_name TEXT NOT NULL,
                service TEXT NULL,
                last_message_at TEXT NULL,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                guid TEXT PRIMARY KEY NOT NULL,
                chat_stable_id TEXT NOT NULL,
                chat_id INTEGER NULL,
                message_row_id INTEGER NULL,
                text TEXT NULL,
                is_from_me INTEGER NOT NULL,
                date_value TEXT NULL,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_messages_chat_stable_id_date
            ON messages(chat_stable_id, date_value);

            CREATE TABLE IF NOT EXISTS chat_sync_state (
                stable_id TEXT PRIMARY KEY NOT NULL,
                last_successful_sync_at TEXT NULL,
                deepest_fetched_limit INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                retry_after TEXT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_state (
                scope TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(scope, key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureMessageRowIdColumnAsync(connection, cancellationToken);
        await EnsureMessageRowIdIndexAsync(connection, cancellationToken);
        await BackfillMessageDateValuesAsync(connection, cancellationToken);
        await BackfillMessageRowIdsAsync(connection, cancellationToken);
        await EnsureSearchIndexAsync(connection, cancellationToken);
    }

    public async Task UpsertChatsAsync(IEnumerable<ImsgChat> chats, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var chat in chats)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO chats (stable_id, chat_id, display_name, service, last_message_at, payload_json, updated_at)
                VALUES ($stable_id, $chat_id, $display_name, $service, $last_message_at, $payload_json, $updated_at)
                ON CONFLICT(stable_id) DO UPDATE SET
                    chat_id = excluded.chat_id,
                    display_name = excluded.display_name,
                    service = excluded.service,
                    last_message_at = excluded.last_message_at,
                    payload_json = excluded.payload_json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$stable_id", chat.StableId);
            command.Parameters.AddWithValue("$chat_id", DbValue(chat.Id));
            command.Parameters.AddWithValue("$display_name", chat.DisplayName);
            command.Parameters.AddWithValue("$service", DbValue(chat.Service));
            command.Parameters.AddWithValue("$last_message_at", DbValue(chat.LastMessageAt));
            command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(chat, ImsgJson.Options));
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertMessagesAsync(IEnumerable<ImsgMessage> messages, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var message in messages.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Guid)))
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO messages (guid, chat_stable_id, chat_id, message_row_id, text, is_from_me, date_value, payload_json, updated_at)
                VALUES ($guid, $chat_stable_id, $chat_id, $message_row_id, $text, $is_from_me, $date_value, $payload_json, $updated_at)
                ON CONFLICT(guid) DO UPDATE SET
                    chat_stable_id = excluded.chat_stable_id,
                    chat_id = excluded.chat_id,
                    message_row_id = excluded.message_row_id,
                    text = excluded.text,
                    is_from_me = excluded.is_from_me,
                    date_value = excluded.date_value,
                    payload_json = excluded.payload_json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$guid", message.Guid!);
            command.Parameters.AddWithValue("$chat_stable_id", message.ChatStableId);
            command.Parameters.AddWithValue("$chat_id", DbValue(message.ChatId));
            command.Parameters.AddWithValue("$message_row_id", DbValue(message.Id));
            command.Parameters.AddWithValue("$text", DbValue(message.Text));
            command.Parameters.AddWithValue("$is_from_me", message.IsFromMe ? 1 : 0);
            command.Parameters.AddWithValue("$date_value", DbValue(message.DateText));
            command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(message, ImsgJson.Options));
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> ReconcileMessagesForFetchedWindowAsync(
        string chatStableId,
        IEnumerable<ImsgMessage> fetchedMessages,
        bool fetchedAllAvailableHistory = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chatStableId))
        {
            return 0;
        }

        if (fetchedAllAvailableHistory)
        {
            var keepGuids = fetchedMessages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Guid))
                .Select(static message => message.Guid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return await DeleteMissingMessagesForCompleteHistoryAsync(chatStableId, keepGuids, cancellationToken);
        }

        var fetched = fetchedMessages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Guid) && message.SortDate is not null)
            .Select(static message => new
            {
                Guid = message.Guid!,
                Date = message.SortDate!.Value.ToUniversalTime()
            })
            .DistinctBy(static message => message.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fetched.Count < 2)
        {
            return 0;
        }

        var start = fetched.Min(static message => message.Date).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var end = fetched.Max(static message => message.Date).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var keepParameters = fetched
            .Select((message, index) => new { message.Guid, Name = $"$guid{index}" })
            .ToList();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM messages
            WHERE chat_stable_id = $chat_stable_id
              AND COALESCE(date_value, '') >= $start
              AND COALESCE(date_value, '') <= $end
              AND guid NOT IN ({string.Join(", ", keepParameters.Select(static parameter => parameter.Name))})
            """;
        command.Parameters.AddWithValue("$chat_stable_id", chatStableId);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        foreach (var parameter in keepParameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Guid);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> DeleteMissingMessagesForCompleteHistoryAsync(
        string chatStableId,
        IReadOnlyList<string> keepGuids,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$chat_stable_id", chatStableId);

        if (keepGuids.Count == 0)
        {
            command.CommandText = "DELETE FROM messages WHERE chat_stable_id = $chat_stable_id";
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var keepParameters = keepGuids
            .Select((guid, index) => new { Guid = guid, Name = $"$guid{index}" })
            .ToList();
        command.CommandText = $"""
            DELETE FROM messages
            WHERE chat_stable_id = $chat_stable_id
              AND guid NOT IN ({string.Join(", ", keepParameters.Select(static parameter => parameter.Name))})
            """;
        foreach (var parameter in keepParameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Guid);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImsgChat>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM chats ORDER BY COALESCE(last_message_at, '') DESC, display_name ASC";

        var chats = new List<ImsgChat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chat = JsonSerializer.Deserialize<ImsgChat>(reader.GetString(0), ImsgJson.Options);
            if (chat is not null)
            {
                chats.Add(chat);
            }
        }

        return chats;
    }

    public async Task<IReadOnlyList<ImsgMessage>> GetMessagesAsync(string chatStableId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM messages
            WHERE chat_stable_id = $chat_stable_id
            ORDER BY COALESCE(date_value, '') ASC, COALESCE(message_row_id, 0) ASC, guid ASC
            """;
        command.Parameters.AddWithValue("$chat_stable_id", chatStableId);

        return await ReadMessagesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ImsgMessage>> GetRecentMessagesAsync(
        string chatStableId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(chatStableId))
        {
            return [];
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM (
                SELECT payload_json, COALESCE(date_value, '') AS sort_date, COALESCE(message_row_id, 0) AS sort_row, guid
                FROM messages
                WHERE chat_stable_id = $chat_stable_id
                ORDER BY sort_date DESC, sort_row DESC, guid DESC
                LIMIT $limit
            )
            ORDER BY sort_date ASC, sort_row ASC, guid ASC
            """;
        command.Parameters.AddWithValue("$chat_stable_id", chatStableId);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadMessagesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLatestMessageTextByChatStableIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chat_stable_id, text
            FROM (
                SELECT
                    chat_stable_id,
                    text,
                    ROW_NUMBER() OVER (
                        PARTITION BY chat_stable_id
                        ORDER BY COALESCE(date_value, '') DESC, COALESCE(message_row_id, 0) DESC, guid DESC
                    ) AS row_number
                FROM messages
                WHERE text IS NOT NULL
                    AND TRIM(text) <> ''
            )
            WHERE row_number = 1
            """;

        var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            previews[reader.GetString(0)] = reader.GetString(1);
        }

        return previews;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestMessageDateByChatStableIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chat_stable_id, date_value
            FROM (
                SELECT
                    chat_stable_id,
                    date_value,
                    ROW_NUMBER() OVER (
                        PARTITION BY chat_stable_id
                        ORDER BY COALESCE(date_value, '') DESC, COALESCE(message_row_id, 0) DESC, guid DESC
                    ) AS row_number
                FROM messages
                WHERE date_value IS NOT NULL
                    AND TRIM(date_value) <> ''
            )
            WHERE row_number = 1
            """;

        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryReadDate(reader.GetString(1), out var date))
            {
                dates[reader.GetString(0)] = date;
            }
        }

        return dates;
    }

    private static bool TryReadDate(string? value, out DateTimeOffset date)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
        {
            return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            date = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }

        date = default;
        return false;
    }

    public async Task<IReadOnlyList<ImsgMessage>> GetMessagesBeforeAsync(
        string chatStableId,
        string beforeDateValue,
        long? beforeMessageRowId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 ||
            string.IsNullOrWhiteSpace(chatStableId) ||
            string.IsNullOrWhiteSpace(beforeDateValue))
        {
            return [];
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM (
                SELECT payload_json, COALESCE(date_value, '') AS sort_date, COALESCE(message_row_id, 0) AS sort_row, guid
                FROM messages
                WHERE chat_stable_id = $chat_stable_id
                  AND (
                        COALESCE(date_value, '') < $before_date
                        OR (
                            COALESCE(date_value, '') = $before_date
                            AND $before_row_id IS NOT NULL
                            AND COALESCE(message_row_id, 0) < $before_row_id
                        )
                  )
                ORDER BY sort_date DESC, sort_row DESC, guid DESC
                LIMIT $limit
            )
            ORDER BY sort_date ASC, sort_row ASC, guid ASC
            """;
        command.Parameters.AddWithValue("$chat_stable_id", chatStableId);
        command.Parameters.AddWithValue("$before_date", beforeDateValue);
        command.Parameters.AddWithValue("$before_row_id", DbValue(beforeMessageRowId));
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadMessagesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> SearchChatStableIdsByMessageContentAsync(
        string query,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var trimmed = query.Trim();
        var chatIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ftsQuery = BuildFtsQuery(trimmed);
        if (!string.IsNullOrWhiteSpace(ftsQuery))
        {
            try
            {
                var ftsCommand = connection.CreateCommand();
                ftsCommand.CommandText = """
                    SELECT chat_stable_id
                    FROM message_search_fts
                    WHERE message_search_fts MATCH $query
                    UNION
                    SELECT stable_id
                    FROM chat_search_fts
                    WHERE chat_search_fts MATCH $query
                    LIMIT $limit
                    """;
                ftsCommand.Parameters.AddWithValue("$query", ftsQuery);
                ftsCommand.Parameters.AddWithValue("$limit", limit);
                await ReadSearchStableIdsAsync(ftsCommand, chatIds, cancellationToken);
            }
            catch (SqliteException)
            {
                chatIds.Clear();
            }
        }

        if (chatIds.Count >= limit)
        {
            return chatIds;
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chat_stable_id
            FROM message_search_fts
            WHERE content COLLATE NOCASE LIKE $query ESCAPE '\'
            UNION
            SELECT stable_id
            FROM chat_search_fts
            WHERE content COLLATE NOCASE LIKE $query ESCAPE '\'
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", $"%{EscapeLikePattern(trimmed)}%");
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit - chatIds.Count));
        await ReadSearchStableIdsAsync(command, chatIds, cancellationToken);

        return chatIds;
    }

    public async Task<IReadOnlyDictionary<string, ChatSyncState>> GetChatSyncStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stable_id, last_successful_sync_at, deepest_fetched_limit, last_error, retry_after, updated_at
            FROM chat_sync_state
            """;

        var states = new Dictionary<string, ChatSyncState>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = new ChatSyncState
            {
                StableId = reader.GetString(0),
                LastSuccessfulSyncAt = ReadNullableString(reader, 1),
                DeepestFetchedLimit = reader.GetInt32(2),
                LastError = ReadNullableString(reader, 3),
                RetryAfter = ReadNullableString(reader, 4),
                UpdatedAt = reader.GetString(5)
            };
            states[state.StableId] = state;
        }

        return states;
    }

    public async Task<long?> GetWatchCursorAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM app_state
            WHERE scope = $scope AND key = 'watch.cursor.rowid'
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$scope", NormalizeScope(scope));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && long.TryParse(text, out var cursor) && cursor > 0
            ? cursor
            : null;
    }

    public async Task SaveWatchCursorAsync(
        string scope,
        long rowId,
        CancellationToken cancellationToken = default)
    {
        if (rowId <= 0)
        {
            return;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_state (scope, key, value, updated_at)
            VALUES ($scope, 'watch.cursor.rowid', $value, $updated_at)
            ON CONFLICT(scope, key) DO UPDATE SET
                value = CASE
                    WHEN CAST(excluded.value AS INTEGER) > CAST(app_state.value AS INTEGER)
                    THEN excluded.value
                    ELSE app_state.value
                END,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$scope", NormalizeScope(scope));
        command.Parameters.AddWithValue("$value", rowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task MarkChatSyncSucceededAsync(
        string stableId,
        int fetchedLimit,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return UpsertChatSyncStateAsync(
            stableId,
            lastSuccessfulSyncAt: now,
            deepestFetchedLimit: fetchedLimit,
            lastError: null,
            retryAfter: null,
            updatedAt: now,
            cancellationToken);
    }

    public Task MarkChatSyncFailedAsync(
        string stableId,
        string error,
        DateTimeOffset retryAfter,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return UpsertChatSyncStateAsync(
            stableId,
            lastSuccessfulSyncAt: null,
            deepestFetchedLimit: 0,
            lastError: error,
            retryAfter: retryAfter.ToString("O"),
            updatedAt: now,
            cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM messages;
            DELETE FROM chats;
            DELETE FROM chat_sync_state;
            DELETE FROM app_state;
            DELETE FROM message_search_fts;
            DELETE FROM chat_search_fts;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_databasePath}");

    private static object DbValue<T>(T? value) => value is null ? DBNull.Value : value;

    private static string NormalizeScope(string scope) =>
        string.IsNullOrWhiteSpace(scope) ? "default" : scope.Trim();

    private static async Task EnsureMessageRowIdColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(messages)";
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Contains("message_row_id"))
        {
            return;
        }

        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE messages ADD COLUMN message_row_id INTEGER NULL";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMessageRowIdIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_messages_chat_stable_id_date_row
            ON messages(chat_stable_id, date_value, message_row_id)
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BackfillMessageDateValuesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT guid, payload_json
            FROM messages
            WHERE date_value IS NULL OR date_value = ''
            """;

        var updates = new List<(string Guid, string DateValue)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var guid = reader.GetString(0);
                var payload = reader.GetString(1);
                var message = JsonSerializer.Deserialize<ImsgMessage>(payload, ImsgJson.Options);
                if (message is null || string.IsNullOrWhiteSpace(message.DateText))
                {
                    continue;
                }

                updates.Add((guid, message.DateText));
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var update in updates)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE messages SET date_value = $date_value WHERE guid = $guid";
            command.Parameters.AddWithValue("$date_value", update.DateValue);
            command.Parameters.AddWithValue("$guid", update.Guid);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task BackfillMessageRowIdsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT guid, payload_json
            FROM messages
            WHERE message_row_id IS NULL
            """;

        var updates = new List<(string Guid, long RowId)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var guid = reader.GetString(0);
                var payload = reader.GetString(1);
                var message = JsonSerializer.Deserialize<ImsgMessage>(payload, ImsgJson.Options);
                if (message?.Id is null)
                {
                    continue;
                }

                updates.Add((guid, message.Id.Value));
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var update in updates)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE messages SET message_row_id = $message_row_id WHERE guid = $guid";
            command.Parameters.AddWithValue("$message_row_id", update.RowId);
            command.Parameters.AddWithValue("$guid", update.Guid);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // v2 slims the indexed content to text plus sender/participant identity;
    // v1 indexed full payload JSON, which bloated cache.db and let JSON keys
    // match user searches. Bumping the version drops and rebuilds the index
    // once, then the triggers keep it current.
    private const string SearchIndexVersion = "2";

    private static string MessageSearchContentSql(string row) =>
        $"COALESCE({row}.text, '') || ' ' || " +
        $"COALESCE(json_extract({row}.payload_json, '$.sender'), '') || ' ' || " +
        $"COALESCE(json_extract({row}.payload_json, '$.sender_name'), '')";

    private static string ChatSearchContentSql(string row) =>
        $"{row}.stable_id || ' ' || COALESCE({row}.display_name, '') || ' ' || COALESCE({row}.service, '') || ' ' || " +
        $"COALESCE(json_extract({row}.payload_json, '$.name'), '') || ' ' || " +
        $"COALESCE(json_extract({row}.payload_json, '$.contact_name'), '') || ' ' || " +
        $"COALESCE(json_extract({row}.payload_json, '$.participants'), '')";

    private static async Task EnsureSearchIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var storedVersion = await ReadAppStateAsync(connection, "search-index", "version", cancellationToken);
        var versionMismatch = !string.Equals(storedVersion, SearchIndexVersion, StringComparison.Ordinal);
        if (versionMismatch)
        {
            var drop = connection.CreateCommand();
            drop.CommandText = """
                DROP TRIGGER IF EXISTS messages_search_ai;
                DROP TRIGGER IF EXISTS messages_search_ad;
                DROP TRIGGER IF EXISTS messages_search_au;
                DROP TRIGGER IF EXISTS chats_search_ai;
                DROP TRIGGER IF EXISTS chats_search_ad;
                DROP TRIGGER IF EXISTS chats_search_au;
                DROP TABLE IF EXISTS message_search_fts;
                DROP TABLE IF EXISTS chat_search_fts;
                """;
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS message_search_fts
            USING fts5(guid UNINDEXED, chat_stable_id UNINDEXED, content);

            CREATE VIRTUAL TABLE IF NOT EXISTS chat_search_fts
            USING fts5(stable_id UNINDEXED, content);

            CREATE TRIGGER IF NOT EXISTS messages_search_ai
            AFTER INSERT ON messages
            BEGIN
                INSERT INTO message_search_fts (guid, chat_stable_id, content)
                VALUES (new.guid, new.chat_stable_id, {MessageSearchContentSql("new")});
            END;

            CREATE TRIGGER IF NOT EXISTS messages_search_ad
            AFTER DELETE ON messages
            BEGIN
                DELETE FROM message_search_fts WHERE guid = old.guid;
            END;

            CREATE TRIGGER IF NOT EXISTS messages_search_au
            AFTER UPDATE ON messages
            BEGIN
                DELETE FROM message_search_fts WHERE guid = old.guid;
                INSERT INTO message_search_fts (guid, chat_stable_id, content)
                VALUES (new.guid, new.chat_stable_id, {MessageSearchContentSql("new")});
            END;

            CREATE TRIGGER IF NOT EXISTS chats_search_ai
            AFTER INSERT ON chats
            BEGIN
                INSERT INTO chat_search_fts (stable_id, content)
                VALUES (new.stable_id, {ChatSearchContentSql("new")});
            END;

            CREATE TRIGGER IF NOT EXISTS chats_search_ad
            AFTER DELETE ON chats
            BEGIN
                DELETE FROM chat_search_fts WHERE stable_id = old.stable_id;
            END;

            CREATE TRIGGER IF NOT EXISTS chats_search_au
            AFTER UPDATE ON chats
            BEGIN
                DELETE FROM chat_search_fts WHERE stable_id = old.stable_id;
                INSERT INTO chat_search_fts (stable_id, content)
                VALUES (new.stable_id, {ChatSearchContentSql("new")});
            END;
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);

        // The triggers keep the FTS tables in sync incrementally; a full
        // rebuild only runs on a version bump or when row counts drift.
        if (!versionMismatch && !await SearchIndexOutOfSyncAsync(connection, cancellationToken))
        {
            return;
        }

        var rebuild = connection.CreateCommand();
        rebuild.CommandText = $"""
            DELETE FROM message_search_fts;
            INSERT INTO message_search_fts (guid, chat_stable_id, content)
            SELECT guid, chat_stable_id, {MessageSearchContentSql("messages")}
            FROM messages;

            DELETE FROM chat_search_fts;
            INSERT INTO chat_search_fts (stable_id, content)
            SELECT stable_id, {ChatSearchContentSql("chats")}
            FROM chats;
            """;
        await rebuild.ExecuteNonQueryAsync(cancellationToken);
        await WriteAppStateAsync(connection, "search-index", "version", SearchIndexVersion, cancellationToken);

        if (versionMismatch)
        {
            // Reclaim the space the old payload-JSON index occupied.
            var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string?> ReadAppStateAsync(
        SqliteConnection connection,
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE scope = $scope AND key = $key";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    private static async Task WriteAppStateAsync(
        SqliteConnection connection,
        string scope,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_state (scope, key, value, updated_at)
            VALUES ($scope, $key, $value, $updated_at)
            ON CONFLICT(scope, key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> SearchIndexOutOfSyncAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var check = connection.CreateCommand();
        check.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM messages) - (SELECT COUNT(*) FROM message_search_fts),
                (SELECT COUNT(*) FROM chats) - (SELECT COUNT(*) FROM chat_search_fts)
            """;
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return true;
        }

        return reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0;
    }

    private static async Task<IReadOnlyList<ImsgMessage>> ReadMessagesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var messages = new List<ImsgMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = JsonSerializer.Deserialize<ImsgMessage>(reader.GetString(0), ImsgJson.Options);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private static async Task ReadSearchStableIdsAsync(
        SqliteCommand command,
        ISet<string> stableIds,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stableIds.Add(reader.GetString(0));
        }
    }

    private async Task UpsertChatSyncStateAsync(
        string stableId,
        string? lastSuccessfulSyncAt,
        int deepestFetchedLimit,
        string? lastError,
        string? retryAfter,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO chat_sync_state (stable_id, last_successful_sync_at, deepest_fetched_limit, last_error, retry_after, updated_at)
            VALUES ($stable_id, $last_successful_sync_at, $deepest_fetched_limit, $last_error, $retry_after, $updated_at)
            ON CONFLICT(stable_id) DO UPDATE SET
                last_successful_sync_at = COALESCE(excluded.last_successful_sync_at, chat_sync_state.last_successful_sync_at),
                deepest_fetched_limit = MAX(chat_sync_state.deepest_fetched_limit, excluded.deepest_fetched_limit),
                last_error = excluded.last_error,
                retry_after = excluded.retry_after,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$stable_id", stableId);
        command.Parameters.AddWithValue("$last_successful_sync_at", DbValue(lastSuccessfulSyncAt));
        command.Parameters.AddWithValue("$deepest_fetched_limit", deepestFetchedLimit);
        command.Parameters.AddWithValue("$last_error", DbValue(lastError));
        command.Parameters.AddWithValue("$retry_after", DbValue(retryAfter));
        command.Parameters.AddWithValue("$updated_at", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    private static string BuildFtsQuery(string value)
    {
        var terms = new List<string>();
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(value, @"[A-Za-z0-9_]+").Cast<System.Text.RegularExpressions.Match>())
        {
            if (match.Value.Length == 0)
            {
                continue;
            }

            terms.Add(match.Value);
            if (terms.Count >= 8)
            {
                break;
            }
        }

        return string.Join(" AND ", terms.Select(static term => $"{term}*"));
    }
}

public sealed record ChatSyncState
{
    public required string StableId { get; init; }

    public string? LastSuccessfulSyncAt { get; init; }

    public int DeepestFetchedLimit { get; init; }

    public string? LastError { get; init; }

    public string? RetryAfter { get; init; }

    public required string UpdatedAt { get; init; }
}
