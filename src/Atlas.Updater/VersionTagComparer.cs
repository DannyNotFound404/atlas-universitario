using System.Diagnostics;

namespace Atlas.Updater;

internal static class VersionTagComparer
{
    public static bool IsAtLeast(
        string? installed,
        string minimum) =>
        !string.IsNullOrWhiteSpace(installed) &&
        !IsTargetNewer(minimum, installed);

    public static bool IsTargetNewer(
        string target,
        string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed))
        {
            return true;
        }
        if (string.Equals(
                target,
                installed,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var left = Parse(target);
        var right = Parse(installed);
        for (int i = 0; i < Math.Max(left.Core.Length, right.Core.Length); i++)
        {
            int order = (i < left.Core.Length ? left.Core[i] : 0).CompareTo(i < right.Core.Length ? right.Core[i] : 0);
            if (order != 0) return order > 0;
        }
        if (left.Pre.Length == 0 || right.Pre.Length == 0) return left.Pre.Length == 0 && right.Pre.Length != 0;
        for (int i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            bool ln = long.TryParse(left.Pre[i], out long lv), rn = long.TryParse(right.Pre[i], out long rv);
            int order = ln && rn ? lv.CompareTo(rv) : ln != rn ? (ln ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(left.Pre[i], right.Pre[i]);
            if (order != 0) return order > 0;
        }
        return left.Pre.Length > right.Pre.Length;
    }

    public static string? ReadProductVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return FileVersionInfo.GetVersionInfo(path).ProductVersion;
    }

    private static (long[] Core, string[] Pre) Parse(string value)
    {
        string withoutMetadata = value.Trim().Split('+', 2)[0];
        string[] parts = withoutMetadata.Split('-', 2);
        if (parts[0].Length == 0 || parts[0].Length > 64) throw new InvalidDataException("invalid_component_version");
        string[] numbers = parts[0].Split('.');
        if (numbers.Length is < 1 or > 4 || numbers.Any(n => !long.TryParse(n, out long number) || number < 0))
            throw new InvalidDataException("invalid_component_version");
        string[] pre = parts.Length == 2 ? parts[1].Split(['.', '_'], StringSplitOptions.None) : [];
        if (pre.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new InvalidDataException("invalid_component_version");
        return (numbers.Select(long.Parse).ToArray(), pre);
    }
}
