using System.Text.Json;
using Miguel.Server.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.BackupControl;

public sealed class BackupPlanRepository(NpgsqlDataSource dataSource,
    BackupControlRepository controls, AdminRepository audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<BackupPlanView>> ReadAsync(CancellationToken token)
    {
        List<BackupPlanView> plans = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT id, revision, configuration::text, updated_at_utc, updated_by FROM backup_control_plans ORDER BY name, id;");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            plans.Add(new(reader.GetGuid(0), reader.GetInt32(1),
                JsonSerializer.Deserialize<BackupPlanDefinition>(reader.GetString(2), Json)!,
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)));
        return plans;
    }

    public async Task<Guid> SaveAsync(Guid? id, BackupPlanSaveRequest request, string actor, CancellationToken token)
    {
        BackupPlanDefinition definition = BackupPlanInput.Normalize(request.Definition);
        if ((id is null && request.ExpectedRevision != 0) || (id.HasValue && request.ExpectedRevision < 1))
            throw new BackupWorkspaceException("La versión del plan no es válida.");
        Guid planId = id ?? Guid.NewGuid();
        int revision = checked(request.ExpectedRevision + 1);
        DateTimeOffset now = clock.GetUtcNow();
        string json = JsonSerializer.Serialize(definition, Json);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(token);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(token);
        BackupPlanDefinition? before = null;
        if (id.HasValue)
        {
            await using var previous = new NpgsqlCommand("SELECT configuration::text FROM backup_control_plans WHERE id=$1 FOR UPDATE;",connection,tx);
            previous.Parameters.AddWithValue(planId);
            if (await previous.ExecuteScalarAsync(token) is string previousJson)
                before = JsonSerializer.Deserialize<BackupPlanDefinition>(previousJson,Json);
        }
        await using (NpgsqlCommand command = new(id is null ? """
            INSERT INTO backup_control_plans(id, name, revision, configuration, created_at_utc, updated_at_utc, updated_by)
            VALUES ($1,$2,$3,$4,$5,$5,$6);
            """ : """
            UPDATE backup_control_plans SET name=$2, revision=$3, configuration=$4, updated_at_utc=$5, updated_by=$6
            WHERE id=$1 AND revision=$7;
            """, connection, tx))
        {
            command.Parameters.AddWithValue(planId); command.Parameters.AddWithValue(definition.Name);
            command.Parameters.AddWithValue(revision); command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
            command.Parameters.AddWithValue(now); command.Parameters.AddWithValue(actor);
            if (id.HasValue) command.Parameters.AddWithValue(request.ExpectedRevision);
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new BackupWorkspaceException("El plan cambió. Actualiza la página antes de guardar.", true);
        }
        await using (NpgsqlCommand version = new("""
            INSERT INTO backup_control_plan_versions(plan_id,revision,configuration,created_at_utc,created_by)
            VALUES ($1,$2,$3,$4,$5);
            """, connection, tx))
        {
            version.Parameters.AddWithValue(planId); version.Parameters.AddWithValue(revision);
            version.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json); version.Parameters.AddWithValue(now);
            version.Parameters.AddWithValue(actor); await version.ExecuteNonQueryAsync(token);
        }
        await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.plan.saved","BackupPlan",planId.ToString(),
            new { Revision = revision, Before = before, After = definition },token,tx);
        await tx.CommitAsync(token);
        return planId;
    }

    public async Task<int> ApplyAsync(Guid planId, BackupPlanApplyRequest request, string actor, CancellationToken token)
    {
        if (request.Targets is null || request.Targets.Count is < 1 or > 500 ||
            request.Targets.Any(t => t is null || (t.ProfileId is null) == (t.DeviceId is null)) ||
            request.Targets.Select(t => t.ProfileId ?? t.DeviceId).Distinct().Count() != request.Targets.Count)
            throw new BackupWorkspaceException("Selecciona entre 1 y 500 equipos o controles diferentes.");
        BackupControlDashboardView dashboard = await controls.ReadDashboardAsync(token);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(token);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(token);
        BackupPlanView plan;
        await using (NpgsqlCommand command = new("""
            SELECT revision, configuration::text, updated_at_utc, updated_by
            FROM backup_control_plans WHERE id=$1 FOR UPDATE;
            """, connection, tx))
        {
            command.Parameters.AddWithValue(planId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetInt32(0) != request.Revision)
                throw new BackupWorkspaceException("El plan cambió. Actualiza y revisa su nueva versión antes de aplicarlo.", true);
            plan = new(planId, request.Revision, JsonSerializer.Deserialize<BackupPlanDefinition>(reader.GetString(1), Json)!,
                reader.GetFieldValue<DateTimeOffset>(2),reader.GetString(3));
        }
        List<object> changes = [];
        DateTimeOffset now = clock.GetUtcNow();
        foreach (BackupPlanTarget target in request.Targets.OrderBy(t => t.ProfileId ?? t.DeviceId))
        {
            BackupControlProfileView? existing = target.ProfileId is { } pid
                ? dashboard.Profiles.FirstOrDefault(p => p.Id == pid) : null;
            BackupControlDeviceOption? device = dashboard.Devices.FirstOrDefault(d => d.Id == (existing?.DeviceId ?? target.DeviceId));
            if (existing?.DeviceRetired == true)
                throw new BackupWorkspaceException("El equipo está desvinculado. Su control se conserva únicamente como historial.",true);
            if ((existing?.DeviceId ?? target.DeviceId) is { } linkedDevice)
            {
                await using var lifecycle = new NpgsqlCommand("SELECT id FROM devices WHERE id=$1 AND enabled FOR SHARE;",connection,tx);
                lifecycle.Parameters.AddWithValue(linkedDevice);
                if (device is null || await lifecycle.ExecuteScalarAsync(token) is not Guid)
                    throw new BackupWorkspaceException("El equipo fue desvinculado. Actualiza la selección; no se aplicó ningún cambio.",true);
            }
            if (target.ProfileId.HasValue)
            {
                await using NpgsqlCommand guard = new("SELECT updated_at_utc FROM backup_control_profiles WHERE id=$1 FOR UPDATE;",connection,tx);
                guard.Parameters.AddWithValue(target.ProfileId.Value);
                object? current = await guard.ExecuteScalarAsync(token);
                if (existing is null || current is not DateTime time || new DateTimeOffset(DateTime.SpecifyKind(time,DateTimeKind.Utc)) != existing.UpdatedAtUtc ||
                    target.ExpectedUpdatedAtUtc != existing.UpdatedAtUtc)
                    throw new BackupWorkspaceException("Un control cambió. Actualiza la lista; no se aplicó ningún cambio.", true);
            }
            else
            {
                await using NpgsqlCommand guard = new("SELECT backup_provider FROM devices WHERE id=$1 AND enabled=TRUE FOR UPDATE;",connection,tx);
                guard.Parameters.AddWithValue(target.DeviceId!.Value);
                object? currentPolicy = await guard.ExecuteScalarAsync(token);
                if (device is null || currentPolicy is null)
                    throw new BackupWorkspaceException("El equipo ya no está disponible.", true);
                if (currentPolicy is BackupProviders.SharedFolder)
                    throw new BackupWorkspaceException("El equipo trabaja en una carpeta de red. Actualiza y revisa su política antes de asignarle una copia individual.", true);
                await using NpgsqlCommand duplicates = new("SELECT EXISTS(SELECT 1 FROM backup_control_profiles WHERE device_id=$1);",connection,tx);
                duplicates.Parameters.AddWithValue(device.Id);
                if (await duplicates.ExecuteScalarAsync(token) is true)
                    throw new BackupWorkspaceException("El equipo ya tiene seguimiento. Actualiza la lista y selecciona su control existente.", true);
            }
            Guid? nasId = target.NasApplianceId ?? existing?.NasApplianceId ?? device?.NasApplianceId;
            BackupControlNasOption? nas = dashboard.Nas.FirstOrDefault(n => n.Id == nasId);
            if (nasId.HasValue && nas is null)
                throw new BackupWorkspaceException("El NAS seleccionado ya no está disponible.",true);
            if (nas is not null && nas.AgencyCode != (existing?.AgencyCode ?? device?.AgencyCode))
                throw new BackupWorkspaceException("El NAS debe pertenecer a la agencia del equipo.");
            if (nas is not null)
            {
                await using NpgsqlCommand guard = new("SELECT host,share_name FROM nas_appliances WHERE id=$1 AND agency_code=$2 AND enabled=TRUE FOR SHARE;",connection,tx);
                guard.Parameters.AddWithValue(nas.Id); guard.Parameters.AddWithValue(nas.AgencyCode);
                await using NpgsqlDataReader reader = await guard.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || reader.GetString(0)!=nas.Host || reader.GetString(1)!=nas.ShareName)
                    throw new BackupWorkspaceException("El destino cambió. Actualiza y revisa la asignación.",true);
            }
            BackupControlStandardView? standard = dashboard.Standards.FirstOrDefault(s => s.Provider == plan.Definition.Provider) ??
                dashboard.Standards.FirstOrDefault(s => s.Provider is null);
            if (standard is null) throw new BackupWorkspaceException("No hay un estándar vigente para este proveedor.");
            BackupControlProfileRequest resolved = BackupPlanInput.Resolve(plan,target,existing,device,nas,standard.Id);
            if (!await controls.ReferencesBelongToAgencyAsync(resolved,token,tx))
                throw new BackupWorkspaceException("La agencia, el equipo o el destino cambiaron. Actualiza la lista.",true);
            Guid profileId;
            if (existing is not null)
            {
                profileId=existing.Id;
                if (!await controls.UpdateProfileAsync(profileId,resolved,actor,now,token,tx))
                    throw new BackupWorkspaceException("El control ya no está disponible.",true);
            }
            else profileId = (await controls.CreateProfileAsync(resolved,actor,now,token,tx))!.Value;
            string[] folders = target.ScopeMode == "KEEP" ? existing!.ProtectedFolders?.ToArray() ?? [] :
                BackupPlanInput.NormalizeFolders(target.ScopeMode == "CUSTOM" ? target.Folders : plan.Definition.Folders);
            await using NpgsqlCommand assign = new("""
                UPDATE backup_control_profiles SET plan_id=$2,plan_revision=$3,protected_folders=$4,scope_override=$5 WHERE id=$1;
                """,connection,tx);
            assign.Parameters.AddWithValue(profileId); assign.Parameters.AddWithValue(planId); assign.Parameters.AddWithValue(request.Revision);
            assign.Parameters.AddWithValue(folders); assign.Parameters.AddWithValue(target.ScopeMode != "PLAN");
            await assign.ExecuteNonQueryAsync(token);
            changes.Add(new { ProfileId=profileId, target.ScopeMode, Before=existing, Applied=resolved, Folders=folders });
        }
        await audit.WriteAuditAsync(now,"Admin",actor,"admin.backup_control.plan.applied","BackupPlan",planId.ToString(),
            new { request.Revision, Changes=changes },token,tx);
        await tx.CommitAsync(token);
        return changes.Count;
    }
}
