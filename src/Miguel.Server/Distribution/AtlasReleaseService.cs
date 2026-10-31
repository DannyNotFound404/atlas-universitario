using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Miguel.Contracts;

namespace Miguel.Server.Distribution;

public sealed class AtlasReleaseService(
    IOptions<ServerOptions> optionsAccessor,
    TimeProvider timeProvider,
    Npgsql.NpgsqlDataSource? updaterTrustDatabase = null)
{
    public const string MediaType = "application/vnd.atlas.release";
    private const string ManifestEntryName = "manifest.json";
    private const string SignatureEntryName = "manifest.sig";
    private const string PresentationEntryName = "release-notes.json";
    private const string PresentationSignatureEntryName = "release-notes.sig";
    private const string ActiveMarkerName = "active-release.txt";
    private const string SignatureAlgorithm = "ECDSA_P256_SHA256_P1363";
    private const int MaximumEntries = 16;
    private const int MaximumManifestBytes = 256 * 1024;
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public async Task<AtlasStoredReleaseView> UploadAsync(
        Stream body,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(fileName) != fileName ||
            !fileName.EndsWith(".atlasrelease", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Seleccione un archivo .atlasrelease válido.");
        }

        string storageRoot = GetStorageRoot(create: true);
        string incomingRoot = Path.Combine(storageRoot, ".incoming");
        Directory.CreateDirectory(incomingRoot);
        string uploadPath = Path.Combine(
            incomingRoot,
            $"{Guid.NewGuid():N}.atlasrelease");
        string? releaseDirectory = null;

        await mutationLock.WaitAsync(cancellationToken);
        try
        {
            await CopyRequestBodyAsync(body, uploadPath, cancellationToken);
            ValidatedArchive archive = await ValidateArchiveAsync(
                uploadPath,
                cancellationToken);

            releaseDirectory = Path.Combine(
                storageRoot,
                archive.Manifest.ReleaseId.ToString("D"));
            if (Directory.Exists(releaseDirectory))
            {
                throw new AtlasReleaseConflictException(
                    "Esta versión firmada ya fue cargada.");
            }

            string stagingDirectory =
                releaseDirectory + $".staging-{Guid.NewGuid():N}";
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                await ExtractValidatedArchiveAsync(
                    uploadPath,
                    stagingDirectory,
                    archive,
                    cancellationToken);
                Directory.Move(stagingDirectory, releaseDirectory);
            }
            catch
            {
                DeleteDirectoryQuietly(stagingDirectory);
                throw;
            }

            Guid? activeId = ReadActiveReleaseId(storageRoot);
            return ToView(
                archive.Manifest,
                archive.SignatureVerified,
                activeId == archive.Manifest.ReleaseId,
                archive.Presentation);
        }
        finally
        {
            TryDelete(uploadPath);
            mutationLock.Release();
        }
    }

    public async Task<AtlasReleaseStoreView> GetReleasesAsync(
        CancellationToken cancellationToken)
    {
        string storageRoot = GetStorageRoot(create: true);
        Guid? activeId = ReadActiveReleaseId(storageRoot);
        List<AtlasStoredReleaseView> releases = [];

        foreach (string directory in Directory.EnumerateDirectories(storageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "D", out Guid releaseId))
            {
                continue;
            }

            string manifestPath = Path.Combine(directory, ManifestEntryName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                byte[] manifestBytes =
                    await File.ReadAllBytesAsync(manifestPath, cancellationToken);
                AtlasReleaseManifest manifest = DeserializeManifest(manifestBytes);
                if (manifest.ReleaseId != releaseId)
                {
                    continue;
                }

                releases.Add(ToView(
                    manifest,
                    signatureVerified: true,
                    isActive: activeId == releaseId,
                    presentation: await ReadStoredPresentationAsync(
                        directory,
                        manifest,
                        cancellationToken)));
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or IOException)
            {
                // Una carpeta dañada no debe impedir administrar las demás.
            }
        }

        return new AtlasReleaseStoreView(
            activeId,
            releases
                .OrderByDescending(release => release.CreatedAtUtc)
                .ToArray());
    }

    public async Task<AtlasStoredReleaseView> ActivateAsync(
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        string storageRoot = GetStorageRoot(create: true);
        string releaseDirectory = Path.Combine(
            storageRoot,
            releaseId.ToString("D"));

        await mutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(releaseDirectory))
            {
                throw new DirectoryNotFoundException(
                    "No se encontró la versión solicitada.");
            }

            ValidatedStoredRelease validated =
                await ValidateStoredReleaseAsync(
                    releaseDirectory,
                    cancellationToken);
            if (validated.Manifest.ReleaseId != releaseId)
            {
                throw new InvalidDataException(
                    "El identificador de la versión no coincide.");
            }

            AtlasPackageCatalog catalog = new(
                ApiContract.SchemaVersion,
                timeProvider.GetUtcNow(),
                validated.Manifest.Packages
                    .Where(package =>
                        !string.Equals(
                            package.Component,
                            AtlasReleaseComponents.Setup,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(package => new AtlasPackageEntry(
                        package.Component,
                        package.Version,
                        package.FileName,
                        package.Sha256,
                        package.SizeBytes,
                        package.ContentType))
                    .ToArray());
            string catalogPath = Path.Combine(
                releaseDirectory,
                "atlas-package-catalog.json");
            await WriteJsonAtomicallyAsync(
                catalogPath,
                catalog,
                cancellationToken);

            string markerPath = Path.Combine(storageRoot, ActiveMarkerName);
            string temporaryMarker =
                markerPath + $".next-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(
                temporaryMarker,
                releaseId.ToString("D"),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryMarker, markerPath, overwrite: true);

            return ToView(
                validated.Manifest,
                signatureVerified: true,
                isActive: true,
                presentation: validated.Presentation);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public string? TryGetActiveDistributionPath()
        => TryGetDistributionPath(TryGetActiveReleaseId());

    public Guid? TryGetActiveReleaseId()
    {
        string storageRoot = GetStorageRoot(create: false);
        if (!Directory.Exists(storageRoot))
        {
            return null;
        }

        return ReadActiveReleaseId(storageRoot);
    }

    public string? TryGetDistributionPath(Guid? releaseId)
    {
        if (releaseId is null)
        {
            return null;
        }

        string storageRoot = GetStorageRoot(create: false);
        string path = Path.Combine(storageRoot, releaseId.Value.ToString("D"));
        return Directory.Exists(path) ? path : null;
    }

    public async Task<AtlasSignedReleaseEnvelope?> GetCurrentEnvelopeAsync(
        CancellationToken cancellationToken)
        => await GetEnvelopeAsync(
            TryGetActiveReleaseId(),
            cancellationToken);

    public async Task<string?> GetActiveReleaseVersionAsync(
        CancellationToken cancellationToken)
    {
        string? releasePath = TryGetActiveDistributionPath();
        if (releasePath is null)
        {
            return null;
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(releasePath, ManifestEntryName),
            cancellationToken);
        AtlasReleaseManifest manifest = DeserializeManifest(manifestBytes);
        ValidateManifestCore(manifest);
        return manifest.ReleaseVersion;
    }

    public async Task<AtlasSignedReleaseEnvelope?> GetEnvelopeAsync(
        Guid? releaseId,
        CancellationToken cancellationToken)
    {
        string? releasePath = TryGetDistributionPath(releaseId);
        if (releasePath is null)
        {
            return null;
        }

        byte[] manifest = await File.ReadAllBytesAsync(
            Path.Combine(releasePath, ManifestEntryName),
            cancellationToken);
        byte[] signature = await File.ReadAllBytesAsync(
            Path.Combine(releasePath, SignatureEntryName),
            cancellationToken);
        return new AtlasSignedReleaseEnvelope(
            Convert.ToBase64String(manifest),
            Convert.ToBase64String(signature));
    }

    public async Task<AtlasPackageFile?> GetActivePackageAsync(
        string component,
        CancellationToken cancellationToken)
        => await GetPackageAsync(
            TryGetActiveReleaseId(),
            component,
            cancellationToken);

    public async Task<AtlasPackageFile?> GetPackageAsync(
        Guid? releaseId,
        string component,
        CancellationToken cancellationToken)
    {
        string? releasePath = TryGetDistributionPath(releaseId);
        if (releasePath is null)
        {
            return null;
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(releasePath, ManifestEntryName),
            cancellationToken);
        AtlasReleaseManifest manifest = DeserializeManifest(manifestBytes);
        ValidateManifestCore(manifest);
        AtlasReleasePackage? package =
            manifest.Packages.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.Component,
                    component,
                    StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return null;
        }

        string path = Path.Combine(releasePath, package.FileName);
        FileInfo information = new(path);
        if (!information.Exists || information.Length != package.SizeBytes)
        {
            throw new InvalidDataException(
                $"El paquete activo de {component} no está disponible.");
        }

        return new AtlasPackageFile(
            information.FullName,
            package.FileName,
            package.ContentType);
    }

    public async Task<AtlasStoredReleaseView> VerifyStoredReleaseAsync(
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        string? releaseDirectory = TryGetDistributionPath(releaseId);
        if (releaseDirectory is null)
        {
            throw new DirectoryNotFoundException(
                "No se encontro la version solicitada.");
        }

        ValidatedStoredRelease validated = await ValidateStoredReleaseAsync(
            releaseDirectory,
            cancellationToken);
        if (validated.Manifest.ReleaseId != releaseId)
        {
            throw new InvalidDataException(
                "El identificador de la version no coincide.");
        }

        return ToView(
            validated.Manifest,
            signatureVerified: true,
            isActive: TryGetActiveReleaseId() == releaseId,
            presentation: validated.Presentation);
    }

    private async Task CopyRequestBodyAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        long maximumBytes = GetMaximumUploadBytes();
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    $"El paquete supera el límite de {maximumBytes} bytes.");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        if (total == 0)
        {
            throw new InvalidDataException("El archivo cargado está vacío.");
        }
    }

    private async Task<ValidatedArchive> ValidateArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 3 or > MaximumEntries)
        {
            throw new InvalidDataException(
                "La estructura del paquete de versión no es válida.");
        }

        Dictionary<string, ZipArchiveEntry> entries =
            new(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.StartsWith('/') ||
                normalized.Contains("../", StringComparison.Ordinal) ||
                normalized.EndsWith('/') ||
                !entries.TryAdd(normalized, entry))
            {
                throw new InvalidDataException(
                    "El paquete contiene rutas o entradas no permitidas.");
            }

            totalUncompressed = checked(totalUncompressed + entry.Length);
            if (entry.Length > GetMaximumUploadBytes() ||
                totalUncompressed > GetMaximumUploadBytes())
            {
                throw new InvalidDataException(
                    "El contenido descomprimido supera el límite permitido.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out ZipArchiveEntry? manifestEntry) ||
            !entries.TryGetValue(SignatureEntryName, out ZipArchiveEntry? signatureEntry) ||
            manifestEntry.Length is <= 0 or > MaximumManifestBytes ||
            signatureEntry.Length is <= 0 or > 1024)
        {
            throw new InvalidDataException(
                "Falta el manifiesto o la firma de la versión.");
        }

        byte[] manifestBytes = await ReadEntryAsync(
            manifestEntry,
            MaximumManifestBytes,
            cancellationToken);
        byte[] signatureBytes = await ReadEntryAsync(
            signatureEntry,
            1024,
            cancellationToken);
        AtlasReleaseManifest manifest = DeserializeManifest(manifestBytes);
        ValidateManifest(manifest, entries);
        await VerifySignatureAsync(manifestBytes, signatureBytes, cancellationToken);

        AtlasReleasePresentation? presentation = null;
        byte[]? presentationBytes = null;
        byte[]? presentationSignatureBytes = null;
        bool hasPresentation = entries.TryGetValue(
            PresentationEntryName,
            out ZipArchiveEntry? presentationEntry);
        bool hasPresentationSignature = entries.TryGetValue(
            PresentationSignatureEntryName,
            out ZipArchiveEntry? presentationSignatureEntry);
        if (hasPresentation != hasPresentationSignature)
        {
            throw new InvalidDataException(
                "Las notas del release están incompletas.");
        }
        if (hasPresentation)
        {
            if (presentationEntry!.Length is <= 0 or > MaximumManifestBytes ||
                presentationSignatureEntry!.Length is <= 0 or > 1024)
            {
                throw new InvalidDataException(
                    "Las notas del release no tienen un tamaño válido.");
            }
            presentationBytes = await ReadEntryAsync(
                presentationEntry,
                MaximumManifestBytes,
                cancellationToken);
            presentationSignatureBytes = await ReadEntryAsync(
                presentationSignatureEntry,
                1024,
                cancellationToken);
            presentation = DeserializePresentation(presentationBytes);
            ValidatePresentation(presentation, manifest);
            await VerifySignatureAsync(presentationBytes, presentationSignatureBytes, cancellationToken);
        }

        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            ZipArchiveEntry entry = entries[$"packages/{package.FileName}"];
            if (entry.Length != package.SizeBytes)
            {
                throw new InvalidDataException(
                    $"El tamaño de {package.Component} no coincide.");
            }

            await using Stream content = entry.Open();
            string hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(content, cancellationToken));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash),
                    Encoding.ASCII.GetBytes(package.Sha256.ToLowerInvariant())))
            {
                throw new InvalidDataException(
                    $"La integridad de {package.Component} no es válida.");
            }
        }

        return new ValidatedArchive(
            manifest,
            manifestBytes,
            signatureBytes,
            presentation,
            presentationBytes,
            presentationSignatureBytes,
            SignatureVerified: true);
    }

    private async Task ExtractValidatedArchiveAsync(
        string archivePath,
        string destination,
        ValidatedArchive validated,
        CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(
            Path.Combine(destination, ManifestEntryName),
            validated.ManifestBytes,
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(destination, SignatureEntryName),
            validated.SignatureBytes,
            cancellationToken);
        if (validated.PresentationBytes is not null &&
            validated.PresentationSignatureBytes is not null)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(destination, PresentationEntryName),
                validated.PresentationBytes,
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(destination, PresentationSignatureEntryName),
                validated.PresentationSignatureBytes,
                cancellationToken);
        }

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (AtlasReleasePackage package in validated.Manifest.Packages)
        {
            ZipArchiveEntry entry =
                archive.GetEntry($"packages/{package.FileName}")
                ?? throw new InvalidDataException(
                    $"Falta el archivo de {package.Component}.");
            string targetPath = Path.Combine(destination, package.FileName);
            await using Stream source = entry.Open();
            await using FileStream target = new(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private async Task<ValidatedStoredRelease> ValidateStoredReleaseAsync(
        string releaseDirectory,
        CancellationToken cancellationToken)
    {
        byte[] manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(releaseDirectory, ManifestEntryName),
            cancellationToken);
        byte[] signatureBytes = await File.ReadAllBytesAsync(
            Path.Combine(releaseDirectory, SignatureEntryName),
            cancellationToken);
        AtlasReleaseManifest manifest = DeserializeManifest(manifestBytes);
        await VerifySignatureAsync(manifestBytes, signatureBytes, cancellationToken);

        HashSet<string> fileNames =
            new(manifest.Packages.Select(package => package.FileName),
                StringComparer.OrdinalIgnoreCase);
        ValidateManifestCore(manifest);
        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            string path = Path.Combine(releaseDirectory, package.FileName);
            FileInfo information = new(path);
            if (!information.Exists || information.Length != package.SizeBytes)
            {
                throw new InvalidDataException(
                    $"El archivo activo de {package.Component} no es válido.");
            }

            await using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(
                    hash,
                    package.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"La integridad almacenada de {package.Component} falló.");
            }
        }

        return new ValidatedStoredRelease(
            manifest,
            await ReadStoredPresentationAsync(
                releaseDirectory,
                manifest,
                cancellationToken));
    }

    private static AtlasReleaseManifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            AtlasReleaseManifest? manifest =
                JsonSerializer.Deserialize<AtlasReleaseManifest>(
                    bytes,
                    JsonOptions);
            return manifest ?? throw new InvalidDataException(
                "No fue posible leer el manifiesto de la versión.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "El manifiesto de la versión no contiene JSON válido.",
                exception);
        }
    }

    private static AtlasReleasePresentation DeserializePresentation(byte[] bytes)
    {
        try
        {
            AtlasReleasePresentation? presentation =
                JsonSerializer.Deserialize<AtlasReleasePresentation>(
                    bytes,
                    JsonOptions);
            return presentation ?? throw new InvalidDataException(
                "No fue posible leer las notas del release.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Las notas del release no contienen JSON válido.",
                exception);
        }
    }

    private async Task<AtlasReleasePresentation?> ReadStoredPresentationAsync(
        string releaseDirectory,
        AtlasReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        string presentationPath = Path.Combine(
            releaseDirectory,
            PresentationEntryName);
        string signaturePath = Path.Combine(
            releaseDirectory,
            PresentationSignatureEntryName);
        bool hasPresentation = File.Exists(presentationPath);
        bool hasSignature = File.Exists(signaturePath);
        if (!hasPresentation && !hasSignature)
        {
            return null;
        }
        if (!hasPresentation || !hasSignature)
        {
            throw new InvalidDataException(
                "Las notas almacenadas del release están incompletas.");
        }

        FileInfo presentationFile = new(presentationPath);
        FileInfo signatureFile = new(signaturePath);
        if (presentationFile.Length is <= 0 or > MaximumManifestBytes ||
            signatureFile.Length is <= 0 or > 1024)
        {
            throw new InvalidDataException(
                "Las notas almacenadas del release no tienen un tamaño válido.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(
            presentationPath,
            cancellationToken);
        byte[] signature = await File.ReadAllBytesAsync(
            signaturePath,
            cancellationToken);
        await VerifySignatureAsync(bytes, signature, cancellationToken);
        AtlasReleasePresentation presentation = DeserializePresentation(bytes);
        ValidatePresentation(presentation, manifest);
        return presentation;
    }

    private static void ValidateManifest(
        AtlasReleaseManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        ValidateManifestCore(manifest);
        HashSet<string> expectedEntries = new(StringComparer.OrdinalIgnoreCase)
        {
            ManifestEntryName,
            SignatureEntryName
        };
        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            expectedEntries.Add($"packages/{package.FileName}");
        }
        bool containsPresentation = entries.ContainsKey(PresentationEntryName);
        bool containsPresentationSignature =
            entries.ContainsKey(PresentationSignatureEntryName);
        if (containsPresentation != containsPresentationSignature)
        {
            throw new InvalidDataException(
                "Las notas del release están incompletas.");
        }
        if (containsPresentation)
        {
            expectedEntries.Add(PresentationEntryName);
            expectedEntries.Add(PresentationSignatureEntryName);
        }

        if (!expectedEntries.SetEquals(entries.Keys))
        {
            throw new InvalidDataException(
                "El paquete contiene archivos faltantes o no declarados.");
        }
    }

    private static void ValidateManifestCore(AtlasReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != ApiContract.SchemaVersion ||
            manifest.ReleaseId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.ReleaseVersion) ||
            manifest.ReleaseVersion.Length > 64 ||
            manifest.CreatedAtUtc == default ||
            !string.Equals(
                manifest.SignatureAlgorithm,
                SignatureAlgorithm,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.MinimumUpdaterVersion) ||
            manifest.MinimumUpdaterVersion.Length > 64 ||
            manifest.Packages.Count < AtlasReleaseComponents.Required.Count ||
            manifest.Packages.Count > AtlasReleaseComponents.Allowed.Count)
        {
            throw new InvalidDataException(
                "El manifiesto de la versión no es válido.");
        }

        HashSet<string> components = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> fileNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            if (!AtlasReleaseComponents.Allowed.Contains(package.Component) ||
                !components.Add(package.Component) ||
                string.IsNullOrWhiteSpace(package.Version) ||
                package.Version.Length > 64 ||
                Path.GetFileName(package.FileName) != package.FileName ||
                !fileNames.Add(package.FileName) ||
                package.SizeBytes <= 0 ||
                package.SizeBytes > 500_000_000 ||
                package.Sha256.Length != 64 ||
                package.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                string.IsNullOrWhiteSpace(package.ContentType) ||
                package.ContentType.Length > 128)
            {
                throw new InvalidDataException(
                    "El manifiesto contiene un componente inválido.");
            }
        }

        if (!AtlasReleaseComponents.IsValidReleaseSet(components))
        {
            throw new InvalidDataException(
                "La versión no contiene todos los componentes obligatorios.");
        }
    }

    private static void ValidatePresentation(
        AtlasReleasePresentation presentation,
        AtlasReleaseManifest manifest)
    {
        if (presentation.SchemaVersion != ApiContract.SchemaVersion ||
            presentation.ReleaseId != manifest.ReleaseId ||
            !string.Equals(
                presentation.ReleaseVersion,
                manifest.ReleaseVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(presentation.Summary) ||
            presentation.Summary.Trim().Length is < 10 or > 500 ||
            string.IsNullOrWhiteSpace(presentation.RestoreOutcome) ||
            presentation.RestoreOutcome.Trim().Length is < 10 or > 1_000 ||
            presentation.PreviousReleaseVersion?.Length > 64 ||
            presentation.Features is null ||
            presentation.ChangesFromPrevious is null ||
            presentation.Features.Count is < 1 or > 20 ||
            presentation.ChangesFromPrevious.Count is < 1 or > 20 ||
            presentation.Features.Concat(presentation.ChangesFromPrevious)
                .Any(item => string.IsNullOrWhiteSpace(item) ||
                             item.Length > 300 ||
                             item.Any(char.IsControl)))
        {
            throw new InvalidDataException(
                "Las notas firmadas del release no son válidas.");
        }
    }

    private async Task VerifySignatureAsync(byte[] manifest, byte[] signature, CancellationToken cancellationToken)
    {
        string? publicKeyPath =
            optionsAccessor.Value.ReleasePublicKeyPath?.Trim();
        if (string.IsNullOrWhiteSpace(publicKeyPath))
        {
            throw new InvalidOperationException(
                "Miguel:ReleasePublicKeyPath no está configurado.");
        }

        string pem = File.ReadAllText(Path.GetFullPath(publicKeyPath));
        var keys = new List<string> { pem };
        if (updaterTrustDatabase is not null)
        {
            await using var command = updaterTrustDatabase.CreateCommand("SELECT envelope::text,created_at_utc FROM updater_controls WHERE purpose='TRUST_TRANSITION' ORDER BY generation DESC LIMIT 1");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var envelope = JsonSerializer.Deserialize<SignedUpdaterControl>(reader.GetString(0), JsonOptions)!;
                var transition = UpdaterControlValidator.Verify(envelope, pem, new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero));
                if (transition.Purpose != "TRUST_TRANSITION") throw new InvalidDataException("invalid_saved_trust");
                keys.Add(transition.NextPublicKeyPem!);
            }
        }
        if (!UpdaterTrust.VerifySignature(manifest, signature, keys))
        {
            throw new InvalidDataException(
                "La firma criptográfica de la versión no es válida.");
        }
    }

    private AtlasStoredReleaseView ToView(
        AtlasReleaseManifest manifest,
        bool signatureVerified,
        bool isActive,
        AtlasReleasePresentation? presentation) =>
        new(
            manifest.ReleaseId,
            manifest.ReleaseVersion,
            manifest.CreatedAtUtc,
            isActive,
            signatureVerified,
            manifest.Packages.Select(package => new AtlasReleasePackageView(
                package.Component,
                package.Version,
                string.Empty,
                package.Sha256,
                package.SizeBytes,
                package.ContentType)).ToArray(),
            presentation);

    private string GetStorageRoot(bool create)
    {
        string? configured = optionsAccessor.Value.ReleaseStoragePath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!create)
            {
                return string.Empty;
            }

            throw new InvalidOperationException(
                "Miguel:ReleaseStoragePath no está configurado.");
        }

        string path = Path.GetFullPath(configured);
        if (create)
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    private long GetMaximumUploadBytes()
    {
        long configured = optionsAccessor.Value.MaxReleaseUploadBytes;
        return configured is >= 10_000_000 and <= 1_000_000_000
            ? configured
            : 300_000_000;
    }

    private static Guid? ReadActiveReleaseId(string storageRoot)
    {
        string marker = Path.Combine(storageRoot, ActiveMarkerName);
        if (!File.Exists(marker))
        {
            return null;
        }

        string value = File.ReadAllText(marker).Trim();
        return Guid.TryParseExact(value, "D", out Guid result)
            ? result
            : null;
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = entry.Open();
        using MemoryStream memory = new((int)entry.Length);
        await stream.CopyToAsync(memory, cancellationToken);
        if (memory.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "Una entrada del paquete supera el límite permitido.");
        }
        return memory.ToArray();
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = path + $".next-{Guid.NewGuid():N}";
        await using (FileStream stream = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                JsonOptions,
                cancellationToken);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record ValidatedArchive(
        AtlasReleaseManifest Manifest,
        byte[] ManifestBytes,
        byte[] SignatureBytes,
        AtlasReleasePresentation? Presentation,
        byte[]? PresentationBytes,
        byte[]? PresentationSignatureBytes,
        bool SignatureVerified);

    private sealed record ValidatedStoredRelease(
        AtlasReleaseManifest Manifest,
        AtlasReleasePresentation? Presentation);
}

public sealed class AtlasReleaseConflictException(string message)
    : Exception(message);
