using System.Security.Cryptography;
using System.Text.Json;

namespace Miguel.Contracts;

public sealed record AcceptedUpdaterTrust(SignedUpdaterControl Envelope, long Generation, DateTimeOffset AcceptedAtUtc);

public static class UpdaterTrust
{
    public static IReadOnlyList<string> Keys(string rootPem, string statePath)
    {
        if (!File.Exists(statePath)) return [rootPem];
        var state = JsonSerializer.Deserialize<AcceptedUpdaterTrust>(File.ReadAllText(statePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("invalid_trust_state");
        var transition = UpdaterControlValidator.Verify(state.Envelope, rootPem, state.AcceptedAtUtc);
        if (transition.Purpose != "TRUST_TRANSITION" || transition.Generation != state.Generation) throw new InvalidDataException("invalid_trust_state");
        // The offline root remains the recovery authority. Only the latest operational key is admitted.
        return [transition.NextPublicKeyPem!, rootPem];
    }

    public static bool VerifySignature(byte[] bytes, byte[] signature, IEnumerable<string> keys)
    {
        foreach (string pem in keys)
        {
            using ECDsa key = ECDsa.Create(); key.ImportFromPem(pem);
            if (key.KeySize == 256 && key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
        }
        return false;
    }
}
