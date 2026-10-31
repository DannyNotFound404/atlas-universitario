using System.Security.Cryptography;
using System.Text.Json;
using Miguel.Contracts;
using Miguel.Server.Admin;
using Miguel.Server.Audit;
using Miguel.Server.Security;
using Miguel.Server.Software;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Storage;

public sealed class MiguelRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    AdminRepository auditRepository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnrollmentTokenIssue> IssueEnrollmentTokenAsync(
        string agencyCode,
        TimeSpan lifetime,
        EnrollmentIssuanceContext issuanceContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agencyCode) || agencyCode.Length > 64)
        {
            throw new ArgumentException("El código de agencia no es válido.", nameof(agencyCode));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "La vigencia debe ser mayor que cero y no exceder siete días.");
        }

        string token = CredentialHasher.CreateSecret();
        byte[] tokenHash = CredentialHasher.HashEnrollmentToken(token);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expiresAtUtc = now.Add(lifetime);
        Guid issuanceId = Guid.NewGuid();

        string normalizedAgency = agencyCode.Trim().ToUpperInvariant();
        const string sql = """
            INSERT INTO enrollment_tokens
                (token_hash, agency_code, created_at_utc, expires_at_utc,
                 issuance_id, issued_by, issued_by_role, issued_from_ip,
                 issuance_source, installer_version)
            SELECT $1, code, $3, $4, $5, $6, $7, $8, $9, $10
            FROM agencies
            WHERE code = $2 AND enabled = TRUE;
            """;

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(tokenHash);
        command.Parameters.AddWithValue(normalizedAgency);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(expiresAtUtc);
        command.Parameters.AddWithValue(issuanceId);
        command.Parameters.AddWithValue(NormalizeIssuer(issuanceContext.ActorId));
        command.Parameters.AddWithValue(NormalizeIssuer(issuanceContext.ActorRole));
        command.Parameters.AddWithValue(NormalizeAddress(issuanceContext.RemoteAddress));
        command.Parameters.AddWithValue(NormalizeSource(issuanceContext.Source));
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeVersion(issuanceContext.InstallerVersion));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ArgumentException(
                "La agencia no existe o esta deshabilitada.",
                nameof(agencyCode));
        }

        await auditRepository.WriteAuditAsync(
            now,
            NormalizeIssuer(issuanceContext.ActorRole),
            NormalizeIssuer(issuanceContext.ActorId),
            "admin.installer.generated",
            "Agency",
            normalizedAgency,
            new
            {
                issuanceId,
                lifetimeHours = Math.Round(lifetime.TotalHours, 2),
                expiresAtUtc,
                remoteAddress = NormalizeAddress(issuanceContext.RemoteAddress),
                source = NormalizeSource(issuanceContext.Source),
                installerVersion = NormalizeVersion(issuanceContext.InstallerVersion),
                tokenStored = false
            },
            cancellationToken,
            transaction);
        await transaction.CommitAsync(cancellationToken);

        return new EnrollmentTokenIssue(
            token,
            issuanceId,
            now,
            expiresAtUtc,
            normalizedAgency,
            NormalizeVersion(issuanceContext.InstallerVersion));
    }

    public async Task<EnrollmentResponse> EnrollAsync(
        EnrollmentRequest request,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        ValidateEnrollmentRequest(request);

        byte[] tokenHash = CredentialHasher.HashEnrollmentToken(request.EnrollmentToken);
        byte[] credentialHash = CredentialHasher.HashDeviceKey(request.InstallationId, request.DeviceKey);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string consumeTokenSql = """
            DELETE FROM enrollment_tokens
            WHERE token_hash = $1
              AND expires_at_utc > $2
            RETURNING agency_code, issuance_id, issued_by, issued_by_role,
                      issued_from_ip, issuance_source, installer_version,
                      created_at_utc, expires_at_utc,
                      issued_by_display_name_snapshot, agency_name_snapshot;
            """;

        string? agencyCode = null;
        Guid? issuanceId = null;
        string? issuedBy = null;
        string? issuedByRole = null;
        string? issuedFromIp = null;
        string issuanceSource = "LEGACY";
        string? installerVersion = null;
        DateTimeOffset? issuedAtUtc = null;
        DateTimeOffset? tokenExpiresAtUtc = null;
        string? issuedByDisplayName = null, agencyName = null;
        await using (NpgsqlCommand tokenCommand = new(consumeTokenSql, connection, transaction))
        {
            tokenCommand.Parameters.AddWithValue(tokenHash);
            tokenCommand.Parameters.AddWithValue(now);
            await using NpgsqlDataReader reader =
                await tokenCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                agencyCode = reader.GetString(0);
                issuanceId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                issuedBy = reader.IsDBNull(2) ? null : reader.GetString(2);
                issuedByRole = reader.IsDBNull(3) ? null : reader.GetString(3);
                issuedFromIp = reader.IsDBNull(4) ? null : reader.GetString(4);
                issuanceSource = reader.GetString(5);
                installerVersion = reader.IsDBNull(6) ? null : reader.GetString(6);
                issuedAtUtc = reader.GetFieldValue<DateTimeOffset>(7);
                tokenExpiresAtUtc = reader.GetFieldValue<DateTimeOffset>(8);
                issuedByDisplayName = reader.IsDBNull(9) ? null : reader.GetString(9);
                agencyName = reader.IsDBNull(10) ? null : reader.GetString(10);
            }
        }

        if (agencyCode is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new EnrollmentRejectedException();
        }

        bool isReenrollment;
        await using (NpgsqlCommand existingCommand = new(
            "SELECT EXISTS(SELECT 1 FROM devices WHERE installation_id = $1);",
            connection,
            transaction))
        {
            existingCommand.Parameters.AddWithValue(request.InstallationId);
            isReenrollment = (bool)(
                await existingCommand.ExecuteScalarAsync(cancellationToken) ??
                false);
        }

        const string enrollSql = """
            INSERT INTO devices
                (id, installation_id, agency_code, machine_name, credential_hash,
                 agent_version, backup_client_version, enrolled_at_utc,
                 last_seen_at_utc, backup_provider)
            SELECT
                $1, $1, $2, $3, $4, $5, $6, $7, $7,
                a.default_backup_provider
            FROM agencies a
            WHERE a.code = $2 AND a.enabled = TRUE
            ON CONFLICT (installation_id) DO UPDATE SET
                agency_code = EXCLUDED.agency_code,
                machine_name = EXCLUDED.machine_name,
                credential_hash = EXCLUDED.credential_hash,
                agent_version = EXCLUDED.agent_version,
                backup_client_version = EXCLUDED.backup_client_version,
                backup_provider = EXCLUDED.backup_provider,
                nas_appliance_id = NULL,
                last_seen_at_utc = EXCLUDED.last_seen_at_utc;
            """;

        await using (NpgsqlCommand enrollCommand = new(enrollSql, connection, transaction))
        {
            enrollCommand.Parameters.AddWithValue(request.InstallationId);
            enrollCommand.Parameters.AddWithValue(agencyCode);
            enrollCommand.Parameters.AddWithValue(request.MachineName.Trim());
            enrollCommand.Parameters.AddWithValue(credentialHash);
            enrollCommand.Parameters.AddWithValue(request.AgentVersion.Trim());
            enrollCommand.Parameters.AddWithValue(
                (object?)request.BackupClientVersion?.Trim() ?? DBNull.Value);
            enrollCommand.Parameters.AddWithValue(now);
            await enrollCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        string attributionStatus = issuedBy is null
            ? "UNAVAILABLE"
            : issuanceSource == "SERVER_CONSOLE"
                ? "SYSTEM"
                : "CONFIRMED";
        const string enrollmentEventSql = """
            INSERT INTO device_enrollment_events
                (issuance_id, device_id, installation_id, agency_code,
                 machine_name, issued_by, issued_by_role, issued_from_ip,
                 issuance_source, installer_version, issued_at_utc,
                 token_expires_at_utc, enrolled_at_utc, enrolled_from_ip,
                 agent_version, backup_client_version, outcome,
                 attribution_status, is_reenrollment,
                 execution_identity_verified, issued_by_display_name_snapshot, agency_name_snapshot)
            VALUES
                ($1, $2, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11,
                 $12, $13, $14, $15, 'SUCCEEDED', $16, $17, FALSE, $18, $19);
            """;
        await using (NpgsqlCommand eventCommand = new(
            enrollmentEventSql,
            connection,
            transaction))
        {
            AddNullableParameter(eventCommand, NpgsqlDbType.Uuid, issuanceId);
            eventCommand.Parameters.AddWithValue(request.InstallationId);
            eventCommand.Parameters.AddWithValue(agencyCode);
            eventCommand.Parameters.AddWithValue(request.MachineName.Trim());
            AddNullableParameter(eventCommand, NpgsqlDbType.Varchar, issuedBy);
            AddNullableParameter(eventCommand, NpgsqlDbType.Varchar, issuedByRole);
            AddNullableParameter(eventCommand, NpgsqlDbType.Varchar, issuedFromIp);
            eventCommand.Parameters.AddWithValue(issuanceSource);
            AddNullableParameter(eventCommand, NpgsqlDbType.Varchar, installerVersion);
            AddNullableParameter(eventCommand, NpgsqlDbType.TimestampTz, issuedAtUtc);
            AddNullableParameter(eventCommand, NpgsqlDbType.TimestampTz, tokenExpiresAtUtc);
            eventCommand.Parameters.AddWithValue(now);
            eventCommand.Parameters.AddWithValue(NormalizeAddress(remoteAddress));
            eventCommand.Parameters.AddWithValue(request.AgentVersion.Trim());
            eventCommand.Parameters.AddWithValue(
                (object?)request.BackupClientVersion?.Trim() ?? DBNull.Value);
            eventCommand.Parameters.AddWithValue(attributionStatus);
            eventCommand.Parameters.AddWithValue(isReenrollment);
            AddNullableParameter(eventCommand, NpgsqlDbType.Text, issuedByDisplayName);
            AddNullableParameter(eventCommand, NpgsqlDbType.Text, agencyName);
            await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await auditRepository.WriteAuditAsync(
            now,
            "Agent",
            request.InstallationId.ToString("D"),
            "device.enrollment.succeeded",
            "Device",
            request.InstallationId.ToString("D"),
            new
            {
                issuanceId,
                agencyCode,
                machineName = request.MachineName.Trim(),
                issuedBy,
                issuedByRole,
                issuanceSource,
                installerVersion,
                enrolledFromIp = NormalizeAddress(remoteAddress),
                attributionStatus,
                isReenrollment,
                executionIdentityVerified = false
            },
            cancellationToken,
            transaction);

        await transaction.CommitAsync(cancellationToken);
        return new EnrollmentResponse(
            ApiContract.SchemaVersion,
            request.InstallationId,
            agencyCode,
            now);
    }

    private static string NormalizeIssuer(string? value)
    {
        string normalized = value?.Trim() ?? "unknown";
        return normalized.Length <= 255 ? normalized : normalized[..255];
    }

    private static string NormalizeAddress(string? value)
    {
        string normalized = value?.Trim() ?? "unknown";
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string NormalizeSource(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? "LEGACY";
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }

    private static string? NormalizeVersion(string? value)
    {
        string? normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    public async Task<bool> ValidateDeviceCredentialAsync(
        Guid deviceId,
        string deviceKey,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT credential_hash FROM devices WHERE id = $1 AND enabled = TRUE;";
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is not byte[] expectedHash)
        {
            _ = CredentialHasher.HashDeviceKey(deviceId, deviceKey);
            return false;
        }

        byte[] suppliedHash = CredentialHasher.HashDeviceKey(deviceId, deviceKey);
        return CredentialHasher.FixedTimeEquals(expectedHash, suppliedHash);
    }

    public async Task<bool> RecordHeartbeatAsync(
        Guid deviceId,
        AgentHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE devices SET
                last_seen_at_utc = $2,
                updater_heartbeat_received_at_utc = now(),
                agent_started_at_utc = $3,
                agent_version = $4,
                backup_client_version = $5,
                backup_running = $6,
                pending_events = $7,
                interactive_user_checked_at_utc =
                    COALESCE($8, interactive_user_checked_at_utc),
                interactive_user_account = CASE
                    WHEN $8 IS NULL THEN interactive_user_account
                    ELSE $9
                END,
                interactive_user_display_name = CASE
                    WHEN $8 IS NULL THEN interactive_user_display_name
                    ELSE $10
                END,
                interactive_user_sid = CASE
                    WHEN $8 IS NULL THEN interactive_user_sid
                    ELSE $11
                END,
                interactive_user_session_type = CASE
                    WHEN $8 IS NULL THEN interactive_user_session_type
                    ELSE $12
                END,
                interactive_user_resolution_source = CASE
                    WHEN $8 IS NULL THEN interactive_user_resolution_source
                    ELSE $13
                END,
                agent_delivery_health = COALESCE($14::jsonb,agent_delivery_health),
                agent_delivery_health_received_at_utc = CASE WHEN $14::jsonb IS NULL
                    THEN agent_delivery_health_received_at_utc ELSE CURRENT_TIMESTAMP END
            WHERE id = $1 AND enabled = TRUE;
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(heartbeat.SentAtUtc);
        command.Parameters.AddWithValue(heartbeat.AgentStartedAtUtc);
        command.Parameters.AddWithValue(heartbeat.AgentVersion);
        command.Parameters.AddWithValue((object?)heartbeat.BackupClientVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(heartbeat.BackupRunning);
        command.Parameters.AddWithValue(heartbeat.PendingEvents);
        AddNullableParameter(
            command,
            NpgsqlDbType.TimestampTz,
            heartbeat.InteractiveUserCheckedAtUtc);
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeObservedText(
                heartbeat.InteractiveUser?.AccountName,
                320));
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeObservedText(
                heartbeat.InteractiveUser?.DisplayName,
                256));
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeObservedText(
                heartbeat.InteractiveUser?.SecurityIdentifier,
                184));
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeObservedText(
                heartbeat.InteractiveUser?.SessionType,
                32));
        AddNullableParameter(
            command,
            NpgsqlDbType.Varchar,
            NormalizeObservedText(
                heartbeat.InteractiveUser?.ResolutionSource,
                32));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = heartbeat.DeliveryHealth is null ? DBNull.Value : JsonSerializer.Serialize(heartbeat.DeliveryHealth, JsonOptions) });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task RecordInventoryAsync(
        Guid deviceId,
        DeviceInventory inventory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (inventory.Software is not null) SoftwareRepository.ValidateInventory(inventory.Software, inventory.CapturedAtUtc, now);
        const string sql = """
            INSERT INTO inventory_snapshots
                (snapshot_id, device_id, captured_at_utc, payload)
            VALUES
                ($1, $2, $3, $4)
            ON CONFLICT (device_id, snapshot_id) DO NOTHING;
            """;

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deviceLock = new NpgsqlCommand("SELECT id FROM devices WHERE id=$1 FOR UPDATE", connection, transaction))
        {
            deviceLock.Parameters.AddWithValue(deviceId);
            await deviceLock.ExecuteScalarAsync(cancellationToken);
        }
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(inventory.SnapshotId);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(inventory.CapturedAtUtc);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(inventory, JsonOptions)
        });
        int recorded = await command.ExecuteNonQueryAsync(cancellationToken);
        if (recorded == 1 && inventory.Software is not null)
            await SoftwareRepository.Record(connection, transaction, deviceId, inventory, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordBackupRunAsync(
        Guid deviceId,
        BackupRunReport report,
        CancellationToken cancellationToken)
    {
        if (!BackupTelemetry.IsValid(report, timeProvider.GetUtcNow()))
            throw new ArgumentException("El reporte de copia no cumple el contrato de telemetría.");
        const string sql = """
            INSERT INTO backup_runs
                (event_id, device_id, started_at_utc, finished_at_utc, status, payload, received_at_utc, acceptable)
            VALUES
                ($1, $2, $3, $4, $5, $6, CURRENT_TIMESTAMP, $7)
            ON CONFLICT (device_id, event_id) DO UPDATE
            SET finished_at_utc=EXCLUDED.finished_at_utc, status=EXCLUDED.status,
                payload=EXCLUDED.payload, received_at_utc=CURRENT_TIMESTAMP, acceptable=EXCLUDED.acceptable
            WHERE backup_runs.status='Running'
              AND backup_runs.started_at_utc=EXCLUDED.started_at_utc
              AND EXCLUDED.finished_at_utc>=backup_runs.finished_at_utc
              AND COALESCE((EXCLUDED.payload->'evidence'->>'sequence')::bigint,0)>
                  COALESCE((backup_runs.payload->'evidence'->>'sequence')::bigint,0);
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(report.EventId);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(report.StartedAtUtc);
        command.Parameters.AddWithValue(report.FinishedAtUtc);
        command.Parameters.AddWithValue(report.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(report, JsonOptions)
        });
        command.Parameters.AddWithValue(BackupTelemetry.IsAcceptable(report));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateEnrollmentRequest(EnrollmentRequest request)
    {
        if (request.SchemaVersion != ApiContract.SchemaVersion ||
            request.InstallationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.EnrollmentToken) ||
            request.EnrollmentToken.Length > 256 ||
            string.IsNullOrWhiteSpace(request.DeviceKey) ||
            request.DeviceKey.Length < 32 ||
            request.DeviceKey.Length > 256 ||
            string.IsNullOrWhiteSpace(request.MachineName) ||
            request.MachineName.Length > 255 ||
            string.IsNullOrWhiteSpace(request.AgentVersion) ||
            request.AgentVersion.Length > 64 ||
            request.BackupClientVersion?.Length > 64)
        {
            throw new ArgumentException("La solicitud de inscripción no cumple el contrato vigente.");
        }
    }

    private static string? NormalizeObservedText(string? value, int maxLength)
    {
        string? normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static void AddNullableParameter(
        NpgsqlCommand command,
        NpgsqlDbType type,
        object? value)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value
        });
    }
}
