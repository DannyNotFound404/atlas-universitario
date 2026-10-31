using Miguel.Server.Admin;
using static Miguel.Server.Admin.AdminEndpointExtensions;
namespace Miguel.Server.NetworkDiscovery;

public static class NetworkDiscoveryEndpoints
{
    public static void MapNetworkDiscoveryEndpoints(this IEndpointRouteBuilder adminApi)
    {
        adminApi.MapGet("/network-discovery", (
            string? scope,
            string? search,
            int? page,
            int? pageSize,
            string? presence,string? sort,bool? descending,Guid? runId,
            NetworkDiscoveryRepository network,
            NetworkDiscoveryService discovery,
            CancellationToken cancellationToken) =>
            network.ReadDashboardAsync(
                discovery.Enabled,
                discovery.AutomaticEnabled,
                discovery.EngineAvailable,
                discovery.ScanInProgress,
                discovery.NextAutomaticScanAtUtc,
                scope,
                search,
                page ?? 1,
                pageSize ?? 25,
                cancellationToken,discovery.IcmpAvailable,presence,sort,descending??false,runId))
            .RequireAuthorization(OperationsReadPolicy);

        adminApi.MapPost("/network-discovery/scans", async (
            HttpContext context,
            QueueNetworkScanRequest request,
            NetworkDiscoveryService discovery,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!Mutation(context))
            {
                return Results.BadRequest(new { error = "invalid_request_origin" });
            }

            string actor = context.User.Identity?.Name ?? "unknown";
            QueueNetworkScanResponse? result;
            try { result=await discovery.QueueAsync(request.ScopeCode,actor,cancellationToken,request.StartAddress,request.EndAddress,request.FullIdentification); }
            catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
            if (result is null)
            {
                return Results.Conflict(new
                {
                    error = discovery.Enabled
                        ? discovery.EngineAvailable
                            ? "scan_already_running"
                            : "network_probe_unavailable"
                        : "network_discovery_disabled"
                });
            }
            if (result.Queued == 0 && result.AlreadyQueued == 0)
            {
                return Results.NotFound(new { error = "network_scope_not_found" });
            }

            await repository.WriteAuditAsync(
                timeProvider.GetUtcNow(),
                "Administrator",
                actor,
                "admin.network.scan.queued",
                "NetworkScope",
                request.ScopeCode?.Trim().ToUpperInvariant() ?? "ALL",
                new
                {
                    result.Queued,
                    result.RunIds,
                    request.StartAddress,request.EndAddress,request.FullIdentification,
                    remoteAddress =
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                },
                cancellationToken);
            return Results.Accepted(value: result);
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapGet("/network-discovery/runs",(string? scope,NetworkDiscoveryRepository network,CancellationToken ct)=>network.ReadRunsAsync(scope,ct))
            .RequireAuthorization(OperationsReadPolicy);
        adminApi.MapGet("/network-discovery/scans/{id:guid}/progress",async(Guid id,NetworkDiscoveryRepository network,CancellationToken ct)=>
            await network.ReadProgressAsync(id,ct) is { } result?Results.Ok(result):Results.NotFound())
            .RequireAuthorization(OperationsReadPolicy);
        adminApi.MapGet("/network-discovery/export",async(string? scope,string? search,string? presence,string? sort,bool? descending,Guid? runId,
            NetworkDiscoveryRepository network,TimeProvider clock,CancellationToken ct)=>
            Results.File(await network.ExportScannerCsvAsync(scope,search,presence,sort,descending??false,runId,clock.GetUtcNow(),ct),
                "text/csv; charset=utf-8","atlas-redes.csv"))
            .RequireAuthorization(OperationsReadPolicy);
        adminApi.MapGet("/network-discovery/assets/{id:guid}/history",(Guid id,NetworkDiscoveryRepository network,CancellationToken ct)=>network.ReadEventsAsync(id,ct))
            .RequireAuthorization(OperationsReadPolicy);
        adminApi.MapPut("/network-discovery/scopes/{code}",async(string code,NetworkScopeSettingsRequest request,HttpContext context,
            NetworkDiscoveryRepository network,TimeProvider clock,CancellationToken ct)=>
        {
            if(!Mutation(context))return Results.BadRequest(new{error="invalid_request_origin"});
            try{return await network.SaveScopeAsync(code,request,context.User.Identity?.Name??"unknown",clock.GetUtcNow(),ct)?Results.Ok(new{saved=true}):Results.NotFound();}
            catch(ArgumentException){return Results.BadRequest(new{error="invalid_network_schedule"});}
            catch(InvalidOperationException ex) when(ex.Message=="network_revision_conflict"){return Results.Conflict(new{error=ex.Message});}
        }).RequireAuthorization(ProvisionPolicy);
        adminApi.MapPost("/network-discovery/scans/{id:guid}/cancel",async(Guid id,HttpContext context,
            NetworkDiscoveryRepository network,AdminRepository audit,TimeProvider clock,CancellationToken ct)=>
        {
            if(!Mutation(context))return Results.BadRequest(new{error="invalid_request_origin"});
            if(!await network.CancelRunAsync(id,clock.GetUtcNow(),ct))return Results.Conflict(new{error="network_scan_already_finished"});
            await audit.WriteAuditAsync(clock.GetUtcNow(),"Administrator",context.User.Identity?.Name??"unknown","admin.network.scan.cancelled","NetworkScan",id.ToString(),new{id},ct);
            return Results.Ok(new{cancelled=true});
        }).RequireAuthorization(ProvisionPolicy);

        adminApi.MapPut("/network-discovery/assets/{assetId:guid}", async (
            HttpContext context,
            Guid assetId,
            UpdateNetworkAssetRequest request,
            NetworkDiscoveryRepository network,
            AdminRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            string kind = request.DeviceKind?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!Mutation(context) ||
                !NetworkDeviceKinds.Contains(kind) ||
                request.DisplayName?.Length > 255)
            {
                return Results.BadRequest(new { error = "invalid_network_asset" });
            }

            string actor = context.User.Identity?.Name ?? "unknown";
            DateTimeOffset now = timeProvider.GetUtcNow();
            bool updated = await network.UpdateAssetAsync(
                assetId, kind, request.DisplayName, actor, now, cancellationToken);
            if (!updated)
            {
                return Results.NotFound(new { error = "network_asset_not_found" });
            }
            await repository.WriteAuditAsync(
                now,
                "Administrator",
                actor,
                "admin.network.asset.classified",
                "NetworkAsset",
                assetId.ToString("D"),
                new { deviceKind = kind, request.DisplayName },
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ProvisionPolicy);

    }
    private static bool Mutation(HttpContext context)=>context.Request.Headers["X-Atlas-Request"]=="dashboard";
    private static readonly HashSet<string> NetworkDeviceKinds=["ROUTER","NAS","ENDPOINT","PRINTER","ACCESS_POINT","SWITCH","SERVER","CAMERA","UNKNOWN"];
}
