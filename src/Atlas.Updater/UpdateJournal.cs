using System.Text.Json;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed record UpdateJournal(Guid AttemptId, Guid ReleaseId, string ReleaseVersion, Guid LeaseId,
    string Phase, long Sequence, DateTimeOffset StartedAtUtc, IReadOnlyList<UpdaterComponentVersion> Before,
    IReadOnlyList<AtlasReleasePackage> Packages, bool AgentChanged = false, bool BackupChanged = false,
    bool UpdaterChanged = false, bool AgentBackupReady = false, bool BackupBackupReady = false,
    string? Code = null, int RetryGeneration = 0, bool TerminalEventQueued = false);

internal static class DurableFile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".next";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;

    public static void Copy(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough);
        input.CopyTo(output); output.Flush(flushToDisk: true);
    }

    public static string Child(string parent, string name)
    {
        string boundary = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(parent, name));
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("path_outside_boundary");
        return target;
    }
}
