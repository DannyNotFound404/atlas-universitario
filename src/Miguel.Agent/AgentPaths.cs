namespace Miguel.Agent;

public sealed class AgentPaths
{
    public AgentPaths()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("MIGUEL_DATA_DIR");
        RootDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MIGUEL")
            : Path.GetFullPath(overrideDirectory);

        IdentityFile = Path.Combine(RootDirectory, "Config", "agent-identity.json");
        OutboxDirectory = Path.Combine(RootDirectory, "Outbox");
        SecurityDirectory = Path.Combine(RootDirectory, "Security");
        SecurityCursorFile = Path.Combine(SecurityDirectory, "event-cursors.json");
        BackupOutboxDirectory = Path.Combine(RootDirectory, "BackupOutbox");
        string? inboxOverride =
            Environment.GetEnvironmentVariable("ATLAS_BACKUP_INBOX_DIR");
        BackupInboxDirectory = string.IsNullOrWhiteSpace(inboxOverride)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "ATLAS",
                "BackupInbox")
            : Path.GetFullPath(inboxOverride);
    }

    public string RootDirectory { get; }
    public string IdentityFile { get; }
    public string OutboxDirectory { get; }
    public string SecurityDirectory { get; }
    public string SecurityCursorFile { get; }
    public string BackupOutboxDirectory { get; }
    public string BackupInboxDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdentityFile)!);
        Directory.CreateDirectory(OutboxDirectory);
        Directory.CreateDirectory(SecurityDirectory);
        Directory.CreateDirectory(BackupOutboxDirectory);
        Directory.CreateDirectory(BackupInboxDirectory);
    }
}
