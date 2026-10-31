namespace Miguel.Server.BackupControl;

public sealed record BackupPlanDefinition(
    string Name, string Provider, IReadOnlyList<string> Folders,
    string? Exclusions, int FrequencyHours, int GraceMinutes,
    int RpoHours, int RtoMinutes, int RestoreTestIntervalDays,
    string ProcessOwner, string TaskName, string? ExpectedDestination);
public sealed record BackupPlanSaveRequest(int ExpectedRevision, BackupPlanDefinition Definition);
public sealed record BackupPlanView(Guid Id, int Revision, BackupPlanDefinition Definition,
    DateTimeOffset UpdatedAtUtc, string UpdatedBy);
public sealed record BackupPlanTarget(Guid? ProfileId, Guid? DeviceId,
    DateTimeOffset? ExpectedUpdatedAtUtc, string ScopeMode,
    IReadOnlyList<string>? Folders, Guid? NasApplianceId);
public sealed record BackupPlanApplyRequest(int Revision, IReadOnlyList<BackupPlanTarget> Targets);
public sealed class BackupWorkspaceException(string message, bool conflict = false) : Exception(message)
{
    public bool Conflict { get; } = conflict;
}

public static class BackupPlanInput
{
    public static string[] NormalizeFolders(IReadOnlyList<string>? folders)
    {
        if (folders is null || folders.Count > 100)
            throw new BackupWorkspaceException("Indica entre 1 y 100 carpetas o ubicaciones.");
        string[] result = folders.Select(item => (item ?? "").Trim())
            .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0 || result.Any(item => item.Length > 512 || item.Any(char.IsControl)) ||
            string.Join("\n", result).Length > 4000)
            throw new BackupWorkspaceException("Revisa las ubicaciones: una por línea, hasta 512 caracteres cada una.");
        return result;
    }

    public static BackupPlanDefinition Normalize(BackupPlanDefinition value)
    {
        if (value is null) throw new BackupWorkspaceException("Falta la definición del plan.");
        value = value with { Name = (value.Name ?? "").Trim(), Provider = (value.Provider ?? "").Trim().ToUpperInvariant(),
            Folders = NormalizeFolders(value.Folders), ProcessOwner = (value.ProcessOwner ?? "").Trim(),
            TaskName = (value.TaskName ?? "").Trim(), Exclusions = value.Exclusions?.Trim(),
            ExpectedDestination = value.ExpectedDestination?.Trim() };
        if (value.Name.Length is < 1 or > 160 || !BackupControlProviders.All.Contains(value.Provider) ||
            value.ProcessOwner.Length is < 1 or > 160 || value.TaskName.Length is < 1 or > 160 ||
            value.Exclusions?.Length > 1000 || value.ExpectedDestination?.Length > 500 ||
            value.FrequencyHours is < 1 or > 8760 || value.GraceMinutes is < 0 or > 10080 ||
            value.RpoHours is < 1 or > 8760 || value.RtoMinutes is < 1 or > 10080 ||
            value.RestoreTestIntervalDays is < 1 or > 730 ||
            (value.Provider != BackupControlProviders.AtlasBackupNas && string.IsNullOrWhiteSpace(value.ExpectedDestination)))
            throw new BackupWorkspaceException("Completa el nombre, responsable del proceso, tarea, destino y plazos del plan.");
        return value;
    }

    public static BackupControlProfileRequest Resolve(BackupPlanView plan, BackupPlanTarget target,
        BackupControlProfileView? existing, BackupControlDeviceOption? device,
        BackupControlNasOption? nas, Guid standardId)
    {
        BackupPlanDefinition d = plan.Definition;
        if (target.ScopeMode is not ("KEEP" or "PLAN" or "CUSTOM") || (target.ScopeMode == "KEEP" && existing is null))
            throw new BackupWorkspaceException("Selecciona el alcance del plan o las carpetas particulares del equipo.");
        if (existing?.ExceptionType == BackupControlExceptionTypes.Normative)
            throw new BackupWorkspaceException("Una excepción normativa requiere su tratamiento formal antes de aplicar un plan.");
        if (existing is null && device?.WorkstationBackupPolicy == "SHARED_FOLDER")
            throw new BackupWorkspaceException("El equipo trabaja en una carpeta de red. Revisa su política antes de asignarle un plan de copia individual.");
        bool keep = target.ScopeMode == "KEEP";
        string[] folders = keep ? existing!.ProtectedFolders?.ToArray() ?? [] :
            NormalizeFolders(target.ScopeMode == "CUSTOM" ? target.Folders : d.Folders);
        string scope = keep ? existing!.ProtectedScope : string.Join("\n", folders);
        string provider = existing?.Provider ?? device?.BackupProvider ?? "";
        if (provider != d.Provider || (existing is null && device is null))
            throw new BackupWorkspaceException("El proveedor del equipo o control no coincide con el plan.");
        Guid? nasId = nas?.Id;
        string? destination = d.Provider == BackupControlProviders.AtlasBackupNas
            ? nas is null ? null : $"\\\\{nas.Host}\\{nas.ShareName}" : d.ExpectedDestination;
        Dictionary<string, string> origins = new(StringComparer.Ordinal);
        foreach (string field in new[] { "frequencyHours", "graceMinutes", "rpoHours", "rtoMinutes", "restoreTestIntervalDays", "taskName", "processOwner", "retentionDays" })
            origins[field] = BackupControlFieldOrigins.Inherited;
        origins["protectedScope"] = keep || target.ScopeMode == "CUSTOM" ? BackupControlFieldOrigins.Override : BackupControlFieldOrigins.Inherited;
        BackupControlProfileRequest request = new(
            existing?.Code ?? $"BKP-{Guid.NewGuid():N}", existing?.ScopeType ?? "ENDPOINT",
            existing?.DeviceId ?? device?.Id, existing?.AgencyCode ?? device!.AgencyCode,
            existing?.SystemName ?? device!.MachineName, scope, d.Provider,
            d.Provider == BackupControlProviders.AtlasBackupNas ? "AUTOMATIC" : "MANUAL",
            d.Provider == BackupControlProviders.AtlasBackupNas ? "Atlas Backup / NAS" : d.Provider == "ACRONIS" ? "Acronis" : "Otro",
            d.TaskName, nasId, destination, d.FrequencyHours, d.GraceMinutes, 180, d.RpoHours,
            d.RtoMinutes, d.RestoreTestIntervalDays, BackupControlResponsibilities.TechnicalResponsible,
            d.ProcessOwner, standardId, "CUSTOMIZED", $"Plan {d.Name}, versión {plan.Revision}.",
            keep ? existing!.ProtectedScopeCategories : new[] { "OTHER" },
            keep ? existing!.ProtectedScopeAdditional : string.Join("\n", folders),
            keep ? existing!.ProtectedScopeExclusions : d.Exclusions, origins,
            existing?.ExceptionType ?? "NONE", existing?.ExceptionReason, existing?.ExceptionExpiresAtUtc,
            true, true, folders);
        if (!BackupControlInput.TryNormalize(request, out request))
            throw new BackupWorkspaceException("El control necesita un destino autorizado y una configuración completa para activarse.");
        return request;
    }
}
