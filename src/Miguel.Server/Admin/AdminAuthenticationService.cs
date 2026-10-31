using Microsoft.AspNetCore.Identity;

namespace Miguel.Server.Admin;

public sealed class AdminAuthenticationService
{
    private const int LockoutThreshold = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly AdminRepository repository;
    private readonly IPasswordHasher<AdminUserAccount> passwordHasher;
    private readonly TimeProvider timeProvider;
    private readonly AdminUserAccount dummyAccount;
    private readonly string dummyHash;

    public AdminAuthenticationService(
        AdminRepository repository,
        IPasswordHasher<AdminUserAccount> passwordHasher,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.passwordHasher = passwordHasher;
        this.timeProvider = timeProvider;
        dummyAccount = new AdminUserAccount(
            Guid.Empty,
            "dummy",
            "DUMMY",
            "Dummy",
            string.Empty,
            AdminRoles.Auditor,
            false,
            0,
            null,
            DateTimeOffset.UnixEpoch,
            null);
        dummyHash = passwordHasher.HashPassword(
            dummyAccount,
            "MIGUEL-dummy-password-never-used");
    }

    public async Task<AdminLoginResult> AuthenticateAsync(
        string username,
        string password,
        string remoteAddress,
        CancellationToken cancellationToken,
        bool deferSuccess = false)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string normalizedUsername;

        try
        {
            normalizedUsername = AdminAccountService.NormalizeUsername(username);
        }
        catch (ArgumentException)
        {
            _ = passwordHasher.VerifyHashedPassword(
                dummyAccount,
                dummyHash,
                password);
            await AuditFailureAsync("INVALID", remoteAddress, now, cancellationToken);
            return new AdminLoginResult(null, AdminLoginFailure.InvalidCredentials);
        }

        AdminUserAccount? account =
            await repository.FindAdminAsync(normalizedUsername, cancellationToken);

        if (account is null)
        {
            _ = passwordHasher.VerifyHashedPassword(
                dummyAccount,
                dummyHash,
                password);
            await AuditFailureAsync(
                normalizedUsername,
                remoteAddress,
                now,
                cancellationToken);
            return new AdminLoginResult(null, AdminLoginFailure.InvalidCredentials);
        }

        if (!account.Enabled)
        {
            await AuditFailureAsync(
                account.Username,
                remoteAddress,
                now,
                cancellationToken);
            return new AdminLoginResult(null, AdminLoginFailure.Disabled);
        }

        if (account.LockedUntilUtc is not null &&
            account.LockedUntilUtc > now)
        {
            await AuditFailureAsync(
                account.Username,
                remoteAddress,
                now,
                cancellationToken);
            return new AdminLoginResult(null, AdminLoginFailure.Locked);
        }

        PasswordVerificationResult verification =
            passwordHasher.VerifyHashedPassword(
                account,
                account.PasswordHash,
                password);

        if (verification == PasswordVerificationResult.Failed)
        {
            DateTimeOffset? lockedUntil = await repository.RecordFailedLoginAsync(
                account.Id,
                LockoutThreshold,
                now.Add(LockoutDuration),
                cancellationToken);
            await AuditFailureAsync(
                account.Username,
                remoteAddress,
                now,
                cancellationToken);
            return new AdminLoginResult(
                null,
                lockedUntil > now
                    ? AdminLoginFailure.Locked
                    : AdminLoginFailure.InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            string updatedHash = passwordHasher.HashPassword(account, password);
            bool updated = await repository.UpdatePasswordHashAsync(
                account.Id,
                updatedHash,
                account.PasswordHash,
                cancellationToken);
            if (!updated) return new AdminLoginResult(null, AdminLoginFailure.InvalidCredentials);
            account = account with { PasswordHash = updatedHash };
        }

        if (!deferSuccess) await CompleteAsync(account, remoteAddress, cancellationToken);
        return new AdminLoginResult(account, AdminLoginFailure.None);
    }

    public async Task CompleteAsync(AdminUserAccount account, string remoteAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await repository.RecordSuccessfulLoginAsync(
            account.Id,
            now,
            cancellationToken);
        await repository.WriteAuditAsync(
            now,
            account.Role,
            account.Username,
            "admin.login.succeeded",
            "AdminUser",
            account.Id.ToString("D"),
            new { remoteAddress },
            cancellationToken);

    }

    private Task AuditFailureAsync(
        string actorId,
        string remoteAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        repository.WriteAuditAsync(
            now,
            "Anonymous",
            actorId,
            "admin.login.failed",
            "AdminUser",
            null,
            new { remoteAddress },
            cancellationToken);
}
