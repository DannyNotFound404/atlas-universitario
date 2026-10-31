using System.Text.Json;
using Miguel.Contracts;
using Miguel.Server.Admin;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Options;

namespace Miguel.Server.Storage;

public sealed class SecurityRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IOptions<ServerOptions>? options = null)
{
    private int FreshnessHours => Math.Clamp(options?.Value.SecurityPostureFreshnessHours ?? 24, 1, 8760);
    public static string ObservationState(DateTimeOffset? captured, DateTimeOffset now, int freshnessHours = 24) =>
        captured is null ? "MISSING" : captured > now.AddMinutes(5) ? "FUTURE" :
        captured >= now.AddHours(-Math.Clamp(freshnessHours, 1, 8760)) ? "FRESH" : "STALE";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task RecordPostureAsync(
        Guid deviceId,
        SecurityPostureSnapshot posture,
        CancellationToken cancellationToken)
    {
        Validate(posture);
        const string sql = """
            INSERT INTO security_posture_snapshots
                (snapshot_id, device_id, captured_at_utc, payload)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (device_id, snapshot_id) DO NOTHING;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(posture.SnapshotId);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(posture.CapturedAtUtc);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(posture, JsonOptions)
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordEventsAsync(
        Guid deviceId,
        SecurityEventBatch batch,
        CancellationToken cancellationToken)
    {
        Validate(batch);
        const string sql = """
            INSERT INTO endpoint_security_events
                (batch_id, device_id, channel, record_id, event_code,
                 occurred_at_utc, category, outcome, logon_type, user_name,
                 user_domain, source_address, workstation_name, severity,
                 provider, target_name, correlation_key, payload)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13,
                 $14, $15, $16, $17, $18)
            ON CONFLICT (device_id, channel, record_id) DO NOTHING;
            """;

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (WindowsSecurityEvent item in batch.Events)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue(batch.BatchId);
            command.Parameters.AddWithValue(deviceId);
            command.Parameters.AddWithValue(item.Channel);
            command.Parameters.AddWithValue(item.RecordId);
            command.Parameters.AddWithValue(item.EventCode);
            command.Parameters.AddWithValue(item.OccurredAtUtc);
            command.Parameters.AddWithValue(item.Category);
            command.Parameters.AddWithValue(item.Outcome);
            command.Parameters.AddWithValue((object?)item.LogonType ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.UserName ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.UserDomain ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.SourceAddress ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.WorkstationName ?? DBNull.Value);
            command.Parameters.AddWithValue(NormalizeSeverity(item.Severity));
            command.Parameters.AddWithValue((object?)item.Provider ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.TargetName ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)item.CorrelationKey ?? DBNull.Value);
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Jsonb,
                Value = JsonSerializer.Serialize(item, JsonOptions)
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminSecurityDashboard> ReadDashboardAsync(
        int devicePage,
        int devicePageSize,
        string? deviceSort,
        string? deviceDirection,
        int eventPage,
        int eventPageSize,
        Guid? eventDeviceId,
        string? eventSort,
        string? eventDirection,
        CancellationToken cancellationToken)
    {
        devicePage = Math.Max(1, devicePage);
        devicePageSize = Math.Clamp(devicePageSize, 5, 50);
        eventPage = Math.Max(1, eventPage);
        eventPageSize = Math.Clamp(eventPageSize, 10, 100);
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<AdminSecurityDeviceView> allDevices =
            await ReadDevicePostureAsync(cancellationToken);
        allDevices = SortDevices(allDevices, deviceSort, deviceDirection);
        int deviceTotalPages = Math.Max(
            1,
            (int)Math.Ceiling(allDevices.Count / (double)devicePageSize));
        devicePage = Math.Min(devicePage, deviceTotalPages);
        IReadOnlyList<AdminSecurityDeviceView> devices = allDevices
            .Skip((devicePage - 1) * devicePageSize)
            .Take(devicePageSize)
            .ToArray();
        AdminSecurityEventPage events = await ReadRecentEventsAsync(
            eventPage,
            eventPageSize,
            eventDeviceId,
            eventSort,
            eventDirection,
            cancellationToken);
        IReadOnlyList<AdminSecurityDailyView> dailyEvents =
            await ReadDailyEventsAsync(cancellationToken);
        (int successfulRemote, int failedLogons, int criticalEvents, int highEvents) =
            await ReadEventCountsAsync(cancellationToken);
        var recent = allDevices.Where(item => item.ObservationState == "FRESH").ToArray();
        AdminSecuritySummary summary = new(
            allDevices.Count,
            recent.Length,
            recent.Count(item => item.FirewallEnabled == true),
            recent.Count(item => item.SystemDriveProtected == true),
            recent.Count(item => item.SecureBootEnabled == true),
            recent.Count(item => item.TpmPresent == true),
            recent.Count(item => item.Smb1Enabled == true),
            recent.Count(item => item.RemoteDesktopEnabled == true),
            recent.Count(item => item.PendingReboot == true),
            successfulRemote,
            failedLogons,
            criticalEvents,
            highEvents,
            allDevices.Count(item => item.CapturedAtUtc is not null),
            allDevices.Count(item => item.ObservationState is "STALE" or "FUTURE"),
            allDevices.Count(item => item.CapturedAtUtc is null), FreshnessHours);
        return new AdminSecurityDashboard(
            now,
            summary,
            new AdminSecurityDevicePage(
                devicePage,
                devicePageSize,
                allDevices.Count,
                devices),
            events,
            allDevices
                .OrderBy(item => item.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.AgencyCode, StringComparer.OrdinalIgnoreCase)
                .Select(item => new AdminSecurityDeviceOption(
                    item.DeviceId,
                    item.MachineName,
                    item.AgencyCode))
                .ToArray(),
            dailyEvents);
    }

    public async Task<AdminSecurityNotificationSignals> ReadNotificationSignalsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminSecurityDeviceView> devices =
            await ReadDevicePostureAsync(cancellationToken);
        AdminSecurityEventPage events = await ReadRecentEventsAsync(
            1,
            100,
            null,
            "date",
            "desc",
            cancellationToken);
        return new AdminSecurityNotificationSignals(devices, events.Items);
    }

    public async Task PruneAsync(
        int eventRetentionDays,
        int postureRetentionDays,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(393206)", connection, transaction)) await gate.ExecuteNonQueryAsync(cancellationToken);
        const string eventSql = """
            DELETE FROM endpoint_security_events e
            WHERE occurred_at_utc < NOW() - ($1::integer * INTERVAL '1 day')
            AND NOT EXISTS(SELECT 1 FROM atlas_evidence_holds h WHERE h.device_id=e.device_id AND h.released_at_utc IS NULL AND e.occurred_at_utc>=h.from_utc AND e.occurred_at_utc<h.to_utc);
            """;
        await using (NpgsqlCommand eventCommand = new(eventSql, connection, transaction))
        {
            eventCommand.Parameters.AddWithValue(eventRetentionDays);
            await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string postureSql = """
            DELETE FROM security_posture_snapshots s
            WHERE captured_at_utc < NOW() - ($1::integer * INTERVAL '1 day')
            AND NOT EXISTS(SELECT 1 FROM atlas_evidence_holds h WHERE h.device_id=s.device_id AND h.released_at_utc IS NULL AND s.captured_at_utc>=h.from_utc AND s.captured_at_utc<h.to_utc);
            """;
        await using NpgsqlCommand postureCommand = new(postureSql, connection, transaction);
        postureCommand.Parameters.AddWithValue(postureRetentionDays);
        await postureCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminSecurityDeviceView?> ReadDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        (await ReadDevicePostureAsync(cancellationToken, deviceId)).FirstOrDefault();

    internal static bool? FirewallState(IReadOnlyList<FirewallProfileInventory>? profiles) =>
        profiles is null || profiles.Count == 0 ? null :
        profiles.Any(p => p.Enabled == false) ? false :
        profiles.Any(p => p.Enabled is null) ? null : true;

    private async Task<IReadOnlyList<AdminSecurityDeviceView>> ReadDevicePostureAsync(
        CancellationToken cancellationToken, Guid? deviceId = null)
    {
        const string sql = """
            SELECT d.id, d.machine_name, d.agency_code,
                   p.captured_at_utc, p.payload::text
            FROM devices d
            LEFT JOIN LATERAL
            (
                SELECT captured_at_utc, payload
                FROM security_posture_snapshots s
                WHERE s.device_id = d.id
                ORDER BY captured_at_utc DESC
                LIMIT 1
            ) p ON TRUE
            WHERE d.enabled = TRUE AND ($1::uuid IS NULL OR d.id=$1)
            ORDER BY d.agency_code, d.machine_name;
            """;
        List<AdminSecurityDeviceView> result = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)deviceId ?? DBNull.Value });
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            SecurityPostureSnapshot? posture = reader.IsDBNull(4)
                ? null
                : JsonSerializer.Deserialize<SecurityPostureSnapshot>(
                    reader.GetString(4),
                    JsonOptions);
            bool? firewall = FirewallState(posture?.FirewallProfiles);
            bool? systemDrive = posture?.BitLockerVolumes?
                .FirstOrDefault(item => string.Equals(
                    item.DriveLetter,
                    "C:",
                    StringComparison.OrdinalIgnoreCase))?
                .ProtectionEnabled;
            DateTimeOffset? capturedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            result.Add(new AdminSecurityDeviceView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                capturedAt,
                firewall,
                systemDrive,
                posture?.SecureBootEnabled,
                posture?.Tpm?.Present,
                posture?.Smb1Enabled,
                posture?.RemoteDesktopEnabled,
                posture?.PendingReboot,
                posture?.SecurityEventLogReadable,
                posture?.InstalledSoftware?.Count ?? 0,
                posture?.InstalledUpdates?.Count ?? 0,
                posture?.CollectionWarnings ?? [],
                ObservationState(capturedAt, timeProvider.GetUtcNow(), FreshnessHours), FreshnessHours,
                capturedAt.HasValue ? (timeProvider.GetUtcNow() - capturedAt.Value).TotalHours : null));
        }
        return result;
    }

    private async Task<AdminSecurityEventPage> ReadRecentEventsAsync(
        int page,
        int pageSize,
        Guid? deviceId,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        string deviceFilter = deviceId.HasValue
            ? " AND e.device_id = $1"
            : "";
        string countSql = """
            SELECT COUNT(*)
            FROM endpoint_security_events e
            INNER JOIN devices d ON d.id = e.device_id
            WHERE d.enabled = TRUE
              AND e.occurred_at_utc >= NOW() - INTERVAL '7 days'
            """ + deviceFilter + ";";
        await using NpgsqlCommand countCommand =
            dataSource.CreateCommand(countSql);
        if (deviceId.HasValue)
        {
            countCommand.Parameters.AddWithValue(deviceId.Value);
        }
        long count = (long)(await countCommand.ExecuteScalarAsync(
            cancellationToken) ?? 0L);
        int total = checked((int)Math.Min(count, int.MaxValue));
        int totalPages = Math.Max(
            1,
            (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);

        string orderColumn = NormalizeEventSort(sort) switch
        {
            "machine" => "d.machine_name",
            "severity" => "CASE e.severity WHEN 'Critical' THEN 5 WHEN 'High' THEN 4 WHEN 'Medium' THEN 3 WHEN 'Low' THEN 2 ELSE 1 END",
            "category" => "e.category",
            "outcome" => "e.outcome",
            _ => "e.occurred_at_utc"
        };
        bool descending = string.IsNullOrWhiteSpace(direction)
            ? NormalizeEventSort(sort) == "date"
            : IsDescending(direction);
        string orderDirection = descending ? "DESC" : "ASC";
        string limitParameters = deviceId.HasValue
            ? "LIMIT $2 OFFSET $3"
            : "LIMIT $1 OFFSET $2";
        string dataSql = """
            SELECT e.id, e.device_id, d.machine_name, d.agency_code,
                   e.occurred_at_utc, e.event_code, e.category, e.outcome,
                   e.logon_type, e.user_name, e.user_domain,
                   e.source_address, e.workstation_name, e.severity,
                   e.provider, e.target_name, e.correlation_key, e.payload::text
            FROM endpoint_security_events e
            INNER JOIN devices d ON d.id = e.device_id
            WHERE d.enabled = TRUE
              AND e.occurred_at_utc >= NOW() - INTERVAL '7 days'
            """ + deviceFilter + $" ORDER BY {orderColumn} {orderDirection}, e.occurred_at_utc DESC, e.id DESC {limitParameters};";
        List<AdminSecurityEventView> result = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(dataSql);
        if (deviceId.HasValue)
        {
            command.Parameters.AddWithValue(deviceId.Value);
        }
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string category = reader.GetString(6);
            WindowsSecurityEvent? payload = JsonSerializer.Deserialize<WindowsSecurityEvent>(
                reader.GetString(17),
                JsonOptions);
            result.Add(new AdminSecurityEventView(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                category,
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                category is SecurityEventCategories.RemoteInteractiveLogon or
                    SecurityEventCategories.RemoteAuthentication or
                    SecurityEventCategories.RemoteSessionLogon or
                    SecurityEventCategories.RemoteSessionLogoff or
                    SecurityEventCategories.RemoteSessionDisconnected or
                    SecurityEventCategories.RemoteSessionReconnected,
                reader.GetString(13),
                reader.IsDBNull(14) ? payload?.Provider : reader.GetString(14),
                payload?.SubjectUserName,
                payload?.SubjectUserDomain,
                reader.IsDBNull(15) ? payload?.TargetName : reader.GetString(15),
                payload?.ProcessName,
                reader.IsDBNull(16) ? payload?.CorrelationKey : reader.GetString(16),
                payload?.Details,
                payload?.AuthenticationPackage,
                payload?.SessionName,
                payload?.FailureReason));
        }
        return new AdminSecurityEventPage(page, pageSize, total, result);
    }

    private async Task<IReadOnlyList<AdminSecurityDailyView>> ReadDailyEventsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH days AS
            (
                SELECT generate_series(
                    CURRENT_DATE - INTERVAL '6 days',
                    CURRENT_DATE,
                    INTERVAL '1 day')::date AS day
            ), totals AS
            (
                SELECT e.occurred_at_utc::date AS day,
                       COUNT(*) AS total_events,
                       COUNT(*) FILTER (WHERE e.severity = 'Critical') AS critical_events,
                       COUNT(*) FILTER (WHERE e.severity = 'High') AS high_events,
                       COUNT(*) FILTER (WHERE e.outcome = 'Failure') AS failed_events
                FROM endpoint_security_events e
                INNER JOIN devices d ON d.id = e.device_id
                WHERE d.enabled = TRUE
                  AND e.occurred_at_utc >= CURRENT_DATE - INTERVAL '6 days'
                GROUP BY e.occurred_at_utc::date
            )
            SELECT days.day,
                   COALESCE(totals.total_events, 0),
                   COALESCE(totals.critical_events, 0),
                   COALESCE(totals.high_events, 0),
                   COALESCE(totals.failed_events, 0)
            FROM days
            LEFT JOIN totals ON totals.day = days.day
            ORDER BY days.day;
            """;
        List<AdminSecurityDailyView> result = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminSecurityDailyView(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)),
                checked((int)reader.GetInt64(3)),
                checked((int)reader.GetInt64(4))));
        }
        return result;
    }

    private static IReadOnlyList<AdminSecurityDeviceView> SortDevices(
        IReadOnlyList<AdminSecurityDeviceView> devices,
        string? sort,
        string? direction)
    {
        string normalized = (sort ?? "machine").Trim().ToLowerInvariant();
        bool descending = IsDescending(direction);
        IOrderedEnumerable<AdminSecurityDeviceView> ordered = normalized switch
        {
            "agency" => descending
                ? devices.OrderByDescending(item => item.AgencyCode, StringComparer.OrdinalIgnoreCase)
                : devices.OrderBy(item => item.AgencyCode, StringComparer.OrdinalIgnoreCase),
            "captured" => descending
                ? devices.OrderByDescending(item => item.CapturedAtUtc)
                : devices.OrderBy(item => item.CapturedAtUtc is null).ThenBy(item => item.CapturedAtUtc),
            "attention" => descending
                ? devices.OrderByDescending(SecurityAttentionScore)
                : devices.OrderBy(SecurityAttentionScore),
            _ => descending
                ? devices.OrderByDescending(item => item.MachineName, StringComparer.OrdinalIgnoreCase)
                : devices.OrderBy(item => item.MachineName, StringComparer.OrdinalIgnoreCase)
        };
        return ordered
            .ThenBy(item => item.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AgencyCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SecurityAttentionScore(AdminSecurityDeviceView item)
    {
        bool?[] expectedTrue =
        [
            item.FirewallEnabled,
            item.SystemDriveProtected,
            item.SecureBootEnabled,
            item.TpmPresent
        ];
        int score = expectedTrue.Count(value => value != true);
        score += item.Smb1Enabled != false ? 1 : 0;
        score += item.RemoteDesktopEnabled != false ? 1 : 0;
        score += item.PendingReboot != false ? 1 : 0;
        return score;
    }

    private static string NormalizeEventSort(string? sort) =>
        (sort ?? "date").Trim().ToLowerInvariant() switch
        {
            "machine" => "machine",
            "severity" => "severity",
            "category" => "category",
            "outcome" => "outcome",
            _ => "date"
        };

    private static bool IsDescending(string? direction) =>
        string.Equals(direction?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

    private async Task<(int SuccessfulRemote, int FailedLogons, int CriticalEvents, int HighEvents)> ReadEventCountsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COUNT(*) FILTER
                (
                    WHERE outcome = 'Success'
                      AND category IN
                      (
                          'RemoteInteractiveLogon',
                          'RemoteAuthentication',
                          'RemoteSessionLogon',
                          'RemoteSessionReconnected'
                      )
                ),
                COUNT(*) FILTER
                (
                    WHERE outcome = 'Failure'
                      AND category IN
                      (
                          'InteractiveLogon',
                          'RemoteInteractiveLogon',
                          'NetworkLogon',
                          'BatchLogon',
                          'ServiceLogon',
                          'NetworkCleartextLogon',
                          'NewCredentialsLogon'
                      )
                ),
                COUNT(*) FILTER (WHERE severity = 'Critical'),
                COUNT(*) FILTER (WHERE severity = 'High')
            FROM endpoint_security_events
            WHERE occurred_at_utc >= NOW() - INTERVAL '24 hours';
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            checked((int)reader.GetInt64(3)));
    }

    private void Validate(SecurityPostureSnapshot posture)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (posture.SchemaVersion != ApiContract.SchemaVersion ||
            posture.SnapshotId == Guid.Empty ||
            posture.CapturedAtUtc < now.AddDays(-30) ||
            posture.CapturedAtUtc > now.AddMinutes(10) ||
            posture.FirewallProfiles.Count > 16 ||
            posture.BitLockerVolumes.Count > 128 ||
            posture.InstalledSoftware.Count > 4096 ||
            posture.InstalledUpdates.Count > 4096 ||
            posture.CollectionWarnings.Count > 64 ||
            posture.InstalledSoftware.Any(item =>
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256) ||
            posture.CollectionWarnings.Any(item => item.Length > 128))
        {
            throw new ArgumentException("La postura de seguridad no cumple el contrato vigente.");
        }
    }

    private void Validate(SecurityEventBatch batch)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (batch.SchemaVersion != ApiContract.SchemaVersion ||
            batch.BatchId == Guid.Empty ||
            batch.Events.Count is < 1 or > 500 ||
            batch.CapturedAtUtc < now.AddDays(-30) ||
            batch.CapturedAtUtc > now.AddMinutes(10) ||
            batch.Events.Any(item =>
                item.RecordId <= 0 ||
                item.EventCode <= 0 ||
                item.Channel.Length is < 1 or > 192 ||
                !SecurityEventCategories.IsKnown(item.Category) ||
                !SecurityEventOutcomes.IsKnown(item.Outcome) ||
                !SecurityEventSeverities.IsKnown(NormalizeSeverity(item.Severity)) ||
                item.OccurredAtUtc < now.AddDays(-30) ||
                item.OccurredAtUtc > now.AddMinutes(10) ||
                item.UserName?.Length > 256 ||
                item.UserDomain?.Length > 256 ||
                item.SourceAddress?.Length > 64 ||
                item.WorkstationName?.Length > 256 ||
                item.Provider?.Length > 192 ||
                item.SubjectUserName?.Length > 256 ||
                item.SubjectUserDomain?.Length > 256 ||
                item.TargetName?.Length > 512 ||
                item.ProcessName?.Length > 512 ||
                item.CorrelationKey?.Length > 128 ||
                item.Details?.Length > 1024 ||
                item.SessionId?.Length > 128))
        {
            throw new ArgumentException("El lote de eventos no cumple el contrato vigente.");
        }
    }

    private static string NormalizeSeverity(string? severity) =>
        string.IsNullOrWhiteSpace(severity)
            ? SecurityEventSeverities.Information
            : severity;
}
