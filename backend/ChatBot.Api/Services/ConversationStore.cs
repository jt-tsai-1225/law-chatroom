using ChatBot.Api.Models;
using Npgsql;
using NpgsqlTypes;

namespace ChatBot.Api.Services;

public interface IConversationStore
{
    /// <summary>有沒有設定資料庫。false 時聊天室功能整個關閉。</summary>
    bool IsConfigured { get; }

    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<List<Conversation>> ListAsync(CancellationToken ct = default);
    Task<ConversationDetail?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Conversation> CreateAsync(string? title, CancellationToken ct = default);
    Task<bool> RenameAsync(Guid id, string title, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>取最近 n 則訊息，由舊到新。供組對話歷史用。</summary>
    Task<List<ConversationMessage>> RecentMessagesAsync(
        Guid conversationId, int limit, CancellationToken ct = default);

    Task<long> AppendAsync(ConversationMessage message, CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL 版的聊天室儲存。
///
/// 設計上的兩個取捨：
///
/// 1. **不用 EF Core。** 兩張表、沒有複雜關聯，為此在 k8s 裡多養一個
///    migration job 不划算。結構由啟動時的冪等 DDL 建立。
///
/// 2. **寫入失敗不讓對話失敗。** 使用者已經拿到模型的回答，此時因為
///    「存不進資料庫」而回 500，是把一個次要功能的故障升級成主要功能的故障。
///    因此 AppendAsync 的錯誤由呼叫端記錄並在回應裡標示 persisted=false，
///    而不是往上拋。讀取則不同——讀不到就是讀不到，誠實回報。
/// </summary>
public sealed class ConversationStore : IConversationStore
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<ConversationStore> _logger;

    public bool IsConfigured => _dataSource is not null;

    public ConversationStore(IConfiguration config, ILogger<ConversationStore> logger)
    {
        _logger = logger;

        var connectionString = config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "未設定 ConnectionStrings:Postgres，聊天室紀錄功能停用。" +
                "問答仍可使用，但不會保存。");
            return;
        }

        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    private NpgsqlDataSource Db => _dataSource
        ?? throw new InvalidOperationException("未設定 ConnectionStrings:Postgres");

    // ── 結構 ────────────────────────────────────────────────────────
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS conversations (
            id          uuid PRIMARY KEY,
            title       text        NOT NULL,
            created_at  timestamptz NOT NULL DEFAULT now(),
            updated_at  timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS messages (
            id                  bigserial PRIMARY KEY,
            conversation_id     uuid NOT NULL
                                REFERENCES conversations(id) ON DELETE CASCADE,
            role                text        NOT NULL CHECK (role IN ('user','assistant')),
            content             text        NOT NULL,
            created_at          timestamptz NOT NULL DEFAULT now(),

            -- 以下僅 assistant 訊息有值。存下來是為了讓三種快取模式的
            -- 差異事後可以直接用 SQL 查，不必另外做紀錄。
            cache_mode          text,
            chunk_order         text,
            ttft_ms             integer,
            total_ms            integer,
            prompt_tokens       integer,
            cached_tokens       integer,
            cache_hit_rate      double precision,
            prefix_ceiling      integer,
            cached_over_ceiling double precision,
            retrieved_articles  text[]
        );

        CREATE INDEX IF NOT EXISTS ix_messages_conversation
            ON messages (conversation_id, id);
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;

        await using var cmd = Db.CreateCommand(SchemaSql);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("聊天室資料表就緒");
    }

    // ── 聊天室 ──────────────────────────────────────────────────────
    public async Task<List<Conversation>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT c.id, c.title, c.created_at, c.updated_at,
                   (SELECT count(*) FROM messages m WHERE m.conversation_id = c.id)
            FROM conversations c
            ORDER BY c.updated_at DESC
            """;

        var result = new List<Conversation>();
        await using var cmd = Db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new Conversation
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                CreatedAt = reader.GetDateTime(2),
                UpdatedAt = reader.GetDateTime(3),
                MessageCount = (int)reader.GetInt64(4)
            });
        }

        return result;
    }

    public async Task<ConversationDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        const string head = """
            SELECT id, title, created_at, updated_at FROM conversations WHERE id = $1
            """;

        Conversation conversation;
        await using (var cmd = Db.CreateCommand(head))
        {
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            conversation = new Conversation
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                CreatedAt = reader.GetDateTime(2),
                UpdatedAt = reader.GetDateTime(3)
            };
        }

        var messages = await ReadMessagesAsync(id, limit: null, ct);
        conversation.MessageCount = messages.Count;

        return new ConversationDetail { Conversation = conversation, Messages = messages };
    }

    public async Task<Conversation> CreateAsync(string? title, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO conversations (id, title) VALUES ($1, $2)
            RETURNING created_at, updated_at
            """;

        var id = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(title) ? "新對話" : title.Trim();

        await using var cmd = Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new Conversation
        {
            Id = id,
            Title = name,
            CreatedAt = reader.GetDateTime(0),
            UpdatedAt = reader.GetDateTime(1)
        };
    }

