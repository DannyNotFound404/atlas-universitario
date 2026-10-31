using System.Diagnostics;
using System.Text.Json;
using Miguel.Contracts;

namespace BackupNAS;

internal static class BackupReportDelivery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string PendingDirectory => Environment.GetEnvironmentVariable("ATLAS_BACKUP_JOURNAL_DIR") is { Length: > 0 } path
        ? Path.GetFullPath(path) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATLAS", "BackupTelemetry", "Pending");
    internal static string InboxDirectory => Environment.GetEnvironmentVariable("ATLAS_BACKUP_INBOX_DIR") is { Length: > 0 } path
        ? Path.GetFullPath(path) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ATLAS", "BackupInbox");

    internal static bool Submit(BackupRunReport report)
    {
        Directory.CreateDirectory(PendingDirectory);
        string name = $"{report.FinishedAtUtc.UtcTicks:D19}-{report.EventId:N}-{report.Evidence!.Sequence:D10}.json";
        string pending = Path.Combine(PendingDirectory, name);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
        AtomicLocalWrite(pending, bytes);
        if (report.Evidence.Phase == "FINISHED")
        {
            string history = Path.Combine(Path.GetDirectoryName(PendingDirectory)!, "History");
            Directory.CreateDirectory(history);
            AtomicLocalWrite(Path.Combine(history, report.EventId.ToString("N") + ".json"), bytes);
        }
        return TryDeliver(pending);
    }

    internal static void Retry()
    {
        if (!Directory.Exists(PendingDirectory)) return;
        foreach (string pending in Directory.EnumerateFiles(PendingDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal).Take(100))
            TryDeliver(pending);
    }

    private static bool TryDeliver(string pending)
    {
        try
        {
            // The installed inbox intentionally grants only WRITE to users.
            // CREATE_NEW plus an exclusive handle works with that ACL. The
            // agent cannot open or reject the JSON until this handle closes.
            // No inbox read, rename, overwrite or delete permission is needed.
            string name = Path.GetFileNameWithoutExtension(pending) + "-" + Guid.NewGuid().ToString("N") + ".json";
            using (var stream = OpenInboxWriter(Path.Combine(InboxDirectory, name)))
            {
                byte[] bytes = File.ReadAllBytes(pending);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Delete(pending);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("ATLAS: informe pendiente de entrega (" + ex.GetType().Name + ").");
            return false;
        }
    }

    private static FileStream OpenInboxWriter(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(InboxDirectory);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
    }

    private static void AtomicLocalWrite(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
