using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed record UpdateState(
    int SchemaVersion,
    Guid AppliedReleaseId,
    string ReleaseVersion,
    DateTimeOffset AppliedAtUtc);

internal sealed record PendingUpdateStatus(
    int SchemaVersion,
    Guid ReleaseId,
    string ReleaseVersion,
    DateTimeOffset DetectedAtUtc,
    string Reason);

internal sealed record VerifiedRelease(
    AtlasReleaseManifest Manifest,
    byte[] ManifestBytes,
    byte[] SignatureBytes);

internal sealed record DownloadedPackage(
    AtlasReleasePackage Package,
    string Path);