    public async Task<bool> RenameAsync(Guid id, string title, CancellationToken ct = default)
    {
        await using var cmd = Db.CreateCommand(
            "UPDATE conversations SET title = $2, updated_at = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(title.Trim());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // messages 有 ON DELETE CASCADE，不必自己清
        await using var cmd = Db.CreateCommand("DELETE FROM conversations WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ── 訊息 ────────────────────────────────────────────────────────
    public Task<List<ConversationMessage>> RecentMessagesAsync(
        Guid conversationId, int limit, CancellationToken ct = default)
        => ReadMessagesAsync(conversationId, limit, ct);

    private async Task<List<ConversationMessage>> ReadMessagesAsync(
        Guid conversationId, int? limit, CancellationToken ct)
    {
        // limit 時取最後 n 則再轉回時間順序——對話歷史要的是「最近幾則」，
        // 而模型需要的是由舊到新。
        var sql = limit is null
            ? """
              SELECT id, conversation_id, role, content, created_at,
                     cache_mode, chunk_order, ttft_ms, total_ms, prompt_tokens,
                     cached_tokens, cache_hit_rate, prefix_ceiling,
                     cached_over_ceiling, retrieved_articles
              FROM messages WHERE conversation_id = $1 ORDER BY id
              """
            : """
              SELECT * FROM (
                SELECT id, conversation_id, role, content, created_at,
                       cache_mode, chunk_order, ttft_ms, total_ms, prompt_tokens,
                       cached_tokens, cache_hit_rate, prefix_ceiling,
                       cached_over_ceiling, retrieved_articles
                FROM messages WHERE conversation_id = $1 ORDER BY id DESC LIMIT $2
              ) t ORDER BY id
              """;

        var result = new List<ConversationMessage>();
        await using var cmd = Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(conversationId);
        if (limit is not null) cmd.Parameters.AddWithValue(limit.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ConversationMessage
            {
                Id = reader.GetInt64(0),
                ConversationId = reader.GetGuid(1),
                Role = reader.GetString(2),
                Content = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                CacheMode = reader.IsDBNull(5) ? null : reader.GetString(5),
                ChunkOrder = reader.IsDBNull(6) ? null : reader.GetString(6),
                TtftMs = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                TotalMs = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                PromptTokens = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                CachedTokens = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                CacheHitRate = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                PrefixCeilingTokens = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                CachedOverPrefixCeiling = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                RetrievedArticles = reader.IsDBNull(14)
                    ? null
                    : reader.GetFieldValue<string[]>(14).ToList()
            });
        }

        return result;
    }

    public async Task<long> AppendAsync(ConversationMessage m, CancellationToken ct = default)
    {
        const string insert = """
            INSERT INTO messages (
                conversation_id, role, content, cache_mode, chunk_order,
                ttft_ms, total_ms, prompt_tokens, cached_tokens, cache_hit_rate,
                prefix_ceiling, cached_over_ceiling, retrieved_articles)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            RETURNING id
            """;

        // 兩個敘述包在一筆交易裡。原本寫成一個 data-modifying CTE 比較短，
        // 但那種寫法只有在 PostgreSQL 的 CTE 可見性規則下才成立，
        // 讀的人要停下來想一下才確定對不對——這裡換成直白的兩步。
        await using var conn = await Db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long id;
        await using (var cmd = new NpgsqlCommand(insert, conn, tx))
        {
            cmd.Parameters.Add(Param(NpgsqlDbType.Uuid, m.ConversationId));
            cmd.Parameters.Add(Param(NpgsqlDbType.Text, m.Role));
            cmd.Parameters.Add(Param(NpgsqlDbType.Text, m.Content));
            cmd.Parameters.Add(Param(NpgsqlDbType.Text, m.CacheMode));
            cmd.Parameters.Add(Param(NpgsqlDbType.Text, m.ChunkOrder));
            cmd.Parameters.Add(Param(NpgsqlDbType.Integer, m.TtftMs));
            cmd.Parameters.Add(Param(NpgsqlDbType.Integer, m.TotalMs));
            cmd.Parameters.Add(Param(NpgsqlDbType.Integer, m.PromptTokens));
            cmd.Parameters.Add(Param(NpgsqlDbType.Integer, m.CachedTokens));
            cmd.Parameters.Add(Param(NpgsqlDbType.Double, m.CacheHitRate));
            cmd.Parameters.Add(Param(NpgsqlDbType.Integer, m.PrefixCeilingTokens));
            cmd.Parameters.Add(Param(NpgsqlDbType.Double, m.CachedOverPrefixCeiling));
            cmd.Parameters.Add(Param(
                NpgsqlDbType.Array | NpgsqlDbType.Text, m.RetrievedArticles?.ToArray()));

            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await using (var touch = new NpgsqlCommand(
            "UPDATE conversations SET updated_at = now() WHERE id = $1", conn, tx))
        {
            touch.Parameters.Add(Param(NpgsqlDbType.Uuid, m.ConversationId));
            await touch.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// 建一個帶明確型別的參數。
    ///
    /// 型別一定要指明：AddWithValue(DBNull.Value) 送出去時型別是未指定的，
    /// 伺服器端能不能推斷要看上下文，在不同敘述裡的行為不一致。
    /// 這裡的欄位大多可為 NULL，寫死型別最省事。
    /// </summary>
    private static NpgsqlParameter Param(NpgsqlDbType type, object? value)
        => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };
}
