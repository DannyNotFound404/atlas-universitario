using Miguel.Server.BackupControl;

namespace Miguel.Server.Admin;

public sealed record AdminAttentionItem(
    string TargetView, string? TargetEntityId, Guid? DeviceId,
    string Name, string? AgencyCode, string Detail, string? Responsible,
    DateTimeOffset? ObservedAtUtc);

public sealed record AdminAttentionGroup(
    string Key, string Category, string Severity, string Title,
    string NextAction, IReadOnlyList<AdminAttentionItem> Items);

// This projection describes current evaluated signals. Reading notifications is
// independent of treatment, and the projection never closes historical cases.
internal static class AdminAttentionBuilder
{
    internal static IReadOnlyList<AdminAttentionGroup> Build(
        IReadOnlyList<AdminNotificationCandidate> candidates,
        IReadOnlyList<AdminBackupCoverageView> devices,
        IReadOnlyList<BackupControlProfileView> profiles,
        IReadOnlyList<AdminSecurityDeviceView> posture,
        IReadOnlyList<BackupPlanDemonstrationSummary>? plans = null)
    {
        var deviceMap = devices.ToDictionary(d => d.DeviceId);
        var profileMap = profiles.ToDictionary(p => p.Id);
        var postureMap = posture.ToDictionary(p => p.DeviceId);
        return candidates
            .GroupBy(c => new { c.Category, c.Title, c.Severity })
            .OrderByDescending(g => g.Key.Severity == "CRITICAL")
            .ThenBy(g => g.Key.Category, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Title, StringComparer.Ordinal)
            .Select(g => new AdminAttentionGroup(
                $"{g.Key.Category}:{g.Key.Title}:{g.Key.Severity}",
                g.Key.Category, g.Key.Severity, g.Key.Title,
                NextAction(g.Key.Category, g.Key.Title),
                g.DistinctBy(c => c.TargetEntityId ?? c.Fingerprint).Select(c =>
                {
                    Guid? target = Guid.TryParse(c.TargetEntityId, out var id) ? id : null;
                    var plan = c.Category == "BACKUP_PLAN" ? plans?.FirstOrDefault(p=>p.PlanId==target) : null;
                    BackupControlProfileView? profile = c.Category == "BACKUP_CONTROL" && target.HasValue
                        ? profileMap.GetValueOrDefault(target.Value) : null;
                    Guid? deviceId = profile?.DeviceId ?? (c.Category is "BACKUP_CONTROL" or "BACKUP_PLAN" ? null : target);
                    var device = deviceId.HasValue ? deviceMap.GetValueOrDefault(deviceId.Value) : null;
                    var security = deviceId.HasValue ? postureMap.GetValueOrDefault(deviceId.Value) : null;
                    return new AdminAttentionItem(c.TargetView, c.TargetEntityId, deviceId,
                        plan?.Name ?? profile?.SystemName ?? device?.MachineName ?? security?.MachineName ?? "Consultar origen",
                        profile?.AgencyCode ?? device?.AgencyCode ?? security?.AgencyCode,
                        c.Message, profile?.TechnicalResponsible,
                        plan?.LastAtUtc ?? (c.Title.Contains("restauración", StringComparison.OrdinalIgnoreCase) ? profile?.LastRestorationAtUtc : profile?.LastObservationAtUtc) ?? (c.Category == "POSTURE" ? security?.CapturedAtUtc
                            : c.Category == "BACKUP" ? device?.LastBackupAtUtc : null));
                }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private static string NextAction(string category, string title) => category switch
    {
        "BACKUP_PLAN" => "Abrir Planes en Control de respaldos y registrar la demostración o repetir únicamente las muestras pendientes.",
        "BACKUP_CONTROL" or "BACKUP" when title.Contains("sin confirmar", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Sin copia", StringComparison.OrdinalIgnoreCase) =>
            "Comprobar la herramienta de respaldo y su fuente de resultados. La ausencia de evidencia no confirma un fallo.",
        "BACKUP_CONTROL" when title.Contains("restauración", StringComparison.OrdinalIgnoreCase) =>
            "Programar una prueba de restauración y registrar su alcance y resultado.",
        "BACKUP_CONTROL" or "BACKUP" =>
            "Abrir el control, revisar el resultado y el destino; documentar el seguimiento si requiere tratamiento.",
        "POSTURE" => "Revisar qué controles están confirmados y cuáles no pudieron consultarse, junto con la fecha de la observación.",
        "SECURITY" => "Investigar los eventos de hoy en su contexto. Un evento de Windows no confirma por sí solo un incidente.",
        "ASSET_CUSTODY" => "Comprobar custodia, preparación y próxima revisión del equipo.",
        _ => "Consultar el origen y registrar la acción que corresponda."
    };
}
