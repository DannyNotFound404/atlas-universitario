using Microsoft.Extensions.Options;

namespace Miguel.Server.Training;

public sealed class TrainingAttendanceImportService(
    IOptions<ServerOptions> options,
    TrainingRepository repository)
{
    private readonly TimeZoneInfo operationalTimeZone = ResolveTimeZone(
        options.Value.OperationalTimeZoneId);

    public async Task<AdminTrainingSessionDetail?> ImportAsync(
        Guid sessionId,
        Stream source,
        string fileName,
        string? mediaType,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TrainingAttendanceImportDocument document =
            await TrainingAttendanceImportParser.ParseAsync(
                source,
                fileName,
                mediaType,
                options.Value.MaxTrainingAttendanceUploadBytes,
                operationalTimeZone,
                cancellationToken);

        Guid importId = Guid.NewGuid();
        string relativePath = Path.Combine(
            sessionId.ToString("D"),
            $"{importId:D}{document.Extension}");
        string root = ResolveStorageRoot(options.Value.TrainingEvidencePath);
        string sessionDirectory = Path.Combine(root, sessionId.ToString("D"));
        Directory.CreateDirectory(sessionDirectory);
        string finalPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootBoundary = root.TrimEnd(Path.DirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
        if (!finalPath.StartsWith(rootBoundary, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "La ruta de evidencia quedó fuera del almacenamiento configurado.");
        }

        string temporaryPath = finalPath + $".incoming-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath, document.Content, cancellationToken);
            File.Move(temporaryPath, finalPath);
            AdminTrainingSessionDetail? result =
                await repository.ReplaceAttendanceFromImportAsync(
                    sessionId,
                    importId,
                    document,
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    actor,
                    now,
                    cancellationToken);
            if (result is null)
            {
                File.Delete(finalPath);
            }
            return result;
        }
        catch
        {
            TryDelete(temporaryPath);
            // Conservar el archivo final es más seguro que eliminar evidencia
            // que PostgreSQL pudo confirmar justo antes de un fallo de lectura.
            // Los archivos no referenciados pueden depurarse posteriormente.
            throw;
        }
    }

    private static string ResolveStorageRoot(string? configured)
    {
        string value = string.IsNullOrWhiteSpace(configured)
            ? "/var/lib/miguel/training-evidence"
            : configured.Trim();
        string fullPath = Path.GetFullPath(value);
        if (!Path.IsPathRooted(fullPath) ||
            fullPath == Path.GetPathRoot(fullPath))
        {
            throw new InvalidOperationException(
                "Miguel:TrainingEvidencePath debe ser una ruta absoluta y específica.");
        }
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup; the file is not referenced by PostgreSQL.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the file is not referenced by PostgreSQL.
        }
    }
}
