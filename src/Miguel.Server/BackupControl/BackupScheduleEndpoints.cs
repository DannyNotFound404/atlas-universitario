using Miguel.Server.Admin;
using Miguel.Server.Reports;

namespace Miguel.Server.BackupControl;

public static class BackupScheduleEndpoints
{
    public static void MapBackupScheduleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/backup-controls/schedules")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy).RequireRateLimiting("admin");
        group.AddEndpointFilter(PortalEndpoints.Guard);
        group.MapGet("", (BackupScheduleService service, CancellationToken ct) => service.ReadAsync(ct));
        foreach (var (segment, type) in new[] { ("agencies", "AGENCY"), ("plans", "PLAN"), ("profiles", "PROFILE") })
        {
            string scope = type;
            group.MapPut("/" + segment + "/{key}", async (string key, BackupScheduleChange request,
                HttpContext context, BackupScheduleService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.SaveAsync(scope, key, request, PortalEndpoints.Actor(context), ct)); }
                catch (BackupWorkspaceException e) { return Results.Json(new { error = e.Message }, statusCode: e.Conflict ? 409 : 400); }
                catch (PortalException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
            }).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        }
    }
}
