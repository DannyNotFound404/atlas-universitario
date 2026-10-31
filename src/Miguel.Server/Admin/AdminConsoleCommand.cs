using Npgsql;

namespace Miguel.Server.Admin;

public static class AdminConsoleCommand
{
    public static async Task<bool> TryHandleAsync(
        string[] args,
        IServiceProvider services)
    {
        if (args.Length == 0 ||
            !string.Equals(args[0], "admins", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length != 5 ||
            !string.Equals(args[1], "create", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "Uso: admins create USUARIO \"NOMBRE VISIBLE\" Administrator|Auditor|Operator|Installer");
            Environment.ExitCode = 2;
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "La creación del administrador requiere una terminal interactiva. Use systemd-run --pty.");
            Environment.ExitCode = 2;
            return true;
        }

        string password = ReadSecret("Contraseña administrativa: ");
        string confirmation = ReadSecret("Repita la contraseña: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Las contraseñas no coinciden.");
            Environment.ExitCode = 2;
            return true;
        }

        try
        {
            AdminUserAccount account = await services
                .GetRequiredService<AdminAccountService>()
                .CreateAsync(
                    args[2],
                    args[3],
                    args[4],
                    password,
                    CancellationToken.None);

            Console.WriteLine($"Administrador creado: {account.Username}");
            Console.WriteLine($"Nombre: {account.DisplayName}");
            Console.WriteLine($"Rol: {account.Role}");
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            Console.Error.WriteLine("Ya existe un administrador con ese nombre de usuario.");
            Environment.ExitCode = 3;
            return true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 2;
            return true;
        }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        System.Text.StringBuilder secret = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar) && secret.Length < 128)
            {
                secret.Append(key.KeyChar);
            }
        }
    }
}
