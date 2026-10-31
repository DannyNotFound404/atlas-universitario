using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Miguel.Agent;

public sealed class BackupClientVersionProvider(
    IOptions<AgentOptions> optionsAccessor)
{
    private readonly AgentOptions options = optionsAccessor.Value;

    public string? GetCurrentVersion()
    {
        try
        {
            string executablePath = ResolveExecutablePath();
            if (!File.Exists(executablePath))
            {
                return null;
            }

            string? installedVersion = Normalize(
                FileVersionInfo.GetVersionInfo(executablePath)
                    .ProductVersion);
            if (installedVersion is not null)
            {
                return installedVersion;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                Win32Exception)
        {
            // La versión es telemetría auxiliar. Si Windows no puede leer
            // el recurso del ejecutable, el heartbeat debe continuar usando
            // el valor de compatibilidad configurado.
        }

        return Normalize(options.BackupClientVersion);
    }

    private string ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(
                options.BackupClientExecutablePath))
        {
            return Path.GetFullPath(
                options.BackupClientExecutablePath.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles),
            "ATLAS",
            "BackupNAS",
            "BackupNAS.exe");
    }

    private static string? Normalize(string? version)
    {
        string? normalized = version?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized.Length > 64
            ? null
            : normalized;
    }
}
