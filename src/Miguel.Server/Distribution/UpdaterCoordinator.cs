using System.Data;
using System.Text.Json;
using Miguel.Contracts;
using Npgsql;

namespace Miguel.Server.Distribution;

public sealed class UpdaterCoordinator(NpgsqlDataSource db, ReleaseDeploymentRepository deployments,
    AtlasReleaseService releases, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> ResolveAsync(Guid device, bool modern, CancellationToken ct)
    {
        Guid? id = await deployments.ResolveReleaseIdAsync(device, ct) ?? releases.TryGetActiveReleaseId();
        if (id is null || id == Guid.Empty) return id;
        var (policy, halted, configured) = await ReadPolicyAsync(id.Value, ct);
        if (policy.Paused || halted || (configured && !modern && policy.RequireModernUpdater) ||
            UpdaterRules.Bucket(device, id.Value) >= policy.WavePercent || !UpdaterRules.InWindow(policy, clock.GetUtcNow()))
            return Guid.Empty;
        return id;
    }

    public async Task<UpdaterCheckResponse> CheckAsync(Guid device, UpdaterCheckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UpdaterVersion) || request.UpdaterVersion.Length > 64 ||
            request.Components is null || request.Components.Count > 5 || request.Components.Any(c => !AtlasReleaseComponents.Allowed.Contains(c.Component) || c.Version?.Length > 64)) throw new ArgumentException("invalid_updater_check");
        await using (var check = db.CreateCommand("INSERT INTO updater_devices(device_id,updater_version,components) VALUES($1,$2,$3::jsonb) ON CONFLICT(device_id) DO UPDATE SET updater_version=EXCLUDED.updater_version,components=EXCLUDED.components,last_check_at_utc=now()"))
        {
            check.Parameters.AddWithValue(device); check.Parameters.AddWithValue(request.UpdaterVersion);
            check.Parameters.AddWithValue(JsonSerializer.Serialize(request.Components, Json)); await check.ExecuteNonQueryAsync(ct);
        }
        Guid? id = await deployments.ResolveReleaseIdAsync(device, ct) ?? releases.TryGetActiveReleaseId();
        if (id is null) return Empty("NONE", "no_release");
        if (id == Guid.Empty) return Empty("PAUSED", "scope_paused");
        var (policy, halted, _) = await ReadPolicyAsync(id.Value, ct);
        string? reason = policy.Paused ? "release_paused" : halted ? "failure_threshold" :
            UpdaterRules.Bucket(device, id.Value) >= policy.WavePercent ? "waiting_wave" :
            !UpdaterRules.InWindow(policy, clock.GetUtcNow()) ? "outside_window" : null;
        if (reason is not null) return new("WAIT", reason, id, null, policy, 900);
        AtlasSignedReleaseEnvelope? envelope = await releases.GetEnvelopeAsync(id, ct);
        if (envelope is null) return Empty("NONE", "release_unavailable");
        return new("AVAILABLE", "eligible", id, envelope, policy, 300,
            await ReadControlAsync("RECOVERY", id.Value, ct), await ReadControlAsync("TRUST_TRANSITION", Guid.Empty, ct), await RetryGenerationAsync(device, ct));
    }

    private async Task<int> RetryGenerationAsync(Guid device, CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT retry_generation FROM updater_devices WHERE device_id=$1");
        command.Parameters.AddWithValue(device); return (int)(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<bool> CanDownloadAsync(Guid device, Guid release, Guid attempt, CancellationToken ct)
    {
        if (await ResolveAsync(device, true, ct) != release) return false;
        await using var command = db.CreateCommand("SELECT EXISTS(SELECT 1 FROM updater_attempts WHERE attempt_id=$1 AND device_id=$2 AND release_id=$3 AND lease_expires_at_utc>now() AND phase IN ('LEASED','DOWNLOADING','READY'))");
        command.Parameters.AddWithValue(attempt); command.Parameters.AddWithValue(device); command.Parameters.AddWithValue(release);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> AllowRetryAsync(Guid device, string reason, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 400) throw new ArgumentException("retry_reason_required");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        int count = await ExecuteAsync(connection, tx, "UPDATE updater_devices SET retry_generation=retry_generation+1 WHERE device_id=$1", ct, device);
        if (count == 0) return false;
        await AuditAsync(connection, tx, actor, "admin.release.device_retry.authorized", device.ToString(), new { reason }, ct);
        await tx.CommitAsync(ct); return true;
    }

    private static UpdaterCheckResponse Empty(string decision, string reason) => new(decision, reason, null, null, new(), 900);

    public async Task<(UpdaterPolicy Policy, bool Halted, bool Configured)> ReadPolicyAsync(Guid release, CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT policy::text,paused_automatically FROM updater_release_policies WHERE release_id=$1");
        command.Parameters.AddWithValue(release);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (JsonSerializer.Deserialize<UpdaterPolicy>(reader.GetString(0), Json)!, reader.GetBoolean(1), true)
            : (new UpdaterPolicy(RequireModernUpdater: false), false, false);
    }

    public async Task<UpdaterLeaseResponse> LeaseAsync(Guid device, UpdaterLeaseRequest request, CancellationToken ct)
    {
        if (request.AttemptId == Guid.Empty || request.ReleaseId == Guid.Empty || string.IsNullOrWhiteSpace(request.UpdaterVersion) || request.UpdaterVersion.Length > 64)
            throw new ArgumentException("invalid_lease");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(935040)", ct);
        if (await ResolveAsync(device, true, ct) != request.ReleaseId) return Deny("not_eligible");
        var (policy, _, _) = await ReadPolicyAsync(request.ReleaseId, ct);
        await using (var existing = Command(connection, tx,
            "SELECT device_id,release_id,lease_id,lease_expires_at_utc,phase FROM updater_attempts WHERE attempt_id=$1", request.AttemptId))
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetGuid(0) != device || reader.GetGuid(1) != request.ReleaseId) return Deny("attempt_conflict");
                if (UpdaterRules.IsTerminal(reader.GetString(4))) return Deny("attempt_finished");
                if (reader.GetDateTime(3) > clock.GetUtcNow().UtcDateTime)
                    return new(true, "lease_existing", reader.GetGuid(2), new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero), 0);
            }
        }
        // Global advisory lock serializes admission across all releases and agencies.
        await using var counts = Command(connection, tx, """
            SELECT count(*),count(*) FILTER (WHERE d.agency_code=(SELECT agency_code FROM devices WHERE id=$1)),
                   count(*) FILTER (WHERE a.device_id=$1)
            FROM updater_attempts a JOIN devices d ON d.id=a.device_id
            WHERE a.lease_expires_at_utc>now() AND a.phase IN ('LEASED','DOWNLOADING','READY','INSTALLING','ROLLING_BACK')
            AND a.attempt_id<>$2
            """, device, request.AttemptId);
        await using (var reader = await counts.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            if (reader.GetInt64(2) > 0) return Deny("device_busy");
            if (reader.GetInt64(0) >= policy.MaxConcurrent || reader.GetInt64(1) >= policy.MaxConcurrentPerAgency) return Deny("capacity_wait");
        }
        Guid lease = Guid.NewGuid();
        DateTimeOffset expiry = clock.GetUtcNow().AddMinutes(35);
        await ExecuteAsync(connection, tx, """
            INSERT INTO updater_attempts(attempt_id,device_id,release_id,lease_id,lease_expires_at_utc,updater_version)
            VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(attempt_id) DO UPDATE
            SET lease_id=EXCLUDED.lease_id,lease_expires_at_utc=EXCLUDED.lease_expires_at_utc,
                phase='LEASED',updated_at_utc=now()
            """, ct, request.AttemptId, device, request.ReleaseId, lease, expiry, request.UpdaterVersion);
        await tx.CommitAsync(ct);
        return new(true, "lease_granted", lease, expiry, 0);
    }
    private static UpdaterLeaseResponse Deny(string reason) => new(false, reason, null, null, 300);

    public async Task<bool> AuthorizeInstallAsync(Guid device, UpdaterInstallRequest request, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(935040)", ct);
        Guid? release = null;
        await using (var command = Command(connection, tx, """
            SELECT release_id FROM updater_attempts WHERE attempt_id=$1 AND device_id=$2 AND lease_id=$3
            AND lease_expires_at_utc>now() AND phase IN ('LEASED','DOWNLOADING','READY') FOR UPDATE
            """, request.AttemptId, device, request.LeaseId))
            release = await command.ExecuteScalarAsync(ct) as Guid?;
        if (release is null || await ResolveAsync(device, true, ct) != release) return false;
        await ExecuteAsync(connection, tx, """
            UPDATE updater_attempts SET phase='INSTALLING',install_authorized_at_utc=now(),
            baseline_agent_started_at_utc=(SELECT agent_started_at_utc FROM devices WHERE id=updater_attempts.device_id),
            lease_expires_at_utc=now()+interval '35 minutes',updated_at_utc=now() WHERE attempt_id=$1
            """, ct, request.AttemptId);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task RecordAsync(Guid device, UpdaterEvent item, CancellationToken ct)
    {
        if (item.EventId == Guid.Empty || item.Sequence < 1 || !UpdaterRules.ClientPhases.Contains(item.Phase) ||
            string.IsNullOrWhiteSpace(item.Code) || item.Code.Length > 120 || item.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            item.Components is null || item.Components.Count > 5 || item.Components.Any(c => !AtlasReleaseComponents.Allowed.Contains(c.Component) || c.Version?.Length > 64) ||
            item.ChangedComponents?.Count > 3 || item.ChangedComponents?.Any(c => c is not ("agent" or "backupnas" or "updater")) == true ||
            string.IsNullOrWhiteSpace(item.UpdaterVersion) || item.UpdaterVersion.Length > 64 ||
            item.DownloadedBytes < 0 || item.TotalBytes < item.DownloadedBytes || item.TotalBytes > 2_500_000_000L ||
            item.OccurredAtUtc > clock.GetUtcNow().AddMinutes(10)) throw new ArgumentException("invalid_update_event");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var owner = Command(connection, tx, "SELECT device_id FROM updater_attempts WHERE attempt_id=$1 AND release_id=$2 FOR UPDATE", item.AttemptId, item.ReleaseId))
            if (await owner.ExecuteScalarAsync(ct) is not Guid id || id != device) throw new ArgumentException("attempt_not_owned");
        int added = await ExecuteAsync(connection, tx, """
            INSERT INTO updater_events(event_id,attempt_id,device_id,sequence,phase,code,occurred_at_utc,payload)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb) ON CONFLICT DO NOTHING
            """, ct, item.EventId, item.AttemptId, device, item.Sequence, item.Phase, item.Code, item.OccurredAtUtc, JsonSerializer.Serialize(item, Json));
        if (added > 0)
        {
            await ExecuteAsync(connection, tx, """
                UPDATE updater_attempts SET phase=$2,code=$3,sequence=$4,updater_version=$5,components=$6::jsonb,
                    downloaded_bytes=$7,total_bytes=$8,agent_changed=agent_changed OR $9,changed_components=$10::jsonb,updated_at_utc=now(),
                    awaiting_confirmation_at_utc=CASE WHEN $2='AWAITING_CONFIRMATION' THEN COALESCE(awaiting_confirmation_at_utc,now()) ELSE awaiting_confirmation_at_utc END,
                    lease_expires_at_utc=CASE WHEN $2 IN ('DOWNLOADING','READY','INSTALLING','ROLLING_BACK')
                        AND lease_expires_at_utc>now() THEN now()+interval '35 minutes' ELSE LEAST(lease_expires_at_utc,now()) END
                WHERE attempt_id=$1 AND sequence<$4 AND phase NOT IN ('CONFIRMED','FAILED','ROLLED_BACK','CANCELLED')
                    AND (phase<>'RECOVERY_FAILED' OR $2 IN ('ROLLING_BACK','ROLLED_BACK','RECOVERY_FAILED'))
                    AND ($2 NOT IN ('INSTALLING','AWAITING_CONFIRMATION') OR install_authorized_at_utc IS NOT NULL)
                    AND ($2 NOT IN ('DOWNLOADING','READY') OR install_authorized_at_utc IS NULL)
                """, ct, item.AttemptId, item.Phase, item.Code, item.Sequence, item.UpdaterVersion,
                JsonSerializer.Serialize(item.Components, Json), item.DownloadedBytes, item.TotalBytes, item.AgentChanged, JsonSerializer.Serialize(item.ChangedComponents ?? [], Json));
            if (item.Phase == "RECOVERY_FAILED")
                await ExecuteAsync(connection, tx, """
                    INSERT INTO admin_notifications(id,fingerprint,category,severity,title,message,target_view,target_entity_id,detected_at_utc,last_seen_at_utc)
                    SELECT $1,$2,'UPDATES','HIGH','Un equipo requiere recuperación',
                        machine_name||': no se confirmó la recuperación de la versión anterior. Revise el historial y los archivos locales conservados.',
                        'distribution',id::text,now(),now() FROM devices WHERE id=$3
                    ON CONFLICT(fingerprint) DO UPDATE SET last_seen_at_utc=now()
                    """, ct, Guid.NewGuid(), $"updater-recovery:{item.AttemptId:D}", device);
        }
        await tx.CommitAsync(ct);
        if (UpdaterRules.IsFailure(item.Phase)) await StopOnFailuresAsync(item.ReleaseId, ct);
    }

    public async Task<UpdaterConfirmation> ConfirmAsync(Guid device, Guid attempt, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT a.release_id,a.phase,a.components::text,a.agent_changed,
                d.agent_version,d.updater_heartbeat_received_at_utc,a.install_authorized_at_utc,d.agent_started_at_utc,
                a.changed_components::text,d.backup_client_version,a.baseline_agent_started_at_utc,a.awaiting_confirmation_at_utc
            FROM updater_attempts a JOIN devices d ON d.id=a.device_id WHERE a.attempt_id=$1 AND a.device_id=$2
            """);
        command.Parameters.AddWithValue(attempt); command.Parameters.AddWithValue(device);
        Guid release; IReadOnlyList<UpdaterComponentVersion> components; bool changed;
        string agentVersion; DateTimeOffset? received, authorized, started, baselineStarted, awaiting; string[] changedComponents; string? backupVersion;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return new(false, "attempt_not_found");
            if (reader.GetString(1) == "CONFIRMED") return new(true, "confirmed");
            if (reader.GetString(1) != "AWAITING_CONFIRMATION") return new(false, "not_installed");
            release = reader.GetGuid(0); components = JsonSerializer.Deserialize<UpdaterComponentVersion[]>(reader.GetString(2), Json)!;
            changed = reader.GetBoolean(3); agentVersion = reader.GetString(4);
            received = Utc(reader, 5); authorized = Utc(reader, 6); started = Utc(reader, 7);
            changedComponents = JsonSerializer.Deserialize<string[]>(reader.GetString(8))!;
            backupVersion = reader.IsDBNull(9) ? null : reader.GetString(9);
            baselineStarted = Utc(reader, 10); awaiting = Utc(reader, 11);
        }
        if (authorized is null || awaiting is null || received is null || received < awaiting || (changed && (started is null || started == baselineStarted)))
            return new(false, "waiting_agent_report");
        AtlasSignedReleaseEnvelope? envelope = await releases.GetEnvelopeAsync(release, ct);
        if (envelope is null) return new(false, "release_unavailable");
        var manifest = JsonSerializer.Deserialize<AtlasReleaseManifest>(Convert.FromBase64String(envelope.ManifestBase64), Json)!;
        string expected = components.SingleOrDefault(c => c.Component == AtlasReleaseComponents.Agent)?.Version ?? "";
        if (expected.Length == 0 || agentVersion != expected || (changed && manifest.Packages.Single(p => p.Component == AtlasReleaseComponents.Agent).Version != expected))
            return new(false, "version_not_confirmed");
        foreach (string component in changedComponents)
        {
            string? target = manifest.Packages.SingleOrDefault(p => p.Component == component)?.Version;
            string? installed = component == "agent" ? agentVersion : component == "backupnas" ? backupVersion :
                components.SingleOrDefault(c => c.Component == component)?.Version;
            if (target is null || target != installed) return new(false, "component_version_not_confirmed");
        }
        await using var update = db.CreateCommand("UPDATE updater_attempts SET phase='CONFIRMED',code='heartbeat_confirmed',confirmed_at_utc=now(),updated_at_utc=now() WHERE attempt_id=$1 AND device_id=$2 AND phase='AWAITING_CONFIRMATION'");
        update.Parameters.AddWithValue(attempt); update.Parameters.AddWithValue(device); await update.ExecuteNonQueryAsync(ct);
        return new(true, "confirmed");
    }

    public async Task SavePolicyAsync(Guid release, UpdaterPolicy policy, int expectedRevision, string actor, bool resume, CancellationToken ct)
    {
        UpdaterRules.Validate(policy);
        _ = await releases.VerifyStoredReleaseAsync(release, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(935040)", ct);
        int affected = await ExecuteAsync(connection, tx, """
            INSERT INTO updater_release_policies(release_id,policy,updated_by)
            SELECT $1,$2::jsonb,$3 WHERE $4=0
            ON CONFLICT(release_id) DO NOTHING
            """, ct, release, JsonSerializer.Serialize(policy, Json), actor, expectedRevision);
        if (affected == 0) affected = await ExecuteAsync(connection, tx, """
            UPDATE updater_release_policies SET policy=$2::jsonb,revision=revision+1,updated_by=$3,updated_at_utc=now(),
                paused_automatically=CASE WHEN $5 THEN false ELSE paused_automatically END,
                failure_window_start=CASE WHEN $5 THEN now() ELSE failure_window_start END,
                pause_reason=CASE WHEN $5 THEN NULL ELSE pause_reason END
            WHERE release_id=$1 AND revision=$4
            """, ct, release, JsonSerializer.Serialize(policy, Json), actor, expectedRevision, resume);
        if (affected != 1) throw new InvalidOperationException("policy_revision_conflict");
        await AuditAsync(connection, tx, actor, "admin.release.policy.saved", release.ToString(), new { policy, expectedRevision, resume }, ct);
        await tx.CommitAsync(ct);
    }

    private async Task StopOnFailuresAsync(Guid release, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(935040)", ct);
        var (policy, halted, _) = await ReadPolicyAsync(release, ct);
        if (halted) return;
        // Count latest result per device, not repeated retries from one endpoint.
        await using var stats = Command(connection, tx, """
            SELECT count(*) FILTER(WHERE phase IN ('FAILED','ROLLED_BACK','RECOVERY_FAILED')),
                   count(*) FILTER(WHERE phase IN ('CONFIRMED','FAILED','ROLLED_BACK','RECOVERY_FAILED'))
            FROM (SELECT DISTINCT ON(device_id) phase FROM updater_attempts
                  WHERE release_id=$1 AND created_at_utc>=COALESCE((SELECT failure_window_start FROM updater_release_policies WHERE release_id=$1),'-infinity'::timestamptz)
                  ORDER BY device_id,created_at_utc DESC) latest
            """, release);
        long failures, total;
        await using (var reader = await stats.ExecuteReaderAsync(ct)) { await reader.ReadAsync(ct); failures = reader.GetInt64(0); total = reader.GetInt64(1); }
        if (failures < policy.FailureStopCount && (total < policy.MinimumSamples || failures * 100 < total * policy.FailurePercent)) return;
        await ExecuteAsync(connection, tx, """
            INSERT INTO updater_release_policies(release_id,policy,paused_automatically,pause_reason,updated_by)
            VALUES($1,$2::jsonb,true,'failure_threshold','SYSTEM') ON CONFLICT(release_id) DO UPDATE
            SET paused_automatically=true,pause_reason='failure_threshold',revision=updater_release_policies.revision+1
            """, ct, release, JsonSerializer.Serialize(policy, Json));
        await ExecuteAsync(connection, tx, """
            INSERT INTO admin_notifications(id,fingerprint,category,severity,title,message,target_view,target_entity_id,detected_at_utc,last_seen_at_utc)
            VALUES($1,$2,'UPDATES','HIGH','Despliegue detenido por fallos','Revise los equipos afectados antes de reanudar el despliegue.','distribution',$3,now(),now())
            ON CONFLICT(fingerprint) DO UPDATE SET last_seen_at_utc=now()
            """, ct, Guid.NewGuid(), $"updater-stop:{release:D}", release.ToString("D"));
        await AuditAsync(connection, tx, "SYSTEM", "admin.release.automatically_paused", release.ToString(), new { failures, total }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task MaintainAsync(CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            INSERT INTO admin_notifications(id,fingerprint,category,severity,title,message,target_view,target_entity_id,detected_at_utc,last_seen_at_utc)
            SELECT gen_random_uuid(),'updater-stalled:'||a.attempt_id::text,'UPDATES','MEDIUM',
                'Actualización sin confirmación reciente',d.machine_name||': revise su conexión y el historial de actualización.',
                'distribution',d.id::text,now(),now()
            FROM updater_attempts a JOIN devices d ON d.id=a.device_id
            WHERE d.enabled=true AND ((a.phase IN ('LEASED','DOWNLOADING','READY','INSTALLING','ROLLING_BACK')
                AND a.lease_expires_at_utc<now()-interval '15 minutes') OR
                (a.phase='AWAITING_CONFIRMATION' AND a.awaiting_confirmation_at_utc<now()-interval '2 hours'))
                AND NOT EXISTS(SELECT 1 FROM updater_attempts newer WHERE newer.device_id=a.device_id AND newer.created_at_utc>a.created_at_utc)
            ON CONFLICT(fingerprint) DO UPDATE SET last_seen_at_utc=now()
            """);
        await command.ExecuteNonQueryAsync(ct);
        var failedReleases = new List<Guid>();
        await using (var failures = db.CreateCommand("SELECT DISTINCT release_id FROM updater_attempts WHERE phase IN ('FAILED','ROLLED_BACK','RECOVERY_FAILED')"))
        await using (var reader = await failures.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) failedReleases.Add(reader.GetGuid(0));
        foreach (Guid release in failedReleases) await StopOnFailuresAsync(release, ct);
    }

    public async Task<JsonElement> ReadDashboardAsync(string? search, int page, CancellationToken ct, int pageSize = 25, string? ring = null)
    {
        if (search?.Length > 120 || page < 1 || page > 100000 || pageSize is < 1 or > 10000) throw new ArgumentException("invalid_update_filter");
        string ringCode = string.IsNullOrWhiteSpace(ring) ? "" : UpdateRings.Normalize(ring);
        await using var command = db.CreateCommand("""
            WITH latest AS (SELECT DISTINCT ON(device_id) * FROM updater_attempts ORDER BY device_id,created_at_utc DESC),
            fleet AS (SELECT d.id AS "deviceId",d.machine_name AS "machineName",d.agency_code AS "agencyCode",
                d.agent_version AS "agentVersion",d.last_seen_at_utc AS "lastSeenAtUtc",
                d.update_ring_code AS "updateRingCode",d.backup_client_version AS "backupClientVersion",
                COALESCE(NULLIF(d.interactive_user_display_name,''),NULLIF(d.interactive_user_account,'')) AS "currentUser",
                a.attempt_id AS "attemptId",a.release_id AS "releaseId",COALESCE(u.updater_version,a.updater_version) AS "updaterVersion",
                u.last_check_at_utc AS "lastCheckAtUtc",
                COALESCE(a.phase,CASE WHEN u.device_id IS NULL THEN 'NOT_REPORTED' ELSE 'CHECKED' END) AS phase,a.code,a.downloaded_bytes AS "downloadedBytes",
                a.total_bytes AS "totalBytes",a.updated_at_utc AS "updatedAtUtc",a.confirmed_at_utc AS "confirmedAtUtc",
                a.lease_expires_at_utc<now() AND a.phase IN ('LEASED','DOWNLOADING','INSTALLING','ROLLING_BACK') AS stalled
                FROM devices d LEFT JOIN latest a ON a.device_id=d.id LEFT JOIN updater_devices u ON u.device_id=d.id WHERE d.enabled=true
                AND ($5='' OR d.update_ring_code=$5)
                AND ($1='' OR d.machine_name ILIKE '%'||$1||'%' OR d.agency_code ILIKE '%'||$1||'%'
                    OR d.interactive_user_display_name ILIKE '%'||$1||'%' OR d.interactive_user_account ILIKE '%'||$1||'%'))
            SELECT jsonb_build_object('total',(SELECT count(*) FROM fleet),'page',$2::integer,'pageSize',$4::integer,
                'items',COALESCE((SELECT jsonb_agg(row_to_json(f)) FROM (SELECT * FROM fleet ORDER BY "machineName","deviceId" LIMIT $4 OFFSET $3) f),'[]'::jsonb),
                'policies',COALESCE((SELECT jsonb_agg(jsonb_build_object('releaseId',release_id,'policy',policy,'revision',revision,'halted',paused_automatically,'reason',pause_reason)) FROM updater_release_policies),'[]'::jsonb))::text
            """);
        command.Parameters.AddWithValue(search?.Trim() ?? ""); command.Parameters.AddWithValue(page); command.Parameters.AddWithValue((page - 1L) * pageSize); command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue(ringCode);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<JsonElement> ReadHistoryAsync(Guid device, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT COALESCE(jsonb_agg(row_to_json(e)),'[]'::jsonb)::text FROM
            (SELECT attempt_id AS "attemptId",phase,code,occurred_at_utc AS "occurredAtUtc",received_at_utc AS "receivedAtUtc",payload
             FROM updater_events WHERE device_id=$1 ORDER BY received_at_utc DESC,sequence DESC LIMIT 200) e
            """);
        command.Parameters.AddWithValue(device); return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<SignedUpdaterControl?> ReadControlAsync(string purpose, Guid release, CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT envelope::text FROM updater_controls WHERE purpose=$1 AND ($2='00000000-0000-0000-0000-000000000000'::uuid OR release_id=$2) AND expires_at_utc>now() ORDER BY generation DESC,created_at_utc DESC LIMIT 1");
        command.Parameters.AddWithValue(purpose); command.Parameters.AddWithValue(release);
        return await command.ExecuteScalarAsync(ct) is string text ? JsonSerializer.Deserialize<SignedUpdaterControl>(text, Json) : null;
    }

    internal static DateTimeOffset? Utc(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : new(reader.GetDateTime(ordinal), TimeSpan.Zero);
    internal static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        return command;
    }
    internal static async Task<int> ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(c, tx, sql, values); return await command.ExecuteNonQueryAsync(ct); }
    internal static Task<int> AuditAsync(NpgsqlConnection c, NpgsqlTransaction tx, string actor, string action, string target, object detail, CancellationToken ct) =>
        ExecuteAsync(c, tx, "INSERT INTO audit_events(occurred_at_utc,actor_type,actor_id,action,target_type,target_id,detail) VALUES(now(),$1,$2,$3,'AtlasRelease',$4,$5::jsonb)",
            ct, actor == "SYSTEM" ? "System" : "Administrator", actor, action, target, JsonSerializer.Serialize(detail, Json));
}
