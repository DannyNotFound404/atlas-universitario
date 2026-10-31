namespace Miguel.Contracts;

public static class AtlasReleaseComponents
{
    public const string Agent = "agent";
    public const string BackupNas = "backupnas";
    public const string BackupService = "backup-service";
    public const string Updater = "updater";
    public const string Setup = "setup";

    public static readonly IReadOnlySet<string> Required =
        new HashSet<string>(
            [Agent, BackupNas, Updater, Setup],
            StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(
            [Agent, BackupNas, BackupService, Updater, Setup],
            StringComparer.OrdinalIgnoreCase);

    public static bool IsValidReleaseSet(IEnumerable<string> components)
    {
        HashSet<string> set = new(
            components,
            StringComparer.OrdinalIgnoreCase);
        return Required.IsSubsetOf(set) && set.IsSubsetOf(Allowed);
    }
}

public sealed record AtlasReleaseManifest(
    int SchemaVersion,
    Guid ReleaseId,
    string ReleaseVersion,
    DateTimeOffset CreatedAtUtc,
    string SignatureAlgorithm,
    string MinimumUpdaterVersion,
    IReadOnlyList<AtlasReleasePackage> Packages);

public sealed record AtlasReleasePackage(
    string Component,
    string Version,
    string FileName,
    string Sha256,
    long SizeBytes,
    string ContentType);

/// <summary>
/// Descripción humana firmada de un release. Se conserva fuera del manifiesto
/// que consumen los updaters existentes para mantener compatibilidad estricta
/// con la flota ya desplegada.
/// </summary>
public sealed record AtlasReleasePresentation(
    int SchemaVersion,
    Guid ReleaseId,
    string ReleaseVersion,
    string? PreviousReleaseVersion,
    string Summary,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> ChangesFromPrevious,
    string RestoreOutcome);

public sealed record AtlasSignedReleaseEnvelope(
    string ManifestBase64,
    string SignatureBase64);
