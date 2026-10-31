using System.Text.Json;
using Miguel.Server.Admin;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Miguel.Server.BackupControl;

public static class BackupWorkspaceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void MapBackupWorkspaceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group=app.MapGroup("/api/v1/admin/backup-controls")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy).RequireRateLimiting("admin");
        group.AddEndpointFilter(Miguel.Server.Nas.NasLifecycleEndpoints.GuardBindings);
        group.MapGet("/plans",(BackupPlanRepository repository,CancellationToken token)=>repository.ReadAsync(token));
        group.MapGet("/context-reviews",(BackupEvidenceContextService service,CancellationToken token)=>service.ReadAsync(token));
        group.MapPost("/context-reviews",(HttpContext context,BackupContextReviewRequest request,BackupEvidenceContextService service,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(await service.ReviewAsync(request,context.User.Identity!.Name!,token))))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapGet("/plans/{id:guid}/demonstrations",(Guid id,HttpContext context,BackupPlanDemonstrationService service,CancellationToken token)=>
            ReadDemonstrations(id,service,token));
        group.MapPost("/plans/{id:guid}/demonstrations",(Guid id,HttpContext context,BackupPlanDemonstrationService service,CancellationToken token)=>
            SaveDemonstration(id,null,context,service,token)).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/plans/{id:guid}/demonstrations/{demonstrationId:guid}/retry",(Guid id,Guid demonstrationId,HttpContext context,BackupPlanDemonstrationService service,CancellationToken token)=>
            SaveDemonstration(id,demonstrationId,context,service,token)).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/plans/{id:guid}/demonstrations/{demonstrationId:guid}/samples",(Guid id,Guid demonstrationId,HttpContext context,BackupPlanDemonstrationService service,CancellationToken token)=>
            SaveDemonstration(id,demonstrationId,context,service,token,true)).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/plans",(HttpContext context,BackupPlanSaveRequest request,BackupPlanRepository repository,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(new { id=await repository.SaveAsync(null,request,context.User.Identity!.Name!,token) })))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPut("/plans/{id:guid}",(Guid id,HttpContext context,BackupPlanSaveRequest request,BackupPlanRepository repository,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(new { id=await repository.SaveAsync(id,request,context.User.Identity!.Name!,token) })))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/plans/{id:guid}/apply",(Guid id,HttpContext context,BackupPlanApplyRequest request,BackupPlanRepository repository,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(new { applied=await repository.ApplyAsync(id,request,context.User.Identity!.Name!,token) })))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/profiles/{id:guid}/activity",(Guid id,HttpContext context,BackupRecordService service,CancellationToken token)=>
            Execute(context,async()=>
            {
                if (!context.Request.HasFormContentType) return Results.BadRequest(new { error="Se requiere el formulario de actividad." });
                IFormCollection form=await context.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit=26_000_000,ValueLengthLimit=32_000,MultipartHeadersLengthLimit=16_384 },token);
                if (form.Files.Count > 1 || form["record"].Count != 1) return Results.BadRequest(new { error="Envía un registro y un archivo opcional." });
                BackupRecordRequest? request=JsonSerializer.Deserialize<BackupRecordRequest>(form["record"].ToString(),Json);
                if (request is null) return Results.BadRequest(new { error="Falta el registro." });
                await service.SaveAsync(id,request,form.Files.FirstOrDefault(),context.User.Identity!.Name!,token);
                return Results.Ok(new { saved=true });
            })).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
    }

    private static async Task<IResult> ReadDemonstrations(Guid id,BackupPlanDemonstrationService service,CancellationToken token)
    {
        try { return Results.Ok(await service.ReadAsync(id,token)); }
        catch(BackupWorkspaceException ex) { return Results.NotFound(new { error=ex.Message }); }
    }

    private static Task<IResult> SaveDemonstration(Guid planId,Guid? demonstrationId,HttpContext context,BackupPlanDemonstrationService service,CancellationToken token,bool append=false)=>
        Execute(context,async()=>
        {
            if(!context.Request.HasFormContentType)return Results.BadRequest(new { error="Se requiere el formulario de demostración." });
            var form=await context.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit=26_000_000,ValueLengthLimit=128_000,MultipartHeadersLengthLimit=16_384 },token);
            if(form.Files.Count>1 || form["record"].Count!=1)return Results.BadRequest(new { error="Envía un registro y una evidencia conjunta opcional." });
            var request=JsonSerializer.Deserialize<BackupDemonstrationRequest>(form["record"].ToString(),Json);
            if(request is null)return Results.BadRequest(new { error="Falta el registro de demostración." });
            return Results.Ok(new { id=await service.SaveAsync(planId,demonstrationId,request,form.Files.FirstOrDefault(),context.User.Identity!.Name!,token,append) });
        });

    private static async Task<IResult> Execute(HttpContext context,Func<Task<IResult>> operation)
    {
        if (context.Request.Headers["X-Atlas-Request"] != "dashboard")
            return Results.BadRequest(new { error="Origen de solicitud no válido." });
        try { return await operation(); }
        catch (BackupWorkspaceException ex) { return ex.Conflict ? Results.Conflict(new { error=ex.Message }) : Results.BadRequest(new { error=ex.Message }); }
        catch (BackupEvidenceException ex) { return Results.BadRequest(new { error=ex.Message }); }
        catch (JsonException) { return Results.BadRequest(new { error="El registro no tiene un formato válido." }); }
        catch (InvalidDataException) { return Results.BadRequest(new { error="El formulario o archivo supera el tamaño permitido." }); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { return Results.Conflict(new { error="La referencia ya existe o cambió. Actualiza la página y revisa los datos." }); }
        catch (IOException) { return Results.Json(new { error="No se pudo conservar la evidencia. Puedes reintentar el mismo registro." },statusCode:503); }
    }
}
