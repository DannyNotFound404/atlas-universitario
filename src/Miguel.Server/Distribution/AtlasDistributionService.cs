using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Miguel.Contracts;
using Miguel.Server.Audit;
using Miguel.Server.Storage;

namespace Miguel.Server.Distribution;

public sealed class AtlasDistributionService(
    IOptions<ServerOptions> optionsAccessor,
    AtlasReleaseService releaseService,
    MiguelRepository repository,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private AtlasPackageCatalog? cachedCatalog;
    private DateTimeOffset catalogExpiresAtUtc;

    public void InvalidateCatalog()
    {
        cachedCatalog = null;
        catalogExpiresAtUtc = default;
    }

    public async Task<AtlasReleaseView> GetReleaseAsync(
        CancellationToken cancellationToken)
    {
        AtlasPackageCatalog catalog = await ReadCatalogAsync(cancellationToken);
        string? releaseVersion =
            await releaseService.GetActiveReleaseVersionAsync(cancellationToken);
        return new AtlasReleaseView(
            catalog.SchemaVersion,
            releaseVersion,
            catalog.PublishedAtUtc,
            catalog.Packages.Select(package => new AtlasReleasePackageView(
                package.Component,
                package.Version,
                $"/api/v1/agent/packages/{Uri.EscapeDataString(package.Component)}",
                package.Sha256,
                package.SizeBytes,
                package.ContentType)).ToArray());
    }

    public async Task<AtlasPackageFile?> GetPackageAsync(
        string component,
        CancellationToken cancellationToken)
    {
        AtlasPackageCatalog catalog = await ReadCatalogAsync(cancellationToken);
        AtlasPackageEntry? package = catalog.Packages.SingleOrDefault(candidate =>
            string.Equals(
                candidate.Component,
                component,
                StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return null;
        }

        string path = ResolvePackagePath(package.FileName);
        return new AtlasPackageFile(path, package.FileName, package.ContentType);
    }

    public async Task<AtlasGeneratedInstaller> CreateInstallerAsync(
        AtlasInstallerRequest request,
        EnrollmentIssuanceContext issuanceContext,
        CancellationToken cancellationToken)
    {
        string agencyCode = request.AgencyCode.Trim().ToUpperInvariant();
        int lifetimeHours = Math.Clamp(request.LifetimeHours, 1, 72);
        AtlasPackageCatalog catalog = await ReadCatalogAsync(cancellationToken);
        _ = GetPublicBaseUri();

        AtlasPackageEntry? setupPackage = catalog.Packages.SingleOrDefault(
            package => string.Equals(
                package.Component,
                "setup",
                StringComparison.OrdinalIgnoreCase));
        string? installerVersion = setupPackage?.Version;

        EnrollmentTokenIssue issue =
            await repository.IssueEnrollmentTokenAsync(
                agencyCode,
                TimeSpan.FromHours(lifetimeHours),
                issuanceContext with { InstallerVersion = installerVersion },
                cancellationToken);
        string physicalPath = ResolvePackagePath("ATLAS.Setup.exe");
        return new AtlasGeneratedInstaller(
            physicalPath,
            $"ATLAS-Setup_{issue.Token}.exe",
            issue.IssuanceId,
            issue.ExpiresAtUtc,
            issue.InstallerVersion);
    }

    private Uri GetPublicBaseUri()
    {
        string? configured = optionsAccessor.Value.PublicBaseUrl?.Trim();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Miguel:PublicBaseUrl debe ser una URL absoluta sin " +
                "credenciales, consulta ni fragmento.");
        }

        bool validHttps = uri.Scheme == Uri.UriSchemeHttps;
        bool validDevelopmentHttp =
            optionsAccessor.Value.AllowInsecureHttp &&
            uri.Scheme == Uri.UriSchemeHttp &&
            uri.IsLoopback;
        if (!validHttps && !validDevelopmentHttp)
        {
            throw new InvalidOperationException(
                "Miguel:PublicBaseUrl debe usar HTTPS.");
        }

        return EnsureTrailingSlash(uri);
    }

    private async Task<AtlasPackageCatalog> ReadCatalogAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cachedCatalog is not null && catalogExpiresAtUtc > now)
        {
            return cachedCatalog;
        }

        await catalogLock.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (cachedCatalog is not null && catalogExpiresAtUtc > now)
            {
                return cachedCatalog;
            }

            string distributionPath = GetDistributionRoot();
            string catalogPath = Path.Combine(
                distributionPath,
                "atlas-package-catalog.json");
            await using FileStream stream = File.OpenRead(catalogPath);
            AtlasPackageCatalog? catalog =
                await JsonSerializer.DeserializeAsync<AtlasPackageCatalog>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            if (catalog is null ||
                catalog.SchemaVersion != ApiContract.SchemaVersion ||
                catalog.Packages.Count is 0 or > 20)
            {
                throw new InvalidDataException(
                    "El catálogo de distribución de ATLAS no es válido.");
            }

            HashSet<string> components = new(StringComparer.OrdinalIgnoreCase);
            foreach (AtlasPackageEntry package in catalog.Packages)
            {
                ValidatePackage(package, components);
                string physicalPath = ResolvePackagePath(package.FileName);
                FileInfo information = new(physicalPath);
                if (information.Length != package.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"El tamaño de {package.Component} no coincide con el catálogo.");
                }

                string actualHash = await ComputeSha256Async(
                    physicalPath,
                    cancellationToken);
                if (!string.Equals(
                        actualHash,
                        package.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"El hash de {package.Component} no coincide con el catálogo.");
                }
            }

            cachedCatalog = catalog;
            catalogExpiresAtUtc = now.AddMinutes(1);
            return catalog;
        }
        finally
        {
            catalogLock.Release();
        }
    }

    private void ValidatePackage(
        AtlasPackageEntry package,
        ISet<string> components)
    {
        if (string.IsNullOrWhiteSpace(package.Component) ||
            package.Component.Length > 32 ||
            !components.Add(package.Component) ||
            string.IsNullOrWhiteSpace(package.Version) ||
            package.Version.Length > 64 ||
            package.SizeBytes <= 0 ||
            package.SizeBytes > 500_000_000 ||
            package.Sha256.Length != 64 ||
            package.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            Path.GetFileName(package.FileName) != package.FileName ||
            string.IsNullOrWhiteSpace(package.ContentType) ||
            package.ContentType.Length > 128)
        {
            throw new InvalidDataException(
                "El catálogo contiene un paquete inválido.");
        }
    }

    private string ResolvePackagePath(string fileName)
    {
        string root = GetDistributionRoot();
        string path = Path.GetFullPath(Path.Combine(root, fileName));
        string boundary = Path.TrimEndingDirectorySeparator(root) +
                          Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "El catálogo intentó acceder fuera de distribución.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "No se encontró un paquete publicado.",
                path);
        }

        return path;
    }

    private static string GetRequiredDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                "Miguel:DistributionPath no está configurado.");
        }

        string path = Path.GetFullPath(configuredPath);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                "No existe el directorio de distribución de ATLAS.");
        }

        return path;
    }

    private string GetDistributionRoot()
    {
        string? activeRelease = releaseService.TryGetActiveDistributionPath();
        return activeRelease is not null
            ? activeRelease
            : GetRequiredDirectory(optionsAccessor.Value.DistributionPath);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        UriBuilder builder = new(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }
        return builder.Uri;
    }
}
