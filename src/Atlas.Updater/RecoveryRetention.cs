namespace Atlas.Updater;

internal static class RecoveryRetention
{
    // These are application rollback files, not institutional backup contents.
    internal static void Clean(string root, DateTimeOffset now)
    {
        if (!Directory.Exists(root)) return;
        var completed = new List<(string Path, DateTimeOffset At)>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out Guid id) || HasReparsePoint(directory)) continue;
            try
            {
                var result = DurableFile.Read<UpdateJournal>(Path.Combine(directory, "result.json"));
                if (result?.AttemptId == id && result.Phase is "CONFIRMED" or "ROLLED_BACK" or "CANCELLED")
                    completed.Add((DurableFile.Child(root, id.ToString("D")), File.GetLastWriteTimeUtc(Path.Combine(directory, "result.json"))));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        }
        foreach (var item in completed.OrderByDescending(x => x.At).Skip(2).Where(x => x.At < now.AddDays(-14)))
        {
            try { if (!HasReparsePoint(item.Path)) Directory.Delete(item.Path, recursive: true); }
            catch (IOException) { }
        }
    }
    private static bool HasReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (string item in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(item) && HasReparsePoint(item)) return true;
        }
        return false;
    }
}
