using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Server.Admin;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Miguel.Server.BackupControl;

public sealed record BackupRecordRequest(Guid RequestId, string Kind,
    BackupControlObservationRequest? Observation, BackupRestorationRequest? Restoration);

public sealed class BackupRecordService(NpgsqlDataSource dataSource, BackupControlRepository repository,
    BackupEvidenceService evidence, AdminRepository audit, IOptions<ServerOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(Guid profileId, BackupRecordRequest request, IFormFile? file, string actor, CancellationToken token)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (request.RequestId == Guid.Empty || request.Kind is not ("OBSERVATION" or "RESTORATION"))
            throw new BackupWorkspaceException("El registro no es válido.");
        if (request.Kind == "OBSERVATION")
        {
            if (request.Observation is null || request.Restoration is not null ||
                !BackupControlInput.TryNormalize(request.Observation,out BackupControlObservationRequest observation) ||
                observation.PeriodEndUtc > now.AddMinutes(5) || observation.PeriodStartUtc == default ||
                observation.FinishedAtUtc > now.AddMinutes(5) || observation.StartedAtUtc > now.AddMinutes(5))
                throw new BackupWorkspaceException("Revisa las fechas y el resultado de la copia.");
            request = request with { Observation=observation };
        }
        else
        {
            if (request.Restoration is null || request.Observation is not null ||
                !BackupControlInput.TryNormalize(request.Restoration,out BackupRestorationRequest restoration) ||
                restoration.FinishedAtUtc > now.AddMinutes(5) || restoration.StartedAtUtc == default ||
                restoration.ValidatedAtUtc > now.AddMinutes(5) || restoration.BackupUsedAtUtc > restoration.StartedAtUtc)
                throw new BackupWorkspaceException("Revisa las fechas, el ejecutor y las comprobaciones de la restauración.");
            request = request with { Restoration=restoration };
        }
        string? fileHash=null;
        if (file is not null)
        {
            if (file.Length <= 0 || file.Length > options.Value.MaxBackupEvidenceUploadBytes)
                throw new BackupWorkspaceException("El archivo debe tener contenido y no superar 25 MB.");
            await using Stream source = file.OpenReadStream();
            fileHash=Convert.ToHexString(await SHA256.HashDataAsync(source,token));
        }
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { profileId, request, FileName=file?.FileName, FileHash=fileHash },Json))));
        await using NpgsqlConnection connection=await dataSource.OpenConnectionAsync(token);
        await using NpgsqlTransaction tx=await connection.BeginTransactionAsync(token);
        BackupEvidenceUploadResult? uploaded=null;
        bool commitStarted=false;
        try
        {
            // Lock the profile so provider edits cannot race a manual source validation.
            await using (NpgsqlCommand guard=new("SELECT p.provider FROM backup_control_profiles p WHERE p.id=$1 AND (p.device_id IS NULL OR EXISTS (SELECT 1 FROM devices d WHERE d.id=p.device_id AND d.enabled)) FOR UPDATE OF p;",connection,tx))
            {
                guard.Parameters.AddWithValue(profileId);
                object? provider=await guard.ExecuteScalarAsync(token);
                if (provider is not string value) throw new BackupWorkspaceException("El control ya no está disponible.",true);
                if (request.Observation?.Source == "ACRONIS_EVIDENCE" && value != "ACRONIS")
                    throw new BackupWorkspaceException("La evidencia de Acronis requiere un control de Acronis.");
            }
            await using (NpgsqlCommand dedup=new("""
                INSERT INTO backup_control_record_requests(id,profile_id,fingerprint,created_at_utc)
                VALUES ($1,$2,$3,$4) ON CONFLICT DO NOTHING RETURNING id;
                """,connection,tx))
            {
                dedup.Parameters.AddWithValue(request.RequestId);dedup.Parameters.AddWithValue(profileId);
                dedup.Parameters.AddWithValue(fingerprint);dedup.Parameters.AddWithValue(now);
                if (await dedup.ExecuteScalarAsync(token) is null)
                {
                    await using NpgsqlCommand previous=new("SELECT fingerprint FROM backup_control_record_requests WHERE id=$1;",connection,tx);
                    previous.Parameters.AddWithValue(request.RequestId);
                    if (await previous.ExecuteScalarAsync(token) is not string saved || saved != fingerprint)
                        throw new BackupWorkspaceException("La solicitud ya se usó con otros datos. Actualiza antes de registrar otra actividad.",true);
                    return;
                }
            }
            Guid? observationId=null,restorationId=null;
            if (request.Observation is { } observation)
                observationId=await repository.AddObservationAsync(profileId,observation,actor,now,token,tx);
            if (request.Restoration is { } restoration)
                restorationId=await repository.AddRestorationAsync(profileId,restoration,actor,now,token,tx);
            if (observationId is null && restorationId is null)
                throw new BackupWorkspaceException("No se pudo guardar la actividad.",true);
            if (file is not null)
            {
                BackupEvidenceUploadMetadata metadata=new(file.FileName,"REPORT",observationId,restorationId,
                    request.Restoration?.OperationalCaseId,request.Observation?.PeriodEndUtc ?? request.Restoration!.FinishedAtUtc,
                    request.Observation?.Detail ?? request.Restoration?.Notes,
                    request.Observation?.Source ?? "Prueba de restauración");
                await using Stream source=file.OpenReadStream();
                uploaded=await evidence.UploadAsync(profileId,metadata,source,actor,now,token,tx);
                if (uploaded is null) throw new BackupWorkspaceException("No se pudo vincular la evidencia al registro.");
            }
            await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.activity.recorded","BackupControlProfile",
                profileId.ToString(),new { Request=request, ObservationId=observationId,RestorationId=restorationId,Evidence=uploaded },token,tx);
            commitStarted=true;
            await tx.CommitAsync(token);
        }
        catch
        {
            // After an uncertain COMMIT, retain the file: a retry resolves the same request key.
            if (!commitStarted && uploaded?.PhysicalPath is { } path)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
