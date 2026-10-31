using System.Globalization;
using System.Text.Json;
using Miguel.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Miguel.Server.Software;

public sealed partial class SoftwareRepository(NpgsqlDataSource db, TimeProvider clock, IOptions<ServerOptions>? options = null)
{
    private readonly string operationalZone = options?.Value.OperationalTimeZoneId ?? "America/Guatemala";
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var connection = await db.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(connection, null, "SELECT set_config('TimeZone',$1,false)", operationalZone);
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object[] values)
    { var command = new NpgsqlCommand(sql, c, tx); foreach (object value in values) command.Parameters.AddWithValue(value); return command; }
    internal static async Task<int> Exec(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(c, tx, sql, values); return await command.ExecuteNonQueryAsync(ct); }
    private static async Task ExecInventoryBatch(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] values)
    {
        await using var batch = new NpgsqlBatch(c, tx);
        // Only the fixed ingestion statements below use this helper, with no semicolons in SQL literals.
        foreach (string statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { var command = new NpgsqlBatchCommand(statement); foreach (object value in values) command.Parameters.AddWithValue(value); batch.BatchCommands.Add(command); }
        await batch.ExecuteNonQueryAsync(ct);
    }
    internal static async Task<JsonElement> Read(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(c, tx, sql, values); string result = (string)(await command.ExecuteScalarAsync(ct) ?? "null"); using var doc = JsonDocument.Parse(result); return doc.RootElement.Clone(); }
    internal static Task<int> Audit(NpgsqlConnection c, NpgsqlTransaction tx, string actor, string action, string target, object detail, CancellationToken ct) =>
        Exec(c, tx, "INSERT INTO audit_events(occurred_at_utc,actor_type,actor_id,action,target_type,target_id,detail) VALUES(now(),'Administrator',$1,$2,'Software',$3,$4::jsonb)", ct, actor, "admin.software." + action, target, JsonSerializer.Serialize(detail, Json));
    internal static string Text(string? value, int max, bool required = false, bool multiline = false)
    { if (value is null || value.Length > max || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')) || required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("invalid_text"); return value.Trim(); }
    private static void Choice(string value, params string[] allowed) { if (!allowed.Contains(value)) throw new ArgumentException("invalid_choice"); }
    private static void Key(string? key) { if (key is null || key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("invalid_product"); }

    public static void ValidateInventory(SoftwareInventory sw, DateTimeOffset captured, DateTimeOffset now)
    {
        if (sw.SchemaVersion != 1 || sw.Sources is null || sw.Applications is null || sw.Sources.Count is < 1 or > 505 || sw.Applications.Count > 10000 ||
            captured > now.AddMinutes(15) || captured < DateTimeOffset.UnixEpoch) throw new ArgumentException("invalid_software_inventory");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sw.Sources)
        {
            if (s is null) throw new ArgumentException("invalid_source");
            Text(s.Id, 100, true); Text(s.Reason ?? "", 100); Choice(s.Kind, "MACHINE", "USER", "PACKAGE"); Choice(s.Status, "COMPLETE", "PARTIAL", "UNAVAILABLE");
            if (!sources.Add(s.Id)) throw new ArgumentException("duplicate_source");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in sw.Applications)
        {
            if (a is null) throw new ArgumentException("invalid_application");
            Key(a.InstallationKey); Text(a.Name, 512, true); Text(a.Publisher, 512); Text(a.Version, 128); Text(a.Architecture, 32); Choice(a.Scope, "MACHINE", "USER", "PACKAGE");
            if (!sources.Contains(a.SourceId) || !keys.Add(a.InstallationKey) || sw.Sources.Single(s => s.Id == a.SourceId).Kind != a.Scope ||
                a.InstalledOn is not null && !DateOnly.TryParseExact(a.InstalledOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("invalid_application_source");
        }
    }

    // Caller holds the device row lock and commits inventory + projection together.
    internal static async Task Record(NpgsqlConnection c, NpgsqlTransaction tx, Guid device, DeviceInventory inventory, DateTimeOffset now, CancellationToken ct)
    {
        var sw = inventory.Software!;
        await using (var previous = Command(c, tx, "SELECT captured_at_utc FROM software_devices WHERE device_id=$1", device))
            if (await previous.ExecuteScalarAsync(ct) is DateTime stamp && new DateTimeOffset(stamp, TimeSpan.Zero) >= inventory.CapturedAtUtc) return;
        string payload = JsonSerializer.Serialize(sw.Applications.Select(a => new { a.InstallationKey, a.SourceId, productId = SoftwareIdentity.Product(a.Name, a.Publisher), a.Name, a.Publisher, a.Version, a.Architecture, a.Scope, a.InstalledOn }), Json);
        // Temp relations are transaction-scoped; raw JSON is always a parameter.
        await ExecInventoryBatch(c, tx, """
            CREATE TEMP TABLE incoming_software ON COMMIT DROP AS
              SELECT * FROM jsonb_to_recordset($1::jsonb) AS x("installationKey" text,"sourceId" text,"productId" text,name text,publisher text,version text,architecture text,scope text,"installedOn" text);
            INSERT INTO software_products(id,name,publisher)
              SELECT DISTINCT ON ("productId") "productId",name,publisher FROM incoming_software ORDER BY "productId",name,publisher ON CONFLICT(id) DO NOTHING;
            INSERT INTO software_changes(device_id,snapshot_id,product_id,installation_key,kind,old_version,new_version,captured_at_utc,received_at_utc)
              SELECT $2,$3,n."productId",n."installationKey",CASE WHEN old.installation_key IS NULL OR old.removed_at_utc IS NOT NULL THEN 'DETECTED' ELSE 'CHANGED' END,
                old.version,n.version,$4,$5 FROM incoming_software n LEFT JOIN software_installations old ON old.device_id=$2 AND old.installation_key=n."installationKey"
              WHERE old.installation_key IS NULL OR old.removed_at_utc IS NOT NULL OR old.version<>n.version OR old.product_id<>n."productId"
              ON CONFLICT DO NOTHING;
            INSERT INTO software_installations(device_id,installation_key,source_id,product_id,version,architecture,scope,installed_on,first_seen_at_utc,last_seen_at_utc)
              SELECT $2,"installationKey","sourceId","productId",version,architecture,scope,"installedOn"::date,$4,$4 FROM incoming_software
              ON CONFLICT(device_id,installation_key) DO UPDATE SET source_id=excluded.source_id,product_id=excluded.product_id,version=excluded.version,
                architecture=excluded.architecture,scope=excluded.scope,installed_on=excluded.installed_on,last_seen_at_utc=excluded.last_seen_at_utc,removed_at_utc=NULL;
            INSERT INTO software_changes(device_id,snapshot_id,product_id,installation_key,kind,old_version,captured_at_utc,received_at_utc)
              SELECT $2,$3,old.product_id,old.installation_key,'REMOVED',old.version,$4,$5 FROM software_installations old
              WHERE old.device_id=$2 AND old.removed_at_utc IS NULL AND old.source_id=ANY($6)
                AND NOT EXISTS(SELECT 1 FROM incoming_software n WHERE n."installationKey"=old.installation_key) ON CONFLICT DO NOTHING;
            UPDATE software_installations old SET removed_at_utc=$4 WHERE old.device_id=$2 AND old.removed_at_utc IS NULL AND old.source_id=ANY($6)
                AND NOT EXISTS(SELECT 1 FROM incoming_software n WHERE n."installationKey"=old.installation_key);
            INSERT INTO software_devices(device_id,snapshot_id,captured_at_utc,received_at_utc,sources) VALUES($2,$3,$4,$5,$7::jsonb)
              ON CONFLICT(device_id) DO UPDATE SET snapshot_id=excluded.snapshot_id,captured_at_utc=excluded.captured_at_utc,received_at_utc=excluded.received_at_utc,sources=excluded.sources;
            """, ct, payload, device, inventory.SnapshotId, inventory.CapturedAtUtc, now, sw.Sources.Where(s => s.Status == "COMPLETE").Select(s => s.Id).ToArray(), JsonSerializer.Serialize(sw.Sources, Json));
    }

    public async Task SaveProduct(string id, SoftwareProductRequest request, string actor, CancellationToken ct)
    {
        Key(id); Text(request.Category, 100); Choice(request.LicenseMetric, "UNREVIEWED", "FREE", "DEVICE", "USER", "OTHER");
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        int count = await Exec(c, tx, "UPDATE software_products SET category=$2,license_metric=$3,revision=revision+1 WHERE id=$1 AND revision=$4", ct, id, request.Category.Trim(), request.LicenseMetric, request.ExpectedRevision);
        if (count != 1) throw new InvalidOperationException("revision_conflict");
        await Audit(c, tx, actor, "product.saved", id, request, ct); await tx.CommitAsync(ct);
    }

    public async Task SaveRule(string id, SoftwareRuleRequest request, string actor, CancellationToken ct)
    {
        Key(id); Text(request.Version, 128, true); Text(request.Reference, 1000, true); Text(request.Notes, 2000, multiline: true);
        Choice(request.Authorization, "UNREVIEWED", "AUTHORIZED", "RESTRICTED", "PROHIBITED");
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        int count = await Exec(c, tx, """
            INSERT INTO software_rules(product_id,version,authorization_status,support_ends_on,reference,notes,revision,updated_by)
              SELECT $1,$2,$3,NULLIF($4,'')::date,$5,$6,1,$7 WHERE $8=0
              ON CONFLICT(product_id,version) DO NOTHING;
            """, ct, id, request.Version.Trim(), request.Authorization, request.SupportEndsOn?.ToString("yyyy-MM-dd") ?? "", request.Reference.Trim(), request.Notes.Trim(), actor, request.ExpectedRevision);
        if (request.ExpectedRevision > 0) count = await Exec(c, tx, """
            UPDATE software_rules SET authorization_status=$3,support_ends_on=NULLIF($4,'')::date,reference=$5,notes=$6,revision=revision+1,updated_by=$7,updated_at_utc=now()
              WHERE product_id=$1 AND version=$2 AND revision=$8
            """, ct, id, request.Version.Trim(), request.Authorization, request.SupportEndsOn?.ToString("yyyy-MM-dd") ?? "", request.Reference.Trim(), request.Notes.Trim(), actor, request.ExpectedRevision);
        if (count != 1) throw new InvalidOperationException("revision_conflict");
        await Audit(c, tx, actor, "rule.saved", id, request, ct); await tx.CommitAsync(ct);
    }

    public async Task SaveLicense(Guid id, SoftwareLicenseRequest r, string actor, CancellationToken ct)
    {
        Key(r.ProductId); Text(r.Reference, 300, true); Text(r.Notes, 2000, multiline: true); Choice(r.Metric, "DEVICE", "USER", "OTHER");
        if (id == Guid.Empty || r.Quantity is < 1 or > 1000000 || r.ExpiresOn < r.StartsOn) throw new ArgumentException("invalid_license");
        string[] coveredVersions = (r.CoveredVersions ?? []).Select(v => Text(v, 128, true)).Distinct().ToArray();
        if (coveredVersions.Length > 200) throw new ArgumentException("invalid_version_scope");
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await Exec(c, tx, "SELECT pg_advisory_xact_lock(hashtext($1))", ct, "software-license:" + id);
        JsonElement previous = await Read(c, tx, "SELECT row_to_json(t)::text FROM (SELECT product_id,metric,(SELECT count(*) FROM software_license_assignments WHERE license_id=$1 AND released_at_utc IS NULL) AS assigned FROM software_licenses WHERE id=$1) t", ct, id);
        if (previous.ValueKind != JsonValueKind.Null && (previous.GetProperty("assigned").GetInt32() > r.Quantity ||
            previous.GetProperty("assigned").GetInt32() > 0 && (previous.GetProperty("product_id").GetString() != r.ProductId || previous.GetProperty("metric").GetString() != r.Metric))) throw new ArgumentException("assigned_license_conflict");
        int count = await Exec(c, tx, """
            INSERT INTO software_licenses(id,product_id,reference,metric,quantity,starts_on,expires_on,notes,active)
              SELECT $1,$2,$3,$4,$5,$6,NULLIF($7,'')::date,$8,$9 WHERE $10=0 ON CONFLICT DO NOTHING;
            """, ct, id, r.ProductId, r.Reference.Trim(), r.Metric, r.Quantity, r.StartsOn, r.ExpiresOn?.ToString("yyyy-MM-dd") ?? "", r.Notes.Trim(), r.Active, r.ExpectedRevision);
        if (r.ExpectedRevision > 0) count = await Exec(c, tx, """
            UPDATE software_licenses SET product_id=$2,reference=$3,metric=$4,quantity=$5,starts_on=$6,expires_on=NULLIF($7,'')::date,notes=$8,active=$9,revision=revision+1
              WHERE id=$1 AND revision=$10
            """, ct, id, r.ProductId, r.Reference.Trim(), r.Metric, r.Quantity, r.StartsOn, r.ExpiresOn?.ToString("yyyy-MM-dd") ?? "", r.Notes.Trim(), r.Active, r.ExpectedRevision);
        if (count != 1) throw new InvalidOperationException("revision_conflict");
        await Exec(c, tx, "UPDATE software_licenses SET covered_versions=$2 WHERE id=$1", ct, id, coveredVersions);
        await Audit(c, tx, actor, "license.saved", id.ToString(), r, ct); await tx.CommitAsync(ct);
    }

    public async Task Assign(Guid id, SoftwareAssignmentRequest r, string actor, CancellationToken ct)
    {
        Text(r.Subject, 300);
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await Exec(c, tx, "SELECT pg_advisory_xact_lock(hashtext($1))", ct, "software-license:" + id);
        var license = await Read(c, tx, """
            SELECT row_to_json(t)::text FROM (SELECT metric,quantity,(SELECT count(*) FROM software_license_assignments WHERE license_id=$1 AND released_at_utc IS NULL) assigned
              FROM software_licenses WHERE id=$1 AND active AND starts_on<=CURRENT_DATE AND (expires_on IS NULL OR expires_on>=CURRENT_DATE)) t
            """, ct, id);
        if (license.ValueKind == JsonValueKind.Null) throw new ArgumentException("license_not_current");
        string metric = license.GetProperty("metric").GetString()!;
        if ((metric == "DEVICE") != r.DeviceId.HasValue || r.DeviceId == Guid.Empty || metric != "DEVICE" && string.IsNullOrWhiteSpace(r.Subject)) throw new ArgumentException("assignment_metric_mismatch");
        if (r.DeviceId.HasValue)
        {
            await using var device = Command(c, tx, "SELECT EXISTS(SELECT 1 FROM devices WHERE id=$1 AND enabled AND retired_at_utc IS NULL)", r.DeviceId.Value);
            if (!(bool)(await device.ExecuteScalarAsync(ct))!) throw new ArgumentException("device_not_active");
        }
        await using var duplicate = Command(c, tx, "SELECT EXISTS(SELECT 1 FROM software_license_assignments WHERE license_id=$1 AND released_at_utc IS NULL AND (device_id=$2 OR (device_id IS NULL AND lower(subject)=lower($3))))", id, r.DeviceId ?? Guid.Empty, r.Subject.Trim());
        if ((bool)(await duplicate.ExecuteScalarAsync(ct))!) return;
        if (license.GetProperty("assigned").GetInt32() >= license.GetProperty("quantity").GetInt32()) throw new ArgumentException("license_capacity_exceeded");
        Guid assignment = Guid.NewGuid();
        await Exec(c, tx, "INSERT INTO software_license_assignments(id,license_id,device_id,subject,assigned_by) VALUES($1,$2,NULLIF($3,'00000000-0000-0000-0000-000000000000'::uuid),$4,$5)", ct, assignment, id, r.DeviceId ?? Guid.Empty, metric == "DEVICE" ? "" : r.Subject.Trim(), actor);
        await Audit(c, tx, actor, "license.assigned", assignment.ToString(), new { licenseId = id, r.DeviceId, subject = metric == "DEVICE" ? "" : r.Subject.Trim() }, ct); await tx.CommitAsync(ct);
    }

    public async Task ReleaseAssignment(Guid id, string actor, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        // Only decreases capacity; concurrent assignment may conservatively ask for a retry.
        if (await Exec(c, tx, "UPDATE software_license_assignments SET released_at_utc=now() WHERE id=$1 AND released_at_utc IS NULL", ct, id) == 1)
            await Audit(c, tx, actor, "license.released", id.ToString(), new { assignmentId = id }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SaveAction(Guid id, SoftwareActionRequest r, string actor, CancellationToken ct)
    {
        if (r.ProductId is not null) Key(r.ProductId);
        Text(r.Title, 300, true); Text(r.Owner, 255, true); Text(r.Result, 2000, r.Status == "RESOLVED", multiline: true); Text(r.Reference, 1000, r.Status == "RESOLVED");
        Choice(r.Status, "OPEN", "IN_PROGRESS", "RESOLVED");
        if (id == Guid.Empty || r.DeviceId == Guid.Empty) throw new ArgumentException("invalid_action");
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        object[] values = [id, r.ProductId ?? "", r.DeviceId ?? Guid.Empty, r.Title.Trim(), r.Owner.Trim(), r.DueOn, r.Status, r.Result.Trim(), r.Reference.Trim(), r.ExpectedRevision];
        int count = await Exec(c, tx, """
            INSERT INTO software_actions(id,product_id,device_id,title,owner,due_on,status,result,reference)
              SELECT $1,NULLIF($2,''),NULLIF($3,'00000000-0000-0000-0000-000000000000'::uuid),$4,$5,$6,$7,$8,$9 WHERE $10=0 ON CONFLICT DO NOTHING
            """, ct, values);
        if (r.ExpectedRevision > 0) count = await Exec(c, tx, """
            UPDATE software_actions SET product_id=NULLIF($2,''),device_id=NULLIF($3,'00000000-0000-0000-0000-000000000000'::uuid),title=$4,owner=$5,due_on=$6,status=$7,result=$8,reference=$9,revision=revision+1,updated_at_utc=now()
              WHERE id=$1 AND revision=$10
            """, ct, values);
        if (count != 1) throw new InvalidOperationException("revision_conflict");
        await Audit(c, tx, actor, "action.saved", id.ToString(), r, ct); await tx.CommitAsync(ct);
    }
}
