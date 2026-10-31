using System.Net;
using Miguel.Server.Reports;
using Miguel.Server.Configuration;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Miguel.Contracts;
using Miguel.Server;
using Miguel.Server.Admin;
using Miguel.Server.Assets;
using Miguel.Server.Audit;
using Miguel.Server.BackupControl;
using Miguel.Server.Cases;
using Miguel.Server.Distribution;
using Miguel.Server.Governance;
using Miguel.Server.Nas;
using Miguel.Server.NetworkDiscovery;
using Miguel.Server.Security;
using Miguel.Server.Storage;
using Miguel.Server.Software;
using Miguel.Server.Vulnerabilities;
using Miguel.Server.Training;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

long configuredReleaseUploadLimit =
    builder.Configuration.GetValue<long?>(
        $"{ServerOptions.SectionName}:MaxReleaseUploadBytes") ??
    300_000_000;
long releaseUploadLimit =
    configuredReleaseUploadLimit is >= 10_000_000 and <= 1_000_000_000
        ? configuredReleaseUploadLimit
        : 300_000_000;
builder.WebHost.ConfigureKestrel(options =>
{
    // Kestrel rejects oversized bodies before endpoint code runs. Keep this
    // transport limit aligned with AtlasReleaseService's stricter validation.
    options.Limits.MaxRequestBodySize = releaseUploadLimit;
});

