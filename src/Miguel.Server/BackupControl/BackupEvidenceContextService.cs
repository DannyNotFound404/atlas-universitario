using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.BackupControl;

public sealed record BackupContextReviewCandidate(Guid ProfileId,string Code,string SystemName,string AgencyCode,
    string Kind,Guid RecordId,string Source,string Result,DateTimeOffset ObservedAtUtc,string? Detail,
    string? ExternalReference,string ExpectedContextKey,JsonElement CurrentContext,
    Guid? ObservationId=null,DateTimeOffset? PeriodEndUtc=null);
public sealed record BackupContextReviewWorkspace(IReadOnlyList<BackupContextReviewCandidate> Items,int Count);
public sealed record BackupContextReviewItem(Guid ProfileId,Guid? RecordId,string ExpectedContextKey,
    string Kind="OBSERVATION",Guid? ObservationId=null);
public sealed record BackupContextReviewRequest(Guid RequestId,string Reason,bool ContextVerified,IReadOnlyList<BackupContextReviewItem> Items);
public sealed record BackupContextReviewResult(int Validated,int Reused);

// V4 pp.9–10/base7.2: review binds an original historical record to an explicitly
// verified context. It does not create a backup or restoration, or renew its date.
public sealed class BackupEvidenceContextService(NpgsqlDataSource db,AdminRepository audit,TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<BackupContextReviewWorkspace> ReadAsync(CancellationToken token)
    {
        await using var command=db.CreateCommand("""
          SELECT p.id,p.code,p.system_name,p.agency_code,h.kind,h.id,h.source,h.result,h.observed_at,h.detail,h.reference,
                 atlas_backup_context_key(p),atlas_backup_context_snapshot(p)::text
          FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
          LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
          CROSS JOIN LATERAL (
            (SELECT 'OBSERVATION' kind,o.id,o.source,o.result,o.period_end_utc observed_at,o.detail,o.external_reference reference,o.context_key
              FROM backup_control_observations o WHERE o.profile_id=p.id AND o.result='SUCCESS'
                AND(c.tracking_since_utc IS NULL OR o.period_end_utc>=c.tracking_since_utc)
              ORDER BY o.period_end_utc DESC,o.created_at_utc DESC LIMIT 1)
            UNION ALL
            (SELECT 'RESTORATION',r.id,'RESTORATION',r.result,r.finished_at_utc,r.tested_scope,NULL::text,r.context_key
              FROM backup_restoration_tests r WHERE r.profile_id=p.id AND r.result='SUCCESS'
                AND(c.tracking_since_utc IS NULL OR r.finished_at_utc>=c.tracking_since_utc)
              ORDER BY r.finished_at_utc DESC,r.created_at_utc DESC LIMIT 1)
          )h
          WHERE p.enabled AND p.approval_status IN('ACTIVE','APPROVED') AND NOT COALESCE(c.backups_paused,false)
            AND(p.device_id IS NULL OR d.enabled) AND h.context_key IS NULL
            AND NOT EXISTS(SELECT 1 FROM backup_evidence_context_validations v WHERE v.record_kind=h.kind AND v.record_id=h.id)
          ORDER BY p.agency_code,p.system_name,h.kind,p.id;
          """);
        List<BackupContextReviewCandidate> items=[];
        await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
            string kind=reader.GetString(4);var record=reader.GetGuid(5);var at=reader.GetFieldValue<DateTimeOffset>(8);
            items.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),kind,record,
                reader.GetString(6),reader.GetString(7),at,reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),
                reader.GetString(11),JsonSerializer.Deserialize<JsonElement>(reader.GetString(12)),kind=="OBSERVATION"?record:null,kind=="OBSERVATION"?at:null));
        }
        return new(items,items.Count);
    }

    public async Task<BackupContextReviewResult> ReviewAsync(BackupContextReviewRequest request,string actor,CancellationToken token)
    {
        string reason=request.Reason?.Trim()??"";
        if(request.RequestId==Guid.Empty || !request.ContextVerified || reason.Length is <3 or >2000 ||
            reason.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')) || request.Items is null || request.Items.Count is <1 or >500 || request.Items.Any(i=>i is null))
            throw new BackupWorkspaceException("Confirma la revisión del activo, alcance y destino originales, explica su fundamento y selecciona entre 1 y 500 evidencias históricas.");
        var items=request.Items.Select(i=>i with{Kind=(i.Kind??"OBSERVATION").Trim().ToUpperInvariant(),RecordId=i.RecordId??i.ObservationId}).ToArray();
        if(items.Any(i=>i.ProfileId==Guid.Empty || i.RecordId is null || i.RecordId==Guid.Empty || i.Kind is not ("OBSERVATION" or "RESTORATION") ||
                i.ExpectedContextKey?.Length!=64) || items.Select(i=>(i.Kind,i.RecordId)).Distinct().Count()!=items.Length)
            throw new BackupWorkspaceException("La selección histórica no es válida. Actualiza su revisión.");
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{reason,items=items.OrderBy(i=>i.ProfileId).ThenBy(i=>i.Kind).ThenBy(i=>i.RecordId)},Json))));
        await using var connection=await db.OpenConnectionAsync(token);await using var transaction=await connection.BeginTransactionAsync(token);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1));",connection,transaction))
        {gate.Parameters.AddWithValue("backup-context-review:"+request.RequestId.ToString("D"));await gate.ExecuteNonQueryAsync(token);}
        await using(var prior=new NpgsqlCommand("SELECT fingerprint FROM backup_context_review_requests WHERE id=$1;",connection,transaction))
        {
            prior.Parameters.AddWithValue(request.RequestId);
            if(await prior.ExecuteScalarAsync(token) is string stored)
            {
                if(stored!=fingerprint)throw new BackupWorkspaceException("La solicitud ya fue utilizada con otros datos.",true);
                return new(0,items.Length);
            }
        }
        var now=clock.GetUtcNow();
        await using(var create=new NpgsqlCommand("INSERT INTO backup_context_review_requests VALUES($1,$2,$3,$4,$5);",connection,transaction))
        {foreach(var value in new object[]{request.RequestId,fingerprint,now,actor,reason})create.Parameters.AddWithValue(value);await create.ExecuteNonQueryAsync(token);}
        int validated=0,reused=0;
        foreach(var item in items.OrderBy(i=>i.ProfileId).ThenBy(i=>i.Kind).ThenBy(i=>i.RecordId))
        {
            string context;
            await using(var guard=new NpgsqlCommand("""
                SELECT atlas_backup_context_key(p),atlas_backup_context_snapshot(p)::text
                FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
                LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
                WHERE p.id=$1 AND p.enabled AND p.approval_status IN('ACTIVE','APPROVED')
                  AND(p.device_id IS NULL OR d.enabled) AND NOT COALESCE(c.backups_paused,false) FOR UPDATE OF p;
                """,connection,transaction))
            {
                guard.Parameters.AddWithValue(item.ProfileId);await using var r=await guard.ExecuteReaderAsync(token);
                if(!await r.ReadAsync(token)||r.GetString(0)!=item.ExpectedContextKey)throw new BackupWorkspaceException("Cambió el activo, alcance, destino o estado. Actualiza la revisión; no se guardó ninguna validación.",true);
                context=r.GetString(1);
            }
            string recordSql=item.Kind=="OBSERVATION"?"SELECT o.context_key,o.period_end_utc FROM backup_control_observations o WHERE o.id=$1 AND o.profile_id=$2 AND o.result='SUCCESS' FOR SHARE;":
                "SELECT o.context_key,o.finished_at_utc FROM backup_restoration_tests o WHERE o.id=$1 AND o.profile_id=$2 AND o.result='SUCCESS' FOR SHARE;";
            DateTimeOffset originalAt;
            await using(var original=new NpgsqlCommand(recordSql,connection,transaction))
            {
                original.Parameters.AddWithValue(item.RecordId!.Value);original.Parameters.AddWithValue(item.ProfileId);
                await using var r=await original.ExecuteReaderAsync(token);
                if(!await r.ReadAsync(token)||!r.IsDBNull(0))throw new BackupWorkspaceException("La evidencia no es un registro histórico sin contexto. Una configuración diferente necesita evidencia nueva.",true);
                originalAt=r.GetFieldValue<DateTimeOffset>(1);
            }
            await using(var custody=new NpgsqlCommand("SELECT c.tracking_since_utc FROM asset_custody c JOIN backup_control_profiles p ON p.device_id=c.current_device_id WHERE p.id=$1;",connection,transaction))
            {custody.Parameters.AddWithValue(item.ProfileId);if(await custody.ExecuteScalarAsync(token) is DateTime since && originalAt.UtcDateTime<since)throw new BackupWorkspaceException("La evidencia es anterior a la asignación actual. No puede acreditarse al nuevo colaborador.",true);}
            await using(var existing=new NpgsqlCommand("SELECT context_key FROM backup_evidence_context_validations WHERE record_kind=$1 AND record_id=$2;",connection,transaction))
            {
                existing.Parameters.AddWithValue(item.Kind);existing.Parameters.AddWithValue(item.RecordId!.Value);
                if(await existing.ExecuteScalarAsync(token) is string key)
                {if(key!=item.ExpectedContextKey)throw new BackupWorkspaceException("La evidencia ya fue validada para otra configuración; conserva su historial y registra evidencia nueva.",true);reused++;continue;}
            }
            await using var insert=new NpgsqlCommand("""
                INSERT INTO backup_evidence_context_validations(id,request_id,profile_id,record_kind,record_id,context_key,context_snapshot,validated_at_utc,validated_by)
                VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9);
                """,connection,transaction);
            foreach(var value in new object[]{Guid.NewGuid(),request.RequestId,item.ProfileId,item.Kind,item.RecordId!.Value,item.ExpectedContextKey,context,now,actor})insert.Parameters.AddWithValue(value);
            await insert.ExecuteNonQueryAsync(token);validated++;
        }
        await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.context_reviewed","BackupContextReview",request.RequestId.ToString(),
            new{reason,items,validated,reused,OriginalDatesPreserved=true,NewCopyOrRestoration=false},token,transaction);
        await transaction.CommitAsync(token);return new(validated,reused);
    }
}
