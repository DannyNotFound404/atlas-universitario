using System.Text.Json;
using Miguel.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Admin;

public sealed class AdminRepository(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<bool> AnyAdminExistsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM admin_users WHERE enabled = TRUE);");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<AdminUserAccount?> FindAdminAsync(
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id, username, normalized_username, display_name, password_hash,
                role, enabled, failed_attempts, locked_until_utc,
                created_at_utc, last_login_at_utc, security_revision
            FROM admin_users
            WHERE normalized_username = $1;
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(normalizedUsername);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadAdmin(reader)
            : null;
    }

    public async Task CreateAdminAsync(
        AdminUserAccount account,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_users
                (id, username, normalized_username, display_name, password_hash,
                 role, enabled, failed_attempts, created_at_utc)
            VALUES
                ($1, $2, $3, $4, $5, $6, TRUE, 0, $7);
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(account.Id);
        command.Parameters.AddWithValue(account.Username);
        command.Parameters.AddWithValue(account.NormalizedUsername);
        command.Parameters.AddWithValue(account.DisplayName);
        command.Parameters.AddWithValue(account.PasswordHash);
        command.Parameters.AddWithValue(account.Role);
        command.Parameters.AddWithValue(account.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> RecordFailedLoginAsync(
        Guid adminId,
        int threshold,
        DateTimeOffset lockUntilUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE admin_users SET
                failed_attempts = failed_attempts + 1,
                locked_until_utc = CASE
                    WHEN failed_attempts + 1 >= $2 THEN $3
                    ELSE locked_until_utc
                END
            WHERE id = $1
            RETURNING locked_until_utc;
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(adminId);
        command.Parameters.AddWithValue(threshold);
        command.Parameters.AddWithValue(lockUntilUtc);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            null or DBNull => null,
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                "PostgreSQL devolvió una fecha de bloqueo inesperada.")
        };
    }

    public async Task RecordSuccessfulLoginAsync(
        Guid adminId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE admin_users SET
                failed_attempts = 0,
                locked_until_utc = NULL,
                last_login_at_utc = $2
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(adminId);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdatePasswordHashAsync(
        Guid adminId,
        string passwordHash,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "UPDATE admin_users SET password_hash = $2 WHERE id = $1 AND password_hash = $3 AND enabled;");
        command.Parameters.AddWithValue(adminId);
        command.Parameters.AddWithValue(passwordHash);
        command.Parameters.AddWithValue(expectedHash);
        return await command.ExecuteNonQueryAsync(cancellationToken)==1;
    }

    public async Task WriteAuditAsync(
        DateTimeOffset occurredAtUtc,
        string actorType,
        string actorId,
        string action,
        string? targetType,
        string? targetId,
        object? detail,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            INSERT INTO audit_events
                (occurred_at_utc, actor_type, actor_id, action,
                 target_type, target_id, detail)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7);
            """;

        await using NpgsqlCommand command = transaction is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue(occurredAtUtc);
        command.Parameters.AddWithValue(actorType);
        command.Parameters.AddWithValue(actorId);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue((object?)targetType ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)targetId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = detail is null
                ? DBNull.Value
                : Miguel.Server.Audit.AuditRedactor.RedactJson(JsonSerializer.Serialize(detail, JsonOptions))!.Value.GetRawText()
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<AdminDeviceData>> ReadDevicesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.id,
                d.agency_code,
                d.machine_name,
                d.enabled,
                d.agent_version,
                d.backup_client_version,
                d.enrolled_at_utc,
                d.last_seen_at_utc,
                d.agent_started_at_utc,
                d.backup_running,
                d.pending_events,
                d.interactive_user_account,
                d.interactive_user_display_name,
                d.interactive_user_sid,
                d.interactive_user_session_type,
                d.interactive_user_resolution_source,
                d.interactive_user_checked_at_utc,
                d.retired_at_utc,
                d.retired_by,
                d.retirement_reason,
                latest_inventory.captured_at_utc,
                latest_inventory.payload::text,
                latest_backup.finished_at_utc,
                latest_backup.payload::text,
                d.backup_provider,
                d.nas_appliance_id,
                assigned_nas.display_name,
                assigned_nas.host,
                assigned_nas.share_name,
                d.update_ring_code,
                d.updater_heartbeat_received_at_utc
            FROM devices d
            LEFT JOIN nas_appliances assigned_nas
                ON assigned_nas.id = d.nas_appliance_id
            LEFT JOIN LATERAL
            (
                SELECT captured_at_utc, payload
                FROM inventory_snapshots
                WHERE device_id = d.id
                ORDER BY captured_at_utc DESC
                LIMIT 1
            ) latest_inventory ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT finished_at_utc, payload
                FROM backup_runs
                WHERE device_id = d.id
                ORDER BY finished_at_utc DESC
                LIMIT 1
            ) latest_backup ON TRUE
            ORDER BY COALESCE(d.updater_heartbeat_received_at_utc, d.last_seen_at_utc) DESC,
                d.machine_name;
            """;

        List<AdminDeviceData> devices = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            DeviceInventory? inventory = reader.IsDBNull(21)
                ? null
                : JsonSerializer.Deserialize<DeviceInventory>(
                    reader.GetString(21),
                    JsonOptions);
            BackupRunReport? backup = reader.IsDBNull(23)
                ? null
                : JsonSerializer.Deserialize<BackupRunReport>(
                    reader.GetString(23),
                    JsonOptions);

            devices.Add(new AdminDeviceData(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ReadUtc(reader, 6),
                ReadUtc(reader, 7),
                reader.IsDBNull(8) ? null : ReadUtc(reader, 8),
                reader.GetBoolean(9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : ReadUtc(reader, 16),
                reader.IsDBNull(20) ? null : ReadUtc(reader, 20),
                inventory,
                reader.IsDBNull(22) ? null : ReadUtc(reader, 22),
                backup,
                reader.IsDBNull(17) ? null : ReadUtc(reader, 17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetGuid(25),
                reader.IsDBNull(26) ? null : reader.GetString(26),
                reader.IsDBNull(27) ? null : reader.GetString(27),
                reader.IsDBNull(28) ? null : reader.GetString(28),
                reader.GetString(29),
                reader.IsDBNull(30) ? null : ReadUtc(reader, 30)));
        }

        return devices;
    }

    internal async Task<AdminDeviceRetirementResult?> RetireDeviceAsync(
        Guid deviceId,
        string actor,
        string reason,
        string remoteAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT machine_name, agency_code, enabled,
                   retired_at_utc, retired_by, retirement_reason
            FROM devices
            WHERE id = $1
            FOR UPDATE;
            """;
        string machineName;
        string agencyCode;
        bool enabled;
        DateTimeOffset? retiredAtUtc;
        string? retiredBy;
        string? retirementReason;
        await using (NpgsqlCommand selectCommand = new(
                         selectSql,
                         connection,
                         transaction))
        {
            selectCommand.Parameters.AddWithValue(deviceId);
            await using NpgsqlDataReader reader =
                await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            machineName = reader.GetString(0);
            agencyCode = reader.GetString(1);
            enabled = reader.GetBoolean(2);
            retiredAtUtc = reader.IsDBNull(3) ? null : ReadUtc(reader, 3);
            retiredBy = reader.IsDBNull(4) ? null : reader.GetString(4);
            retirementReason = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        if (!enabled)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminDeviceRetirementResult(
                false,
                machineName,
                agencyCode,
                retiredAtUtc,
                retiredBy,
                retirementReason);
        }

        const string updateSql = """
            UPDATE devices SET
                enabled = FALSE,
                backup_running = FALSE,
                retired_at_utc = $2,
                retired_by = $3,
                retirement_reason = $4
            WHERE id = $1;
            """;
        await using (NpgsqlCommand updateCommand = new(
                         updateSql,
                         connection,
                         transaction))
        {
            updateCommand.Parameters.AddWithValue(deviceId);
            updateCommand.Parameters.AddWithValue(now);
            updateCommand.Parameters.AddWithValue(actor);
            updateCommand.Parameters.AddWithValue(reason);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string auditSql = """
            INSERT INTO audit_events
                (occurred_at_utc, actor_type, actor_id, action,
                 target_type, target_id, detail)
            VALUES
                ($1, 'Administrator', $2, 'admin.device.retired',
                 'Device', $3, $4);
            """;
        await using (NpgsqlCommand auditCommand = new(
                         auditSql,
                         connection,
                         transaction))
        {
            auditCommand.Parameters.AddWithValue(now);
            auditCommand.Parameters.AddWithValue(actor);
            auditCommand.Parameters.AddWithValue(deviceId.ToString("D"));
            auditCommand.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Jsonb,
                Value = JsonSerializer.Serialize(
                    new
                    {
                        machineName,
                        agencyCode,
                        reason,
                        remoteAddress
                    },
                    JsonOptions)
            });
            await auditCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AdminDeviceRetirementResult(
            true,
            machineName,
            agencyCode,
            now,
            actor,
            reason);
    }

    private static AdminUserAccount ReadAdmin(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : ReadUtc(reader, 8),
            ReadUtc(reader, 9),
            reader.IsDBNull(10) ? null : ReadUtc(reader, 10), reader.GetInt32(11));

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
