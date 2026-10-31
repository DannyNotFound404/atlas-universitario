using System.Security.Cryptography;
using System.Text;
using Miguel.Contracts;

namespace Miguel.Server.Distribution;

public static class UpdaterRules
{
    public static void Validate(UpdaterPolicy value)
    {
        if (value.WavePercent is < 0 or > 100 || value.MaxConcurrent is < 1 or > 200 ||
            value.MaxConcurrentPerAgency < 1 || value.MaxConcurrentPerAgency > value.MaxConcurrent ||
            value.FailureStopCount is < 1 or > 100 || value.FailurePercent is < 1 or > 100 ||
            value.MinimumSamples is < 1 or > 100 || value.WindowStartMinute is < 0 or > 1439 ||
            value.WindowEndMinute is < 1 or > 1440 || value.WindowStartMinute == value.WindowEndMinute ||
            value.DownloadKiBPerSecond is < 64 or > 102400)
            throw new ArgumentException("Revise porcentajes, límites, horario y velocidad del despliegue.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new ArgumentException("La zona horaria no es válida."); }
    }

    public static int Bucket(Guid device, Guid release) =>
        (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{release:D}/{device:D}"))) % 100);

    public static bool InWindow(UpdaterPolicy policy, DateTimeOffset now)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
        int minute = local.Hour * 60 + local.Minute;
        return policy.WindowStartMinute < policy.WindowEndMinute
            ? minute >= policy.WindowStartMinute && minute < policy.WindowEndMinute
            : minute >= policy.WindowStartMinute || minute < policy.WindowEndMinute;
    }

    public static bool IsTerminal(string phase) => phase is "CONFIRMED" or "FAILED" or "ROLLED_BACK" or "RECOVERY_FAILED" or "CANCELLED";
    public static bool IsFailure(string phase) => phase is "FAILED" or "ROLLED_BACK" or "RECOVERY_FAILED";
    public static readonly HashSet<string> ClientPhases = ["DOWNLOADING", "READY", "INSTALLING", "AWAITING_CONFIRMATION", "DEFERRED", "FAILED", "ROLLING_BACK", "ROLLED_BACK", "RECOVERY_FAILED", "CANCELLED"];
}
