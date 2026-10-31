using System;
using System.Collections.Generic;
using System.Linq;

namespace Miguel.Contracts;

// Shared by the Windows copy client, its courier agent and the server.
public sealed record BackupExecutionEvidence(
    int Version,
    long Sequence,
    string Phase,
    string Reason,
    IReadOnlyList<BackupFolderEvidence> Folders,
    IReadOnlyList<string> Warnings,
    bool ExcludesHidden = false,
    bool ExcludesTemporary = false);

public sealed record BackupFolderEvidence(
    string Name,
    string State,
    int? ExitCode = null,
    long? FilesTotal = null,
    long? FilesCopied = null,
    long? FilesUnchanged = null,
    long? FilesFailed = null,
    long? BytesCopied = null);

public static class BackupTelemetry
{
    public static bool IsComplete(BackupExecutionEvidence? evidence) =>
        evidence is { Version: 2, Phase: "FINISHED", Folders.Count: > 0 } &&
        evidence.Folders.All(f => f.State == "COMPLETED" && f.ExitCode is >= 0 and <= 3 &&
            f.FilesTotal.HasValue && f.FilesCopied.HasValue && f.FilesUnchanged.HasValue &&
            f.BytesCopied.HasValue && f.FilesFailed == 0 && f.FilesTotal == f.FilesCopied + f.FilesUnchanged);

    public static bool IsAcceptable(BackupRunReport report) =>
        report.Status is BackupRunStatus.Successful or BackupRunStatus.Warning &&
        report.FailedFolders is { Count: 0 } && report.MissingFolders is { Count: 0 } &&
        (report.Evidence is null ? report.Status == BackupRunStatus.Successful : IsComplete(report.Evidence));

    // Operational acceptance explicitly retains file failures in the raw report.
    // It is not a statement that all protected files were copied or restored.
    public static bool IsAcceptableWithPendingFiles(BackupRunReport report) =>
        report.Status is BackupRunStatus.Partial or BackupRunStatus.Failed &&
        report.MissingFolders is { Count: 0 } &&
        report.Evidence is { Reason: "PROCESS_FINISHED" } evidence &&
        IsAcceptableWithPendingFiles(evidence) &&
        report.FailedFolders is { } failed &&
        failed.All(name => evidence.Folders.Any(f => f.State == "FAILED" && f.FilesFailed > 0 &&
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));

    public static bool IsAcceptableWithPendingFiles(BackupExecutionEvidence? evidence) =>
        evidence is { Version: 2, Phase: "FINISHED", Reason: "PROCESS_FINISHED", Folders.Count: > 0 } &&
        evidence.Folders.All(f => f is not null &&
            f.FilesTotal is >= 0 and <= 1_000_000_000_000_000 &&
            f.FilesCopied is >= 0 and <= 1_000_000_000_000_000 &&
            f.FilesUnchanged is >= 0 and <= 1_000_000_000_000_000 &&
            f.FilesFailed is >= 0 and <= 1_000_000_000_000_000 &&
            f.BytesCopied is >= 0 and <= 1_000_000_000_000_000 &&
            f.FilesTotal == f.FilesCopied + f.FilesUnchanged + f.FilesFailed &&
            (f.State == "COMPLETED" && f.ExitCode is >= 0 and <= 3 && f.FilesFailed == 0 ||
             f.State == "FAILED" && f.ExitCode is >= 8 and <= 11 && f.FilesFailed > 0)) &&
        evidence.Folders.Any(f => f.FilesFailed > 0) &&
        evidence.Folders.Any(f => f.FilesCopied > 0 || f.FilesUnchanged > 0);

    public static string Result(BackupRunReport report, DateTimeOffset now) =>
        report.Status == BackupRunStatus.Running && report.FinishedAtUtc < now.AddMinutes(-10)
            ? "UNCONFIRMED" : report.Status.ToString();

    public static bool IsValid(BackupRunReport? report, DateTimeOffset now)
    {
        if (report is null || report.SchemaVersion != 1 || report.EventId == Guid.Empty ||
            report.StartedAtUtc == default || report.StartedAtUtc > report.FinishedAtUtc ||
            report.FinishedAtUtc > now.AddMinutes(5) || !Enum.IsDefined(report.Status) ||
            report.FilesCopied < 0 || report.FilesSkipped < 0 || report.BytesCopied < 0 ||
            !List(report.MissingFolders, 100, 1024) || !List(report.FailedFolders, 100, 1024) ||
            string.IsNullOrWhiteSpace(report.BackupClientVersion) || !Text(report.BackupClientVersion, 64) ||
            !Text(report.LogFileName, 255) || !Text(report.DestinationHost, 255) ||
            !Text(report.DestinationShare, 128) || !Text(report.DestinationBasePath, 1024) ||
            report.SnapshotMode is not ("vss" or "live" or "live-fallback") || !Text(report.SnapshotWarning, 512))
            return false;
        if (report.Evidence is not { } e)
            return report.Status is BackupRunStatus.Successful or BackupRunStatus.Warning or BackupRunStatus.Failed or BackupRunStatus.Cancelled;
        if (e.Version != 2 || e.Sequence <= 0 || e.Sequence > 1_000_000 ||
            e.Phase is not ("RUNNING" or "FINISHED") || !Text(e.Reason, 256) ||
            e.Folders is null || e.Folders.Count > 100 || !List(e.Warnings, 100, 512) ||
            (report.Status == BackupRunStatus.Running) != (e.Phase == "RUNNING")) return false;
        foreach (var f in e.Folders)
            if (f is null || string.IsNullOrWhiteSpace(f.Name) || !Text(f.Name, 1024) ||
                f.State is not ("PENDING" or "RUNNING" or "COMPLETED" or "FAILED" or "MISSING" or "UNCONFIRMED") ||
                !Count(f.FilesTotal) || !Count(f.FilesCopied) || !Count(f.FilesUnchanged) || !Count(f.FilesFailed) || !Count(f.BytesCopied))
                return false;
        if (e.Folders.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != e.Folders.Count) return false;
        if (report.Status == BackupRunStatus.Successful && !IsComplete(e)) return false;
        if (report.Status == BackupRunStatus.Warning && !IsComplete(e)) return false;
        if (report.FilesCopied != e.Folders.Sum(f => f.FilesCopied ?? 0) ||
            report.FilesSkipped != e.Folders.Sum(f => f.FilesUnchanged ?? 0) ||
            report.BytesCopied != e.Folders.Sum(f => f.BytesCopied ?? 0)) return false;
        if (report.Status is BackupRunStatus.Successful or BackupRunStatus.Warning &&
            (report.MissingFolders.Count != 0 || report.FailedFolders.Count != 0)) return false;
        return true;
    }

    private static bool Count(long? value) => value is null or >= 0 and <= 1_000_000_000_000_000;

    private static bool Text(string? value, int length) => value is null ||
        (value.Length <= length && !value.Any(char.IsControl));
    private static bool List(IReadOnlyList<string>? items, int count, int length) =>
        items is not null && items.Count <= count && items.All(s => s is not null && Text(s, length));
}
