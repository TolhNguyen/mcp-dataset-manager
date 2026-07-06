using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class KnowledgeService(NpgsqlDataSource dataSource)
{
    private const string SelectEntrySql = """
        SELECT id AS Id,
               dataset_id AS DatasetId,
               kind AS Kind,
               title AS Title,
               content AS Content,
               source AS Source,
               created_by AS CreatedBy,
               pinned AS Pinned,
               archived_at AS ArchivedAt,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
        FROM dataset_knowledge_entries
        """;

    // ============================================================
    // List
    // ============================================================

    public async Task<ApiResult<object>> ListAsync(Guid datasetId, bool includeArchived, string? kind, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var sql = SelectEntrySql + " WHERE dataset_id = @DatasetId";
        if (!includeArchived)
        {
            sql += " AND archived_at IS NULL";
        }
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND kind = @Kind";
        }
        sql += " ORDER BY pinned DESC, created_at DESC";

        var entries = (await conn.QueryAsync<KnowledgeEntry>(sql, new { DatasetId = datasetId, Kind = kind })).ToList();

        return ApiResult<object>.Ok(new { entries = entries.Select(BuildEntryDto).ToList() });
    }

    // ============================================================
    // Create (advisory-lock per dataset + 200-active cap + revision)
    // ============================================================

    public async Task<ApiResult<object>> CreateAsync(Guid datasetId, CreateKnowledgeRequest req, string source, string actor, CancellationToken ct)
    {
        var validationError = KnowledgeGuard.ValidateCreate(req.Kind, req.Title, req.Content);
        if (validationError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, validationError);
        }

        var kind = string.IsNullOrWhiteSpace(req.Kind) ? "note" : req.Kind!;
        var title = req.Title!.Trim();
        var content = req.Content!;
        var pinned = req.Pinned ?? false;
        var entryId = Guid.NewGuid();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // pg_advisory_xact_lock takes a bigint; derive a stable key from the dataset id so
        // concurrent creates for the same dataset cannot both see "count < 200" and both insert.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@DatasetKey::text, 0))",
            new { DatasetKey = datasetId }, tx);

        var activeCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dataset_knowledge_entries WHERE dataset_id = @DatasetId AND archived_at IS NULL",
            new { DatasetId = datasetId }, tx);

        if (activeCount >= KnowledgeGuard.MaxActivePerDataset)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(
                ErrorCodes.KnowledgeLimitReached,
                $"Dataset has reached the {KnowledgeGuard.MaxActivePerDataset}-entry knowledge limit.",
                new { max_active = KnowledgeGuard.MaxActivePerDataset, current_count = activeCount });
        }

        await conn.ExecuteAsync("""
            INSERT INTO dataset_knowledge_entries
                (id, dataset_id, kind, title, content, source, created_by, pinned)
            VALUES
                (@Id, @DatasetId, @Kind, @Title, @Content, @Source, @CreatedBy, @Pinned)
            """, new
        {
            Id = entryId,
            DatasetId = datasetId,
            Kind = kind,
            Title = title,
            Content = content,
            Source = source,
            CreatedBy = actor,
            Pinned = pinned
        }, tx);

        await conn.ExecuteAsync("""
            INSERT INTO dataset_knowledge_revisions (id, entry_id, action, previous_content, actor)
            VALUES (@Id, @EntryId, 'create', NULL, @Actor)
            """, new { Id = Guid.NewGuid(), EntryId = entryId, Actor = actor }, tx);

        var created = await GetEntryAsync(conn, datasetId, entryId, tx);

        await tx.CommitAsync(ct);

        return ApiResult<object>.Ok(BuildEntryDto(created!));
    }

    // ============================================================
    // Update (revision: previous content before change)
    // ============================================================

    public async Task<ApiResult<object>> UpdateAsync(Guid datasetId, Guid entryId, UpdateKnowledgeRequest req, string actor, CancellationToken ct)
    {
        var validationError = KnowledgeGuard.ValidateUpdate(req.Title, req.Content, req.Pinned);
        if (validationError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, validationError);
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await GetEntryAsync(conn, datasetId, entryId, tx, forUpdate: true);
        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(ErrorCodes.KnowledgeNotFound, "Knowledge entry not found.");
        }

        var newTitle = req.Title ?? existing.Title;
        var newContent = req.Content ?? existing.Content;
        var newPinned = req.Pinned ?? existing.Pinned;

        await conn.ExecuteAsync("""
            UPDATE dataset_knowledge_entries
            SET title = @Title, content = @Content, pinned = @Pinned, updated_at = NOW()
            WHERE id = @Id
            """, new { Id = entryId, Title = newTitle, Content = newContent, Pinned = newPinned }, tx);

        await conn.ExecuteAsync("""
            INSERT INTO dataset_knowledge_revisions (id, entry_id, action, previous_content, actor)
            VALUES (@Id, @EntryId, 'update', @PreviousContent, @Actor)
            """, new { Id = Guid.NewGuid(), EntryId = entryId, PreviousContent = existing.Content, Actor = actor }, tx);

        var updated = await GetEntryAsync(conn, datasetId, entryId, tx);

        await tx.CommitAsync(ct);

        return ApiResult<object>.Ok(BuildEntryDto(updated!));
    }

    // ============================================================
    // Archive (soft delete; revision: content at archive time)
    // ============================================================

    public async Task<ApiResult<object>> ArchiveAsync(Guid datasetId, Guid entryId, string actor, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await GetEntryAsync(conn, datasetId, entryId, tx, forUpdate: true);
        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(ErrorCodes.KnowledgeNotFound, "Knowledge entry not found.");
        }

        if (existing.ArchivedAt is null)
        {
            await conn.ExecuteAsync("""
                UPDATE dataset_knowledge_entries
                SET archived_at = NOW(), updated_at = NOW()
                WHERE id = @Id
                """, new { Id = entryId }, tx);

            await conn.ExecuteAsync("""
                INSERT INTO dataset_knowledge_revisions (id, entry_id, action, previous_content, actor)
                VALUES (@Id, @EntryId, 'archive', @Content, @Actor)
                """, new { Id = Guid.NewGuid(), EntryId = entryId, Content = existing.Content, Actor = actor }, tx);
        }

        var archived = await GetEntryAsync(conn, datasetId, entryId, tx);

        await tx.CommitAsync(ct);

        return ApiResult<object>.Ok(BuildEntryDto(archived!));
    }

    // ============================================================
    // Hard delete (JWT-only path; cascades revisions via FK)
    // ============================================================

    public async Task<ApiResult<object>> HardDeleteAsync(Guid datasetId, Guid entryId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM dataset_knowledge_entries WHERE id = @Id AND dataset_id = @DatasetId",
            new { Id = entryId, DatasetId = datasetId });

        if (affected == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.KnowledgeNotFound, "Knowledge entry not found.");
        }

        return ApiResult<object>.Ok(new { deleted = true, entry_id = entryId });
    }

    // ============================================================
    // Search (trigram similarity + title-match + pinned ranking)
    // ============================================================

    public async Task<ApiResult<object>> SearchAsync(Guid[] datasetIds, string query, int limit, CancellationToken ct)
    {
        if (datasetIds is null || datasetIds.Length == 0)
        {
            return ApiResult<object>.Ok(new { results = Array.Empty<object>() });
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "query is required.");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var rows = (await conn.QueryAsync<SearchRow>("""
            SELECT e.id AS Id, e.dataset_id AS DatasetId, e.kind AS Kind, e.title AS Title, e.content AS Content,
                   e.source AS Source, e.pinned AS Pinned,
                   similarity(unaccent(lower(e.title || ' ' || e.content)), unaccent(lower(@Q)))::double precision AS Score
            FROM dataset_knowledge_entries e
            WHERE e.dataset_id = ANY(@DatasetIds) AND e.archived_at IS NULL
              AND (unaccent(lower(e.title || ' ' || e.content)) % unaccent(lower(@Q))
                   OR e.pinned)
            ORDER BY (CASE WHEN unaccent(lower(e.title)) LIKE '%' || unaccent(lower(@Q)) || '%' THEN 1 ELSE 0 END) DESC,
                     e.pinned DESC, Score DESC
            LIMIT @Limit
            """, new { DatasetIds = datasetIds, Q = query, Limit = limit })).ToList();

        var results = rows.Select(r => (object)new
        {
            id = r.Id,
            dataset_id = r.DatasetId,
            kind = r.Kind,
            title = r.Title,
            content = r.Content,
            source = r.Source,
            pinned = r.Pinned,
            score = r.Score
        }).ToList();

        return ApiResult<object>.Ok(new { results });
    }

    // ============================================================
    // Pinned entries (used by context API / manifest)
    // ============================================================

    public async Task<IReadOnlyList<KnowledgeEntry>> GetPinnedAsync(Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var rows = await conn.QueryAsync<KnowledgeEntry>(
            SelectEntrySql + """
             WHERE dataset_id = @DatasetId AND pinned = TRUE AND archived_at IS NULL
             ORDER BY created_at
            """,
            new { DatasetId = datasetId });

        return rows.ToList();
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    private static Task<KnowledgeEntry?> GetEntryAsync(
        NpgsqlConnection conn, Guid datasetId, Guid entryId, NpgsqlTransaction? tx = null, bool forUpdate = false)
    {
        var sql = SelectEntrySql + " WHERE id = @Id AND dataset_id = @DatasetId";
        if (forUpdate)
        {
            sql += " FOR UPDATE";
        }
        return conn.QuerySingleOrDefaultAsync<KnowledgeEntry>(sql, new { Id = entryId, DatasetId = datasetId }, tx);
    }

    private static object BuildEntryDto(KnowledgeEntry e) => new
    {
        id = e.Id,
        dataset_id = e.DatasetId,
        kind = e.Kind,
        title = e.Title,
        content = e.Content,
        source = e.Source,
        created_by = e.CreatedBy,
        pinned = e.Pinned,
        archived_at = e.ArchivedAt,
        created_at = e.CreatedAt,
        updated_at = e.UpdatedAt
    };

    private sealed record SearchRow(
        Guid Id, Guid DatasetId, string Kind, string Title, string Content,
        string Source, bool Pinned, double Score);
}
