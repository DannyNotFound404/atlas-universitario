using System.Data;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Miguel.Server.Reports;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Configuration;

public sealed record GeneralSettings(string Institution = "ATLAS", string ReportTimeZone = "America/Guatemala",
    string Classification = "Uso interno", int DefaultPeriodDays = 30);
public sealed record SettingChange(int Revision, GeneralSettings Value, string Reason);

public sealed class ConfigurationService(NpgsqlDataSource db, IOptions<ServerOptions> options, TimeProvider clock)
{
    public async Task<(int Revision, GeneralSettings Value)> GeneralAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT revision,value::text FROM atlas_settings WHERE section='general'");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt32(0), PortalJson.Read<GeneralSettings>(r.GetString(1)))
            : (0, new GeneralSettings(ReportTimeZone: options.Value.OperationalTimeZoneId));
    }
    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new PortalException("Zona horaria no reconocida."); }
    }
    public static string Text(string? text, int max, string field)
    {
        string value = text?.Trim() ?? "";
        if (value.Length is 0 || value.Length > max || value.Any(c => char.IsControl(c)))
            throw new PortalException($"Revise {field} (máximo {max} caracteres).");
        return value;
    }
    public async Task SaveAsync(SettingChange change, string actor, CancellationToken ct)
    {
        var value = change.Value with {
            Institution = Text(change.Value.Institution, 160, "el nombre institucional"),
            Classification = Text(change.Value.Classification, 80, "la clasificación"),
            ReportTimeZone = Text(change.Value.ReportTimeZone, 100, "la zona horaria") };
        Zone(value.ReportTimeZone);
        if (value.DefaultPeriodDays is < 1 or > 366) throw new PortalException("El período debe ser de 1 a 366 días.");
        string reason = Text(change.Reason, 500, "el motivo");
        await using var c = await db.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(393201)", c, t)) await gate.ExecuteNonQueryAsync(ct);
        int revision = 0; string? previous = null;
        await using (var cmd = new NpgsqlCommand("SELECT revision,value::text FROM atlas_settings WHERE section='general' FOR UPDATE", c, t))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { revision = r.GetInt32(0); previous = r.GetString(1); }
        if (revision != change.Revision) throw new PortalException("Otro usuario cambió estos ajustes. Actualice antes de guardar.", 409);
        string json = PortalJson.Write(value);
        await using (var cmd = new NpgsqlCommand("""
            WITH saved AS (INSERT INTO atlas_settings(section,revision,value,updated_by) VALUES('general',$1,$2::jsonb,$3)
            ON CONFLICT(section) DO UPDATE SET revision=EXCLUDED.revision,value=EXCLUDED.value,updated_by=EXCLUDED.updated_by,updated_at_utc=CURRENT_TIMESTAMP RETURNING section)
            INSERT INTO atlas_setting_history(section,revision,before_value,after_value,reason,actor)
            VALUES('general',$1,$4::jsonb,$2::jsonb,$5,$3);
            """, c, t))
        {
            cmd.Parameters.AddWithValue(revision + 1); cmd.Parameters.AddWithValue(json); cmd.Parameters.AddWithValue(actor);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Text, Value=(object?)previous ?? DBNull.Value });
            cmd.Parameters.AddWithValue(reason); await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(c,t,actor,"admin.configuration.changed","Configuration","general",new { revision=revision+1, before=previous is null ? null : PortalJson.Read<GeneralSettings>(previous), after=value, reason },ct);
        await t.CommitAsync(ct);
    }
    public async Task<object> OverviewAsync(CancellationToken ct)
    {
        var general = await GeneralAsync(ct);
        var o = options.Value;
        var configuredRetention = await QueryAsync("SELECT value::text AS value FROM atlas_settings WHERE section='retention'",ct);
        var effectiveRetention = configuredRetention.Count==0 ? new RetentionSettings(o.SecurityEventRetentionDays,o.SecurityPostureRetentionDays,o.AdminNotificationRetentionDays)
            : PortalJson.Read<RetentionSettings>((string)configuredRetention[0]["value"]!);
        var stats = await QueryAsync("""
            SELECT (SELECT COUNT(*) FROM devices WHERE enabled AND retired_at_utc IS NULL) AS active_devices,
            (SELECT COUNT(*) FROM atlas_report_jobs WHERE status IN ('QUEUED','RUNNING')) AS report_queue,
            (SELECT COALESCE(SUM(octet_length(content)),0) FROM atlas_report_files) AS report_bytes,
            pg_database_size(current_database()) AS database_bytes,
            (SELECT MAX(applied_at_utc) FROM schema_migrations) AS migrated_at_utc
            """, ct);
        var acronis = await QueryAsync("SELECT MAX(report_at_utc) AS report_at_utc,MAX(reviewed_at_utc) AS reviewed_at_utc FROM acronis_report_imports",ct);
        return new { general = new { revision=general.Revision, value=general.Value, effective=true, origin=general.Revision==0 ? "SERVER_DEFAULT" : "PORTAL", restartRequired=false },
            server = new { version=typeof(ConfigurationService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                utc=clock.GetUtcNow(), databaseReady=true, stats=stats[0], operationalTimeZone=o.OperationalTimeZoneId,
                securityPostureFreshnessHours=Math.Clamp(o.SecurityPostureFreshnessHours,1,8760) },
            retention = new { securityEventsDays=effectiveRetention.EventsDays, securityPostureDays=effectiveRetention.PostureDays,
                notificationsDays=effectiveRetention.NotificationsDays, origin=configuredRetention.Count==0?"SERVER_STARTUP":"PORTAL", restartRequired=false,
                backupReference="Seis meses · Política V4, pp. 9–10", reportRetention="Conservación sin borrado automático; plazo documental pendiente de aprobación" },
            integrations = new[] {
                new { name="NAS", status="LOCAL", detail="Recursos y sondeos administrados en Almacenamiento NAS." },
                new { name="Acronis Cloud", status="MANUAL", detail="Importación y revisión de informes Excel disponibles. La API automática no está configurada; una carga conserva la fecha del informe y no es una consulta en tiempo real." },
                new { name="Correo institucional", status="PENDING", detail="Proveedor externo. Se requieren servidor, puerto y método de autenticación antes de habilitar avisos." },
                new { name="Active Directory", status="PENDING", detail="Directorio administrado por terceros. La federación requiere coordinación con sus administradores." } },
            acronis = new { importAvailable=true, automaticApiConfigured=false, lastReportAtUtc=acronis[0]["report_at_utc"],lastReviewAtUtc=acronis[0]["reviewed_at_utc"] },
            policy="V4 · aplicación anticipada; vigencia formal 1 de junio de 2027. Accesos: pp. 6, 13–15 y 23–26. Registros: pp. 20–22." };
    }
    public async Task<List<Dictionary<string,object?>>> QueryAsync(string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd=db.CreateCommand(sql);
        foreach(var arg in args) cmd.Parameters.AddWithValue(arg);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        List<Dictionary<string,object?>> rows=[];
        while(await r.ReadAsync(ct)) { Dictionary<string,object?> row=[]; for(int i=0;i<r.FieldCount;i++) row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i); rows.Add(row); }
        return rows;
    }
    public static async Task AuditAsync(NpgsqlConnection c,NpgsqlTransaction t,string actor,string action,string targetType,string target,object detail,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO audit_events(occurred_at_utc,actor_type,actor_id,action,target_type,target_id,detail)
            VALUES(CURRENT_TIMESTAMP,'Admin',$1,$2,$3,$4,$5::jsonb)
            """,c,t);
        foreach(var v in new[]{actor,action,targetType,target,PortalJson.Write(detail)})cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
