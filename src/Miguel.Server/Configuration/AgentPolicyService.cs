using System.Data;
using Miguel.Contracts;
using Miguel.Server.Reports;
using Npgsql;

namespace Miguel.Server.Configuration;

public sealed record AgentPolicyChange(string Scope,string Target,long ExpectedRevision,AgentPolicyValues Values,string Reason);
public sealed class AgentPolicyService(NpgsqlDataSource db,ConfigurationService configuration)
{
    private static void Validate(AgentPolicyChange v)
    {
        if(v.Scope is not("INSTITUTION" or "AGENCY" or "DEVICE") || v.Target is null || v.Target.Length>64 || v.ExpectedRevision<0 || !AgentPolicyContract.Valid(v.Values))throw new PortalException("Revise el alcance y los intervalos permitidos.");
        if(v.Scope=="INSTITUTION"&&v.Target!="*" || v.Scope=="DEVICE"&&!Guid.TryParse(v.Target,out _))throw new PortalException("Destino de política no válido.");
        ConfigurationService.Text(v.Reason,500,"el motivo");
    }
    public async Task<object> ImpactAsync(AgentPolicyChange v,CancellationToken ct)
    {
        Validate(v);
        var devices=await configuration.QueryAsync("""
            SELECT d.id,d.machine_name,d.agency_code,s.protocol,s.applied_revision,
            EXISTS(SELECT 1 FROM atlas_agent_policies p WHERE p.scope='DEVICE' AND p.target=d.id::text) AS individual_override,
            NOT EXISTS(SELECT 1 FROM atlas_agent_policies p WHERE ($1<>'DEVICE' AND p.scope='DEVICE' AND p.target=d.id::text) OR ($1='INSTITUTION' AND p.scope='AGENCY' AND p.target=d.agency_code)) AS affected
            FROM devices d LEFT JOIN atlas_agent_policy_status s ON s.device_id=d.id WHERE d.enabled AND d.retired_at_utc IS NULL AND
            ($1='INSTITUTION' OR ($1='AGENCY' AND d.agency_code=$2) OR ($1='DEVICE' AND d.id::text=$2)) ORDER BY d.machine_name,d.id
            """,ct,v.Scope,v.Target);
        if(v.Scope!="INSTITUTION"&&devices.Count==0)throw new PortalException("No hay equipos activos en ese alcance.");
        return new{scope=v.Scope,target=v.Target,matched=devices.Count,affected=devices.Count(x=>x["affected"] is true),compatible=devices.Count(x=>x["protocol"] is not null && x["affected"] is true),devices,
            precedence="Equipo > agencia > institución. Cada política define todos los intervalos; las excepciones individuales prevalecen.",values=v.Values};
    }
    public async Task<long> SaveAsync(AgentPolicyChange v,string actor,CancellationToken ct)
    {
        Validate(v);await ImpactAsync(v,ct);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393205)",c,t))await gate.ExecuteNonQueryAsync(ct);
        await using(var prior=new NpgsqlCommand("SELECT COALESCE(MAX(revision),0) FROM atlas_agent_policies WHERE scope=$1 AND target=$2",c,t))
        {prior.Parameters.AddWithValue(v.Scope);prior.Parameters.AddWithValue(v.Target);if((long)(await prior.ExecuteScalarAsync(ct))! != v.ExpectedRevision)throw new PortalException("La política cambió. Revise de nuevo el impacto.",409);}
        long revision;
        await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_agent_policies(scope,target,values,sha256,actor,reason) VALUES($1,$2,$3::jsonb,$4,$5,$6) RETURNING revision",c,t))
        {foreach(string value in new[]{v.Scope,v.Target,PortalJson.Write(v.Values),AgentPolicyContract.Hash(v.Values),actor,v.Reason.Trim()})cmd.Parameters.AddWithValue(value);revision=(long)(await cmd.ExecuteScalarAsync(ct))!;}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.agent_policy.published","AgentPolicy",revision.ToString(),new{v.Scope,v.Target,v.Values,v.Reason,revision},ct);await t.CommitAsync(ct);return revision;
    }
    public async Task<AgentPolicyEnvelope> ResolveAsync(Guid device,bool received,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var result=await Resolve(c,t,device,ct);
        if(received){await using var cmd=new NpgsqlCommand("""
            INSERT INTO atlas_agent_policy_status(device_id,protocol,desired_revision,desired_sha256,status,checked_at_utc)
            VALUES($1,1,$2,$3,'RECEIVED',CURRENT_TIMESTAMP)
            ON CONFLICT(device_id) DO UPDATE SET protocol=1,desired_revision=$2,desired_sha256=$3,checked_at_utc=CURRENT_TIMESTAMP,
            status=CASE WHEN atlas_agent_policy_status.desired_revision=$2 THEN atlas_agent_policy_status.status ELSE 'RECEIVED' END,
            error_code=CASE WHEN atlas_agent_policy_status.desired_revision=$2 THEN atlas_agent_policy_status.error_code ELSE NULL END
            """,c,t);cmd.Parameters.AddWithValue(device);cmd.Parameters.AddWithValue(result.Revision);cmd.Parameters.AddWithValue(result.Sha256);await cmd.ExecuteNonQueryAsync(ct);}
        await t.CommitAsync(ct);return result;
    }
    private static async Task<AgentPolicyEnvelope> Resolve(NpgsqlConnection c,NpgsqlTransaction t,Guid device,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT p.revision,p.scope,p.target,p.values::text,p.sha256 FROM devices d
            LEFT JOIN LATERAL(SELECT * FROM atlas_agent_policies WHERE (scope='INSTITUTION' AND target='*') OR (scope='AGENCY' AND target=d.agency_code) OR(scope='DEVICE' AND target=d.id::text)
            ORDER BY CASE scope WHEN 'DEVICE' THEN 3 WHEN 'AGENCY' THEN 2 ELSE 1 END DESC,revision DESC LIMIT 1)p ON true
            WHERE d.id=$1 AND d.enabled AND d.retired_at_utc IS NULL
            """,c,t);cmd.Parameters.AddWithValue(device);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new PortalException("Equipo no habilitado.",403);
        if(r.IsDBNull(0)){var values=new AgentPolicyValues();return new(1,0,device,"DEFAULT","*",values,AgentPolicyContract.Hash(values));}
        return new(1,r.GetInt64(0),device,r.GetString(1),r.GetString(2),PortalJson.Read<AgentPolicyValues>(r.GetString(3)),r.GetString(4));
    }
    public async Task ReceiptAsync(Guid device,AgentPolicyReceipt receipt,CancellationToken ct)
    {
        if(receipt.Status is not("APPLIED" or "REJECTED") || receipt.ErrorCode is not(null or "INVALID_POLICY" or "CACHE_WRITE_FAILED"))throw new PortalException("Confirmación no válida.");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var expected=await Resolve(c,t,device,ct);
        if(expected.Revision!=receipt.Revision || expected.Sha256!=receipt.Sha256)throw new PortalException("La política esperada cambió.",409);
        await using var cmd=new NpgsqlCommand("""
            UPDATE atlas_agent_policy_status SET status=$1,error_code=$2,
            applied_revision=CASE WHEN $1='APPLIED' THEN $3 ELSE applied_revision END,
            applied_at_utc=CASE WHEN $1='APPLIED' THEN CURRENT_TIMESTAMP ELSE applied_at_utc END
            WHERE device_id=$4 AND desired_revision=$3 AND desired_sha256=$5
            """,c,t);cmd.Parameters.AddWithValue(receipt.Status);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)receipt.ErrorCode??DBNull.Value});cmd.Parameters.AddWithValue(receipt.Revision);cmd.Parameters.AddWithValue(device);cmd.Parameters.AddWithValue(receipt.Sha256);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("Primero debe consultar la política vigente.",409);
        await t.CommitAsync(ct);
    }
    public async Task<object> OverviewAsync(CancellationToken ct)=>new{
        currentPolicies=await configuration.QueryAsync("SELECT DISTINCT ON(scope,target) revision,scope,target FROM atlas_agent_policies ORDER BY scope,target,revision DESC",ct),
        policies=await configuration.QueryAsync("SELECT revision,scope,target,values,actor,reason,created_at_utc FROM atlas_agent_policies ORDER BY revision DESC LIMIT 200",ct),
        devices=await configuration.QueryAsync("""
            SELECT d.id,d.machine_name,d.agency_code,d.agent_version,d.last_seen_at_utc,p.revision AS expected_revision,s.applied_revision,s.checked_at_utc,s.error_code,
            CASE WHEN s.device_id IS NULL THEN 'NOT_COMPATIBLE' WHEN COALESCE(p.revision,0)<>s.desired_revision THEN 'PENDING' ELSE s.status END AS policy_status
            FROM devices d LEFT JOIN atlas_agent_policy_status s ON s.device_id=d.id
            LEFT JOIN LATERAL(SELECT revision FROM atlas_agent_policies WHERE(scope='INSTITUTION' AND target='*') OR(scope='AGENCY' AND target=d.agency_code) OR(scope='DEVICE' AND target=d.id::text)
            ORDER BY CASE scope WHEN 'DEVICE' THEN 3 WHEN 'AGENCY' THEN 2 ELSE 1 END DESC,revision DESC LIMIT 1)p ON true
            WHERE d.enabled AND d.retired_at_utc IS NULL ORDER BY d.machine_name,d.id
            """,ct),defaults=new AgentPolicyValues()};
}
