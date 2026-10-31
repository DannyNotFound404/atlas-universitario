using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.NetworkDiscovery;

public sealed partial class NetworkDiscoveryRepository
{
    public async Task<QueueNetworkScanResponse> QueueDurableAsync(string? scopeCode,string actor,DateTimeOffset now,bool dueOnly,CancellationToken ct,
        string? startAddress=null,string? endAddress=null,bool forceIdentification=false)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        var selected=new List<NetworkScopeDefinition>();
        await using(var select=new NpgsqlCommand("""
            SELECT code,agency_code,display_name,network::text,host(gateway),scan_order,automatic_enabled,
                interval_minutes,identification_interval_hours,excluded_addresses,next_scan_at_utc,configuration_revision
            FROM network_scopes WHERE enabled AND ($1::text IS NULL OR code=$1)
                AND (NOT $3 OR automatic_enabled AND (next_scan_at_utc IS NULL OR next_scan_at_utc<=$2))
            ORDER BY scan_order,code FOR UPDATE SKIP LOCKED
            """,connection,tx))
        {
            select.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)scopeCode?.Trim().ToUpperInvariant()??DBNull.Value});
            select.Parameters.AddWithValue(now.ToUniversalTime());select.Parameters.AddWithValue(dueOnly);
            await using var reader=await select.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))selected.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt32(5),reader.GetBoolean(6),reader.GetInt32(7),reader.GetInt32(8),reader.GetFieldValue<string[]>(9),reader.IsDBNull(10)?null:ReadUtc(reader,10),reader.GetInt32(11)));
        }
        var ids=new List<Guid>();
        if((!string.IsNullOrWhiteSpace(startAddress)||!string.IsNullOrWhiteSpace(endAddress))&&selected.Count!=1)
            throw new ArgumentException("network_range_requires_scope");
        foreach(var scope in selected)
        {
            string[] targets=NetworkTargets.Range(scope,startAddress,endAddress);
            await using var insert=new NpgsqlCommand("""
                INSERT INTO network_scan_runs(id,scope_code,requested_by,status,requested_at_utc,addresses_targeted,configuration_revision,target_addresses,force_identification)
                VALUES($1,$2,$3,'QUEUED',$4,$5,$6,$7,$8) ON CONFLICT(scope_code) WHERE status IN ('QUEUED','RUNNING') DO NOTHING RETURNING id
                """,connection,tx);
            insert.Parameters.AddWithValue(Guid.NewGuid());insert.Parameters.AddWithValue(scope.Code);insert.Parameters.AddWithValue(actor);
            insert.Parameters.AddWithValue(now.ToUniversalTime());insert.Parameters.AddWithValue(targets.Length);insert.Parameters.AddWithValue(scope.ConfigurationRevision);
            insert.Parameters.AddWithValue(targets);insert.Parameters.AddWithValue(forceIdentification);
            if(await insert.ExecuteScalarAsync(ct) is Guid id)
            {
                ids.Add(id);
                if(targets.Length==NetworkTargets.Addresses(scope).Length)
                {
                    await using var schedule=new NpgsqlCommand("UPDATE network_scopes SET next_scan_at_utc=$2 WHERE code=$1",connection,tx);
                    schedule.Parameters.AddWithValue(scope.Code);schedule.Parameters.AddWithValue(now.AddMinutes(scope.IntervalMinutes).ToUniversalTime());await schedule.ExecuteNonQueryAsync(ct);
                }
            }
        }
        await tx.CommitAsync(ct);return new(ids.Count,ids,selected.Count-ids.Count);
    }

    public async Task RecoverJobsAsync(DateTimeOffset now,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            UPDATE network_scan_runs SET status=CASE WHEN attempt_count<2 THEN 'QUEUED' ELSE 'INTERRUPTED' END,
                lease_owner=NULL,lease_expires_at_utc=NULL,error_code='worker_interrupted',
                completed_at_utc=CASE WHEN attempt_count<2 THEN NULL ELSE $1 END,
                retry_after_at_utc=$1
            WHERE status='RUNNING' AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc<$1)
            """);command.Parameters.AddWithValue(now.ToUniversalTime());await command.ExecuteNonQueryAsync(ct);
        await using var cancel=dataSource.CreateCommand("""
            UPDATE network_scan_runs r SET status='CANCELLED',completed_at_utc=$1,error_code='configuration_changed'
            FROM network_scopes s WHERE r.scope_code=s.code AND r.status='QUEUED'
                AND (NOT s.enabled OR r.configuration_revision<>s.configuration_revision);
            """);cancel.Parameters.AddWithValue(now.ToUniversalTime());await cancel.ExecuteNonQueryAsync(ct);
    }

    public async Task<NetworkScanClaim?> ClaimAsync(Guid owner,DateTimeOffset now,CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        Guid? id=null;string? code=null;string[] targets=[];bool forceIdentification=false;
        await using(var claim=new NpgsqlCommand("""
            WITH candidate AS (SELECT r.id FROM network_scan_runs r JOIN network_scopes s ON s.code=r.scope_code
                WHERE r.status='QUEUED' AND s.enabled AND r.configuration_revision=s.configuration_revision
                    AND (r.retry_after_at_utc IS NULL OR r.retry_after_at_utc<=$2)
                ORDER BY r.requested_at_utc,r.id FOR UPDATE OF r SKIP LOCKED LIMIT 1)
            UPDATE network_scan_runs r SET status='RUNNING',lease_owner=$1,lease_expires_at_utc=$2+interval '2 minutes',
                attempt_count=attempt_count+1,started_at_utc=$2,completed_at_utc=NULL,progress='{}'::jsonb,progress_at_utc=NULL
            FROM candidate WHERE r.id=candidate.id RETURNING r.id,r.scope_code,r.target_addresses,r.force_identification
            """,connection,tx))
        {
            claim.Parameters.AddWithValue(owner);claim.Parameters.AddWithValue(now.ToUniversalTime());
            await using var reader=await claim.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){id=reader.GetGuid(0);code=reader.GetString(1);targets=reader.GetFieldValue<string[]>(2);forceIdentification=reader.GetBoolean(3);}
        }
        await tx.CommitAsync(ct);if(id is null)return null;
        var scope=(await ReadEnabledScopesAsync(code,ct)).SingleOrDefault();return scope is null?null:new(scope with{ScanTargets=targets.Length==0?null:targets,ForceIdentification=forceIdentification},id.Value,owner);
    }
    public async Task<bool> MaintainClaimAsync(NetworkScanClaim claim,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            UPDATE network_scan_runs r SET lease_expires_at_utc=$3+interval '2 minutes'
            FROM network_scopes s WHERE r.id=$1 AND r.lease_owner=$2 AND r.status='RUNNING' AND s.code=r.scope_code
                AND s.enabled AND s.configuration_revision=r.configuration_revision AND r.configuration_revision=$4
            """);command.Parameters.AddWithValue(claim.RunId);command.Parameters.AddWithValue(claim.Owner);command.Parameters.AddWithValue(now.ToUniversalTime());command.Parameters.AddWithValue(claim.Scope.ConfigurationRevision);
        return await command.ExecuteNonQueryAsync(ct)==1;
    }
    public async Task FailClaimAsync(NetworkScanClaim claim,string status,string error,DateTimeOffset now,CancellationToken ct)
    {
        now=now.ToUniversalTime();
        await using var connection=await dataSource.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand("""
            UPDATE network_scan_runs SET status=CASE WHEN attempt_count<2 AND $3 IN ('FAILED','TIMED_OUT') THEN 'QUEUED' ELSE $3 END,
                retry_after_at_utc=$5+interval '1 minute',completed_at_utc=CASE WHEN attempt_count<2 AND $3 IN ('FAILED','TIMED_OUT') THEN NULL ELSE $5 END,
                lease_owner=NULL,lease_expires_at_utc=NULL,error_code=$4
            WHERE id=$1 AND lease_owner=$2 AND status='RUNNING' RETURNING status
            """,connection,tx);command.Parameters.AddWithValue(claim.RunId);command.Parameters.AddWithValue(claim.Owner);command.Parameters.AddWithValue(status);command.Parameters.AddWithValue(error);command.Parameters.AddWithValue(now);
        if(await command.ExecuteScalarAsync(ct) is string saved)
        {
            await AuditAsync(connection,tx,"system:network-worker",saved=="QUEUED"?"admin.network.scan.retry_queued":saved=="INTERRUPTED"?"admin.network.scan.interrupted":"admin.network.scan.failed",claim.Scope.Code,new{runId=claim.RunId,status=saved,error},now,ct);
            if(saved!="QUEUED")await AlertAsync(connection,tx,$"network-coverage:{claim.Scope.Code}:{NotificationDay(now)}","WARNING","Revisar ejecución del sondeo",$"{claim.Scope.DisplayName}: el sondeo no terminó. Se conserva el inventario anterior; revise el historial de redes.",claim.Scope.Code,now,ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<bool> CancelRunAsync(Guid id,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("UPDATE network_scan_runs SET status='CANCELLED',completed_at_utc=$2,error_code='cancelled_by_operator',lease_expires_at_utc=NULL WHERE id=$1 AND status IN ('QUEUED','RUNNING')");
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(now.ToUniversalTime());return await command.ExecuteNonQueryAsync(ct)==1;
    }
    public async Task<bool> HasActiveJobsAsync(CancellationToken ct)
    {await using var command=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM network_scan_runs WHERE status IN ('QUEUED','RUNNING'))");return (bool)(await command.ExecuteScalarAsync(ct))!;}
    public async Task<DateTimeOffset?> ReadNextAutomaticAsync(CancellationToken ct)
    {await using var command=dataSource.CreateCommand("SELECT min(COALESCE(next_scan_at_utc,now())) FROM network_scopes WHERE enabled AND automatic_enabled");return await command.ExecuteScalarAsync(ct) is DateTime time?new DateTimeOffset(time,TimeSpan.Zero):null;}

    public async Task<bool> SaveScopeAsync(string code,NetworkScopeSettingsRequest request,string actor,DateTimeOffset now,CancellationToken ct)
    {
        if(request.IntervalMinutes is <5 or >10080 || request.IdentificationIntervalHours is <1 or >168)throw new ArgumentException("invalid_network_schedule");
        var scope=(await ReadEnabledScopesAsync(code,ct)).SingleOrDefault();if(scope is null)return false;
        string[] exclusions=NetworkTargets.NormalizeExclusions(scope,request.ExcludedAddresses);
        await using var connection=await dataSource.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand("""
            UPDATE network_scopes SET automatic_enabled=$2,interval_minutes=$3,identification_interval_hours=$4,
                excluded_addresses=$5,next_scan_at_utc=$6,configuration_revision=configuration_revision+1,updated_at_utc=$6,updated_by=$7
            WHERE code=$1 AND configuration_revision=$8
            """,connection,tx);
        command.Parameters.AddWithValue(scope.Code);command.Parameters.AddWithValue(request.AutomaticEnabled);command.Parameters.AddWithValue(request.IntervalMinutes);command.Parameters.AddWithValue(request.IdentificationIntervalHours);command.Parameters.AddWithValue(exclusions);command.Parameters.AddWithValue(now.ToUniversalTime());command.Parameters.AddWithValue(actor);command.Parameters.AddWithValue(request.ExpectedRevision);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("network_revision_conflict");
        await using var cancel=new NpgsqlCommand("UPDATE network_scan_runs SET status='CANCELLED',completed_at_utc=$2,error_code='configuration_changed' WHERE scope_code=$1 AND status IN ('QUEUED','RUNNING')",connection,tx);
        cancel.Parameters.AddWithValue(scope.Code);cancel.Parameters.AddWithValue(now.ToUniversalTime());await cancel.ExecuteNonQueryAsync(ct);
        await AuditAsync(connection,tx,actor,"admin.network.scope.configured",scope.Code,new{request.AutomaticEnabled,request.IntervalMinutes,request.IdentificationIntervalHours,exclusions},now,ct);
        await tx.CommitAsync(ct);return true;
    }
    public async Task<IReadOnlySet<string>> IdentifyTargetsAsync(NetworkScopeDefinition scope,DateTimeOffset now,CancellationToken ct)
    {
        var targets=NetworkTargets.Addresses(scope).ToHashSet(StringComparer.Ordinal);
        if(scope.ForceIdentification)return targets;
        await using var command=dataSource.CreateCommand("SELECT host(ip_address) FROM network_assets WHERE scope_code=$1 AND retired_at_utc IS NULL AND services_check_status='COMPLETE' AND services_checked_at_utc>=$2");
        command.Parameters.AddWithValue(scope.Code);command.Parameters.AddWithValue(now.AddHours(-scope.IdentificationIntervalHours).ToUniversalTime());
        await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))targets.Remove(reader.GetString(0));return targets;
    }
    public async Task<IReadOnlyList<NetworkScanRunView>> ReadRunsAsync(string? code,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            SELECT id,scope_code,status,requested_by,requested_at_utc,started_at_utc,completed_at_utc,addresses_targeted,
                hosts_observed,discovery_complete,identification_status,warnings::text,methods,error_code
            FROM network_scan_runs WHERE ($1::text IS NULL OR scope_code=$1) ORDER BY requested_at_utc DESC,id DESC LIMIT 50
            """);command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)code?.Trim().ToUpperInvariant()??DBNull.Value});
        var result=new List<NetworkScanRunView>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),ReadUtc(reader,4),reader.IsDBNull(5)?null:ReadUtc(reader,5),reader.IsDBNull(6)?null:ReadUtc(reader,6),reader.GetInt32(7),reader.IsDBNull(8)?null:reader.GetInt32(8),reader.GetBoolean(9),reader.GetString(10),JsonSerializer.Deserialize<NetworkScanWarning[]>(reader.GetString(11))??[],reader.GetFieldValue<string[]>(12),reader.IsDBNull(13)?null:reader.GetString(13)));
        return result;
    }
    public async Task<IReadOnlyList<NetworkAssetEventView>> ReadEventsAsync(Guid id,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT id,event_type,occurred_at_utc,evidence::text FROM network_asset_events WHERE asset_id=$1 ORDER BY occurred_at_utc DESC,id DESC LIMIT 100");command.Parameters.AddWithValue(id);
        var result=new List<NetworkAssetEventView>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetString(1),ReadUtc(reader,2),JsonSerializer.Deserialize<JsonElement>(reader.GetString(3))));return result;
    }
    private static async Task AuditAsync(NpgsqlConnection connection,NpgsqlTransaction tx,string actor,string action,string target,object detail,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("INSERT INTO audit_events(occurred_at_utc,actor_type,actor_id,action,target_type,target_id,detail) VALUES($1,$2,$3,$4,'NetworkScope',$5,$6::jsonb)",connection,tx);
        command.Parameters.AddWithValue(now.ToUniversalTime());command.Parameters.AddWithValue(actor.StartsWith("system:")?"System":"Administrator");command.Parameters.AddWithValue(actor);command.Parameters.AddWithValue(action);command.Parameters.AddWithValue(target);command.Parameters.AddWithValue(JsonSerializer.Serialize(detail));await command.ExecuteNonQueryAsync(ct);
    }
}
