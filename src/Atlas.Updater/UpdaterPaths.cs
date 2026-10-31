namespace Atlas.Updater;

internal sealed class UpdaterPaths
{
    public UpdaterPaths(string? testRoot = null)
    {
        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (testRoot is not null)
        {
            programData = Path.Combine(Path.GetFullPath(testRoot), "ProgramData");
            programFiles = Path.Combine(Path.GetFullPath(testRoot), "ProgramFiles");
        }
        RootDirectory = Path.Combine(programData, "ATLAS", "Updater");
        LockFile = Path.Combine(RootDirectory, "updater.lock");
        StagingDirectory = Path.Combine(RootDirectory, "Staging");
        LogDirectory = Path.Combine(RootDirectory, "Logs");
        StatusDirectory = Path.Combine(RootDirectory, "Status");
        PendingUpdateFile = Path.Combine(
            StatusDirectory,
            "pending-update.json");
        StateFile = Path.Combine(RootDirectory, "state.json");
        IdentityFile = Path.Combine(
            programData,
            "MIGUEL",
            "Config",
            "agent-identity.json");
        ReleasePublicKeyFile = Path.Combine(
            programData,
            "ATLAS",
            "Trust",
            "release-signing.public.pem");
        ComponentPolicyFile = Path.Combine(
            programData,
            "MIGUEL",
            "Config",
            "components.json");
        AgentDirectory = Path.Combine(programFiles, "MIGUEL", "Agent");
        AgentExecutable = Path.Combine(AgentDirectory, "Miguel.Agent.exe");
        BackupDirectory = Path.Combine(programFiles, "ATLAS", "BackupNAS");
        BackupExecutable = Path.Combine(BackupDirectory, "BackupNAS.exe");
        BackupServiceDirectory = Path.Combine(
            programFiles,
            "ATLAS",
            "BackupService");
        BackupServiceExecutable = Path.Combine(
            BackupServiceDirectory,
            "ATLAS.Backup.Service.exe");
        UpdaterDirectory = Path.Combine(programFiles, "ATLAS", "Updater");
        UpdaterExecutable = Path.Combine(
            UpdaterDirectory,
            "ATLAS.Updater.exe");
    }

    public string RootDirectory { get; }
    public string LockFile { get; }
    public string StagingDirectory { get; }
    public string LogDirectory { get; }
    public string StatusDirectory { get; }
    public string PendingUpdateFile { get; }
    public string StateFile { get; }
    public string IdentityFile { get; }
    public string ReleasePublicKeyFile { get; }
    public string ComponentPolicyFile { get; }
    public string AgentDirectory { get; }
    public string AgentExecutable { get; }
    public string BackupDirectory { get; }
    public string BackupExecutable { get; }
    public string BackupServiceDirectory { get; }
    public string BackupServiceExecutable { get; }
    public string UpdaterDirectory { get; }
    public string UpdaterExecutable { get; }
    public string JournalFile => Path.Combine(RootDirectory, "operation.json");
    public string EventQueueDirectory => Path.Combine(RootDirectory, "Events");
    public string CacheDirectory => Path.Combine(RootDirectory, "Cache");
    public string RecoveryDirectory => Path.Combine(RootDirectory, "Recovery");
    public string TrustStateFile => Path.Combine(RootDirectory, "trust-transition.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(StatusDirectory);
        Directory.CreateDirectory(EventQueueDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }
}
