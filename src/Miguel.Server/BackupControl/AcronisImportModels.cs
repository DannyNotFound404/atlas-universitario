using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miguel.Server.BackupControl;

public sealed record AcronisReportRow(int RowNumber,string DeviceName,string PlanName,string LastResult,
    string DeviceState,string LastSuccessfulBackupRaw,DateTimeOffset? LastSuccessfulBackupAtUtc,
    int? TotalRuns,int? SuccessfulRuns,IReadOnlyList<string> Issues);
public sealed record AcronisReportDocument(string SheetName,IReadOnlyList<AcronisReportRow> Rows);
public sealed record AcronisImportMetadata(DateTimeOffset ReportAtUtc,int DateOffsetMinutes);
public sealed record AcronisImportReview(IReadOnlyList<int> SourceRows,bool PlansReviewed,string? Notes=null);
public sealed record AcronisAtlasProfile(Guid Id,string Provider,bool Enabled,string ApprovalStatus,
    string? TaskName,string ProtectedScope,string? ExpectedDestination,int FrequencyHours,int GraceMinutes,DateTimeOffset UpdatedAtUtc,
    BackupScheduleSetting? Schedule=null);
public sealed record AcronisAtlasDevice(Guid Id,string Name,string Agency,string Provider,bool Enabled,
    bool BackupsPaused,DateTimeOffset? TrackingSinceUtc,IReadOnlyList<AcronisAtlasProfile> Profiles)
{
    public string BindingStamp(AcronisAtlasProfile profile)=>AcronisImportRules.Hash(JsonSerializer.Serialize(
        new { Id,Name,Agency,Provider,Enabled,BackupsPaused,TrackingSinceUtc,Profile=profile }));
}
public sealed record AcronisComparisonRow(AcronisReportRow Source,string Category,string Detail,
    Guid? DeviceId=null,string? AtlasName=null,string? Agency=null,string? AtlasProvider=null,
    Guid? ProfileId=null,string? BindingStamp=null,bool CanRegister=false,string? EvidenceState=null,
    string? TaskName=null,string? ProtectedScope=null,string? RunCountNote=null);
public sealed record AcronisUnlistedDevice(Guid Id,string Name,string Agency);
public sealed record AcronisImportComparison(DateTimeOffset ComparedAtUtc,IReadOnlyList<AcronisComparisonRow> Rows,
    IReadOnlyList<AcronisUnlistedDevice> NotListed,int AtlasAcronisDevices);
public sealed record AcronisImportSummary(Guid Id,string FileName,string Sha256,DateTimeOffset ReportAtUtc,
    int DateOffsetMinutes,int Rows,int DistinctNames,int Matches,int EligibleCopies,int Differences,int NotListed,
    DateTimeOffset CreatedAtUtc,string CreatedBy,DateTimeOffset? ReviewedAtUtc,string? ReviewedBy,
    int RegisteredCopies,int ReusedCopies);
public sealed record AcronisImportDetail(AcronisImportSummary Summary,AcronisReportDocument Document,
    AcronisImportComparison Comparison,string? ReviewNotes,IReadOnlyList<int> RegisteredRows,bool DuplicateUpload=false,
    IReadOnlyList<AcronisSupplementHistory>? Supplements=null);
public sealed record AcronisImportReviewResult(int Registered,int Reused);
public sealed record AcronisSupplementPreview(Guid ImportId,AcronisImportComparison Comparison,IReadOnlyList<int> RegisteredRows,string Stamp);
public sealed record AcronisSupplementRequest(Guid RequestId,string ExpectedStamp,IReadOnlyList<int> SourceRows,bool PlansReviewed,string? Notes=null);
public sealed record AcronisSupplementHistory(DateTimeOffset AtUtc,string Actor,IReadOnlyList<int> SourceRows,int Registered,int Reused,string? Notes);

public static class AcronisImportRules
{
    public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Name(string value)=>value.Trim().TrimEnd('.').Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public static string ShortName(string value)=>Name(value).Split('.')[0];
    public static bool SameName(string source,string atlas)=>Name(source)==Name(atlas) ||
        ((!Name(source).Contains('.') || !Name(atlas).Contains('.')) && ShortName(source)==ShortName(atlas));
    public static bool ResultOk(string value)=>Name(value) is "OK" or "SUCCESS" or "SUCCEEDED" or "COMPLETADA CORRECTAMENTE" or "SATISFACTORIA";

