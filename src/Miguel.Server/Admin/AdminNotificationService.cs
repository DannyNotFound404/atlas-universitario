using System.Globalization;
using Microsoft.Extensions.Options;
using Miguel.Server.BackupControl;
using Miguel.Server.Storage;

namespace Miguel.Server.Admin;

public sealed class AdminNotificationService(
    AdminNotificationRepository repository,
    AdminDashboardService dashboard,
    BackupControlRepository backupControls,
    SecurityRepository securityRepository,
    TimeProvider timeProvider,
    IOptions<ServerOptions> options,
    Miguel.Server.Assets.AssetCustodyService? custody = null,
    Miguel.Server.Vulnerabilities.VulnerabilityRepository? vulnerabilities = null)
{
    private readonly TimeZoneInfo operationalTimeZone = ResolveTimeZone(
        options.Value.OperationalTimeZoneId);

    public async Task<AdminNotificationPage> ReadAsync(
        string username,
        int page,
        int pageSize,
        bool unreadOnly,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly day = ToOperationalDay(now);
        IReadOnlyList<AdminBackupCoverageView> backups =
            await dashboard.GetBackupCoverageAsync(cancellationToken);
        BackupControlDashboardView controlDashboard =
            await backupControls.ReadDashboardAsync(cancellationToken);
        AdminSecurityNotificationSignals security =
            await securityRepository.ReadNotificationSignalsAsync(cancellationToken);
        List<AdminNotificationCandidate> candidates = [];
        var held = custody is null ? [] : await custody.ReadHeld(cancellationToken);
        HashSet<Guid> governedDevices = controlDashboard.Profiles
            .Where(profile =>
                profile.Enabled &&
                BackupApprovalStatuses.IsActive(profile.ApprovalStatus) &&
                profile.DeviceId.HasValue)
            .Select(profile => profile.DeviceId!.Value)
            .ToHashSet();
        candidates.AddRange(BuildBackupCandidates(
            backups.Where(item => !governedDevices.Contains(item.DeviceId) && !held.Any(h => h.DeviceId == item.DeviceId && h.BackupsPaused)),
            day,
            now));
        candidates.AddRange(BuildBackupControlCandidates(
            controlDashboard.Profiles, day, now));
        candidates.AddRange(BuildPlanDemonstrationCandidates(controlDashboard.PlanDemonstrations ?? [],day,now));
        foreach (var item in held.Where(h => h.ReviewDueUtc <= now))
            candidates.Add(Candidate($"asset-custody:{day:yyyy-MM-dd}:{item.DeviceId:D}", "ASSET_CUSTODY", "WARNING",
                "Revisar equipo bajo custodia", $"{item.MachineName}: venció la revisión de custodia. Responsable: {item.Custodian}. Comprueba la preparación y el seguimiento de copias.",
                "assets", item.DeviceId.ToString("D"), now));
        candidates.AddRange(BuildPostureCandidates(security.Devices, day, now));
        candidates.AddRange(BuildSecurityEventCandidates(
            security.RecentEvents,
            day,
            now));
        await repository.UpsertAsync(candidates, cancellationToken);
        var history = await repository.ReadAsync(
            username, page, pageSize, unreadOnly, cancellationToken);
        return history with
        {
            AttentionGroups = AdminAttentionBuilder.Build(candidates, backups,
                controlDashboard.Profiles, security.Devices, controlDashboard.PlanDemonstrations)
                .Concat(vulnerabilities is null ? [] : await vulnerabilities.Attention(cancellationToken)).ToArray()
        };
    }

    public Task MarkReadAsync(
        Guid notificationId,
        string username,
        CancellationToken cancellationToken) =>
        repository.MarkReadAsync(notificationId, username, cancellationToken);

    public Task MarkAllReadAsync(
        string username,
        CancellationToken cancellationToken) =>
        repository.MarkAllReadAsync(username, cancellationToken);

    private IEnumerable<AdminNotificationCandidate> BuildBackupCandidates(
        IEnumerable<AdminBackupCoverageView> backups,
        DateOnly day,
        DateTimeOffset now)
    {
        foreach (AdminBackupCoverageView item in backups)
        {
            (string severity, string title)? alert = item.Compliance switch
            {
                "OVERDUE" => ("WARNING", "Copia atrasada"),
                "FAILED" => ("CRITICAL", "Copia fallida"),
                "CANCELLED" => ("WARNING", "Copia cancelada"),
                "WARNING" => ("WARNING", "Copia con pendientes"),
                "MISSING" => ("WARNING", "Sin copia reportada"),
                "CONFIGURATION_REQUIRED" => ("WARNING", "NAS pendiente de configurar"),
                "DESTINATION_MISMATCH" => ("CRITICAL", "Destino de copia incorrecto"),
                _ => null
            };
            if (!alert.HasValue)
            {
                continue;
            }

            string lastCopy = item.LastBackupAtUtc.HasValue
                ? TimeZoneInfo.ConvertTime(
                    item.LastBackupAtUtc.Value,
                    operationalTimeZone)
                    .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : "sin registro";
            string message = item.Compliance == "OVERDUE"
                ? $"{item.MachineName} ({item.AgencyCode}): {item.Detail} Última evidencia registrada: {lastCopy}. La ausencia de evidencia reciente requiere comprobación."
                : $"{item.MachineName} ({item.AgencyCode}): {item.Detail} Ultimo registro: {lastCopy}.";
            yield return Candidate(
                $"backup:{day:yyyy-MM-dd}:{item.DeviceId:D}:{item.Compliance}",
                "BACKUP",
                alert.Value.severity,
                alert.Value.title,
                message,
                "backups",
                item.DeviceId.ToString("D"),
                now);
        }
    }

    private IEnumerable<AdminNotificationCandidate> BuildPostureCandidates(
        IReadOnlyList<AdminSecurityDeviceView> devices,
        DateOnly day,
        DateTimeOffset now)
    {
        foreach (AdminSecurityDeviceView item in devices)
        {
            string freshness=item.ObservationState;
            if(freshness is "UNKNOWN" or "")freshness=item.CapturedAtUtc is null?"MISSING":item.CapturedAtUtc>now.AddMinutes(5)?"FUTURE":item.CapturedAtUtc<now.AddHours(-item.FreshnessHours)?"STALE":"RECENT";
            if(freshness is "MISSING" or "STALE" or "FUTURE")
            {
                yield return Candidate($"posture:{day:yyyy-MM-dd}:{item.DeviceId:D}","POSTURE","WARNING","Revisar cobertura de postura",
                    $"{item.MachineName} ({item.AgencyCode}): {(freshness=="MISSING"?"sin postura recibida":freshness=="FUTURE"?"fecha de postura posterior al corte; revisar reloj y fuente":"postura antigua; actualizar su evidencia")}. Captura: {(item.CapturedAtUtc.HasValue?item.CapturedAtUtc.Value.ToString("u"):"sin dato")}. No se infiere un fallo ni protección actual.","security",item.DeviceId.ToString("D"),now);
                continue;
            }
            List<string> issues = [];
            bool critical = false;
            if (item.CapturedAtUtc is null)
            {
                issues.Add("sin postura recibida");
            }
            if (item.FirewallEnabled != true)
            {
                issues.Add(item.FirewallEnabled == false
                    ? "firewall desactivado"
                    : "firewall sin confirmar");
                critical |= item.FirewallEnabled == false;
            }
            if (item.SystemDriveProtected != true)
            {
                issues.Add(item.SystemDriveProtected == false
                    ? "BitLocker C: desactivado"
                    : "BitLocker C: sin confirmar");
            }
            if (item.SecureBootEnabled != true)
            {
                issues.Add(item.SecureBootEnabled == false
                    ? "Secure Boot desactivado"
                    : "Secure Boot sin confirmar");
            }
            if (item.Smb1Enabled == true)
            {
                issues.Add("SMBv1 habilitado");
                critical = true;
            }
            if (item.SecurityEventLogReadable == false)
            {
                issues.Add("bitacora de seguridad inaccesible");
                critical = true;
            }
            if (item.PendingReboot == true)
            {
                issues.Add("reinicio pendiente");
            }
            if (item.CollectionWarnings.Count > 0)
                issues.Add("recopilación parcial; consultar cobertura del equipo");
            if (issues.Count == 0)
            {
                continue;
            }

            yield return Candidate(
                $"posture:{day:yyyy-MM-dd}:{item.DeviceId:D}",
                "POSTURE",
                critical ? "CRITICAL" : "WARNING",
                "Postura de seguridad requiere atencion",
                $"{item.MachineName} ({item.AgencyCode}): {string.Join(", ", issues.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                "security",
                item.DeviceId.ToString("D"),
                now);
        }
    }

    internal static IEnumerable<AdminNotificationCandidate>
        BuildBackupControlCandidates(
            IReadOnlyList<BackupControlProfileView> profiles,
            DateOnly day,
            DateTimeOffset now)
    {
        foreach (BackupControlProfileView profile in profiles.Where(item =>
                     item.Enabled &&
                     BackupApprovalStatuses.IsActive(item.ApprovalStatus)))
        {
            if(!profile.TrackingPaused&&profile.ScheduleStatus?.ExceptionExpired==true)
                yield return Candidate($"backup-schedule:{day:yyyy-MM-dd}:{profile.Id:D}:EXPIRED","BACKUP_CONTROL","WARNING","Revisar agenda individual vencida",
                    $"{profile.SystemName} ({profile.AgencyCode}): la agenda de presencia individual alcanzó su fecha de revisión. {profile.ScheduleStatus.Detail}","backups",profile.Id.ToString("D"),now);
            (string severity, string title)? alert = profile.Compliance switch
            {
                BackupControlCompliance.Cancelled =>
                    ("WARNING", "Copia cancelada"),
                BackupControlCompliance.Skipped => ("WARNING", "Copia omitida sin iniciar"),
                BackupControlCompliance.Partial => ("WARNING", "Copia con archivos o carpetas pendientes"),
                BackupControlCompliance.Failed =>
                    ("CRITICAL", "Control de respaldo fallido"),
                BackupControlCompliance.DestinationMismatch =>
                    ("CRITICAL", "Destino de respaldo no autorizado"),
                BackupControlCompliance.Overdue =>
                    ("WARNING", "Evidencia de respaldo vencida"),
                BackupControlCompliance.NotConfirmed =>
                    ("WARNING", "Respaldo sin confirmar"),
                BackupControlCompliance.ConfigurationRequired =>
                    ("WARNING", "Control de respaldo incompleto"),
                BackupControlCompliance.RestoreTestDue =>
                    ("WARNING", "Prueba de restauración vencida"),
                BackupControlCompliance.Warning =>
                    ("WARNING", "Respaldo con advertencias"),
                BackupControlCompliance.ExceptionExpired =>
                    ("WARNING", "Revisar excepción operativa vencida"),
                _ => null
            };
            string latestDetail = "";
            if(!profile.TrackingPaused && profile.ObservationContextStatus is "HISTORICAL_UNVERIFIED" or "CONTEXT_CHANGED")
            {alert=("WARNING","Revisar contexto de evidencia de respaldo");latestDetail=" "+profile.ObservationContextDetail;}
            if (!profile.TrackingPaused && profile.Compliance is BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue or BackupControlCompliance.Scheduled &&
                profile.LatestExecution is { Result: not "RUNNING" } latest &&
                !(profile.LatestExecutionAssessment?.AcceptableForControl ?? latest.Acceptable))
            {
                alert = latest.Result switch {
                    "CANCELLED" => ("WARNING", "Último intento de copia cancelado"),
                    "SKIPPED" => ("WARNING", "Última ejecución omitida sin iniciar"),
                    "FAILED" => ("CRITICAL", "Último intento de copia con fallos"),
                    "PARTIAL" => ("WARNING", "Último intento con archivos o carpetas pendientes"),
                    _ => ("WARNING", "Último intento de copia sin confirmar")
                };
                latestDetail = " Se conserva por separado la última copia aceptable dentro del plazo.";
            }
            if (!alert.HasValue)
            {
                continue;
            }
            if (profile.LatestExecutionAssessment is { AcceptableForControl: false } assessment && !profile.TrackingPaused &&
                alert.Value.title != "Prueba de restauración vencida")
            {
                alert = assessment.Code switch
                {
                    "FILES_PENDING" => ("WARNING", assessment.PendingFiles.HasValue
                        ? $"{(assessment.PendingFilesCountComplete ? "" : "Al menos ")}{assessment.PendingFiles} archivo(s) pendientes de copiar"
                        : "Archivos pendientes de copiar; recuento por confirmar"),
                    "MISSING_FOLDERS" => ("WARNING", "Carpetas de origen no encontradas"),
                    "SCOPE_MISMATCH" => ("WARNING", "Revisar carpetas del control de copia"),
                    "SCOPE_CONFIGURATION_REQUIRED" => ("WARNING", "Completar alcance del control de copia"),
                    "DESTINATION_MISMATCH" => ("CRITICAL", "Revisar destino de la copia"),
                    _ => alert
                };
                latestDetail = $" {assessment.Detail}{latestDetail}";
            }
            yield return Candidate(
                $"backup-control:{day:yyyy-MM-dd}:{profile.Id:D}:{profile.Compliance}",
                "BACKUP_CONTROL",
                alert.Value.severity,
                alert.Value.title,
                $"{profile.SystemName} ({profile.AgencyCode}): {profile.ComplianceDetail}{latestDetail}",
                "backups",
                profile.Id.ToString("D"),
                now);
            if (profile.RestoreTestDue && !profile.TrackingPaused && profile.Compliance != BackupControlCompliance.RestoreTestDue)
                yield return Candidate(
                    $"backup-control:{day:yyyy-MM-dd}:{profile.Id:D}:{BackupControlCompliance.RestoreTestDue}",
                    "BACKUP_CONTROL", "WARNING", "Prueba de restauración vencida",
                    $"{profile.SystemName} ({profile.AgencyCode}): además del resultado de copia, requiere una prueba de restauración vigente.",
                    "backups", profile.Id.ToString("D"), now);
        }
    }

    internal static IEnumerable<AdminNotificationCandidate> BuildPlanDemonstrationCandidates(
        IReadOnlyList<BackupPlanDemonstrationSummary> plans,DateOnly day,DateTimeOffset now)
    {
        foreach(var plan in plans.Where(p=>p.Pending))
            yield return Candidate($"backup-plan:{day:yyyy-MM-dd}:{plan.PlanId:D}:{plan.Status}","BACKUP_PLAN",
                plan.UnresolvedSamples>0?"CRITICAL":"WARNING",
                plan.UnresolvedSamples>0?"Demostración de restauración con muestras pendientes":"Demostración de restauración pendiente",
                $"{plan.Name}: {plan.Tested}/{plan.LatestRequiredSamples ?? plan.RequiredSamples} muestras registradas; {plan.UnresolvedSamples} resultado(s) por resolver. Continúa la demostración desde su historial.",
                "backups",plan.PlanId.ToString("D"),now);
    }

    private IEnumerable<AdminNotificationCandidate> BuildSecurityEventCandidates(
        IReadOnlyList<AdminSecurityEventView> events,
        DateOnly day,
        DateTimeOffset now)
    {
        IEnumerable<IGrouping<Guid, AdminSecurityEventView>> groups = events
            .Where(item =>
                ToOperationalDay(item.OccurredAtUtc) == day &&
                (item.Severity is "Critical" or "High" ||
                 item.Outcome == "Failure"))
            .GroupBy(item => item.DeviceId);
        foreach (IGrouping<Guid, AdminSecurityEventView> group in groups)
        {
            AdminSecurityEventView latest = group
                .OrderByDescending(item => item.OccurredAtUtc)
                .First();
            int count = group.Count();
            bool critical = group.Any(item => item.Severity is "Critical" or "High");
            string kinds = string.Join(", ", group
                .Select(item => item.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3));
            yield return Candidate(
                $"security-events:{day:yyyy-MM-dd}:{group.Key:D}",
                "SECURITY",
                critical ? "CRITICAL" : "WARNING",
                "Actividad de seguridad inusual",
                $"{latest.MachineName} ({latest.AgencyCode}) registro {count} evento(s) de atencion hoy: {kinds}.",
                "security",
                group.Key.ToString("D"),
                now);
        }
    }

    private static AdminNotificationCandidate Candidate(
        string fingerprint,
        string category,
        string severity,
        string title,
        string message,
        string targetView,
        string? targetEntityId,
        DateTimeOffset detectedAtUtc) =>
        new(
            fingerprint[..Math.Min(fingerprint.Length, 255)],
            category,
            severity,
            title[..Math.Min(title.Length, 160)],
            message[..Math.Min(message.Length, 800)],
            targetView,
            targetEntityId,
            detectedAtUtc);

    private DateOnly ToOperationalDay(DateTimeOffset value) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(value, operationalTimeZone).DateTime);

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(timeZoneId)
                    ? "America/Guatemala"
                    : timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
