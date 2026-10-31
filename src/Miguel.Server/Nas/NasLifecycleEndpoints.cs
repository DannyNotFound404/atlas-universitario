using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Nas;

public static class NasLifecycleEndpoints
{
    public static async ValueTask<object?> GuardBindings(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (PostgresException ex) when (ex.SqlState == "23514" && ex.ConstraintName == "active_nas_binding")
        { return Results.Conflict(new { error = "nas_inactive", detail = "El destino está dado de baja. Selecciona un NAS activo o reactívalo antes de asignarlo." }); }
    }

    public static void MapNasLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/nas").RequireRateLimiting("admin");
        group.MapGet("/{id:guid}/lifecycle", async (Guid id, string? scope, NasLifecycleService service, CancellationToken ct) =>
        {
            try { var preview = await service.Preview(id, scope ?? "RESOURCE", ct); return preview is null ? Results.NotFound() : Results.Ok(preview); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy);
        group.MapPost("/{id:guid}/lifecycle", async (Guid id, NasLifecycleRequest request, HttpContext context,
            NasLifecycleService service, AdminDashboardService dashboard, CancellationToken ct) =>
        {
            if (context.Request.Headers["X-Atlas-Request"] != "dashboard") return Results.BadRequest(new { error = "invalid_request_origin" });
            try
            {
                int changed = await service.Change(id, request, context.User.Identity?.Name ?? "unknown",
                    context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown", context.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
                dashboard.Invalidate(); return Results.Ok(new { changed, enabled = request.Enabled });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (NasLifecycleConflict ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (PostgresException ex) when (ex.SqlState == "23505") { return Results.Conflict(new { error = "nas_reactivation_conflict" }); }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01") { return Results.Conflict(new { error = "nas_lifecycle_changed" }); }
        }).RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireRole("Administrator"));
    }
}
