using System.Text.Json;
using Npgsql;

namespace Miguel.Server.BackupControl;

public sealed partial class AcronisImportService
{
    private const string SupplementAction="admin.backup_control.acronis.supplemented";
    private sealed record SupplementAudit(Guid RequestId,string Fingerprint,int[] SourceRows,int Registered,int Reused,string? Notes);

    public async Task<AcronisSupplementPreview> SupplementPreviewAsync(Guid id,CancellationToken token)
    {
        if(await FileAsync(id,token) is null)throw new BackupWorkspaceException("La evidencia original no está disponible.",true);
        await using var connection=await db.OpenConnectionAsync(token);
        var report=await SnapshotAsync(id,connection,null,false,token);
        return SupplementPreview(report,await Devices(connection,null,report.Summary.ReportAtUtc,token),clock.GetUtcNow());
    }

    public async Task<AcronisImportReviewResult> SupplementAsync(Guid id,AcronisSupplementRequest request,string actor,CancellationToken token)
    {
        if(request.RequestId==Guid.Empty || request.ExpectedStamp is not { Length:64 } || request.SourceRows is null ||
            request.SourceRows.Count is <1 or >500 || request.SourceRows.Any(n=>n<1) ||
            request.SourceRows.Distinct().Count()!=request.SourceRows.Count || !request.PlansReviewed || request.Notes?.Length>4000)
            throw new BackupWorkspaceException("Selecciona copias pendientes, confirma sus planes y conserva la referencia de esta revisión.");
        request=request with { SourceRows=request.SourceRows.Order().ToArray(),Notes=request.Notes?.Trim() };
        string fingerprint=AcronisImportRules.Hash(JsonSerializer.Serialize(request,Json));
        if(await FileAsync(id,token) is null)throw new BackupWorkspaceException("La evidencia original no está disponible.",true);
        var now=clock.GetUtcNow();
        await using var connection=await db.OpenConnectionAsync(token);await using var transaction=await connection.BeginTransactionAsync(token);
        var report=await SnapshotAsync(id,connection,transaction,true,token);
        if(report.Summary.ReviewedAtUtc is null)throw new BackupWorkspaceException("Guarda primero la revisión inicial del informe.",true);
        await using(var prior=new NpgsqlCommand("""
            SELECT detail::text FROM audit_events WHERE action=$1 AND target_type='AcronisReport'
              AND target_id=$2 AND detail->>'requestId'=$3 ORDER BY id LIMIT 1;
            """,connection,transaction))
        {
            prior.Parameters.AddWithValue(SupplementAction);prior.Parameters.AddWithValue(id.ToString());prior.Parameters.AddWithValue(request.RequestId.ToString());
            if(await prior.ExecuteScalarAsync(token) is string json)
            {
                var saved=JsonSerializer.Deserialize<SupplementAudit>(json,Json)!;
                if(saved.Fingerprint!=fingerprint)throw new BackupWorkspaceException("Esta solicitud ya se guardó con otra selección. Su evidencia se conserva.",true);
                await transaction.CommitAsync(token);return new(saved.Registered,saved.Reused);
            }
        }
        var preview=SupplementPreview(report,await Devices(connection,transaction,report.Summary.ReportAtUtc,token),now);
        if(preview.Stamp!=request.ExpectedStamp)throw new BackupWorkspaceException("La configuración o las copias pendientes cambiaron. Abre nuevamente la revisión complementaria.",true);
        var selected=request.SourceRows.Select(n=>preview.Comparison.Rows.SingleOrDefault(r=>r.Source.RowNumber==n)).ToArray();
        if(selected.Any(r=>r is null || !r.CanRegister || r.ProfileId is null) || selected.Select(r=>r!.ProfileId).Distinct().Count()!=selected.Length)
            throw new BackupWorkspaceException("Selecciona únicamente copias pendientes verificables, con un control diferente por fila.");
        foreach(string sql in new[]{
            "SELECT id FROM devices WHERE id=ANY($1) ORDER BY id FOR SHARE;",
            "SELECT current_device_id FROM asset_custody WHERE current_device_id=ANY($1) ORDER BY current_device_id FOR SHARE;"})
        {
            await using var guard=new NpgsqlCommand(sql,connection,transaction);
            guard.Parameters.AddWithValue(selected.Select(r=>r!.DeviceId!.Value).ToArray());await guard.ExecuteNonQueryAsync(token);
        }
        await using(var guard=new NpgsqlCommand("SELECT id FROM backup_control_profiles WHERE id=ANY($1) ORDER BY id FOR UPDATE;",connection,transaction))
        {guard.Parameters.AddWithValue(selected.Select(r=>r!.ProfileId!.Value).Order().ToArray());await guard.ExecuteNonQueryAsync(token);}
        var current=SupplementPreview(report,await Devices(connection,transaction,report.Summary.ReportAtUtc,token),now);
        if(current.Stamp!=request.ExpectedStamp)throw new BackupWorkspaceException("Un control o custodia cambió durante la revisión. Abre nuevamente el informe.",true);
        var result=await RegisterRowsAsync(id,selected.Select(r=>r!),actor,now,connection,transaction,token);
        await audit.WriteAuditAsync(now,"Admin",actor,SupplementAction,"AcronisReport",id.ToString(),new {
            request.RequestId,Fingerprint=fingerprint,request.SourceRows,request.PlansReviewed,request.Notes,
            result.Registered,result.Reused,request.ExpectedStamp,OriginalReviewAtUtc=report.Summary.ReviewedAtUtc,Sha256=report.Summary.Sha256
        },token,transaction);
        await transaction.CommitAsync(token);return result;
    }

