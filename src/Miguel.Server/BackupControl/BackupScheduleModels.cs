using Miguel.Server.Configuration;

namespace Miguel.Server.BackupControl;

public sealed record BackupScheduleChange(
    int ExpectedRevision, string Mode, string? TimeZone = null,
    int[]? Weekdays = null, DateOnly[]? ExcludedDates = null,
    DateOnly? ValidFrom = null, DateOnly? ValidUntil = null, string? Reason = null);

public sealed record BackupScheduleSetting(
    string Mode, string TimeZone, int[] Weekdays, DateOnly[] ExcludedDates,
    DateOnly? ValidFrom, DateOnly? ValidUntil, string Reason,
    DateTimeOffset? AppliesFromUtc = null);

public sealed record BackupScheduleOption(string Key, string Label, int Revision,
    BackupScheduleSetting Setting, string? AgencyCode = null);
public sealed record BackupScheduleDashboard(DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<BackupScheduleOption> Agencies, IReadOnlyList<BackupScheduleOption> Plans,
    IReadOnlyList<BackupScheduleOption> Profiles);

public sealed record BackupScheduleStatus(
    string Mode, string Source, bool ConfigurationPending,
    DateOnly? ObligationDate, DateTimeOffset? DeadlineUtc, DateOnly? LastAcceptedDate,
    bool Overdue, bool WaitingForFirstCopy, bool ExceptionExpired,
    string TimeZone, int[] Weekdays, string Detail,
    DateTimeOffset? AppliesFromUtc = null, int Revision = 0);

public static class BackupScheduleRules
{
    public static BackupScheduleSetting Inherit(string zone = "America/Guatemala") =>
        new("INHERIT", zone, [], [], null, null, "");

