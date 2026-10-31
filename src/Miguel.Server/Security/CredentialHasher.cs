using System.Security.Cryptography;
using System.Text;

namespace Miguel.Server.Security;

public static class CredentialHasher
{
    public static byte[] HashDeviceKey(Guid deviceId, string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId:D}:{deviceKey}"));
    }

    public static byte[] HashEnrollmentToken(string enrollmentToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentToken);
        return SHA256.HashData(Encoding.UTF8.GetBytes(enrollmentToken));
    }

    public static bool FixedTimeEquals(byte[] expected, byte[] actual) =>
        expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(expected, actual);

    public static string CreateSecret(int byteCount = 32)
    {
        if (byteCount < 24)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), "El secreto debe contener al menos 192 bits.");
        }

        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
