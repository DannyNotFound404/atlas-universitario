using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.Updater;

internal static class UpdaterStatusWriter
{
    private const string StatusFileName = "updater-status.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void TryWrite(
        UpdaterPaths paths,
        DateTimeOffset startedAtUtc,
        string operation,
        int exitCode,
        Guid? releaseId)
    {
        try
        {
            if (exitCode == 12) return;
            paths.EnsureDirectories();
            var journal = DurableFile.Read<UpdateJournal>(paths.JournalFile);
            string updaterVersion = ReadUpdaterVersion();
            UpdaterExecutionStatus status = new(
                SchemaVersion: 1,
                UpdaterVersion: updaterVersion,
                Operation: operation,
                Result: exitCode switch
                {
                    0 => "success",
                    10 => "deferred",
                    11 => "handoff",
                    12 => "busy",
                    20 => "rolled_back",
                    21 => "recovery_failed",
                    _ => "failed"
                },
                ExitCode: exitCode,
                ReleaseId: releaseId ?? journal?.ReleaseId,
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Phase: journal?.Phase ?? "CHECKED");
            string destination = Path.Combine(
                paths.StatusDirectory,
                StatusFileName);
            string temporary = destination + ".next";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(status, JsonOptions),
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                JsonException or
                Win32Exception)
        {
            // La bandera es evidencia auxiliar y nunca debe convertir una
            // actualización correcta en un fallo del componente.
        }
    }

    private static string ReadUpdaterVersion()
    {
        string? processPath = Environment.ProcessPath;
        string? version = string.IsNullOrWhiteSpace(processPath)
            ? null
            : FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
        return string.IsNullOrWhiteSpace(version)
            ? "unknown"
            : version.Trim();
    }

    private sealed record UpdaterExecutionStatus(
        int SchemaVersion,
        string UpdaterVersion,
        string Operation,
        string Result,
        int ExitCode,
        Guid? ReleaseId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        string Phase);
}
