using System.Text.Json;
using Miguel.Contracts;

namespace Miguel.Agent.Identity;

public sealed class AgentIdentityStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<AgentIdentity> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.IdentityFile))
        {
            throw new InvalidOperationException(
                $"El agente no está inscrito. No existe {paths.IdentityFile}.");
        }

        await using FileStream stream = File.OpenRead(paths.IdentityFile);
        AgentIdentity? identity = await JsonSerializer.DeserializeAsync<AgentIdentity>(
            stream,
            JsonOptions,
            cancellationToken);

        if (identity is null ||
            identity.SchemaVersion != ApiContract.SchemaVersion ||
            identity.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.DeviceKey))
        {
            throw new InvalidOperationException("La identidad local del agente es inválida.");
        }

        return identity;
    }

    public async Task SaveAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        string temporaryFile = paths.IdentityFile + ".tmp";

        await using (FileStream stream = new(
            temporaryFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, identity, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryFile, paths.IdentityFile, overwrite: true);
    }
}
