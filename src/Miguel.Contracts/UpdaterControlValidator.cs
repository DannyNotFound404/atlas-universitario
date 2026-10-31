using System.Security.Cryptography;
using System.Text.Json;

namespace Miguel.Contracts;

public static class UpdaterControlValidator
{
    public static UpdaterControlDocument Verify(SignedUpdaterControl envelope, string publicKeyPem, DateTimeOffset now)
    {
        if (envelope.PayloadBase64?.Length is not (> 0 and <= 32768) || envelope.SignatureBase64?.Length is not (> 0 and <= 256))
            throw new InvalidDataException("invalid_control_size");
        byte[] payload = Convert.FromBase64String(envelope.PayloadBase64);
        using ECDsa key = ECDsa.Create(); key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256 || !key.VerifyData(payload, Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("invalid_control_signature");
        var document = JsonSerializer.Deserialize<UpdaterControlDocument>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("empty_control");
        if (document.SchemaVersion != 1 || document.AuthorizationId == Guid.Empty || document.Generation < 1 ||
            document.NotBeforeUtc > now || document.ExpiresAtUtc <= now || document.ExpiresAtUtc <= document.NotBeforeUtc ||
            document.ExpiresAtUtc - document.NotBeforeUtc > TimeSpan.FromDays(366)) throw new InvalidDataException("control_not_effective");
        if (document.Purpose == "RECOVERY")
        {
            if (document.ReleaseId is null || document.ReleaseId == Guid.Empty || document.PackageHashes is null ||
                document.PackageHashes.Count is < 1 or > 5 || document.PackageHashes.Any(p => !AtlasReleaseComponents.Allowed.Contains(p.Key) ||
                    p.Value is null || p.Value.Length != 64 || p.Value.Any(c => !Uri.IsHexDigit(c))) || document.NextPublicKeyPem is not null || document.PreviousKeySha256 is not null)
                throw new InvalidDataException("invalid_recovery_control");
        }
        else if (document.Purpose == "TRUST_TRANSITION")
        {
            if (document.NextPublicKeyPem is null || document.NextPublicKeyPem.Length > 1024 ||
                !document.NextPublicKeyPem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
                document.NextPublicKeyPem.Contains("PRIVATE", StringComparison.Ordinal) || document.PackageHashes is not null)
                throw new InvalidDataException("public_key_required");
            using ECDsa next = ECDsa.Create(); next.ImportFromPem(document.NextPublicKeyPem ?? "");
            if (next.KeySize != 256 || document.PreviousKeySha256 != Fingerprint(publicKeyPem) || document.ReleaseId is not null)
                throw new InvalidDataException("invalid_trust_transition");
        }
        else throw new InvalidDataException("unsupported_control");
        return document;
    }

    public static string Fingerprint(string pem)
    {
        using ECDsa key = ECDsa.Create(); key.ImportFromPem(pem);
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
}