    private static AcronisSupplementPreview SupplementPreview(AcronisImportDetail report,IReadOnlyList<AcronisAtlasDevice> devices,DateTimeOffset now)
    {
        if(report.Summary.ReviewedAtUtc is null)throw new BackupWorkspaceException("Guarda primero la revisión inicial del informe.",true);
        var comparison=AcronisImportRules.Compare(report.Document,devices,report.Summary.ReportAtUtc,now);
        comparison=comparison with { Rows=comparison.Rows.Select(row=>{
            if(report.RegisteredRows.Contains(row.Source.RowNumber))return row with { CanRegister=false,Detail="Esta copia ya está documentada en el informe." };
            var original=report.Comparison.Rows.SingleOrDefault(r=>r.Source.RowNumber==row.Source.RowNumber);
            if(row.CanRegister && (original is null || original.DeviceId!=row.DeviceId || original.ProfileId!=row.ProfileId || original.BindingStamp!=row.BindingStamp))
                return row with { CanRegister=false,Category="CONTROL_CHANGED",Detail="El activo o control cambió desde la carga original. Usa un nuevo informe para ese alcance." };
            return row;
        }).ToArray() };
        // ComparedAtUtc is display metadata, not a changing part of the approval stamp.
        string stamp=AcronisImportRules.Hash(JsonSerializer.Serialize(new { report.Summary.Id,report.Summary.Sha256,
            report.Summary.ReportAtUtc,report.RegisteredRows,comparison.Rows,comparison.NotListed },Json));
        return new(report.Summary.Id,comparison,report.RegisteredRows,stamp);
    }

    private static async Task<AcronisImportDetail> SnapshotAsync(Guid id,NpgsqlConnection connection,NpgsqlTransaction? transaction,bool locked,CancellationToken token)
    {
        await using var command=new NpgsqlCommand(Query+" WHERE i.id=$1"+(locked?" FOR UPDATE OF i":"")+";",connection,transaction);
        command.Parameters.AddWithValue(id);await using var reader=await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)?Read(reader):throw new BackupWorkspaceException("El informe no existe.",true);
    }

    private static async Task<AcronisImportReviewResult> LinkedCountsAsync(Guid id,IReadOnlyList<int> sourceRows,NpgsqlConnection connection,NpgsqlTransaction? transaction,CancellationToken token)
    {
        await using var command=new NpgsqlCommand("""
            SELECT COUNT(*) FILTER(WHERE NOT reused)::integer,COUNT(*) FILTER(WHERE reused)::integer
            FROM acronis_report_observations WHERE import_id=$1 AND source_row=ANY($2);
            """,connection,transaction);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(sourceRows.ToArray());await using var reader=await command.ExecuteReaderAsync(token);await reader.ReadAsync(token);
        return new(reader.GetInt32(0),reader.GetInt32(1));
    }

    private async Task<IReadOnlyList<AcronisSupplementHistory>> SupplementHistoryAsync(Guid id,CancellationToken token)
    {
        await using var command=db.CreateCommand("""
            SELECT occurred_at_utc,actor_id,detail::text FROM audit_events
            WHERE action=$1 AND target_type='AcronisReport' AND target_id=$2 ORDER BY occurred_at_utc,id;
            """);
        command.Parameters.AddWithValue(SupplementAction);command.Parameters.AddWithValue(id.ToString());await using var reader=await command.ExecuteReaderAsync(token);
        List<AcronisSupplementHistory> history=[];
        while(await reader.ReadAsync(token))
        {
            var item=JsonSerializer.Deserialize<SupplementAudit>(reader.GetString(2),Json)!;
            history.Add(new(reader.GetFieldValue<DateTimeOffset>(0),reader.GetString(1),item.SourceRows,item.Registered,item.Reused,item.Notes));
        }
        return history;
    }
}
