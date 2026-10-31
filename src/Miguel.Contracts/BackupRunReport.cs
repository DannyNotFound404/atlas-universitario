using System;
using System.Collections.Generic;

namespace Miguel.Contracts;

public sealed record BackupRunReport(
    int SchemaVersion,
    Guid EventId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    BackupRunStatus Status,
    long FilesCopied,
    long FilesSkipped,
    long BytesCopied,
    IReadOnlyList<string> MissingFolders,
    IReadOnlyList<string> FailedFolders,
    string BackupClientVersion,
    string? LogFileName,
    string? DestinationHost = null,
    string? DestinationShare = null,
    string? DestinationBasePath = null,
    string SnapshotMode = "live",
    string? SnapshotWarning = null,
    BackupExecutionEvidence? Evidence = null);

public enum BackupRunStatus
{
    Successful = 1, Warning = 2, Failed = 3, Cancelled = 4,
    Skipped = 5, Unconfirmed = 6, Running = 7, Partial = 8
}
