using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Miguel.Server.Admin;

public sealed partial class AdminAccountService(
    AdminRepository repository,
    IPasswordHasher<AdminUserAccount> passwordHasher,
    TimeProvider timeProvider)
{
    public async Task<AdminUserAccount> CreateAsync(
        string username,
        string displayName,
        string role,
        string password,
        CancellationToken cancellationToken)
    {
        string normalizedUsername = NormalizeUsername(username);
        string trimmedDisplayName = displayName.Trim();
        string normalizedRole = NormalizeRole(role);
        ValidatePassword(password, normalizedUsername);

        if (trimmedDisplayName.Length is < 3 or > 128)
        {
            throw new ArgumentException(
                "El nombre visible debe tener entre 3 y 128 caracteres.",
                nameof(displayName));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AdminUserAccount account = new(
            Guid.NewGuid(),
            username.Trim(),
            normalizedUsername,
            trimmedDisplayName,
            string.Empty,
            normalizedRole,
            true,
            0,
            null,
            now,
            null);
        account = account with
        {
            PasswordHash = passwordHasher.HashPassword(account, password)
        };

        await repository.CreateAdminAsync(account, cancellationToken);
        await repository.WriteAuditAsync(
            now,
            "System",
            "admin-cli",
            "admin.account.created",
            "AdminUser",
            account.Id.ToString("D"),
            new { account.Username, account.Role },
            cancellationToken);

        return account;
    }

    public static string NormalizeUsername(string username)
    {
        string normalized = username.Trim().ToUpperInvariant();
        if (!UsernamePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "El usuario debe tener entre 3 y 64 caracteres y usar solo letras, números, punto, guion o guion bajo.",
                nameof(username));
        }

        return normalized;
    }

    private static string NormalizeRole(string role)
    {
        string? canonicalRole = new[]
            {
                AdminRoles.Administrator,
                AdminRoles.Auditor,
                AdminRoles.Operator,
                AdminRoles.Installer,
                AdminRoles.TechnologyManager
            }
            .SingleOrDefault(candidate =>
                string.Equals(candidate, role.Trim(), StringComparison.OrdinalIgnoreCase));

        return canonicalRole
            ?? throw new ArgumentException("El rol administrativo no es válido.", nameof(role));
    }

    private static void ValidatePassword(string password, string normalizedUsername)
    {
        if (password.Length is < 14 or > 128)
        {
            throw new ArgumentException(
                "La contraseña debe tener entre 14 y 128 caracteres.",
                nameof(password));
        }

        if (password.Contains(normalizedUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "La contraseña no debe contener el nombre de usuario.",
                nameof(password));
        }
    }

    [GeneratedRegex(@"^[A-Z0-9._-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
