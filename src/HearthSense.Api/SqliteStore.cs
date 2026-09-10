using System.Data;
using System.Text.Json;
using HearthSense.Core;
using Microsoft.Data.Sqlite;

namespace HearthSense.Api;

/// <summary>
/// Three tables, hand-written SQL, no ORM. The schema is small enough that an ORM would cost more than
/// it saves, and every query here is one the app actually issues.
/// </summary>
public sealed class SqliteStore(string connectionString) : IStore
{
    private const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ConnectionStringFor(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

    /// <summary>Creates the schema if needed. Safe to call on every start.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        // A first run inside a container points at /data/hearthsense.db; make sure the folder is there so
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

        // Each step brings a database from version N to N+1; a fresh file runs all of them in order.
        if (current < 1) await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
        if (current < 2) await ExecuteAsync(connection, SchemaV2, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"PRAGMA user_version={SchemaVersion};", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Version 2: a proposal can refine an earlier one, carrying the user's feedback.</summary>
    private const string SchemaV2 = """
        ALTER TABLE proposals ADD COLUMN feedback TEXT;
        ALTER TABLE proposals ADD COLUMN parent_id INTEGER;
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
                 duplicates_json, ha_automation_id, error, anomaly_id, created_utc, decided_utc, feedback, parent_id)
            VALUES
                ($request, $source, $status, $alias, $description, $config, $entities, $actions,
                 $duplicates, $haId, $error, $anomalyId, $created, $decided, $feedback, $parentId)
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

    public async Task<IReadOnlyList<Proposal>> ListProposalsAsync(ProposalStatus? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = status is null
            ? $"SELECT {ProposalColumns} FROM proposals ORDER BY id DESC LIMIT $limit;"
            : $"SELECT {ProposalColumns} FROM proposals WHERE status = $status ORDER BY id DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
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
                feedback = $feedback, parent_id = $parentId
            WHERE id = $id;
            """;

        Bind(command, proposal);
        command.Parameters.AddWithValue("$id", proposal.Id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string ProposalColumns =
        "id, request, source, status, alias, description, config_json, entities_json, actions_json, " +
        "duplicates_json, ha_automation_id, error, anomaly_id, created_utc, decided_utc, feedback, parent_id";

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

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT entity_id, state, numeric, changed_utc
            FROM samples
            WHERE changed_utc >= $since
            ORDER BY entity_id, changed_utc;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeMilliseconds());

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
                (dedup_key, entity_id, kind, summary, evidence_json, suggested_request, status, detected_utc, decided_utc, proposal_id)
            VALUES
                ($dedup, $entity, $kind, $summary, $evidence, $suggested, $status, $detected, $decided, $proposal)
            ON CONFLICT (dedup_key) DO UPDATE SET
                summary = excluded.summary,
                evidence_json = excluded.evidence_json,
                suggested_request = excluded.suggested_request,
                detected_utc = excluded.detected_utc
            RETURNING id;
            """;

        BindAnomaly(command, anomaly);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return anomaly with { Id = id };
    }

    public async Task<IReadOnlyList<Anomaly>> ListAnomaliesAsync(AnomalyStatus? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = status is null
            ? $"SELECT {AnomalyColumns} FROM anomalies ORDER BY detected_utc DESC LIMIT $limit;"
            : $"SELECT {AnomalyColumns} FROM anomalies WHERE status = $status ORDER BY detected_utc DESC LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        if (status is not null) command.Parameters.AddWithValue("$status", (int)status.Value);

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
                decided_utc = $decided, proposal_id = $proposal
            WHERE dedup_key = $dedup;
            """;

        BindAnomaly(command, anomaly);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string AnomalyColumns =
        "id, dedup_key, entity_id, kind, summary, evidence_json, suggested_request, status, detected_utc, decided_utc, proposal_id";

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
        command.Parameters.AddWithValue("$decided", anomaly.DecidedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$proposal", (object?)anomaly.ProposalId ?? DBNull.Value);
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
