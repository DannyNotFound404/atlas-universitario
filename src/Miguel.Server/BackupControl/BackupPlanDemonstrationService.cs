using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Miguel.Server.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.BackupControl;

public sealed class BackupPlanDemonstrationService(NpgsqlDataSource db, BackupControlRepository controls,
    BackupEvidenceService evidence, AdminRepository audit, IOptions<ServerOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Snake=new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower };
    private const string Eligible="""
        p.enabled AND p.approval_status IN ('ACTIVE','APPROVED')
        AND NOT COALESCE(c.backups_paused,false) AND (p.device_id IS NULL OR d.enabled)
        """;

    public static async Task<IReadOnlyList<BackupPlanDemonstrationSummary>> ReadSummariesAsync(
        NpgsqlDataSource db,DateTimeOffset now,CancellationToken token)
    {
        await using var cmd=db.CreateCommand($"""
            SELECT pl.id,pl.name,pl.revision,COALESCE(e.total,0),r.id,r.tested,r.successful,
                COALESCE(u.unresolved,0)+(SELECT COUNT(*)::integer FROM backup_plan_legacy_restoration_failures l WHERE l.plan_id=pl.id),r.finished_at_utc,r.next_due_utc,r.plan_revision,r.effective_required_samples,pl.configuration->>'provider'
            FROM backup_control_plans pl
            LEFT JOIN LATERAL (
                SELECT COUNT(DISTINCT COALESCE(p.device_id,p.id))::integer total
                FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
                LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
                WHERE p.plan_id=pl.id AND {Eligible}
            ) e ON true
            LEFT JOIN LATERAL (
                SELECT r.*,{BackupDemonstrationRules.EffectiveRequiredSql} AS effective_required_samples
                FROM backup_plan_demonstration_results r JOIN backup_plan_demonstrations d ON d.id=r.id WHERE r.plan_id=pl.id
                ORDER BY r.created_at_utc DESC,r.id DESC LIMIT 1
            ) r ON true
            LEFT JOIN LATERAL (
                SELECT SUM(unresolved)::integer unresolved FROM backup_plan_demonstration_results WHERE plan_id=pl.id
            ) u ON true ORDER BY pl.name,pl.id;
            """);
        await using var reader=await cmd.ExecuteReaderAsync(token);
        List<BackupPlanDemonstrationSummary> items=[];
        while(await reader.ReadAsync(token))
        {
            int eligible=reader.GetInt32(3),unresolved=reader.GetInt32(7);
            Guid? id=reader.IsDBNull(4)?null:reader.GetGuid(4);
            DateTimeOffset? last=reader.IsDBNull(8)?null:reader.GetFieldValue<DateTimeOffset>(8);
            DateTimeOffset? due=reader.IsDBNull(9)?null:reader.GetFieldValue<DateTimeOffset>(9);
            int tested=reader.IsDBNull(5)?0:reader.GetInt32(5);
            int? sessionRequired=reader.IsDBNull(11)?null:reader.GetInt32(11);
            bool incomplete=id.HasValue && tested<sessionRequired;
            bool sameRevision=id.HasValue && reader.GetInt32(10)==reader.GetInt32(2);
            // A partial session records real evidence but never earns a renewal deadline.
            if(incomplete || unresolved>0)due=null;
            string status=unresolved>0?"FOLLOW_UP":incomplete && sameRevision?"IN_PROGRESS":eligible==0?"NO_TARGETS":id is null?"PENDING":
                !sameRevision?"PLAN_CHANGED":due<=now?"OVERDUE":"CURRENT";
            items.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetInt32(2),eligible,
                BackupDemonstrationRules.Required(eligible,reader.GetString(12)),id,tested,
                reader.IsDBNull(6)?0:reader.GetInt32(6),unresolved,last,due,status,status is not ("CURRENT" or "NO_TARGETS"),sessionRequired,reader.GetString(12)));
        }
        return items;
    }

    private static async Task<List<BackupDemonstrationCandidate>> Candidates(NpgsqlConnection conn,
        NpgsqlTransaction? tx,Guid planId,CancellationToken token,bool locked=false)
    {
        await using var cmd=new NpgsqlCommand($"""
            SELECT p.id,p.device_id,p.system_name,p.agency_code,p.plan_revision,p.nas_appliance_id,
                CASE WHEN n.id IS NOT NULL THEN concat(chr(92),chr(92),n.host,chr(92),n.share_name)
                ELSE COALESCE(p.expected_destination,'Destino no informado') END,p.protected_scope,p.updated_at_utc,
                (SELECT MAX(r.finished_at_utc) FROM backup_restoration_tests r WHERE r.profile_id=p.id)
            FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
            LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
            LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id
            WHERE p.plan_id=$1 AND {Eligible}
            ORDER BY p.id {(locked?"FOR UPDATE OF p":"")};
            """,conn,tx);
        cmd.Parameters.AddWithValue(planId);
        await using var r=await cmd.ExecuteReaderAsync(token);
        List<BackupDemonstrationCandidate> items=[];
        while(await r.ReadAsync(token)) items.Add(new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetString(2),
            r.GetString(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetGuid(5),r.GetString(6),r.GetString(7),
            r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9)));
        return items;
    }

    public async Task<BackupDemonstrationWorkspace> ReadAsync(Guid planId,CancellationToken token)
    {
        var summary=(await ReadSummariesAsync(db,clock.GetUtcNow(),token)).FirstOrDefault(p=>p.PlanId==planId)
            ?? throw new BackupWorkspaceException("El plan no existe.",true);
        await using var conn=await db.OpenConnectionAsync(token);
        var candidates=await Candidates(conn,null,planId,token);
        // Recent sessions plus every session with unresolved samples, so failures never disappear behind pagination.
        await using var cmd=new NpgsqlCommand($"""
            SELECT d.id,d.plan_revision,d.configuration->>'name',d.population_count,{BackupDemonstrationRules.EffectiveRequiredSql},d.notes,d.created_at_utc,d.created_by,d.required_samples
            FROM backup_plan_demonstrations d JOIN backup_plan_demonstration_results r ON r.id=d.id
            WHERE d.plan_id=$1 AND (r.unresolved>0 OR r.tested<({BackupDemonstrationRules.EffectiveRequiredSql}) OR d.id IN (
                SELECT id FROM backup_plan_demonstrations WHERE plan_id=$1 ORDER BY created_at_utc DESC,id DESC LIMIT 30))
            ORDER BY d.created_at_utc DESC,d.id DESC;
            """,conn);
        cmd.Parameters.AddWithValue(planId);
        List<BackupDemonstrationView> sessions=[];
        await using(var r=await cmd.ExecuteReaderAsync(token))
            while(await r.ReadAsync(token)) sessions.Add(new(r.GetGuid(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3),r.GetInt32(4),
                r.IsDBNull(5)?null:r.GetString(5),r.GetFieldValue<DateTimeOffset>(6),r.GetString(7),[],[],r.GetInt32(8)));
        for(int i=0;i<sessions.Count;i++)
        {
            var attempts=await Attempts(conn,null,sessions[i].Id,token);
            await using var files=new NpgsqlCommand("""
                SELECT e.id,e.original_file_name,e.sha256,q.id FROM backup_plan_demonstration_requests q
                JOIN backup_control_evidence e ON e.id=q.evidence_id
                WHERE q.demonstration_id=$1 AND e.status='ACTIVE' ORDER BY q.created_at_utc;
                """,conn);
            files.Parameters.AddWithValue(sessions[i].Id);
            List<BackupDemonstrationEvidence> items=[];
            await using(var r=await files.ExecuteReaderAsync(token))
                while(await r.ReadAsync(token)) items.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetGuid(3)));
            sessions[i]=sessions[i] with { Attempts=attempts,Evidence=items };
        }
        List<BackupLegacyRestorationFailure> legacy=[];
        await using(var failures=new NpgsqlCommand("SELECT profile_id,system_name,result,finished_at_utc FROM backup_plan_legacy_restoration_failures WHERE plan_id=$1 ORDER BY system_name;",conn))
        {
            failures.Parameters.AddWithValue(planId);
            await using var r=await failures.ExecuteReaderAsync(token);
            while(await r.ReadAsync(token))legacy.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetFieldValue<DateTimeOffset>(3)));
        }
        return new(summary,candidates,BackupDemonstrationRules.Suggest(candidates,summary.Provider),sessions,legacy);
    }

    private static async Task<List<BackupDemonstrationAttempt>> Attempts(NpgsqlConnection conn,NpgsqlTransaction? tx,Guid id,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT s.id,s.profile_id,s.repeats_sample_id,s.snapshot::text,to_jsonb(r)::text,s.created_at_utc,r.created_by
            FROM backup_plan_demonstration_samples s JOIN backup_restoration_tests r ON r.id=s.restoration_id
            WHERE s.demonstration_id=$1 ORDER BY s.attempt_order;
            """,conn,tx);
        cmd.Parameters.AddWithValue(id);
        List<BackupDemonstrationAttempt> items=[];
        await using var reader=await cmd.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token)) items.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),
            JsonSerializer.Deserialize<BackupDemonstrationCandidate>(reader.GetString(3),Json)!,
            JsonSerializer.Deserialize<BackupRestorationRequest>(reader.GetString(4),Snake)!,reader.GetFieldValue<DateTimeOffset>(5),reader.GetString(6)));
        return items;
    }

    public async Task<Guid> SaveAsync(Guid planId,Guid? demonstrationId,BackupDemonstrationRequest request,
        IFormFile? file,string actor,CancellationToken token,bool append=false)
    {
        DateTimeOffset now=clock.GetUtcNow();
        if(append && demonstrationId is null)throw new BackupWorkspaceException("Selecciona una demostración para continuar.");
        request=BackupDemonstrationRules.Normalize(request,now,demonstrationId.HasValue && !append);
        string? fileHash=null;
        if(file is not null)
        {
            if(file.Length<=0 || file.Length>options.Value.MaxBackupEvidenceUploadBytes)
                throw new BackupWorkspaceException("La evidencia debe tener contenido y no superar 25 MB.");
            await using var source=file.OpenReadStream();
            fileHash=Convert.ToHexString(await SHA256.HashDataAsync(source,token));
        }
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            // Preserve the RC1 fingerprint for existing requests; new append requests bind their operation.
            append ? (object)new { planId,demonstrationId,request,FileName=file?.FileName,FileHash=fileHash,Operation="append" } :
                new { planId,demonstrationId,request,FileName=file?.FileName,FileHash=fileHash },Json))));
        await using var conn=await db.OpenConnectionAsync(token);
        await using var tx=await conn.BeginTransactionAsync(token);
        BackupEvidenceUploadResult? uploaded=null;bool committing=false;
        try
        {
            // Serialize by plan before checking idempotency and current membership.
            await using var plan=new NpgsqlCommand("SELECT revision,configuration::text FROM backup_control_plans WHERE id=$1 FOR UPDATE;",conn,tx);
            plan.Parameters.AddWithValue(planId);
            int revision;string configuration;
            await using(var r=await plan.ExecuteReaderAsync(token))
            {
                if(!await r.ReadAsync(token))throw new BackupWorkspaceException("El plan ya no existe.",true);
                revision=r.GetInt32(0);configuration=r.GetString(1);
            }
            await using(var duplicate=new NpgsqlCommand("SELECT fingerprint,demonstration_id FROM backup_plan_demonstration_requests WHERE id=$1;",conn,tx))
            {
                duplicate.Parameters.AddWithValue(request.RequestId);
                await using var r=await duplicate.ExecuteReaderAsync(token);
                if(await r.ReadAsync(token))
                {
                    if(r.GetString(0)!=fingerprint)throw new BackupWorkspaceException("La solicitud ya fue utilizada con otros datos.",true);
                    return r.GetGuid(1);
                }
            }
            if(revision!=request.ExpectedRevision)throw new BackupWorkspaceException("El plan cambió. Actualiza antes de guardar.",true);
            var candidates=await Candidates(conn,tx,planId,token,true);
            var definition=JsonSerializer.Deserialize<BackupPlanDefinition>(configuration,Json)!;
            int population=candidates.Select(c=>c.Identity).Distinct().Count(),required=BackupDemonstrationRules.Required(population,definition.Provider);
            var selected=request.Samples.Select(s=>candidates.FirstOrDefault(c=>c.ProfileId==s.ProfileId)).ToArray();
            if(selected.Any(s=>s is null) || selected.Select(c=>c!.Identity).Distinct().Count()!=selected.Length ||
                request.Samples.Where((s,i)=>s.BindingStamp!=selected[i]!.BindingStamp).Any())
                throw new BackupWorkspaceException("Un equipo, destino o alcance cambió, está pausado o ya no pertenece al plan. Actualiza la selección.",true);
            Guid id=demonstrationId ?? request.RequestId;
            if(demonstrationId is null)
            {
                if(required==0 || request.Samples.Count>required)
                    throw new BackupWorkspaceException(required==1?"Registra una sola muestra para esta demostración.":$"Registra entre una y {required} muestras de equipos o sistemas distintos.");
                await using(var active=new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM backup_plan_demonstration_results r JOIN backup_plan_demonstrations d ON d.id=r.id WHERE r.plan_id=$1 AND r.plan_revision=$2 AND r.tested<({BackupDemonstrationRules.EffectiveRequiredSql}));",conn,tx))
                {
                    active.Parameters.AddWithValue(planId);active.Parameters.AddWithValue(revision);
                    if((bool)(await active.ExecuteScalarAsync(token))!)
                        throw new BackupWorkspaceException("Ya hay una demostración en curso. Ábrela para registrar las muestras restantes.",true);
                }
                await using var create=new NpgsqlCommand("""
                    INSERT INTO backup_plan_demonstrations(id,plan_id,plan_revision,configuration,population_count,required_samples,notes,created_at_utc,created_by)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9);
                    """,conn,tx);
                create.Parameters.AddWithValue(id);create.Parameters.AddWithValue(planId);create.Parameters.AddWithValue(revision);
                create.Parameters.AddWithValue(NpgsqlDbType.Jsonb,configuration);create.Parameters.AddWithValue(population);create.Parameters.AddWithValue(required);
                create.Parameters.AddWithValue(NpgsqlDbType.Varchar,(object?)request.Notes??DBNull.Value);
                create.Parameters.AddWithValue(now);create.Parameters.AddWithValue(actor);await create.ExecuteNonQueryAsync(token);
            }
            else if(append)
            {
                await using var owner=new NpgsqlCommand($"SELECT d.plan_id,d.plan_revision,{BackupDemonstrationRules.EffectiveRequiredSql} FROM backup_plan_demonstrations d WHERE d.id=$1 FOR UPDATE;",conn,tx);
                owner.Parameters.AddWithValue(id);
                int sessionRequired;
                await using(var r=await owner.ExecuteReaderAsync(token))
                {
                    if(!await r.ReadAsync(token) || r.GetGuid(0)!=planId)
                        throw new BackupWorkspaceException("La demostración no pertenece a este plan.",true);
                    if(r.GetInt32(1)!=revision)
                        throw new BackupWorkspaceException("La versión del plan cambió. Conserva esta sesión y registra una demostración de la nueva versión.",true);
                    sessionRequired=r.GetInt32(2);
                }
                var attempts=await Attempts(conn,tx,id,token);
                var identities=attempts.Select(a=>a.Snapshot.Identity).ToHashSet();
                if(attempts.Select(a=>a.ProfileId).Distinct().Count()+request.Samples.Count>sessionRequired)
                    throw new BackupWorkspaceException("La demostración ya tiene todas sus muestras o cambió mientras la completabas. Actualiza el historial.",true);
                if(selected.Any(c=>identities.Contains(c!.Identity)) || request.Samples.Any(s=>attempts.Any(a=>a.ProfileId==s.ProfileId)))
                    throw new BackupWorkspaceException("Ese equipo ya tiene una muestra en esta demostración. Usa Repetir esta muestra para resolver un fallo.",true);
            }
            else
            {
                await using var owner=new NpgsqlCommand("SELECT plan_id FROM backup_plan_demonstrations WHERE id=$1 FOR UPDATE;",conn,tx);
                owner.Parameters.AddWithValue(id);
                if(await owner.ExecuteScalarAsync(token) is not Guid ownerId || ownerId!=planId)
                    throw new BackupWorkspaceException("La demostración no pertenece a este plan.",true);
                var attempts=await Attempts(conn,tx,id,token);
                var sample=request.Samples[0];
                var prior=attempts.LastOrDefault(a=>a.ProfileId==sample.ProfileId);
                if(prior is null || prior.Id!=sample.RepeatsSampleId || prior.Restoration.Result=="SUCCESS" ||
                    sample.Restoration.StartedAtUtc<prior.Restoration.FinishedAtUtc)
                    throw new BackupWorkspaceException("Selecciona el último intento pendiente y registra una repetición posterior.",true);
                if(prior.Snapshot.DeviceId!=selected[0]!.DeviceId || prior.Snapshot.Destination!=selected[0]!.Destination ||
                    prior.Snapshot.Scope!=selected[0]!.Scope || prior.Snapshot.PlanRevision!=selected[0]!.PlanRevision)
                    throw new BackupWorkspaceException("El alcance o destino cambió desde el fallo. Una prueba del nuevo alcance no resuelve el anterior; conserva su seguimiento y registra una nueva demostración.",true);
            }
            Guid? firstRestoration=null;
            for(int i=0;i<request.Samples.Count;i++)
            {
                var sample=request.Samples[i];
                Guid restorationId=await controls.AddRestorationAsync(sample.ProfileId,sample.Restoration,actor,now,token,tx)
                    ?? throw new BackupWorkspaceException("No se pudo registrar una muestra.",true);
                firstRestoration??=restorationId;
                await using var insert=new NpgsqlCommand("""
                    INSERT INTO backup_plan_demonstration_samples(id,demonstration_id,profile_id,restoration_id,snapshot,repeats_sample_id,created_at_utc)
                    VALUES($1,$2,$3,$4,$5,$6,$7);
                    """,conn,tx);
                insert.Parameters.AddWithValue(Guid.NewGuid());insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(sample.ProfileId);
                insert.Parameters.AddWithValue(restorationId);insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(selected[i],Json));
                insert.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)sample.RepeatsSampleId??DBNull.Value);insert.Parameters.AddWithValue(now);
                await insert.ExecuteNonQueryAsync(token);
            }
            if(file is not null)
            {
                // Store one joint report, linked both to the session and its first actual restoration.
                var metadata=new BackupEvidenceUploadMetadata(file.FileName,"REPORT",null,firstRestoration,null,
                    request.Samples.Max(s=>s.Restoration.FinishedAtUtc),request.Notes,"Demostración de restauración por plan");
                await using var source=file.OpenReadStream();
                uploaded=await evidence.UploadAsync(request.Samples[0].ProfileId,metadata,source,actor,now,token,tx)
                    ?? throw new BackupWorkspaceException("No se pudo conservar la evidencia conjunta.");
            }
            await using(var saved=new NpgsqlCommand("""
                INSERT INTO backup_plan_demonstration_requests(id,demonstration_id,fingerprint,evidence_id,created_at_utc)
                VALUES($1,$2,$3,$4,$5);
                """,conn,tx))
            {
                saved.Parameters.AddWithValue(request.RequestId);saved.Parameters.AddWithValue(id);saved.Parameters.AddWithValue(fingerprint);
                saved.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)uploaded?.Id??DBNull.Value);saved.Parameters.AddWithValue(now);
                await saved.ExecuteNonQueryAsync(token);
            }
            await audit.WriteAuditAsync(now,"Admin",actor,demonstrationId is null?"admin.backup_control.demonstration.created":append?"admin.backup_control.demonstration.sample_added":"admin.backup_control.demonstration.retried",
                "BackupPlan",planId.ToString(),new { DemonstrationId=id,Request=request,Scope=selected,Evidence=uploaded },token,tx);
            committing=true;await tx.CommitAsync(token);return id;
        }
        catch
        {
            if(!committing && uploaded?.PhysicalPath is { } path)
                try { File.Delete(path); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            throw;
        }
    }
}
