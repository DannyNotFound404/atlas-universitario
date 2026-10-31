using System.Security.Claims;
using Miguel.Server.Audit;
using Microsoft.AspNetCore.Authentication;
using Miguel.Server.Distribution;
using Miguel.Server.Nas;
using Miguel.Server.NetworkDiscovery;
using Miguel.Server.Storage;
using Npgsql;

namespace Miguel.Server.Admin;

public static class AdminEndpointExtensions
{
    public const string AuthenticationScheme = "MiguelAdmin";
    public const string ReadPolicy = "MiguelAdminRead";
    public const string OperationsReadPolicy = "MiguelOperationsRead";
    public const string ExportPolicy = "MiguelAdminExport";
    public const string ProvisionPolicy = "AtlasAdminProvision";
    public const string InstallerPolicy = "AtlasInstallerProvision";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/bootstrap-status", async (
            AdminRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                configured = await repository.AnyAdminExistsAsync(cancellationToken)
            }))
            .AllowAnonymous()
            .RequireRateLimiting("login");

        app.MapPost("/api/v1/admin/login", async (
            HttpContext context,
            AdminLoginRequest request,
            AdminAuthenticationService authentication,
            Miguel.Server.Configuration.AccessService access,
            Miguel.Server.Configuration.MfaService mfa,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                request.Username.Length > 64 ||
                string.IsNullOrEmpty(request.Password) ||
                request.Password.Length > 128)
            {
                return Results.Json(
                    new { error = "invalid_credentials" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            string remoteAddress =
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            AdminLoginResult result = await authentication.AuthenticateAsync(
                request.Username,
                request.Password,
                remoteAddress,
                cancellationToken, deferSuccess: true);

            if (!result.Succeeded)
            {
                return Results.Json(
                    new { error = "invalid_credentials_or_locked" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            AdminUserAccount account = result.Account!;
            bool mfaEnabled = await mfa.EnabledAsync(account.Id, cancellationToken);
            if (mfaEnabled)
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                    return Results.Json(new { error = "mfa_required", mfaRequired = true }, statusCode: 401);
                if (!await mfa.VerifyLoginAsync(account, request.Code, cancellationToken))
                    return Results.Json(new { error = "mfa_invalid", mfaRequired = true }, statusCode: 401);
            }
            Guid sessionId;
            try { sessionId = await access.CreateSessionAsync(account.Id, cancellationToken, account.PasswordHash, mfaEnabled, account.SecurityRevision); }
            catch (Miguel.Server.Reports.PortalException) { return Results.Json(new { error="credentials_changed" },statusCode:401); }
            await authentication.CompleteAsync(account, remoteAddress, cancellationToken);
            DateTimeOffset expiresAtUtc =
                timeProvider.GetUtcNow().Add(SessionLifetime);
            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, account.Id.ToString("D")),
                new(ClaimTypes.Name, account.Username),
                new(ClaimTypes.GivenName, account.DisplayName),
                new(ClaimTypes.Role, account.Role),
                new("atlas-session", sessionId.ToString())
            ];
            ClaimsIdentity identity = new(claims, AuthenticationScheme);
            await context.SignInAsync(
                AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    AllowRefresh = true,
                    IsPersistent = false,
                    ExpiresUtc = expiresAtUtc
                });

            return Results.Ok(new AdminSessionResponse(
                account.Username,
                account.DisplayName,
                account.Role,
                expiresAtUtc));
        })
            .AllowAnonymous()
            .RequireRateLimiting("login");

        app.MapPost("/api/v1/admin/logout", async (
            HttpContext context,
            AdminRepository repository,
            TimeProvider timeProvider,
            Miguel.Server.Configuration.AccessService access,
            CancellationToken cancellationToken) =>
        {
            string username = context.User.Identity?.Name ?? "unknown";
            if (Guid.TryParse(context.User.FindFirstValue("atlas-session"), out var sessionId) && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId))
                await access.RevokeAsync(accountId, sessionId, username, cancellationToken);
            await context.SignOutAsync(AuthenticationScheme);
            await repository.WriteAuditAsync(
                timeProvider.GetUtcNow(),
                context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown",
                username,
                "admin.logout",
                null,
                null,
                new
                {
                    remoteAddress =
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ReadPolicy);

        app.MapGet("/api/v1/admin/session", async (HttpContext context) =>
        {
            AuthenticateResult authentication =
                await context.AuthenticateAsync(AuthenticationScheme);
            DateTimeOffset expiresAtUtc =
                authentication.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow;
            return Results.Ok(new AdminSessionResponse(
                context.User.Identity?.Name ?? string.Empty,
                context.User.FindFirstValue(ClaimTypes.GivenName) ?? string.Empty,
                context.User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
                expiresAtUtc));
        }).RequireAuthorization(ReadPolicy);

        RouteGroupBuilder adminApi = app.MapGroup("/api/v1/admin")
            .RequireAuthorization(ReadPolicy)
            .RequireRateLimiting("admin");
        adminApi.AddEndpointFilter(Miguel.Server.Nas.NasLifecycleEndpoints.GuardBindings);

        adminApi.MapGet("/summary", (
            AdminDashboardService dashboard,
            CancellationToken cancellationToken) =>
            dashboard.GetSummaryAsync(cancellationToken));

        adminApi.MapGet("/security", (
            int? devicePage,
            int? devicePageSize,
            string? deviceSort,
            string? deviceDirection,
            int? eventPage,
            int? eventPageSize,
            Guid? eventDeviceId,
            string? eventSort,
            string? eventDirection,
            SecurityRepository security,
            CancellationToken cancellationToken) =>
            security.ReadDashboardAsync(
                devicePage ?? 1,
                devicePageSize ?? 10,
                deviceSort,
                deviceDirection,
                eventPage ?? 1,
                eventPageSize ?? 20,
                eventDeviceId,
                eventSort,
                eventDirection,
                cancellationToken))
            .RequireAuthorization(OperationsReadPolicy);

        adminApi.MapGet("/security/devices/{deviceId:guid}", async (
            Guid deviceId, SecurityRepository security, CancellationToken cancellationToken) =>
        {
            var item = await security.ReadDeviceAsync(deviceId, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapNetworkDiscoveryEndpoints();

        adminApi.MapGet("/agencies", (
            AdminDashboardService dashboard,
            CancellationToken cancellationToken) =>
            dashboard.GetAgenciesAsync(cancellationToken));

        adminApi.MapPost("/agencies", async (
            HttpContext context,
            AdminAgencyRequest request,
            InfrastructureRepository infrastructure,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !InfrastructureInput.TryNormalizeAgency(request, out AdminAgencyRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_agency" });
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            string actor = context.User.Identity?.Name ?? "unknown";
            bool created = await infrastructure.CreateAgencyAsync(
                normalized,
                actor,
                now,
                cancellationToken);
            if (!created)
            {
                return Results.Conflict(new { error = "agency_already_exists" });
            }
            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.agency.created",
                "Agency",
                normalized.Code,
                new
                {
                    normalized.DisplayName,
                    normalized.Location,
                    normalized.Responsible,
                    normalized.DefaultBackupProvider,
                    normalized.Enabled,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            dashboard.Invalidate();
            return Results.Created(
                $"/api/v1/admin/agencies/{normalized.Code}",
                normalized);
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPut("/agencies/{agencyCode}", async (
            string agencyCode,
            HttpContext context,
            AdminAgencyRequest request,
            InfrastructureRepository infrastructure,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string routeCode = InfrastructureInput.NormalizeCode(agencyCode);
            AdminAgencyRequest candidate = request with { Code = routeCode };
            if (!IsDashboardMutation(context) ||
                !InfrastructureInput.TryNormalizeAgency(candidate, out AdminAgencyRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_agency" });
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            string actor = context.User.Identity?.Name ?? "unknown";
            bool updated = await infrastructure.UpdateAgencyAsync(
                routeCode,
                normalized,
                actor,
                now,
                cancellationToken);
            if (!updated)
            {
                return Results.NotFound(new { error = "agency_not_found" });
            }
            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.agency.updated",
                "Agency",
                routeCode,
                new
                {
                    normalized.DisplayName,
                    normalized.Location,
                    normalized.Responsible,
                    normalized.DefaultBackupProvider,
                    normalized.Enabled,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            dashboard.Invalidate();
            return Results.Ok(normalized);
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/nas", async (
            InfrastructureRepository infrastructure,
            NasPathPolicy pathPolicy,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<AdminNasDefinition> appliances =
                await infrastructure.ReadNasAsync(cancellationToken);
            return Results.Ok(appliances.Select(item => ToNasView(item, pathPolicy)));
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapPost("/nas", async (
            HttpContext context,
            AdminNasRequest request,
            InfrastructureRepository infrastructure,
            NasPathPolicy pathPolicy,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !InfrastructureInput.TryNormalizeNas(request, out AdminNasRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_nas" });
            }

            Guid id = Guid.NewGuid();
            DateTimeOffset now = timeProvider.GetUtcNow();
            string actor = context.User.Identity?.Name ?? "unknown";
            try
            {
                bool created = await infrastructure.CreateNasAsync(
                    id,
                    normalized,
                    actor,
                    now,
                    cancellationToken);
                if (!created)
                {
                    return Results.Conflict(new { error = "nas_or_agency_invalid" });
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "nas_or_primary_already_exists" });
            }

            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.nas.created",
                "NasAppliance",
                id.ToString("D"),
                new
                {
                    normalized.Code,
                    normalized.AgencyCode,
                    normalized.Host,
                    normalized.SmbPort,
                    normalized.ShareName,
                    normalized.IsPrimary,
                    normalized.Enabled,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            dashboard.Invalidate();
            AdminNasDefinition createdNas = (await infrastructure.FindNasAsync(id, cancellationToken))!;
            return Results.Created(
                $"/api/v1/admin/nas/{id:D}",
                ToNasView(createdNas, pathPolicy));
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPut("/nas/{nasId:guid}", async (
            Guid nasId,
            HttpContext context,
            AdminNasRequest request,
            InfrastructureRepository infrastructure,
            NasPathPolicy pathPolicy,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !InfrastructureInput.TryNormalizeNas(request, out AdminNasRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_nas" });
            }
            DateTimeOffset now = timeProvider.GetUtcNow();
            string actor = context.User.Identity?.Name ?? "unknown";
            try
            {
                bool updated = await infrastructure.UpdateNasAsync(
                    nasId,
                    normalized,
                    actor,
                    now,
                    cancellationToken);
                if (!updated)
                {
                    return Results.NotFound(new { error = "nas_or_agency_not_found_or_lifecycle_changed" });
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "nas_or_primary_already_exists" });
            }
            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.nas.updated",
                "NasAppliance",
                nasId.ToString("D"),
                new
                {
                    normalized.Code,
                    normalized.AgencyCode,
                    normalized.Host,
                    normalized.SmbPort,
                    normalized.ShareName,
                    normalized.IsPrimary,
                    normalized.Enabled,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            dashboard.Invalidate();
            AdminNasDefinition updatedNas = (await infrastructure.FindNasAsync(nasId, cancellationToken))!;
            return Results.Ok(ToNasView(updatedNas, pathPolicy));
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost("/nas/{nasId:guid}/mount", async (
            Guid nasId,
            HttpContext context,
            AdminNasMountRequest request,
            InfrastructureRepository infrastructure,
            NasProvisionerClient provisioner,
            NasProbeService probes,
            NasPathPolicy pathPolicy,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !InfrastructureInput.TryNormalizeNasMount(
                    request,
                    out AdminNasMountRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_nas_mount" });
            }

            AdminNasDefinition? appliance =
                await infrastructure.FindNasAsync(nasId, cancellationToken);
            if (appliance is null || !appliance.Enabled)
            {
                return Results.NotFound(new { error = "nas_not_found" });
            }

            Miguel.Contracts.NasProvisioningResponse result;
            try
            {
                result = await provisioner.ProvisionAsync(
                    appliance,
                    normalized,
                    cancellationToken);
            }
            catch (NasProvisionerUnavailableException)
            {
                return Results.Json(
                    new { error = "nas_provisioner_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!result.Succeeded)
            {
                return Results.Json(
                    new
                    {
                        error = "nas_mount_failed",
                        code = result.ErrorCode,
                        detail = result.Detail
                    },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            NasProbeResult? probe = await probes.ProbeNowAsync(
                nasId,
                cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            string actor = context.User.Identity?.Name ?? "unknown";
            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.nas.mount.provisioned",
                "NasAppliance",
                nasId.ToString("D"),
                new
                {
                    normalized.Protocol,
                    normalized.SmbSecurityProfile,
                    result.MountPath,
                    probeStatus = probe?.Status,
                    remoteAddress =
                        context.Connection.RemoteIpAddress?.ToString() ??
                        "unknown"
                },
                cancellationToken);
            dashboard.Invalidate();
            AdminNasDefinition refreshed =
                (await infrastructure.FindNasAsync(nasId, cancellationToken))!;
            return Results.Ok(new
            {
                provisioning = result,
                nas = ToNasView(refreshed, pathPolicy)
            });
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost("/nas/{nasId:guid}/probe", async (
            Guid nasId,
            HttpContext context,
            NasProbeService probes,
            InfrastructureRepository infrastructure,
            NasPathPolicy pathPolicy,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(new { error = "invalid_probe_request" });
            }
            NasProbeResult? result = await probes.ProbeNowAsync(nasId, cancellationToken);
            if (result is null)
            {
                return Results.NotFound(new { error = "nas_not_found" });
            }
            await repository.WriteAuditAsync(
                timeProvider.GetUtcNow(),
                "Administrator",
                context.User.Identity?.Name ?? "unknown",
                "admin.nas.probed",
                "NasAppliance",
                nasId.ToString("D"),
                new
                {
                    result.Status,
                    result.LatencyMs,
                    result.ErrorCode,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            AdminNasDefinition appliance = (await infrastructure.FindNasAsync(nasId, cancellationToken))!;
            return Results.Ok(ToNasView(appliance, pathPolicy));
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/nas/{nasId:guid}/browse", async (
            Guid nasId,
            string? path,
            int? page,
            int? pageSize,
            HttpContext context,
            NasExplorerService explorer,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                AdminNasBrowsePage? result = await explorer.BrowseAsync(
                    nasId,
                    path,
                    page ?? 1,
                    pageSize ?? 100,
                    cancellationToken);
                if (result is null)
                {
                    return Results.NotFound(new { error = "nas_not_found" });
                }
                await repository.WriteAuditAsync(
                    timeProvider.GetUtcNow(),
                    context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown",
                    context.User.Identity?.Name ?? "unknown",
                    "admin.nas.browsed",
                    "NasAppliance",
                    nasId.ToString("D"),
                    new
                    {
                        result.RelativePath,
                        result.Page,
                        result.PageSize,
                        result.Total,
                        remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                    },
                    cancellationToken);
                return Results.Ok(result);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "invalid_nas_path" });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(
                    new { error = "nas_path_denied" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DirectoryNotFoundException)
            {
                return Results.Conflict(new { error = "nas_mount_unavailable" });
            }
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapGet("/backup-coverage", (
            AdminDashboardService dashboard,
            CancellationToken cancellationToken) =>
            dashboard.GetBackupCoverageAsync(cancellationToken))
            .RequireAuthorization(OperationsReadPolicy);

        adminApi.MapGet("/notifications", async (
            HttpContext context,
            int? page,
            int? pageSize,
            bool? unreadOnly,
            AdminNotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            string? username = context.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(username))
            {
                return Results.Unauthorized();
            }

            AdminNotificationPage result = await notifications.ReadAsync(
                username,
                page ?? 1,
                pageSize ?? 20,
                unreadOnly ?? false,
                cancellationToken);
            return Results.Ok(result);
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapPost("/notifications/read-all", async (
            HttpContext context,
            AdminNotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            string? username = context.User.Identity?.Name;
            if (!IsDashboardMutation(context) || string.IsNullOrWhiteSpace(username))
            {
                return Results.BadRequest(new { error = "invalid_request_origin" });
            }

            await notifications.MarkAllReadAsync(username, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapPost("/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            HttpContext context,
            AdminNotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            string? username = context.User.Identity?.Name;
            if (!IsDashboardMutation(context) || string.IsNullOrWhiteSpace(username))
            {
                return Results.BadRequest(new { error = "invalid_request_origin" });
            }

            await notifications.MarkReadAsync(
                notificationId,
                username,
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(OperationsReadPolicy);

        adminApi.MapPut("/devices/{deviceId:guid}/backup-policy", async (
            Guid deviceId,
            HttpContext context,
            AdminDeviceBackupPolicyRequest request,
            InfrastructureRepository infrastructure,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string provider = request.BackupProvider?.Trim().ToUpperInvariant() ?? "";
            Guid? nasId = BackupProviders.RequiresNas(provider)
                ? request.NasApplianceId
                : null;
            if (!IsDashboardMutation(context) || !BackupProviders.IsValid(provider))
            {
                return Results.BadRequest(new { error = "invalid_backup_policy" });
            }
            bool updated = await infrastructure.UpdateDeviceBackupPolicyWithAuditAsync(
                deviceId,
                provider,
                nasId,
                context.User.Identity?.Name ?? "unknown",
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                timeProvider.GetUtcNow(),
                cancellationToken,
                actorRole: context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown");
            if (!updated)
            {
                return Results.Conflict(new { error = "device_or_nas_policy_invalid" });
            }
            dashboard.Invalidate();
            return Results.NoContent();
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPut("/devices/{deviceId:guid}/update-ring", async (
            Guid deviceId,
            HttpContext context,
            AtlasDeviceUpdateRingRequest request,
            ReleaseDeploymentRepository deployments,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(
                    new { error = "invalid_update_ring_request" });
            }

            try
            {
                string ringCode = UpdateRings.Normalize(request.RingCode);
                bool updated = await deployments.SetDeviceRingAsync(
                    deviceId,
                    ringCode,
                    cancellationToken);
                if (!updated)
                {
                    return Results.NotFound(
                        new { error = "device_not_found" });
                }

                await repository.WriteAuditAsync(
                    timeProvider.GetUtcNow(),
                    "Administrator",
                    context.User.Identity?.Name ?? "unknown",
                    "admin.device.update_ring.updated",
                    "Device",
                    deviceId.ToString("D"),
                    new
                    {
                        ringCode,
                        remoteAddress =
                            context.Connection.RemoteIpAddress?.ToString() ??
                            "unknown"
                    },
                    cancellationToken);
                dashboard.Invalidate();
                return Results.NoContent();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_update_ring",
                    message = exception.Message
                });
            }
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/devices", (
            string? search,
            string? agency,
            string? connectivity,
            int? page,
            int? pageSize,
            AdminDashboardService dashboard,
            CancellationToken cancellationToken) =>
            dashboard.GetDevicesAsync(
                search,
                agency,
                connectivity,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken));

        adminApi.MapGet("/devices/{deviceId:guid}", async (
            Guid deviceId,
            AdminDashboardService dashboard,
            CancellationToken cancellationToken) =>
        {
            AdminDeviceDetail? device =
                await dashboard.GetDeviceAsync(deviceId, cancellationToken);
            return device is null
                ? Results.NotFound()
                : Results.Ok(device);
        });

        adminApi.MapPost("/devices/{deviceId:guid}/retire", async (
            Guid deviceId,
            HttpContext context,
            AdminDeviceRetirementRequest request,
            AdminRepository repository,
            AdminDashboardService dashboard,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string reason = request.Reason?.Trim() ?? string.Empty;
            if (!IsDashboardMutation(context) ||
                reason.Length is < 8 or > 256 ||
                reason.Any(char.IsControl))
            {
                return Results.BadRequest(
                    new { error = "invalid_retirement_request" });
            }

            AdminDeviceRetirementResult? result =
                await repository.RetireDeviceAsync(
                    deviceId,
                    context.User.Identity?.Name ?? "unknown",
                    reason,
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            if (result is null)
            {
                return Results.NotFound(
                    new { error = "device_not_found" });
            }
            if (!result.Retired)
            {
                return Results.Conflict(new
                {
                    error = "device_already_retired",
                    result.RetiredAtUtc,
                    result.RetiredBy
                });
            }

            dashboard.Invalidate();
            return Results.Ok(new
            {
                deviceId,
                result.MachineName,
                result.AgencyCode,
                result.RetiredAtUtc,
                result.RetiredBy,
                result.RetirementReason
            });
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/devices/export.csv", async (
            HttpContext context,
            AdminDashboardService dashboard,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            byte[] csv = await dashboard.ExportDevicesCsvAsync(cancellationToken);
            await repository.WriteAuditAsync(
                timeProvider.GetUtcNow(),
                "Administrator",
                context.User.Identity?.Name ?? "unknown",
                "admin.inventory.exported",
                "DeviceInventory",
                null,
                new { format = "csv" },
                cancellationToken);
            return Results.File(
                csv,
                "text/csv; charset=utf-8",
                $"atlas-inventario-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        }).RequireAuthorization(ExportPolicy);

        adminApi.MapGet("/distribution", (
            AtlasDistributionService distribution,
            CancellationToken cancellationToken) =>
            distribution.GetReleaseAsync(cancellationToken))
            .RequireAuthorization(InstallerPolicy);

        adminApi.MapGet("/releases", (
            AtlasReleaseService releases,
            CancellationToken cancellationToken) =>
            releases.GetReleasesAsync(cancellationToken))
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/server-releases", (
            AtlasServerReleaseCatalogService releases,
            CancellationToken cancellationToken) =>
            releases.ReadAsync(cancellationToken))
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/release-deployments", (
            ReleaseDeploymentRepository deployments,
            CancellationToken cancellationToken) =>
            deployments.ReadAsync(cancellationToken))
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost(
            "/releases/{releaseId:guid}/deployments",
            async (
                Guid releaseId,
                HttpContext context,
                AtlasReleaseDeploymentRequest request,
                AtlasReleaseService releases,
                ReleaseDeploymentRepository deployments,
                AdminRepository repository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!IsDashboardMutation(context))
                {
                    return Results.BadRequest(
                        new { error = "invalid_deployment_request" });
                }

                try
                {
                    AtlasStoredReleaseView release =
                        await releases.VerifyStoredReleaseAsync(
                            releaseId,
                            cancellationToken);
                    string actor =
                        context.User.Identity?.Name ?? "unknown";
                    DateTimeOffset now = timeProvider.GetUtcNow();
                    AtlasReleaseDeploymentView deployment =
                        await deployments.UpsertAsync(
                            releaseId,
                            request.ScopeType,
                            request.ScopeValue,
                            actor,
                            now,
                            cancellationToken);
                    await repository.WriteAuditAsync(
                        now,
                        "Administrator",
                        actor,
                        "admin.release.deployment.updated",
                        "AtlasRelease",
                        releaseId.ToString("D"),
                        new
                        {
                            release.ReleaseVersion,
                            deployment.ScopeType,
                            deployment.ScopeValue,
                            deployment.TargetDevices,
                            remoteAddress =
                                context.Connection.RemoteIpAddress?.ToString() ??
                                "unknown"
                        },
                        cancellationToken);
                    return Results.Ok(deployment);
                }
                catch (DirectoryNotFoundException)
                {
                    return Results.NotFound(
                        new { error = "release_not_found" });
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_deployment_target",
                        message = exception.Message
                    });
                }
                catch (InvalidDataException exception)
                {
                    return Results.BadRequest(new
                    {
                        error = "release_integrity_failed",
                        message = exception.Message
                    });
                }
            })
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost(
            "/release-deployments/pause",
            async (
                HttpContext context,
                AtlasReleaseDeploymentPauseRequest request,
                ReleaseDeploymentRepository deployments,
                AdminRepository repository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!IsDashboardMutation(context))
                {
                    return Results.BadRequest(
                        new { error = "invalid_deployment_request" });
                }

                try
                {
                    string actor =
                        context.User.Identity?.Name ?? "unknown";
                    DateTimeOffset now = timeProvider.GetUtcNow();
                    bool updated = await deployments.SetPausedAsync(
                        request.ScopeType,
                        request.ScopeValue,
                        request.Paused,
                        actor,
                        now,
                        cancellationToken);
                    if (!updated)
                    {
                        return Results.NotFound(
                            new { error = "deployment_not_found" });
                    }

                    await repository.WriteAuditAsync(
                        now,
                        "Administrator",
                        actor,
                        request.Paused
                            ? "admin.release.deployment.paused"
                            : "admin.release.deployment.resumed",
                        "AtlasReleaseDeployment",
                        $"{request.ScopeType}:{request.ScopeValue}",
                        new
                        {
                            request.Paused,
                            remoteAddress =
                                context.Connection.RemoteIpAddress?.ToString() ??
                                "unknown"
                        },
                        cancellationToken);
                    return Results.NoContent();
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_deployment_target",
                        message = exception.Message
                    });
                }
            })
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapDelete(
            "/release-deployments",
            async (
                string scopeType,
                string scopeValue,
                HttpContext context,
                ReleaseDeploymentRepository deployments,
                AdminRepository repository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!IsDashboardMutation(context))
                {
                    return Results.BadRequest(
                        new { error = "invalid_deployment_request" });
                }

                try
                {
                    bool deleted = await deployments.DeleteAsync(
                        scopeType,
                        scopeValue,
                        cancellationToken);
                    if (!deleted)
                    {
                        return Results.NotFound(
                            new { error = "deployment_not_found" });
                    }

                    await repository.WriteAuditAsync(
                        timeProvider.GetUtcNow(),
                        "Administrator",
                        context.User.Identity?.Name ?? "unknown",
                        "admin.release.deployment.removed",
                        "AtlasReleaseDeployment",
                        $"{scopeType}:{scopeValue}",
                        new
                        {
                            remoteAddress =
                                context.Connection.RemoteIpAddress?.ToString() ??
                                "unknown"
                        },
                        cancellationToken);
                    return Results.NoContent();
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_deployment_target",
                        message = exception.Message
                    });
                }
            })
            .RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost("/releases", async (
            HttpContext context,
            AtlasReleaseService releases,
            ILogger<AtlasReleaseService> logger,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !string.Equals(
                    context.Request.ContentType,
                    AtlasReleaseService.MediaType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(
                    new { error = "invalid_release_upload" });
            }

            string fileName =
                context.Request.Headers["X-Atlas-File-Name"].ToString();
            try
            {
                AtlasStoredReleaseView release = await releases.UploadAsync(
                    context.Request.Body,
                    fileName,
                    cancellationToken);
                await repository.WriteAuditAsync(
                    timeProvider.GetUtcNow(),
                    "Administrator",
                    context.User.Identity?.Name ?? "unknown",
                    "admin.release.uploaded",
                    "AtlasRelease",
                    release.ReleaseId.ToString("D"),
                    new
                    {
                        release.ReleaseVersion,
                        release.CreatedAtUtc,
                        packages = release.Packages.Select(
                            package => new
                            {
                                package.Component,
                                package.Version,
                                package.Sha256,
                                package.SizeBytes
                            }),
                        remoteAddress =
                            context.Connection.RemoteIpAddress?.ToString() ??
                            "unknown"
                    },
                    cancellationToken);
                return Results.Created(
                    $"/api/v1/admin/releases/{release.ReleaseId:D}",
                    release);
            }
            catch (AtlasReleaseConflictException exception)
            {
                return Results.Conflict(new
                {
                    error = "release_already_exists",
                    message = exception.Message
                });
            }
            catch (InvalidDataException exception)
            {
                logger.LogWarning(
                    exception,
                    "Release ATLAS rechazado desde {RemoteAddress}. " +
                    "Archivo declarado: {FileName}; longitud HTTP: {ContentLength}.",
                    context.Connection.RemoteIpAddress,
                    fileName,
                    context.Request.ContentLength);
                return Results.BadRequest(new
                {
                    error = "invalid_signed_release",
                    message = exception.Message
                });
            }
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost("/releases/{releaseId:guid}/activate", async (
            Guid releaseId,
            HttpContext context,
            AtlasReleaseService releases,
            AtlasDistributionService distribution,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(
                    new { error = "invalid_activation_request" });
            }

            try
            {
                AtlasStoredReleaseView release =
                    await releases.ActivateAsync(
                        releaseId,
                        cancellationToken);
                distribution.InvalidateCatalog();
                await repository.WriteAuditAsync(
                    timeProvider.GetUtcNow(),
                    "Administrator",
                    context.User.Identity?.Name ?? "unknown",
                    "admin.release.activated",
                    "AtlasRelease",
                    release.ReleaseId.ToString("D"),
                    new
                    {
                        release.ReleaseVersion,
                        remoteAddress =
                            context.Connection.RemoteIpAddress?.ToString() ??
                            "unknown"
                    },
                    cancellationToken);
                return Results.Ok(release);
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(
                    new { error = "release_not_found" });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new
                {
                    error = "release_integrity_failed",
                    message = exception.Message
                });
            }
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPost("/installers", async (
            HttpContext context,
            AtlasInstallerRequest request,
            AtlasDistributionService distribution,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                string.IsNullOrWhiteSpace(request.AgencyCode) ||
                request.AgencyCode.Length > 64 ||
                request.LifetimeHours is < 1 or > 72)
            {
                return Results.BadRequest(
                    new { error = "invalid_installer_request" });
            }

            AtlasGeneratedInstaller installer =
                await distribution.CreateInstallerAsync(
                    request,
                    new EnrollmentIssuanceContext(
                        context.User.Identity?.Name ?? "unknown",
                        context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown",
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        "DASHBOARD",
                        null),
                    cancellationToken);
            return Results.File(
                installer.PhysicalPath,
                "application/vnd.microsoft.portable-executable",
                installer.DownloadFileName,
                enableRangeProcessing: false);
        }).RequireAuthorization(InstallerPolicy);
    }

    private static bool IsDashboardMutation(HttpContext context) =>
        string.Equals(
            context.Request.Headers["X-Atlas-Request"].ToString(),
            "dashboard",
            StringComparison.Ordinal);

    private static readonly HashSet<string> NetworkDeviceKinds =
    [
        "ROUTER", "NAS", "ENDPOINT", "PRINTER", "ACCESS_POINT",
        "SWITCH", "SERVER", "CAMERA", "UNKNOWN"
    ];

    private static AdminNasView ToNasView(
        AdminNasDefinition appliance,
        NasPathPolicy pathPolicy) =>
        new(
            appliance.Id,
            appliance.Code,
            appliance.AgencyCode,
            appliance.DisplayName,
            appliance.Host,
            appliance.SmbPort,
            appliance.ShareName,
            appliance.Manufacturer,
            appliance.Model,
            appliance.SerialNumber,
            appliance.IsPrimary,
            appliance.Enabled,
            pathPolicy.GetMountPath(appliance),
            appliance.LastProbeAtUtc,
            appliance.LastProbeStatus,
            appliance.LastProbeLatencyMs,
            appliance.TotalBytes,
            appliance.FreeBytes,
            appliance.LastProbeError,
            appliance.CreatedAtUtc,
            appliance.UpdatedAtUtc,
            appliance.UpdatedBy, appliance.LifecycleRevision, appliance.RetiredAtUtc,
            appliance.RetiredBy, appliance.RetirementReason);
}
