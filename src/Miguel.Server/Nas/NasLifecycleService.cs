using System.Security.Cryptography;
using System.Text;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Nas;

public sealed record NasLifecycleRequest(string Scope, bool Enabled, string ExpectedVersion, string Reason);
public sealed record NasLifecycleResource(Guid Id, string Name, string Share, bool Enabled, long Revision,
    DateTimeOffset? RetiredAtUtc, string? RetiredBy, string? RetirementReason);
public sealed record NasLifecycleDependency(string Kind, Guid Id, string Name, string AgencyCode);
public sealed record NasLifecyclePreview(string Scope, string Host, int Port, string AgencyCode, string Version,
    IReadOnlyList<NasLifecycleResource> Resources, IReadOnlyList<NasLifecycleDependency> Dependencies);
public sealed class NasLifecycleConflict(string code) : Exception(code);

public sealed class NasLifecycleService(NpgsqlDataSource db, TimeProvider clock)
{
    // Serializes changes to a physical NAS's resource set, including create/edit.
    internal const long CatalogLock = 937030;

    public async Task<NasLifecyclePreview?> Preview(Guid id, string scope, CancellationToken ct)
    {
        ValidateScope(scope);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        return await Read(connection, tx, id, scope, false, ct);
    }

    public async Task<int> Change(Guid id, NasLifecycleRequest request, string actor, string role, string remote, CancellationToken ct)
    {
        ValidateScope(request.Scope);
        string reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 3 or > 1000 || reason.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            request.ExpectedVersion?.Length != 64) throw new ArgumentException("invalid_nas_lifecycle_request");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var catalog = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({CatalogLock})", connection, tx))
            await catalog.ExecuteNonQueryAsync(ct);
        var before = await Read(connection, tx, id, request.Scope, true, ct)
            ?? throw new NasLifecycleConflict("nas_not_found");
        if (before.Version != request.ExpectedVersion) throw new NasLifecycleConflict("nas_lifecycle_changed");
        var changed = before.Resources.Where(r => r.Enabled != request.Enabled).ToArray();
        if (changed.Length == 0) return 0;
        if (!request.Enabled && before.Dependencies.Count != 0) throw new NasLifecycleConflict("nas_has_active_dependencies");
        if (request.Enabled)
        {
            await using var agency = new NpgsqlCommand("SELECT enabled FROM agencies WHERE code=$1 FOR SHARE", connection, tx);
            agency.Parameters.AddWithValue(before.AgencyCode);
            if (await agency.ExecuteScalarAsync(ct) is not true) throw new NasLifecycleConflict("nas_agency_inactive");
        }
        var now = clock.GetUtcNow();
        foreach (var resource in changed)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE nas_appliances SET enabled=$2,updated_at_utc=$3,updated_by=$4,
                    retired_at_utc=CASE WHEN $2 THEN NULL ELSE $3 END,
                    retired_by=CASE WHEN $2 THEN NULL ELSE $4 END,
                    retirement_reason=CASE WHEN $2 THEN NULL ELSE $5 END,
                    last_probe_at_utc=CASE WHEN $2 THEN NULL ELSE last_probe_at_utc END,
                    last_probe_status=CASE WHEN $2 THEN 'UNKNOWN' ELSE last_probe_status END,
                    last_probe_latency_ms=CASE WHEN $2 THEN NULL ELSE last_probe_latency_ms END,
                    total_bytes=CASE WHEN $2 THEN NULL ELSE total_bytes END,
                    free_bytes=CASE WHEN $2 THEN NULL ELSE free_bytes END,
                    last_probe_error=CASE WHEN $2 THEN NULL ELSE last_probe_error END
                WHERE id=$1
                """, connection, tx);
            update.Parameters.AddWithValue(resource.Id); update.Parameters.AddWithValue(request.Enabled);
            update.Parameters.AddWithValue(now); update.Parameters.AddWithValue(actor); update.Parameters.AddWithValue(reason);
            await update.ExecuteNonQueryAsync(ct);
        }
        await new AdminRepository(db).WriteAuditAsync(now, role, actor,
            request.Enabled ? "admin.nas.reactivated" : "admin.nas.retired", "NasAppliance", id.ToString(),
            new { scope = request.Scope, reason, host = before.Host, agencyCode = before.AgencyCode,
                before = changed, after = new { enabled = request.Enabled, resourceIds = changed.Select(r => r.Id) }, remoteAddress = remote }, ct, tx);
        await tx.CommitAsync(ct);
        return changed.Length;
    }

    private static void ValidateScope(string scope)
    {
        if (scope is not ("NAS" or "RESOURCE")) throw new ArgumentException("invalid_nas_scope");
    }

    private static async Task<NasLifecyclePreview?> Read(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid id, string scope, bool locked, CancellationToken ct)
    {
        string host, agency; int port;
        await using (var find = new NpgsqlCommand("SELECT host,smb_port,agency_code FROM nas_appliances WHERE id=$1", connection, tx))
        {
            find.Parameters.AddWithValue(id);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            host = reader.GetString(0); port = reader.GetInt32(1); agency = reader.GetString(2);
        }
        List<NasLifecycleResource> resources = [];
        await using (var select = new NpgsqlCommand("""
            SELECT id,display_name,share_name,enabled,lifecycle_revision,retired_at_utc,retired_by,retirement_reason
            FROM nas_appliances WHERE ($1='RESOURCE' AND id=$2) OR
                ($1='NAS' AND lower(host)=lower($3) AND smb_port=$4 AND agency_code=$5)
            ORDER BY id
            """ + (locked ? " FOR UPDATE" : ""), connection, tx))
        {
            select.Parameters.AddWithValue(scope); select.Parameters.AddWithValue(id); select.Parameters.AddWithValue(host);
            select.Parameters.AddWithValue(port); select.Parameters.AddWithValue(agency);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) resources.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        List<NasLifecycleDependency> dependencies = [];
        await using (var select = new NpgsqlCommand("""
            SELECT 'DEVICE',id,machine_name,agency_code FROM devices WHERE enabled AND nas_appliance_id=ANY($1)
            UNION ALL SELECT 'CONTROL',p.id,p.system_name,p.agency_code FROM backup_control_profiles p
              WHERE p.enabled AND p.nas_appliance_id=ANY($1)
                AND (p.device_id IS NULL OR EXISTS(SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled))
            ORDER BY 1,3,2
            """, connection, tx))
        {
            select.Parameters.AddWithValue(resources.Select(r => r.Id).ToArray());
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) dependencies.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        }
        string version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', resources.Select(r => $"{r.Id:D}:{r.Revision}")))));
        return new(scope, host, port, agency, version, resources, dependencies);
    }
}
