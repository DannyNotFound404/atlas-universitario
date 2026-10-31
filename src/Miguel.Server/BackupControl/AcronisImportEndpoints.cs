using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Miguel.Server.Admin;

namespace Miguel.Server.BackupControl;

public static class AcronisImportEndpoints
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void MapAcronisImportEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/admin/backup-controls/acronis-imports")
            .RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy).RequireRateLimiting("admin");
        group.AddEndpointFilter(Miguel.Server.Nas.NasLifecycleEndpoints.GuardBindings);
        group.MapGet("",(AcronisImportService service,CancellationToken token)=>service.ListAsync(token));
        group.MapGet("/{id:guid}",async(Guid id,AcronisImportService service,CancellationToken token)=>
            await service.ReadAsync(id,token) is { } detail?Results.Ok(detail):Results.NotFound(new { error="El informe no existe." }));
        group.MapGet("/{id:guid}/file",(Guid id,HttpContext context,AcronisImportService service,CancellationToken token)=>
            Execute(context,async()=>await service.FileAsync(id,token) is { } file?
                Results.File(file.PhysicalPath,file.MediaType,file.OriginalFileName):Results.NotFound(new { error="El archivo de evidencia no está disponible." }),false));
        group.MapPost("",(HttpContext context,AcronisImportService service,CancellationToken token)=>Execute(context,async()=>
        {
            if(!context.Request.HasFormContentType)return Results.BadRequest(new { error="Se requiere el Excel y la fecha de corte." });
            var form=await context.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit=26_000_000,ValueLengthLimit=32_000,MultipartHeadersLengthLimit=16_384 },token);
            if(form.Files.Count!=1 || form["record"].Count!=1)return Results.BadRequest(new { error="Envía un solo Excel con su fecha y zona horaria." });
            var metadata=JsonSerializer.Deserialize<AcronisImportMetadata>(form["record"].ToString(),Json);
            if(metadata is null)return Results.BadRequest(new { error="Falta la fecha del informe." });
            var file=form.Files[0];await using var stream=file.OpenReadStream();
            return Results.Ok(await service.UploadAsync(stream,file.FileName,metadata,context.User.Identity!.Name!,token));
        })).RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapPost("/{id:guid}/review",(Guid id,AcronisImportReview request,HttpContext context,AcronisImportService service,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(await service.ReviewAsync(id,request,context.User.Identity!.Name!,token))))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
        group.MapGet("/{id:guid}/supplement",(Guid id,HttpContext context,AcronisImportService service,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(await service.SupplementPreviewAsync(id,token)),false));
        group.MapPost("/{id:guid}/supplement",(Guid id,AcronisSupplementRequest request,HttpContext context,AcronisImportService service,CancellationToken token)=>
            Execute(context,async()=>Results.Ok(await service.SupplementAsync(id,request,context.User.Identity!.Name!,token))))
            .RequireAuthorization(BackupControlEndpointExtensions.ManagePolicy);
    }
    private static async Task<IResult> Execute(HttpContext context,Func<Task<IResult>> action,bool mutation=true)
    {
        if(mutation && context.Request.Headers["X-Atlas-Request"]!="dashboard")return Results.BadRequest(new { error="Origen de solicitud no válido." });
        try{return await action();}
        catch(BackupWorkspaceException ex){return ex.Conflict?Results.Conflict(new { error=ex.Message }):Results.BadRequest(new { error=ex.Message });}
        catch(JsonException){return Results.BadRequest(new { error="Los datos del informe no tienen un formato válido." });}
        catch(InvalidDataException){return Results.BadRequest(new { error="El formulario o archivo excede el límite permitido." });}
        catch(IOException){return Results.Json(new { error="No se pudo conservar o leer la evidencia. Reintenta la carga del mismo archivo." },statusCode:503);}
    }
}
