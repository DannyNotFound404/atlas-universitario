using Microsoft.Extensions.Options;
using Miguel.Server.Admin;

namespace Miguel.Server.Nas;

public sealed class NasPathPolicy
{
    private readonly string mountRoot;

    public NasPathPolicy(IOptions<ServerOptions> optionsAccessor)
    {
        string configured = optionsAccessor.Value.NasMountRoot?.Trim() ?? "";
        if (configured.Length == 0)
        {
            configured = Path.Combine(
                AppContext.BaseDirectory,
                "NasMounts");
        }
        mountRoot = Path.GetFullPath(configured)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string MountRoot => mountRoot;

    public string GetMountPath(AdminNasDefinition appliance) =>
        GetMountPath(appliance.Code);

    public string GetMountPath(string nasCode)
    {
        string code = InfrastructureInput.NormalizeCode(nasCode);
        if (code.Length == 0 ||
            code.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new InvalidDataException("El identificador del NAS no es seguro.");
        }
        return EnsureWithinRoot(Path.Combine(mountRoot, code), mountRoot);
    }

    public string ResolveDirectory(
        AdminNasDefinition appliance,
        string? relativePath,
        out string normalizedRelativePath)
    {
        normalizedRelativePath = NormalizeRelativePath(relativePath);
        string applianceRoot = GetMountPath(appliance);
        string candidate = normalizedRelativePath.Length == 0
            ? applianceRoot
            : Path.Combine(
                applianceRoot,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return EnsureWithinRoot(candidate, applianceRoot);
    }

    public static string NormalizeRelativePath(string? relativePath)
    {
        string value = relativePath?.Trim().Replace('\\', '/') ?? "";
        if (value.Length == 0)
        {
            return "";
        }
        if (value.Length > 1024 || value.StartsWith('/') || value.Contains('\0'))
        {
            throw new InvalidDataException("La ruta solicitada no es valida.");
        }

        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 64 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                segment.Length is < 1 or > 255 ||
                segment.Any(char.IsControl)))
        {
            throw new InvalidDataException("La ruta solicitada no es valida.");
        }
        return string.Join('/', segments);
    }

    public static string? ParentOf(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0)
        {
            return null;
        }
        int separator = normalized.LastIndexOf('/');
        return separator < 0 ? "" : normalized[..separator];
    }

    private static string EnsureWithinRoot(string path, string root)
    {
        string resolvedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resolvedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string boundary = resolvedRoot + Path.DirectorySeparatorChar;
        if (!resolvedPath.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase) &&
            !resolvedPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("La ruta excede la raiz autorizada.");
        }
        return resolvedPath;
    }
}
