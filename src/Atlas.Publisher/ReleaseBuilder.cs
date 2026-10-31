using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Contracts;

namespace Atlas.Publisher;

internal enum ReleaseBuildProfile
{
    Standard,
    UpdaterBridge
}

internal sealed record DistributionInspection(
    string Directory,
    IReadOnlyList<AtlasReleasePackage> Packages);

internal sealed record ReleaseCompatibility(
    string MinimumUpdaterVersion,
    string TargetUpdaterVersion,
    bool IncludesCoordinatedUpdater);

internal sealed record ReleaseNarrative(
    string? PreviousReleaseVersion,
    string Summary,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> ChangesFromPrevious,
    string RestoreOutcome);

internal static class ReleaseBuilder
{
    public const string SignatureAlgorithm =
        "ECDSA_P256_SHA256_P1363";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static ReleaseCompatibility GetCompatibility(
        IReadOnlyList<AtlasReleasePackage> packages,
        string? requestedMinimum = null)
    {
        AtlasReleasePackage updater = packages.Single(package => string.Equals(
            package.Component, AtlasReleaseComponents.Updater,
            StringComparison.OrdinalIgnoreCase));
        // Include prereleases of 0.4.0, not just its final stable version.
        bool coordinated = Atlas.Updater.VersionTagComparer.IsAtLeast(
            updater.Version, "0.4.0-0");
        bool backupService = packages.Any(package => string.Equals(
            package.Component, AtlasReleaseComponents.BackupService,
            StringComparison.OrdinalIgnoreCase));
        string required = coordinated ? "0.3.2-rc.1"
            : backupService ? "0.3.0-bridge.1" : "0.2.0";
        string minimum = requestedMinimum is null
            ? required : ValidateVersion(requestedMinimum, "actualizador mínimo");
        if (!Atlas.Updater.VersionTagComparer.IsAtLeast(minimum, required))
        {
            throw new InvalidOperationException(
                $"Esta distribución requiere Updater {required} o posterior. " +
                "No se puede firmar con una compatibilidad inferior.");
        }
        return new ReleaseCompatibility(minimum, updater.Version, coordinated);
    }

