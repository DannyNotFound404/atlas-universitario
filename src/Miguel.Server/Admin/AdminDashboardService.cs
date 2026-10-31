using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Miguel.Contracts;
using Miguel.Server.BackupControl;

namespace Miguel.Server.Admin;

public sealed class AdminDashboardService(
    AdminRepository repository,
    InfrastructureRepository infrastructure,
    TimeProvider timeProvider,
    IOptions<ServerOptions> serverOptions,
    BackupControlRepository? backupControls = null)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private IReadOnlyList<AdminDeviceData>? cachedDevices;
    private DateTimeOffset cacheExpiresAtUtc;
    private readonly TimeZoneInfo operationalTimeZone = ResolveTimeZone(
        serverOptions.Value.OperationalTimeZoneId);

    public async Task<AdminDashboardSummary> GetSummaryAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceView> devices =
            await GetViewsAsync(cancellationToken);
        devices = devices.Where(device => device.Enabled).ToArray();

        AdminDeviceView[] atlasDevices = devices
            .Where(device => device.BackupProvider == BackupProviders.Atlas)
            .ToArray();
        DateTimeOffset now = timeProvider.GetUtcNow();
        AdminBackupCoverageView[] backupCoverage = (await GetBackupCoverageAsync(cancellationToken))
            .Where(device => device.BackupProvider == BackupProviders.Atlas).ToArray();
        IReadOnlyList<AdminAgencyDefinition> agencies =
            await infrastructure.ReadAgenciesAsync(cancellationToken);

        return new AdminDashboardSummary(
            now,
            devices.Count,
            devices.Count(device => device.Connectivity == "Online"),
            devices.Count(device => device.Connectivity == "Attention"),
            devices.Count(device => device.Connectivity == "Offline"),
            devices.Count(device => device.SecurityProducts.Count > 0),
            backupCoverage.Count(item => item.Compliance == "COMPLIANT"),
            backupCoverage.Count(item => item.Compliance is
                "WARNING" or "CANCELLED" or "SKIPPED" or "OVERDUE" or "CONFIGURATION_REQUIRED"),
            backupCoverage.Count(item => item.Compliance is
                "FAILED" or "PARTIAL" or "DESTINATION_MISMATCH"),
            backupCoverage.Count(item => item.Compliance is "MISSING" or "NOT_CONFIRMED"),
            agencies.Count(agency => agency.Enabled));
    }

    public async Task<IReadOnlyList<AdminAgencyView>> GetAgenciesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceView> devices =
            await GetViewsAsync(cancellationToken);
        IReadOnlyList<AdminAgencyDefinition> agencies =
            await infrastructure.ReadAgenciesAsync(cancellationToken);
        IReadOnlyList<AdminNasDefinition> appliances =
            await infrastructure.ReadNasAsync(cancellationToken);

        return agencies.Select(agency =>
        {
            AdminDeviceView[] active = devices
                .Where(device => device.Enabled && string.Equals(
                    device.AgencyCode,
                    agency.Code,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            AdminNasDefinition[] agencyNas = appliances
                .Where(nas => nas.Enabled && string.Equals(
                    nas.AgencyCode,
                    agency.Code,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            IGrouping<string, AdminNasDefinition>[] physicalNas = agencyNas
                .GroupBy(
                    nas => $"{nas.Host}:{nas.SmbPort}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new AdminAgencyView(
                agency.Code,
                agency.DisplayName,
                agency.Location,
                agency.Responsible,
                agency.DefaultBackupProvider,
                agency.Enabled,
                active.Length,
                active.Count(device => device.Connectivity == "Online"),
                active.Count(device => device.Connectivity == "Attention"),
                active.Count(device => device.Connectivity == "Offline"),
                active.Count(device => device.SecurityProducts.Count > 0),
                active.Count(device => device.BackupProvider == BackupProviders.Atlas),
                active.Count(device => device.BackupProvider == BackupProviders.Acronis),
                active.Count(device => device.BackupProvider == BackupProviders.External),
                active.Count(device => device.BackupProvider == BackupProviders.SharedFolder),
                physicalNas.Length,
                physicalNas.Count(group => group.Any(nas =>
                    nas.LastProbeStatus is NasProbeStatuses.Online or
                        NasProbeStatuses.Degraded)),
                active.Length == 0
                    ? null
                    : active.Max(device => (DateTimeOffset?)device.LastSeenAtUtc),
                active.Length == 0
                    ? null
                    : active.Max(device => device.LastBackupAtUtc));
        })
        .OrderBy(agency => agency.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(agency => agency.AgencyCode, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    public async Task<IReadOnlyList<AdminBackupCoverageView>> GetBackupCoverageAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceView> devices =
            await GetViewsAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var profiles = backupControls is null ? [] : (await backupControls.ReadDashboardAsync(cancellationToken)).Profiles;
        return devices
            .Where(device => device.Enabled)
            .OrderBy(device => device.AgencyCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(device =>
            {
                var coverage = ToCoverage(device, now);
                var profile = profiles.Where(p => p.DeviceId == device.Id && p.Enabled && BackupApprovalStatuses.IsActive(p.ApprovalStatus))
                    .OrderBy(p => p.Compliance is BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue ? 1 : 0).FirstOrDefault();
                var scopedRun=profile?.LastObservationSource==BackupObservationSources.Agent?profile.LatestExecution:null;
                var destination=scopedRun?.Destination?.Split('\\',StringSplitOptions.RemoveEmptyEntries)??[];
                return profile is null ? coverage : coverage with
                {
                    NasApplianceId=profile.NasApplianceId,NasDisplayName=profile.NasDisplayName,NasHost=profile.NasHost,NasShareName=profile.NasShareName,
                    LastBackupAtUtc=profile.LastObservationAtUtc,LastBackupStatus=profile.LastObservationResult,
                    FilesCopied=scopedRun?.FilesCopied,FilesSkipped=scopedRun?.FilesSkipped,BytesCopied=scopedRun?.BytesCopied,
                    ReportedDestinationHost=destination.Length>=2?destination[0]:null,
                    ReportedDestinationShare=destination.Length>=2?string.Join('\\',destination.Skip(1)):null,
                    Compliance = profile.Compliance == BackupControlCompliance.RestoreTestDue ? BackupControlCompliance.Compliant : profile.Compliance,
                    Detail = profile.ComplianceDetail + (profile.LastAcceptableAtUtc.HasValue ? $" Última copia aceptable: {profile.LastAcceptableAtUtc:u}." : "")
                };
            })
            .ToArray();
    }

    public async Task<AdminDevicePage> GetDevicesAsync(
        string? search,
        string? agency,
        string? connectivity,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        IEnumerable<AdminDeviceView> query =
            await GetViewsAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(connectivity))
        {
            query = query.Where(device => device.Enabled);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            query = query.Where(device =>
                Contains(device.MachineName, term) ||
                Contains(device.AgencyCode, term) ||
                Contains(device.Manufacturer, term) ||
                Contains(device.Model, term) ||
                Contains(device.SerialNumber, term) ||
                Contains(device.Processor, term) ||
                Contains(device.InteractiveUserAccount, term) ||
                Contains(device.InteractiveUserDisplayName, term) ||
                device.UserProfiles.Any(profile => Contains(profile, term)));
        }

        if (!string.IsNullOrWhiteSpace(agency))
        {
            query = query.Where(device =>
                string.Equals(
                    device.AgencyCode,
                    agency.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(connectivity))
        {
            query = query.Where(device =>
                string.Equals(
                    device.Connectivity,
                    connectivity.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        AdminDeviceView[] filtered = query.ToArray();
        AdminDeviceView[] items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new AdminDevicePage(
            page,
            pageSize,
            filtered.Length,
            items);
    }

    public async Task<AdminDeviceDetail?> GetDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceData> devices =
            await GetDataAsync(cancellationToken);
        AdminDeviceData? device = devices.SingleOrDefault(item => item.Id == deviceId);
        return device is null
            ? null
            : new AdminDeviceDetail(
                ToView(device, timeProvider.GetUtcNow()),
                device.Inventory,
                device.LatestBackup);
    }

    public async Task<byte[]> ExportDevicesCsvAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceView> devices =
            await GetViewsAsync(cancellationToken);
        devices = devices.Where(device => device.Enabled).ToArray();
        StringBuilder csv = new();
        csv.Append('\uFEFF');
        csv.AppendLine(
            "Equipo,Agencia,Usuario observado,Cuenta Windows,Usuario comprobado,Estado,Ultimo reporte,Fabricante,Modelo,Serie,Procesador,RAM GB,Disco GB,Disco usado %,IPv4,Asignacion IPv4,Seguridad,Windows,Agente,BackupNAS,Proveedor backup,NAS asignado,Ultima copia");

        foreach (AdminDeviceView device in devices)
        {
            NetworkInterfaceInventory? network = device.NetworkInterfaces
                .FirstOrDefault(adapter => adapter.Ipv4Addresses.Count > 0);
            string[] values =
            [
                device.MachineName,
                device.AgencyCode,
                device.InteractiveUserDisplayName ?? string.Empty,
                device.InteractiveUserAccount ?? string.Empty,
                device.InteractiveUserCheckedAtUtc?.ToString(
                    "O",
                    CultureInfo.InvariantCulture) ?? string.Empty,
                device.Connectivity,
                device.LastSeenAtUtc.ToString("O", CultureInfo.InvariantCulture),
                device.Manufacturer ?? string.Empty,
                device.Model ?? string.Empty,
                device.SerialNumber ?? string.Empty,
                device.Processor ?? string.Empty,
                BytesToGib(device.MemoryBytes).ToString("0.##", CultureInfo.InvariantCulture),
                BytesToGib((ulong)Math.Max(0, device.DiskTotalBytes))
                    .ToString("0.##", CultureInfo.InvariantCulture),
                device.DiskUsedPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                network?.Ipv4Addresses.FirstOrDefault() ?? string.Empty,
                network?.DhcpEnabled switch
                {
                    true => "DHCP",
                    false => "Estatica",
                    null => string.Empty
                },
                string.Join(" | ", device.SecurityProducts),
                device.OperatingSystem ?? string.Empty,
                device.AgentVersion,
                device.BackupClientVersion ?? string.Empty,
                device.BackupProvider,
                device.NasDisplayName ?? string.Empty,
                device.LastBackupAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
            ];
            csv.AppendLine(string.Join(',', values.Select(EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private async Task<IReadOnlyList<AdminDeviceView>> GetViewsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminDeviceData> data =
            await GetDataAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return data.Select(device => ToView(device, now)).ToArray();
    }

    private async Task<IReadOnlyList<AdminDeviceData>> GetDataAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cachedDevices is not null && cacheExpiresAtUtc > now)
        {
            return cachedDevices;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (cachedDevices is not null && cacheExpiresAtUtc > now)
            {
                return cachedDevices;
            }

            cachedDevices = await repository.ReadDevicesAsync(cancellationToken);
            cacheExpiresAtUtc = now.Add(CacheLifetime);
            return cachedDevices;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    internal static AdminDeviceView ToView(
        AdminDeviceData data,
        DateTimeOffset now)
    {
        DeviceInventory? inventory = data.Inventory;
        long diskTotal = inventory?.Disks?.Sum(disk => Math.Max(0, disk.TotalBytes)) ?? 0;
        long diskFree = inventory?.Disks?.Sum(disk => Math.Max(0, disk.FreeBytes)) ?? 0;
        int? diskUsedPercent = diskTotal > 0
            ? (int)Math.Round(
                (1 - (diskFree / (double)diskTotal)) * 100,
                MidpointRounding.AwayFromZero)
            : null;

        return new AdminDeviceView(
            data.Id,
            data.AgencyCode,
            data.MachineName,
            data.Enabled,
            GetConnectivity(data, now),
            data.LastSeenAtUtc,
            data.EnrolledAtUtc,
            data.AgentVersion,
            data.BackupClientVersion,
            data.LatestBackup is not null && BackupTelemetry.Result(data.LatestBackup, now) == nameof(BackupRunStatus.Running),
            data.PendingEvents,
            data.InteractiveUserAccount,
            data.InteractiveUserDisplayName,
            data.InteractiveUserSid,
            data.InteractiveUserSessionType,
            data.InteractiveUserResolutionSource,
            data.InteractiveUserCheckedAtUtc,
            data.InventoryCapturedAtUtc,
            inventory?.Computer?.Manufacturer,
            inventory?.Computer?.Model,
            inventory?.Bios?.SerialNumber,
            inventory?.Processor?.Name,
            inventory?.Processor?.LogicalProcessors ?? 0,
            inventory?.Memory?.TotalBytes ?? 0,
            diskTotal,
            diskFree,
            diskUsedPercent,
            inventory?.OperatingSystem?.Description,
            inventory?.UserProfiles?.Select(profile => profile.ProfileName).ToArray() ?? [],
            inventory?.SecurityProducts?.Select(product => product.Name).ToArray() ?? [],
            inventory?.NetworkInterfaces ?? [],
            inventory?.WindowsLicense?.Edition,
            inventory?.WindowsLicense?.LicenseStatus,
            data.LastBackupAtUtc,
            data.LatestBackup is null ? null : BackupTelemetry.Result(data.LatestBackup, now),
            data.LatestBackup?.Evidence is null ? null : data.LatestBackup.FilesCopied,
            data.LatestBackup?.Evidence is null ? null : data.LatestBackup.FilesSkipped,
            data.LatestBackup?.Evidence is null ? null : data.LatestBackup.BytesCopied,
            data.RetiredAtUtc,
            data.RetiredBy,
            data.RetirementReason,
            data.BackupProvider,
            data.NasApplianceId,
            data.NasDisplayName,
            data.NasHost,
            data.NasShareName,
            data.LatestBackup?.DestinationHost,
            data.LatestBackup?.DestinationShare,
            data.UpdateRingCode, data.LatestBackup is not null && BackupTelemetry.IsAcceptable(data.LatestBackup),
            data.LastContactAtUtc);
    }

    private AdminBackupCoverageView ToCoverage(
        AdminDeviceView device,
        DateTimeOffset now)
    {
        string compliance;
        string detail;
        if (device.BackupProvider == BackupProviders.Acronis)
        {
            compliance = "EXTERNAL";
            detail = "Gestionado por Acronis; no se exige telemetria de BackupNAS.";
        }
        else if (device.BackupProvider == BackupProviders.External)
        {
            compliance = "EXTERNAL";
            detail = "Gestionado por un proveedor externo.";
        }
        else if (device.BackupProvider == BackupProviders.SharedFolder)
        {
            compliance = "NOT_APPLICABLE";
            detail = "Puesto comunal: los archivos se guardan directamente en una carpeta de red y no se exige copia por equipo.";
        }
        else if (device.NasApplianceId is null)
        {
            compliance = "CONFIGURATION_REQUIRED";
            detail = "El equipo usa ATLAS pero todavia no tiene un NAS asignado.";
        }
        else if (device.LastBackupAtUtc is null)
        {
            compliance = "MISSING";
            detail = "No se ha recibido una copia para este equipo.";
        }
        else if (!string.IsNullOrWhiteSpace(device.LastBackupDestinationHost) &&
                 !string.IsNullOrWhiteSpace(device.LastBackupDestinationShare) &&
                 (!string.Equals(
                     device.LastBackupDestinationHost,
                     device.NasHost,
                     StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(
                     device.LastBackupDestinationShare,
                     device.NasShareName,
                     StringComparison.OrdinalIgnoreCase)))
        {
            compliance = "DESTINATION_MISMATCH";
            detail = "La copia reporto un NAS o recurso distinto del asignado.";
        }
        else if (IsBeforeOperationalToday(device.LastBackupAtUtc.Value, now))
        {
            compliance = "OVERDUE";
            detail = "La ultima copia corresponde a un dia anterior en la zona operativa.";
        }
        else if (device.LastBackupAcceptable)
        {
            compliance = "COMPLIANT";
            detail = device.LastBackupStatus == nameof(BackupRunStatus.Warning)
                ? "Copia completada con advertencias; aceptable dentro de la ventana esperada."
                : "Copia correcta dentro de la ventana esperada.";
        }
        else if (device.LastBackupStatus == nameof(BackupRunStatus.Warning))
        {
            compliance = "WARNING";
            detail = "Reporte anterior con advertencias; no contiene evidencia suficiente para confirmar el alcance completo.";
        }
        else if (device.LastBackupStatus == nameof(BackupRunStatus.Cancelled))
        {
            compliance = "CANCELLED";
            detail = "La última copia fue cancelada; no confirma una copia completa.";
        }
        else if (device.LastBackupStatus == nameof(BackupRunStatus.Failed))
        {
            compliance = "FAILED";
            detail = "La última copia terminó con fallo.";
        }
        else if (device.LastBackupStatus is nameof(BackupRunStatus.Running) or nameof(BackupRunStatus.Skipped) or nameof(BackupRunStatus.Partial))
        {
            compliance = device.LastBackupStatus.ToUpperInvariant();
            detail = device.LastBackupStatus switch
            {
                nameof(BackupRunStatus.Running) => "En curso; todavía no existe un cierre confirmado.",
                nameof(BackupRunStatus.Skipped) => "El usuario cerró la aplicación sin iniciar la copia.",
                _ => "La ejecución dejó parte del alcance sin completar."
            };
        }
        else
        {
            compliance = "MISSING";
            detail = "El resultado recibido no permite confirmar una copia completa.";
        }

        return new AdminBackupCoverageView(
            device.Id,
            device.MachineName,
            device.AgencyCode,
            device.BackupProvider,
            device.NasApplianceId,
            device.NasDisplayName,
            device.NasHost,
            device.NasShareName,
            device.LastBackupAtUtc,
            device.LastBackupStatus,
            device.LastBackupFilesCopied,
            device.LastBackupFilesSkipped,
            device.LastBackupBytesCopied,
            device.LastBackupDestinationHost,
            device.LastBackupDestinationShare,
            compliance,
            detail);
    }

    private bool IsBeforeOperationalToday(
        DateTimeOffset value,
        DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, operationalTimeZone).DateTime) <
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, operationalTimeZone).DateTime);

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        string configured = string.IsNullOrWhiteSpace(timeZoneId)
            ? "America/Guatemala"
            : timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configured);
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

    private static string GetConnectivity(
        AdminDeviceData device,
        DateTimeOffset now)
    {
        if (!device.Enabled)
        {
            return "Retired";
        }

        // La recepción del servidor no depende del reloj del agente. Los registros
        // heredados conservan su fecha original, sin inventar una recepción.
        TimeSpan age = now - (device.LastContactAtUtc ?? device.LastSeenAtUtc);
        if (age < TimeSpan.Zero)
        {
            return "Attention";
        }

        if (age <= TimeSpan.FromMinutes(15))
        {
            return "Online";
        }

        return age <= TimeSpan.FromHours(24)
            ? "Attention"
            : "Offline";
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    private static double BytesToGib(ulong bytes) =>
        bytes / 1024d / 1024d / 1024d;

    private static string EscapeCsv(string value)
    {
        string safeValue = value;
        if (safeValue.Length > 0 && safeValue[0] is '=' or '+' or '-' or '@')
        {
            safeValue = "'" + safeValue;
        }

        return $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public void Invalidate()
    {
        cachedDevices = null;
        cacheExpiresAtUtc = DateTimeOffset.MinValue;
    }
}
