namespace Miguel.Server.Nas;

internal static class NasMountInspector
{
    private static readonly HashSet<string> SupportedFileSystems = new(
        ["cifs", "nfs", "nfs4"],
        StringComparer.Ordinal);

    public static bool IsSupportedReadOnlyMount(string mountPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Directory.Exists(mountPath);
        }

        return IsSupportedReadOnlyMount(
            mountPath,
            File.ReadLines("/proc/self/mountinfo"));
    }

    internal static bool IsSupportedReadOnlyMount(
        string mountPath,
        IEnumerable<string> mountInfoLines)
    {
        string expected = Path.GetFullPath(mountPath)
            .TrimEnd(Path.DirectorySeparatorChar);
        foreach (string line in mountInfoLines)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int separator = Array.IndexOf(fields, "-");
            if (separator < 0 || separator + 1 >= fields.Length || fields.Length < 6)
            {
                continue;
            }
            string mountedAt = DecodeMountInfoPath(fields[4])
                .TrimEnd(Path.DirectorySeparatorChar);
            string fileSystem = fields[separator + 1];
            if (string.Equals(mountedAt, expected, StringComparison.Ordinal) &&
                SupportedFileSystems.Contains(fileSystem) &&
                HasMountOption(fields[5], "ro"))
            {
                return true;
            }
        }
        return false;
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string DecodeMountInfoPath(string value) =>
        value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static bool HasMountOption(string options, string expected) =>
        options.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(expected, StringComparer.Ordinal);
}
