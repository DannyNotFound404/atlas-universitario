using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed class CoordinatedUpdateEngine(UpdaterPaths paths, UpdateLog log, IUpdatePlatform platform, HttpMessageHandler? handler = null)
{
    internal const string Version = "0.4.0-rc.1";
    private UpdateJournal? journal;
    private UpdaterProtocolClient? protocol;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        paths.EnsureDirectories();
        journal = DurableFile.Read<UpdateJournal>(paths.JournalFile);
        // Recovery must work even if network, credentials, or the telemetry queue are unavailable.
        if (journal?.Phase is "APPLYING" or "ROLLING_BACK" or "RECOVERY_FAILED" or "SELF_PENDING")
        {
            if (journal.Phase == "RECOVERY_FAILED" && !journal.TerminalEventQueued)
            { await EmitAsync("RECOVERY_FAILED", "manual_recovery_required", ct); journal = journal with { TerminalEventQueued = true }; Save(); }
            RetryState? recoveryRetry = DurableFile.Read<RetryState>(Path.Combine(paths.RootDirectory, "recovery-retry.json"));
            int result = journal.Phase == "RECOVERY_FAILED" && recoveryRetry?.NextAttemptUtc > DateTimeOffset.UtcNow ? 21 : await RecoverAsync(ct);
            // A failed local recovery must still reach the console when connectivity permits.
            try
            {
                var recoveryIdentity = DurableFile.Read<AgentIdentity>(paths.IdentityFile);
                if (recoveryIdentity is not null)
                {
                    using var recoveryConnection = new UpdaterProtocolClient(recoveryIdentity, handler); protocol = recoveryConnection;
                    await FlushEventsAsync(ct);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or TaskCanceledException) { log.Write("El resultado de recuperación permanece en la cola local."); }
            return result;
        }
        AgentIdentity identity = DurableFile.Read<AgentIdentity>(paths.IdentityFile) ?? throw new IOException("identity_missing");
        using var connection = new UpdaterProtocolClient(identity, handler); protocol = connection;
        await FlushEventsAsync(ct);
        if (Directory.EnumerateFiles(paths.EventQueueDirectory, "*.json").Take(5001).Count() > 5000)
            throw new IOException("evidence_queue_full");
        if (journal is not null)
        {
            if (journal.Phase == "AWAITING_CONFIRMATION") return await ConfirmAsync(ct);
            if (journal.Phase == "CONFIRMED")
            { DurableFile.Write(paths.StateFile, new UpdateState(1, journal.ReleaseId, journal.ReleaseVersion, DateTimeOffset.UtcNow)); File.Delete(paths.PendingUpdateFile); }
            if (journal.Phase is "ROLLED_BACK" or "CANCELLED") await CompleteTerminalAsync(ct);
            if (journal.Phase is "CONFIRMED" or "ROLLED_BACK" or "CANCELLED") ArchiveJournal();
        }
        string retryPath = Path.Combine(paths.RootDirectory, "retry.json");
        RetryState? retry = DurableFile.Read<RetryState>(retryPath);
        if (retry is not null && retry.NextAttemptUtc > DateTimeOffset.UtcNow) return 10;
        try
        {
            UpdaterCheckResponse check = await protocol.CheckAsync(new(Version, platform.Versions()), ct);
            if (check.Decision != "AVAILABLE" || check.Release is null)
            { log.Write($"Comprobación: {check.Reason}."); Defer(retryPath, check.RetryAfterSeconds, 0, check.Reason); return 10; }
            QuarantinedRelease? quarantine = DurableFile.Read<QuarantinedRelease>(Path.Combine(paths.RootDirectory, "quarantined-release.json"));
            if (quarantine is not null && quarantine.ReleaseId == check.ReleaseId && quarantine.RetryGeneration >= check.RetryGeneration)
            { log.Write("La versión fallida espera autorización de reintento o una versión diferente."); return 10; }
            string publicKey = LoadTrustedKey(check.TrustTransition);
            using var signed = new SignedReleaseClient(identity, paths.ReleasePublicKeyFile, log, handler);
            VerifiedRelease release;
            try { release = signed.VerifyEnvelope(check.Release, publicKey); }
            catch (CryptographicException) { release = signed.VerifyEnvelope(check.Release, File.ReadAllText(paths.ReleasePublicKeyFile)); }
            if (release.Manifest.ReleaseId != check.ReleaseId) throw new InvalidDataException("release_identity_mismatch");
            bool recovery;
            try { recovery = VerifyRecovery(check.RecoveryAuthorization, release.Manifest, publicKey); }
            catch (CryptographicException) { recovery = VerifyRecovery(check.RecoveryAuthorization, release.Manifest, File.ReadAllText(paths.ReleasePublicKeyFile)); }
            var before = platform.Versions();
            bool backupEnabled = ComponentPolicy.IsBackupNasEnabled(paths.ComponentPolicyFile);
            AtlasReleasePackage[] packages = release.Manifest.Packages.Where(p => p.Component is "agent" or "updater" || p.Component == "backupnas" && backupEnabled)
                .Where(p => recovery ? before.SingleOrDefault(v => v.Component == p.Component)?.Version != p.Version :
                    VersionTagComparer.IsTargetNewer(p.Version, before.SingleOrDefault(v => v.Component == p.Component)?.Version)).ToArray();
            if (packages.Length == 0) { log.Write("Los componentes ya corresponden a la versión autorizada."); File.Delete(retryPath); return 0; }
            Guid attempt = journal is { Phase: "DOWNLOADING" or "READY" } && journal.ReleaseId == release.Manifest.ReleaseId
                ? journal.AttemptId : Guid.NewGuid();
            var lease = await protocol.LeaseAsync(new(release.Manifest.ReleaseId, attempt, Version), ct);
            if (!lease.Granted || lease.LeaseId is null) { Defer(retryPath, lease.RetryAfterSeconds, 0, lease.Reason); return 10; }
            journal = new(attempt, release.Manifest.ReleaseId, release.Manifest.ReleaseVersion, lease.LeaseId.Value, "DOWNLOADING",
                journal?.AttemptId == attempt ? journal.Sequence : 0, DateTimeOffset.UtcNow, before, packages, RetryGeneration: check.RetryGeneration);
            Save();
            string work = WorkDirectory(journal); Directory.CreateDirectory(work);
            CleanCache();
            var downloads = new Dictionary<string, string>(StringComparer.Ordinal);
            signed.AttemptId = attempt;
            long downloadBytes = packages.Sum(p => p.SizeBytes);
            string driveRoot = Path.GetPathRoot(Path.GetFullPath(paths.RootDirectory))!;
            if (new DriveInfo(driveRoot).AvailableFreeSpace < downloadBytes * 3 + 1_073_741_824L)
                throw new IOException("insufficient_update_space");
            foreach (var package in packages)
            {
                await EmitAsync("DOWNLOADING", "downloading", ct);
                downloads[package.Component] = await signed.DownloadResumableAsync(release.Manifest.ReleaseId, package, paths.CacheDirectory,
                    check.Policy.DownloadKiBPerSecond, async (done, total) => await EmitAsync("DOWNLOADING", "downloading", ct, done, total), ct);
            }
            journal = journal with { Phase = "READY" }; Save(); await EmitAsync("READY", "packages_verified", ct);
            // Existing BackupNAS 5.7+ understands this marker and closes itself only when idle.
            if (platform.BackupOpen())
            {
                DurableFile.Write(paths.PendingUpdateFile, new PendingUpdateStatus(1, journal.ReleaseId, journal.ReleaseVersion, DateTimeOffset.UtcNow, "coordinated_update"));
                for (int i = 0; i < 6 && platform.BackupOpen(); i++) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (platform.BackupOpen())
                { await EmitAsync("DEFERRED", "backup_busy", ct); Defer(retryPath, 300, 0, "backup_busy"); return 10; }
            }
            if (!await protocol.InstallAsync(new(journal.AttemptId, journal.LeaseId), ct))
            { journal = journal with { Phase = "CANCELLED", Code = "authorization_changed" }; Save(); await CompleteTerminalAsync(ct); return 10; }
            // Persist recoverable snapshots before marking any component as changed.
            if (downloads.ContainsKey("agent"))
            {
                if (!File.Exists(paths.AgentExecutable)) throw new IOException("agent_baseline_missing");
                WindowsUpdatePlatform.CopyTree(paths.AgentDirectory, Path.Combine(work, "agent-before"));
                journal = journal with { AgentBackupReady = true }; Save();
            }
            if (downloads.ContainsKey("backupnas"))
            {
                if (!File.Exists(paths.BackupExecutable)) throw new IOException("backup_baseline_missing");
                DurableFile.Copy(paths.BackupExecutable, Path.Combine(work, "backup-before.exe"));
                journal = journal with { BackupBackupReady = true }; Save();
            }
            journal = journal with { Phase = "APPLYING" }; Save();
            await EmitAsync("INSTALLING", "applying", ct);
            try
            {
                if (downloads.TryGetValue("agent", out string? agent))
                {
                    journal = journal with { AgentChanged = true }; Save();
                    await platform.ApplyAgentAsync(agent, work, ct);
                    RequireVersion("agent", packages.Single(p => p.Component == "agent").Version);
                }
                if (downloads.TryGetValue("backupnas", out string? backup))
                {
                    if (platform.BackupOpen()) throw new IOException("backup_started_during_install");
                    journal = journal with { BackupChanged = true }; Save();
                    string next = paths.BackupExecutable + ".next"; File.Copy(backup, next, true);
                    File.Move(next, paths.BackupExecutable, true);
                    RequireVersion("backupnas", packages.Single(p => p.Component == "backupnas").Version);
                }
                if (!await platform.HealthyAsync(ct)) throw new IOException("agent_health_failed");
                if (downloads.TryGetValue("updater", out string? updater))
                {
                    DurableFile.Copy(paths.UpdaterExecutable, Path.Combine(work, "updater-before.exe"));
                    // Keep the executable extension: the content-addressed cache uses .package.
                    string helper = Path.Combine(work, "ATLAS.Updater.next.exe"); File.Copy(updater, helper, true);
                    journal = journal with { UpdaterChanged = true, Phase = "SELF_PENDING" }; Save();
                    platform.StartSelf(helper, journal.ReleaseId);
                    return 11;
                }
                journal = journal with { Phase = "AWAITING_CONFIRMATION" }; Save();
                await EmitAsync("AWAITING_CONFIRMATION", "installed_waiting_report", ct);
                File.Delete(retryPath); File.Delete(paths.PendingUpdateFile);
                return await ConfirmAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Write($"Aplicación interrumpida: {ex.GetType().Name}. Se conserva la recuperación.");
                return await RecoverAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string code = ex is UpdateAdmissionException ? "authorization_changed" : ex is CryptographicException ? "signature_rejected" : ex is InvalidDataException ? "package_rejected" :
                ex.Message == "insufficient_update_space" ? "insufficient_space" : "network_or_preflight_failed";
            log.Write($"Actualización pendiente: {code} ({ex.GetType().Name}).");
            if (journal is not null && journal.Phase is "DOWNLOADING" or "READY")
            {
                bool permanent = ex is CryptographicException or InvalidDataException;
                if (permanent)
                {
                    journal = journal with { Phase = "CANCELLED", Code = code }; Save();
                    await CompleteTerminalAsync(ct);
                }
                else await EmitAsync("DEFERRED", code, ct);
            }
            int failures = (retry?.Failures ?? 0) + 1;
            Defer(retryPath, Math.Min(21600, 300 * (1 << Math.Min(failures - 1, 6))) + Random.Shared.Next(0, 120), failures, code);
            return 10;
        }
    }

    internal async Task<int> RecoverAsync(CancellationToken ct)
    {
        journal ??= DurableFile.Read<UpdateJournal>(paths.JournalFile) ?? throw new IOException("recovery_journal_missing");
        journal = journal with { Phase = "ROLLING_BACK", TerminalEventQueued = false }; Save();
        await EmitAsync("ROLLING_BACK", "recovering_previous_version", ct);
        string work = WorkDirectory(journal);
        try
        {
            if (journal.UpdaterChanged)
            {
                string previous = Path.Combine(work, "updater-before.exe");
                if (!File.Exists(previous)) throw new IOException("updater_recovery_missing");
                // A running installed updater cannot replace itself. A helper from recovery performs that operation.
                if (string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(paths.UpdaterExecutable), StringComparison.OrdinalIgnoreCase))
                {
                    string helper = Path.Combine(work, "ATLAS.Updater.recover.exe"); File.Copy(paths.UpdaterExecutable, helper, true);
                    platform.StartSelf(helper, journal.ReleaseId); return 11;
                }
                File.Copy(previous, paths.UpdaterExecutable + ".next", true);
                File.Move(paths.UpdaterExecutable + ".next", paths.UpdaterExecutable, true);
                RequireVersion("updater", journal.Before.Single(p => p.Component == "updater").Version);
            }
            if (journal.BackupChanged)
            {
                if (!journal.BackupBackupReady || platform.BackupOpen()) throw new IOException("backup_recovery_deferred");
                File.Copy(Path.Combine(work, "backup-before.exe"), paths.BackupExecutable + ".next", true);
                File.Move(paths.BackupExecutable + ".next", paths.BackupExecutable, true);
                RequireVersion("backupnas", journal.Before.Single(p => p.Component == "backupnas").Version);
            }
            if (journal.AgentChanged)
            {
                if (!journal.AgentBackupReady) throw new IOException("agent_recovery_missing");
                await platform.RestoreAgentAsync(Path.Combine(work, "agent-before"), ct);
                RequireVersion("agent", journal.Before.Single(p => p.Component == "agent").Version);
            }
            if (!await platform.HealthyAsync(ct)) throw new IOException("recovered_agent_unhealthy");
            journal = journal with { Phase = "ROLLED_BACK", Code = "previous_version_restored" }; Save();
            await CompleteTerminalAsync(ct);
            File.Delete(paths.PendingUpdateFile);
            File.Delete(Path.Combine(paths.RootDirectory, "recovery-retry.json"));
            return 20;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            journal = journal with { Phase = "RECOVERY_FAILED", Code = "manual_recovery_required" }; Save();
            await EmitAsync("RECOVERY_FAILED", "manual_recovery_required", ct);
            journal = journal with { TerminalEventQueued = true }; Save();
            string retryPath = Path.Combine(paths.RootDirectory, "recovery-retry.json");
            int failures = (DurableFile.Read<RetryState>(retryPath)?.Failures ?? 0) + 1;
            Defer(retryPath, Math.Min(21600, 1800 * (1 << Math.Min(failures - 1, 4))), failures, "manual_recovery_required");
            log.Write($"Recuperación pendiente ({ex.GetType().Name}); los archivos anteriores permanecen en {work}.");
            return 21;
        }
    }

    private async Task CompleteTerminalAsync(CancellationToken ct)
    {
        if (journal is null) return;
        bool failed = journal.Phase == "ROLLED_BACK" || journal.Code is "package_rejected" or "signature_rejected";
        if (failed) DurableFile.Write(Path.Combine(paths.RootDirectory, "quarantined-release.json"), new QuarantinedRelease(journal.ReleaseId, journal.RetryGeneration));
        if (journal.TerminalEventQueued) return;
        await EmitAsync(journal.Phase == "ROLLED_BACK" ? "ROLLED_BACK" : failed ? "FAILED" : "CANCELLED", journal.Code ?? "authorization_changed", ct);
        journal = journal with { TerminalEventQueued = true }; Save();
    }

    private async Task<int> ConfirmAsync(CancellationToken ct)
    {
        if (journal is null || protocol is null) return 10;
        try
        {
            if (journal.Code != "awaiting_report_queued")
            {
                await EmitAsync("AWAITING_CONFIRMATION", "installed_waiting_report", ct);
                journal = journal with { Code = "awaiting_report_queued" }; Save();
            }
            var result = await protocol.ConfirmAsync(journal.AttemptId, ct);
            if (!result.Confirmed) { log.Write($"Pendiente de confirmación: {result.Reason}."); return 10; }
            journal = journal with { Phase = "CONFIRMED", Code = "heartbeat_confirmed" }; Save();
            DurableFile.Write(paths.StateFile, new UpdateState(1, journal.ReleaseId, journal.ReleaseVersion, DateTimeOffset.UtcNow));
            File.Delete(paths.PendingUpdateFile); CleanCache(); return 0;
        }
        catch (HttpRequestException) { log.Write("Instalado; confirmación pendiente de conexión."); return 10; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return 10; }
    }

    private async Task EmitAsync(string phase, string code, CancellationToken ct, long done = 0, long total = 0)
    {
        if (journal is null) return;
        journal = journal with { Sequence = journal.Sequence + 1 }; Save();
        var item = new UpdaterEvent(Guid.NewGuid(), journal.AttemptId, journal.ReleaseId, journal.Sequence, phase, code,
            Version, DateTimeOffset.UtcNow, platform.Versions(), done, total, journal.AgentChanged, journal.Packages.Select(p => p.Component).ToArray());
        DurableFile.Write(Path.Combine(paths.EventQueueDirectory, $"{journal.AttemptId:D}-{journal.Sequence:D12}.json"), item);
        await FlushEventsAsync(ct);
    }
    private async Task FlushEventsAsync(CancellationToken ct)
    {
        if (protocol is null) return;
        foreach (string file in Directory.EnumerateFiles(paths.EventQueueDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                UpdaterEvent item = DurableFile.Read<UpdaterEvent>(file) ?? throw new InvalidDataException("invalid_event_queue");
                await protocol.ReportAsync(item, ct); File.Delete(file);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { log.Write("La evidencia local espera conexión con el servidor."); break; }
        }
    }
    private string LoadTrustedKey(SignedUpdaterControl? transition)
    {
        string root = File.ReadAllText(paths.ReleasePublicKeyFile);
        TrustState? current = DurableFile.Read<TrustState>(paths.TrustStateFile);
        if (current is not null)
        {
            var accepted = UpdaterControlValidator.Verify(current.Envelope, root, current.AcceptedAtUtc);
            if (accepted.Purpose != "TRUST_TRANSITION" || accepted.Generation != current.Generation) throw new InvalidDataException("invalid_saved_trust");
        }
        if (transition is not null)
        {
            var document = UpdaterControlValidator.Verify(transition, root, DateTimeOffset.UtcNow);
            if (document.Purpose != "TRUST_TRANSITION") throw new InvalidDataException("invalid_trust_purpose");
            if (current is null || document.Generation > current.Generation)
            {
                current = new(transition, document.Generation, DateTimeOffset.UtcNow);
                DurableFile.Write(paths.TrustStateFile, current);
            }
        }
        return current is null ? root : UpdaterControlValidator.Verify(current.Envelope, root, current.AcceptedAtUtc).NextPublicKeyPem!;
    }
    private static bool VerifyRecovery(SignedUpdaterControl? envelope, AtlasReleaseManifest manifest, string key)
    {
        if (envelope is null) return false;
        var value = UpdaterControlValidator.Verify(envelope, key, DateTimeOffset.UtcNow);
        return value.Purpose == "RECOVERY" && value.ReleaseId == manifest.ReleaseId &&
            manifest.Packages.All(p => value.PackageHashes!.TryGetValue(p.Component, out string? hash) && string.Equals(hash, p.Sha256, StringComparison.OrdinalIgnoreCase));
    }
    private void RequireVersion(string component, string? expected)
    { if (string.IsNullOrWhiteSpace(expected) || platform.Versions().Single(v => v.Component == component).Version != expected) throw new IOException("installed_version_mismatch"); }
    private string WorkDirectory(UpdateJournal value) => DurableFile.Child(paths.RecoveryDirectory, value.AttemptId.ToString("D"));
    private void Save() => DurableFile.Write(paths.JournalFile, journal!);
    private void ArchiveJournal()
    {
        if (journal is null) return;
        DurableFile.Write(Path.Combine(WorkDirectory(journal), "result.json"), journal);
        File.Delete(paths.JournalFile); journal = null;
        RecoveryRetention.Clean(paths.RecoveryDirectory, DateTimeOffset.UtcNow);
    }
    private void CleanCache()
    {
        var files = new DirectoryInfo(paths.CacheDirectory).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (journal?.Packages.Any(p => file.Name.StartsWith(p.Sha256, StringComparison.OrdinalIgnoreCase)) == true) continue;
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7) && bytes <= 1_073_741_824L) continue;
            _ = DurableFile.Child(paths.CacheDirectory, file.Name);
            try { long size = file.Length; file.Delete(); bytes -= size; } catch (IOException) { }
        }
    }
    private static void Defer(string path, int seconds, int failures, string reason) => DurableFile.Write(path,
        new RetryState(DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 21600)), failures, reason));
    private sealed record RetryState(DateTimeOffset NextAttemptUtc, int Failures, string Reason);
    private sealed record QuarantinedRelease(Guid ReleaseId, int RetryGeneration);
    private sealed record TrustState(SignedUpdaterControl Envelope, long Generation, DateTimeOffset AcceptedAtUtc);
}