builder.Services
    .AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.SectionName))
    .Validate(
        options => options.SecurityEventRetentionDays is >= 30 and <= 730,
        "SecurityEventRetentionDays debe estar entre 30 y 730 días.")
    .Validate(
        options => options.SecurityPostureRetentionDays is >= 90 and <= 1825,
        "SecurityPostureRetentionDays debe estar entre 90 y 1825 días.")
    .Validate(
        options => options.NetworkDiscoveryIntervalHours is >= 1 and <= 168,
        "NetworkDiscoveryIntervalHours debe estar entre 1 y 168 horas.")
    .Validate(
        options => options.AdminNotificationRetentionDays is >= 30 and <= 730,
        "AdminNotificationRetentionDays debe estar entre 30 y 730 dias.")
    .Validate(
        options => options.MaxTrainingAttendanceUploadBytes is >= 100_000 and <= 10_000_000,
        "MaxTrainingAttendanceUploadBytes debe estar entre 100 KB y 10 MB.")
    .Validate(
        options => options.MaxGovernanceDocumentUploadBytes is >= 100_000 and <= 25_000_000,
        "MaxGovernanceDocumentUploadBytes debe estar entre 100 KB y 25 MB.")
    .Validate(
        options => options.MaxBackupEvidenceUploadBytes is >= 100_000 and <= 25_000_000,
        "MaxBackupEvidenceUploadBytes debe estar entre 100 KB y 25 MB.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.OperationalTimeZoneId),
        "OperationalTimeZoneId es obligatorio.")
    .Validate(
        options => options.NetworkDiscoveryStartupDelaySeconds is >= 30 and <= 3600,
        "NetworkDiscoveryStartupDelaySeconds debe estar entre 30 y 3600 segundos.")
    .Validate(
        options => options.NetworkDiscoveryScopeTimeoutSeconds is >= 30 and <= 900,
        "NetworkDiscoveryScopeTimeoutSeconds debe estar entre 30 y 900 segundos.")
    .Validate(
        options => options.NetworkDiscoveryMaxPacketsPerSecond is >= 10 and <= 500,
        "NetworkDiscoveryMaxPacketsPerSecond debe estar entre 10 y 500.")
    .Validate(options => options.NetworkDiscoveryWorkers is >= 1 and <= 4,
        "NetworkDiscoveryWorkers debe estar entre 1 y 4.")
    .Validate(options => options.NetworkDiscoveryInventoryMaxAgeMinutes is >= 5 and <= 120,
        "NetworkDiscoveryInventoryMaxAgeMinutes debe estar entre 5 y 120.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<AgentPolicyService>();
builder.Services.AddSingleton<OperationalSettingsService>();
builder.Services.AddSingleton<ReportCatalog>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHostedService<ReportWorker>();

string connectionString = builder.Configuration.GetConnectionString("Miguel")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Miguel.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<MiguelRepository>();
builder.Services.AddSingleton<SoftwareRepository>();
builder.Services.Configure<VulnerabilityOptions>(builder.Configuration.GetSection("VulnerabilityIntelligence"));
builder.Services.AddHttpClient("vulnerability-nvd", c => c.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("vulnerability-feed", c => c.Timeout = TimeSpan.FromSeconds(180))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { MaxAutomaticRedirections = 5 });
builder.Services.AddSingleton<VulnerabilitySync>();
builder.Services.AddSingleton<VulnerabilityRepository>();
builder.Services.AddHostedService<VulnerabilityWorker>();
builder.Services.AddHostedService<SoftwareNotificationService>();
builder.Services.AddSingleton<SecurityRepository>();
builder.Services.AddHostedService<SecurityTelemetryMaintenanceService>();
builder.Services.AddSingleton<AdminRepository>();
builder.Services.AddSingleton<UpdaterCoordinator>();
builder.Services.AddHostedService<UpdaterMaintenanceService>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<InfrastructureRepository>();
builder.Services.AddSingleton<AdminAccountService>();
builder.Services.AddSingleton<AdminAuthenticationService>();
builder.Services.AddSingleton<AdminDashboardService>();
builder.Services.AddSingleton<AdminNotificationRepository>();
builder.Services.AddSingleton<AdminNotificationService>();
builder.Services.AddSingleton<BackupControlRepository>();
builder.Services.AddSingleton<BackupScheduleService>();
builder.Services.AddSingleton<BackupEvidenceContextService>();
builder.Services.AddSingleton<BackupEvidenceService>();
builder.Services.AddSingleton<BackupPlanRepository>();
builder.Services.AddSingleton<BackupRecordService>();
builder.Services.AddSingleton<BackupPlanDemonstrationService>();
builder.Services.AddSingleton<AcronisImportService>();
builder.Services.AddSingleton<CaseRepository>();
builder.Services.AddSingleton<TrainingRepository>();
builder.Services.AddSingleton<TrainingAttendanceImportService>();
builder.Services.AddSingleton<GovernanceRepository>();
builder.Services.AddSingleton<GovernanceDocumentService>();
builder.Services.AddSingleton<GovernancePreviewService>();
builder.Services.AddSingleton<AtlasReleaseService>();
builder.Services.AddSingleton<AtlasServerReleaseCatalogService>();
builder.Services.AddSingleton<AtlasDistributionService>();
builder.Services.AddSingleton<ReleaseDeploymentRepository>();
builder.Services.AddSingleton<NasLifecycleService>();
builder.Services.AddSingleton<AssetCustodyService>();
builder.Services.AddSingleton<NasPathPolicy>();
builder.Services.AddSingleton<NasExplorerService>();
builder.Services.AddSingleton<NasProvisionerClient>();
builder.Services.AddSingleton<NasProbeService>();
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<NasProbeService>());
builder.Services.AddSingleton<NetworkDiscoveryRepository>();
builder.Services.AddSingleton<INetworkProbeRunner,NetworkProbeRunner>();
builder.Services.AddSingleton<INetworkNameResolver,NetworkNameResolver>();
builder.Services.AddSingleton<NmapDiscoveryEngine>();
builder.Services.AddSingleton<INetworkDiscoveryEngine>(provider=>provider.GetRequiredService<NmapDiscoveryEngine>());
builder.Services.AddSingleton<NetworkDiscoveryService>();
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<NetworkDiscoveryService>());

builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    options.IterationCount = 600_000;
});
builder.Services.AddSingleton<
    IPasswordHasher<AdminUserAccount>,
    PasswordHasher<AdminUserAccount>>();

