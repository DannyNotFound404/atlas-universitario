using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Cases;

public sealed class CaseRepository(NpgsqlDataSource dataSource)
{
    private const string ListColumns = """
        c.id, c.case_number, c.case_type, c.title, c.severity, c.priority,
        c.status, c.agency_code, a.display_name, c.assigned_to,
        c.institutional_owner, c.due_at_utc, c.created_at_utc,
        c.updated_at_utc,
        (SELECT COUNT(*)::int FROM operational_case_devices cd WHERE cd.case_id = c.id)
        """;

    public async Task<AdminCasePage> ReadPageAsync(
        string? search,
        string? type,
        string? status,
        string? severity,
        string? agencyCode,
        int page,
        int pageSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        List<object> parameters = [];
        List<string> filters = [];

        AddFilter(filters, parameters, "c.case_type", NormalizeFilter(type));
        AddFilter(filters, parameters, "c.status", NormalizeFilter(status));
        AddFilter(filters, parameters, "c.severity", NormalizeFilter(severity));
        AddFilter(filters, parameters, "c.agency_code", NormalizeFilter(agencyCode));
        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add($"%{search.Trim()}%");
            int index = parameters.Count;
            filters.Add($"(c.case_number ILIKE ${index} OR c.title ILIKE ${index} OR " +
                        $"c.description ILIKE ${index} OR c.assigned_to ILIKE ${index})");
        }

        string where = filters.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", filters);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        int total;
        await using (NpgsqlCommand count = new($"SELECT COUNT(*)::int FROM operational_cases c {where};", connection))
        {
            AddParameters(count, parameters);
            total = (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        parameters.Add(pageSize);
        int limitIndex = parameters.Count;
        parameters.Add((page - 1) * pageSize);
        int offsetIndex = parameters.Count;
        string pageSql = $"""
            SELECT {ListColumns}
            FROM operational_cases c
            LEFT JOIN agencies a ON a.code = c.agency_code
            {where}
            ORDER BY
                CASE c.priority WHEN 'URGENT' THEN 0 WHEN 'HIGH' THEN 1
                     WHEN 'MEDIUM' THEN 2 ELSE 3 END,
                c.updated_at_utc DESC
            LIMIT ${limitIndex} OFFSET ${offsetIndex};
            """;
        List<AdminCaseListItem> items = [];
        await using (NpgsqlCommand command = new(pageSql, connection))
        {
            AddParameters(command, parameters);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadListItem(reader));
            }
        }

