using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Distribution;

public sealed class AtlasServerReleaseCatalogService(
    NpgsqlDataSource dataSource,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<AtlasServerReleaseCatalogService> logger)
{
    private const string CatalogFileName = "server-release-catalog.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public string CurrentReleaseVersion { get; private set; } = "unknown";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ServerReleaseCatalogDocument catalog = await ReadCatalogAsync(
            cancellationToken);
        ValidateCatalog(catalog);
        CurrentReleaseVersion = catalog.CurrentReleaseVersion;

        string assemblyPath = typeof(AtlasServerReleaseCatalogService)
            .Assembly.Location;
        string applicationSha256;
        await using (FileStream assembly = File.OpenRead(assemblyPath))
        {
            applicationSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(assembly, cancellationToken));
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        int schemaMigrationCount;
        await using (NpgsqlCommand countCommand = new(
                         "SELECT count(*)::int FROM schema_migrations;",
                         connection))
        {
            schemaMigrationCount =
                (int)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (ServerReleaseCatalogEntry release in catalog.Releases)
        {
            await UpsertDocumentedReleaseAsync(
                connection,
                transaction,
                release,
                now,
                cancellationToken);
        }

        await using (NpgsqlCommand clearActive = new(
                         "UPDATE atlas_server_releases SET is_active = FALSE WHERE is_active;",
                         connection,
                         transaction))
        {
            await clearActive.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand activate = new(
                         """
                         UPDATE atlas_server_releases
                         SET is_active = TRUE,
                             observed_on_server = TRUE,
                             first_activated_at_utc = COALESCE(first_activated_at_utc, $2),
                             last_activated_at_utc = $2,
                             application_sha256 = $3,
                             schema_migration_count = $4,
                             evidence_source = 'RUNTIME_AND_DOCUMENTATION'
                         WHERE release_version = $1;
                         """,
                         connection,
                         transaction))
        {
            activate.Parameters.AddWithValue(catalog.CurrentReleaseVersion);
            activate.Parameters.AddWithValue(now);
            activate.Parameters.AddWithValue(applicationSha256);
            activate.Parameters.AddWithValue(schemaMigrationCount);
            int affected = await activate.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "La versión activa no existe en el catálogo del servidor.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Catálogo de versiones ATLAS verificado: {ReleaseCount} versiones; " +
            "activa {ActiveVersion}; {MigrationCount} migraciones.",
            catalog.Releases.Count,
            catalog.CurrentReleaseVersion,
            schemaMigrationCount);
    }

    public async Task<AtlasServerReleaseCatalogView> ReadAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT release_version,
                   released_at_utc,
                   previous_release_version,
                   summary,
                   features::text,
                   changes_from_previous::text,
                   application_restore_outcome,
                   full_restore_outcome,
                   is_active,
                   observed_on_server,
                   first_activated_at_utc,
                   last_activated_at_utc,
                   application_sha256,
                   schema_migration_count,
                   evidence_source
            FROM atlas_server_releases
            ORDER BY released_at_utc DESC, release_version DESC;
            """;
        List<AtlasServerReleaseView> releases = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            releases.Add(new AtlasServerReleaseView(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DeserializeItems(reader.GetString(4)),
                DeserializeItems(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12).Trim(),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.GetString(14)));
        }

        string active = releases.FirstOrDefault(release => release.IsActive)
            ?.ReleaseVersion ?? CurrentReleaseVersion;
        return new AtlasServerReleaseCatalogView(active, releases);
    }

    private async Task<ServerReleaseCatalogDocument> ReadCatalogAsync(
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(environment.ContentRootPath, CatalogFileName);
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ServerReleaseCatalogDocument>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException(
                   "No fue posible leer el catálogo de versiones del servidor.");
    }

    private static async Task UpsertDocumentedReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServerReleaseCatalogEntry release,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO atlas_server_releases
            (
                release_version,
                released_at_utc,
                previous_release_version,
                summary,
                features,
                changes_from_previous,
                application_restore_outcome,
                full_restore_outcome,
                evidence_source,
                catalogued_at_utc
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'DOCUMENTATION', $9)
            ON CONFLICT (release_version) DO UPDATE
            SET released_at_utc = EXCLUDED.released_at_utc,
                previous_release_version = EXCLUDED.previous_release_version,
                summary = EXCLUDED.summary,
                features = EXCLUDED.features,
                changes_from_previous = EXCLUDED.changes_from_previous,
                application_restore_outcome = EXCLUDED.application_restore_outcome,
                full_restore_outcome = EXCLUDED.full_restore_outcome;
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue(release.ReleaseVersion);
        // Catalogs may contain explicit local offsets. Preserve the instant and
        // normalize at the PostgreSQL boundary, which requires offset zero.
        command.Parameters.AddWithValue(release.ReleasedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue(
            (object?)release.PreviousReleaseVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(release.Summary);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(release.Features, JsonOptions));
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(release.ChangesFromPrevious, JsonOptions));
        command.Parameters.AddWithValue(release.ApplicationRestoreOutcome);
        command.Parameters.AddWithValue(release.FullRestoreOutcome);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateCatalog(ServerReleaseCatalogDocument catalog)
    {
        if (catalog.SchemaVersion != 1 ||
            !IsVersion(catalog.CurrentReleaseVersion) ||
            catalog.Releases.Count is < 1 or > 100)
        {
            throw new InvalidDataException(
                "El catálogo de versiones del servidor no es válido.");
        }

        HashSet<string> versions = new(StringComparer.OrdinalIgnoreCase);
        foreach (ServerReleaseCatalogEntry release in catalog.Releases)
        {
            if (!IsVersion(release.ReleaseVersion) ||
                !versions.Add(release.ReleaseVersion) ||
                (release.PreviousReleaseVersion is not null &&
                 !IsVersion(release.PreviousReleaseVersion)) ||
                release.ReleasedAtUtc == default ||
                string.IsNullOrWhiteSpace(release.Summary) ||
                release.Summary.Length > 500 ||
                !IsItemList(release.Features) ||
                !IsItemList(release.ChangesFromPrevious) ||
                string.IsNullOrWhiteSpace(release.ApplicationRestoreOutcome) ||
                release.ApplicationRestoreOutcome.Length > 1_200 ||
                string.IsNullOrWhiteSpace(release.FullRestoreOutcome) ||
                release.FullRestoreOutcome.Length > 1_200)
            {
                throw new InvalidDataException(
                    $"La entrada {release.ReleaseVersion} del catálogo no es válida.");
            }
        }

        if (!versions.Contains(catalog.CurrentReleaseVersion))
        {
            throw new InvalidDataException(
                "La versión activa declarada no existe en el catálogo.");
        }
    }

    private static bool IsVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_');

    private static bool IsItemList(IReadOnlyList<string>? values) =>
        values is { Count: >= 1 and <= 20 } &&
        values.All(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 300 &&
            !value.Any(char.IsControl));

    private static IReadOnlyList<string> DeserializeItems(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private sealed record ServerReleaseCatalogDocument(
        int SchemaVersion,
        string CurrentReleaseVersion,
        IReadOnlyList<ServerReleaseCatalogEntry> Releases);

    private sealed record ServerReleaseCatalogEntry(
        string ReleaseVersion,
        DateTimeOffset ReleasedAtUtc,
        string? PreviousReleaseVersion,
        string Summary,
        IReadOnlyList<string> Features,
        IReadOnlyList<string> ChangesFromPrevious,
        string ApplicationRestoreOutcome,
        string FullRestoreOutcome);
}
