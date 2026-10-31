using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.NetworkDiscovery;

public sealed partial class NetworkDiscoveryRepository(NpgsqlDataSource dataSource, Microsoft.Extensions.Options.IOptions<ServerOptions>? options = null)
{
    public async Task<IReadOnlyList<NetworkScopeDefinition>> ReadEnabledScopesAsync(
        string? scopeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT code, agency_code, display_name, network::text,
                   host(gateway), scan_order,automatic_enabled,interval_minutes,identification_interval_hours,
                   excluded_addresses,next_scan_at_utc,configuration_revision
            FROM network_scopes
            WHERE enabled = TRUE
              AND ($1::text IS NULL OR code = $1)
            ORDER BY scan_order, code;
            """;
        List<NetworkScopeDefinition> scopes = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = string.IsNullOrWhiteSpace(scopeCode)
                ? DBNull.Value
                : scopeCode.Trim().ToUpperInvariant()
        });
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            scopes.Add(new NetworkScopeDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),reader.GetBoolean(6),reader.GetInt32(7),reader.GetInt32(8),reader.GetFieldValue<string[]>(9),
                reader.IsDBNull(10)?null:ReadUtc(reader,10),reader.GetInt32(11)));
        }
        return scopes;
    }

    public async Task<Guid> CreateRunAsync(
        string scopeCode,
        string requestedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO network_scan_runs
                (id, scope_code, requested_by, status, requested_at_utc,
                 addresses_targeted)
            VALUES ($1, $2, $3, 'QUEUED', $4, 254);
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(scopeCode);
        command.Parameters.AddWithValue(requestedBy);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task MarkRunStartedAsync(
        Guid runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE network_scan_runs
            SET status = 'RUNNING', started_at_utc = $2
            WHERE id = $1 AND status = 'QUEUED';
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRunFailedAsync(
        Guid runId,
        string status,
        string errorCode,
        string? detail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE network_scan_runs
            SET status = $2, completed_at_utc = $3,
                error_code = $4, error_detail = $5
            WHERE id = $1;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(errorCode);
        command.Parameters.AddWithValue((object?)Limit(detail, 512) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<NetworkDiscoveryDashboard> ReadDashboardAsync(
        bool enabled,
        bool automaticEnabled,
        bool engineAvailable,
        bool scanInProgress,
        DateTimeOffset? nextAutomaticScanAtUtc,
        string? scopeCode,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool icmpAvailable = false,string? presence=null,string? sort=null,bool descending=false,Guid? runId=null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        IReadOnlyList<NetworkScopeView> scopes = await ReadScopeViewsAsync(cancellationToken);
        NetworkAssetPage assets = await ReadAssetPageAsync(
            scopeCode, search, page, pageSize, cancellationToken,presence,sort,descending,runId);
        return new NetworkDiscoveryDashboard(
            enabled,
            automaticEnabled,
            engineAvailable,
            scanInProgress,
            "ICMP + TCP + correlación ATLAS",
            nextAutomaticScanAtUtc,
            scopes,
            assets,
            icmpAvailable,
            await ReadRunsAsync(scopeCode,cancellationToken));
    }

    public async Task<bool> UpdateAssetAsync(
        Guid assetId,
        string deviceKind,
        string? displayName,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE network_assets
            SET device_kind = $2,
                display_name = $3,
                classification_source = 'MANUAL',
                identification_confidence = 100,
                identification_basis = 'MANUAL',
                classified_at_utc = $4,
                classified_by = $5
            WHERE id = $1 AND retired_at_utc IS NULL;
            """;
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(assetId);
        command.Parameters.AddWithValue(deviceKind);
        command.Parameters.AddWithValue((object?)Limit(displayName?.Trim(), 255) ?? DBNull.Value);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actor);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<IReadOnlyList<NetworkScopeView>> ReadScopeViewsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.code, s.agency_code, s.display_name, s.network::text,
                   host(s.gateway),
                   (SELECT count(*)::int FROM network_assets a
                    WHERE a.scope_code = s.code AND a.retired_at_utc IS NULL AND a.consecutive_misses = 0
                      AND a.last_seen_at_utc>now()-make_interval(mins=>s.interval_minutes*2)
                      AND NOT(host(a.ip_address)=ANY(s.excluded_addresses))),
                   last.completed_at_utc, COALESCE(last.status, 'NEVER'),
                   last.hosts_observed, last.error_code,s.automatic_enabled,s.interval_minutes,
                   s.identification_interval_hours,s.excluded_addresses,s.next_scan_at_utc,s.configuration_revision,
                   (SELECT max(r.completed_at_utc) FROM network_scan_runs r WHERE r.scope_code=s.code AND r.discovery_complete
                      AND r.configuration_revision=s.configuration_revision AND r.addresses_targeted=
                        (power(2,32-masklen(s.network))-CASE WHEN masklen(s.network)<31 THEN 2 ELSE 0 END-cardinality(s.excluded_addresses)))
            FROM network_scopes s
            LEFT JOIN LATERAL
            (
                SELECT completed_at_utc, status, hosts_observed, error_code
                FROM network_scan_runs r
                WHERE r.scope_code = s.code
                ORDER BY r.requested_at_utc DESC
                LIMIT 1
            ) last ON TRUE
            WHERE s.enabled = TRUE
            ORDER BY s.scan_order, s.code;
            """;
        List<NetworkScopeView> scopes = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int observed = reader.GetInt32(5);
            var definition=new NetworkScopeDefinition(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),0,
                reader.GetBoolean(10),reader.GetInt32(11),reader.GetInt32(12),reader.GetFieldValue<string[]>(13));
            int targeted=NetworkTargets.Addresses(definition).Length;
            scopes.Add(new NetworkScopeView(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), targeted, observed,
                Math.Max(0, targeted - observed),
                reader.IsDBNull(6) ? null : ReadUtc(reader, 6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),reader.GetBoolean(10),reader.GetInt32(11),reader.GetInt32(12),reader.GetFieldValue<string[]>(13),
                reader.IsDBNull(14)?null:ReadUtc(reader,14),reader.IsDBNull(16)?null:ReadUtc(reader,16),reader.GetInt32(15)));
        }
        return scopes;
    }

    private async Task<NetworkAssetPage> ReadAssetPageAsync(
        string? scopeCode,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken,string? presence=null,string? sort=null,bool descending=false,Guid? runId=null)
    {
        string? normalizedScope = string.IsNullOrWhiteSpace(scopeCode)
            ? null : scopeCode.Trim().ToUpperInvariant();
        string? normalizedSearch = string.IsNullOrWhiteSpace(search)
            ? null : $"%{search.Trim()}%";
        string filter=presence?.ToUpperInvariant() is "CURRENT" or "MISSING" or "UNKNOWN"?presence.ToUpperInvariant():"ALL";
        string order=sort switch{"name"=>"COALESCE(d.machine_name,a.hostname,a.display_name,'')","vendor"=>"COALESCE(a.vendor,'')","latency"=>"a.response_time_ms","seen"=>"a.last_seen_at_utc","state"=>"a.consecutive_misses",_=>"a.ip_address"};
        const string countSql = """
            SELECT count(*)::int
            FROM network_assets a
            JOIN network_scopes s ON s.code = a.scope_code
            JOIN agencies ag ON ag.code = s.agency_code
            LEFT JOIN devices d ON d.id = a.linked_device_id
            WHERE a.retired_at_utc IS NULL AND ($1::text IS NULL OR a.scope_code = $1)
              AND ($4::uuid IS NULL OR a.last_scan_id=$4)
              AND ($3='ALL' OR $3='CURRENT' AND a.consecutive_misses=0 AND a.last_seen_at_utc>=now()-make_interval(mins=>GREATEST(10,s.interval_minutes)*2)
                   OR $3='MISSING' AND (a.consecutive_misses>0 OR a.last_seen_at_utc<now()-make_interval(mins=>GREATEST(10,s.interval_minutes)*2))
                   OR $3='UNKNOWN' AND (a.device_kind='UNKNOWN' OR a.identification_basis='AMBIGUOUS'))
              AND ($2::text IS NULL OR host(a.ip_address) ILIKE $2
                   OR COALESCE(a.hostname, '') ILIKE $2
                   OR COALESCE(a.display_name, '') ILIKE $2
                   OR COALESCE(a.vendor, '') ILIKE $2
                   OR COALESCE(a.mac_address, '') ILIKE $2
                   OR COALESCE(a.operating_system_hint, '') ILIKE $2
                   OR COALESCE(a.device_type_hint, '') ILIKE $2
                   OR COALESCE(a.services::text, '') ILIKE $2
                   OR COALESCE(d.machine_name, '') ILIKE $2
                   OR a.device_kind ILIKE $2 OR ag.display_name ILIKE $2);
            """;
        await using NpgsqlCommand count = dataSource.CreateCommand(countSql);
        AddFilterParameters(count, normalizedScope, normalizedSearch);
        count.Parameters.AddWithValue(filter);count.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)runId??DBNull.Value});
        int total = (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);

        const string pageSql = """
            SELECT a.id, a.scope_code, s.agency_code, ag.display_name,
                   host(a.ip_address), a.hostname, a.mac_address, a.vendor,
                   a.device_kind, a.display_name, a.first_seen_at_utc,
                   a.last_seen_at_utc, a.consecutive_misses,
                   a.linked_device_id, d.machine_name,
                   a.linked_nas_id, n.display_name, a.classification_source,
                   a.services::text, a.operating_system_hint,
                   a.device_type_hint, a.identification_confidence,
                   inventory.payload->'computer'->>'manufacturer',
                   inventory.payload->'computer'->>'model',
                   inventory.payload->'baseBoard'->>'manufacturer',
                   inventory.payload->'baseBoard'->>'product',a.presence_sources,a.identification_basis,
                   a.services_checked_at_utc,a.services_check_status,h.machine_name,
                   (a.linked_device_id IS NULL OR a.identification_basis='LEGACY') AND a.last_identified_device_id IS NOT NULL,
                   a.response_time_ms,a.hostname_source,a.mac_source,a.mac_checked_at_utc
            FROM network_assets a
            JOIN network_scopes s ON s.code = a.scope_code
            JOIN agencies ag ON ag.code = s.agency_code
            LEFT JOIN devices d ON d.id = a.linked_device_id
            LEFT JOIN nas_appliances n ON n.id = a.linked_nas_id
            LEFT JOIN devices h ON h.id=a.last_identified_device_id
            LEFT JOIN LATERAL
            (
                SELECT i.payload
                FROM inventory_snapshots i
                WHERE i.device_id = COALESCE(d.id,h.id)
                ORDER BY i.captured_at_utc DESC
                LIMIT 1
            ) inventory ON TRUE
            WHERE a.retired_at_utc IS NULL AND ($1::text IS NULL OR a.scope_code = $1)
              AND ($6::uuid IS NULL OR a.last_scan_id=$6)
              AND ($5='ALL' OR $5='CURRENT' AND a.consecutive_misses=0 AND a.last_seen_at_utc>=now()-make_interval(mins=>GREATEST(10,s.interval_minutes)*2)
                   OR $5='MISSING' AND (a.consecutive_misses>0 OR a.last_seen_at_utc<now()-make_interval(mins=>GREATEST(10,s.interval_minutes)*2))
                   OR $5='UNKNOWN' AND (a.device_kind='UNKNOWN' OR a.identification_basis='AMBIGUOUS'))
              AND ($2::text IS NULL OR host(a.ip_address) ILIKE $2
                   OR COALESCE(a.hostname, '') ILIKE $2
                   OR COALESCE(a.display_name, '') ILIKE $2
                   OR COALESCE(a.vendor, '') ILIKE $2
                   OR COALESCE(a.mac_address, '') ILIKE $2
                   OR COALESCE(a.operating_system_hint, '') ILIKE $2
                   OR COALESCE(a.device_type_hint, '') ILIKE $2
                   OR COALESCE(a.services::text, '') ILIKE $2
                   OR COALESCE(d.machine_name, '') ILIKE $2
                   OR a.device_kind ILIKE $2 OR ag.display_name ILIKE $2)
            ORDER BY __SCANNER_ORDER__
            LIMIT $3 OFFSET $4;
            """;
        List<NetworkAssetView> items = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(pageSql.Replace("__SCANNER_ORDER__",order+(descending?" DESC":" ASC")+" NULLS LAST,a.ip_address ASC,a.id ASC"));
        AddFilterParameters(command, normalizedScope, normalizedSearch);
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        command.Parameters.AddWithValue(filter);command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)runId??DBNull.Value});
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new NetworkAssetView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ReadUtc(reader, 10), ReadUtc(reader, 11), reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetGuid(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetString(17),
                DeserializeServices(reader.GetString(18)),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetInt32(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetString(25),reader.GetFieldValue<string[]>(26),reader.GetString(27),
                reader.IsDBNull(28)?null:ReadUtc(reader,28),reader.GetString(29),reader.IsDBNull(30)?null:reader.GetString(30),reader.GetBoolean(31),
                reader.IsDBNull(32)?null:reader.GetDouble(32),reader.IsDBNull(33)?null:reader.GetString(33),reader.IsDBNull(34)?null:reader.GetString(34),reader.IsDBNull(35)?null:ReadUtc(reader,35)));
        }
        return new NetworkAssetPage(page, pageSize, total, items);
    }

    private static async Task<(Guid? Id, string? Name)> FindDeviceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ipAddress,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.machine_name
            FROM devices d
            JOIN LATERAL
            (
                SELECT payload
                FROM inventory_snapshots i
                WHERE i.device_id = d.id
                ORDER BY captured_at_utc DESC
                LIMIT 1
            ) inventory ON TRUE
            WHERE d.enabled = TRUE
              AND EXISTS
              (
                  SELECT 1
                  FROM jsonb_array_elements(
                      COALESCE(inventory.payload->'networkInterfaces', '[]'::jsonb)) nic
                  CROSS JOIN LATERAL jsonb_array_elements_text(
                      COALESCE(nic->'ipv4Addresses', '[]'::jsonb)) address
                  WHERE address = $1
              )
            LIMIT 1;
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(ipAddress);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), reader.GetString(1))
            : (null, null);
    }

    private static async Task<(Guid? Id, string? Name)> FindNasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ipAddress,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, display_name
            FROM nas_appliances
            WHERE enabled = TRUE AND host = $1
            ORDER BY is_primary DESC
            LIMIT 1;
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(ipAddress);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), reader.GetString(1))
            : (null, null);
    }

    private static void AddFilterParameters(
        NpgsqlCommand command,
        string? scopeCode,
        string? search)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)scopeCode ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)search ?? DBNull.Value
        });
    }

    private static string? Limit(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal).ToUniversalTime();

    private static IReadOnlyList<NetworkServiceObservation> DeserializeServices(
        string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetworkServiceObservation[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static NetworkClassification Classify(
        NetworkScopeDefinition scope,
        NetworkObservation observation,
        Guid? deviceId,
        string? deviceName,
        Guid? nasId,
        string? nasName)
    {
        if (observation.IpAddress == scope.Gateway)
        {
            return new NetworkClassification("ROUTER", "Gateway " + scope.DisplayName, 100);
        }
        if (nasId.HasValue)
        {
            return new NetworkClassification("NAS", nasName, 100);
        }
        if (deviceId.HasValue)
        {
            return new NetworkClassification("ENDPOINT", deviceName, 100);
        }

        string evidence = string.Join(' ', observation.Services.SelectMany(service =>
        {
            return new[]
            {
                service.Name, service.Product, service.ExtraInfo,
                service.DeviceType, service.OperatingSystem,
                string.Join(' ', service.Cpes)
            };
        }).Append(observation.DeviceTypeHint)
          .Append(observation.OperatingSystemHint)
          .Where(value => !string.IsNullOrWhiteSpace(value)))
          .ToLowerInvariant();
        HashSet<int> ports = observation.Services.Select(service => service.Port).ToHashSet();
        string kind = evidence switch
        {
            _ when ContainsAny(evidence, "router", "mikrotik", "routeros") => "ROUTER",
            _ when ContainsAny(evidence, "wireless access point", "access point", "unifi", "wireless ap") => "ACCESS_POINT",
            _ when ContainsAny(evidence, "ethernet switch", "network switch") => "SWITCH",
            _ when ContainsAny(evidence, "printer", "jetdirect", "laserjet", "print server") => "PRINTER",
            _ when ContainsAny(evidence, "qnap", "synology", "network storage", "storage device", "nas ") => "NAS",
            _ when ContainsAny(evidence, "camera", "hikvision", "dahua", "webcam") => "CAMERA",
            _ when ContainsAny(evidence, "server") => "SERVER",
            _ when ports.Contains(8291) || ports.Contains(8728) || ports.Contains(8729) => "ROUTER",
            _ when ports.Contains(9100) || ports.Contains(515) => "PRINTER",
            _ when ports.Contains(554) => "CAMERA",
            _ => "UNKNOWN"
        };
        int confidence = kind == "UNKNOWN"
            ? observation.Hostname is null ? 20 : 35
            : 78;
        string? name = observation.Hostname ?? observation.Services
            .Select(service => service.Hostname ?? service.Product)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new NetworkClassification(kind, name, confidence);
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private sealed record NetworkClassification(
        string Kind,
        string? Name,
        int Confidence);
}