    public static BackupScheduleSetting Normalize(BackupScheduleChange request, string scope,
        DateTimeOffset now)
    {
        string mode = request.Mode?.Trim().ToUpperInvariant() ?? "";
        if (mode is not ("INHERIT" or "CALENDAR" or "LEGACY") || request.ExpectedRevision < 0)
            throw new BackupWorkspaceException("Revise el modo y la revisión del calendario.");
        string reason = ConfigurationService.Text(request.Reason, 500, "el motivo del calendario");
        string zone = string.IsNullOrWhiteSpace(request.TimeZone) ? "America/Guatemala" : request.TimeZone.Trim();
        ConfigurationService.Zone(zone);
        int[] days = (request.Weekdays ?? []).Distinct().Order().ToArray();
        DateOnly[] exclusions = (request.ExcludedDates ?? []).Distinct().Order().ToArray();
        if (days.Any(d => d is < 1 or > 7) || exclusions.Length > 366 ||
            mode == "CALENDAR" && days.Length == 0)
            throw new BackupWorkspaceException("Seleccione los días esperados (lunes a domingo) y revise las fechas excluidas.");
        if (request.ValidFrom > request.ValidUntil)
            throw new BackupWorkspaceException("La fecha final no puede ser anterior a la inicial.");
        if (scope == "PROFILE" && mode == "CALENDAR" &&
            (request.ValidFrom is null || request.ValidUntil is null))
            throw new BackupWorkspaceException("La agenda individual necesita inicio, fin y motivo para su revisión.");
        if (scope != "PROFILE" && (request.ValidFrom is not null || request.ValidUntil is not null))
            throw new BackupWorkspaceException("La vigencia individual solo corresponde a un control.");
        if (scope == "PROFILE" && mode == "CALENDAR")
        {
            bool hasDay = false;
            for (int offset = 0; offset < 7 * (exclusions.Length + 2); offset++)
            {
                DateOnly candidate = request.ValidFrom!.Value.AddDays(offset);
                if (candidate > request.ValidUntil) break;
                int iso = candidate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)candidate.DayOfWeek;
                if (days.Contains(iso) && !exclusions.Contains(candidate)) { hasDay = true; break; }
            }
            if (!hasDay) throw new BackupWorkspaceException("La vigencia no contiene ningún día de copia previsto. Revise los días y fechas excluidas.");
        }
        return new(mode, zone, mode == "CALENDAR" ? days : [],
            mode == "CALENDAR" ? exclusions : [], request.ValidFrom, request.ValidUntil, reason, now.ToUniversalTime());
    }

    public static bool Active(BackupScheduleSetting setting, DateTimeOffset at)
    {
        DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at,
            ConfigurationService.Zone(setting.TimeZone)).DateTime);
        return setting.Mode != "INHERIT" &&
            (!setting.AppliesFromUtc.HasValue || setting.AppliesFromUtc <= at) &&
            (!setting.ValidFrom.HasValue || date >= setting.ValidFrom) &&
            (!setting.ValidUntil.HasValue || date <= setting.ValidUntil);
    }

    // A whole local day is the unit of obligation, not a rolling24h window.
    // Old failures and restoration states remain separate; this is only scheduling.
    public static BackupScheduleStatus EvaluateCalendar(BackupScheduleSetting setting,
        DateTimeOffset? lastAcceptedAt, DateTimeOffset at, string source = "PROFILE",
        bool exceptionExpired = false, int revision = 0)
    {
        TimeZoneInfo zone = ConfigurationService.Zone(setting.TimeZone);
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        DateOnly? accepted = lastAcceptedAt.HasValue && lastAcceptedAt <= at.AddMinutes(5)
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(lastAcceptedAt.Value, zone).DateTime) : null;
        if (setting.Mode != "CALENDAR" || !Active(setting, at))
            return new("LEGACY", source, source == "UNCONFIGURED", null, null, accepted,
                false, false, exceptionExpired, setting.TimeZone, [],
                source == "UNCONFIGURED" ? "Calendario sin configurar; se conserva la frecuencia y el margen del control."
                    : "Se utiliza la frecuencia y el margen configurados.", setting.AppliesFromUtc, revision);
        DateOnly? start = setting.AppliesFromUtc.HasValue
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(setting.AppliesFromUtc.Value, zone).DateTime) : null;
        if (setting.ValidFrom.HasValue && (!start.HasValue || setting.ValidFrom > start)) start = setting.ValidFrom;
        DateOnly? duty = null;
        for (int offset = 1; offset <= 7 * (setting.ExcludedDates.Length + 2); offset++)
        {
            DateOnly candidate = today.AddDays(-offset);
            if (start.HasValue && candidate < start) break;
            int iso = candidate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)candidate.DayOfWeek;
            if (setting.Weekdays.Contains(iso) && !setting.ExcludedDates.Contains(candidate))
            { duty = candidate; break; }
        }
        bool overdue = duty.HasValue && (!accepted.HasValue || accepted < duty);
        bool waiting = !duty.HasValue && (!accepted.HasValue || start.HasValue && accepted < start);
        DateOnly? deadlineDay = duty;
        if (!deadlineDay.HasValue)
            for (int offset = 0; offset <= 7 * (setting.ExcludedDates.Length + 2); offset++)
            {
                DateOnly candidate = today.AddDays(offset);
                if (setting.ValidUntil.HasValue && candidate > setting.ValidUntil) break;
                int iso = candidate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)candidate.DayOfWeek;
                if (setting.Weekdays.Contains(iso) && !setting.ExcludedDates.Contains(candidate)) { deadlineDay = candidate; break; }
            }
        DateTimeOffset? deadline = deadlineDay.HasValue ? DayStartUtc(deadlineDay.Value.AddDays(1), zone) : null;
        string detail = overdue
            ? $"No hay copia aceptable para la obligación cerrada del {duty:dd/MM/yyyy}; revise la evidencia y la asistencia antes de atribuir un incumplimiento."
            : waiting ? "Pendiente de copia dentro del plazo; el día local completo aún no ha cerrado."
            : "La última copia aceptable cubre el último día exigible del calendario; el día actual dispone de su plazo completo.";
        if (exceptionExpired) detail += " La agenda individual venció; revise su vigencia.";
        return new("CALENDAR", source, false, duty, deadline, accepted, overdue, waiting,
            exceptionExpired, setting.TimeZone, setting.Weekdays, detail, setting.AppliesFromUtc, revision);
    }

    private static DateTimeOffset DayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        DateTime local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some time zones change offset at midnight. Use the first actual instant.
        for (int i = 0; zone.IsInvalidTime(local) && i < 2880; i++) local = local.AddMinutes(1);
        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
