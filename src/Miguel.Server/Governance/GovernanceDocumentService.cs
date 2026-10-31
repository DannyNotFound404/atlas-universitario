using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Miguel.Server.Governance;

public sealed class GovernanceDocumentException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record GovernanceStoredFile(
    string PhysicalPath,
    string OriginalFileName,
    string MediaType);

public sealed record GovernanceCorrectionStorageOutcome(
    GovernanceCorrectionOutcome Database,
    bool FilesPurged,
    IReadOnlyList<string> PurgeFailures);

public sealed class GovernanceDocumentService(
    IOptions<ServerOptions> options,
    GovernanceRepository repository)
{
    public async Task<AdminGovernanceDocumentDetail?> UploadVersionAsync(
        Guid documentId,
        Stream source,
        string fileName,
        string? mediaType,
        string? versionLabel,
        string? changeSummary,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long maximum = options.Value.MaxGovernanceDocumentUploadBytes;
        byte[] content = await ReadBoundedAsync(
            source, maximum, cancellationToken);
        string extension = Path.GetExtension(
            Path.GetFileName((fileName ?? string.Empty).Trim()))
            .ToLowerInvariant();
        if (!HasExpectedSignature(extension, content))
        {
            throw new GovernanceDocumentException(
                "invalid_governance_document_content",
                "El contenido del archivo no coincide con su extensión.");
        }
        string sha256 = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        if (!GovernanceInput.TryNormalizeVersion(
                fileName,
                MediaTypeForExtension(extension),
                versionLabel,
                changeSummary,
                content,
                sha256,
                out GovernanceVersionUpload upload))
        {
            throw new GovernanceDocumentException(
                "invalid_governance_document_version",
                "El archivo, la versión o el resumen de cambios no son válidos.");
        }

        Guid versionId = Guid.NewGuid();
        string relativePath = Path.Combine(
            documentId.ToString("D"),
            $"{versionId:D}{upload.Extension}");
        string root = ResolveStorageRoot(options.Value.GovernanceDocumentPath);
        string directory = Path.Combine(root, documentId.ToString("D"));
        Directory.CreateDirectory(directory);
        string finalPath = ResolveSafePath(root, relativePath);
        string temporaryPath = finalPath + $".incoming-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath, upload.Content, cancellationToken);
            File.Move(temporaryPath, finalPath);
            bool added = await repository.AddVersionAsync(
                documentId,
                versionId,
                upload,
                relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                actor,
                now,
                cancellationToken);
            if (!added)
            {
                File.Delete(finalPath);
                return null;
            }
            return await repository.ReadDocumentAsync(documentId, cancellationToken);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public async Task<GovernanceStoredFile?> ResolveVersionFileAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        GovernanceDocumentFile? file =
            await repository.ReadVersionFileAsync(versionId, cancellationToken);
        if (file is null)
        {
            return null;
        }
        string root = ResolveStorageRoot(options.Value.GovernanceDocumentPath);
        string physicalPath = ResolveSafePath(
            root,
            file.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(physicalPath)
            ? new GovernanceStoredFile(
                physicalPath, file.OriginalFileName, file.MediaType)
            : null;
    }

    public async Task<GovernanceCorrectionStorageOutcome> RequestCorrectionAsync(
        Guid documentId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        GovernanceCorrectionOutcome outcome =
            await repository.RequestCorrectionAsync(
                documentId, actor, now, cancellationToken);
        if (outcome.Result != GovernanceCorrectionResult.Corrected)
        {
            return new(outcome, true, []);
        }

        string root = ResolveStorageRoot(options.Value.GovernanceDocumentPath);
        List<string> failures = [];
        foreach (GovernanceRejectedVersion version in outcome.RejectedVersions)
        {
            string path = ResolveSafePath(
                root,
                version.StoredRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                failures.Add(version.StoredRelativePath);
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(version.StoredRelativePath);
            }
        }

        string documentDirectory = ResolveSafePath(
            root, documentId.ToString("D"));
        try
        {
            if (Directory.Exists(documentDirectory) &&
                !Directory.EnumerateFileSystemEntries(documentDirectory).Any())
            {
                Directory.Delete(documentDirectory);
            }
        }
        catch (IOException)
        {
            // El directorio vacío puede limpiarse en el siguiente mantenimiento.
        }
        catch (UnauthorizedAccessException)
        {
            // El directorio vacío puede limpiarse en el siguiente mantenimiento.
        }

        return new(outcome, failures.Count == 0, failures);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maximum,
        CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        byte[] buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > maximum)
            {
                throw new GovernanceDocumentException(
                    "governance_document_too_large",
                    $"El documento supera el límite de {maximum} bytes.");
            }
            destination.Write(buffer, 0, read);
        }
        if (total == 0)
        {
            throw new GovernanceDocumentException(
                "empty_governance_document",
                "El documento está vacío.");
        }
        return destination.ToArray();
    }

    private static string ResolveStorageRoot(string? configured)
    {
        string value = string.IsNullOrWhiteSpace(configured)
            ? "/var/lib/miguel/governance-documents"
            : configured.Trim();
        string fullPath = Path.GetFullPath(value);
        if (!Path.IsPathRooted(fullPath) ||
            fullPath == Path.GetPathRoot(fullPath))
        {
            throw new InvalidOperationException(
                "Miguel:GovernanceDocumentPath debe ser una ruta absoluta y específica.");
        }
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static bool HasExpectedSignature(string extension, byte[] content)
    {
        if (extension == ".pdf")
        {
            return content.Length >= 5 &&
                   content[0] == (byte)'%' && content[1] == (byte)'P' &&
                   content[2] == (byte)'D' && content[3] == (byte)'F' &&
                   content[4] == (byte)'-';
        }
        if (extension is ".docx" or ".xlsx" or ".pptx" or
            ".odt" or ".ods" or ".odp")
        {
            return content.Length >= 4 &&
                   content[0] == (byte)'P' && content[1] == (byte)'K' &&
                   content[2] is 3 or 5 or 7 &&
                   content[3] is 4 or 6 or 8;
        }
        if (extension is ".txt" or ".csv")
        {
            return !content.Contains((byte)0);
        }
        return false;
    }

    private static string MediaTypeForExtension(string extension) =>
        extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".csv" => "text/csv; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };

    private static string ResolveSafePath(string root, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string boundary = root.TrimEnd(Path.DirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(boundary, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "La ruta del documento quedó fuera del almacenamiento configurado.");
        }
        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo para un archivo todavía no registrado.
        }
        catch (UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo para un archivo todavía no registrado.
        }
    }
}