    public static AcronisImportComparison Compare(AcronisReportDocument report,IReadOnlyList<AcronisAtlasDevice> devices,
        DateTimeOffset reportAt,DateTimeOffset now)
    {
        var duplicates=report.Rows.Where(r=>r.DeviceName.Length>0).GroupBy(r=>ShortName(r.DeviceName))
            .Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
        List<AcronisComparisonRow> rows=[];
        foreach(var source in report.Rows)
        {
            if(source.Issues.Count>0) { rows.Add(new(source,"INVALID_ROW",string.Join(" ",source.Issues)));continue; }
            if(duplicates.Contains(ShortName(source.DeviceName)))
            { rows.Add(new(source,"DUPLICATE_SOURCE","Hay varias filas con el mismo nombre corto. Revisa planes e identidades; no se elige una fila automáticamente."));continue; }
            var matches=devices.Where(d=>d.Enabled && SameName(source.DeviceName,d.Name)).ToArray();
            if(matches.Length==0)
            { rows.Add(new(source,"NOT_IN_ATLAS",devices.Any(d=>!d.Enabled && SameName(source.DeviceName,d.Name))?
                "Solo coincide con un activo dado de baja en ATLAS.":"No aparece un activo habilitado con ese nombre en ATLAS."));continue; }
            if(matches.Length>1) { rows.Add(new(source,"AMBIGUOUS_ATLAS","Más de un activo habilitado coincide con el nombre. Revisa su identidad."));continue; }
            var device=matches[0];
            AcronisComparisonRow row=new(source,"MATCHED","Coincidencia por nombre; revisa el plan de Acronis antes de registrar la copia.",
                device.Id,device.Name,device.Agency,device.Provider);
            if(device.Provider!="ACRONIS")
            { rows.Add(row with { Category="PROVIDER_MISMATCH",Detail="Aparece en Acronis, pero el activo declara otro proveedor en ATLAS. Su vinculación requiere revisión." });continue; }
            if(device.BackupsPaused)
            { rows.Add(row with { Category="PAUSED",Detail="El seguimiento está pausado por custodia o devolución." });continue; }
            var profiles=device.Profiles.Where(p=>p.Enabled && p.ApprovalStatus is "ACTIVE" or "APPROVED").ToArray();
            if(profiles.Length==0) { rows.Add(row with { Category="NO_ACTIVE_CONTROL",Detail="El activo declara Acronis, pero no tiene un control de copia activo." });continue; }
            if(profiles.Length>1) { rows.Add(row with { Category="AMBIGUOUS_CONTROL",Detail="El equipo tiene varios controles activos. Revisa cuál corresponde al plan del informe." });continue; }
            var profile=profiles[0];
            if(profile.Provider!="ACRONIS")
            { rows.Add(row with { Category="CONTROL_MISMATCH",Detail="El proveedor del control no coincide con Acronis." });continue; }
            row=row with { ProfileId=profile.Id,BindingStamp=device.BindingStamp(profile),TaskName=profile.TaskName,ProtectedScope=profile.ProtectedScope };
            bool future=source.LastSuccessfulBackupAtUtc>reportAt.AddMinutes(5);
            bool beforeCustody=source.LastSuccessfulBackupAtUtc<device.TrackingSinceUtc;
            // Aggregate counters do not describe the latest successful copy.
            // The parser rejects invalid counters; other runs remain a separate observation.
            bool otherRuns=source.SuccessfulRuns<source.TotalRuns;
            bool countsValid=source.TotalRuns is >=0 && source.SuccessfulRuns is >=0 && source.SuccessfulRuns<=source.TotalRuns;
            bool eligible=ResultOk(source.LastResult) && source.LastSuccessfulBackupAtUtc.HasValue && !future && !beforeCustody && countsValid;
            var calendar=profile.Schedule?.Mode=="CALENDAR"
                ?BackupScheduleRules.EvaluateCalendar(profile.Schedule,source.LastSuccessfulBackupAtUtc,reportAt):null;
            string state=!ResultOk(source.LastResult)?"RESULT_REVIEW":!source.LastSuccessfulBackupAtUtc.HasValue?"NO_SUCCESS_DATE":
                future?"FUTURE_DATE":beforeCustody?"BEFORE_CUSTODY":!countsValid?"COUNTS_INVALID":
                (calendar?.Overdue??(source.LastSuccessfulBackupAtUtc.Value.AddHours(profile.FrequencyHours).AddMinutes(profile.GraceMinutes)<reportAt))?"OLDER_COPY":"WITHIN_INTERVAL";
            string detail=state switch {
                "RESULT_REVIEW"=>"El último resultado requiere revisión; el informe no indica cuándo terminó ese intento.",
                "NO_SUCCESS_DATE"=>"No hay una fecha verificable de copia correcta.",
                "FUTURE_DATE"=>"La copia figura después del corte del informe; revisa fecha y zona horaria.",
                "BEFORE_CUSTODY"=>"La copia es anterior al inicio del seguimiento de la custodia actual.",
                "COUNTS_INVALID"=>"Los contadores son inválidos; se requiere una exportación corregida.",
                "OLDER_COPY"=>"Copia anterior al plazo declarado en ATLAS. Se puede conservar su fecha histórica sin renovar la copia al día.",
                _=>"Última copia correcta dentro del plazo declarado en ATLAS al corte del informe. Revisa su plan y alcance." };
            if(calendar is not null && state is "OLDER_COPY" or "WITHIN_INTERVAL")
                detail=calendar.Detail+" Evaluado con el calendario registrado al corte del informe. Revisa su plan y alcance.";
            rows.Add(row with { CanRegister=eligible,EvidenceState=state,Detail=detail,
                RunCountNote=otherRuns?$"El informe muestra {source.SuccessfulRuns} ejecuciones correctas de {source.TotalRuns}. Hay otras ejecuciones no acreditadas como correctas; el resumen no precisa su fecha ni causa. Esto no invalida la última copia correcta.":null });
        }
        var expected=devices.Where(d=>d.Enabled && d.Provider=="ACRONIS").ToArray();
        var missing=expected.Where(d=>!report.Rows.Any(r=>r.DeviceName.Length>0 && SameName(r.DeviceName,d.Name)))
            .Select(d=>new AcronisUnlistedDevice(d.Id,d.Name,d.Agency)).ToArray();
        return new(now,rows,missing,expected.Length);
    }
}
