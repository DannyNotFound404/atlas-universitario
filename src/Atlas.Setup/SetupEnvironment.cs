using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atlas.Setup;

internal sealed record SetupEnvironment(
    string Name,
    Uri ServerUrl,
    string RootCertificateSha256,
    string ReleasePublicKeySha256)
{
    private const string EnvironmentResource =
        "Atlas.Setup.atlas-environment.json";
    private const string RootCertificateResource =
        "Atlas.Setup.atlas-root-ca.crt";
    private const string ReleasePublicKeyResource =
        "Atlas.Setup.atlas-release-signing.public.pem";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static SetupEnvironment Load()
    {
        Assembly assembly = typeof(SetupEnvironment).Assembly;
        EnvironmentDocument document = ReadDocument(assembly);

        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "La configuración de ambiente de ATLAS no es compatible.");
        }

        string name = document.EnvironmentName?.Trim().ToUpperInvariant() ?? "";
        if (name.Length is < 2 or > 32 ||
            name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new InvalidDataException(
                "El nombre del ambiente ATLAS no es válido.");
        }

        if (!Uri.TryCreate(
                document.ServerUrl?.Trim(),
                UriKind.Absolute,
                out Uri? serverUrl) ||
            serverUrl.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(serverUrl.UserInfo) ||
            !string.IsNullOrEmpty(serverUrl.Query) ||
            !string.IsNullOrEmpty(serverUrl.Fragment))
        {
            throw new InvalidDataException(
                "La URL del servidor ATLAS debe ser HTTPS y no contener " +
                "credenciales, consulta ni fragmento.");
        }

        Uri normalizedServerUrl = new(
            serverUrl.AbsoluteUri.TrimEnd('/') + "/",
            UriKind.Absolute);
        string rootCertificateHash = NormalizeHash(
            document.RootCertificateSha256,
            "certificado raíz");
        string releasePublicKeyHash = NormalizeHash(
            document.ReleasePublicKeySha256,
            "clave pública de releases");

        VerifyResourceHash(
            assembly,
            RootCertificateResource,
            rootCertificateHash,
            "certificado raíz");
        VerifyResourceHash(
            assembly,
            ReleasePublicKeyResource,
            releasePublicKeyHash,
            "clave pública de releases");

        return new SetupEnvironment(
            name,
            normalizedServerUrl,
            rootCertificateHash,
            releasePublicKeyHash);
    }

    private static EnvironmentDocument ReadDocument(Assembly assembly)
    {
        using Stream stream = OpenRequiredResource(
            assembly,
            EnvironmentResource);
        return JsonSerializer.Deserialize<EnvironmentDocument>(
                   stream,
                   JsonOptions) ??
               throw new InvalidDataException(
                   "La configuración de ambiente ATLAS está vacía.");
    }

    private static void VerifyResourceHash(
        Assembly assembly,
        string resourceName,
        string expectedHash,
        string description)
    {
        using Stream stream = OpenRequiredResource(assembly, resourceName);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualHash.Equals(
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                $"El {description} incrustado no corresponde al ambiente " +
                "declarado. La instalación fue bloqueada.");
        }
    }

    private static Stream OpenRequiredResource(
        Assembly assembly,
        string resourceName) =>
        assembly.GetManifestResourceStream(resourceName) ??
        throw new InvalidDataException(
            $"El instalador no contiene el recurso obligatorio {resourceName}.");

    private static string NormalizeHash(
        string? value,
        string description)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? "";
        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"La huella SHA-256 del {description} no es válida.");
        }

        return normalized;
    }

    private sealed record EnvironmentDocument(
        int SchemaVersion,
        string? EnvironmentName,
        string? ServerUrl,
        string? RootCertificateSha256,
        string? ReleasePublicKeySha256);
}
