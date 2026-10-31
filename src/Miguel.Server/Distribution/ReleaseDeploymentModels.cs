namespace Miguel.Server.Distribution;

public static class ReleaseDeploymentScopes
{
    public const string Device = "DEVICE";
    public const string Ring = "RING";
    public const string Agency = "AGENCY";

    public static (string ScopeType, string ScopeValue) Normalize(
        string scopeType,
        string scopeValue)
    {
        string normalizedType = (scopeType ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        string normalizedValue = (scopeValue ?? string.Empty).Trim();

        return normalizedType switch
        {
            Device when Guid.TryParse(normalizedValue, out Guid deviceId) &&
                        deviceId != Guid.Empty =>
                (Device, deviceId.ToString("D")),
            Ring when string.Equals(
                normalizedValue,
                UpdateRings.Pilot,
                StringComparison.OrdinalIgnoreCase) =>
                (Ring, UpdateRings.Pilot),
            Agency when IsValidAgencyCode(normalizedValue) =>
                (Agency, normalizedValue.ToUpperInvariant()),
            _ => throw new ArgumentException(
                "El destino del despliegue no es valido.")
        };
    }

    private static bool IsValidAgencyCode(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
}

public static class UpdateRings
{
    public const string General = "GENERAL";
    public const string Pilot = "PILOT";

    public static string Normalize(string ringCode)
    {
        string normalized = (ringCode ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        return normalized is General or Pilot
            ? normalized
            : throw new ArgumentException(
                "El anillo debe ser GENERAL o PILOT.");
    }
}

public sealed record AtlasReleaseDeploymentRequest(
    string ScopeType,
    string ScopeValue);

public sealed record AtlasReleaseDeploymentPauseRequest(
    string ScopeType,
    string ScopeValue,
    bool Paused);

public sealed record AtlasDeviceUpdateRingRequest(string RingCode);

public sealed record AtlasReleaseDeploymentView(
    string ScopeType,
    string ScopeValue,
    Guid ReleaseId,
    bool Paused,
    int TargetDevices,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AtlasUpdateRingView(
    string RingCode,
    int Devices);

public sealed record AtlasReleaseDeploymentStoreView(
    IReadOnlyList<AtlasReleaseDeploymentView> Deployments,
    IReadOnlyList<AtlasUpdateRingView> Rings);
