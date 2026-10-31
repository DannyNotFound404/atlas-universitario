using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Miguel.Server.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.BackupControl;

public sealed partial class AcronisImportService(NpgsqlDataSource db,BackupControlRepository controls,
    AdminRepository audit,IOptions<ServerOptions> options,TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AcronisImportSummary>> ListAsync(CancellationToken token)
    {
        List<AcronisImportSummary> items=[];
        await using var command=db.CreateCommand(Query+" ORDER BY i.created_at_utc DESC,i.id DESC LIMIT 30;");
        await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))items.Add(Read(reader).Summary);
        return items;
    }
    public async Task<AcronisImportDetail?> ReadAsync(Guid id,CancellationToken token)
    {
        await using var command=db.CreateCommand(Query+" WHERE i.id=$1;");command.Parameters.AddWithValue(id);
        AcronisImportDetail? detail;
        await using(var reader=await command.ExecuteReaderAsync(token))
            detail=await reader.ReadAsync(token)?Read(reader):null;
        return detail is null?null:detail with { Supplements=await SupplementHistoryAsync(id,token) };
    }
    private const string Query="""
        SELECT i.id,i.original_file_name,i.sha256,i.report_at_utc,i.date_offset_minutes,i.document::text,
            i.comparison::text,i.created_at_utc,i.created_by,i.reviewed_at_utc,i.reviewed_by,i.review_notes,
            COALESCE(o.rows,'{}'::integer[]),COALESCE(o.new_count,0),COALESCE(o.reused_count,0)
        FROM acronis_report_imports i LEFT JOIN LATERAL (
            SELECT array_agg(source_row ORDER BY source_row) rows,
                COUNT(*) FILTER(WHERE NOT reused)::integer new_count,
                COUNT(*) FILTER(WHERE reused)::integer reused_count
            FROM acronis_report_observations WHERE import_id=i.id
        ) o ON true
        """;
    private static AcronisImportDetail Read(NpgsqlDataReader reader)
    {
        var document=JsonSerializer.Deserialize<AcronisReportDocument>(reader.GetString(5),Json)!;
        var comparison=JsonSerializer.Deserialize<AcronisImportComparison>(reader.GetString(6),Json)!;
        return new(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt32(4),document.Rows.Count,document.Rows.Select(r=>AcronisImportRules.ShortName(r.DeviceName)).Distinct().Count(),
            comparison.Rows.Count(r=>r.Category=="MATCHED"),comparison.Rows.Count(r=>r.CanRegister),
            comparison.Rows.Count(r=>!r.CanRegister),comparison.NotListed.Count,reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetString(8),reader.IsDBNull(9)?null:reader.GetFieldValue<DateTimeOffset>(9),reader.IsDBNull(10)?null:reader.GetString(10),
            reader.GetInt32(13),reader.GetInt32(14)),document,comparison,reader.IsDBNull(11)?null:reader.GetString(11),reader.GetFieldValue<int[]>(12));
    }

    public async Task<AcronisImportDetail> UploadAsync(Stream source,string fileName,AcronisImportMetadata metadata,
        string actor,CancellationToken token)
    {
        DateTimeOffset now=clock.GetUtcNow();
        metadata=metadata with { ReportAtUtc=metadata.ReportAtUtc.ToUniversalTime() };
        // PostgreSQL timestamps retain microseconds; normalize once for stable duplicate checks.
        metadata=metadata with { ReportAtUtc=new DateTimeOffset(metadata.ReportAtUtc.Ticks/10*10,TimeSpan.Zero) };
        if(metadata.ReportAtUtc==default || metadata.ReportAtUtc>now.AddMinutes(5))
            throw new BackupWorkspaceException("Revisa la fecha de corte: no puede estar en el futuro.");
        if(string.IsNullOrWhiteSpace(fileName) || fileName.Length>255 || fileName.IndexOfAny(['/', '\\'])>=0 ||
            fileName.Any(char.IsControl) || !fileName.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))
            throw new BackupWorkspaceException("Selecciona la exportación .xlsx de Acronis.");
        byte[] content=await Bounded(source,options.Value.MaxBackupEvidenceUploadBytes,token);
        var document=AcronisReportParser.Parse(content,metadata.DateOffsetMinutes);
        string hash=Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var conn=await db.OpenConnectionAsync(token);
        await using var tx=await conn.BeginTransactionAsync(token);
        string? savedPath=null;bool commitStarted=false;
        try
        {
            await using(var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1));",conn,tx))
            {guard.Parameters.AddWithValue("acronis-report:"+hash);await guard.ExecuteNonQueryAsync(token);}
            Guid? duplicate;
            await using(var existing=new NpgsqlCommand("SELECT id,report_at_utc,date_offset_minutes FROM acronis_report_imports WHERE sha256=$1;",conn,tx))
            {
                existing.Parameters.AddWithValue(hash);await using var r=await existing.ExecuteReaderAsync(token);
                duplicate=await r.ReadAsync(token)?r.GetGuid(0):null;
                if(duplicate.HasValue && (r.GetFieldValue<DateTimeOffset>(1)!=metadata.ReportAtUtc || r.GetInt32(2)!=metadata.DateOffsetMinutes))
                    throw new BackupWorkspaceException("Este archivo ya está registrado con otra fecha o zona horaria. Consulta su carga original.",true);
            }
            if(duplicate.HasValue)
            {await tx.CommitAsync(token);return (await ReadAsync(duplicate.Value,token))! with { DuplicateUpload=true };}
            var comparison=AcronisImportRules.Compare(document,await Devices(conn,tx,metadata.ReportAtUtc,token),metadata.ReportAtUtc,now);
            Guid id=Guid.NewGuid();string relative=$"acronis-reports/{id:D}.xlsx";
            savedPath=PathFor(relative);Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
            await File.WriteAllBytesAsync(savedPath,content,token);
            await using var insert=new NpgsqlCommand("""
                INSERT INTO acronis_report_imports(id,original_file_name,stored_relative_path,size_bytes,sha256,
                    report_at_utc,date_offset_minutes,document,comparison,created_at_utc,created_by)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);
                """,conn,tx);
            insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(fileName);insert.Parameters.AddWithValue(relative);
            insert.Parameters.AddWithValue((long)content.Length);insert.Parameters.AddWithValue(hash);insert.Parameters.AddWithValue(metadata.ReportAtUtc);
            insert.Parameters.AddWithValue(metadata.DateOffsetMinutes);insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(document,Json));
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(comparison,Json));insert.Parameters.AddWithValue(now);insert.Parameters.AddWithValue(actor);
            await insert.ExecuteNonQueryAsync(token);
            await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.acronis.imported","AcronisReport",id.ToString(),
                new { FileName=fileName,Sha256=hash,metadata.ReportAtUtc,metadata.DateOffsetMinutes,Rows=document.Rows.Count,
                    Matches=comparison.Rows.Count(r=>r.Category=="MATCHED"),NotListed=comparison.NotListed.Count },token,tx);
            commitStarted=true;await tx.CommitAsync(token);
            return (await ReadAsync(id,token))!;
        }
        catch
        {
            if(!commitStarted && savedPath is not null)
                try{File.Delete(savedPath);}catch(IOException){}catch(UnauthorizedAccessException){}
            throw;
        }
    }

    public async Task<AcronisImportReviewResult> ReviewAsync(Guid id,AcronisImportReview request,string actor,CancellationToken token)
    {
        if(request.SourceRows is null || request.SourceRows.Count>500 || request.SourceRows.Any(n=>n<1) ||
            request.SourceRows.Distinct().Count()!=request.SourceRows.Count || request.Notes?.Length>4000 ||
            (request.SourceRows.Count>0 && !request.PlansReviewed))
            throw new BackupWorkspaceException("Selecciona hasta 500 filas distintas y confirma que revisaste sus planes y alcances.");
        request=request with { SourceRows=request.SourceRows.Order().ToArray(),Notes=request.Notes?.Trim() };
        if(await FileAsync(id,token) is null)throw new BackupWorkspaceException("La evidencia original no está disponible para revisar este informe.",true);
        string fingerprint=AcronisImportRules.Hash(JsonSerializer.Serialize(request,Json));
        DateTimeOffset now=clock.GetUtcNow();
        await using var conn=await db.OpenConnectionAsync(token);await using var tx=await conn.BeginTransactionAsync(token);
        AcronisImportComparison comparison;DateTimeOffset reportAt;
        await using(var report=new NpgsqlCommand("SELECT comparison::text,report_at_utc,review_fingerprint FROM acronis_report_imports WHERE id=$1 FOR UPDATE;",conn,tx))
        {
            report.Parameters.AddWithValue(id);await using var r=await report.ExecuteReaderAsync(token);
            if(!await r.ReadAsync(token))throw new BackupWorkspaceException("El informe no existe.",true);
            comparison=JsonSerializer.Deserialize<AcronisImportComparison>(r.GetString(0),Json)!;reportAt=r.GetFieldValue<DateTimeOffset>(1);
            if(!r.IsDBNull(2))
            {
                if(r.GetString(2)!=fingerprint)throw new BackupWorkspaceException("Este informe ya fue revisado con otra selección. Su revisión se conserva.",true);
                await r.DisposeAsync();await tx.CommitAsync(token);
                return await LinkedCountsAsync(id,request.SourceRows,conn,null,token);
            }
        }
        var selected=request.SourceRows.Select(n=>comparison.Rows.SingleOrDefault(r=>r.Source.RowNumber==n)).ToArray();
        if(selected.Any(r=>r is null || !r.CanRegister || r.ProfileId is null) ||
            selected.Select(r=>r!.ProfileId).Distinct().Count()!=selected.Length)
            throw new BackupWorkspaceException("Selecciona solo coincidencias verificables, con un control diferente por fila.");
        var profileIds=selected.Select(r=>r!.ProfileId!.Value).Order().ToArray();
        // Freeze the selected policy and custody while validating the original comparison.
        foreach(string sql in new[] {
            "SELECT id FROM devices WHERE id=ANY($1) ORDER BY id FOR SHARE;",
            "SELECT current_device_id FROM asset_custody WHERE current_device_id=ANY($1) ORDER BY current_device_id FOR SHARE;" })
        {
            await using var guard=new NpgsqlCommand(sql,conn,tx);guard.Parameters.AddWithValue(selected.Select(r=>r!.DeviceId!.Value).ToArray());
            await guard.ExecuteNonQueryAsync(token);
        }
        await using(var guard=new NpgsqlCommand("SELECT id FROM backup_control_profiles WHERE id=ANY($1) ORDER BY id FOR UPDATE;",conn,tx))
        {guard.Parameters.AddWithValue(profileIds);await guard.ExecuteNonQueryAsync(token);}
        var current=AcronisImportRules.Compare(new("Revisión",selected.Select(r=>r!.Source).ToArray()),await Devices(conn,tx,reportAt,token),reportAt,now);
        foreach(var row in selected)
        {
            var fresh=current.Rows.Single(r=>r.Source.RowNumber==row!.Source.RowNumber);
            if(!fresh.CanRegister || fresh.DeviceId!=row!.DeviceId || fresh.ProfileId!=row.ProfileId || fresh.BindingStamp!=row.BindingStamp)
                throw new BackupWorkspaceException("Un activo, control o custodia cambió desde la comparación. Carga un nuevo informe para revisar su estado actual.",true);
        }
        var saved=await RegisterRowsAsync(id,selected.Select(r=>r!),actor,now,conn,tx,token);
        int registered=saved.Registered,reused=saved.Reused;
        await using(var done=new NpgsqlCommand("UPDATE acronis_report_imports SET reviewed_at_utc=$2,reviewed_by=$3,review_fingerprint=$4,review_notes=$5 WHERE id=$1;",conn,tx))
        {
            done.Parameters.AddWithValue(id);done.Parameters.AddWithValue(now);done.Parameters.AddWithValue(actor);done.Parameters.AddWithValue(fingerprint);
            done.Parameters.AddWithValue(NpgsqlDbType.Varchar,(object?)request.Notes??DBNull.Value);await done.ExecuteNonQueryAsync(token);
        }
        await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.acronis.reviewed","AcronisReport",id.ToString(),
            new { request,Registered=registered,Reused=reused },token,tx);
        await tx.CommitAsync(token);return new(registered,reused);
    }

    private async Task<AcronisImportReviewResult> RegisterRowsAsync(Guid id,IEnumerable<AcronisComparisonRow> selected,string actor,DateTimeOffset now,NpgsqlConnection conn,NpgsqlTransaction tx,CancellationToken token)
    {
        int registered=0,reused=0;
        foreach(var row in selected.Select(r=>r!))
        {
            var copyAt=row.Source.LastSuccessfulBackupAtUtc!.Value.ToUniversalTime();
            string reference="acronis-report-copy:"+AcronisImportRules.Hash(JsonSerializer.Serialize(new {
                Device=AcronisImportRules.Name(row.Source.DeviceName),Plan=row.Source.PlanName.Trim().ToUpperInvariant(),CopyAt=copyAt }));
            Guid? observation;
            await using(var existing=new NpgsqlCommand("SELECT id FROM backup_control_observations WHERE profile_id=$1 AND source='ACRONIS_EVIDENCE' AND external_reference=$2 ORDER BY created_at_utc LIMIT 1;",conn,tx))
            {existing.Parameters.AddWithValue(row.ProfileId!.Value);existing.Parameters.AddWithValue(reference);observation=await existing.ExecuteScalarAsync(token) is Guid prior?prior:null;}
            bool wasReused=observation.HasValue;
            if(!wasReused)
            {
                string detail=$"Informe general Acronis {id:D}. Plan: {row.Source.PlanName}. Último resultado: {row.Source.LastResult}. " +
                    $"Última copia correcta: {row.Source.LastSuccessfulBackupRaw}. Ejecuciones: {row.Source.TotalRuns}; correctas: {row.Source.SuccessfulRuns}. " +
                    "La fecha corresponde a la copia, no a la carga. El informe no detalla carpetas, destino por equipo ni prueba de restauración.";
                if(row.RunCountNote is not null)detail+=" "+row.RunCountNote;
                if(detail.Length>1800)detail=detail[..1800];
                var input=new BackupControlObservationRequest("ACRONIS_EVIDENCE","SUCCESS",copyAt,copyAt,null,null,reference,detail);
                if(!BackupControlInput.TryNormalize(input,out input))throw new BackupWorkspaceException("La fila no se puede convertir en evidencia de copia.");
                observation=await controls.AddObservationAsync(row.ProfileId!.Value,input,actor,now,token,tx)
                    ?? throw new BackupWorkspaceException("No se pudo conservar una copia comprobada.",true);
                registered++;
            }
            else reused++;
            await using var link=new NpgsqlCommand("INSERT INTO acronis_report_observations(import_id,source_row,profile_id,observation_id,reused) VALUES($1,$2,$3,$4,$5);",conn,tx);
            link.Parameters.AddWithValue(id);link.Parameters.AddWithValue(row.Source.RowNumber);link.Parameters.AddWithValue(row.ProfileId!.Value);
            link.Parameters.AddWithValue(observation!.Value);link.Parameters.AddWithValue(wasReused);await link.ExecuteNonQueryAsync(token);
        }
        return new(registered,reused);
    }

    private async Task<IReadOnlyList<AcronisAtlasDevice>> Devices(NpgsqlConnection conn,NpgsqlTransaction? tx,DateTimeOffset reportAt,CancellationToken token)
    {
        await using var command=new NpgsqlCommand("""
            SELECT d.id,d.machine_name,d.agency_code,d.backup_provider,d.enabled,
                COALESCE(c.backups_paused,false),c.tracking_since_utc,
                p.id,p.provider,p.enabled,p.approval_status,p.task_name,p.protected_scope,p.expected_destination,
                p.frequency_hours,p.grace_minutes,p.updated_at_utc,p.plan_id
            FROM devices d LEFT JOIN asset_custody c ON c.current_device_id=d.id
            LEFT JOIN backup_control_profiles p ON p.device_id=d.id
            ORDER BY d.id,p.id;
            """,conn,tx);
        await using var reader=await command.ExecuteReaderAsync(token);Dictionary<Guid,AcronisAtlasDevice> devices=[];
        List<(Guid Id,Guid? PlanId,string AgencyCode)> scheduleTargets=[];
        while(await reader.ReadAsync(token))
        {
            Guid id=reader.GetGuid(0);
            if(!devices.TryGetValue(id,out var device))
            {device=new(id,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetBoolean(4),reader.GetBoolean(5),
                reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),[]);devices.Add(id,device);}
            if(!reader.IsDBNull(7))
            {
                scheduleTargets.Add((reader.GetGuid(7),reader.IsDBNull(17)?null:reader.GetGuid(17),reader.GetString(2)));
                devices[id]=device with { Profiles=device.Profiles.Append(new(reader.GetGuid(7),reader.GetString(8),
                    reader.GetBoolean(9),reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13),
                    reader.GetInt32(14),reader.GetInt32(15),reader.GetFieldValue<DateTimeOffset>(16))).ToArray() };
            }
        }
        await reader.DisposeAsync();
        var schedules=await new BackupScheduleService(db,clock).ResolveSettingsAsync(scheduleTargets,reportAt,token);
        foreach(var device in devices.Values.ToArray()) devices[device.Id]=device with{Profiles=device.Profiles
            .Select(p=>p with{Schedule=schedules.GetValueOrDefault(p.Id)}).ToArray()};
        return devices.Values.ToArray();
    }

    public async Task<BackupStoredEvidence?> FileAsync(Guid id,CancellationToken token)
    {
        await using var command=db.CreateCommand("SELECT stored_relative_path,original_file_name,sha256 FROM acronis_report_imports WHERE id=$1;");
        command.Parameters.AddWithValue(id);await using var r=await command.ExecuteReaderAsync(token);
        if(!await r.ReadAsync(token))return null;
        string path=PathFor(r.GetString(0));if(!File.Exists(path))return null;
        await using var stream=File.OpenRead(path);
        if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,token)).Equals(r.GetString(2),StringComparison.OrdinalIgnoreCase))
            throw new BackupWorkspaceException("El archivo conservado no coincide con su hash de evidencia.",true);
        return new(path,r.GetString(1),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }
    private string PathFor(string relative)
    {
        string root=Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.BackupEvidencePath)?"/var/lib/miguel/backup-evidence":options.Value.BackupEvidencePath);
        if(root==Path.GetPathRoot(root))throw new InvalidOperationException("El almacenamiento de evidencia debe tener una carpeta específica.");
        string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("La evidencia debe permanecer dentro del almacenamiento configurado.");
        return full;
    }
    private static async Task<byte[]> Bounded(Stream source,long maximum,CancellationToken token)
    {
        using var memory=new MemoryStream();byte[] buffer=new byte[81920];int read;
        while((read=await source.ReadAsync(buffer,token))>0)
        {if(memory.Length+read>maximum)throw new BackupWorkspaceException("El Excel supera el límite de evidencia permitido.");memory.Write(buffer,0,read);}
        if(memory.Length==0)throw new BackupWorkspaceException("El archivo está vacío.");return memory.ToArray();
    }
}
