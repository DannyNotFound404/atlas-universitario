using Miguel.Server.Admin;

namespace Miguel.Server.Nas;

public sealed class NasExplorerService(
    InfrastructureRepository repository,
    NasPathPolicy pathPolicy)
{
    public async Task<AdminNasBrowsePage?> BrowseAsync(
        Guid nasId,
        string? relativePath,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        AdminNasDefinition? appliance =
            await repository.FindNasAsync(nasId, cancellationToken);
        if (appliance is null || !appliance.Enabled)
        {
            return null;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);
        string directory = pathPolicy.ResolveDirectory(
            appliance,
            relativePath,
            out string normalizedPath);
        DirectoryInfo root = new(pathPolicy.GetMountPath(appliance));
        DirectoryInfo target = new(directory);
        if (!root.Exists || !target.Exists)
        {
            throw new DirectoryNotFoundException(
                "El recurso de respaldo no esta montado o la ruta no existe.");
        }
        if (!NasMountInspector.IsSupportedReadOnlyMount(root.FullName))
        {
            throw new DirectoryNotFoundException(
                "La ruta no corresponde a un montaje CIFS o NFS de solo lectura.");
        }
        EnsureNotReparsePoint(root);
        EnsurePathDoesNotContainReparsePoints(root, normalizedPath);

        FileSystemInfo[] entries = target
            .EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Where(entry => !entry.Name.StartsWith(".~", StringComparison.Ordinal) &&
                            !entry.Name.StartsWith("~$", StringComparison.Ordinal))
            .OrderByDescending(entry =>
                (entry.Attributes & FileAttributes.Directory) != 0)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int totalPages = Math.Max(
            1,
            (int)Math.Ceiling(entries.Length / (double)pageSize));
        page = Math.Min(page, totalPages);

        AdminNasBrowseItem[] items = entries
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(entry => ToView(entry, normalizedPath))
            .ToArray();
        return new AdminNasBrowsePage(
            appliance.Id,
            appliance.DisplayName,
            normalizedPath,
            NasPathPolicy.ParentOf(normalizedPath),
            page,
            pageSize,
            entries.Length,
            items);
    }

    private static AdminNasBrowseItem ToView(
        FileSystemInfo entry,
        string parent)
    {
        bool isDirectory =
            (entry.Attributes & FileAttributes.Directory) != 0;
        string relative = parent.Length == 0
            ? entry.Name
            : $"{parent}/{entry.Name}";
        long? size = !isDirectory && entry is FileInfo file
            ? file.Length
            : null;
        return new AdminNasBrowseItem(
            entry.Name,
            relative,
            isDirectory,
            size,
            new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static void EnsureNotReparsePoint(FileSystemInfo information)
    {
        if ((information.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "ATLAS no navega enlaces o puntos de redireccion.");
        }
    }

    private static void EnsurePathDoesNotContainReparsePoints(
        DirectoryInfo root,
        string normalizedRelativePath)
    {
        DirectoryInfo current = root;
        foreach (string segment in normalizedRelativePath.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            EnsureNotReparsePoint(current);
        }
    }
}
