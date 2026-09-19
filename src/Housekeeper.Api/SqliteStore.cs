using System.Data;
using System.Text.Json;
using Housekeeper.Core;
using Microsoft.Data.Sqlite;

namespace Housekeeper.Api;

/// <summary>
/// Three tables, hand-written SQL, no ORM. The schema is small enough that an ORM would cost more than
/// it saves, and every query here is one the app actually issues.
/// </summary>
public sealed class SqliteStore(string connectionString) : IStore
{
    private const int SchemaVersion = 7;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ConnectionStringFor(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

    /// <summary>Creates the schema if needed. Safe to call on every start.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        // A first run inside a container points at /data/housekeeper.db; make sure the folder is there so
        // the failure, if any, is about permissions rather than a cryptic "unable to open database file".
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL is persistent in the database file, so it only needs setting once.
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        long current;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        if (current >= SchemaVersion) return;

        // One transaction around the whole ladder, with the version bump inside it. PRAGMA user_version is
        // transactional in SQLite, so a process killed mid-upgrade rolls back to the version it started at and
        // simply tries again next time. Without this, dying between a step and the bump left the old version
        // recorded with the new column already added, and every later start died on "duplicate column name" --
        // before the host could serve the page needed to fix it.
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);

        try
        {
            // Each step brings a database from version N to N+1; a fresh file runs all of them in order.
            if (current < 1) await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
            if (current < 2) await ExecuteAsync(connection, SchemaV2, cancellationToken).ConfigureAwait(false);
            if (current < 3) await ExecuteAsync(connection, SchemaV3, cancellationToken).ConfigureAwait(false);
            if (current < 4) await ExecuteAsync(connection, SchemaV4, cancellationToken).ConfigureAwait(false);
            if (current < 5) await ExecuteAsync(connection, SchemaV5, cancellationToken).ConfigureAwait(false);
            if (current < 6) await ExecuteAsync(connection, SchemaV6, cancellationToken).ConfigureAwait(false);
            if (current < 7) await ExecuteAsync(connection, SchemaV7, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, $"PRAGMA user_version={SchemaVersion};", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort, and deliberately silent: the exception being handled is the one worth reporting,
            // and a rollback that itself fails (a connection already gone, for instance) must not replace it
            // with something that says nothing about why the upgrade failed.
            try { await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
            catch (SqliteException) { /* the original failure is the one that matters */ }
            catch (ObjectDisposedException) { /* same */ }

            throw;
        }
    }

    /// <summary>Version 2: a proposal can refine an earlier one, carrying the user's feedback.</summary>
    private const string SchemaV2 = """
        ALTER TABLE proposals ADD COLUMN feedback TEXT;
        ALTER TABLE proposals ADD COLUMN parent_id INTEGER;
        """;

    /// <summary>Version 3: a finished proposal can be put out of sight without being deleted.</summary>
    private const string SchemaV3 = "ALTER TABLE proposals ADD COLUMN dismissed_utc INTEGER;";

    /// <summary>
    /// Version 4: a finding records how far past its own bar it is, so the list can be ordered by what
    /// matters rather than by what happened to be noticed last. Existing rows default to the bar itself.
    /// </summary>
    private const string SchemaV4 = """
        ALTER TABLE anomalies ADD COLUMN severity REAL NOT NULL DEFAULT 1;
        CREATE INDEX IF NOT EXISTS ix_anomalies_severity ON anomalies (status, severity DESC, detected_utc DESC);
        """;

    /// <summary>Version 5: what the user asked to be watched, in their words and as the scanner reads them.</summary>
    private const string SchemaV5 = """
        CREATE TABLE IF NOT EXISTS concerns (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            text          TEXT    NOT NULL,
            entities_json TEXT    NOT NULL DEFAULT '[]',
            rule_json     TEXT    NOT NULL DEFAULT '{}',
            explanation   TEXT,
            interpreted   INTEGER NOT NULL DEFAULT 0,
            created_utc   INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// Version 6: a concern keeps why the model's reading is missing apart from what was matched, and whether
    /// the model should be asked again.
    /// </summary>
    private const string SchemaV6 = """
        ALTER TABLE concerns ADD COLUMN note TEXT;
        ALTER TABLE concerns ADD COLUMN provisional INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>Version 7: a finding remembers how many times the user dismissed it, so a dismissal teaches the detector.</summary>
    private const string SchemaV7 = """
        ALTER TABLE anomalies ADD COLUMN dismissals INTEGER NOT NULL DEFAULT 0;
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS proposals (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            request          TEXT    NOT NULL,
            source           INTEGER NOT NULL,
            status           INTEGER NOT NULL,
            alias            TEXT,
            description      TEXT,
            config_json      TEXT,
            entities_json    TEXT    NOT NULL DEFAULT '[]',
            actions_json     TEXT    NOT NULL DEFAULT '[]',
            duplicates_json  TEXT    NOT NULL DEFAULT '[]',
            ha_automation_id TEXT,
            error            TEXT,
            anomaly_id       INTEGER,
            created_utc      INTEGER NOT NULL,
            decided_utc      INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_proposals_status ON proposals (status, id DESC);

        CREATE TABLE IF NOT EXISTS samples (
            entity_id   TEXT    NOT NULL,
            changed_utc INTEGER NOT NULL,
            state       TEXT    NOT NULL,
            numeric     REAL,
            PRIMARY KEY (entity_id, changed_utc)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_samples_changed ON samples (changed_utc);

        CREATE TABLE IF NOT EXISTS anomalies (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            dedup_key         TEXT    NOT NULL UNIQUE,
            entity_id         TEXT    NOT NULL,
            kind              INTEGER NOT NULL,
            summary           TEXT    NOT NULL,
            evidence_json     TEXT    NOT NULL DEFAULT '{}',
            suggested_request TEXT    NOT NULL DEFAULT '',
            status            INTEGER NOT NULL,
            detected_utc      INTEGER NOT NULL,
            decided_utc       INTEGER,
            proposal_id       INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_anomalies_status ON anomalies (status, detected_utc DESC);
        """;

    // ---- proposals ----

    public async Task<Proposal> AddProposalAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO proposals
                (request, source, status, alias, description, config_json, entities_json, actions_json,
                 duplicates_json, ha_automation_id, error, anomaly_id, created_utc, decided_utc, feedback,
                 parent_id, dismissed_utc)
            VALUES
                ($request, $source, $status, $alias, $description, $config, $entities, $actions,
                 $duplicates, $haId, $error, $anomalyId, $created, $decided, $feedback, $parentId, $dismissed)
            RETURNING id;
            """;

        Bind(command, proposal);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return proposal with { Id = id };
    }

    public async Task<Proposal?> GetProposalAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {ProposalColumns} FROM proposals WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProposal(reader) : null;
    }

    public async Task<IReadOnlyList<Proposal>> ListProposalsAsync(
        ProposalStatus? status,
        int limit,
        bool includeDismissed,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        List<string> where = [];
        if (status is not null) where.Add("status = $status");
        if (!includeDismissed) where.Add("dismissed_utc IS NULL");

        var filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        command.CommandText = $"SELECT {ProposalColumns} FROM proposals{filter} ORDER BY id DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxPageSize));
        if (status is not null) command.Parameters.AddWithValue("$status", (int)status.Value);

        List<Proposal> results = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadProposal(reader));

        return results;
    }

    public async Task UpdateProposalAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE proposals SET
                request = $request, source = $source, status = $status, alias = $alias,
                description = $description, config_json = $config, entities_json = $entities,
                actions_json = $actions, duplicates_json = $duplicates, ha_automation_id = $haId,
                error = $error, anomaly_id = $anomalyId, created_utc = $created, decided_utc = $decided,
                feedback = $feedback, parent_id = $parentId, dismissed_utc = $dismissed
            WHERE id = $id;
            """;

        Bind(command, proposal);
        command.Parameters.AddWithValue("$id", proposal.Id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryClaimAsync(
        long id,
        ProposalStatus from,
        ProposalStatus to,
        DateTimeOffset? decidedUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Three columns and a status predicate. Deliberately not the whole row: the caller read it before
        // whatever slow thing it just did, and writing that snapshot back would erase any decision made in
        // between — which is exactly how a live automation ended up recorded as superseded.
        command.CommandText = """
            UPDATE proposals SET status = $to, decided_utc = $decided, error = $error
            WHERE id = $id AND status = $from;
            """;
        command.Parameters.AddWithValue("$to", (int)to);
        command.Parameters.AddWithValue("$decided", decidedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$from", (int)from);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// The most rows any one query returns. It exists so a caller-supplied limit cannot ask for the whole
    /// table; it was 500, which silently halved the 1000 the scanner asks for when re-examining open
    /// findings, so the oldest of them were never looked at again and never resolved.
    /// </summary>
    private const int MaxPageSize = 1000;

    public async Task RecordOutcomeAsync(
        long id,
        ProposalStatus to,
        DateTimeOffset decidedUtc,
        string? haAutomationId,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // COALESCE so that passing nothing leaves the recorded id alone rather than clearing it.
        command.CommandText = """
            UPDATE proposals
            SET status = $to, decided_utc = $decided, error = $error,
                ha_automation_id = COALESCE($haId, ha_automation_id)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$to", (int)to);
        command.Parameters.AddWithValue("$decided", decidedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$haId", (object?)haAutomationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetDismissedAsync(long id, DateTimeOffset? dismissedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE proposals SET dismissed_utc = $dismissed
            WHERE id = $id AND status <> $draft;
            """;
        command.Parameters.AddWithValue("$dismissed", dismissedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$draft", (int)ProposalStatus.Draft);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private const string ProposalColumns =
        "id, request, source, status, alias, description, config_json, entities_json, actions_json, " +
        "duplicates_json, ha_automation_id, error, anomaly_id, created_utc, decided_utc, feedback, parent_id, " +
        "dismissed_utc";

    private static void Bind(SqliteCommand command, Proposal proposal)
    {
        command.Parameters.AddWithValue("$request", proposal.Request);
        command.Parameters.AddWithValue("$source", (int)proposal.Source);
        command.Parameters.AddWithValue("$status", (int)proposal.Status);
        command.Parameters.AddWithValue("$alias", (object?)proposal.Alias ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)proposal.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$config", (object?)proposal.ConfigJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$entities", JsonSerializer.Serialize(proposal.Entities, Json));
        command.Parameters.AddWithValue("$actions", JsonSerializer.Serialize(proposal.Actions, Json));
        command.Parameters.AddWithValue("$duplicates", JsonSerializer.Serialize(proposal.Duplicates, Json));
        command.Parameters.AddWithValue("$haId", (object?)proposal.HaAutomationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)proposal.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$anomalyId", (object?)proposal.AnomalyId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", proposal.CreatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$decided", proposal.DecidedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$feedback", (object?)proposal.Feedback ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentId", (object?)proposal.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dismissed", proposal.DismissedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
    }

    private static Proposal ReadProposal(IDataRecord row) => new()
    {
        Id = row.GetInt64(0),
        Request = row.GetString(1),
        Source = (ProposalSource)row.GetInt32(2),
        Status = (ProposalStatus)row.GetInt32(3),
        Alias = Text(row, 4),
        Description = Text(row, 5),
        ConfigJson = Text(row, 6),
        Entities = Deserialize<string>(row.GetString(7)),
        Actions = Deserialize<string>(row.GetString(8)),
        Duplicates = Deserialize<DuplicateMatch>(row.GetString(9)),
        HaAutomationId = Text(row, 10),
        Error = Text(row, 11),
        AnomalyId = row.IsDBNull(12) ? null : row.GetInt64(12),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(13)),
        DecidedUtc = row.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(14)),
        Feedback = Text(row, 15),
        ParentId = row.IsDBNull(16) ? null : row.GetInt64(16),
        DismissedUtc = row.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(17)),
    };

    // ---- samples ----

    public async Task<int> AddSamplesAsync(IReadOnlyList<(string EntityId, StateSample Sample)> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0) return 0;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO samples (entity_id, changed_utc, state, numeric)
            VALUES ($entity, $changed, $state, $numeric)
            ON CONFLICT (entity_id, changed_utc) DO NOTHING;
            """;

        var entity = command.Parameters.Add("$entity", SqliteType.Text);
        var changed = command.Parameters.Add("$changed", SqliteType.Integer);
        var state = command.Parameters.Add("$state", SqliteType.Text);
        var numeric = command.Parameters.Add("$numeric", SqliteType.Real);

        var inserted = 0;
        foreach (var (entityId, sample) in samples)
        {
            entity.Value = entityId;
            changed.Value = sample.ChangedUtc.ToUnixTimeMilliseconds();
            state.Value = sample.State;
            numeric.Value = (object?)sample.Numeric ?? DBNull.Value;

            inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestSampleTimesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id, MAX(changed_utc) FROM samples GROUP BY entity_id;";

        Dictionary<string, DateTimeOffset> latest = new(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            latest[reader.GetString(0)] = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));

        return latest;
    }

    public async Task<IReadOnlyList<(string EntityId, StateSample Sample)>> ListRecentSamplesAsync(
        string? entityContains,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var filter = string.IsNullOrWhiteSpace(entityContains) ? "" : " WHERE entity_id LIKE $like ESCAPE '\\'";
        command.CommandText = $"SELECT entity_id, changed_utc, state, numeric FROM samples{filter} ORDER BY changed_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        if (filter.Length > 0)
        {
            var escaped = entityContains!.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue("$like", "%" + escaped + "%");
        }

        List<(string, StateSample)> results = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add((reader.GetString(0), new StateSample(
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)))));

        return results;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetEarliestSampleTimesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id, MIN(changed_utc) FROM samples GROUP BY entity_id;";

        Dictionary<string, DateTimeOffset> earliest = new(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            earliest[reader.GetString(0)] = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));

        return earliest;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesAsync(
        DateTimeOffset sinceUtc,
        int maxPerEntity,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Rank within each entity so only its newest samples are returned. The primary key is
        // (entity_id, changed_utc), so the window runs over the index rather than sorting.
        command.CommandText = """
            SELECT entity_id, state, numeric, changed_utc
            FROM (
                SELECT entity_id, state, numeric, changed_utc,
                       ROW_NUMBER() OVER (PARTITION BY entity_id ORDER BY changed_utc DESC) AS rank
                FROM samples
                WHERE changed_utc >= $since
            )
            WHERE rank <= $cap
            ORDER BY entity_id, changed_utc;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$cap", Math.Max(1, maxPerEntity));

        return await ReadGroupedAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many entity ids one query names. SQLite takes far more, but the statement stays readable in a trace.</summary>
    private const int IdsPerQuery = 400;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesForAsync(
        IReadOnlyCollection<string> entityIds,
        DateTimeOffset sinceUtc,
        int maxPerEntity,
        CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<StateSample>> all = new(StringComparer.Ordinal);
        if (entityIds.Count == 0) return all;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in entityIds.Distinct(StringComparer.Ordinal).Chunk(IdsPerQuery))
        {
            await using var command = connection.CreateCommand();

            var names = batch.Select((_, i) => $"$e{i}").ToArray();
            command.CommandText = $"""
                SELECT entity_id, state, numeric, changed_utc
                FROM (
                    SELECT entity_id, state, numeric, changed_utc,
                           ROW_NUMBER() OVER (PARTITION BY entity_id ORDER BY changed_utc DESC) AS rank
                    FROM samples
                    WHERE changed_utc >= $since AND entity_id IN ({string.Join(", ", names)})
                )
                WHERE rank <= $cap
                ORDER BY entity_id, changed_utc;
                """;
            command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$cap", Math.Max(1, maxPerEntity));
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue(names[i], batch[i]);

            foreach (var (entityId, samples) in await ReadGroupedAsync(command, cancellationToken).ConfigureAwait(false))
                all[entityId] = samples;
        }

        return all;
    }

    /// <summary>An hour, in milliseconds: the bucket numeric history is thinned within.</summary>
    private const long BucketMillis = 60 * 60 * 1000;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetNumericHistoryAsync(
        DateTimeOffset sinceUtc,
        int perBucket,
        int maxPerEntity,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Two ranks, both newest-first. The inner one keeps a few readings from every hour the entity was
        // active, so the result reaches back across the whole retention window instead of piling up at the
        // end of it; the outer one bounds what any single entity can contribute to a scan.
        command.CommandText = """
            SELECT entity_id, state, numeric, changed_utc
            FROM (
                SELECT entity_id, state, numeric, changed_utc,
                       ROW_NUMBER() OVER (PARTITION BY entity_id ORDER BY changed_utc DESC) AS overall
                FROM (
                    SELECT entity_id, state, numeric, changed_utc,
                           ROW_NUMBER() OVER (
                               PARTITION BY entity_id, changed_utc / $bucket
                               ORDER BY changed_utc DESC) AS in_bucket
                    FROM samples
                    WHERE changed_utc >= $since AND numeric IS NOT NULL
                )
                WHERE in_bucket <= $perBucket
            )
            WHERE overall <= $cap
            ORDER BY entity_id, changed_utc;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$bucket", BucketMillis);
        command.Parameters.AddWithValue("$perBucket", Math.Max(1, perBucket));
        command.Parameters.AddWithValue("$cap", Math.Max(1, maxPerEntity));

        return await ReadGroupedAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an (entity_id, state, numeric, changed_utc) result already ordered by entity.</summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> ReadGroupedAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<StateSample>> grouped = new(StringComparer.Ordinal);
        List<StateSample>? current = null;
        string? currentEntity = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entityId = reader.GetString(0);
            if (currentEntity != entityId)
            {
                current = [];
                currentEntity = entityId;
                grouped[entityId] = current;
            }

            current!.Add(new StateSample(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }

        return grouped;
    }

    public async Task<int> PruneSamplesAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM samples WHERE changed_utc < $before;";
        command.Parameters.AddWithValue("$before", beforeUtc.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HistorySummary> GetHistorySummaryAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(DISTINCT entity_id), COUNT(*), MIN(changed_utc)
            FROM samples
            WHERE changed_utc >= $since;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new HistorySummary(0, 0, null);

        return new HistorySummary(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
    }

    public async Task<int> PruneProposalsAsync(DateTimeOffset decidedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM proposals
            WHERE status IN ($rejected, $failed, $superseded, $removed)
              AND decided_utc IS NOT NULL
              AND decided_utc < $before;
            """;
        command.Parameters.AddWithValue("$rejected", (int)ProposalStatus.Rejected);
        command.Parameters.AddWithValue("$failed", (int)ProposalStatus.Failed);
        command.Parameters.AddWithValue("$superseded", (int)ProposalStatus.Superseded);
        command.Parameters.AddWithValue("$removed", (int)ProposalStatus.Removed);
        command.Parameters.AddWithValue("$before", decidedBefore.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAnomaliesAsync(DateTimeOffset decidedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // A routine the user put away is kept for good: the row is the record of their "no", and without it
        // the same suggestion comes back as new the hour after the prune. A finding dismissed enough times to
        // be silenced is kept for the same reason.
        command.CommandText = """
            DELETE FROM anomalies
            WHERE status IN ($dismissed, $resolved)
              AND decided_utc IS NOT NULL
              AND decided_utc < $before
              AND NOT (status = $dismissed AND (kind = $habit OR dismissals >= $silenced));
            """;
        command.Parameters.AddWithValue("$dismissed", (int)AnomalyStatus.Dismissed);
        command.Parameters.AddWithValue("$resolved", (int)AnomalyStatus.Resolved);
        command.Parameters.AddWithValue("$habit", (int)AnomalyKind.Habit);
        command.Parameters.AddWithValue("$silenced", AnomalyScanner.DismissalsToSilence);
        command.Parameters.AddWithValue("$before", decidedBefore.ToUnixTimeMilliseconds());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- anomalies ----

    public async Task<Anomaly?> GetAnomalyAsync(long id, CancellationToken cancellationToken) =>
        await FindAnomalyAsync("id = $key", id, cancellationToken).ConfigureAwait(false);

    public async Task<Anomaly?> FindAnomalyAsync(string dedupKey, CancellationToken cancellationToken) =>
        await FindAnomalyAsync("dedup_key = $key", dedupKey, cancellationToken).ConfigureAwait(false);

    private async Task<Anomaly?> FindAnomalyAsync(string predicate, object key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {AnomalyColumns} FROM anomalies WHERE {predicate};";
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAnomaly(reader) : null;
    }

    public async Task<Anomaly> UpsertAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO anomalies
                (dedup_key, entity_id, kind, summary, evidence_json, suggested_request, status, detected_utc, decided_utc, proposal_id, severity, dismissals)
            VALUES
                ($dedup, $entity, $kind, $summary, $evidence, $suggested, $status, $detected, $decided, $proposal, $severity, $dismissals)
            ON CONFLICT (dedup_key) DO UPDATE SET
                summary = excluded.summary,
                evidence_json = excluded.evidence_json,
                suggested_request = excluded.suggested_request,
                detected_utc = excluded.detected_utc,
                severity = excluded.severity
            RETURNING id;
            """;

        BindAnomaly(command, anomaly);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return anomaly with { Id = id };
    }

    public async Task<IReadOnlyList<Anomaly>> ListAnomaliesAsync(
        AnomalyStatus? status,
        int limit,
        bool includeClosed,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        List<string> where = [];
        if (status is not null) where.Add("status = $status");
        // An explicit status is the caller saying exactly what it wants; do not then filter it away.
        else if (!includeClosed) where.Add("status NOT IN ($dismissed, $resolved)");

        var filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        command.CommandText = $"SELECT {AnomalyColumns} FROM anomalies{filter} ORDER BY severity DESC, detected_utc DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxPageSize));
        if (status is not null) command.Parameters.AddWithValue("$status", (int)status.Value);
        else if (!includeClosed)
        {
            command.Parameters.AddWithValue("$dismissed", (int)AnomalyStatus.Dismissed);
            command.Parameters.AddWithValue("$resolved", (int)AnomalyStatus.Resolved);
        }

        List<Anomaly> results = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadAnomaly(reader));

        return results;
    }

    public async Task UpdateAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE anomalies SET
                entity_id = $entity, kind = $kind, summary = $summary, evidence_json = $evidence,
                suggested_request = $suggested, status = $status, detected_utc = $detected,
                decided_utc = $decided, proposal_id = $proposal, severity = $severity, dismissals = $dismissals
            WHERE dedup_key = $dedup;
            """;

        BindAnomaly(command, anomaly);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- concerns ----

    public async Task<Concern> AddConcernAsync(Concern concern, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO concerns (text, entities_json, rule_json, explanation, interpreted, created_utc, note, provisional)
            VALUES ($text, $entities, $rule, $explanation, $interpreted, $created, $note, $provisional)
            RETURNING id;
            """;
        BindConcern(command, concern);
        command.Parameters.AddWithValue("$created", concern.CreatedUtc.ToUnixTimeMilliseconds());

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return concern with { Id = id };
    }

    public async Task UpdateConcernAsync(Concern concern, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE concerns SET
                text = $text, entities_json = $entities, rule_json = $rule, explanation = $explanation,
                interpreted = $interpreted, note = $note, provisional = $provisional
            WHERE id = $id;
            """;
        BindConcern(command, concern);
        command.Parameters.AddWithValue("$id", concern.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindConcern(SqliteCommand command, Concern concern)
    {
        command.Parameters.AddWithValue("$text", concern.Text);
        command.Parameters.AddWithValue("$entities", JsonSerializer.Serialize(concern.Entities));
        command.Parameters.AddWithValue("$rule", JsonSerializer.Serialize(concern.Rule));
        command.Parameters.AddWithValue("$explanation", (object?)concern.Explanation ?? DBNull.Value);
        command.Parameters.AddWithValue("$interpreted", concern.Interpreted ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)concern.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$provisional", concern.Provisional ? 1 : 0);
    }

    public async Task<IReadOnlyList<Concern>> ListConcernsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ConcernColumns} FROM concerns ORDER BY id;";

        List<Concern> results = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadConcern(reader));

        return results;
    }

    public async Task<Concern?> GetConcernAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ConcernColumns} FROM concerns WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConcern(reader) : null;
    }

    public async Task<bool> DeleteConcernAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM concerns WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private const string ConcernColumns = "id, text, entities_json, rule_json, explanation, interpreted, created_utc, note, provisional";

    private static Concern ReadConcern(SqliteDataReader row) => new()
    {
        Id = row.GetInt64(0),
        Text = row.GetString(1),
        Entities = JsonSerializer.Deserialize<List<string>>(row.GetString(2)) ?? [],
        Rule = JsonSerializer.Deserialize<WatchRule>(row.GetString(3)) ?? WatchRule.Attention,
        Explanation = row.IsDBNull(4) ? null : row.GetString(4),
        Interpreted = row.GetInt64(5) != 0,
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(6)),
        Note = row.IsDBNull(7) ? null : row.GetString(7),
        Provisional = row.GetInt64(8) != 0,
    };

