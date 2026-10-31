namespace Miguel.Server;

public sealed class ServerOptions
{
    public const string SectionName = "Miguel";

    public bool AllowInsecureHttp { get; init; }
    public string? DataProtectionPath { get; init; }
    public string? DistributionPath { get; init; }
    public string? ReleaseStoragePath { get; init; }
    public string? ReleasePublicKeyPath { get; init; }
    public long MaxReleaseUploadBytes { get; init; } = 300_000_000;
    public string? TrainingEvidencePath { get; init; }
    public long MaxTrainingAttendanceUploadBytes { get; init; } = 10_000_000;
    public string? GovernanceDocumentPath { get; init; }
    public long MaxGovernanceDocumentUploadBytes { get; init; } = 25_000_000;
    public string? BackupEvidencePath { get; init; }
    public long MaxBackupEvidenceUploadBytes { get; init; } = 25_000_000;
    public string? PublicCertificatePath { get; init; }
    public string? PublicBaseUrl { get; init; }
    public string? NasMountRoot { get; init; }
    public string? NasProvisionerSocketPath { get; init; }
    public int NasProbeIntervalMinutes { get; init; } = 5;
    public int NasProbeTimeoutSeconds { get; init; } = 5;
    public int SecurityEventRetentionDays { get; init; } = 90;
    public int SecurityPostureRetentionDays { get; init; } = 365;
    public int SecurityPostureFreshnessHours { get; init; } = 24;
    public string OperationalTimeZoneId { get; init; } = "America/Guatemala";
    public int AdminNotificationRetentionDays { get; init; } = 180;
    public bool NetworkDiscoveryEnabled { get; init; }
    public bool NetworkDiscoveryAutomaticEnabled { get; init; }
    public string NetworkDiscoveryNmapPath { get; init; } = "/usr/bin/nmap";
    public int NetworkDiscoveryIntervalHours { get; init; } = 12;
    public int NetworkDiscoveryStartupDelaySeconds { get; init; } = 300;
    public int NetworkDiscoveryScopeTimeoutSeconds { get; init; } = 180;
    public int NetworkDiscoveryMaxPacketsPerSecond { get; init; } = 50;
    public string NetworkDiscoveryPingPath { get; init; } = "/usr/local/libexec/atlas-network/ping";
    public int NetworkDiscoveryWorkers { get; init; } = 2;
    public int NetworkDiscoveryInventoryMaxAgeMinutes { get; init; } = 30;
}