    public static (string PrivateKeyPath, string PublicKeyPath) CreateKeyPair(
        string privateKeyPath,
        ReadOnlySpan<char> password)
    {
        if (password.Length < 14)
        {
            throw new ArgumentException(
                "La contraseña de la clave debe tener al menos 14 caracteres.",
                nameof(password));
        }

        string resolvedPrivate = Path.GetFullPath(privateKeyPath);
        string? directory = Path.GetDirectoryName(resolvedPrivate);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "Seleccione una carpeta válida para la clave.",
                nameof(privateKeyPath));
        }
        Directory.CreateDirectory(directory);

        string publicKeyPath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(resolvedPrivate) + ".public.pem");
        if (File.Exists(resolvedPrivate) || File.Exists(publicKeyPath))
        {
            throw new IOException(
                "La clave privada o su clave pública ya existen. " +
                "No se sobrescribieron.");
        }

        using ECDsa signingKey = ECDsa.Create(
            ECCurve.NamedCurves.nistP256);
        PbeParameters encryption = new(
            PbeEncryptionAlgorithm.Aes256Cbc,
            HashAlgorithmName.SHA256,
            iterationCount: 250_000);
        string privatePem =
            signingKey.ExportEncryptedPkcs8PrivateKeyPem(password, encryption);
        string publicPem = signingKey.ExportSubjectPublicKeyInfoPem();

        File.WriteAllText(
            resolvedPrivate,
            privatePem,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            publicKeyPath,
            publicPem,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return (resolvedPrivate, publicKeyPath);
    }

    public static async Task<DistributionInspection> InspectDistributionAsync(
        string distributionDirectory,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        string distribution = Path.GetFullPath(distributionDirectory);
        if (!Directory.Exists(distribution))
        {
            throw new DirectoryNotFoundException(
                "No existe la carpeta de distribución seleccionada.");
        }

        string catalogPath = Path.Combine(
            distribution,
            "atlas-package-catalog.json");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                "La distribución no contiene atlas-package-catalog.json.",
                catalogPath);
        }

        report?.Invoke("Leyendo el catálogo de componentes…");
        await using FileStream catalogStream = File.OpenRead(catalogPath);
        PublishedCatalog? catalog =
            await JsonSerializer.DeserializeAsync<PublishedCatalog>(
                catalogStream,
                JsonOptions,
                cancellationToken);
        if (catalog is null ||
            catalog.SchemaVersion != 1 ||
            catalog.Packages.Count is < 3 or > 5)
        {
            throw new InvalidDataException(
                "El catálogo debe contener agente, BackupNAS y actualizador.");
        }

        List<AtlasReleasePackage> packages = [];
        HashSet<string> components = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (AtlasReleasePackage package in catalog.Packages)
        {
            report?.Invoke(
                $"Verificando {package.Component} {package.Version}…");
            await ValidatePackageAsync(
                distribution,
                package,
                components,
                cancellationToken);
            packages.Add(package with
            {
                Sha256 = package.Sha256.ToLowerInvariant()
            });
        }

        string setupPath = Path.Combine(distribution, "ATLAS.Setup.exe");
        FileInfo setupFile = new(setupPath);
        if (!setupFile.Exists)
        {
            throw new FileNotFoundException(
                "La distribución no contiene ATLAS.Setup.exe.",
                setupPath);
        }
        string setupVersion =
            FileVersionInfo.GetVersionInfo(setupPath).ProductVersion ??
            throw new InvalidDataException(
                "ATLAS.Setup.exe no reporta una versión.");
        string validatedSetupVersion = ValidateVersion(setupVersion, "setup");
        AtlasReleasePackage? publishedSetup = packages.FirstOrDefault(
            package => string.Equals(
                package.Component,
                AtlasReleaseComponents.Setup,
                StringComparison.OrdinalIgnoreCase));
        if (publishedSetup is null)
        {
            // Compatibilidad con distribuciones anteriores: el catálogo no
            // declaraba Setup y Publisher lo incorporaba al firmar.
            AtlasReleasePackage setup = new(
                AtlasReleaseComponents.Setup,
                validatedSetupVersion,
                setupFile.Name,
                await ComputeFileSha256Async(setupPath, cancellationToken),
                setupFile.Length,
                "application/vnd.microsoft.portable-executable");
            await ValidatePackageAsync(
                distribution,
                setup,
                components,
                cancellationToken);
            packages.Add(setup);
        }
        else if (!string.Equals(
                     publishedSetup.FileName,
                     setupFile.Name,
                     StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(
                     publishedSetup.Version,
                     validatedSetupVersion,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "El instalador publicado no coincide con ATLAS.Setup.exe.");
        }

        if (!AtlasReleaseComponents.IsValidReleaseSet(components))
        {
            throw new InvalidDataException(
                "La distribución no contiene exactamente los componentes " +
                "permitidos por ATLAS.");
        }

        _ = GetCompatibility(packages);

        return new DistributionInspection(
            distribution,
            packages
                .OrderBy(package => package.Component, StringComparer.Ordinal)
                .ToArray());
    }

    public static async Task<AtlasReleaseManifest> BuildAsync(
        string distributionDirectory,
        string privateKeyPath,
        ReadOnlyMemory<char> password,
        string releaseVersion,
        ReleaseNarrative narrative,
        string outputPath,
        ReleaseBuildProfile profile,
        Action<string>? report,
        CancellationToken cancellationToken,
        string? minimumUpdaterVersion = null)
    {
        string version = ValidateVersion(releaseVersion, "release");
        ReleaseNarrative validatedNarrative = ValidateNarrative(narrative);
        if (string.Equals(
                version,
                validatedNarrative.PreviousReleaseVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "La versión nueva no puede ser igual a la versión anterior.",
                nameof(releaseVersion));
        }
        DistributionInspection inspection =
            await InspectDistributionAsync(
                distributionDirectory,
                report,
                cancellationToken);
        string distribution = inspection.Directory;
        List<AtlasReleasePackage> packages = [.. inspection.Packages];

        if (profile == ReleaseBuildProfile.UpdaterBridge)
        {
            if (packages.Any(package => string.Equals(
                    package.Component,
                    AtlasReleaseComponents.BackupService,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "El release puente no puede incluir backup-service. " +
                    "Primero debe actualizarse y validarse el updater puente.");
            }
            report?.Invoke(
                "Perfil puente activo: se publican únicamente los tipos de " +
                "componente que entiende el updater actual.");
        }

        ReleaseCompatibility compatibility = GetCompatibility(
            packages, minimumUpdaterVersion);
        report?.Invoke(
            $"Compatibilidad: Updater {compatibility.MinimumUpdaterVersion} " +
            $"o posterior; destino {compatibility.TargetUpdaterVersion}.");

        Guid releaseId = Guid.NewGuid();
        AtlasReleaseManifest manifest = new(
            SchemaVersion: 1,
            ReleaseId: releaseId,
            ReleaseVersion: version,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SignatureAlgorithm,
            MinimumUpdaterVersion: compatibility.MinimumUpdaterVersion,
            Packages: packages
                .OrderBy(package => package.Component, StringComparer.Ordinal)
                .ToArray());
        AtlasReleasePresentation presentation = new(
            SchemaVersion: 1,
            ReleaseId: releaseId,
            ReleaseVersion: version,
            PreviousReleaseVersion: validatedNarrative.PreviousReleaseVersion,
            Summary: validatedNarrative.Summary,
            Features: validatedNarrative.Features,
            ChangesFromPrevious: validatedNarrative.ChangesFromPrevious,
            RestoreOutcome: validatedNarrative.RestoreOutcome);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            JsonOptions);
        byte[] presentationBytes = JsonSerializer.SerializeToUtf8Bytes(
            presentation,
            JsonOptions);

        report?.Invoke("Abriendo la clave privada cifrada…");
        string privatePem = await File.ReadAllTextAsync(
            Path.GetFullPath(privateKeyPath),
            cancellationToken);
        using ECDsa signingKey = ECDsa.Create();
        signingKey.ImportFromEncryptedPem(
            privatePem,
            password.Span);
        byte[] signature = signingKey.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!signingKey.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                "La firma recién creada no pudo verificarse.");
        }
        byte[] presentationSignature = signingKey.SignData(
            presentationBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!signingKey.VerifyData(
                presentationBytes,
                presentationSignature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                "La firma de las notas del release no pudo verificarse.");
        }

        string resolvedOutput = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(resolvedOutput);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException(
                "La ruta del release no es válida.",
                nameof(outputPath));
        }
        Directory.CreateDirectory(outputDirectory);
        string temporary = resolvedOutput + "." + Guid.NewGuid().ToString("N") +
                           ".tmp";

        try
        {
            report?.Invoke("Creando el paquete .atlasrelease…");
            await using (FileStream output = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             useAsync: true))
            {
                using (ZipArchive archive = new(
                           output,
                           ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    await AddBytesAsync(
                        archive,
                        "manifest.json",
                        manifestBytes,
                        cancellationToken);
                    await AddBytesAsync(
                        archive,
                        "manifest.sig",
                        signature,
                        cancellationToken);
                    await AddBytesAsync(
                        archive,
                        "release-notes.json",
                        presentationBytes,
                        cancellationToken);
                    await AddBytesAsync(
                        archive,
                        "release-notes.sig",
                        presentationSignature,
                        cancellationToken);

                    foreach (AtlasReleasePackage package in manifest.Packages)
                    {
                        report?.Invoke(
                            $"Incluyendo {package.Component} {package.Version}…");
                        string sourcePath = Path.Combine(
                            distribution,
                            package.FileName);
                        await AddFileAsync(
                            archive,
                            sourcePath,
                            $"packages/{package.FileName}",
                            cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            report?.Invoke(
                "Reabriendo el archivo para comprobar firmas y componentes…");
            await VerifyReleaseArchiveAsync(
                temporary,
                signingKey,
                manifest,
                manifestBytes,
                presentationBytes,
                cancellationToken);

            try
            {
                File.Move(temporary, resolvedOutput, overwrite: true);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "No se pudo reemplazar el archivo de salida. Ciérrelo " +
                    "si está abierto en otra aplicación e inténtelo de nuevo.",
                    exception);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        report?.Invoke(
            "Release firmado y verificado desde el archivo final.");
        return manifest;
    }

    public static bool VerifyManifest(
        ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> signature,
        string publicKeyPath)
    {
        string publicPem = File.ReadAllText(
            Path.GetFullPath(publicKeyPath));
        using ECDsa publicKey = ECDsa.Create();
        publicKey.ImportFromPem(publicPem);
        return publicKey.VerifyData(
            manifest,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static async Task ValidatePackageAsync(
        string distribution,
        AtlasReleasePackage package,
        ISet<string> components,
        CancellationToken cancellationToken)
    {
        if (!AtlasReleaseComponents.Allowed.Contains(package.Component) ||
            !components.Add(package.Component) ||
            package.Component.Length > 32 ||
            string.IsNullOrWhiteSpace(package.Version) ||
            package.Version.Length > 64 ||
            package.SizeBytes is <= 0 or > 500_000_000 ||
            package.Sha256.Length != 64 ||
            package.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            Path.GetFileName(package.FileName) != package.FileName ||
            string.IsNullOrWhiteSpace(package.ContentType) ||
            package.ContentType.Length > 128)
        {
            throw new InvalidDataException(
                $"El componente {package.Component} no es válido.");
        }

        string path = Path.Combine(distribution, package.FileName);
        FileInfo information = new(path);
        if (!information.Exists ||
            information.Length != package.SizeBytes)
        {
            throw new InvalidDataException(
                $"El tamaño publicado de {package.Component} no coincide.");
        }

        string actualHash = await ComputeFileSha256Async(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(package.Sha256.ToLowerInvariant())))
        {
            throw new InvalidDataException(
                $"El hash publicado de {package.Component} no coincide.");
        }
        _ = ValidateVersion(package.Version, package.Component);
        string actualVersion = package.Component == AtlasReleaseComponents.Agent
            ? await ReadAgentVersionAsync(path, cancellationToken)
            : ReadExecutableVersion(path);
        if (!string.Equals(package.Version, actualVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"La versión declarada de {package.Component} ({package.Version}) " +
                $"no coincide con el ejecutable ({actualVersion}). No se firmó la distribución.");
    }

    private static string ReadExecutableVersion(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                throw new InvalidDataException("El componente no es un ejecutable Windows PE válido.");
        }
        string? version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("El ejecutable no declara ProductVersion.");
        return ValidateVersion(version, Path.GetFileName(path));
    }

    private static async Task<string> ReadAgentVersionAsync(string path, CancellationToken token)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry[] candidates = archive.Entries.Where(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), "agent/Miguel.Agent.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1 || candidates[0].Length is <= 0 or > 500_000_000)
            throw new InvalidDataException("El ZIP del agente debe contener un único agent/Miguel.Agent.exe con versión verificable.");
        // Inspect only the PE resource; never load or execute code from the archive.
        string temporary = Path.Combine(Path.GetTempPath(), "atlas-publisher-pe-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await using (Stream input = candidates[0].Open())
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, token);
            if (new FileInfo(temporary).Length != candidates[0].Length)
                throw new InvalidDataException("El ejecutable del ZIP del agente está truncado.");
            return ReadExecutableVersion(temporary);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ValidateVersion(string value, string name)
    {
        string version = value.Trim();
        if (version.Length is < 1 or > 64 ||
            !char.IsAsciiLetterOrDigit(version[0]) ||
            version.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                $"La versión de {name} no es válida.",
                nameof(value));
        }
        return version;
    }

    private static ReleaseNarrative ValidateNarrative(ReleaseNarrative narrative)
    {
        string? previous = string.IsNullOrWhiteSpace(narrative.PreviousReleaseVersion)
            ? null
            : ValidateVersion(narrative.PreviousReleaseVersion, "release anterior");
        string summary = ValidateNarrativeText(
            narrative.Summary,
            "resumen",
            500);
        string restoreOutcome = ValidateNarrativeText(
            narrative.RestoreOutcome,
            "resultado de restauración",
            1_000);
        string[] features = ValidateNarrativeItems(
            narrative.Features,
            "funcionalidades");
        string[] changes = ValidateNarrativeItems(
            narrative.ChangesFromPrevious,
            "mejoras frente a la versión anterior");
        return new ReleaseNarrative(
            previous,
            summary,
            features,
            changes,
            restoreOutcome);
    }

    private static string ValidateNarrativeText(
        string value,
        string name,
        int maximumLength)
    {
        string text = value.Trim();
        if (text.Length is < 10 || text.Length > maximumLength ||
            text.Any(character => char.IsControl(character) &&
                                  character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException(
                $"El {name} debe contener entre 10 y {maximumLength} caracteres.",
                nameof(value));
        }
        return text;
    }

    private static string[] ValidateNarrativeItems(
        IReadOnlyList<string> values,
        string name)
    {
        string[] items = values
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length is < 1 or > 20 ||
            items.Any(item => item.Length > 300 ||
                              item.Any(character => char.IsControl(character))))
        {
            throw new ArgumentException(
                $"Declare entre 1 y 20 {name}, una por línea y con un máximo de 300 caracteres.",
                nameof(values));
        }
        return items;
    }

    internal static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task VerifyReleaseArchiveAsync(
        string archivePath,
        ECDsa signingKey,
        AtlasReleaseManifest manifest,
        ReadOnlyMemory<byte> expectedManifest,
        ReadOnlyMemory<byte> expectedPresentation,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using ZipArchive archive = new(source, ZipArchiveMode.Read);

        int expectedEntryCount = manifest.Packages.Count + 4;
        if (archive.Entries.Count != expectedEntryCount ||
            archive.Entries.Select(entry => entry.FullName)
                .Distinct(StringComparer.Ordinal).Count() != expectedEntryCount)
        {
            throw new InvalidDataException(
                "El archivo final contiene entradas faltantes o inesperadas.");
        }

        byte[] manifestBytes = await ReadRequiredEntryAsync(
            archive,
            "manifest.json",
            cancellationToken);
        byte[] manifestSignature = await ReadRequiredEntryAsync(
            archive,
            "manifest.sig",
            cancellationToken);
        byte[] presentationBytes = await ReadRequiredEntryAsync(
            archive,
            "release-notes.json",
            cancellationToken);
        byte[] presentationSignature = await ReadRequiredEntryAsync(
            archive,
            "release-notes.sig",
            cancellationToken);

        if (!manifestBytes.AsSpan().SequenceEqual(expectedManifest.Span) ||
            !presentationBytes.AsSpan().SequenceEqual(expectedPresentation.Span) ||
            !signingKey.VerifyData(
                manifestBytes,
                manifestSignature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation) ||
            !signingKey.VerifyData(
                presentationBytes,
                presentationSignature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                "El archivo final no conservó correctamente sus firmas.");
        }

        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            ZipArchiveEntry entry = archive.GetEntry(
                $"packages/{package.FileName}") ??
                throw new InvalidDataException(
                    $"El archivo final no contiene {package.FileName}.");
            if (entry.Length != package.SizeBytes)
            {
                throw new InvalidDataException(
                    $"El tamaño final de {package.Component} no coincide.");
            }

            await using Stream content = entry.Open();
            byte[] hash = await SHA256.HashDataAsync(content, cancellationToken);
            string actualHash = Convert.ToHexStringLower(hash);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(package.Sha256.ToLowerInvariant())))
            {
                throw new InvalidDataException(
                    $"El contenido final de {package.Component} no coincide.");
            }
        }
    }

    private static async Task<byte[]> ReadRequiredEntryAsync(
        ZipArchive archive,
        string entryName,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName) ??
            throw new InvalidDataException(
                $"El archivo final no contiene {entryName}.");
        await using Stream content = entry.Open();
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task AddBytesAsync(
        ZipArchive archive,
        string entryName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            entryName,
            CompressionLevel.NoCompression);
        await using Stream destination = entry.Open();
        await destination.WriteAsync(content, cancellationToken);
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            entryName,
            CompressionLevel.NoCompression);
        await using Stream destination = entry.Open();
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        await source.CopyToAsync(
            destination,
            bufferSize: 1024 * 1024,
            cancellationToken);
    }

    private sealed record PublishedCatalog(
        int SchemaVersion,
        IReadOnlyList<AtlasReleasePackage> Packages);
}
