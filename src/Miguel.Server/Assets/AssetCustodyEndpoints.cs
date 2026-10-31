using System.Security.Claims;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Assets;

public static class AssetCustodyEndpoints
{
    public static void MapAssetCustodyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/devices").RequireRateLimiting("admin");
        group.MapGet("/custody/proposals", async (Guid? after,string? agency,AssetCustodyService service,CancellationToken ct) =>
        {
            try{return Results.Ok(await service.ReadProposals(after,agency,ct));}
            catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
        }).RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy);
        group.MapPost("/custody/register-many",async (AssetCustodyBulkRequest request,HttpContext context,
            AssetCustodyService service,AdminDashboardService dashboard,CancellationToken ct) =>
        {
            if(context.Request.Headers["X-Atlas-Request"]!="dashboard")return Results.BadRequest(new{error="invalid_request_origin"});
            try
            {
                var results=await service.RegisterMany(request,context.User.Identity?.Name??"unknown",context.User.FindFirstValue(ClaimTypes.Role)??"Unknown",ct);
                return Results.Ok(new{results});
            }
            catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
            finally{dashboard.Invalidate();}
        }).RequireAuthorization(p=>p.RequireAuthenticatedUser().RequireRole("Administrator"));
        group.MapGet("/custody", async (string ids, AssetCustodyService service, CancellationToken ct) =>
        {
            string[] tokens = ids.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 1 or > 100 || tokens.Any(t => !Guid.TryParse(t, out _))) return Results.BadRequest();
            return Results.Ok(await service.ReadSummaries(tokens.Select(Guid.Parse).Distinct().ToArray(), ct));
        }).RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy);
        group.MapGet("/{id:guid}/custody", async (Guid id, AssetCustodyService service, CancellationToken ct) =>
        {
            var view = await service.Read(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy);
        group.MapPost("/{id:guid}/custody", async (Guid id, AssetCustodyRequest request, HttpContext context,
            AssetCustodyService service, AdminDashboardService dashboard, CancellationToken ct) =>
        {
            if (context.Request.Headers["X-Atlas-Request"] != "dashboard") return Results.BadRequest(new { error = "invalid_request_origin" });
            try
            {
                await service.Change(id, request, context.User.Identity?.Name ?? "unknown",
                    context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown", ct);
                dashboard.Invalidate(); return Results.Ok(new { saved = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (AssetCustodyConflict ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" or "23505")
            { return Results.Conflict(new { error = "custody_changed" }); }
        }).RequireAuthorization(p => p.RequireAuthenticatedUser().RequireRole("Administrator"));
    }
}
