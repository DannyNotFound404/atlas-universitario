using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Miguel.Contracts;
using Miguel.Server.Admin;
using Miguel.Server.Security;
using Npgsql;

namespace Miguel.Server.Distribution;

public sealed record UpdaterPolicySaveRequest(int ExpectedRevision, UpdaterPolicy Policy, bool ResumeAfterFailure = false);
public sealed record UpdaterRetryRequest(string Reason);

public static class UpdaterEndpoints
{
    public const string ManagePolicy = "UpdaterManage";

    public static IEndpointRouteBuilder MapUpdaterEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/v1/agent/updater").RequireRateLimiting("agent");
        agent.AddEndpointFilter(ErrorFilter);
        agent.MapPost("/check", (HttpContext c, UpdaterCheckRequest request, UpdaterCoordinator service, CancellationToken ct) =>
            service.CheckAsync(Device(c), request, ct));
        agent.MapPost("/lease", (HttpContext c, UpdaterLeaseRequest request, UpdaterCoordinator service, CancellationToken ct) =>
            service.LeaseAsync(Device(c), request, ct));
        agent.MapPost("/install", async (HttpContext c, UpdaterInstallRequest request, UpdaterCoordinator service, CancellationToken ct) =>
            await service.AuthorizeInstallAsync(Device(c), request, ct) ? Results.Ok(new { allowed = true }) : Results.Conflict(new { error = "install_not_authorized" }));
        agent.MapPost("/events", async (HttpContext c, UpdaterEvent request, UpdaterCoordinator service, CancellationToken ct) =>
        { await service.RecordAsync(Device(c), request, ct); return Results.Ok(new { accepted = true }); });
        agent.MapGet("/attempts/{id:guid}/confirmation", (HttpContext c, Guid id, UpdaterCoordinator service, CancellationToken ct) =>
            service.ConfirmAsync(Device(c), id, ct));

