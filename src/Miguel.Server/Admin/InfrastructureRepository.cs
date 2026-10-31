using Miguel.Server.Nas;
using Npgsql;

namespace Miguel.Server.Admin;

public sealed class InfrastructureRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<AdminAgencyDefinition>> ReadAgenciesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT code, display_name, location, responsible,
                   default_backup_provider, enabled, created_at_utc,
                   updated_at_utc, updated_by
            FROM agencies
            ORDER BY display_name, code;
            """;
        List<AdminAgencyDefinition> agencies = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            agencies.Add(new AdminAgencyDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                ReadUtc(reader, 6),
                ReadUtc(reader, 7),
                reader.GetString(8)));
        }
        return agencies;
    }

    public async Task<bool> CreateAgencyAsync(
        AdminAgencyRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO agencies
                (code, display_name, location, responsible,
                 default_backup_provider, enabled, created_at_utc,
                 updated_at_utc, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $7, $8)
            ON CONFLICT (code) DO NOTHING;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddAgencyParameters(command, request, actor, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> UpdateAgencyAsync(
        string code,
        AdminAgencyRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE agencies SET
                display_name = $2,
                location = $3,
                responsible = $4,
                default_backup_provider = $5,
                enabled = $6,
                updated_at_utc = $7,
                updated_by = $8
            WHERE code = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddAgencyParameters(command, request with { Code = code }, actor, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<AdminNasDefinition>> ReadNasAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, agency_code, display_name, host, smb_port,
                   share_name, manufacturer, model, serial_number,
                   is_primary, enabled, last_probe_at_utc, last_probe_status,
                   last_probe_latency_ms, total_bytes, free_bytes,
                   last_probe_error, created_at_utc, updated_at_utc, updated_by,
                   lifecycle_revision, retired_at_utc, retired_by, retirement_reason
            FROM nas_appliances
            ORDER BY agency_code, is_primary DESC, display_name, code;
            """;
        List<AdminNasDefinition> appliances = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            appliances.Add(ReadNas(reader));
        }
        return appliances;
    }

    public async Task<AdminNasDefinition?> FindNasAsync(
        Guid nasId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, agency_code, display_name, host, smb_port,
                   share_name, manufacturer, model, serial_number,
                   is_primary, enabled, last_probe_at_utc, last_probe_status,
                   last_probe_latency_ms, total_bytes, free_bytes,
                   last_probe_error, created_at_utc, updated_at_utc, updated_by,
                   lifecycle_revision, retired_at_utc, retired_by, retirement_reason
            FROM nas_appliances
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(nasId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNas(reader) : null;
    }

    public async Task<bool> CreateNasAsync(
        Guid id,
        AdminNasRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO nas_appliances
                (id, code, agency_code, display_name, host, smb_port,
                 share_name, manufacturer, model, serial_number,
                 is_primary, enabled, created_at_utc, updated_at_utc, updated_by)
            SELECT
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                $11, $12, $13, $13, $14
            FROM agencies
            WHERE code = $3 AND enabled = TRUE
            ON CONFLICT (code) DO NOTHING;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({NasLifecycleService.CatalogLock})", connection, tx))
            await guard.ExecuteNonQueryAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection, tx);
        AddNasParameters(command, id, request, actor, now);
        bool changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<bool> UpdateNasAsync(
        Guid id,
        AdminNasRequest request,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE nas_appliances SET
                code = $2,
                agency_code = $3,
                display_name = $4,
                host = $5,
                smb_port = $6,
                share_name = $7,
                manufacturer = $8,
                model = $9,
                serial_number = $10,
                is_primary = $11,
                enabled = $12,
                updated_at_utc = $13,
                updated_by = $14
            WHERE id = $1 AND enabled = $12
              AND EXISTS
              (
                  SELECT 1 FROM agencies
                  WHERE code = $3 AND enabled = TRUE
              );
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({NasLifecycleService.CatalogLock})", connection, tx))
            await guard.ExecuteNonQueryAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection, tx);
        AddNasParameters(command, id, request, actor, now);
        bool changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<bool> UpdateDeviceBackupPolicyAsync(
        Guid deviceId,
        string provider,
        Guid? nasId,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string withoutNasSql = """
            UPDATE devices SET
                backup_provider = $2,
                nas_appliance_id = NULL
            WHERE id = $1
              AND enabled = TRUE;
            """;
        const string atlasSql = """
            UPDATE devices d SET
                backup_provider = $2,
                nas_appliance_id = $3
            WHERE d.id = $1
              AND d.enabled = TRUE
              AND
              (
                  $3 IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM nas_appliances n
                      WHERE n.id = $3
                        AND n.agency_code = d.agency_code
                        AND n.enabled = TRUE
                  )
              );
            """;
        bool requiresNas = BackupProviders.RequiresNas(provider);
        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(requiresNas ? atlasSql : withoutNasSql) : new NpgsqlCommand(requiresNas ? atlasSql : withoutNasSql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(provider);
        if (requiresNas)
        {
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)nasId ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> UpdateDeviceBackupPolicyWithAuditAsync(Guid deviceId, string provider, Guid? nasId,
        string actor, string remoteAddress, DateTimeOffset now, CancellationToken token, string actorRole = "Admin")
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(token);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(token);
        string previousProvider; Guid? previousNas;
        await using (NpgsqlCommand locked = new("SELECT backup_provider, nas_appliance_id FROM devices WHERE id=$1 AND enabled=TRUE FOR UPDATE", connection, tx)) {
            locked.Parameters.AddWithValue(deviceId);
            await using var reader = await locked.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            previousProvider = reader.GetString(0); previousNas = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }
        if (!await UpdateDeviceBackupPolicyAsync(deviceId,provider,nasId,token,tx)) return false;
        await new AdminRepository(dataSource).WriteAuditAsync(now,actorRole,actor,"admin.device.backup_policy.updated","Device",deviceId.ToString(),
            new { before = new { backupProvider=previousProvider, nasApplianceId=previousNas },
                  after = new { backupProvider=provider, nasApplianceId=nasId }, remoteAddress },token,tx);
        await tx.CommitAsync(token); return true;
    }

    public async Task RecordProbeAsync(
        Guid nasId,
        NasProbeResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken, long? expectedRevision = null)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        const string updateSql = """
            UPDATE nas_appliances SET
                last_probe_at_utc = $2,
                last_probe_status = $3,
                last_probe_latency_ms = $4,
                total_bytes = $5,
                free_bytes = $6,
                last_probe_error = $7
            WHERE id = $1 AND enabled AND ($8::bigint IS NULL OR lifecycle_revision=$8);
            """;
        await using (NpgsqlCommand update = new(updateSql, connection, transaction))
        {
            AddProbeParameters(update, nasId, result, now);
            update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, (object?)expectedRevision ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return;
        }

        const string historySql = """
            INSERT INTO nas_probe_history
                (nas_appliance_id, probed_at_utc, status, latency_ms,
                 total_bytes, free_bytes, error_code)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """;
        await using (NpgsqlCommand history = new(historySql, connection, transaction))
        {
            AddProbeParameters(history, nasId, result, now);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddAgencyParameters(
        NpgsqlCommand command,
        AdminAgencyRequest request,
        string actor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(request.Code);
        command.Parameters.AddWithValue(request.DisplayName);
        command.Parameters.AddWithValue((object?)request.Location ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Responsible ?? DBNull.Value);
        command.Parameters.AddWithValue(request.DefaultBackupProvider);
        command.Parameters.AddWithValue(request.Enabled);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
    }

    private static void AddNasParameters(
        NpgsqlCommand command,
        Guid id,
        AdminNasRequest request,
        string actor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Code);
        command.Parameters.AddWithValue(request.AgencyCode);
        command.Parameters.AddWithValue(request.DisplayName);
        command.Parameters.AddWithValue(request.Host);
        command.Parameters.AddWithValue(request.SmbPort);
        command.Parameters.AddWithValue(request.ShareName);
        command.Parameters.AddWithValue((object?)request.Manufacturer ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Model ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.SerialNumber ?? DBNull.Value);
        command.Parameters.AddWithValue(request.IsPrimary);
        command.Parameters.AddWithValue(request.Enabled);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
    }

    private static void AddProbeParameters(
        NpgsqlCommand command,
        Guid nasId,
        NasProbeResult result,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue(nasId);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(result.Status);
        command.Parameters.AddWithValue((object?)result.LatencyMs ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)result.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)result.FreeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)result.ErrorCode ?? DBNull.Value);
    }

    private static AdminNasDefinition ReadNas(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetBoolean(10),
            reader.GetBoolean(11),
            reader.IsDBNull(12) ? null : ReadUtc(reader, 12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            ReadUtc(reader, 18),
            ReadUtc(reader, 19),
            reader.GetString(20), reader.GetInt64(21),
            reader.IsDBNull(22) ? null : ReadUtc(reader, 22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24));

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
