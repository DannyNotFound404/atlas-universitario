using System.Text.Json;

namespace Miguel.Agent.Security;

public sealed class SecurityEventCursorStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyDictionary<string, long>> LoadAsync(
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        if (!File.Exists(paths.SecurityCursorFile))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        await using FileStream stream = new(
            paths.SecurityCursorFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        Dictionary<string, long>? cursors =
            await JsonSerializer.DeserializeAsync<Dictionary<string, long>>(
                stream,
                JsonOptions,
                cancellationToken);
        return cursors is null
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(cursors, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, long> cursors,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        string temporary = paths.SecurityCursorFile + ".tmp";
        await using (FileStream stream = new(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                cursors,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, paths.SecurityCursorFile, true);
    }
}
