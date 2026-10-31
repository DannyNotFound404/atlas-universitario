using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Server.Admin;
using Npgsql;

namespace Miguel.Server.Software;

public sealed record SoftwareCatalogCreate(string Name, string Publisher);
public static class SoftwareEndpoints
{
    public static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/software").RequireRateLimiting("admin").RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && context.HttpContext.Request.Headers["X-Atlas-Request"] != "dashboard") return Results.BadRequest(new { error = "invalid_dashboard_request" });
            try { return await next(context); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message.Split('\n')[0] }); }
            catch (InvalidOperationException ex) when (ex.Message is "revision_conflict" or "review_already_recorded") { return Results.Conflict(new { error = ex.Message }); }
            catch (PostgresException ex) when (ex.SqlState is "23503" or "23505" or "40001") { return Results.Conflict(new { error = "record_changed_or_unavailable" }); }
        });
        group.MapGet("", ([AsParameters] SoftwareQuery query, SoftwareRepository repository, CancellationToken ct) => repository.Query(query, ct));
        group.MapGet("/summary", (SoftwareRepository r, CancellationToken ct) => r.Summary(ct));
        group.MapGet("/products/{id}", async (string id, SoftwareRepository r, CancellationToken ct) => Result(await r.ProductDetail(id, ct)));
        group.MapPost("/products", async (SoftwareCatalogCreate request, HttpContext c, SoftwareRepository r, CancellationToken ct) => Results.Ok(new { id = await r.CreateProduct(request.Name, request.Publisher, Actor(c), ct) })).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapPut("/products/{id}", async (string id, SoftwareProductRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.SaveProduct(id, request, Actor(c), ct); return Results.Ok(new { saved = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapPut("/products/{id}/rule", async (string id, SoftwareRuleRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.SaveRule(id, request, Actor(c), ct); return Results.Ok(new { saved = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapPut("/licenses/{id:guid}", async (Guid id, SoftwareLicenseRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.SaveLicense(id, request, Actor(c), ct); return Results.Ok(new { saved = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapGet("/licenses/{id:guid}/assignments", (Guid id, SoftwareRepository r, CancellationToken ct) => r.Assignments(id, ct));
        group.MapPost("/licenses/{id:guid}/assignments", async (Guid id, SoftwareAssignmentRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.Assign(id, request, Actor(c), ct); return Results.Ok(new { saved = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapDelete("/assignments/{id:guid}", async (Guid id, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.ReleaseAssignment(id, Actor(c), ct); return Results.Ok(new { released = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapPut("/actions/{id:guid}", async (Guid id, SoftwareActionRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => { await r.SaveAction(id, request, Actor(c), ct); return Results.Ok(new { saved = true }); }).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapPost("/reviews", async (SoftwareReviewRequest request, HttpContext c, SoftwareRepository r, CancellationToken ct) => Results.Ok(await r.CreateReview(request, Actor(c), ct))).RequireAuthorization(AdminEndpointExtensions.ProvisionPolicy);
        group.MapGet("/reviews/{id:guid}", async (Guid id, SoftwareRepository r, CancellationToken ct) => Result(await r.Review(id, ct)));
        group.MapGet("/reviews/{id:guid}/evidence.json", async (Guid id, HttpContext c, SoftwareRepository r, AdminRepository audit, CancellationToken ct) =>
        {
            var review = await r.Review(id, ct); if (review.ValueKind == JsonValueKind.Null) return Results.NotFound();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(review.GetProperty("evidence"));
            await ExportAudit(audit, c, "review-evidence", bytes, ct);
            return Results.File(bytes, "application/json", $"atlas-software-{review.GetProperty("period").GetString()}-evidencia.json");
        }).RequireAuthorization(AdminEndpointExtensions.ExportPolicy);
        group.MapGet("/report.html", async ([AsParameters] SoftwareQuery query, HttpContext c, SoftwareRepository r, AdminRepository audit, CancellationToken ct) =>
        {
            var data = await r.Query(query with { Page = 1 }, ct, 10000);
            if (data.GetProperty("total").GetInt32() > 10000) return Results.BadRequest(new { error = "export_too_large_narrow_filters" });
            byte[] bytes = SoftwareReport.Build(query, data, Actor(c));
            await ExportAudit(audit, c, JsonSerializer.Serialize(query), bytes, ct);
            return Results.File(bytes, "text/html; charset=utf-8", "atlas-software.html");
        }).RequireAuthorization(AdminEndpointExtensions.ExportPolicy);
        return app;
    }
    private static IResult Result(JsonElement value) => value.ValueKind == JsonValueKind.Null ? Results.NotFound() : Results.Ok(value);
    private static string Actor(HttpContext c) => c.User.Identity?.Name ?? "unknown";
    private static Task ExportAudit(AdminRepository r, HttpContext c, string filter, byte[] bytes, CancellationToken ct) => r.WriteAuditAsync(DateTimeOffset.UtcNow, c.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown", Actor(c), "admin.software.report.exported", "Software", null, new { filter, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, ct);
}

internal static class SoftwareReport
{
    internal static byte[] Build(SoftwareQuery query, JsonElement data, string actor)
    {
        string E(string? x) => WebUtility.HtmlEncode(x ?? "");
        (string Key, string Title)[] columns = query.View switch
        {
            "products" => [("name","Producto"),("publisher","Editor"),("versions","Versiones"),("devices","Equipos"),("authorization_status","Regla general"),("license_metric","Licenciamiento")],
            "devices" => [("machine_name","Equipo"),("agency_code","Agencia"),("current_user_name","Usuario observado"),("agent_version","Agente"),("captured_at_utc","Inventario UTC"),("status","Cobertura")],
            "licenses" => [("name","Producto"),("reference","Referencia"),("metric","Métrica"),("quantity","Cantidad"),("assigned","Asignadas"),("expires_on","Vencimiento"),("status","Estado")],
            "actions" => [("title","Acción"),("owner","Responsable"),("due_on","Plazo"),("status","Estado"),("result","Resultado"),("reference","Evidencia")],
            "reviews" => [("period","Semestre"),("executed_by","Ejecutó"),("validated_by","Validó"),("conclusion","Conclusión"),("reference","Referencia"),("sha256","SHA-256 evidencia")],
            "changes" => [("machine_name","Equipo"),("name","Producto"),("kind","Cambio observado"),("old_version","Versión anterior"),("new_version","Versión observada"),("captured_at_utc","Capturado UTC"),("received_at_utc","Recibido UTC")],
            _ => [("machine_name","Equipo"),("agency_code","Agencia"),("current_user_name","Usuario observado"),("name","Producto"),("publisher","Editor"),("version","Versión"),("authorization_status","Autorización"),("issues","Pendientes"),("last_seen_at_utc","Observado UTC")]
        };
        var rows = new StringBuilder();
        foreach (var row in data.GetProperty("items").EnumerateArray())
        {
            rows.Append("<tr>");
            foreach (var col in columns)
            { string value = row.TryGetProperty(col.Key, out var v) ? v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(x => Label(x.ToString()))) : Label(v.ToString()) : ""; rows.Append("<td>").Append(E(value)).Append("</td>"); }
            rows.Append("</tr>");
        }
        string html = $"<!doctype html><html lang=es><meta charset=utf-8><meta http-equiv=Content-Security-Policy content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>ATLAS · Software</title><style>body{{font:14px system-ui;color:#15394b;margin:30px}}table{{border-collapse:collapse;width:100%}}th,td{{text-align:left;padding:9px;border-bottom:1px solid #d8e3ea;overflow-wrap:anywhere}}th{{background:#eff5f7}}@media print{{thead{{display:table-header-group}}tr{{break-inside:avoid}}}}</style><h1>Control de software</h1><p>Generado {DateTimeOffset.UtcNow:O} · Cuenta {E(actor)} · {data.GetProperty("total")} registros</p><p>Vista: {E(query.View)} · Búsqueda: {E(query.Search)} · Agencia: {E(query.Agency)} · Estado: {E(Label(query.Status))} · Producto: {E(query.Product)} · Equipo: {E(query.Device)}</p><p>Política V4 §6 Software p.16. Aplicación anticipada; vigencia formal 1 de junio de 2027. Los datos representan lo observado, no acreditan uso ni licencia legal por sí solos. Las fechas de instalación son declaradas por el instalador. Imprima para guardar como PDF.</p><table><thead><tr>{string.Join("", columns.Select(x => "<th>" + E(x.Title) + "</th>"))}</tr></thead><tbody>{rows}</tbody></table></html>";
        return Encoding.UTF8.GetBytes(html);
    }
    internal static string Label(string value) => value switch
    {
        "AUTHORIZED" => "Autorizado", "UNREVIEWED" => "Por clasificar", "RESTRICTED" => "Uso restringido: revisar condiciones", "PROHIBITED" => "No autorizado",
        "END_OF_SUPPORT" => "Fuera de soporte", "UNLICENSED" => "Sin asignación vigente registrada", "LICENSE_UNREVIEWED" => "Licenciamiento por clasificar", "MANUAL_LICENSE_REVIEW" => "Validación de licencia por usuario/otra métrica", "UNVERIFIED" => "Observación sin verificar recientemente",
        "NOT_REPORTED" => "Sin reporte", "PARTIAL" => "Cobertura parcial", "STALE" => "Inventario antiguo", "REPORTED" => "Fuentes consultadas", "FREE" => "Sin asignación de licencia requerida", "DEVICE" => "Por equipo", "USER" => "Por usuario", "OTHER" => "Otra métrica",
        "OPEN" => "Pendiente", "IN_PROGRESS" => "En curso", "RESOLVED" => "Resuelta", "CURRENT" => "Vigente", "EXPIRED" => "Vencida", "EXPIRING" => "Por vencer", "FUTURE" => "Vigencia futura", "INACTIVE" => "Inactiva", "DETECTED" => "Detectado", "CHANGED" => "Versión o producto cambiado", "REMOVED" => "Ya no detectado en la fuente", _ => value
    };
}
