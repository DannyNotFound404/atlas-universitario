using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using Microsoft.Win32;
using Miguel.Contracts;

namespace Miguel.Agent.Security;

public sealed class SecurityPostureCollector(
    TimeProvider timeProvider,
    ILogger<SecurityPostureCollector> logger)
{
    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public SecurityPostureSnapshot Capture()
    {
        List<string> warnings = [];
        return new SecurityPostureSnapshot(
            ApiContract.SchemaVersion,
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            CaptureFirewallProfiles(warnings),
            CaptureBitLocker(warnings),
            CaptureSecureBoot(warnings),
            CaptureTpm(warnings),
            CaptureRemoteDesktop(warnings),
            CaptureSmb1(warnings),
            CapturePendingReboot(warnings),
            CanReadSecurityEventLog(warnings),
            CaptureInstalledSoftware(warnings),
            CaptureInstalledUpdates(warnings),
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private IReadOnlyList<FirewallProfileInventory> CaptureFirewallProfiles(
        ICollection<string> warnings)
    {
        try
        {
            return Query(
                    @"root\StandardCimv2",
                    "SELECT Name, Enabled FROM MSFT_NetFirewallProfile")
                .Select(item => new FirewallProfileInventory(
                    ReadString(item, "Name") ?? "Perfil desconocido",
                    ReadBool(item, "Enabled")))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar el Firewall de Windows.");
            warnings.Add("firewall_unavailable");
            return [];
        }
    }

    private IReadOnlyList<BitLockerVolumeInventory> CaptureBitLocker(
        ICollection<string> warnings)
    {
        try
        {
            return Query(
                    @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                    "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume")
                .Select(item => new BitLockerVolumeInventory(
                    ReadString(item, "DriveLetter") ?? string.Empty,
                    ReadInt(item, "ProtectionStatus") switch
                    {
                        1 => true,
                        0 => false,
                        _ => null
                    },
                    ReadInt(item, "ProtectionStatus"),
                    ReadInt(item, "ConversionStatus")))
                .Where(item => !string.IsNullOrWhiteSpace(item.DriveLetter))
                .OrderBy(item => item.DriveLetter, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (ManagementException ex) when (
            ex.ErrorCode is ManagementStatus.InvalidNamespace or
                ManagementStatus.InvalidClass)
        {
            warnings.Add("bitlocker_not_supported");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar BitLocker.");
            warnings.Add("bitlocker_unavailable");
            return [];
        }
    }

    private bool? CaptureSecureBoot(ICollection<string> warnings)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            object? value = key?.GetValue("UEFISecureBootEnabled");
            return value is null
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar Secure Boot.");
            warnings.Add("secure_boot_unavailable");
            return null;
        }
    }

    private TpmInventory CaptureTpm(ICollection<string> warnings)
    {
        try
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> results = Query(
                @"root\CIMV2\Security\MicrosoftTpm",
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm");
            IReadOnlyDictionary<string, object?>? item = results.FirstOrDefault();
            return item is null
                ? new TpmInventory(false, false, false, null)
                : new TpmInventory(
                    true,
                    ReadBool(item, "IsEnabled_InitialValue"),
                    ReadBool(item, "IsActivated_InitialValue"),
                    ReadString(item, "SpecVersion"));
        }
        catch (ManagementException ex) when (
            ex.ErrorCode is ManagementStatus.InvalidNamespace or
                ManagementStatus.InvalidClass)
        {
            return new TpmInventory(false, false, false, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar el TPM.");
            warnings.Add("tpm_unavailable");
            return new TpmInventory(null, null, null, null);
        }
    }

    private bool? CaptureRemoteDesktop(ICollection<string> warnings)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server");
            object? value = key?.GetValue("fDenyTSConnections");
            return value is null
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar Escritorio remoto.");
            warnings.Add("remote_desktop_unavailable");
            return null;
        }
    }

    private bool? CaptureSmb1(ICollection<string> warnings)
    {
        try
        {
            IReadOnlyDictionary<string, object?>? feature = Query(
                    @"root\cimv2",
                    "SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'SMB1Protocol'")
                .FirstOrDefault();
            int? state = feature is null ? null : ReadInt(feature, "InstallState");
            return state switch
            {
                1 => true,
                2 or 3 => false,
                _ => null
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar SMBv1.");
            warnings.Add("smb1_unavailable");
            return null;
        }
    }

    private bool? CapturePendingReboot(ICollection<string> warnings)
    {
        try
        {
            using RegistryKey? componentBasedServicingKey =
                Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using RegistryKey? windowsUpdateKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using RegistryKey? sessionManager = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            bool pendingRename = sessionManager?.GetValue(
                "PendingFileRenameOperations") is not null;
            return componentBasedServicingKey is not null ||
                   windowsUpdateKey is not null ||
                   pendingRename;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar el reinicio pendiente.");
            warnings.Add("pending_reboot_unavailable");
            return null;
        }
    }

    private bool CanReadSecurityEventLog(ICollection<string> warnings)
    {
        try
        {
            using EventLogSession session = new();
            _ = session.GetLogInformation("Security", PathType.LogName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "El agente no puede leer el registro de seguridad.");
            warnings.Add("security_event_log_unavailable");
            return false;
        }
    }

    private IReadOnlyList<InstalledSoftwareInventory> CaptureInstalledSoftware(
        ICollection<string> warnings)
    {
        try
        {
            Dictionary<string, InstalledSoftwareInventory> software =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryView view in new[]
                     {
                         RegistryView.Registry64,
                         RegistryView.Registry32
                     })
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? product = uninstall.OpenSubKey(subKeyName);
                    string? name = ReadRegistryString(product, "DisplayName");
                    if (string.IsNullOrWhiteSpace(name) ||
                        ReadRegistryInt(product, "SystemComponent") == 1 ||
                        !string.IsNullOrWhiteSpace(
                            ReadRegistryString(product, "ParentKeyName")))
                    {
                        continue;
                    }

                    InstalledSoftwareInventory item = new(
                        Limit(name, 256)!,
                        Limit(ReadRegistryString(product, "DisplayVersion"), 128),
                        Limit(ReadRegistryString(product, "Publisher"), 256),
                        Limit(ReadRegistryString(product, "InstallDate"), 32));
                    string key = $"{item.Name}\u001f{item.Version}\u001f{item.Publisher}";
                    software.TryAdd(key, item);
                }
            }

            return software.Values
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Take(4096)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo enumerar el software instalado.");
            warnings.Add("software_inventory_unavailable");
            return [];
        }
    }

    private IReadOnlyList<InstalledUpdateInventory> CaptureInstalledUpdates(
        ICollection<string> warnings)
    {
        try
        {
            return Query(
                    @"root\cimv2",
                    "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering")
                .Select(item => new InstalledUpdateInventory(
                    Limit(ReadString(item, "HotFixID"), 64) ?? "Desconocida",
                    Limit(ReadString(item, "Description"), 128),
                    ParseInstalledDate(ReadString(item, "InstalledOn"))))
                .Where(item => item.HotFixId != "Desconocida")
                .DistinctBy(item => item.HotFixId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.InstalledAtUtc)
                .Take(4096)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron enumerar las actualizaciones instaladas.");
            warnings.Add("update_inventory_unavailable");
            return [];
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(
        string scope,
        string query)
    {
        using ManagementObjectSearcher searcher = new(scope, query);
        using ManagementObjectCollection results = searcher.Get();
        return results.Cast<ManagementBaseObject>()
            .Select(item => (IReadOnlyDictionary<string, object?>)item.Properties
                .Cast<PropertyData>()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value,
                    StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, object?> values,
        string name) =>
        values.TryGetValue(name, out object? value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim()
            : null;

    private static int? ReadInt(
        IReadOnlyDictionary<string, object?> values,
        string name)
    {
        try
        {
            return values.TryGetValue(name, out object? value) && value is not null
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBool(
        IReadOnlyDictionary<string, object?> values,
        string name)
    {
        try
        {
            return values.TryGetValue(name, out object? value) && value is not null
                ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRegistryString(RegistryKey? key, string name) =>
        Convert.ToString(
            key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            CultureInfo.InvariantCulture)?.Trim();

    private static int? ReadRegistryInt(RegistryKey? key, string name)
    {
        try
        {
            object? value = key?.GetValue(name);
            return value is null
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseInstalledDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime parsed) &&
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out parsed))
        {
            return null;
        }

        return new DateTimeOffset(parsed).ToUniversalTime();
    }

    private static string? Limit(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
}
