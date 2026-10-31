using System.Net.Http.Json;
using System.Security.Cryptography;
using Miguel.Contracts;

namespace Miguel.Agent.Identity;

public static class AgentEnrollment
{
    public static async Task<int> RunInteractiveAsync(
        string[] args,
        AgentIdentityStore identityStore,
        AgentOptions options,
        BackupClientVersionProvider backupClientVersionProvider,
        CancellationToken cancellationToken)
    {
        string? serverInput = args.Length >= 2 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(serverInput))
        {
            Console.Write("URL del servidor MIGUEL: ");
            serverInput = Console.ReadLine();
        }

        if (!Uri.TryCreate(serverInput, UriKind.Absolute, out Uri? serverUrl) ||
            (serverUrl.Scheme != Uri.UriSchemeHttps &&
             !(serverUrl.Scheme == Uri.UriSchemeHttp && serverUrl.IsLoopback)))
        {
            Console.Error.WriteLine("La URL debe usar HTTPS. HTTP solo se permite para localhost.");
            return 2;
        }

        Console.Write("Token de inscripción: ");
        string enrollmentToken = ReadSecret();
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            Console.Error.WriteLine("El token es obligatorio.");
            return 2;
        }

        Guid installationId = Guid.NewGuid();
        string deviceKey = CreateSecret();
        EnrollmentRequest request = new(
            ApiContract.SchemaVersion,
            installationId,
            enrollmentToken,
            deviceKey,
            Environment.MachineName,
            AgentVersion.Current,
            backupClientVersionProvider.GetCurrentVersion());

        using HttpClient client = new()
        {
            BaseAddress = EnsureTrailingSlash(serverUrl),
            Timeout = options.HttpTimeout
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ApiContract.EnrollmentRoute.TrimStart('/'),
            request,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(
                $"Inscripción rechazada por el servidor: {(int)response.StatusCode} {response.ReasonPhrase}");
            return 3;
        }

        EnrollmentResponse? enrollment = await response.Content
            .ReadFromJsonAsync<EnrollmentResponse>(cancellationToken);
        if (enrollment is null || enrollment.DeviceId != installationId)
        {
            Console.Error.WriteLine("La respuesta de inscripción no es válida.");
            return 4;
        }

        AgentIdentity identity = new(
            ApiContract.SchemaVersion,
            EnsureTrailingSlash(serverUrl),
            enrollment.DeviceId,
            deviceKey,
            enrollment.AgencyCode,
            enrollment.EnrolledAtUtc);
        await identityStore.SaveAsync(identity, cancellationToken);

        Console.WriteLine($"Equipo inscrito: {identity.DeviceId:D}");
        Console.WriteLine($"Agencia: {identity.AgencyCode}");
        return 0;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        UriBuilder builder = new(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static string CreateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        List<char> characters = [];
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. characters]).Trim();
            }

            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
                Console.Write('*');
            }
        }
    }
}
