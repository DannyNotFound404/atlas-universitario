using System.Security.Claims;
using Miguel.Server.Admin;
using Miguel.Server.Configuration;
using Npgsql;

namespace Miguel.Server.Reports;

public static class PortalEndpoints
{
    public static Guid User(HttpContext c)=>Guid.TryParse(c.User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?id:throw new PortalException("Sesión no válida.",401);
    public static string Actor(HttpContext c)=>c.User.Identity?.Name??throw new PortalException("Sesión no válida.",401);
    public static async ValueTask<object?> Guard(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        if(context.HttpContext.Request.Method is not("GET" or "HEAD") && context.HttpContext.Request.Headers["X-Atlas-Request"]!="dashboard")return Results.BadRequest(new{error="Solicitud no válida."});
        try{
            if(context.HttpContext.Request.Method is not("GET" or "HEAD") && context.HttpContext.Request.Path.StartsWithSegments("/api/v1/admin/settings/users"))
            {
                Guid.TryParse(context.HttpContext.User.FindFirstValue("atlas-session"),out var session);
                await context.HttpContext.RequestServices.GetRequiredService<AccessService>().RequireRecentAsync(User(context.HttpContext),session,context.HttpContext.RequestAborted);
            }
            return await next(context);}
        catch(PortalException e){return Results.Json(new{error=e.Message},statusCode:e.Status);}
        catch(ArgumentException){return Results.BadRequest(new{error="Revise los campos indicados."});}
        catch(PostgresException e)when(e.SqlState is "23505" or "40001" or "40P01"){return Results.Conflict(new{error="Otro cambio se registró al mismo tiempo o el nombre ya existe. Actualice la pantalla."});}
    }
    public static void MapPortalEndpoints(this WebApplication app)
    {
        var security=app.MapGroup("/api/v1/admin/account-security").RequireAuthorization(AdminEndpointExtensions.ReadPolicy).RequireRateLimiting("login");security.AddEndpointFilter(Guard);
        security.MapGet("/",async(HttpContext c,MfaService service,CancellationToken ct)=>Results.Ok(new{enabled=await service.EnabledAsync(User(c),ct)}));
        security.MapPost("/confirm",async(HttpContext c,MfaRequest request,MfaService service,CancellationToken ct)=>{Guid.TryParse(c.User.FindFirstValue("atlas-session"),out var session);await service.ConfirmAsync(User(c),session,Actor(c),request,ct);return Results.Ok(new{confirmed=true});});
        security.MapPost("/begin",async(HttpContext c,MfaRequest request,MfaService service,CancellationToken ct)=>Results.Ok(new{secret=await service.BeginAsync(User(c),Actor(c),request,ct)}));
        security.MapPost("/enable",async(HttpContext c,MfaRequest request,MfaService service,CancellationToken ct)=>Results.Ok(new{recoveryCodes=await service.EnableAsync(User(c),Actor(c),request,ct,Guid.TryParse(c.User.FindFirstValue("atlas-session"),out var session)?session:null)}));
        security.MapPost("/disable",async(HttpContext c,MfaRequest request,MfaService service,CancellationToken ct)=>{await service.DisableAsync(User(c),Actor(c),request,ct);return Results.Ok(new{saved=true});});
        var reports=app.MapGroup("/api/v1/admin/reports").RequireAuthorization(AdminEndpointExtensions.ExportPolicy).RequireRateLimiting("admin");reports.AddEndpointFilter(Guard);
        reports.MapGet("/catalog",async(ConfigurationService config,CancellationToken ct)=>{var g=await config.GeneralAsync(ct);return Results.Ok(new{templates=ReportCatalog.Templates,general=g.Value,agencies=await config.QueryAsync("SELECT code,display_name FROM agencies ORDER BY display_name",ct)});});
        reports.MapPost("/preview",async(HttpContext c,ReportRequest request,ReportService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(request,Actor(c),ct)));
        reports.MapPost("/jobs",async(HttpContext c,ReportJobRequest request,ReportService service,CancellationToken ct)=>Results.Ok(new{id=await service.QueueAsync(User(c),Actor(c),request,ct)}));
        reports.MapGet("/jobs",async(HttpContext c,int? page,ReportService service,CancellationToken ct)=>Results.Ok(await service.HistoryAsync(User(c),page??1,ct)));
        reports.MapPost("/jobs/{id:guid}/cancel",async(HttpContext c,Guid id,ReportService service,CancellationToken ct)=>{await service.CancelAsync(User(c),id,Actor(c),ct);return Results.Ok(new{saved=true});});
        reports.MapGet("/jobs/{id:guid}",async(HttpContext c,Guid id,ReportService service,CancellationToken ct)=>Results.Ok(await service.SnapshotAsync(User(c),id,ct)));
        reports.MapGet("/jobs/{id:guid}/files/{format}",async(HttpContext c,Guid id,string format,ReportService service,CancellationToken ct)=>{
            var f=await service.FileAsync(User(c),id,format,Actor(c),ct);c.Response.Headers["X-Content-SHA256"]=f.Hash;
            return Results.File(f.Bytes,format switch{"pdf"=>"application/pdf","xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",_=>"text/csv; charset=utf-8"},$"ATLAS-{id:D}.{format}");});
        reports.MapGet("/variants",async(HttpContext c,ConfigurationService config,CancellationToken ct)=>Results.Ok(await config.QueryAsync("SELECT id,name,request,revision FROM atlas_report_variants WHERE owner_id=$1 ORDER BY name,id",ct,User(c))));
        reports.MapPost("/variants",async(HttpContext c,ReportVariantRequest request,ReportService service,CancellationToken ct)=>{await service.SaveVariantAsync(User(c),request,Actor(c),ct);return Results.Ok(new{saved=true});});
        reports.MapGet("/schedules",async(HttpContext c,ConfigurationService config,CancellationToken ct)=>Results.Ok(await config.QueryAsync("SELECT id,name,request,cadence,time_zone,next_at_utc,enabled,revision,suspension_reason FROM atlas_report_schedules WHERE owner_id=$1 ORDER BY next_at_utc,id",ct,User(c))));
        reports.MapPost("/schedules",async(HttpContext c,ReportScheduleRequest request,ReportService service,CancellationToken ct)=>{await service.ScheduleAsync(User(c),request,Actor(c),ct);return Results.Ok(new{saved=true});});
        reports.MapPut("/schedules/{id:guid}",async(HttpContext c,Guid id,ReportScheduleUpdate request,ReportService service,CancellationToken ct)=>{await service.ToggleScheduleAsync(User(c),id,request,Actor(c),ct);return Results.Ok(new{saved=true});});

        var settings=app.MapGroup("/api/v1/admin/settings").RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy).RequireRateLimiting("admin");settings.AddEndpointFilter(Guard);
        settings.MapGet("/",async(ConfigurationService service,CancellationToken ct)=>Results.Ok(await service.OverviewAsync(ct)));
        settings.MapGet("/operations",async(OperationalSettingsService service,CancellationToken ct)=>Results.Ok(await service.OverviewAsync(ct)));
        settings.MapPost("/retention/preview",async(RetentionChange request,OperationalSettingsService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(request,ct)));
        settings.MapPut("/retention",async(HttpContext c,RetentionChange request,OperationalSettingsService service,CancellationToken ct)=>{await service.SaveRetentionAsync(request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/holds",async(HttpContext c,HoldRequest request,OperationalSettingsService service,CancellationToken ct)=>{await service.HoldAsync(request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/holds/{id:guid}/release",async(HttpContext c,Guid id,HoldRelease request,OperationalSettingsService service,CancellationToken ct)=>{await service.ReleaseHoldAsync(id,request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/mutes",async(HttpContext c,MuteRequest request,OperationalSettingsService service,CancellationToken ct)=>{await service.MuteAsync(request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/mutes/{id:guid}/release",async(HttpContext c,Guid id,OperationalSettingsService service,CancellationToken ct)=>{await service.UnmuteAsync(id,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapGet("/agent-policies",async(AgentPolicyService service,CancellationToken ct)=>Results.Ok(await service.OverviewAsync(ct)));
        settings.MapPost("/agent-policies/impact",async(AgentPolicyChange request,AgentPolicyService service,CancellationToken ct)=>Results.Ok(await service.ImpactAsync(request,ct)));
        settings.MapPost("/agent-policies",async(HttpContext c,AgentPolicyChange request,AgentPolicyService service,CancellationToken ct)=>Results.Ok(new{revision=await service.SaveAsync(request,Actor(c),ct)}));
        settings.MapPut("/general",async(HttpContext c,SettingChange request,ConfigurationService service,CancellationToken ct)=>{await service.SaveAsync(request,Actor(c),ct);return Results.Ok(new{saved=true,effective=true});});
        settings.MapGet("/history",async(ConfigurationService service,CancellationToken ct)=>Results.Ok(await service.QueryAsync("SELECT section,revision,before_value,after_value,reason,actor,occurred_at_utc FROM atlas_setting_history ORDER BY id DESC LIMIT 100",ct)));
        settings.MapGet("/users",async(ConfigurationService service,CancellationToken ct)=>Results.Ok(await service.QueryAsync("SELECT id,username,display_name,role,enabled,security_revision AS revision,last_login_at_utc,locked_until_utc FROM admin_users ORDER BY username",ct)));
        settings.MapPost("/users",async(HttpContext c,AccountCreate request,AccessService service,CancellationToken ct)=>Results.Ok(new{id=await service.CreateAsync(request,Actor(c),ct)}));
        settings.MapPut("/users/{id:guid}",async(HttpContext c,Guid id,AccountUpdate request,AccessService service,CancellationToken ct)=>{await service.UpdateAsync(id,request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/users/{id:guid}/password",async(HttpContext c,Guid id,PasswordReset request,AccessService service,CancellationToken ct)=>{await service.ResetAsync(id,request,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapPost("/users/{id:guid}/revoke",async(HttpContext c,Guid id,AccessService service,CancellationToken ct)=>{await service.RevokeAsync(id,null,Actor(c),ct);return Results.Ok(new{saved=true});});
        settings.MapGet("/sessions",async(ConfigurationService service,CancellationToken ct)=>Results.Ok(await service.QueryAsync("SELECT s.id,u.username,s.created_at_utc,s.expires_at_utc,s.revoked_at_utc FROM atlas_admin_sessions s JOIN admin_users u ON u.id=s.user_id WHERE s.expires_at_utc>CURRENT_TIMESTAMP ORDER BY s.created_at_utc DESC LIMIT 200",ct)));
    }
}
