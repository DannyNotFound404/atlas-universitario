using Microsoft.Extensions.Options;
using Miguel.Server.Reports;
using Npgsql;

namespace Miguel.Server.Configuration;

public sealed record RetentionSettings(int EventsDays,int PostureDays,int NotificationsDays);
public sealed record RetentionChange(int Revision,RetentionSettings Value,string Reason);
public sealed record HoldRequest(Guid DeviceId,Guid CaseId,DateTimeOffset FromUtc,DateTimeOffset ToUtc,string Reason);
public sealed record HoldRelease(string Reason);
public sealed record MuteRequest(string Category,DateTimeOffset UntilUtc,string Reason);
public sealed class OperationalSettingsService(NpgsqlDataSource db,IOptions<ServerOptions> options,ConfigurationService configuration,TimeProvider clock)
{
    public async Task<(int Revision,RetentionSettings Value)> RetentionAsync(CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT revision,value::text FROM atlas_settings WHERE section='retention'");await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?(r.GetInt32(0),PortalJson.Read<RetentionSettings>(r.GetString(1))):(0,new(options.Value.SecurityEventRetentionDays,options.Value.SecurityPostureRetentionDays,options.Value.AdminNotificationRetentionDays));
    }
    private static void Validate(RetentionChange change)
    {
        if(change.Value.EventsDays is <30 or >730 || change.Value.PostureDays is <90 or >1825 || change.Value.NotificationsDays is <30 or >730)throw new PortalException("Eventos: 30–730 días; postura: 90–1825; notificaciones: 30–730.");
        ConfigurationService.Text(change.Reason,500,"el motivo");
    }
    public async Task<object> PreviewAsync(RetentionChange change,CancellationToken ct)
    {
        Validate(change);var current=await RetentionAsync(ct);
        var rows=await configuration.QueryAsync("""
            SELECT (SELECT COUNT(*) FROM endpoint_security_events e WHERE occurred_at_utc<CURRENT_TIMESTAMP-make_interval(days=>$1) AND NOT EXISTS(SELECT 1 FROM atlas_evidence_holds h WHERE h.device_id=e.device_id AND h.released_at_utc IS NULL AND e.occurred_at_utc>=h.from_utc AND e.occurred_at_utc<h.to_utc)) AS eligible_events,
            (SELECT COUNT(*) FROM security_posture_snapshots s WHERE captured_at_utc<CURRENT_TIMESTAMP-make_interval(days=>$2) AND NOT EXISTS(SELECT 1 FROM atlas_evidence_holds h WHERE h.device_id=s.device_id AND h.released_at_utc IS NULL AND s.captured_at_utc>=h.from_utc AND s.captured_at_utc<h.to_utc)) AS eligible_postures,
            (SELECT COUNT(*) FROM admin_notifications WHERE detected_at_utc<CURRENT_TIMESTAMP-make_interval(days=>$3)) AS eligible_notifications
            """,ct,change.Value.EventsDays,change.Value.PostureDays,change.Value.NotificationsDays);
        return new{current=new{revision=current.Revision,value=current.Value},proposed=change.Value,eligible=rows[0],effect="La próxima limpieza automática utiliza estos plazos. Las cifras pueden variar al recibirse nuevos datos. Los informes emitidos y la evidencia de expedientes no se eliminan."};
    }
    public async Task SaveRetentionAsync(RetentionChange change,string actor,CancellationToken ct)
    {
        Validate(change);await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393206)",c,t))await gate.ExecuteNonQueryAsync(ct);
        int revision=0;string? before=null;
        await using(var cmd=new NpgsqlCommand("SELECT revision,value::text FROM atlas_settings WHERE section='retention' FOR UPDATE",c,t))
        await using(var r=await cmd.ExecuteReaderAsync(ct))if(await r.ReadAsync(ct)){revision=r.GetInt32(0);before=r.GetString(1);}
        if(change.Revision!=revision)throw new PortalException("La retención cambió. Revise de nuevo el impacto.",409);
        string json=PortalJson.Write(change.Value);
        await using(var cmd=new NpgsqlCommand("""
            WITH saved AS (INSERT INTO atlas_settings(section,revision,value,updated_by) VALUES('retention',$1,$2::jsonb,$3)
            ON CONFLICT(section) DO UPDATE SET revision=$1,value=$2::jsonb,updated_by=$3,updated_at_utc=CURRENT_TIMESTAMP RETURNING section)
            INSERT INTO atlas_setting_history(section,revision,before_value,after_value,reason,actor) VALUES('retention',$1,$4::jsonb,$2::jsonb,$5,$3)
            """,c,t))
        {cmd.Parameters.AddWithValue(revision+1);cmd.Parameters.AddWithValue(json);cmd.Parameters.AddWithValue(actor);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)before??DBNull.Value});cmd.Parameters.AddWithValue(change.Reason.Trim());await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.configuration.retention.changed","Configuration","retention",new{revision=revision+1,change.Value,change.Reason},ct);await t.CommitAsync(ct);
    }
    public async Task HoldAsync(HoldRequest request,string actor,CancellationToken ct)
    {
        string reason=ConfigurationService.Text(request.Reason,500,"el motivo");
        if(request.FromUtc>=request.ToUtc || request.FromUtc.Year<2000 || request.ToUtc-request.FromUtc>TimeSpan.FromDays(1825))throw new PortalException("Indique un intervalo de conservación de hasta cinco años.");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393206)",c,t))await gate.ExecuteNonQueryAsync(ct);
        Guid id=Guid.NewGuid();await using(var cmd=new NpgsqlCommand("""
            INSERT INTO atlas_evidence_holds(id,device_id,case_id,from_utc,to_utc,reason,created_by)
            SELECT $1,$2,$3,$4,$5,$6,$7 WHERE EXISTS(SELECT 1 FROM devices WHERE id=$2) AND EXISTS(SELECT 1 FROM operational_cases WHERE id=$3)
            """,c,t))
        {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(request.DeviceId);cmd.Parameters.AddWithValue(request.CaseId);cmd.Parameters.AddWithValue(request.FromUtc.ToUniversalTime());cmd.Parameters.AddWithValue(request.ToUtc.ToUniversalTime());cmd.Parameters.AddWithValue(reason);cmd.Parameters.AddWithValue(actor);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("Equipo o expediente no disponible.");}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.evidence.hold.created","EvidenceHold",id.ToString(),request,ct);await t.CommitAsync(ct);
    }
    public async Task ReleaseHoldAsync(Guid id,HoldRelease value,string actor,CancellationToken ct)
    {
        var reason=ConfigurationService.Text(value.Reason,500,"el motivo de liberación");await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(393206)",c,t))await gate.ExecuteNonQueryAsync(ct);
        await using(var cmd=new NpgsqlCommand("UPDATE atlas_evidence_holds SET released_at_utc=CURRENT_TIMESTAMP,released_by=$1,release_reason=$2 WHERE id=$3 AND released_at_utc IS NULL",c,t))
        {cmd.Parameters.AddWithValue(actor);cmd.Parameters.AddWithValue(reason);cmd.Parameters.AddWithValue(id);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("La conservación ya fue liberada o no existe.",409);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.evidence.hold.released","EvidenceHold",id.ToString(),new{reason},ct);await t.CommitAsync(ct);
    }
    public async Task MuteAsync(MuteRequest value,string actor,CancellationToken ct)
    {
        var reason=ConfigurationService.Text(value.Reason,500,"el motivo");if(value.Category is not("BACKUP" or "BACKUP_CONTROL" or "POSTURE" or "SECURITY" or "SOFTWARE" or "ASSET_CUSTODY") || value.UntilUtc<=clock.GetUtcNow() || value.UntilUtc>clock.GetUtcNow().AddDays(7))throw new PortalException("Elija una categoría y una fecha futura dentro de siete días.");
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);Guid id=Guid.NewGuid();
        await using(var cmd=new NpgsqlCommand("INSERT INTO atlas_notification_mutes(id,category,until_utc,reason,created_by) VALUES($1,$2,$3,$4,$5)",c,t))
        {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(value.Category);cmd.Parameters.AddWithValue(value.UntilUtc.ToUniversalTime());cmd.Parameters.AddWithValue(reason);cmd.Parameters.AddWithValue(actor);await cmd.ExecuteNonQueryAsync(ct);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.notification.muted","NotificationMute",id.ToString(),new{value.Category,value.UntilUtc,reason,scope="INSTITUTION",resolutionChanged=false},ct);await t.CommitAsync(ct);
    }
    public async Task UnmuteAsync(Guid id,string actor,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);await using(var cmd=new NpgsqlCommand("UPDATE atlas_notification_mutes SET released_at_utc=CURRENT_TIMESTAMP WHERE id=$1 AND released_at_utc IS NULL",c,t)){cmd.Parameters.AddWithValue(id);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new PortalException("El silencio ya terminó.",409);}
        await ConfigurationService.AuditAsync(c,t,actor,"admin.notification.unmuted","NotificationMute",id.ToString(),new{},ct);await t.CommitAsync(ct);
    }
    public async Task<object> OverviewAsync(CancellationToken ct)
    {
        var retention=await RetentionAsync(ct);return new{retention=new{revision=retention.Revision,value=retention.Value},
            holds=await configuration.QueryAsync("SELECT h.id,d.machine_name,c.case_number,h.from_utc,h.to_utc,h.reason,h.created_by,h.released_at_utc FROM atlas_evidence_holds h JOIN devices d ON d.id=h.device_id JOIN operational_cases c ON c.id=h.case_id ORDER BY h.created_at_utc DESC LIMIT 200",ct),
            mutes=await configuration.QueryAsync("SELECT id,category,until_utc,reason,created_by,released_at_utc FROM atlas_notification_mutes WHERE until_utc>CURRENT_TIMESTAMP ORDER BY until_utc",ct),
            devices=await configuration.QueryAsync("SELECT id,machine_name,agency_code FROM devices ORDER BY machine_name,id",ct),
            cases=await configuration.QueryAsync("SELECT id,case_number,title FROM operational_cases WHERE status<>'CLOSED' ORDER BY case_number",ct)};
    }
}
