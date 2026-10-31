using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json.Serialization;

namespace Miguel.Server.BackupControl;

public sealed class BackupEvidenceException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BackupEvidenceUploadResult(
    Guid Id,
    string OriginalFileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    [property: JsonIgnore] string? PhysicalPath = null);

public sealed record BackupStoredEvidence(
    string PhysicalPath,
    string OriginalFileName,
    string MediaType);

public sealed class BackupEvidenceService(
    IOptions<ServerOptions> options,
    BackupControlRepository repository)
{
    public async Task<BackupEvidenceUploadResult?> UploadAsync(
        Guid profileId,
        BackupEvidenceUploadMetadata metadata,
        Stream source,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        if (!BackupControlInput.TryNormalizeEvidence(metadata, out metadata))
        {
            throw new BackupEvidenceException(
                "invalid_backup_evidence_metadata",
                "Los datos de la evidencia no son válidos.");
        }

        long maximum = options.Value.MaxBackupEvidenceUploadBytes;
        byte[] content = await ReadBoundedAsync(source, maximum, cancellationToken);
        string extension = Path.GetExtension(metadata.FileName).ToLowerInvariant();
        if (!HasExpectedSignature(extension, content))
        {
            throw new BackupEvidenceException(
                "invalid_backup_evidence_content",
                "El contenido no coincide con un tipo de evidencia permitido.");
        }

        string mediaType = MediaTypeForExtension(extension);
        string sha256 = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        Guid evidenceId = Guid.NewGuid();
        string relativePath = Path.Combine(
            profileId.ToString("D"),
            $"{evidenceId:D}{extension}");
        string root = ResolveStorageRoot(options.Value.BackupEvidencePath);
        string directory = ResolveSafePath(root, profileId.ToString("D"));
        Directory.CreateDirectory(directory);
        string finalPath = ResolveSafePath(root, relativePath);
        string temporaryPath = finalPath + $".incoming-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, finalPath);
            bool stored = await repository.AddEvidenceAsync(
                evidenceId,
                profileId,
                metadata,
                mediaType,
                relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                content.LongLength,
                sha256,
                actor,
                now,
                cancellationToken, transaction);
            if (!stored)
            {
                File.Delete(finalPath);
                return null;
            }
            return new(
                evidenceId,
                metadata.FileName,
                mediaType,
                content.LongLength,
                sha256, finalPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(finalPath);
            throw;
        }
    }

    public async Task<BackupStoredEvidence?> ResolveAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        BackupEvidenceFileRecord? file =
            await repository.ReadEvidenceFileAsync(evidenceId, cancellationToken);
        if (file is null)
        {
            return null;
        }
        string root = ResolveStorageRoot(options.Value.BackupEvidencePath);
        string physicalPath = ResolveSafePath(
            root,
            file.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(physicalPath)
            ? new(physicalPath, file.OriginalFileName, file.MediaType)
            : null;
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
                throw new BackupEvidenceException(
                    "backup_evidence_too_large",
                    $"La evidencia supera el límite de {maximum} bytes.");
            }
            destination.Write(buffer, 0, read);
        }
        if (total == 0)
        {
            throw new BackupEvidenceException(
                "empty_backup_evidence",
                "La evidencia está vacía.");
        }
        return destination.ToArray();
    }

    private static bool HasExpectedSignature(string extension, byte[] content)
    {
        if (extension == ".pdf")
        {
            return StartsWith(content, "%PDF-"u8);
        }
        if (extension is ".docx" or ".xlsx")
        {
            return HasExpectedOfficePackage(extension, content);
        }
        if (extension == ".png")
        {
            return content.Length >= 8 &&
                   content.AsSpan(0, 8).SequenceEqual(
                       new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        }
        if (extension is ".jpg" or ".jpeg")
        {
            return content.Length >= 3 && content[0] == 0xff &&
                   content[1] == 0xd8 && content[2] == 0xff;
        }
        if (extension is ".txt" or ".csv" or ".log")
        {
            return !content.Contains((byte)0);
        }
        return false;
    }

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> signature) =>
        content.AsSpan().StartsWith(signature);

    private static bool HasExpectedOfficePackage(string extension, byte[] content)
    {
        try
        {
            using MemoryStream source = new(content, writable: false);
            using ZipArchive archive = new(source, ZipArchiveMode.Read);
            bool hasContentTypes = archive.GetEntry("[Content_Types].xml") is not null;
            bool hasPrimaryDocument = extension == ".docx"
                ? archive.GetEntry("word/document.xml") is not null
                : archive.GetEntry("xl/workbook.xml") is not null;
            return hasContentTypes && hasPrimaryDocument;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string MediaTypeForExtension(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".csv" => "text/csv; charset=utf-8",
        ".txt" or ".log" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static string ResolveStorageRoot(string? configured)
    {
        string value = string.IsNullOrWhiteSpace(configured)
            ? "/var/lib/miguel/backup-evidence"
            : configured.Trim();
        string fullPath = Path.GetFullPath(value);
        if (!Path.IsPathRooted(fullPath) ||
            fullPath == Path.GetPathRoot(fullPath))
        {
            throw new InvalidOperationException(
                "Miguel:BackupEvidencePath debe ser una ruta absoluta y específica.");
        }
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string boundary = root.TrimEnd(Path.DirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(boundary, comparison))
        {
            throw new InvalidOperationException(
                "La evidencia quedó fuera del almacenamiento configurado.");
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
