using System.Text;
using System.Text.Json;
using Miguel.Server.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Reports;

public sealed class ReportService(NpgsqlDataSource db,ConfigurationService configuration,ReportCatalog catalog,TimeProvider clock)
{
    public static bool ExportRole(string role)=>role is "Administrator" or "Auditor" or "TechnologyManager";
    public async Task<Guid> QueueAsync(Guid owner,string actor,ReportJobRequest job,CancellationToken ct)
    {
        if(job.RequestKey==Guid.Empty)throw new PortalException("Falta identificador de solicitud.");
        var settings=await configuration.GeneralAsync(ct);
        var request=ReportCatalog.Normalize(job.Report,settings.Value,clock.GetUtcNow());
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393202)",c,t))await gate.ExecuteNonQueryAsync(ct);
        await using(var existing=new NpgsqlCommand("SELECT id,request::text FROM atlas_report_jobs WHERE owner_id=$1 AND request_key=$2",c,t))
        {existing.Parameters.AddWithValue(owner);existing.Parameters.AddWithValue(job.RequestKey);await using var r=await existing.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){if(PortalJson.Read<ReportRequest>(r.GetString(1))!=request)throw new PortalException("La solicitud ya se usó con otros filtros.",409);return r.GetGuid(0);}}
        await using(var count=new NpgsqlCommand("SELECT COUNT(*) FROM atlas_report_jobs WHERE status IN ('QUEUED','RUNNING') AND owner_id=$1",c,t))
        {count.Parameters.AddWithValue(owner);if((long)(await count.ExecuteScalarAsync(ct))!>=3)throw new PortalException("Ya tiene tres informes en proceso. Espere o cancele alguno.",429);}
        Guid id=Guid.NewGuid();await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_report_jobs(id,owner_id,request_key,request) VALUES($1,$2,$3,$4::jsonb)",c,t))
        {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(job.RequestKey);cmd.Parameters.AddWithValue(PortalJson.Write(request));await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.queued","Report",id.ToString(),new{request},ct);await t.CommitAsync(ct);return id;
    }
    public Task<List<Dictionary<string,object?>>> HistoryAsync(Guid owner,int page,CancellationToken ct)=>configuration.QueryAsync("""
        SELECT id,request,status,progress,created_at_utc,finished_at_utc,error_code,schedule_id,COUNT(*) OVER() AS total
        FROM atlas_report_jobs WHERE owner_id=$1 ORDER BY created_at_utc DESC,id LIMIT 20 OFFSET $2
        """,ct,owner,(Math.Clamp(page,1,100000)-1)*20);
    public async Task CancelAsync(Guid owner,Guid id,string actor,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_report_jobs SET status='CANCELLED',finished_at_utc=CURRENT_TIMESTAMP,lease_id=NULL WHERE id=$1 AND owner_id=$2 AND status IN ('QUEUED','RUNNING')",c,t))
        {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(owner);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("El informe ya terminó o no está disponible.",409);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.cancelled","Report",id.ToString(),new{},ct);await t.CommitAsync(ct);
    }
    public async Task<ReportSnapshot> PreviewAsync(ReportRequest request,string actor,CancellationToken ct)
    { var s=await configuration.GeneralAsync(ct);return await catalog.CaptureAsync(Guid.Empty,request,s.Value,actor,ct); }
    public async Task<ReportSnapshot> SnapshotAsync(Guid owner,Guid id,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT s.snapshot::text FROM atlas_report_snapshots s JOIN atlas_report_jobs j ON j.id=s.report_id WHERE j.owner_id=$1 AND j.id=$2 AND j.status='COMPLETE'");cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) is string json?PortalJson.Read<ReportSnapshot>(json):throw new PortalException("Informe no disponible.",404);
    }
    public async Task<(byte[] Bytes,string Hash)> FileAsync(Guid owner,Guid id,string format,string actor,CancellationToken ct)
    {
        if(format is not("pdf" or "csv" or "xlsx"))throw new PortalException("Formato no disponible.",404);
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        byte[] bytes;string hash;
        await using(var cmd=new NpgsqlCommand("SELECT f.content,f.sha256 FROM atlas_report_files f JOIN atlas_report_jobs j ON j.id=f.report_id WHERE j.owner_id=$1 AND j.id=$2 AND f.format=$3 AND j.status='COMPLETE'",c,t))
        {cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(format);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new PortalException("Archivo no disponible.",404);bytes=r.GetFieldValue<byte[]>(0);hash=r.GetString(1);}
        if(ReportFormats.Hash(bytes)!=hash)throw new PortalException("No se pudo verificar la integridad del archivo.",409);
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.downloaded","Report",id.ToString(),new{format,sha256=hash,size=bytes.Length},ct);await t.CommitAsync(ct);return(bytes,hash);
    }
    public async Task SaveVariantAsync(Guid owner,ReportVariantRequest value,string actor,CancellationToken ct)
    {
        var settings=await configuration.GeneralAsync(ct);var report=ReportCatalog.Normalize(value.Report,settings.Value,clock.GetUtcNow());string name=ConfigurationService.Text(value.Name,120,"el nombre");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_report_variants(id,owner_id,name,request) VALUES($1,$2,$3,$4::jsonb)",c,t))
        {cmd.Parameters.AddWithValue(Guid.NewGuid());cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(name);cmd.Parameters.AddWithValue(PortalJson.Write(report));await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.variant.saved","ReportVariant",owner.ToString(),new{name,report},ct);await t.CommitAsync(ct);
    }
    public async Task ScheduleAsync(Guid owner,ReportScheduleRequest value,string actor,CancellationToken ct)
    {
        var settings=await configuration.GeneralAsync(ct);var report=ReportCatalog.Normalize(value.Report,settings.Value,clock.GetUtcNow());
        string name=ConfigurationService.Text(value.Name,120,"el nombre");ConfigurationService.Zone(value.TimeZone);
        if(value.Cadence is not("DAILY" or "WEEKLY" or "MONTHLY" or "QUARTERLY"))throw new PortalException("Frecuencia no válida.");
        if(value.FirstAtUtc<clock.GetUtcNow() || value.FirstAtUtc>clock.GetUtcNow().AddYears(1))throw new PortalException("La primera ejecución debe ser futura, dentro del próximo año.");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393203)",c,t))await gate.ExecuteNonQueryAsync(ct);
        await using(var count=new NpgsqlCommand("SELECT COUNT(*) FROM atlas_report_schedules WHERE owner_id=$1",c,t)){count.Parameters.AddWithValue(owner);if((long)(await count.ExecuteScalarAsync(ct))!>=50)throw new PortalException("Límite de 50 programaciones por usuario.");}
        var id=Guid.NewGuid();await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_report_schedules(id,owner_id,name,request,cadence,time_zone,next_at_utc) VALUES($1,$2,$3,$4::jsonb,$5,$6,$7)",c,t))
        {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(name);cmd.Parameters.AddWithValue(PortalJson.Write(report));cmd.Parameters.AddWithValue(value.Cadence);cmd.Parameters.AddWithValue(value.TimeZone);cmd.Parameters.AddWithValue(value.FirstAtUtc.ToUniversalTime());await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.schedule.created","ReportSchedule",id.ToString(),new{name,value.Cadence,value.TimeZone,value.FirstAtUtc,report},ct);await t.CommitAsync(ct);
    }
    public async Task ToggleScheduleAsync(Guid owner,Guid id,ReportScheduleUpdate value,string actor,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_report_schedules SET enabled=$1,revision=revision+1,suspension_reason=NULL,updated_at_utc=CURRENT_TIMESTAMP WHERE id=$2 AND owner_id=$3 AND revision=$4",c,t))
        {cmd.Parameters.AddWithValue(value.Enabled);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(value.Revision);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("La programación cambió. Actualice la pantalla.",409);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.report.schedule.changed","ReportSchedule",id.ToString(),value,ct);await t.CommitAsync(ct);
    }
    public static DateTimeOffset Next(DateTimeOffset previous,string cadence,string zoneId)
    {
        var zone=ConfigurationService.Zone(zoneId);var local=TimeZoneInfo.ConvertTime(previous,zone).DateTime;
        local=cadence switch{"DAILY"=>local.AddDays(1),"WEEKLY"=>local.AddDays(7),"MONTHLY"=>local.AddMonths(1),"QUARTERLY"=>local.AddMonths(3),_=>throw new PortalException("Frecuencia no válida.")};
        while(zone.IsInvalidTime(local))local=local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,zone));
    }
    public async Task TickSchedulesAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var suspend=new NpgsqlCommand("""
            UPDATE atlas_report_schedules s SET enabled=false,suspension_reason='OWNER_NOT_AUTHORIZED',revision=revision+1
            FROM admin_users u WHERE u.id=s.owner_id AND s.enabled AND (NOT u.enabled OR u.role NOT IN ('Administrator','Auditor','TechnologyManager'))
            """,c,t))await suspend.ExecuteNonQueryAsync(ct);
        List<(Guid Id,Guid Owner,string Actor,string Request,string Cadence,string Zone,DateTimeOffset Due)> pending=[];
        await using(var cmd=new NpgsqlCommand("""
            SELECT s.id,s.owner_id,u.username,s.request::text,s.cadence,s.time_zone,s.next_at_utc
            FROM atlas_report_schedules s JOIN admin_users u ON u.id=s.owner_id
            WHERE s.enabled AND s.next_at_utc<=$1 ORDER BY s.next_at_utc LIMIT 10 FOR UPDATE OF s SKIP LOCKED
            """,c,t))
        {cmd.Parameters.AddWithValue(clock.GetUtcNow());await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))pending.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetFieldValue<DateTimeOffset>(6)));}
        foreach(var s in pending)
        {
            // Missed runs are coalesced into one report, never a flood of historical deliveries.
            var request=PortalJson.Read<ReportRequest>(s.Request);var localToday=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(),ConfigurationService.Zone(s.Zone)).DateTime);
            int days=request.To!.Value.DayNumber-request.From!.Value.DayNumber+1;request=request with{To=localToday.AddDays(-1),From=localToday.AddDays(-days)};
            DateTimeOffset next=s.Due;do{next=Next(next,s.Cadence,s.Zone);}while(next<=clock.GetUtcNow());
            Guid id=Guid.NewGuid();await using(var cmd=new NpgsqlCommand("""
                WITH queued AS (INSERT INTO atlas_report_jobs(id,owner_id,request_key,request,schedule_id,scheduled_at_utc) VALUES($1,$2,$1,$3::jsonb,$4,$5) ON CONFLICT(schedule_id,scheduled_at_utc) DO NOTHING RETURNING id)
                UPDATE atlas_report_schedules SET next_at_utc=$6 WHERE id=$4
                """,c,t))
            {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(s.Owner);cmd.Parameters.AddWithValue(PortalJson.Write(request));cmd.Parameters.AddWithValue(s.Id);cmd.Parameters.AddWithValue(s.Due);cmd.Parameters.AddWithValue(next);await cmd.ExecuteNonQueryAsync(ct);}
            await ConfigurationService.AuditAsync(c,t,s.Actor,"admin.report.schedule.queued","Report",id.ToString(),new{scheduleId=s.Id,scheduledAtUtc=s.Due},ct);
        }
        await t.CommitAsync(ct);
    }
    public async Task<bool> WorkOneAsync(CancellationToken ct)
    {
        Guid id,owner,lease=Guid.NewGuid();string request,author;string? scheduledZone;bool authorized;
        await using(var cmd=db.CreateCommand("""
            WITH candidate AS (SELECT id FROM atlas_report_jobs WHERE (status='QUEUED' OR(status='RUNNING' AND lease_until_utc<CURRENT_TIMESTAMP)) AND attempts<3 ORDER BY created_at_utc LIMIT 1 FOR UPDATE SKIP LOCKED)
            UPDATE atlas_report_jobs j SET status='RUNNING',attempts=attempts+1,progress=10,lease_id=$1,lease_until_utc=CURRENT_TIMESTAMP+INTERVAL '5 minutes'
            FROM candidate,admin_users u WHERE j.id=candidate.id AND u.id=j.owner_id RETURNING j.id,j.owner_id,j.request::text,u.username,u.enabled AND u.role IN ('Administrator','Auditor','TechnologyManager'),(SELECT time_zone FROM atlas_report_schedules WHERE id=j.schedule_id)
            """))
        {cmd.Parameters.AddWithValue(lease);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return false;id=r.GetGuid(0);owner=r.GetGuid(1);request=r.GetString(2);author=r.GetString(3);authorized=r.GetBoolean(4);scheduledZone=r.IsDBNull(5)?null:r.GetString(5);}
        try
        {
            if(!authorized)throw new PortalException("OWNER_NOT_AUTHORIZED",403);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(3));var token=timeout.Token;
            var settings=await configuration.GeneralAsync(token);
            var snapshot=await catalog.CaptureAsync(id,PortalJson.Read<ReportRequest>(request),scheduledZone is null?settings.Value:settings.Value with{ReportTimeZone=scheduledZone},author,token);
            var json=PortalJson.Write(snapshot);var files=new Dictionary<string,byte[]> { ["csv"]=ReportFormats.Csv(snapshot),["xlsx"]=ReportFormats.Xlsx(snapshot),["pdf"]=ReportFormats.Pdf(snapshot) };
            if(files.Values.Any(b=>b.Length>25000000))throw new PortalException("REPORT_TOO_LARGE");
            await using var c=await db.OpenConnectionAsync(token);await using var t=await c.BeginTransactionAsync(token);
            // Fence stale workers, cancellation and permission revocation before committing immutable evidence.
            await using(var guard=new NpgsqlCommand("""
                SELECT u.enabled AND u.role IN ('Administrator','Auditor','TechnologyManager') FROM atlas_report_jobs j JOIN admin_users u ON u.id=j.owner_id
                WHERE j.id=$1 AND j.lease_id=$2 AND j.status='RUNNING' FOR UPDATE OF j,u
                """,c,t))
            {guard.Parameters.AddWithValue(id);guard.Parameters.AddWithValue(lease);var result=await guard.ExecuteScalarAsync(token);if(result is null)return true;if(!(bool)result)throw new PortalException("OWNER_NOT_AUTHORIZED",403);}
            string snapshotHash=ReportFormats.Hash(Encoding.UTF8.GetBytes(json));
            await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_report_snapshots(report_id,snapshot,sha256) VALUES($1,$2::jsonb,$3)",c,t))
            {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(json);cmd.Parameters.AddWithValue(snapshotHash);await cmd.ExecuteNonQueryAsync(token);}
            foreach(var file in files)
            {await using var cmd=new NpgsqlCommand("INSERT INTO atlas_report_files(report_id,format,content,sha256) VALUES($1,$2,$3,$4)",c,t);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(file.Key);cmd.Parameters.AddWithValue(file.Value);cmd.Parameters.AddWithValue(ReportFormats.Hash(file.Value));await cmd.ExecuteNonQueryAsync(token);}
            await using(var cmd=new NpgsqlCommand("UPDATE atlas_report_jobs SET status='COMPLETE',progress=100,finished_at_utc=CURRENT_TIMESTAMP,lease_id=NULL WHERE id=$1",c,t)){cmd.Parameters.AddWithValue(id);await cmd.ExecuteNonQueryAsync(token);}
            await ConfigurationService.AuditAsync(c,t,author,"admin.report.generated","Report",id.ToString(),new{sha256=snapshotHash,rows=snapshot.Tables.Sum(x=>x.Rows.Count),snapshot.Filters},token);await t.CommitAsync(token);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            string code=ex is PortalException p?(p.Status==403?"OWNER_NOT_AUTHORIZED":"SCOPE_TOO_LARGE"):ex is OperationCanceledException?"TIMEOUT":"GENERATION_FAILED";
            await using var cmd=db.CreateCommand("UPDATE atlas_report_jobs SET status='FAILED',error_code=$1,finished_at_utc=CURRENT_TIMESTAMP WHERE id=$2 AND lease_id=$3 AND status='RUNNING'");cmd.Parameters.AddWithValue(code);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(lease);await cmd.ExecuteNonQueryAsync(ct);
            // Keep stack details in the server log, never in the report or browser.
            if(ex is not PortalException)throw;
        }
        return true;
    }
}

public sealed class ReportWorker(ReportService reports,NpgsqlDataSource db,ILogger<ReportWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reports.TickSchedulesAsync(stoppingToken);
                await using(var expired=db.CreateCommand("UPDATE atlas_report_jobs SET status='FAILED',error_code='RETRY_LIMIT',finished_at_utc=CURRENT_TIMESTAMP WHERE status='RUNNING' AND lease_until_utc<CURRENT_TIMESTAMP AND attempts>=3"))await expired.ExecuteNonQueryAsync(stoppingToken);
                if(await reports.WorkOneAsync(stoppingToken))continue;
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception ex){logger.LogError(ex,"No se pudo procesar la cola de reportes.");}
            await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);
        }
    }
}
