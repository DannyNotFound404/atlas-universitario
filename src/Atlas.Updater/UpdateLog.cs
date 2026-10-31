using System.Diagnostics;

namespace Atlas.Updater;

internal sealed class UpdateLog(UpdaterPaths paths)
{
    private readonly object sync = new();

    public void Write(string message)
    {
        string line =
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        try
        {
            paths.EnsureDirectories();
            lock (sync)
            {
                File.AppendAllText(
                    Path.Combine(
                        paths.LogDirectory,
                        $"updater-{DateTime.UtcNow:yyyyMMdd}.log"),
                    line);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // El registro es evidencia auxiliar. Un permiso alterado no debe
            // provocar una segunda excepción que oculte el resultado real.
            Debug.WriteLine(line.TrimEnd());
        }
    }
}
