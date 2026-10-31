namespace Miguel.Contracts;

public sealed record DeviceInventory(
    int SchemaVersion,
    Guid SnapshotId,
    DateTimeOffset CapturedAtUtc,
    string MachineName,
    OperatingSystemInventory OperatingSystem,
    ComputerInventory Computer,
    ProcessorInventory Processor,
    MemoryInventory Memory,
    BaseBoardInventory BaseBoard,
    BiosInventory Bios,
    IReadOnlyList<DiskInventory> Disks,
    IReadOnlyList<UserProfileInventory> UserProfiles,
    IReadOnlyList<SecurityProductInventory> SecurityProducts,
    WindowsLicenseInventory? WindowsLicense,
    IReadOnlyList<NetworkInterfaceInventory>? NetworkInterfaces = null,
    SoftwareInventory? Software = null);

public sealed record OperatingSystemInventory(
    string Description,
    string Version,
    string Architecture,
    string? BuildNumber,
    DateTimeOffset? LastBootAtUtc);

public sealed record ComputerInventory(
    string? Manufacturer,
    string? Model);

public sealed record ProcessorInventory(
    string? Name,
    int LogicalProcessors,
    int? MaxClockMegahertz);

public sealed record MemoryInventory(ulong TotalBytes);

public sealed record BaseBoardInventory(
    string? Manufacturer,
    string? Product,
    string? SerialNumber);

public sealed record BiosInventory(
    string? Version,
    string? SerialNumber);

public sealed record DiskInventory(
    string Name,
    string? VolumeLabel,
    long TotalBytes,
    long FreeBytes,
    string DriveFormat);

public sealed record UserProfileInventory(string ProfileName);

public sealed record SecurityProductInventory(
    string Name,
    int? ProductState,
    string? ExecutablePath);

public sealed record NetworkInterfaceInventory(
    string Description,
    string? MacAddress,
    bool? DhcpEnabled,
    IReadOnlyList<string> Ipv4Addresses,
    IReadOnlyList<string> DefaultGateways);

public sealed record WindowsLicenseInventory(
    string? Edition,
    int LicenseStatus,
    string? Channel,
    string? PartialProductKey);
