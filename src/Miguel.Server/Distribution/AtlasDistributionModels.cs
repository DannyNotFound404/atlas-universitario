namespace Miguel.Server.Distribution;

public sealed record AtlasPackageCatalog(
    int SchemaVersion,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<AtlasPackageEntry> Packages);

public sealed record AtlasPackageEntry(
    string Component,
    string Version,
    string FileName,
    string Sha256,
    long SizeBytes,
    string ContentType);

public sealed record AtlasReleaseView(
    int SchemaVersion,
    string? ReleaseVersion,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<AtlasReleasePackageView> Packages);

public sealed record AtlasReleasePackageView(
    string Component,
    string Version,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string ContentType);

public sealed record AtlasInstallerRequest(
    string AgencyCode,
    int LifetimeHours = 24);

public sealed record AtlasPackageFile(
    string PhysicalPath,
    string FileName,
    string ContentType);

public sealed record AtlasGeneratedInstaller(
    string PhysicalPath,
    string DownloadFileName,
    Guid IssuanceId,
    DateTimeOffset ExpiresAtUtc,
    string? InstallerVersion);

public sealed record AtlasReleaseStoreView(
    Guid? ActiveReleaseId,
    IReadOnlyList<AtlasStoredReleaseView> Releases);

public sealed record AtlasStoredReleaseView(
    Guid ReleaseId,
    string ReleaseVersion,
    DateTimeOffset CreatedAtUtc,
    bool IsActive,
    bool SignatureVerified,
    IReadOnlyList<AtlasReleasePackageView> Packages,
    Miguel.Contracts.AtlasReleasePresentation? Presentation);

public sealed record AtlasServerReleaseCatalogView(
    string ActiveReleaseVersion,
    IReadOnlyList<AtlasServerReleaseView> Releases);

public sealed record AtlasServerReleaseView(
    string ReleaseVersion,
    DateTimeOffset ReleasedAtUtc,
    string? PreviousReleaseVersion,
    string Summary,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> ChangesFromPrevious,
    string ApplicationRestoreOutcome,
    string FullRestoreOutcome,
    bool IsActive,
    bool ObservedOnServer,
    DateTimeOffset? FirstActivatedAtUtc,
    DateTimeOffset? LastActivatedAtUtc,
    string? ApplicationSha256,
    int? SchemaMigrationCount,
    string EvidenceSource);
