using System.Diagnostics;
using System.IO.Compression;
using Miguel.Contracts;

namespace Atlas.Updater;

internal interface IUpdatePlatform
{
    IReadOnlyList<UpdaterComponentVersion> Versions();
    bool BackupOpen();
    Task ApplyAgentAsync(string package, string work, CancellationToken ct);
    Task RestoreAgentAsync(string backup, CancellationToken ct);
    Task<bool> HealthyAsync(CancellationToken ct);
    void StartSelf(string path, Guid release);
}

internal sealed class WindowsUpdatePlatform(UpdaterPaths paths, UpdateLog log) : IUpdatePlatform
{
    public IReadOnlyList<UpdaterComponentVersion> Versions() => [
        new("agent", VersionTagComparer.ReadProductVersion(paths.AgentExecutable)),
        new("backupnas", VersionTagComparer.ReadProductVersion(paths.BackupExecutable)),
        new("updater", VersionTagComparer.ReadProductVersion(paths.UpdaterExecutable))];
    public bool BackupOpen()
    {
        var processes = Process.GetProcessesByName("BackupNAS");
        try { return processes.Length > 0; } finally { foreach (var p in processes) p.Dispose(); }
    }
    public async Task ApplyAgentAsync(string package, string work, CancellationToken ct)
    {
        string extracted = DurableFile.Child(work, "agent-package");
        Extract(package, extracted);
        string installer = DurableFile.Child(extracted, "Install-MiguelAgent.ps1");
        string source = DurableFile.Child(extracted, "agent");
        if (!File.Exists(installer) || !File.Exists(Path.Combine(source, "Miguel.Agent.exe"))) throw new InvalidDataException("invalid_agent_package");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        int result = await ProcessRunner.RunAsync(powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", installer, "-SourceDirectory", source],
            TimeSpan.FromMinutes(8), log, ct);
        if (result != 0) throw new IOException("agent_install_failed");
    }
    public async Task RestoreAgentAsync(string backup, CancellationToken ct)
    {
        string expected = Path.GetFullPath(paths.RecoveryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(backup).StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(backup, "Miguel.Agent.exe"))) throw new IOException("agent_recovery_missing");
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        _ = await ProcessRunner.RunAsync(Path.Combine(system, "sc.exe"), ["stop", "MIGUEL Agent"], TimeSpan.FromSeconds(30), log, ct);
        // Wait for STOPPED before mirroring. An unsuccessful stop never destroys the recovery copy.
        for (int i = 0; i < 30; i++)
        {
            if (await ServiceStateAsync("STOPPED", ct)) break;
            await Task.Delay(1000, ct);
        }
        if (!await ServiceStateAsync("STOPPED", ct)) throw new IOException("agent_stop_not_confirmed");
        int copied = await ProcessRunner.RunAsync(Path.Combine(system, "robocopy.exe"),
            [backup, paths.AgentDirectory, "/MIR", "/XJ", "/R:2", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP"], TimeSpan.FromMinutes(3), log, ct);
        if (copied > 7) throw new IOException("agent_recovery_copy_failed");
        _ = await ProcessRunner.RunAsync(Path.Combine(system, "sc.exe"), ["start", "MIGUEL Agent"], TimeSpan.FromSeconds(30), log, ct);
        if (!await HealthyAsync(ct)) throw new IOException("agent_recovery_health_failed");
    }
    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.AgentExecutable)) return false;
        for (int i = 0; i < 20; i++)
        {
            if (await ServiceRunningAsync(ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return await ServiceRunningAsync(ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }
    private static Task<bool> ServiceRunningAsync(CancellationToken ct) => ServiceStateAsync("RUNNING", ct);
    private static async Task<bool> ServiceStateAsync(string expected, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("query"); start.ArgumentList.Add("MIGUEL Agent");
        using var process = Process.Start(start) ?? throw new IOException("service_query_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); _ = await error;
        return process.ExitCode == 0 && (await output).Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
    public void StartSelf(string path, Guid release)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--complete-coordinated-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(release.ToString("D"));
        _ = Process.Start(start) ?? throw new IOException("self_update_start_failed");
    }
    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse_point_not_allowed");
            string output = DurableFile.Child(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, output); else DurableFile.Copy(entry, output);
        }
    }
    internal static void Extract(string archive, string target)
    {
        Directory.CreateDirectory(target); long total = 0;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("too_many_archive_entries");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = DurableFile.Child(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!names.Add(path) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || entry.FullName.Contains(':'))
                throw new InvalidDataException("invalid_archive_entry");
            total = checked(total + entry.Length);
            if (total > 500_000_000) throw new InvalidDataException("archive_too_large");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, true);
        }
    }
}