        var admin = app.MapGroup("/api/v1/admin/updater").RequireRateLimiting("admin")
            .RequireAuthorization(AdminEndpointExtensions.ExportPolicy);
        admin.AddEndpointFilter(ErrorFilter);
        admin.MapGet("", (string? search, int? page, string? ring, UpdaterCoordinator service, CancellationToken ct) => service.ReadDashboardAsync(search, page ?? 1, ct, ring: ring));
        admin.MapGet("/devices/{id:guid}/history", (Guid id, UpdaterCoordinator service, CancellationToken ct) => service.ReadHistoryAsync(id, ct));
        admin.MapPost("/devices/{id:guid}/retry", async (Guid id, HttpContext c, UpdaterRetryRequest request, UpdaterCoordinator service, CancellationToken ct) =>
        {
            if (!Mutation(c)) return Results.BadRequest(new { error = "invalid_dashboard_request" });
            return await service.AllowRetryAsync(id, request.Reason, Actor(c), ct) ? Results.Ok(new { authorized = true }) : Results.NotFound();
        }).RequireAuthorization(ManagePolicy);
        admin.MapGet("/report.html", async (HttpContext c, string? search, string? ring, UpdaterCoordinator service, AdminRepository audit, TimeProvider clock, CancellationToken ct) =>
        {
            JsonElement first = await service.ReadDashboardAsync(search, 1, ct, 10000, ring);
            int total = first.GetProperty("total").GetInt32();
            if (total > 10000) return Results.BadRequest(new { error = "export_too_large" });
            var items = new List<JsonElement>(first.GetProperty("items").EnumerateArray());
            string E(string? value) => WebUtility.HtmlEncode(value ?? "");
            var rows = new StringBuilder();
            foreach (var item in items) rows.Append("<tr><td>").Append(E(item.GetProperty("machineName").GetString()))
                .Append("</td><td>").Append(E(item.GetProperty("agencyCode").GetString())).Append("</td><td>")
                .Append(E(item.GetProperty("currentUser").GetString())).Append("</td><td>")
                .Append(E(item.GetProperty("updateRingCode").GetString() == "PILOT" ? "Piloto" : "General")).Append("</td><td>")
                .Append(E(UpdaterReportLabels.Phase(item.GetProperty("phase").GetString()))).Append("</td><td>")
                .Append(E(item.GetProperty("updaterVersion").GetString())).Append("</td><td>")
                .Append(E(item.GetProperty("agentVersion").GetString())).Append("</td><td>")
                .Append(E(item.GetProperty("backupClientVersion").GetString())).Append("</td></tr>");
            byte[] bytes = Encoding.UTF8.GetBytes($"<!doctype html><html lang=es><meta charset=utf-8><meta http-equiv=Content-Security-Policy content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>ATLAS · Actualizaciones</title><style>body{{font:14px system-ui;color:#15394b;margin:32px}}table{{border-collapse:collapse;width:100%}}td,th{{border-bottom:1px solid #ddd;padding:10px;text-align:left}}</style><h1>Seguimiento de actualizaciones</h1><p>Generado {clock.GetUtcNow():O} · Cuenta {E(Actor(c))} · Anillo {E(string.IsNullOrWhiteSpace(ring) ? "Todos" : UpdateRings.Normalize(ring) == "PILOT" ? "Piloto" : "General")} · Búsqueda {E(search ?? "Todos")} · {items.Count} equipos</p><p>Consulta del estado observado durante la generación. Sin reporte no significa instalación fallida. Imprima este documento para guardar una copia PDF.</p><table><thead><tr><th>Equipo</th><th>Agencia</th><th>Usuario observado</th><th>Anillo</th><th>Estado</th><th>Actualizador</th><th>Agente</th><th>Atlas Backup</th></tr></thead><tbody>{rows}</tbody></table></html>");
            await audit.WriteAuditAsync(clock.GetUtcNow(), c.User.FindFirstValue(ClaimTypes.Role) ?? "Unknown", Actor(c), "admin.release.report.exported", "AtlasUpdater", null,
                new { rows = items.Count, search, ring, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, ct);
            c.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, "text/html; charset=utf-8", "atlas-actualizaciones.html");
        });
        admin.MapPut("/releases/{id:guid}/policy", async (Guid id, HttpContext c, UpdaterPolicySaveRequest request, UpdaterCoordinator service, CancellationToken ct) =>
        {
            if (!Mutation(c)) return Results.BadRequest(new { error = "invalid_dashboard_request" });
            try { await service.SavePolicyAsync(id, request.Policy, request.ExpectedRevision, Actor(c), request.ResumeAfterFailure, ct); return Results.Ok(new { saved = true }); }
            catch (InvalidOperationException ex) when (ex.Message == "policy_revision_conflict") { return Results.Conflict(new { error = ex.Message }); }
        }).RequireAuthorization(ManagePolicy);
        admin.MapPost("/controls", async (HttpContext c, SignedUpdaterControl envelope, UpdaterCoordinator service,
            NpgsqlDataSource db, IOptions<ServerOptions> options, TimeProvider clock, AtlasReleaseService releases, CancellationToken ct) =>
        {
            if (!Mutation(c)) return Results.BadRequest(new { error = "invalid_dashboard_request" });
            string pem = await File.ReadAllTextAsync(options.Value.ReleasePublicKeyPath ?? throw new InvalidOperationException("ReleasePublicKeyPath missing"), ct);
            UpdaterControlDocument control = UpdaterControlValidator.Verify(envelope, pem, clock.GetUtcNow());
            if (control.Purpose == "RECOVERY")
            {
                _ = await releases.VerifyStoredReleaseAsync(control.ReleaseId!.Value, ct);
                AtlasSignedReleaseEnvelope signed = (await releases.GetEnvelopeAsync(control.ReleaseId, ct))!;
                var manifest = JsonSerializer.Deserialize<AtlasReleaseManifest>(Convert.FromBase64String(signed.ManifestBase64), UpdaterCoordinator.Json)!;
                if (control.PackageHashes!.Count != manifest.Packages.Count || manifest.Packages.Any(p =>
                        !control.PackageHashes.TryGetValue(p.Component, out string? hash) || !string.Equals(hash, p.Sha256, StringComparison.OrdinalIgnoreCase)))
                    return Results.BadRequest(new { error = "recovery_hashes_mismatch" });
            }
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await UpdaterCoordinator.ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(935040)", ct);
            await using (var existing = UpdaterCoordinator.Command(connection, tx,
                "SELECT envelope::text FROM updater_controls WHERE authorization_id=$1", control.AuthorizationId))
            {
                if (await existing.ExecuteScalarAsync(ct) is string saved)
                {
                    var previous = JsonSerializer.Deserialize<SignedUpdaterControl>(saved, UpdaterCoordinator.Json);
                    return previous?.PayloadBase64 == envelope.PayloadBase64 ? Results.Ok(new { control.AuthorizationId, alreadyRegistered = true }) :
                        Results.Conflict(new { error = "authorization_id_conflict" });
                }
            }
            if (control.Purpose == "TRUST_TRANSITION")
            {
                await using var latest = UpdaterCoordinator.Command(connection, tx, "SELECT COALESCE(max(generation),0) FROM updater_controls WHERE purpose='TRUST_TRANSITION'");
                if (control.Generation <= (long)(await latest.ExecuteScalarAsync(ct))!)
                    return Results.Conflict(new { error = "trust_generation_conflict" });
            }
            await UpdaterCoordinator.ExecuteAsync(connection, tx, """
                INSERT INTO updater_controls(authorization_id,purpose,release_id,generation,expires_at_utc,envelope,created_by)
                VALUES($1,$2,NULLIF($3,'00000000-0000-0000-0000-000000000000'::uuid),$4,$5,$6::jsonb,$7)
                ON CONFLICT(authorization_id) DO NOTHING
                """, ct, control.AuthorizationId, control.Purpose, control.ReleaseId ?? Guid.Empty, control.Generation,
                control.ExpiresAtUtc, JsonSerializer.Serialize(envelope, UpdaterCoordinator.Json), Actor(c));
            await UpdaterCoordinator.AuditAsync(connection, tx, Actor(c), "admin.release.control.registered", control.AuthorizationId.ToString(),
                new { control.Purpose, control.ReleaseId, control.Generation, control.ExpiresAtUtc }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { control.AuthorizationId, control.Purpose, control.ExpiresAtUtc });
        }).RequireAuthorization(ManagePolicy);
        return app;
    }
    private static Guid Device(HttpContext c) => (Guid)c.Items[DeviceAuthenticationMiddleware.DeviceIdItemKey]!;
    private static string Actor(HttpContext c) => c.User.Identity?.Name ?? "unknown";
    private static bool Mutation(HttpContext c) => c.Request.Headers["X-Atlas-Request"] == "dashboard";
    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or CryptographicException or FormatException)
        { return Results.BadRequest(new { error = "invalid_updater_request" }); }
        catch (DirectoryNotFoundException) { return Results.NotFound(new { error = "release_not_found" }); }
    }
}

internal static class UpdaterReportLabels
{
    internal static string Phase(string? phase) => phase switch
    {
        "NOT_REPORTED" => "Sin reporte del actualizador", "CHECKED" => "Comprobación recibida",
        "LEASED" => "Preparando descarga", "DOWNLOADING" => "Descargando", "READY" => "Listo para instalar",
        "INSTALLING" => "Instalando", "AWAITING_CONFIRMATION" => "Esperando confirmación", "CONFIRMED" => "Confirmado",
        "DEFERRED" => "Aplazado", "FAILED" => "Falló", "ROLLING_BACK" => "Recuperando versión anterior",
        "ROLLED_BACK" => "Versión anterior recuperada", "RECOVERY_FAILED" => "Requiere recuperación",
        "CANCELLED" => "Detenido antes de instalar", _ => "Estado sin clasificar"
    };
}