    private const string AnomalyColumns =
        "id, dedup_key, entity_id, kind, summary, evidence_json, suggested_request, status, detected_utc, decided_utc, proposal_id, severity, dismissals";

    private static void BindAnomaly(SqliteCommand command, Anomaly anomaly)
    {
        command.Parameters.AddWithValue("$dedup", anomaly.DedupKey);
        command.Parameters.AddWithValue("$entity", anomaly.EntityId);
        command.Parameters.AddWithValue("$kind", (int)anomaly.Kind);
        command.Parameters.AddWithValue("$summary", anomaly.Summary);
        command.Parameters.AddWithValue("$evidence", anomaly.EvidenceJson);
        command.Parameters.AddWithValue("$suggested", anomaly.SuggestedRequest);
        command.Parameters.AddWithValue("$status", (int)anomaly.Status);
        command.Parameters.AddWithValue("$detected", anomaly.DetectedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$severity", anomaly.Severity);
        command.Parameters.AddWithValue("$decided", anomaly.DecidedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$proposal", (object?)anomaly.ProposalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dismissals", anomaly.Dismissals);
    }

    private static Anomaly ReadAnomaly(IDataRecord row) => new()
    {
        Id = row.GetInt64(0),
        DedupKey = row.GetString(1),
        EntityId = row.GetString(2),
        Kind = (AnomalyKind)row.GetInt32(3),
        Summary = row.GetString(4),
        EvidenceJson = row.GetString(5),
        SuggestedRequest = row.GetString(6),
        Status = (AnomalyStatus)row.GetInt32(7),
        DetectedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(8)),
        DecidedUtc = row.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(9)),
        ProposalId = row.IsDBNull(10) ? null : row.GetInt64(10),
        Severity = row.IsDBNull(11) ? 1 : row.GetDouble(11),
        Dismissals = row.IsDBNull(12) ? 0 : row.GetInt32(12),
    };

    // ---- plumbing ----

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Per-connection pragmas do not survive pooling, so they are set each time a connection is handed out.
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Text(IDataRecord row, int index) => row.IsDBNull(index) ? null : row.GetString(index);

    private static List<T> Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
