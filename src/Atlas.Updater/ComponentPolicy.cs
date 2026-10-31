using System.Text.Json;

namespace Atlas.Updater;

internal static class ComponentPolicy
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static bool IsBackupNasEnabled(string policyPath)
    {
        if (!File.Exists(policyPath))
        {
            // Compatibilidad con instalaciones anteriores, donde BackupNAS
            // siempre formaba parte del endpoint.
            return true;
        }

        try
        {
            // components.json también contiene metadatos informativos como
            // backupProvider y updatedAtUtc. La decisión de instalación sólo
            // depende del esquema y de backupNasEnabled; el archivo permanece
            // protegido para SYSTEM y Administradores por el instalador.
            ComponentPolicyDocument? policy = JsonSerializer.Deserialize<
                ComponentPolicyDocument>(File.ReadAllText(policyPath), JsonOptions);
            return policy is { SchemaVersion: 1, BackupNasEnabled: true };
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            // Una política ilegible nunca debe provocar la instalación
            // inesperada de BackupNAS en un equipo destinado a Acronis.
            return false;
        }
    }

    public static bool RepairFromInstalledBackupNas(
        string policyPath,
        string backupExecutable)
    {
        if (IsBackupNasEnabled(policyPath) ||
            !File.Exists(backupExecutable))
        {
            return IsBackupNasEnabled(policyPath);
        }

        string? directory = Path.GetDirectoryName(policyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        string temporary = policyPath + ".next";
        string json = JsonSerializer.Serialize(
            new ComponentPolicyDocument(
                SchemaVersion: 1,
                BackupNasEnabled: true,
                BackupProvider: "ATLAS / BackupNAS",
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            JsonOptions);
        File.WriteAllText(temporary, json);
        File.Move(temporary, policyPath, overwrite: true);
        return true;
    }

    private sealed record ComponentPolicyDocument(
        int SchemaVersion,
        bool BackupNasEnabled,
        string? BackupProvider = null,
        DateTimeOffset? UpdatedAtUtc = null);
}
