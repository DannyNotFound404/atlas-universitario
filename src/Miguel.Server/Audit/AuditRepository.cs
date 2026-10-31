using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Audit;

public sealed class AuditRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider)
{
    private const string ModuleExpression = """
        CASE
          WHEN e.action LIKE 'admin.login%' OR e.action ILIKE '%logout%'
               OR e.action ILIKE '%account%' OR e.action LIKE 'admin.mfa.%' THEN 'ACCESS'
          WHEN e.action LIKE 'admin.report.%' THEN 'REPORTS'
          WHEN e.action LIKE 'admin.configuration.%' OR e.action LIKE 'admin.agent_policy.%' OR e.action LIKE 'admin.evidence.hold.%' OR e.action LIKE 'admin.notification.%' THEN 'CONFIGURATION'
          WHEN e.action ILIKE '%installer%' OR e.action ILIKE '%enrollment%'
               THEN 'ENROLLMENT'
          WHEN e.action LIKE 'admin.audit.%' THEN 'AUDIT'
          WHEN e.action ILIKE '%software%' THEN 'SOFTWARE'
          WHEN e.action ILIKE '%backup_control%' THEN 'BACKUPS'
          WHEN e.action ILIKE '%release%' THEN 'RELEASES'
          WHEN e.action ILIKE '%governance%' THEN 'GOVERNANCE'
          WHEN e.action ILIKE '%training%' THEN 'TRAINING'
          WHEN e.action ILIKE '%case%' THEN 'CASES'
          WHEN e.action ILIKE '%network%' THEN 'NETWORK'
          WHEN e.action ILIKE '%nas%' THEN 'NAS'
          WHEN e.action ILIKE '%agency%' THEN 'AGENCIES'
          WHEN e.action ILIKE '%device%' OR e.action ILIKE '%inventory%'
               THEN 'ASSETS'
          ELSE 'OTHER'
        END
        """;

    private static readonly string OutcomeExpression = AuditTaxonomy.OutcomeSql("e");

