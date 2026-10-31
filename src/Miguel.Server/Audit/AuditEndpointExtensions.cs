using System.Globalization;
using System.Security.Claims;
using Miguel.Server.Admin;

namespace Miguel.Server.Audit;

public static class AuditEndpointExtensions
{
    public const int ExportLimit = 10_000;
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var audit = endpoints.MapGroup("/api/v1/admin/audit")
            .RequireAuthorization(AdminEndpointExtensions.ExportPolicy).RequireRateLimiting("admin");
        audit.MapGet("/overview", (AuditRepository repository, CancellationToken ct) => repository.ReadOverviewAsync(ct));
        audit.MapGet("/events", (HttpContext context, AuditRepository repository, CancellationToken ct) =>
            repository.ReadEventsAsync(EventQuery(context.Request.Query), ct));
        audit.MapGet("/enrollments", (HttpContext context, AuditRepository repository, CancellationToken ct) =>
            repository.ReadEnrollmentsAsync(EnrollmentQuery(context.Request.Query), ct));
        audit.MapGet("/events/{id:long}", async (long id, AuditRepository repository, CancellationToken ct) =>
            await repository.ReadEventAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        foreach (string kind in new[] { "events", "enrollments" })
        foreach (string format in new[] { "csv", "html" })
        {
            string path = format == "csv" ? "export.csv" : "report.html";
            audit.MapGet($"/{kind}/{path}", async (HttpContext context, AuditRepository repository,
                AdminRepository writer, TimeProvider clock, CancellationToken ct) =>
            {
                DateTimeOffset generated = clock.GetUtcNow();
                string actor = context.User.Identity?.Name ?? "unknown";
                byte[] bytes;
                long total;
                string filters;
                if (kind == "events")
                {
                    var query = EventQuery(context.Request.Query) with { Page = 1, PageSize = ExportLimit };
                    query = query with { FromUtc = query.FromUtc ?? PeriodStart(query.Period, generated), ToUtc = query.ToUtc is { } end && end < generated ? end : generated };
                    var page = await repository.ReadEventsAsync(query, ct);
                    total = page.Total;
                    if (total > ExportLimit) return TooLarge(total);
                    filters = AuditReportBuilder.Filters(query);
                    bytes = AuditReportBuilder.Events(page.Items, generated, actor, filters, format);
                }
                else
                {
                    var query = EnrollmentQuery(context.Request.Query) with { Page = 1, PageSize = ExportLimit };
                    query = query with { FromUtc = query.FromUtc ?? PeriodStart(query.Period, generated), ToUtc = query.ToUtc is { } end && end < generated ? end : generated };
                    var page = await repository.ReadEnrollmentsAsync(query, ct);
                    total = page.Total;
                    if (total > ExportLimit) return TooLarge(total);
                    filters = AuditReportBuilder.Filters(query);
                    bytes = AuditReportBuilder.Enrollments(page.Items, generated, actor, filters, format);
                }
                await writer.WriteAuditAsync(generated, context.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown",
                    actor, "admin.audit.exported", "Audit", kind,
                    new { exportType = kind, format, exportedRows = total, filters,
                        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                        remoteAddress = context.Connection.RemoteIpAddress?.ToString() }, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, format == "csv" ? "text/csv; charset=utf-8" : "text/html; charset=utf-8",
                    $"atlas-{(kind == "events" ? "auditoria" : "vinculaciones")}-{generated:yyyyMMdd-HHmmss}.{format}");
            });
        }
        return endpoints;
    }

    private static IResult TooLarge(long total) => Results.BadRequest(new {
        code = "audit_export_too_large", detail = $"La consulta contiene {total:N0} registros. Ajusta las fechas o los filtros a un máximo de {ExportLimit:N0}; no se exportó un informe incompleto." });

    private static DateTimeOffset? PeriodStart(string period, DateTimeOffset now) => period switch {
        "24H" => now.AddHours(-24), "7D" => now.AddDays(-7), "30D" => now.AddDays(-30),
        "90D" => now.AddDays(-90), "365D" => now.AddDays(-365), _ => null };

    internal static AuditEventQuery EventQuery(IQueryCollection q)
    {
        var (from, to) = Dates(q);
        return new(Text(q,"search",200), Text(q,"actor",255), Choice(q,"module",Modules),
            Choice(q,"outcome",Outcomes), Choice(q,"period",Periods, from.HasValue || to.HasValue ? "ALL" : "30D"),
            Integer(q,"page",1,int.MaxValue), Integer(q,"pageSize",30,200), from, to, Text(q,"target",255));
    }
    internal static AuditEnrollmentQuery EnrollmentQuery(IQueryCollection q)
    {
        var (from, to) = Dates(q);
        return new(Text(q,"search",200), Text(q,"agency",64).ToUpperInvariant(), Choice(q,"attribution",Attributions),
            Choice(q,"period",Periods,"ALL"), Integer(q,"page",1,int.MaxValue), Integer(q,"pageSize",20,200),from,to);
    }
    private static (DateTimeOffset?,DateTimeOffset?) Dates(IQueryCollection q)
    {
        DateTimeOffset? Parse(string key)
        {
            string text = q[key].ToString();
            if (text.Length == 0) return null;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
                throw new BadHttpRequestException("La fecha del filtro no es válida.");
            return value.ToUniversalTime();
        }
        var from = Parse("fromUtc"); var to = Parse("toUtc");
        if (from.HasValue && to.HasValue && from >= to) throw new BadHttpRequestException("La fecha final debe ser posterior a la inicial.");
        return (from,to);
    }
    private static int Integer(IQueryCollection q,string key,int fallback,int max) =>
        !q.ContainsKey(key) ? fallback : int.TryParse(q[key],out int value) && value > 0 ? Math.Min(value,max) : throw new BadHttpRequestException("Paginación no válida.");
    private static string Text(IQueryCollection q,string key,int max)
    {
        string value=q[key].ToString().Trim();
        return value.Length<=max ? value : throw new BadHttpRequestException("El filtro excede la longitud permitida.");
    }
    private static string Choice(IQueryCollection q,string key,HashSet<string> allowed,string fallback="")
    {
        string value=q[key].ToString().Trim().ToUpperInvariant();
        return value.Length==0 ? fallback : allowed.Contains(value) ? value : throw new BadHttpRequestException("Opción de filtro no válida.");
    }
    private static readonly HashSet<string> Modules=["ACCESS","ENROLLMENT","ASSETS","SOFTWARE","AGENCIES","NAS","NETWORK","BACKUPS","RELEASES","CASES","TRAINING","GOVERNANCE","AUDIT","REPORTS","CONFIGURATION","OTHER"];
    private static readonly HashSet<string> Outcomes=["SUCCEEDED","FAILED","REQUESTED","WARNING","UNKNOWN"];
    private static readonly HashSet<string> Attributions=["CONFIRMED","SYSTEM","UNAVAILABLE"];
    private static readonly HashSet<string> Periods=["24H","7D","30D","90D","365D","ALL"];
}
