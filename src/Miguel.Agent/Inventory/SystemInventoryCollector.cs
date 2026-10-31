using System.Globalization;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Miguel.Contracts;

namespace Miguel.Agent.Inventory;

public sealed class SystemInventoryCollector(
    TimeProvider timeProvider,
    ILogger<SystemInventoryCollector> logger)
{
    private static readonly HashSet<string> IgnoredProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "All Users",
        "Default",
        "Default User",
        "defaultuser0",
        "Public"
    };

    public DeviceInventory Capture()
    {
        Dictionary<string, object?> computer = QueryFirst(
            @"root\cimv2",
            "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
        Dictionary<string, object?> processor = QueryFirst(
            @"root\cimv2",
            "SELECT Name, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        Dictionary<string, object?> board = QueryFirst(
            @"root\cimv2",
            "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
        Dictionary<string, object?> bios = QueryFirst(
            @"root\cimv2",
            "SELECT SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS");
        Dictionary<string, object?> operatingSystem = QueryFirst(
            @"root\cimv2",
            "SELECT Caption, Version, BuildNumber, LastBootUpTime FROM Win32_OperatingSystem");

        return new DeviceInventory(
            ApiContract.SchemaVersion,
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            Environment.MachineName,
            new OperatingSystemInventory(
                ReadString(operatingSystem, "Caption") ?? RuntimeInformation.OSDescription,
                ReadString(operatingSystem, "Version") ?? Environment.OSVersion.VersionString,
                RuntimeInformation.OSArchitecture.ToString(),
                ReadString(operatingSystem, "BuildNumber"),
                ReadWmiDateTime(operatingSystem, "LastBootUpTime")),
            new ComputerInventory(
                ReadString(computer, "Manufacturer"),
                ReadString(computer, "Model")),
            new ProcessorInventory(
                ReadString(processor, "Name"),
                ReadInt(processor, "NumberOfLogicalProcessors") ?? Environment.ProcessorCount,
                ReadInt(processor, "MaxClockSpeed")),
            new MemoryInventory(
                ReadUlong(computer, "TotalPhysicalMemory") ?? ReadPhysicalMemoryFallback()),
            new BaseBoardInventory(
                ReadString(board, "Manufacturer"),
                ReadString(board, "Product"),
                ReadString(board, "SerialNumber")),
            new BiosInventory(
                ReadString(bios, "SMBIOSBIOSVersion"),
                ReadString(bios, "SerialNumber")),
            CaptureDisks(),
            CaptureUserProfiles(),
            CaptureSecurityProducts(),
            CaptureWindowsLicense(),
            CaptureNetworkInterfaces(),
            new SoftwareInventoryCollector().Capture());
    }

    private Dictionary<string, object?> QueryFirst(string scope, string query)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();
            ManagementBaseObject? first = results.Cast<ManagementBaseObject>().FirstOrDefault();
            if (first is null)
            {
                return [];
            }

            return first.Properties
                .Cast<PropertyData>()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo ejecutar la consulta WMI: {Query}", query);
            return [];
        }
    }

    private IReadOnlyList<DiskInventory> CaptureDisks()
    {
        List<DiskInventory> disks = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives()
                     .Where(candidate => candidate.DriveType == DriveType.Fixed))
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                disks.Add(new DiskInventory(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.DriveFormat));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo leer el disco {Disk}.", drive.Name);
            }
        }

        return disks;
    }

    private IReadOnlyList<UserProfileInventory> CaptureUserProfiles()
    {
        string profilesRoot = GetProfilesRoot();

        try
        {
            return Directory.EnumerateDirectories(profilesRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IgnoredProfiles.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new UserProfileInventory(name!))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron enumerar los perfiles locales.");
            return [];
        }
    }

    private static string GetProfilesRoot()
    {
        const string profileListKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(profileListKey);
            string? configuredDirectory = Convert.ToString(
                key?.GetValue(
                    "ProfilesDirectory",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames),
                CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                return Environment.ExpandEnvironmentVariables(configuredDirectory);
            }
        }
        catch
        {
            // El valor predeterminado sigue siendo válido en instalaciones
            // donde la cuenta del servicio no pueda consultar esta clave.
        }

        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return Path.Combine(systemRoot, "Users");
    }

    private IReadOnlyList<SecurityProductInventory> CaptureSecurityProducts()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                @"root\SecurityCenter2",
                "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct");
            using ManagementObjectCollection results = searcher.Get();
            return results.Cast<ManagementBaseObject>()
                .Select(item => new SecurityProductInventory(
                    Convert.ToString(item["displayName"], CultureInfo.InvariantCulture) ?? "Desconocido",
                    ConvertNullableInt(item["productState"]),
                    Convert.ToString(item["pathToSignedProductExe"], CultureInfo.InvariantCulture)))
                .OrderBy(product => product.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar SecurityCenter2.");
            return [];
        }
    }

    private WindowsLicenseInventory? CaptureWindowsLicense()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                @"root\cimv2",
                """
                SELECT Name, Description, LicenseStatus, PartialProductKey
                FROM SoftwareLicensingProduct
                WHERE PartialProductKey IS NOT NULL
                """);
            using ManagementObjectCollection results = searcher.Get();
            ManagementBaseObject? windowsLicense = results
                .Cast<ManagementBaseObject>()
                .FirstOrDefault(item =>
                    Convert.ToString(item["Name"], CultureInfo.InvariantCulture)
                        ?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true);

            return windowsLicense is null
                ? null
                : new WindowsLicenseInventory(
                    Convert.ToString(windowsLicense["Name"], CultureInfo.InvariantCulture),
                    ConvertNullableInt(windowsLicense["LicenseStatus"]) ?? 0,
                    Convert.ToString(windowsLicense["Description"], CultureInfo.InvariantCulture),
                    Convert.ToString(windowsLicense["PartialProductKey"], CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar el estado de activación de Windows.");
            return null;
        }
    }

    private IReadOnlyList<NetworkInterfaceInventory> CaptureNetworkInterfaces()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                @"root\cimv2",
                """
                SELECT Description, DHCPEnabled, IPAddress, DefaultIPGateway, MACAddress
                FROM Win32_NetworkAdapterConfiguration
                WHERE IPEnabled = TRUE
                """);
            using ManagementObjectCollection results = searcher.Get();

            return results
                .Cast<ManagementBaseObject>()
                .Select(item =>
                {
                    string[] ipv4Addresses = ReadStringArray(item["IPAddress"])
                        .Where(IsUsefulIpv4Address)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    string[] gateways = ReadStringArray(item["DefaultIPGateway"])
                        .Where(IsIpv4Address)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new NetworkInterfaceInventory(
                        Convert.ToString(
                            item["Description"],
                            CultureInfo.InvariantCulture)?.Trim() ??
                        "Adaptador de red",
                        Convert.ToString(
                            item["MACAddress"],
                            CultureInfo.InvariantCulture)?.Trim(),
                        item["DHCPEnabled"] is null
                            ? null
                            : Convert.ToBoolean(
                                item["DHCPEnabled"],
                                CultureInfo.InvariantCulture),
                        ipv4Addresses,
                        gateways);
                })
                .Where(adapter => adapter.Ipv4Addresses.Count > 0)
                .OrderBy(adapter => adapter.DhcpEnabled == true)
                .ThenBy(
                    adapter => adapter.Description,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar la configuración IPv4.");
            return [];
        }
    }

    private static IEnumerable<string> ReadStringArray(object? value) =>
        value switch
        {
            string[] items =>
                items.Where(item => !string.IsNullOrWhiteSpace(item)),
            IEnumerable<string> items =>
                items.Where(item => !string.IsNullOrWhiteSpace(item)),
            string item when !string.IsNullOrWhiteSpace(item) => [item],
            _ => []
        };

    private static bool IsUsefulIpv4Address(string value) =>
        IPAddress.TryParse(value, out IPAddress? address) &&
        address.AddressFamily ==
        System.Net.Sockets.AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) &&
        !value.StartsWith("169.254.", StringComparison.Ordinal);

    private static bool IsIpv4Address(string value) =>
        IPAddress.TryParse(value, out IPAddress? address) &&
        address.AddressFamily ==
        System.Net.Sockets.AddressFamily.InterNetwork;

    private static string? ReadString(
        IReadOnlyDictionary<string, object?> values,
        string key) =>
        values.TryGetValue(key, out object? value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim()
            : null;

    private static int? ReadInt(
        IReadOnlyDictionary<string, object?> values,
        string key) =>
        values.TryGetValue(key, out object? value)
            ? ConvertNullableInt(value)
            : null;

    private static int? ConvertNullableInt(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static ulong? ReadUlong(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        try
        {
            return values.TryGetValue(key, out object? value) && value is not null
                ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadWmiDateTime(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        string? wmiValue = ReadString(values, key);
        if (string.IsNullOrWhiteSpace(wmiValue))
        {
            return null;
        }

        try
        {
            DateTime local = ManagementDateTimeConverter.ToDateTime(wmiValue);
            return new DateTimeOffset(local).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static ulong ReadPhysicalMemoryFallback()
    {
        MemoryStatusEx memoryStatus = new();
        return GlobalMemoryStatusEx(memoryStatus)
            ? memoryStatus.TotalPhysical
            : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
