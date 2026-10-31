using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Cases;

public static class CaseEndpointExtensions
{
    public const string ManagePolicy = "AtlasCaseManage";

    public static void MapCaseEndpoints(this WebApplication app)
    {
        RouteGroupBuilder cases = app.MapGroup("/api/v1/admin/cases")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy)
            .RequireRateLimiting("admin");

        cases.MapGet("", (
            string? search,
            string? type,
            string? status,
            string? severity,
            string? agency,
            int? page,
            int? pageSize,
            CaseRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            repository.ReadPageAsync(
                search, type, status, severity, agency,
                page ?? 1, pageSize ?? 20,
                timeProvider.GetUtcNow(), cancellationToken));

        cases.MapGet("/by-source", async (
            string sourceKind,
            string sourceKey,
            CaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            string kind = (sourceKind ?? string.Empty).Trim().ToUpperInvariant();
            string key = (sourceKey ?? string.Empty).Trim();
            if (kind.Length is 0 or > 32 || key.Length is 0 or > 255)
            {
                return Results.BadRequest(new { error = "invalid_case_source" });
            }
            AdminCaseDetail? detail = await repository.ReadOpenBySourceAsync(
                kind, key, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        cases.MapGet("/{id:guid}", async (
            Guid id,
            CaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            AdminCaseDetail? detail = await repository.ReadDetailAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        cases.MapPost("", async (
            HttpContext context,
            AdminCaseCreateRequest request,
            CaseRepository casesRepository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !CaseInput.TryNormalize(request, out AdminCaseCreateRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_case" });
            }

            string actor = context.User.Identity?.Name ?? "unknown";
            string role = context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                if (normalized.SourceKind is not null && normalized.SourceKey is not null)
                {
                    AdminCaseDetail? existing = await casesRepository.ReadOpenBySourceAsync(
                        normalized.SourceKind, normalized.SourceKey, cancellationToken);
                    if (existing is not null)
                    {
                        return Results.Conflict(new
                        {
                            error = "case_source_exists",
                            detail = existing.Case.Id
                        });
                    }
                }
                AdminCaseDetail created = await casesRepository.CreateAsync(
                    normalized, actor, role, now, cancellationToken);
                await WriteAuditAsync(
                    context, auditRepository, now, role, actor,
                    "admin.case.created", created.Case.Id, created.Case.CaseNumber,
                    cancellationToken);
                return Results.Created($"/api/v1/admin/cases/{created.Case.Id:D}", created);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = "invalid_case_reference" });
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "case_source_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        cases.MapPut("/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminCaseUpdateRequest request,
            CaseRepository casesRepository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !CaseInput.TryNormalize(request, out AdminCaseUpdateRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_case" });
            }

            string actor = context.User.Identity?.Name ?? "unknown";
            string role = context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
            DateTimeOffset now = timeProvider.GetUtcNow();
            try
            {
                AdminCaseDetail? updated = await casesRepository.UpdateAsync(
                    id, normalized, actor, role, now, cancellationToken);
                if (updated is null) return Results.NotFound();
                await WriteAuditAsync(
                    context, auditRepository, now, role, actor,
                    "admin.case.updated", id, updated.Case.CaseNumber,
                    cancellationToken);
                return Results.Ok(updated);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = "invalid_case_reference" });
            }
        }).RequireAuthorization(ManagePolicy);

        cases.MapPost("/{id:guid}/comments", async (
            Guid id,
            HttpContext context,
            AdminCaseCommentRequest request,
            CaseRepository casesRepository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string? comment = CaseInput.NormalizeComment(request.Comment);
            if (!IsDashboardMutation(context) || comment is null)
            {
                return Results.BadRequest(new { error = "invalid_comment" });
            }
            string actor = context.User.Identity?.Name ?? "unknown";
            string role = context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
            DateTimeOffset now = timeProvider.GetUtcNow();
            AdminCaseDetail? updated = await casesRepository.AddCommentAsync(
                id, comment, actor, role, now, cancellationToken);
            if (updated is null) return Results.NotFound();
            await WriteAuditAsync(
                context, auditRepository, now, role, actor,
                "admin.case.commented", id, updated.Case.CaseNumber,
                cancellationToken);
            return Results.Ok(updated);
        }).RequireAuthorization(ManagePolicy);
    }

    private static bool IsDashboardMutation(HttpContext context) =>
        string.Equals(
            context.Request.Headers["X-Atlas-Request"].ToString(),
            "dashboard",
            StringComparison.Ordinal);

    private static Task WriteAuditAsync(
        HttpContext context,
        AdminRepository repository,
        DateTimeOffset now,
        string role,
        string actor,
        string action,
        Guid id,
        string caseNumber,
        CancellationToken cancellationToken) =>
        repository.WriteAuditAsync(
            now,
            role,
            actor,
            action,
            "OperationalCase",
            id.ToString("D"),
            new
            {
                caseNumber,
                remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            },
            cancellationToken);
}