    public async Task<AuditOverview> ReadOverviewAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM audit_events),
                (SELECT COUNT(*) FROM audit_events
                 WHERE occurred_at_utc >= $1),
                (SELECT COUNT(*) FROM audit_events
                 WHERE occurred_at_utc >= $1
                   AND action = 'admin.login.failed'),
                (SELECT COUNT(DISTINCT actor_id) FROM audit_events
                 WHERE occurred_at_utc >= $2),
                (SELECT COUNT(*) FROM device_enrollment_events),
                (SELECT COUNT(*) FROM device_enrollment_events
                 WHERE attribution_status = 'CONFIRMED'),
                (SELECT COUNT(*) FROM device_enrollment_events
                 WHERE attribution_status = 'SYSTEM'),
                (SELECT COUNT(*) FROM device_enrollment_events
                 WHERE attribution_status = 'UNAVAILABLE'),
                (SELECT COUNT(*) FROM device_enrollment_events
                 WHERE is_reenrollment = TRUE),
                (SELECT COUNT(*) FROM enrollment_tokens
                 WHERE expires_at_utc > $3);
            """;

        long[] values = new long[10];
        await using (NpgsqlCommand command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue(now.AddHours(-24));
            command.Parameters.AddWithValue(now.AddDays(-30));
            command.Parameters.AddWithValue(now);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = reader.GetInt64(index);
            }
        }

        const string actorSql = """
            SELECT DISTINCT e.actor_id,
                   COALESCE(e.actor_display_name_snapshot, e.actor_id) AS actor_label
            FROM audit_events e
            WHERE e.actor_id <> ''
            ORDER BY actor_label, e.actor_id
            LIMIT 250;
            """;
        List<AuditFilterOption> actors = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(actorSql))
        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                actors.Add(new AuditFilterOption(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }

        return new AuditOverview(
            now, values[0], values[1], values[2], values[3], values[4],
            values[5], values[6], values[7], values[8], values[9], actors);
    }

    public async Task<AuditEventPage> ReadEventsAsync(
        AuditEventQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        DateTimeOffset? since = query.FromUtc ?? ResolveSince(query.Period);
        string where = $"""
            WHERE ($1::timestamptz IS NULL OR e.occurred_at_utc >= $1)
              AND ($2 = '' OR e.actor_id = $2)
              AND ($3 = '' OR {ModuleExpression} = $3)
              AND ($4 = '' OR {OutcomeExpression} = $4)
              AND ($5 = '' OR e.actor_id ILIKE $6 OR e.action ILIKE $6
                   OR COALESCE(e.target_type, '') ILIKE $6
                   OR COALESCE(e.target_id, '') ILIKE $6
                   OR COALESCE(e.actor_display_name_snapshot, '') ILIKE $6
                   OR COALESCE(e.target_display_name_snapshot, '') ILIKE $6)
              AND ($9::timestamptz IS NULL OR e.occurred_at_utc < $9)
              AND ($10 = '' OR e.target_id = $10)
            """;
        string countSql = $"""
            SELECT COUNT(*)
            FROM audit_events e
            {where} AND $7::int > 0 AND $8::bigint >= 0;
            """;

        long total;
        await using (NpgsqlCommand count = new NpgsqlCommand(countSql, connection, tx))
        {
            AddEventParameters(count, query, since);
            total = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        string pageSql = $"""
            SELECT e.id, e.occurred_at_utc, e.actor_type, e.actor_id,
                   COALESCE(e.actor_display_name_snapshot, e.actor_id), e.action,
                   e.target_type, e.target_id, e.detail::text,
                   e.target_display_name_snapshot, e.context_captured, {OutcomeExpression}
            FROM audit_events e
            {where}
            ORDER BY e.occurred_at_utc DESC, e.id DESC
            LIMIT $7 OFFSET $8;
            """;
        List<AuditEventView> items = [];
        await using (NpgsqlCommand command = new NpgsqlCommand(pageSql, connection, tx))
        {
            AddEventParameters(command, query, since);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadEvent(reader));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new AuditEventPage(query.Page, query.PageSize, total, items);
    }

    public async Task<AuditEventView?> ReadEventAsync(
        long id,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT e.id, e.occurred_at_utc, e.actor_type, e.actor_id,
                   COALESCE(e.actor_display_name_snapshot, e.actor_id), e.action,
                   e.target_type, e.target_id, e.detail::text,
                   e.target_display_name_snapshot, e.context_captured, {OutcomeExpression}
            FROM audit_events e
            WHERE e.id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadEvent(reader)
            : null;
    }

    public async Task<AuditEnrollmentPage> ReadEnrollmentsAsync(
        AuditEnrollmentQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        DateTimeOffset? since = query.FromUtc ?? ResolveSince(query.Period);
        const string where = """
            WHERE ($1::timestamptz IS NULL OR e.enrolled_at_utc >= $1)
              AND ($2 = '' OR e.agency_code = $2)
              AND ($3 = '' OR e.attribution_status = $3)
              AND ($4 = '' OR e.machine_name ILIKE $5
                   OR e.agency_code ILIKE $5
                   OR COALESCE(e.agency_name_snapshot, '') ILIKE $5
                   OR COALESCE(e.issued_by, '') ILIKE $5
                   OR COALESCE(e.issued_by_display_name_snapshot, '') ILIKE $5)
              AND ($8::timestamptz IS NULL OR e.enrolled_at_utc < $8)
            """;
        string countSql = $"""
            SELECT COUNT(*)
            FROM device_enrollment_events e
            {where} AND $6::int > 0 AND $7::bigint >= 0;
            """;
        long total;
        await using (NpgsqlCommand count = new NpgsqlCommand(countSql, connection, tx))
        {
            AddEnrollmentParameters(count, query, since);
            total = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        string pageSql = $"""
            SELECT e.id, e.issuance_id, e.device_id, e.machine_name,
                   e.agency_code, COALESCE(e.agency_name_snapshot, e.agency_code),
                   e.issued_by, COALESCE(e.issued_by_display_name_snapshot, e.issued_by, 'Emisor no registrado'),
                   e.issued_by_role, e.issuance_source, e.issued_from_ip,
                   e.enrolled_from_ip, e.installer_version, e.issued_at_utc,
                   e.enrolled_at_utc, e.agent_version, e.backup_client_version,
                   e.attribution_status, e.is_reenrollment,
                   e.execution_identity_verified, d.interactive_user_account,
                   d.interactive_user_display_name,
                   d.interactive_user_checked_at_utc, e.issued_by_display_name_snapshot IS NOT NULL
            FROM device_enrollment_events e
            LEFT JOIN devices d ON d.id = e.device_id
            {where}
            ORDER BY e.enrolled_at_utc DESC, e.id DESC
            LIMIT $6 OFFSET $7;
            """;
        List<AuditEnrollmentView> items = [];
        await using (NpgsqlCommand command = new NpgsqlCommand(pageSql, connection, tx))
        {
            AddEnrollmentParameters(command, query, since);
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadEnrollment(reader));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new AuditEnrollmentPage(query.Page, query.PageSize, total, items);
    }

    private DateTimeOffset? ResolveSince(string period)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return period.ToUpperInvariant() switch
        {
            "24H" => now.AddHours(-24),
            "7D" => now.AddDays(-7),
            "90D" => now.AddDays(-90),
            "365D" => now.AddDays(-365),
            "ALL" => null,
            _ => now.AddDays(-30)
        };
    }

    private static void AddEventParameters(
        NpgsqlCommand command,
        AuditEventQuery query,
        DateTimeOffset? since)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = (object?)since ?? DBNull.Value
        });
        command.Parameters.AddWithValue(query.Actor);
        command.Parameters.AddWithValue(query.Module);
        command.Parameters.AddWithValue(query.Outcome);
        command.Parameters.AddWithValue(query.Search);
        command.Parameters.AddWithValue($"%{query.Search}%");
        command.Parameters.AddWithValue(query.PageSize);
        command.Parameters.AddWithValue((long)(query.Page - 1) * query.PageSize);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)query.ToUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(query.Target);
    }

    private static void AddEnrollmentParameters(
        NpgsqlCommand command,
        AuditEnrollmentQuery query,
        DateTimeOffset? since)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = (object?)since ?? DBNull.Value
        });
        command.Parameters.AddWithValue(query.Agency);
        command.Parameters.AddWithValue(query.Attribution);
        command.Parameters.AddWithValue(query.Search);
        command.Parameters.AddWithValue($"%{query.Search}%");
        command.Parameters.AddWithValue(query.PageSize);
        command.Parameters.AddWithValue((long)(query.Page - 1) * query.PageSize);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)query.ToUtc ?? DBNull.Value);
    }

    private static AuditEventView ReadEvent(NpgsqlDataReader reader)
    {
        string action = reader.GetString(5);
        string module = AuditTaxonomy.GetModule(action);
        return new AuditEventView(
            reader.GetInt64(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            action,
            AuditTaxonomy.GetActionLabel(action),
            module,
            AuditTaxonomy.GetModuleLabel(module),
            reader.GetString(11),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            AuditRedactor.RedactJson(
                reader.IsDBNull(8) ? null : reader.GetString(8)),
            reader.IsDBNull(9) ? null : AuditRedactor.RedactText(reader.GetString(9)), reader.GetBoolean(10));
    }

    private static AuditEnrollmentView ReadEnrollment(
        NpgsqlDataReader reader) => new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetString(17),
            reader.GetBoolean(18),
            reader.GetBoolean(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22), reader.GetBoolean(23));
}
