using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Governance;

public static class GovernanceEndpointExtensions
{
    public const string ManagePolicy = "AtlasGovernanceManage";
    public const string ApprovePolicy = "AtlasGovernanceApprove";

    public static void MapGovernanceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder governance = app.MapGroup("/api/v1/admin/governance")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy)
            .RequireRateLimiting("admin");

        governance.MapGet("", (
            GovernanceRepository repository,
            CancellationToken cancellationToken) =>
            repository.ReadDashboardAsync(cancellationToken));

        governance.MapGet("/documents/{id:guid}", async (
            Guid id,
            GovernanceRepository repository,
            CancellationToken cancellationToken) =>
        {
            AdminGovernanceDocumentDetail? detail =
                await repository.ReadDocumentAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        governance.MapGet("/versions/{id:guid}/preview", async (
            Guid id,
            HttpContext context,
            GovernancePreviewService previews,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            ILogger<GovernancePreviewService> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                GovernancePreviewDocument? preview =
                    await previews.BuildAsync(id, cancellationToken);
                if (preview is null)
                {
                    return Results.NotFound();
                }
                context.Response.Headers.CacheControl = "private, no-store";
                Actor actor = ReadActor(context);
                await WriteAuditAsync(
                    context,
                    auditRepository,
                    timeProvider.GetUtcNow(),
                    actor,
                    "admin.governance.version.previewed",
                    "GovernanceDocumentVersion",
                    id,
                    new
                    {
                        preview.OriginalFileName,
                        preview.Kind,
                        preview.Truncated
                    },
                    cancellationToken);
                return Results.Ok(preview);
            }
            catch (GovernancePreviewException exception)
            {
                return Results.UnprocessableEntity(new
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
                    "No fue posible generar la vista documental {VersionId}.",
                    id);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "No fue posible generar la vista documental.");
            }
        });

        governance.MapGet("/versions/{id:guid}/inline", async (
            Guid id,
            GovernanceDocumentService documents,
            CancellationToken cancellationToken) =>
        {
            GovernanceStoredFile? file =
                await documents.ResolveVersionFileAsync(id, cancellationToken);
            if (file is null)
            {
                return Results.NotFound();
            }
            if (!string.Equals(
                    Path.GetExtension(file.OriginalFileName),
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    file.MediaType,
                    "application/pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.UnprocessableEntity(new
                {
                    error = "unsupported_governance_inline_preview"
                });
            }
            return Results.File(
                file.PhysicalPath,
                "application/pdf",
                enableRangeProcessing: true);
        });

        governance.MapGet("/versions/{id:guid}/download", async (
            Guid id,
            HttpContext context,
            GovernanceDocumentService documents,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            GovernanceStoredFile? file =
                await documents.ResolveVersionFileAsync(id, cancellationToken);
            if (file is null)
            {
                return Results.NotFound();
            }
            Actor actor = ReadActor(context);
            await WriteAuditAsync(
                context, auditRepository, timeProvider.GetUtcNow(), actor,
                "admin.governance.version.downloaded", "GovernanceDocumentVersion", id,
                new { file.OriginalFileName }, cancellationToken);
            return Results.File(
                file.PhysicalPath,
                file.MediaType,
                file.OriginalFileName,
                enableRangeProcessing: true);
        });

        governance.MapPost("/documents", async (
            HttpContext context,
            AdminGovernanceDocumentRequest request,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceDocumentRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_governance_document" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminGovernanceDocumentDetail created =
                    await repository.CreateDocumentAsync(
                        normalized, actor.Id, now, cancellationToken);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.governance.document.created", "GovernanceDocument",
                    created.Document.Id,
                    new
                    {
                        created.Document.Code,
                        created.Document.Title,
                        created.Document.DocumentType
                    },
                    cancellationToken);
                return Results.Created(
                    $"/api/v1/admin/governance/documents/{created.Document.Id:D}",
                    created);
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { error = "governance_document_code_exists" });
            }
            catch (GovernanceHierarchyException exception)
            {
                return Results.BadRequest(new { error = exception.ErrorCode });
            }
            catch (PostgresException exception) when (exception.SqlState == "23503")
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_relationship" });
            }
            catch (PostgresException exception) when (
                exception.SqlState == "23514" &&
                exception.ConstraintName == "ck_governance_document_hierarchy_depth")
            {
                return Results.BadRequest(
                    new { error = "governance_document_hierarchy_too_deep" });
            }
            catch (PostgresException exception) when (exception.SqlState == "23514")
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_relationship" });
            }
        }).RequireAuthorization(ManagePolicy);

        governance.MapPut("/documents/{id:guid}/description", async (
            Guid id,
            HttpContext context,
            AdminGovernanceDescriptionRequest request,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceDescriptionRequest normalized))
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_description" });
            }

            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            GovernanceDescriptionUpdateOutcome outcome =
                await repository.UpdateDescriptionAsync(
                    id, normalized, actor.Id, now, cancellationToken);
            if (outcome.Result == GovernanceDescriptionUpdateResult.NotFound)
            {
                return Results.NotFound();
            }
            if (outcome.Result ==
                GovernanceDescriptionUpdateResult.RequiresApprovedDocument)
            {
                return Results.Conflict(
                    new { error = "governance_description_requires_approved_document" });
            }

            AdminGovernanceDocumentDetail detail =
                (await repository.ReadDocumentAsync(id, cancellationToken))!;
            await WriteAuditAsync(
                context,
                auditRepository,
                now,
                actor,
                "admin.governance.document.description_updated",
                "GovernanceDocument",
                id,
                new
                {
                    Fields = new[] { "summary", "objective", "scope" },
                    Before = outcome.Previous,
                    After = normalized,
                    Preserved = new
                    {
                        detail.Document.Code,
                        detail.Document.Title,
                        detail.Document.DocumentType,
                        detail.Document.Status,
                        detail.Document.CurrentVersionId,
                        detail.Document.CurrentVersionLabel,
                        detail.Document.CurrentFileName,
                        detail.Document.CurrentSha256,
                        detail.Document.ApproverName,
                        detail.Document.EffectiveDate,
                        detail.Document.NextReviewDate,
                        detail.Document.ParentDocumentId
                    }
                },
                cancellationToken);
            return Results.Ok(detail);
        }).RequireAuthorization(ManagePolicy);

        governance.MapPut("/documents/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminGovernanceDocumentRequest request,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceDocumentRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_governance_document" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                GovernanceDocumentUpdateResult result =
                    await repository.UpdateDocumentAsync(
                        id, normalized, actor.Id, now, cancellationToken);
                if (result == GovernanceDocumentUpdateResult.NotFound)
                {
                    return Results.NotFound();
                }
                if (result == GovernanceDocumentUpdateResult.Locked)
                {
                    return Results.Conflict(
                        new { error = "approved_governance_document_is_locked" });
                }
                if (result == GovernanceDocumentUpdateResult.InvalidRelationship)
                {
                    return Results.BadRequest(
                        new { error = "invalid_governance_document_relationship" });
                }
                if (result == GovernanceDocumentUpdateResult.HierarchyTooDeep)
                {
                    return Results.BadRequest(
                        new { error = "governance_document_hierarchy_too_deep" });
                }
                AdminGovernanceDocumentDetail detail =
                    (await repository.ReadDocumentAsync(id, cancellationToken))!;
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.governance.document.updated", "GovernanceDocument", id,
                    new { detail.Document.Code, detail.Document.Title },
                    cancellationToken);
                return Results.Ok(detail);
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(new { error = "governance_document_code_exists" });
            }
            catch (PostgresException exception) when (exception.SqlState == "23503")
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_relationship" });
            }
            catch (PostgresException exception) when (
                exception.SqlState == "23514" &&
                exception.ConstraintName == "ck_governance_document_hierarchy_depth")
            {
                return Results.BadRequest(
                    new { error = "governance_document_hierarchy_too_deep" });
            }
            catch (PostgresException exception) when (exception.SqlState == "23514")
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_relationship" });
            }
        }).RequireAuthorization(ManagePolicy);

        governance.MapPost("/documents/{id:guid}/status", async (
            Guid id,
            HttpContext context,
            AdminGovernanceStatusRequest request,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceStatusRequest normalized))
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_status" });
            }
            if (normalized.Status == GovernanceDocumentStatuses.Approved)
            {
                return Results.Json(
                    new { error = "governance_approval_requires_technology_manager" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            GovernanceStatusUpdateResult result =
                await repository.UpdateStatusAsync(
                    id, normalized, actor.Id, now, cancellationToken);
            if (result == GovernanceStatusUpdateResult.NotFound)
            {
                return Results.NotFound();
            }
            if (result == GovernanceStatusUpdateResult.InvalidTransition)
            {
                return Results.Conflict(
                    new { error = "invalid_governance_status_transition" });
            }
            if (result == GovernanceStatusUpdateResult.VersionRequired)
            {
                return Results.Conflict(
                    new { error = "governance_document_version_required" });
            }
            AdminGovernanceDocumentDetail detail =
                (await repository.ReadDocumentAsync(id, cancellationToken))!;
            await WriteAuditAsync(
                context, auditRepository, now, actor,
                "admin.governance.document.status_changed", "GovernanceDocument", id,
                new
                {
                    normalized.Status,
                    normalized.ApproverName,
                    normalized.EffectiveDate,
                    normalized.NextReviewDate
                },
                cancellationToken);
            return Results.Ok(detail);
        }).RequireAuthorization(ManagePolicy);

        governance.MapPost("/documents/{id:guid}/approve", async (
            Guid id,
            HttpContext context,
            AdminGovernanceApprovalRequest request,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceApprovalRequest normalized))
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_approval_attestation" });
            }

            string approverName =
                context.User.FindFirstValue(ClaimTypes.GivenName)?.Trim() ??
                string.Empty;
            if (approverName.Length is < 3 or > 160)
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_approver_identity" });
            }

            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            GovernanceStatusUpdateResult result =
                await repository.UpdateStatusAsync(
                    id,
                    new AdminGovernanceStatusRequest(
                        GovernanceDocumentStatuses.Approved,
                        approverName,
                        normalized.EffectiveDate,
                        normalized.NextReviewDate),
                    actor.Id,
                    now,
                    cancellationToken);
            if (result == GovernanceStatusUpdateResult.NotFound)
            {
                return Results.NotFound();
            }
            if (result == GovernanceStatusUpdateResult.InvalidTransition)
            {
                return Results.Conflict(
                    new { error = "invalid_governance_status_transition" });
            }
            if (result == GovernanceStatusUpdateResult.VersionRequired)
            {
                return Results.Conflict(
                    new { error = "governance_document_version_required" });
            }
            if (result == GovernanceStatusUpdateResult.PdfRequired)
            {
                return Results.Conflict(
                    new { error = "governance_official_pdf_required" });
            }

            AdminGovernanceDocumentDetail detail =
                (await repository.ReadDocumentAsync(id, cancellationToken))!;
            await WriteAuditAsync(
                context,
                auditRepository,
                now,
                actor,
                "admin.governance.document.approved",
                "GovernanceDocument",
                id,
                new
                {
                    ApproverName = approverName,
                    normalized.EffectiveDate,
                    normalized.NextReviewDate,
                    Attestation = GovernanceInput.ApprovalAttestation,
                    AttestationAccepted = true,
                    detail.Document.CurrentVersionId,
                    detail.Document.CurrentVersionLabel,
                    detail.Document.CurrentFileName,
                    detail.Document.CurrentSha256
                },
                cancellationToken);
            return Results.Ok(detail);
        }).RequireAuthorization(ApprovePolicy);

        governance.MapPost("/documents/{id:guid}/request-correction", async (
            Guid id,
            HttpContext context,
            AdminGovernanceCorrectionRequest request,
            GovernanceDocumentService documents,
            GovernanceRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !GovernanceInput.TryNormalize(
                    request, out AdminGovernanceCorrectionRequest normalized))
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_correction_reason" });
            }

            string reviewerName =
                context.User.FindFirstValue(ClaimTypes.GivenName)?.Trim() ??
                string.Empty;
            if (reviewerName.Length is < 3 or > 160)
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_reviewer_identity" });
            }

            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            GovernanceCorrectionStorageOutcome outcome =
                await documents.RequestCorrectionAsync(
                    id, actor.Id, now, cancellationToken);
            if (outcome.Database.Result == GovernanceCorrectionResult.NotFound)
            {
                return Results.NotFound();
            }
            if (outcome.Database.Result ==
                GovernanceCorrectionResult.InvalidTransition)
            {
                return Results.Conflict(
                    new { error = "invalid_governance_status_transition" });
            }
            if (outcome.Database.Result == GovernanceCorrectionResult.VersionRequired)
            {
                return Results.Conflict(
                    new { error = "governance_document_version_required" });
            }

            await WriteAuditAsync(
                context,
                auditRepository,
                now,
                actor,
                "admin.governance.document.correction_requested",
                "GovernanceDocument",
                id,
                new
                {
                    ReviewerName = reviewerName,
                    normalized.Reason,
                    RejectedVersions = outcome.Database.RejectedVersions.Select(
                        version => new
                        {
                            version.Id,
                            version.VersionLabel,
                            version.OriginalFileName,
                            version.SizeBytes,
                            version.Sha256
                        }),
                    RejectedFilesPurged = outcome.FilesPurged,
                    outcome.PurgeFailures
                },
                cancellationToken);

            if (!outcome.FilesPurged)
            {
                return Results.Json(
                    new { error = "governance_rejected_file_purge_failed" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            AdminGovernanceDocumentDetail detail =
                (await repository.ReadDocumentAsync(id, cancellationToken))!;
            return Results.Ok(detail);
        }).RequireAuthorization(ApprovePolicy);

        governance.MapPost("/documents/{id:guid}/versions", async (
            Guid id,
            HttpContext context,
            GovernanceDocumentService documents,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            ILogger<GovernanceDocumentService> logger,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_request" });
            }
            string encodedFileName =
                context.Request.Headers["X-Atlas-File-Name"].ToString();
            string encodedVersionLabel =
                context.Request.Headers["X-Atlas-Document-Version"].ToString();
            string encodedChangeSummary =
                context.Request.Headers["X-Atlas-Change-Summary"].ToString();
            if (string.IsNullOrWhiteSpace(encodedFileName) ||
                string.IsNullOrWhiteSpace(encodedVersionLabel) ||
                string.IsNullOrWhiteSpace(encodedChangeSummary))
            {
                return Results.BadRequest(
                    new { error = "missing_governance_document_metadata" });
            }
            string fileName;
            string versionLabel;
            string changeSummary;
            try
            {
                fileName = Uri.UnescapeDataString(encodedFileName);
                versionLabel = Uri.UnescapeDataString(encodedVersionLabel);
                changeSummary = Uri.UnescapeDataString(encodedChangeSummary);
            }
            catch (UriFormatException)
            {
                return Results.BadRequest(
                    new { error = "invalid_governance_document_file_name" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminGovernanceDocumentDetail? detail =
                    await documents.UploadVersionAsync(
                        id,
                        context.Request.Body,
                        fileName,
                        context.Request.ContentType,
                        versionLabel,
                        changeSummary,
                        actor.Id,
                        now,
                        cancellationToken);
                if (detail is null)
                {
                    return Results.NotFound();
                }
                AdminGovernanceVersionView current =
                    detail.Versions.First(item => item.IsCurrent);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.governance.version.uploaded", "GovernanceDocument", id,
                    new
                    {
                        current.VersionLabel,
                        current.OriginalFileName,
                        current.SizeBytes,
                        current.Sha256
                    },
                    cancellationToken);
                return Results.Ok(detail);
            }
            catch (GovernanceDocumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Code,
                    message = exception.Message
                });
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                return Results.Conflict(
                    new { error = "governance_document_version_exists" });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    exception,
                    "No fue posible conservar la versión documental para {DocumentId}.",
                    id);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "No fue posible conservar el documento.");
            }
        }).RequireAuthorization(ManagePolicy);
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