        AdminCaseSummary summary;
        const string summarySql = """
            SELECT
                COUNT(*)::int,
                COUNT(*) FILTER (WHERE status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION'))::int,
                COUNT(*) FILTER (WHERE severity = 'CRITICAL' AND status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION'))::int,
                COUNT(*) FILTER (WHERE due_at_utc < $1 AND status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION'))::int,
                COUNT(*) FILTER (WHERE assigned_to IS NULL AND status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION'))::int
            FROM operational_cases;
            """;
        await using (NpgsqlCommand command = new(summarySql, connection))
        {
            command.Parameters.AddWithValue(now);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            summary = new(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4));
        }

        return new(now, page, pageSize, total, summary, items);
    }

    public async Task<AdminCaseDetail?> ReadDetailAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadDetailAsync(connection, id, cancellationToken);
    }

    public async Task<AdminCaseDetail?> ReadOpenBySourceAsync(
        string sourceKind,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id
            FROM operational_cases
            WHERE source_kind = $1
              AND source_key = $2
              AND status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION')
            LIMIT 1;
            """;
        Guid? id = null;
        await using (NpgsqlCommand command = new(sql, connection))
        {
            command.Parameters.AddWithValue(sourceKind);
            command.Parameters.AddWithValue(sourceKey);
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is Guid value) id = value;
        }
        return id is null
            ? null
            : await ReadDetailAsync(connection, id.Value, cancellationToken);
    }

    public async Task<AdminCaseDetail> CreateAsync(
        AdminCaseCreateRequest request,
        string actor,
        string actorRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        long sequence;
        await using (NpgsqlCommand command = new("SELECT nextval('atlas_case_number_seq');", connection, transaction))
        {
            sequence = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        Guid id = Guid.NewGuid();
        string caseNumber = $"ATLAS-{now.UtcDateTime.Year}-{sequence:000000}";
        const string insertSql = """
            INSERT INTO operational_cases
                (id, case_number, case_type, title, description, source,
                 severity, priority, status, agency_code, assigned_to,
                 institutional_owner, due_at_utc, closed_at_utc,
                 created_at_utc, created_by, updated_at_utc, updated_by,
                 source_kind, source_key, source_entity_id, source_snapshot)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, 'NEW', $9, $10,
                 $11, $12, NULL, $13, $14, $13, $14, $15, $16, $17, $18);
            """;
        await using (NpgsqlCommand command = new(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(caseNumber);
            command.Parameters.AddWithValue(request.Type);
            command.Parameters.AddWithValue(request.Title);
            command.Parameters.AddWithValue(request.Description);
            command.Parameters.AddWithValue((object?)request.Source ?? DBNull.Value);
            command.Parameters.AddWithValue(request.Severity);
            command.Parameters.AddWithValue(request.Priority);
            command.Parameters.AddWithValue((object?)request.AgencyCode ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.AssignedTo ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.InstitutionalOwner ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.DueAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            command.Parameters.AddWithValue((object?)request.SourceKind ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.SourceKey ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.SourceEntityId ?? DBNull.Value);
            command.Parameters.Add(CreateSourceSnapshotParameter(request.SourceSnapshot));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var relatedDevices = (request.DeviceIds ?? []).ToHashSet();
        // Resolve against authoritative records, not the currently loaded UI page
        // or user-supplied snapshot names. Preserve all explicitly chosen assets.
        if (Guid.TryParse(request.SourceEntityId ?? request.SourceKey, out Guid originId))
        {
            string? originSql = request.SourceKind switch
            {
                "BACKUP_CONTROL" => "SELECT device_id FROM backup_control_profiles WHERE id=$1",
                "VULNERABILITY" => "SELECT device_id FROM vulnerability_findings WHERE id=$1",
                "BACKUP" or "DEVICE" or "POSTURE" or "SECURITY" or "ASSET_CUSTODY" =>
                    "SELECT id FROM devices WHERE id=$1",
                _ => null
            };
            if (originSql is not null)
            {
                await using var origin = new NpgsqlCommand(originSql, connection, transaction);
                origin.Parameters.AddWithValue(originId);
                if (await origin.ExecuteScalarAsync(cancellationToken) is Guid deviceId)
                    relatedDevices.Add(deviceId);
            }
        }
        await ReplaceDevicesAsync(connection, transaction, id, relatedDevices.ToArray(), cancellationToken);
        await InsertTimelineAsync(
            connection, transaction, id, "CREATED", "Expediente creado.",
            null, "NEW", actor, actorRole, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadDetailAsync(connection, id, cancellationToken))!;
    }

    public async Task<AdminCaseDetail?> UpdateAsync(
        Guid id,
        AdminCaseUpdateRequest request,
        string actor,
        string actorRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? oldStatus = null;
        string? oldAssignedTo = null;
        await using (NpgsqlCommand command = new(
            "SELECT status, assigned_to FROM operational_cases WHERE id = $1 FOR UPDATE;",
            connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            oldStatus = reader.GetString(0);
            oldAssignedTo = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        const string updateSql = """
            UPDATE operational_cases
            SET case_type = $2, title = $3, description = $4, source = $5,
                severity = $6, priority = $7, status = $8, agency_code = $9,
                assigned_to = $10, institutional_owner = $11, due_at_utc = $12,
                closed_at_utc = CASE
                    WHEN $8 IN ('CLOSED', 'ACCEPTED_EXCEPTION')
                        THEN COALESCE(closed_at_utc, $13)
                    ELSE NULL END,
                updated_at_utc = $13, updated_by = $14
            WHERE id = $1;
            """;
        await using (NpgsqlCommand command = new(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(request.Type);
            command.Parameters.AddWithValue(request.Title);
            command.Parameters.AddWithValue(request.Description);
            command.Parameters.AddWithValue((object?)request.Source ?? DBNull.Value);
            command.Parameters.AddWithValue(request.Severity);
            command.Parameters.AddWithValue(request.Priority);
            command.Parameters.AddWithValue(request.Status);
            command.Parameters.AddWithValue((object?)request.AgencyCode ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.AssignedTo ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.InstitutionalOwner ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.DueAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceDevicesAsync(connection, transaction, id, request.DeviceIds, cancellationToken);
        if (!string.Equals(oldStatus, request.Status, StringComparison.Ordinal))
        {
            await InsertTimelineAsync(connection, transaction, id, "STATUS_CHANGED",
                request.ChangeNote, oldStatus, request.Status, actor, actorRole, now, cancellationToken);
        }
        if (!string.Equals(oldAssignedTo, request.AssignedTo, StringComparison.Ordinal))
        {
            await InsertTimelineAsync(connection, transaction, id, "ASSIGNMENT_CHANGED",
                $"Asignación: {oldAssignedTo ?? "Sin asignar"} → {request.AssignedTo ?? "Sin asignar"}.",
                null, null, actor, actorRole, now, cancellationToken);
        }
        if (string.Equals(oldStatus, request.Status, StringComparison.Ordinal) &&
            string.Equals(oldAssignedTo, request.AssignedTo, StringComparison.Ordinal))
        {
            await InsertTimelineAsync(connection, transaction, id, "UPDATED",
                request.ChangeNote ?? "Expediente actualizado.", null, null,
                actor, actorRole, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ReadDetailAsync(connection, id, cancellationToken);
    }

    public async Task<AdminCaseDetail?> AddCommentAsync(
        Guid id,
        string comment,
        string actor,
        string actorRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (NpgsqlCommand update = new(
            "UPDATE operational_cases SET updated_at_utc = $2, updated_by = $3 WHERE id = $1;",
            connection, transaction))
        {
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(now);
            update.Parameters.AddWithValue(actor);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }
        await InsertTimelineAsync(connection, transaction, id, "COMMENT", comment,
            null, null, actor, actorRole, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadDetailAsync(connection, id, cancellationToken);
    }

    internal static NpgsqlParameter CreateSourceSnapshotParameter(
        JsonElement? sourceSnapshot) =>
        new()
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = sourceSnapshot is { ValueKind: JsonValueKind.Object }
                ? sourceSnapshot.Value.GetRawText()
                : "{}"
        };

    private static async Task ReplaceDevicesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        IReadOnlyList<Guid>? deviceIds,
        CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand delete = new(
            "DELETE FROM operational_case_devices WHERE case_id = $1;", connection, transaction))
        {
            delete.Parameters.AddWithValue(caseId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (Guid deviceId in deviceIds ?? [])
        {
            await using NpgsqlCommand insert = new(
                "INSERT INTO operational_case_devices (case_id, device_id) VALUES ($1, $2);",
                connection, transaction);
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(deviceId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertTimelineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string eventType,
        string? comment,
        string? fromStatus,
        string? toStatus,
        string actor,
        string actorRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO operational_case_timeline
                (case_id, event_type, comment, from_status, to_status,
                 actor_id, actor_role, occurred_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue((object?)comment ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)fromStatus ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)toStatus ?? DBNull.Value);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(actorRole);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdminCaseDetail?> ReadDetailAsync(
        NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {ListColumns}, c.description, c.source, c.source_kind,
                   c.source_key, c.source_entity_id, c.source_snapshot::text,
                   c.created_by, c.updated_by, c.closed_at_utc
            FROM operational_cases c
            LEFT JOIN agencies a ON a.code = c.agency_code
            WHERE c.id = $1;
            """;
        AdminCaseListItem? item;
        string description;
        string? source;
        string? sourceKind;
        string? sourceKey;
        string? sourceEntityId;
        JsonElement sourceSnapshot;
        string createdBy;
        string updatedBy;
        DateTimeOffset? closedAt;
        await using (NpgsqlCommand command = new(sql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            item = ReadListItem(reader);
            description = reader.GetString(15);
            source = reader.IsDBNull(16) ? null : reader.GetString(16);
            sourceKind = reader.IsDBNull(17) ? null : reader.GetString(17);
            sourceKey = reader.IsDBNull(18) ? null : reader.GetString(18);
            sourceEntityId = reader.IsDBNull(19) ? null : reader.GetString(19);
            sourceSnapshot = JsonSerializer.Deserialize<JsonElement>(reader.GetString(20));
            createdBy = reader.GetString(21);
            updatedBy = reader.GetString(22);
            closedAt = ReadNullableTimestamp(reader, 23);
        }

        List<AdminCaseDevice> devices = [];
        const string deviceSql = """
            SELECT d.id, d.machine_name, d.agency_code,
                   CASE WHEN NOT d.enabled THEN 'RETIRED'
                        WHEN d.last_seen_at_utc >= NOW() - INTERVAL '15 minutes' THEN 'ONLINE'
                        WHEN d.last_seen_at_utc >= NOW() - INTERVAL '24 hours' THEN 'ATTENTION'
                        ELSE 'OFFLINE' END
            FROM operational_case_devices cd
            JOIN devices d ON d.id = cd.device_id
            WHERE cd.case_id = $1
            ORDER BY d.machine_name;
            """;
        await using (NpgsqlCommand command = new(deviceSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                devices.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        List<AdminCaseTimelineItem> timeline = [];
        const string timelineSql = """
            SELECT id, event_type, comment, from_status, to_status,
                   actor_id, actor_role, occurred_at_utc
            FROM operational_case_timeline
            WHERE case_id = $1
            ORDER BY occurred_at_utc DESC, id DESC;
            """;
        await using (NpgsqlCommand command = new(timelineSql, connection))
        {
            command.Parameters.AddWithValue(id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                timeline.Add(new(
                    reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }
        return new(
            item, description, source, sourceKind, sourceKey, sourceEntityId,
            sourceSnapshot, createdBy, updatedBy, closedAt, devices, timeline);
    }

    private static AdminCaseListItem ReadListItem(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ReadNullableTimestamp(reader, 11),
        reader.GetFieldValue<DateTimeOffset>(12),
        reader.GetFieldValue<DateTimeOffset>(13),
        reader.GetInt32(14));

    private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetFieldValue<DateTimeOffset>(index);

    private static void AddFilter(
        List<string> filters,
        List<object> parameters,
        string column,
        string? value)
    {
        if (value is null) return;
        parameters.Add(value);
        filters.Add($"{column} = ${parameters.Count}");
    }

    private static string? NormalizeFilter(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static void AddParameters(NpgsqlCommand command, IEnumerable<object> values)
    {
        foreach (object value in values) command.Parameters.AddWithValue(value);
    }
}
