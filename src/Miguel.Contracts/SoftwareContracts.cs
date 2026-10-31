using System.Security.Cryptography;
using System.Text;

namespace Miguel.Contracts;

// Optional inventory extension: older agents omit it and older servers ignore it.
public sealed record SoftwareInventory(int SchemaVersion, IReadOnlyList<SoftwareSource> Sources,
    IReadOnlyList<InstalledSoftware> Applications);
public sealed record SoftwareSource(string Id, string Kind, string Status, string? Reason = null);
public sealed record InstalledSoftware(string InstallationKey, string SourceId, string Name,
    string Publisher, string Version, string Architecture, string Scope, string? InstalledOn = null);

public static class SoftwareIdentity
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    // Versions, editions and architecture are never guessed or stripped from the product name.
    public static string Product(string name, string publisher) => Hash(Normalize(name) + "\n" + Normalize(publisher));
}
