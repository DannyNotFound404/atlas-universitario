using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Training;

public static class TrainingEndpointExtensions
{
    public const string ManagePolicy = "AtlasTrainingManage";

    public static void MapTrainingEndpoints(this WebApplication app)
    {
        RouteGroupBuilder training = app.MapGroup("/api/v1/admin/training")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy)
            .RequireRateLimiting("admin");

        training.MapGet("", (
            Guid? cycleId,
            TrainingRepository repository,
            CancellationToken cancellationToken) =>
            repository.ReadDashboardAsync(cycleId, cancellationToken));

        training.MapGet("/sessions/{id:guid}", async (
            Guid id,
            TrainingRepository repository,
            CancellationToken cancellationToken) =>
        {
            AdminTrainingSessionDetail? detail =
                await repository.ReadSessionAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        training.MapPost("/cycles", async (
            HttpContext context,
            AdminTrainingCycleRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingCycleRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_cycle" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingCycleView created = await repository.CreateCycleAsync(
                    normalized, actor.Id, now, cancellationToken);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.cycle.created", "TrainingCycle", created.Id,
                    new { created.Name, created.Status }, cancellationToken);
                return Results.Created(
                    $"/api/v1/admin/training?cycleId={created.Id:D}", created);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_cycle_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPut("/cycles/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminTrainingCycleRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingCycleRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_cycle" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingCycleView? updated = await repository.UpdateCycleAsync(
                    id, normalized, actor.Id, now, cancellationToken);
                if (updated is null)
                {
                    return Results.Conflict(new { error = "training_cycle_range_conflict" });
                }
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.cycle.updated", "TrainingCycle", updated.Id,
                    new { updated.Name, updated.Status }, cancellationToken);
                return Results.Ok(updated);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_cycle_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPost("/cycles/{id:guid}/clone", async (
            Guid id,
            HttpContext context,
            AdminTrainingCycleCloneRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingCycleCloneRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_cycle_clone" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingCycleView? created = await repository.CloneCycleAsync(
                    id, normalized, actor.Id, now, cancellationToken);
                if (created is null) return Results.NotFound();
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.cycle.cloned", "TrainingCycle", created.Id,
                    new { sourceCycleId = id, created.Name }, cancellationToken);
                return Results.Created(
                    $"/api/v1/admin/training?cycleId={created.Id:D}", created);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_cycle_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPost("/topics", async (
            HttpContext context,
            AdminTrainingTopicRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingTopicRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_topic" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingTopicView created = await repository.CreateTopicAsync(
                    normalized, actor.Id, now, cancellationToken);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.topic.created", "TrainingTopic", created.Id,
                    new { created.Code, created.Title }, cancellationToken);
                return Results.Created("/api/v1/admin/training", created);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_topic_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPut("/topics/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminTrainingTopicRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingTopicRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_topic" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingTopicView? updated = await repository.UpdateTopicAsync(
                    id, normalized, actor.Id, now, cancellationToken);
                if (updated is null) return Results.NotFound();
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.topic.updated", "TrainingTopic", updated.Id,
                    new { updated.Code, updated.Title, updated.Active }, cancellationToken);
                return Results.Ok(updated);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_topic_exists" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPost("/participants", async (
            HttpContext context,
            AdminTrainingParticipantRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(
                    request, out AdminTrainingParticipantRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_participant" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingParticipantView created =
                    await repository.CreateParticipantAsync(
                        normalized, actor.Id, now, cancellationToken);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.participant.created", "TrainingParticipant",
                    created.Id, new { created.DisplayName, created.AccountName },
                    cancellationToken);
                return Results.Created("/api/v1/admin/training", created);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_participant_exists" });
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = "invalid_agency" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPut("/participants/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminTrainingParticipantRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(
                    request, out AdminTrainingParticipantRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_participant" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingParticipantView? updated =
                    await repository.UpdateParticipantAsync(
                        id, normalized, actor.Id, now, cancellationToken);
                if (updated is null) return Results.NotFound();
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.participant.updated", "TrainingParticipant",
                    updated.Id,
                    new { updated.DisplayName, updated.AccountName, updated.Active },
                    cancellationToken);
                return Results.Ok(updated);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "training_participant_exists" });
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = "invalid_agency" });
            }
        }).RequireAuthorization(ManagePolicy);

        training.MapPost("/sessions", async (
            HttpContext context,
            AdminTrainingSessionRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingSessionRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_session" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            AdminTrainingSessionView? created = await repository.CreateSessionAsync(
                normalized, actor.Id, now, cancellationToken);
            if (created is null)
            {
                return Results.BadRequest(new { error = "training_session_outside_cycle" });
            }
            await WriteAuditAsync(
                context, auditRepository, now, actor,
                "admin.training.session.created", "TrainingSession", created.Id,
                new { created.CycleId, created.Title, created.ScheduledDate },
                cancellationToken);
            return Results.Created(
                $"/api/v1/admin/training/sessions/{created.Id:D}", created);
        }).RequireAuthorization(ManagePolicy);

        training.MapPut("/sessions/{id:guid}", async (
            Guid id,
            HttpContext context,
            AdminTrainingSessionRequest request,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context) ||
                !TrainingInput.TryNormalize(request, out AdminTrainingSessionRequest normalized))
            {
                return Results.BadRequest(new { error = "invalid_training_session" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            AdminTrainingSessionView? updated = await repository.UpdateSessionAsync(
                id, normalized, actor.Id, now, cancellationToken);
            if (updated is null)
            {
                return Results.BadRequest(new { error = "training_session_outside_cycle" });
            }
            await WriteAuditAsync(
                context, auditRepository, now, actor,
                "admin.training.session.updated", "TrainingSession", updated.Id,
                new
                {
                    updated.CycleId,
                    updated.Title,
                    updated.ScheduledDate,
                    updated.Status
                },
                cancellationToken);
            return Results.Ok(updated);
        }).RequireAuthorization(ManagePolicy);

        training.MapDelete("/sessions/{id:guid}", async (
            Guid id,
            HttpContext context,
            TrainingRepository repository,
            AdminRepository auditRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(new { error = "invalid_training_request" });
            }
            TrainingSessionDeleteResult result =
                await repository.DeleteSessionAsync(id, cancellationToken);
            if (result == TrainingSessionDeleteResult.NotFound)
            {
                return Results.NotFound();
            }
            if (result == TrainingSessionDeleteResult.CompletedSession)
            {
                return Results.Conflict(
                    new { error = "completed_training_session_is_protected" });
            }
            Actor actor = ReadActor(context);
            DateTimeOffset now = timeProvider.GetUtcNow();
            await WriteAuditAsync(
                context, auditRepository, now, actor,
                "admin.training.session.deleted", "TrainingSession", id,
                new { }, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ManagePolicy);

        training.MapPost("/sessions/{id:guid}/attendance-imports", async (
            Guid id,
            HttpContext context,
            TrainingAttendanceImportService imports,
            AdminRepository auditRepository,
            ILogger<TrainingAttendanceImportService> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!IsDashboardMutation(context))
            {
                return Results.BadRequest(new { error = "invalid_training_request" });
            }
            string encodedFileName = context.Request.Headers["X-Atlas-File-Name"]
                .ToString();
            if (string.IsNullOrWhiteSpace(encodedFileName))
            {
                return Results.BadRequest(new { error = "missing_attendance_file_name" });
            }
            string fileName;
            try
            {
                fileName = Uri.UnescapeDataString(encodedFileName);
            }
            catch (UriFormatException)
            {
                return Results.BadRequest(new { error = "invalid_attendance_file_name" });
            }
            try
            {
                Actor actor = ReadActor(context);
                DateTimeOffset now = timeProvider.GetUtcNow();
                AdminTrainingSessionDetail? detail =
                    await imports.ImportAsync(
                        id,
                        context.Request.Body,
                        fileName,
                        context.Request.ContentType,
                        actor.Id,
                        now,
                        cancellationToken);
                if (detail is null) return Results.NotFound();
                AdminTrainingAttendanceImportView current = detail.Imports
                    .First(item => item.IsCurrent);
                await WriteAuditAsync(
                    context, auditRepository, now, actor,
                    "admin.training.attendance.imported", "TrainingSession", id,
                    new
                    {
                        current.OriginalFileName,
                        current.Sha256,
                        current.SizeBytes,
                        current.SourceRowCount,
                        current.AcceptedRowCount,
                        current.DuplicateRowCount,
                        current.RejectedRowCount
                    },
                    cancellationToken);
                return Results.Ok(detail);
            }
            catch (TrainingAttendanceImportException exception)
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
                    "No fue posible conservar la evidencia de asistencia para {SessionId}.",
                    id);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "No fue posible conservar la evidencia de asistencia.");
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
