using Npgsql;

namespace Miguel.Server.Distribution;

public sealed class ReleaseDeploymentRepository(NpgsqlDataSource dataSource)
{
    public async Task<Guid?> ResolveReleaseIdAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM release_deployments paused
                WHERE paused.paused=TRUE AND
                    ((paused.scope_type='DEVICE' AND paused.scope_value=device.id::text) OR
                     (paused.scope_type='RING' AND paused.scope_value=device.update_ring_code) OR
                     (paused.scope_type='AGENCY' AND paused.scope_value=device.agency_code))
                ) THEN '00000000-0000-0000-0000-000000000000'::uuid ELSE deployment.release_id END
            FROM devices device
            JOIN release_deployments deployment
              ON
             (
                 (deployment.scope_type = 'DEVICE'
                  AND deployment.scope_value = device.id::text)
                 OR
                 (deployment.scope_type = 'RING'
                  AND deployment.scope_value = device.update_ring_code)
                 OR
                 (deployment.scope_type = 'AGENCY'
                  AND deployment.scope_value = device.agency_code)
             )
            WHERE device.id = $1
              AND device.enabled = TRUE
            ORDER BY CASE deployment.scope_type
                WHEN 'DEVICE' THEN 1
                WHEN 'RING' THEN 2
                WHEN 'AGENCY' THEN 3
                ELSE 4
            END
            LIMIT 1;
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid releaseId ? releaseId : null;
    }

    public async Task<AtlasReleaseDeploymentStoreView> ReadAsync(
        CancellationToken cancellationToken)
    {
        const string deploymentsSql = """
            SELECT
                deployment.scope_type,
                deployment.scope_value,
                deployment.release_id,
                deployment.paused,
                CASE deployment.scope_type
                    WHEN 'DEVICE' THEN
                        (SELECT count(*)::integer
                         FROM devices
                         WHERE id::text = deployment.scope_value
                           AND enabled = TRUE)
                    WHEN 'RING' THEN
                        (SELECT count(*)::integer
                         FROM devices
                         WHERE update_ring_code = deployment.scope_value
                           AND enabled = TRUE)
                    WHEN 'AGENCY' THEN
                        (SELECT count(*)::integer
                         FROM devices
                         WHERE agency_code = deployment.scope_value
                           AND enabled = TRUE)
                    ELSE 0
                END AS target_devices,
                deployment.updated_at_utc,
                deployment.updated_by
            FROM release_deployments deployment
            ORDER BY
                CASE deployment.scope_type
                    WHEN 'DEVICE' THEN 1
                    WHEN 'RING' THEN 2
                    WHEN 'AGENCY' THEN 3
                    ELSE 4
                END,
                deployment.scope_value;
            """;
        List<AtlasReleaseDeploymentView> deployments = [];
        await using (NpgsqlCommand command =
                     dataSource.CreateCommand(deploymentsSql))
        await using (NpgsqlDataReader reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                deployments.Add(new AtlasReleaseDeploymentView(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.GetBoolean(3),
                    reader.GetInt32(4),
                    ReadUtc(reader, 5),
                    reader.GetString(6)));
            }
        }

        const string ringsSql = """
            SELECT ring_code, count(device.id)::integer
            FROM (VALUES ('PILOT'), ('GENERAL')) AS rings(ring_code)
            LEFT JOIN devices device
              ON device.update_ring_code = rings.ring_code
             AND device.enabled = TRUE
            GROUP BY ring_code
            ORDER BY CASE ring_code WHEN 'PILOT' THEN 1 ELSE 2 END;
            """;
        List<AtlasUpdateRingView> rings = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(ringsSql))
        await using (NpgsqlDataReader reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rings.Add(new AtlasUpdateRingView(
                    reader.GetString(0),
                    reader.GetInt32(1)));
            }
        }

        return new AtlasReleaseDeploymentStoreView(deployments, rings);
    }

    public async Task<AtlasReleaseDeploymentView> UpsertAsync(
        Guid releaseId,
        string scopeType,
        string scopeValue,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string normalizedType, string normalizedValue) =
            ReleaseDeploymentScopes.Normalize(scopeType, scopeValue);
        if (!await TargetExistsAsync(
                normalizedType,
                normalizedValue,
                cancellationToken))
        {
            throw new ArgumentException(
                "El equipo o la agencia seleccionada no existe o esta deshabilitada.");
        }
        const string sql = """
            INSERT INTO release_deployments
                (scope_type, scope_value, release_id, paused,
                 updated_at_utc, updated_by)
            VALUES ($1, $2, $3, FALSE, $4, $5)
            ON CONFLICT (scope_type, scope_value) DO UPDATE SET
                release_id = EXCLUDED.release_id,
                paused = FALSE,
                updated_at_utc = EXCLUDED.updated_at_utc,
                updated_by = EXCLUDED.updated_by;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(normalizedType);
        command.Parameters.AddWithValue(normalizedValue);
        command.Parameters.AddWithValue(releaseId);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        await command.ExecuteNonQueryAsync(cancellationToken);

        AtlasReleaseDeploymentStoreView store = await ReadAsync(cancellationToken);
        return store.Deployments.Single(deployment =>
            deployment.ScopeType == normalizedType &&
            deployment.ScopeValue == normalizedValue);
    }

    public async Task<bool> SetPausedAsync(
        string scopeType,
        string scopeValue,
        bool paused,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string normalizedType, string normalizedValue) =
            ReleaseDeploymentScopes.Normalize(scopeType, scopeValue);
        const string sql = """
            UPDATE release_deployments
            SET paused = $3,
                updated_at_utc = $4,
                updated_by = $5
            WHERE scope_type = $1 AND scope_value = $2;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(normalizedType);
        command.Parameters.AddWithValue(normalizedValue);
        command.Parameters.AddWithValue(paused);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(
        string scopeType,
        string scopeValue,
        CancellationToken cancellationToken)
    {
        (string normalizedType, string normalizedValue) =
            ReleaseDeploymentScopes.Normalize(scopeType, scopeValue);
        const string sql = """
            DELETE FROM release_deployments
            WHERE scope_type = $1 AND scope_value = $2;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(normalizedType);
        command.Parameters.AddWithValue(normalizedValue);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SetDeviceRingAsync(
        Guid deviceId,
        string ringCode,
        CancellationToken cancellationToken)
    {
        string normalized = UpdateRings.Normalize(ringCode);
        const string sql = """
            UPDATE devices
            SET update_ring_code = $2
            WHERE id = $1 AND enabled = TRUE;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(normalized);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private async Task<bool> TargetExistsAsync(
        string scopeType,
        string scopeValue,
        CancellationToken cancellationToken)
    {
        if (scopeType == ReleaseDeploymentScopes.Ring)
        {
            return true;
        }

        string sql = scopeType == ReleaseDeploymentScopes.Device
            ? "SELECT EXISTS(SELECT 1 FROM devices WHERE id::text = $1 AND enabled = TRUE);"
            : "SELECT EXISTS(SELECT 1 FROM agencies WHERE code = $1 AND enabled = TRUE);";
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(scopeValue);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }
}
