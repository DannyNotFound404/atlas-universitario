using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed class SignedReleaseClient(
    AgentIdentity identity,
    string publicKeyPath,
    UpdateLog log,
    HttpMessageHandler? handler = null) : IDisposable
{
    private const string SignatureAlgorithm =
        "ECDSA_P256_SHA256_P1363";
    private readonly HttpClient client = CreateClient(identity, handler);
    internal Guid? AttemptId { get; set; }
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public async Task<VerifiedRelease?> GetReleaseAsync(
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            identity,
            HttpMethod.Get,
            "/api/v1/agent/releases/current-signed");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            log.Write("No hay una versión firmada activa.");
            return null;
        }
        response.EnsureSuccessStatusCode();
        AtlasSignedReleaseEnvelope? envelope =
            await response.Content.ReadFromJsonAsync<AtlasSignedReleaseEnvelope>(
                JsonOptions,
                cancellationToken);
        if (envelope is null)
        {
            throw new InvalidDataException(
                "El servidor no devolvió el manifiesto firmado.");
        }

        byte[] manifestBytes = Convert.FromBase64String(
            envelope.ManifestBase64);
        byte[] signatureBytes = Convert.FromBase64String(
            envelope.SignatureBase64);
        if (manifestBytes.Length is <= 0 or > 256 * 1024 ||
            signatureBytes.Length is <= 0 or > 1024)
        {
            throw new InvalidDataException(
                "El manifiesto firmado excede los límites permitidos.");
        }

        VerifySignature(manifestBytes, signatureBytes);
        AtlasReleaseManifest? manifest =
            JsonSerializer.Deserialize<AtlasReleaseManifest>(
                manifestBytes,
                JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException(
                "El manifiesto firmado está vacío.");
        }
        ValidateManifest(manifest);
        return new VerifiedRelease(
            manifest,
            manifestBytes,
            signatureBytes);
    }

    public async Task<DownloadedPackage> DownloadAsync(
        Guid releaseId,
        AtlasReleasePackage package,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        string destination = Path.Combine(
            destinationDirectory,
            package.FileName);
        string temporary = destination + ".download";
        using HttpRequestMessage request = CreateRequest(
            identity,
            HttpMethod.Get,
            $"/api/v1/agent/releases/{releaseId:D}/packages/" +
            Uri.EscapeDataString(package.Component));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != package.SizeBytes)
        {
            throw new InvalidDataException(
                $"El tamaño HTTP de {package.Component} no coincide.");
        }

        await using Stream source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using IncrementalHash hasher =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        await using (FileStream target = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > package.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"La descarga de {package.Component} excedió su tamaño firmado.");
                }
                hasher.AppendData(buffer, 0, read);
                await target.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
            await target.FlushAsync(cancellationToken);
        }
        if (total != package.SizeBytes)
        {
            throw new InvalidDataException(
                $"La descarga de {package.Component} quedó incompleta.");
        }

        string actualHash = Convert.ToHexStringLower(
            hasher.GetHashAndReset());
        if (!string.Equals(
                actualHash,
                package.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Falló la integridad firmada de {package.Component}.");
        }

        File.Move(temporary, destination, overwrite: true);
        return new DownloadedPackage(package, destination);
    }

    internal VerifiedRelease VerifyEnvelope(AtlasSignedReleaseEnvelope envelope, string publicKey)
    {
        if (envelope.ManifestBase64?.Length is not (> 0 and <= 350000) || envelope.SignatureBase64?.Length is not (> 0 and <= 2048))
            throw new InvalidDataException("manifest_size_invalid");
        byte[] bytes = Convert.FromBase64String(envelope.ManifestBase64);
        byte[] signature = Convert.FromBase64String(envelope.SignatureBase64);
        using ECDsa verifier = ECDsa.Create(); verifier.ImportFromPem(publicKey);
        if (verifier.KeySize != 256 || !verifier.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("release_signature_rejected");
        var manifest = JsonSerializer.Deserialize<AtlasReleaseManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("empty_manifest");
        ValidateManifest(manifest);
        return new(manifest, bytes, signature);
    }

    internal Task<string> DownloadResumableAsync(Guid release, AtlasReleasePackage package, string cache,
        int kibPerSecond, Func<long, long, Task> progress, CancellationToken ct) =>
        ResumableDownload.FetchAsync(client, () =>
        {
            var request = CreateRequest(identity, HttpMethod.Get, $"/api/v1/agent/releases/{release:D}/packages/{Uri.EscapeDataString(package.Component)}");
            request.Headers.Add("X-Atlas-Updater-Protocol", "2");
            if (AttemptId is not null) request.Headers.Add("X-Atlas-Updater-Attempt", AttemptId.Value.ToString("D"));
            return request;
        }, package, cache, kibPerSecond, progress, ct);

    private void VerifySignature(byte[] manifest, byte[] signature)
    {
        if (!File.Exists(publicKeyPath))
        {
            throw new FileNotFoundException(
                "No está instalada la clave pública de releases ATLAS.",
                publicKeyPath);
        }
        string pem = File.ReadAllText(publicKeyPath);
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportFromPem(pem);
        if (verifier.KeySize != 256 ||
            !verifier.VerifyData(
                manifest,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                "La versión no fue firmada por la autoridad de releases ATLAS.");
        }
    }

    private static void ValidateManifest(AtlasReleaseManifest manifest)
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
                "El manifiesto firmado no cumple el contrato ATLAS.");
        }

        string currentUpdaterVersion = typeof(SignedReleaseClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        if (VersionTagComparer.IsTargetNewer(
                manifest.MinimumUpdaterVersion,
                currentUpdaterVersion))
        {
            throw new InvalidDataException(
                $"El release requiere ATLAS Updater " +
                $"{manifest.MinimumUpdaterVersion} o posterior; este equipo " +
                $"tiene {currentUpdaterVersion}.");
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
                package.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException(
                    "El manifiesto contiene un paquete no permitido.");
            }
        }
        if (!AtlasReleaseComponents.IsValidReleaseSet(components))
        {
            throw new InvalidDataException(
                "Faltan componentes obligatorios en la versión.");
        }
    }

    private static HttpClient CreateClient(AgentIdentity identity, HttpMessageHandler? handler)
    {
        if (identity.ServerUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "ATLAS Updater solo se comunica mediante HTTPS.");
        }
        var http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = identity.ServerUrl; http.Timeout = TimeSpan.FromMinutes(10); return http;
    }

    private static HttpRequestMessage CreateRequest(
        AgentIdentity identity,
        HttpMethod method,
        string relativePath)
    {
        HttpRequestMessage request = new(
            method,
            new Uri(identity.ServerUrl, relativePath.TrimStart('/')));
        request.Headers.Add(
            ApiContract.DeviceIdHeader,
            identity.DeviceId.ToString("D"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            ApiContract.AuthorizationScheme,
            identity.DeviceKey);
        request.Headers.UserAgent.ParseAdd("ATLAS.Updater/0.3.0");
        return request;
    }

    public void Dispose() => client.Dispose();
}
