using System.Reflection;
using Npgsql;

namespace Miguel.Server.Storage;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
    private const string MigrationPrefix = "Database.Migrations.";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(DatabaseInitializer).Assembly;
        string[] resourceNames = assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(MigrationPrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException("No se encontraron migraciones incrustadas.");
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        await using (NpgsqlCommand lockCommand = new(
                         "SELECT pg_advisory_lock(hashtext('miguel-schema-migrations'));",
                         connection))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await EnsureMigrationTableAsync(connection, cancellationToken);

            foreach (string resourceName in resourceNames)
            {
                string version = resourceName[
                    (resourceName.IndexOf(MigrationPrefix, StringComparison.Ordinal) +
                     MigrationPrefix.Length)..];

                if (await IsMigrationAppliedAsync(connection, version, cancellationToken))
                {
                    continue;
                }

                await ApplyMigrationAsync(
                    assembly,
                    connection,
                    resourceName,
                    version,
                    cancellationToken);
            }
        }
        finally
        {
            await using NpgsqlCommand unlockCommand = new(
                "SELECT pg_advisory_unlock(hashtext('miguel-schema-migrations'));",
                connection);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Esquema PostgreSQL de MIGUEL verificado con {MigrationCount} migraciones.",
            resourceNames.Length);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT 1;");
            return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostgreSQL no está disponible.");
            return false;
        }
    }

    private static async Task EnsureMigrationTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_migrations
            (
                version        VARCHAR(255) PRIMARY KEY,
                applied_at_utc TIMESTAMPTZ NOT NULL
            );
            """;
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        NpgsqlConnection connection,
        string version,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $1);";
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue(version);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task ApplyMigrationAsync(
        Assembly assembly,
        NpgsqlConnection connection,
        string resourceName,
        string version,
        CancellationToken cancellationToken)
    {
        await using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"No se encontró la migración incrustada {resourceName}.");
        using StreamReader reader = new(stream);
        string migration = await reader.ReadToEndAsync(cancellationToken);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (NpgsqlCommand migrationCommand = new(
                         migration,
                         connection,
                         transaction))
        {
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand recordCommand = new(
                         """
                         INSERT INTO schema_migrations (version, applied_at_utc)
                         VALUES ($1, $2);
                         """,
                         connection,
                         transaction))
        {
            recordCommand.Parameters.AddWithValue(version);
            recordCommand.Parameters.AddWithValue(DateTimeOffset.UtcNow);
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
