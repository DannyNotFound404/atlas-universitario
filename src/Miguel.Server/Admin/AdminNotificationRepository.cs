using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Admin;

internal sealed record AdminNotificationCandidate(
    string Fingerprint,
    string Category,
    string Severity,
    string Title,
    string Message,
    string TargetView,
    string? TargetEntityId,
    DateTimeOffset DetectedAtUtc);

public sealed class AdminNotificationRepository(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IOptions<ServerOptions> options,
    Miguel.Server.Configuration.OperationalSettingsService? settings = null)
{
    internal async Task UpsertAsync(
        IReadOnlyList<AdminNotificationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int retentionDays = settings is null ? options.Value.AdminNotificationRetentionDays : (await settings.RetentionAsync(cancellationToken)).Value.NotificationsDays;
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        const string upsertSql = """
            INSERT INTO admin_notifications
                (id, fingerprint, category, severity, title, message,
                 target_view, target_entity_id, detected_at_utc, last_seen_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (fingerprint) DO UPDATE
            SET category = EXCLUDED.category,
                severity = EXCLUDED.severity,
                title = EXCLUDED.title,
                message = EXCLUDED.message,
                target_view = EXCLUDED.target_view,
                target_entity_id = EXCLUDED.target_entity_id,
                last_seen_at_utc = EXCLUDED.last_seen_at_utc;
            """;
        foreach (AdminNotificationCandidate candidate in candidates)
        {
            await using NpgsqlCommand command =
                new(upsertSql, connection, transaction);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(candidate.Fingerprint);
            command.Parameters.AddWithValue(candidate.Category);
            command.Parameters.AddWithValue(candidate.Severity);
            command.Parameters.AddWithValue(candidate.Title);
            command.Parameters.AddWithValue(candidate.Message);
            command.Parameters.AddWithValue(candidate.TargetView);
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Varchar,
                Value = (object?)candidate.TargetEntityId ?? DBNull.Value
            });
            command.Parameters.AddWithValue(candidate.DetectedAtUtc);
            command.Parameters.AddWithValue(now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string pruneSql = """
            DELETE FROM admin_notifications
            WHERE detected_at_utc < $1;
            """;
        await using (NpgsqlCommand prune = new(pruneSql, connection, transaction))
        {
            prune.Parameters.AddWithValue(
                now.AddDays(-retentionDays));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminNotificationPage> ReadAsync(
        string username,
        int page,
        int pageSize,
        bool unreadOnly,
        CancellationToken cancellationToken)
    {
        username = NormalizeUsername(username);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 50);
        string unreadFilter = unreadOnly
            ? " AND r.notification_id IS NULL"
            : string.Empty;

        const string unreadSql = """
            SELECT COUNT(*)
            FROM atlas_visible_notifications n
            LEFT JOIN admin_notification_reads r
              ON r.notification_id = n.id AND r.username = $1
            WHERE r.notification_id IS NULL;
            """;
        await using NpgsqlCommand unreadCommand = dataSource.CreateCommand(unreadSql);
        unreadCommand.Parameters.AddWithValue(username);
        int unreadCount = checked((int)(long)(
            await unreadCommand.ExecuteScalarAsync(cancellationToken) ?? 0L));

        string countSql = """
            SELECT COUNT(*)
            FROM atlas_visible_notifications n
            LEFT JOIN admin_notification_reads r
              ON r.notification_id = n.id AND r.username = $1
            WHERE TRUE
            """ + unreadFilter + ";";
        await using NpgsqlCommand countCommand = dataSource.CreateCommand(countSql);
        countCommand.Parameters.AddWithValue(username);
        int total = checked((int)(long)(
            await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L));
        int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);

        string dataSql = """
            SELECT n.id, n.category, n.severity, n.title, n.message,
                   n.target_view, n.target_entity_id, n.detected_at_utc,
                   n.last_seen_at_utc, r.read_at_utc
            FROM atlas_visible_notifications n
            LEFT JOIN admin_notification_reads r
              ON r.notification_id = n.id AND r.username = $1
            WHERE TRUE
            """ + unreadFilter + "\n" + """
            ORDER BY (r.notification_id IS NULL) DESC,
                     n.detected_at_utc DESC,
                     CASE n.severity
                       WHEN 'CRITICAL' THEN 3
                       WHEN 'WARNING' THEN 2
                       ELSE 1
                     END DESC,
                     n.id DESC
            LIMIT $2 OFFSET $3;
            """;
        List<AdminNotificationView> items = [];
        await using NpgsqlCommand dataCommand = dataSource.CreateCommand(dataSql);
        dataCommand.Parameters.AddWithValue(username);
        dataCommand.Parameters.AddWithValue(pageSize);
        dataCommand.Parameters.AddWithValue((page - 1) * pageSize);
        await using NpgsqlDataReader reader =
            await dataCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? readAt = reader.IsDBNull(9)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(9);
            items.Add(new AdminNotificationView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                readAt.HasValue,
                readAt));
        }

        await reader.DisposeAsync();
        List<string> muted = [];
        await using (var muteCommand = dataSource.CreateCommand("SELECT DISTINCT category FROM atlas_notification_mutes WHERE released_at_utc IS NULL AND until_utc>CURRENT_TIMESTAMP ORDER BY category"))
        await using (var muteReader = await muteCommand.ExecuteReaderAsync(cancellationToken))
            while(await muteReader.ReadAsync(cancellationToken)) muted.Add(muteReader.GetString(0));
        return new AdminNotificationPage(
            timeProvider.GetUtcNow(),
            page,
            pageSize,
            total,
            unreadCount,
            items, muted);
    }

    public async Task MarkReadAsync(
        Guid notificationId,
        string username,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_notification_reads
                (notification_id, username, read_at_utc)
            SELECT id, $2, $3
            FROM admin_notifications
            WHERE id = $1
            ON CONFLICT (notification_id, username) DO UPDATE
            SET read_at_utc = EXCLUDED.read_at_utc;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(notificationId);
        command.Parameters.AddWithValue(NormalizeUsername(username));
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(
        string username,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_notification_reads
                (notification_id, username, read_at_utc)
            SELECT id, $1, $2
            FROM admin_notifications
            ON CONFLICT (notification_id, username) DO UPDATE
            SET read_at_utc = EXCLUDED.read_at_utc;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(NormalizeUsername(username));
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeUsername(string username) =>
        username.Trim().ToLowerInvariant();
}