IDataProtectionBuilder dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("MIGUEL");
string? dataProtectionPath =
    builder.Configuration[$"{ServerOptions.SectionName}:DataProtectionPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    DirectoryInfo keyDirectory = Directory.CreateDirectory(
        Path.GetFullPath(dataProtectionPath));
    dataProtection.PersistKeysToFileSystem(keyDirectory);
}

bool isDevelopment = builder.Environment.IsDevelopment();
builder.Services
    .AddAuthentication(AdminEndpointExtensions.AuthenticationScheme)
    .AddCookie(
        AdminEndpointExtensions.AuthenticationScheme,
        options =>
        {
            options.Cookie.Name = isDevelopment
                ? "Miguel.Admin.Development"
                : "__Host-Miguel.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = isDevelopment
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(1);
            options.SlidingExpiration = false;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<AccessService>().ValidateAsync(context),
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    }
                    else
                    {
                        context.Response.Redirect("/login");
                    }

                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    }
                    else
                    {
                        context.Response.Redirect("/login");
                    }

                    return Task.CompletedTask;
                }
            };
        });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(UpdaterEndpoints.ManagePolicy, policy => policy
        .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireRole(AdminRoles.Administrator));
    options.AddPolicy(
        AdminEndpointExtensions.ReadPolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(
                AdminRoles.Administrator,
                AdminRoles.Auditor,
                AdminRoles.Operator,
                AdminRoles.Installer,
                AdminRoles.TechnologyManager));
    options.AddPolicy(
        AdminEndpointExtensions.OperationsReadPolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(
                AdminRoles.Administrator,
                AdminRoles.Auditor,
                AdminRoles.Operator,
                AdminRoles.TechnologyManager));
    options.AddPolicy(
        AdminEndpointExtensions.ExportPolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(
                AdminRoles.Administrator,
                AdminRoles.Auditor,
                AdminRoles.TechnologyManager));
    options.AddPolicy(
        AdminEndpointExtensions.ProvisionPolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator));
    options.AddPolicy(
        AdminEndpointExtensions.InstallerPolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator, AdminRoles.Installer));
    options.AddPolicy(
        CaseEndpointExtensions.ManagePolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator, AdminRoles.Operator));
    options.AddPolicy(
        TrainingEndpointExtensions.ManagePolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator, AdminRoles.Operator));
    options.AddPolicy(
        GovernanceEndpointExtensions.ManagePolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator, AdminRoles.Operator));
    options.AddPolicy(
        GovernanceEndpointExtensions.ApprovePolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.TechnologyManager));
    options.AddPolicy(
        BackupControlEndpointExtensions.ManagePolicy,
        policy => policy
            .AddAuthenticationSchemes(AdminEndpointExtensions.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(AdminRoles.Administrator));
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("agent", context =>
    {
        string partition = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.AddPolicy("login", context =>
    {
        string partition = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.AddPolicy("admin", context =>
    {
        string partition =
            context.User.Identity?.Name ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

WebApplication app = builder.Build();

await app.Services.GetRequiredService<DatabaseInitializer>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<AtlasServerReleaseCatalogService>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

if (args.Length >= 3 &&
    string.Equals(args[0], "tokens", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "issue", StringComparison.OrdinalIgnoreCase))
{
    string agencyCode = args[2];
    int hours = args.Length >= 4 && int.TryParse(args[3], out int parsedHours)
        ? parsedHours
        : 24;
    EnrollmentTokenIssue issue = await app.Services
        .GetRequiredService<MiguelRepository>()
        .IssueEnrollmentTokenAsync(
            agencyCode,
            TimeSpan.FromHours(hours),
            new EnrollmentIssuanceContext(
                "server-console",
                "System",
                "loopback",
                "SERVER_CONSOLE",
                null),
            CancellationToken.None);

    Console.WriteLine($"Token de inscripción para {agencyCode.ToUpperInvariant()}:");
    Console.WriteLine(issue.Token);
    Console.WriteLine($"Expira (UTC): {issue.ExpiresAtUtc:O}");
    Console.WriteLine(
        "El token se muestra una sola vez y se invalida al inscribir un equipo.");
    return;
}

if (await AdminConsoleCommand.TryHandleAsync(args, app.Services))
{
    return;
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        IHeaderDictionary headers = context.Response.Headers;
        string requestPath = context.Request.Path.Value ?? string.Empty;
        bool isGovernanceInlinePreview =
            requestPath.StartsWith(
                "/api/v1/admin/governance/versions/",
                StringComparison.OrdinalIgnoreCase) &&
            requestPath.EndsWith(
                "/inline",
                StringComparison.OrdinalIgnoreCase);
        headers["Content-Security-Policy"] =
            "default-src 'self'; base-uri 'none'; frame-ancestors " +
            (isGovernanceInlinePreview ? "'self'; " : "'none'; ") +
            "form-action 'self'; img-src 'self' data:; object-src 'none'; " +
            "script-src 'self'; style-src 'self'; connect-src 'self'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] =
                "max-age=31536000; includeSubDomains";
        }

        if (context.Request.Path.StartsWithSegments("/api/v1/admin") ||
            context.Request.Path == "/login" ||
            context.Request.Path == "/dashboard")
        {
            headers["Cache-Control"] = "no-store";
            headers["Pragma"] = "no-cache";
        }

        return Task.CompletedTask;
    });

    ServerOptions options = context.RequestServices
        .GetRequiredService<IOptions<ServerOptions>>()
        .Value;
    IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
    bool isLoopback =
        remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);

    if (!options.AllowInsecureHttp &&
        !context.Request.IsHttps &&
        !isLoopback)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new { error = "https_required" },
            cancellationToken: context.RequestAborted);
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/v1/agent"),
    branch => branch.UseMiddleware<DeviceAuthenticationMiddleware>());

string webDirectory = Path.Combine(app.Environment.ContentRootPath, "Web");
app.MapGet("/", () => Results.Redirect("/dashboard"));
app.MapGet("/login", (HttpContext context) =>
    context.User.Identity?.IsAuthenticated == true
        ? Results.Redirect("/dashboard")
        : Results.File(
            Path.Combine(webDirectory, "login.html"),
            "text/html; charset=utf-8"))
    .AllowAnonymous();
app.MapGet("/dashboard", () =>
        Results.File(
            Path.Combine(webDirectory, "dashboard.html"),
            "text/html; charset=utf-8"))
    .RequireAuthorization(AdminEndpointExtensions.ReadPolicy);

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "ok",
    service = "Miguel.Server",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/health/ready", async (
    DatabaseInitializer database,
    CancellationToken cancellationToken) =>
{
    bool ready = await database.IsReadyAsync(cancellationToken);
    return ready
        ? Results.Ok(new { status = "ready" })
        : Results.Json(
            new { status = "not_ready" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapPost(ApiContract.EnrollmentRoute, async (
    HttpContext context,
    EnrollmentRequest request,
    MiguelRepository repository,
    AdminRepository auditRepository,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    try
    {
        EnrollmentResponse response =
            await repository.EnrollAsync(
                request,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                cancellationToken);
        return Results.Created(
            $"/api/v1/devices/{response.DeviceId:D}",
            response);
    }
    catch (EnrollmentRejectedException)
    {
        await auditRepository.WriteAuditAsync(
            timeProvider.GetUtcNow(),
            "Anonymous",
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            "device.enrollment.rejected",
            "Device",
            request.InstallationId.ToString("D"),
            new
            {
                request.MachineName,
                remoteAddress =
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                reason = "invalid_or_expired_one_time_token",
                tokenStored = false
            },
            cancellationToken);
        return Results.Json(
            new { error = "enrollment_rejected" },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (ArgumentException)
    {
        await auditRepository.WriteAuditAsync(
            timeProvider.GetUtcNow(),
            "Anonymous",
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            "device.enrollment.invalid",
            "Device",
            request.InstallationId.ToString("D"),
            new
            {
                machineName = request.MachineName?.Trim(),
                remoteAddress =
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                reason = "invalid_enrollment_contract",
                tokenStored = false
            },
            cancellationToken);
        return Results.BadRequest(
            new { error = "invalid_enrollment_request" });
    }
}).RequireRateLimiting("agent");

RouteGroupBuilder agentApi = app.MapGroup("/api/v1/agent")
    .RequireRateLimiting("agent");
agentApi.MapGet("/policy", async (HttpContext context, AgentPolicyService service, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    Guid device = (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    try { return Results.Ok(await service.ResolveAsync(device, true, ct)); }
    catch (PortalException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
});
agentApi.MapPost("/policy/receipt", async (HttpContext context, AgentPolicyReceipt receipt, AgentPolicyService service, CancellationToken ct) =>
{
    Guid device = (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    try { await service.ReceiptAsync(device, receipt, ct); return Results.NoContent(); }
    catch (PortalException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
});

agentApi.MapPost("/heartbeat", async (
    HttpContext context,
    AgentHeartbeat heartbeat,
    MiguelRepository repository,
    CancellationToken cancellationToken) =>
{
    if (heartbeat.SchemaVersion != ApiContract.SchemaVersion)
    {
        return Results.BadRequest(new { error = "unsupported_schema" });
    }

    if (!AdminAgentDeliveryHealthEndpoints.IsValid(heartbeat.DeliveryHealth))
        return Results.BadRequest(new { error = "invalid_delivery_health" });

    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    bool recorded =
        await repository.RecordHeartbeatAsync(
            deviceId,
            heartbeat,
            cancellationToken);
    return recorded ? Results.NoContent() : Results.NotFound();
});

agentApi.MapPost("/inventory", async (
    HttpContext context,
    DeviceInventory inventory,
    MiguelRepository repository,
    CancellationToken cancellationToken) =>
{
    if (inventory.SchemaVersion != ApiContract.SchemaVersion)
    {
        return Results.BadRequest(new { error = "unsupported_schema" });
    }

    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    try { await repository.RecordInventoryAsync(deviceId, inventory, cancellationToken); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_inventory" }); }
    return Results.NoContent();
});

agentApi.MapPost("/backup-runs", async (
    HttpContext context,
    BackupRunReport report,
    MiguelRepository repository,
    CancellationToken cancellationToken) =>
{
    if (!BackupTelemetry.IsValid(report, DateTimeOffset.UtcNow))
    {
        return Results.BadRequest(new { error = "invalid_backup_report" });
    }

    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    await repository.RecordBackupRunAsync(
        deviceId,
        report,
        cancellationToken);
    return Results.NoContent();
});

agentApi.MapPost("/security/posture", async (
    HttpContext context,
    SecurityPostureSnapshot posture,
    SecurityRepository repository,
    CancellationToken cancellationToken) =>
{
    try
    {
        Guid deviceId =
            (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
        await repository.RecordPostureAsync(deviceId, posture, cancellationToken);
        return Results.NoContent();
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_security_posture" });
    }
});

agentApi.MapPost("/security/events", async (
    HttpContext context,
    SecurityEventBatch batch,
    SecurityRepository repository,
    CancellationToken cancellationToken) =>
{
    try
    {
        Guid deviceId =
            (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
        await repository.RecordEventsAsync(deviceId, batch, cancellationToken);
        return Results.NoContent();
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_security_events" });
    }
});

agentApi.MapGet("/releases", (
    AtlasDistributionService distribution,
    CancellationToken cancellationToken) =>
    distribution.GetReleaseAsync(cancellationToken));

agentApi.MapGet("/releases/current-signed", async (
    HttpContext context,
    AtlasReleaseService releases,
    ReleaseDeploymentRepository deployments,
    UpdaterCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    Guid? releaseId =
        await coordinator.ResolveAsync(deviceId, false, cancellationToken);
    AtlasSignedReleaseEnvelope? release =
        await releases.GetEnvelopeAsync(releaseId, cancellationToken);
    return release is null ? Results.NotFound() : Results.Ok(release);
});

agentApi.MapGet("/releases/current-signed/packages/{component}", async (
    HttpContext context,
    string component,
    AtlasReleaseService releases,
    ReleaseDeploymentRepository deployments,
    UpdaterCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (component.Length is < 1 or > 32 ||
        component.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character != '-'))
    {
        return Results.BadRequest(new { error = "invalid_component" });
    }

    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    Guid? releaseId =
        await coordinator.ResolveAsync(deviceId, false, cancellationToken);
    AtlasPackageFile? package = await releases.GetPackageAsync(
        releaseId,
        component,
        cancellationToken);
    return package is null
        ? Results.NotFound()
        : Results.File(
            package.PhysicalPath,
            package.ContentType,
            package.FileName,
            enableRangeProcessing: true);
});

agentApi.MapGet(
    "/releases/{releaseId:guid}/packages/{component}",
    async (
        HttpContext context,
        Guid releaseId,
        string component,
        AtlasReleaseService releases,
        ReleaseDeploymentRepository deployments,
        UpdaterCoordinator coordinator,
        CancellationToken cancellationToken) =>
    {
        if (component.Length is < 1 or > 32 ||
            component.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            return Results.BadRequest(new { error = "invalid_component" });
        }

        Guid deviceId =
            (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
        Guid? authorizedReleaseId =
            await coordinator.ResolveAsync(deviceId, context.Request.Headers["X-Atlas-Updater-Protocol"] == "2", cancellationToken);
        if (authorizedReleaseId != releaseId)
        {
            return Results.NotFound();
        }
        if (context.Request.Headers["X-Atlas-Updater-Protocol"] == "2" &&
            (!Guid.TryParse(context.Request.Headers["X-Atlas-Updater-Attempt"], out Guid attemptId) ||
             !await coordinator.CanDownloadAsync(deviceId, releaseId, attemptId, cancellationToken)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        AtlasPackageFile? package = await releases.GetPackageAsync(
            releaseId,
            component,
            cancellationToken);
        return package is null
            ? Results.NotFound()
            : Results.File(
                package.PhysicalPath,
                package.ContentType,
                package.FileName,
                enableRangeProcessing: true);
    });

agentApi.MapGet("/packages/{component}", async (
    HttpContext context,
    string component,
    AtlasDistributionService distribution,
    AtlasReleaseService releases,
    ReleaseDeploymentRepository deployments,
    UpdaterCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (component.Length is < 1 or > 32 ||
        component.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character != '-'))
    {
        return Results.BadRequest(new { error = "invalid_component" });
    }

    bool signedUpdater = context.Request.Headers.UserAgent
        .ToString()
        .StartsWith("ATLAS.Updater/", StringComparison.OrdinalIgnoreCase);
    Guid deviceId =
        (Guid)context.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    Guid? releaseId = signedUpdater
        ? await coordinator.ResolveAsync(deviceId, false, cancellationToken)
        : null;
    AtlasPackageFile? package = signedUpdater
        ? await releases.GetPackageAsync(
            releaseId,
            component,
            cancellationToken)
        : await distribution.GetPackageAsync(component, cancellationToken);
    return package is null
        ? Results.NotFound()
        : Results.File(
            package.PhysicalPath,
            package.ContentType,
            package.FileName,
            enableRangeProcessing: true);
});

app.MapAdminEndpoints();
app.MapAgentDeliveryHealthEndpoints();
app.MapNasLifecycleEndpoints();
app.MapAssetCustodyEndpoints();
app.MapBackupControlEndpoints();
app.MapBackupScheduleEndpoints();
app.MapBackupWorkspaceEndpoints();
app.MapAcronisImportEndpoints();
app.MapCaseEndpoints();
app.MapTrainingEndpoints();
app.MapGovernanceEndpoints();
app.MapAuditEndpoints();
app.MapUpdaterEndpoints();
app.MapSoftwareEndpoints();
app.MapVulnerabilityEndpoints();
app.MapPortalEndpoints();

await app.RunAsync();
