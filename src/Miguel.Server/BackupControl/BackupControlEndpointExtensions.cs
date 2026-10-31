using System.Globalization;
using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.BackupControl;

public static class BackupControlEndpointExtensions
{
    public const string ManagePolicy = "AtlasBackupControlManage";

    public static void MapBackupControlEndpoints(this WebApplication app)
    {
        RouteGroupBuilder controls = app.MapGroup("/api/v1/admin/backup-controls")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy)
            .RequireRateLimiting("admin");
        controls.AddEndpointFilter(Miguel.Server.Nas.NasLifecycleEndpoints.GuardBindings);

        controls.MapGet("", (
            BackupControlRepository repository,
            CancellationToken cancellationToken) =>
            repository.ReadDashboardAsync(cancellationToken));

        controls.MapGet("/profiles/{id:guid}", async (
            Guid id,
            BackupControlRepository repository,
            CancellationToken cancellationToken) =>
        {
            BackupControlProfileDetail? detail =
                await repository.ReadDetailAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        controls.MapGet("/evidence/{id:guid}/file", async (
            Guid id,
            BackupEvidenceService evidence,
            CancellationToken cancellationToken) =>
        {
            BackupStoredEvidence? file =
                await evidence.ResolveAsync(id, cancellationToken);
            return file is null
                ? Results.NotFound()
                : Results.File(
                    file.PhysicalPath,
                    file.MediaType,
                    file.OriginalFileName,
                    enableRangeProcessing: true);
        });

        controls.MapPost("/profiles", async (
            HttpContext context,
            BackupControlProfileRequest request,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !BackupControlInput.TryNormalize(request, out request))
            {
                return Results.BadRequest(new { error = "invalid_backup_control_profile" });
            }
            if (!await repository.ReferencesBelongToAgencyAsync(
                    request, cancellationToken))
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_control_relationship" });
            }

            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                Guid? id = await repository.CreateProfileAsync(
                    request, actor.Id, now, cancellationToken);
                if (!id.HasValue)
                {
                    return Results.BadRequest(
                        new { error = "invalid_backup_control_profile" });
                }
                await WriteAuditAsync(
                    context, audit, now, actor,
                    "admin.backup_control.profile.created", "BackupControlProfile",
                    id.Value,
                    new
                    {
                        request.Code,
                        request.AgencyCode,
                        request.Provider,
                        request.StandardId,
                        request.ConfigurationMode,
                        request.ExceptionType,
                        request.FieldOrigins,
                        State = BackupApprovalStatuses.ForSave(request)
                    },
                    cancellationToken);
                BackupControlProfileDetail detail =
                    (await repository.ReadDetailAsync(id.Value, cancellationToken))!;
                return Results.Created(
                    $"/api/v1/admin/backup-controls/profiles/{id.Value:D}", detail);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(
                    new { error = "backup_control_profile_code_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        controls.MapPut("/profiles/{id:guid}", async (
            Guid id,
            HttpContext context,
            BackupControlProfileRequest request,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !BackupControlInput.TryNormalize(request, out request))
            {
                return Results.BadRequest(new { error = "invalid_backup_control_profile" });
            }
            if (!await repository.ReferencesBelongToAgencyAsync(
                    request, cancellationToken))
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_control_relationship" });
            }

            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                bool updated = await repository.UpdateProfileAsync(
                    id, request, actor.Id, now, cancellationToken);
                if (!updated)
                {
                    return Results.NotFound();
                }
                await WriteAuditAsync(
                    context, audit, now, actor,
                    "admin.backup_control.profile.updated", "BackupControlProfile",
                    id,
                    new
                    {
                        request.Code,
                        request.AgencyCode,
                        request.Provider,
                        request.StandardId,
                        request.ConfigurationMode,
                        request.ExceptionType,
                        request.FieldOrigins,
                        State = BackupApprovalStatuses.ForSave(request)
                    },
                    cancellationToken);
                return Results.Ok(
                    await repository.ReadDetailAsync(id, cancellationToken));
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(
                    new { error = "backup_control_profile_code_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        // Reject stale dashboards: cancelling their old prompt used to send /approve.
        foreach (string retiredAction in new[] { "submit", "approve", "return" })
        {
            controls.MapPost($"/profiles/{{id:guid}}/{retiredAction}", (HttpContext context) =>
                !IsDashboardMutation(context)
                    ? Results.BadRequest(new { error = "invalid_request_origin" })
                    : Results.Conflict(new { error = "backup_control_workflow_retired_reload" }))
                .RequireAuthorization(ManagePolicy);
        }

        controls.MapPost("/profiles/{id:guid}/activate", async (
            Guid id,
            HttpContext context,
            BackupControlActivationRequest request,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) || request.ExpectedUpdatedAtUtc == default)
            {
                return Results.BadRequest(new { error = "invalid_request_origin" });
            }
            return await ActivateAsync(
                context, id, request, repository, audit, timeProvider, cancellationToken);
        }).RequireAuthorization(ManagePolicy);

        controls.MapPost("/profiles/{id:guid}/observations", async (
            Guid id,
            HttpContext context,
            BackupControlObservationRequest request,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !BackupControlInput.TryNormalize(request, out request))
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_control_observation" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (!await repository.CanAcceptObservationAsync(
                    id, request.Source, cancellationToken))
            {
                return Results.BadRequest(new
                {
                    error = "backup_control_observation_source_mismatch"
                });
            }
            try
            {
                Guid? observationId = await repository.AddObservationAsync(
                    id, request, actor.Id, now, cancellationToken);
                if (!observationId.HasValue)
                {
                    return Results.NotFound();
                }
                await WriteAuditAsync(
                    context, audit, now, actor,
                    "admin.backup_control.observation.created",
                    "BackupControlObservation", observationId.Value,
                    new
                    {
                        ProfileId = id,
                        request.Source,
                        request.Result,
                        request.PeriodEndUtc,
                        request.ExternalReference
                    },
                    cancellationToken);
                return Results.Ok(
                    await repository.ReadDetailAsync(id, cancellationToken));
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(
                    new { error = "backup_control_observation_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        controls.MapPost("/profiles/{id:guid}/restorations", async (
            Guid id,
            HttpContext context,
            BackupRestorationRequest request,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !BackupControlInput.TryNormalize(request, out request))
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_restoration" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                Guid? restorationId = await repository.AddRestorationAsync(
                    id, request, actor.Id, now, cancellationToken);
                if (!restorationId.HasValue)
                {
                    return Results.NotFound();
                }
                await WriteAuditAsync(
                    context, audit, now, actor,
                    "admin.backup_control.restoration.created",
                    "BackupRestorationTest", restorationId.Value,
                    new
                    {
                        ProfileId = id,
                        request.Modality,
                        request.Result,
                        request.FinishedAtUtc,
                        request.IntegrityVerified,
                        request.FunctionalityVerified
                    },
                    cancellationToken);
                return Results.Ok(
                    await repository.ReadDetailAsync(id, cancellationToken));
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_restoration_case" });
            }
        }).RequireAuthorization(ManagePolicy);

        controls.MapPost("/profiles/{id:guid}/evidence", async (
            Guid id,
            HttpContext context,
            BackupEvidenceService evidence,
            BackupControlRepository repository,
            AdminRepository audit,
            TimeProvider timeProvider,
            ILogger<BackupEvidenceService> logger,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TryReadEvidenceMetadata(context, out BackupEvidenceUploadMetadata metadata))
            {
                return Results.BadRequest(
                    new { error = "invalid_backup_evidence_metadata" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                BackupEvidenceUploadResult? result = await evidence.UploadAsync(
                    id, metadata, context.Request.Body, actor.Id, now,
                    cancellationToken);
                if (result is null)
                {
                    return Results.NotFound();
                }
                await WriteAuditAsync(
                    context, audit, now, actor,
                    "admin.backup_control.evidence.uploaded",
                    "BackupControlEvidence", result.Id,
                    new
                    {
                        ProfileId = id,
                        result.OriginalFileName,
                        result.MediaType,
                        result.SizeBytes,
                        result.Sha256,
                        metadata.EvidenceType
                    },
                    cancellationToken);
                return Results.Ok(
                    await repository.ReadDetailAsync(id, cancellationToken));
            }
            catch (BackupEvidenceException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Code,
                    message = exception.Message
                });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    exception,
                    "No fue posible conservar evidencia del control {ProfileId}.",
                    id);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "No fue posible conservar la evidencia.");
            }
        }).RequireAuthorization(ManagePolicy);
    }

    private static async Task<IResult> ActivateAsync(
        HttpContext context,
        Guid id,
        BackupControlActivationRequest request,
        BackupControlRepository repository,
        AdminRepository audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Actor actor = ReadActor(context);
        DateTimeOffset now = timeProvider.GetUtcNow();
        BackupProfileDecisionResult result = await repository.ActivateAsync(
            id, request.ExpectedUpdatedAtUtc, actor.Id, now, cancellationToken);
        if (result == BackupProfileDecisionResult.NotFound)
        {
            return Results.NotFound();
        }
        if (result == BackupProfileDecisionResult.InvalidTransition)
        {
            return Results.Conflict(
                new { error = "invalid_backup_control_activation_transition" });
        }
        if (result == BackupProfileDecisionResult.NormativeException)
        {
            return Results.Conflict(new
            {
                error = "backup_control_normative_exception_requires_formal_authority"
            });
        }
        await WriteAuditAsync(
            context, audit, now, actor,
            "admin.backup_control.profile.activated",
            "BackupControlProfile", id,
            new { State = BackupApprovalStatuses.Active, request.ExpectedUpdatedAtUtc },
            cancellationToken);
        return Results.Ok(await repository.ReadDetailAsync(id, cancellationToken));
    }

    private static bool TryReadEvidenceMetadata(
        HttpContext context,
        out BackupEvidenceUploadMetadata metadata)
    {
        string? fileName = DecodeHeader(context, "X-Atlas-File-Name");
        string evidenceType = context.Request.Headers["X-Atlas-Evidence-Type"]
            .ToString();
        string evidenceAtText = context.Request.Headers["X-Atlas-Evidence-At"]
            .ToString();
        bool validDate = DateTimeOffset.TryParse(
            evidenceAtText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset evidenceAt);
        metadata = new BackupEvidenceUploadMetadata(
            fileName ?? string.Empty,
            evidenceType,
            ParseOptionalGuid(context, "X-Atlas-Observation-Id"),
            ParseOptionalGuid(context, "X-Atlas-Restoration-Id"),
            ParseOptionalGuid(context, "X-Atlas-Case-Id"),
            evidenceAt,
            DecodeHeader(context, "X-Atlas-Evidence-Description"),
            DecodeHeader(context, "X-Atlas-Evidence-Source"));
        return validDate &&
               BackupControlInput.TryNormalizeEvidence(metadata, out metadata);
    }

    private static string? DecodeHeader(HttpContext context, string name)
    {
        string value = context.Request.Headers[name].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static Guid? ParseOptionalGuid(HttpContext context, string name)
    {
        string value = context.Request.Headers[name].ToString();
        return Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
            ? parsed
            : null;
    }

    private static bool IsDashboardMutation(HttpContext context) =>
        string.Equals(
            context.Request.Headers["X-Atlas-Request"].ToString(),
            "dashboard",
            StringComparison.Ordinal);

    private static Actor ReadActor(HttpContext context) =>
        new(
            context.User.Identity?.Name ?? "unknown",
            context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown");

    private static Task WriteAuditAsync(
        HttpContext context,
        AdminRepository repository,
        DateTimeOffset now,
        Actor actor,
        string action,
        string targetType,
        Guid targetId,
        object detail,
        CancellationToken cancellationToken) =>
        repository.WriteAuditAsync(
            now,
            actor.Role,
            actor.Id,
            action,
            targetType,
            targetId.ToString("D"),
            new
            {
                detail,
                remoteAddress =
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            },
            cancellationToken);

    private sealed record Actor(string Id, string Role);
}
