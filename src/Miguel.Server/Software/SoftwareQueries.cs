using System.Data;
using System.Text.Json;
using Npgsql;
using Miguel.Contracts;

namespace Miguel.Server.Software;

public sealed partial class SoftwareRepository
{
    private const string Inventory = """
        SELECT e.*, array_remove(ARRAY[
          CASE WHEN e.authorization_status='PROHIBITED' THEN 'PROHIBITED' END,
          CASE WHEN e.authorization_status='RESTRICTED' THEN 'RESTRICTED' END,
          CASE WHEN e.authorization_status='UNREVIEWED' THEN 'UNREVIEWED' END,
          CASE WHEN e.support_ends_on<CURRENT_DATE THEN 'END_OF_SUPPORT' END,
          CASE WHEN e.license_metric='UNREVIEWED' THEN 'LICENSE_UNREVIEWED' END,
          CASE WHEN e.license_metric IN ('USER','OTHER') THEN 'MANUAL_LICENSE_REVIEW' END,
          CASE WHEN e.last_seen_at_utc<e.inventory_at_utc OR e.inventory_at_utc<now()-interval '72 hours' THEN 'UNVERIFIED' END,
          CASE WHEN e.license_metric='DEVICE' AND NOT EXISTS(
            SELECT 1 FROM software_license_assignments a JOIN software_licenses l ON l.id=a.license_id
            WHERE a.device_id=e.device_id AND a.released_at_utc IS NULL AND l.product_id=e.product_id AND l.metric='DEVICE'
              AND l.active AND l.starts_on<=CURRENT_DATE AND (l.expires_on IS NULL OR l.expires_on>=CURRENT_DATE)
              AND (cardinality(l.covered_versions)=0 OR e.version=ANY(l.covered_versions))) THEN 'UNLICENSED' END
        ],NULL) AS issues
        FROM software_effective_inventory e WHERE e.enabled AND e.retired_at_utc IS NULL
        """;
    private const string Filter = """
        ($1='' OR name ILIKE $1 ESCAPE '\' OR publisher ILIKE $1 ESCAPE '\' OR machine_name ILIKE $1 ESCAPE '\' OR current_user_name ILIKE $1 ESCAPE '\')
        AND ($2='' OR agency_code=$2) AND ($4='' OR product_id=$4) AND ($5='' OR device_id::text=$5)
        """;
    private static string Pattern(string value) => value.Length == 0 ? "" : "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    private static void ValidateQuery(SoftwareQuery q)
    {
        Text(q.Search, 120); Text(q.Agency, 64); Text(q.Status, 40); Text(q.Product, 64); Text(q.Device, 36);
        if (q.Product.Length > 0) Key(q.Product);
        if (q.Device.Length > 0 && !Guid.TryParse(q.Device, out _)) throw new ArgumentException("invalid_device");
        if (q.Page is < 1 or > 1000000) throw new ArgumentException("invalid_page");
        Choice(q.View, "products", "installations", "devices", "licenses", "findings", "actions", "reviews", "changes");
    }

    public async Task<JsonElement> Query(SoftwareQuery q, CancellationToken ct, int size = 25)
    {
        ValidateQuery(q); if (size is < 1 or > 10000) throw new ArgumentException("invalid_page_size");
        string sql = q.View switch
        {
            "products" => """
                SELECT p.*,COALESCE(x.devices,0) AS devices,COALESCE(x.installations,0) AS installations,
                  COALESCE(x.versions,'') AS versions,COALESCE(x.pending,0) AS pending,
                  COALESCE(r.authorization_status,'UNREVIEWED') AS authorization_status,r.support_ends_on
                FROM software_products p LEFT JOIN LATERAL (
                  SELECT count(DISTINCT device_id) devices,count(*) installations,string_agg(DISTINCT version,', ' ORDER BY version) versions,
                    count(*) FILTER(WHERE cardinality(issues)>0) pending FROM inventory WHERE product_id=p.id
                    AND ($2='' OR agency_code=$2) AND ($5='' OR device_id::text=$5)
                ) x ON true LEFT JOIN software_rules r ON r.product_id=p.id AND r.version='*'
                WHERE ($1='' OR p.name ILIKE $1 ESCAPE '\' OR p.publisher ILIKE $1 ESCAPE '\')
                  AND ($4='' OR p.id=$4) AND (($2='' AND $5='') OR x.devices>0)
                  AND ($3='' OR COALESCE(r.authorization_status,'UNREVIEWED')=$3)
                """,
            "installations" or "findings" => $"SELECT * FROM inventory WHERE {Filter} AND ($3='' OR $3=ANY(issues))" + (q.View == "findings" ? " AND cardinality(issues)>0" : ""),
            "devices" => """
                SELECT d.id,d.machine_name,d.agency_code,COALESCE(d.interactive_user_display_name,d.interactive_user_account,'') current_user_name,
                  d.interactive_user_checked_at_utc,d.agent_version,s.captured_at_utc,s.received_at_utc,COALESCE(s.sources,'[]'::jsonb) sources,
                  (SELECT count(*) FROM software_installations i WHERE i.device_id=d.id AND i.removed_at_utc IS NULL) applications,
                  CASE WHEN s.device_id IS NULL THEN 'NOT_REPORTED' WHEN s.captured_at_utc<now()-interval '72 hours' THEN 'STALE'
                    WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(s.sources) x WHERE x->>'status'<>'COMPLETE') THEN 'PARTIAL' ELSE 'REPORTED' END AS status
                FROM devices d LEFT JOIN software_devices s ON s.device_id=d.id
                WHERE d.enabled AND d.retired_at_utc IS NULL AND ($1='' OR d.machine_name ILIKE $1 ESCAPE '\' OR COALESCE(d.interactive_user_display_name,d.interactive_user_account,'') ILIKE $1 ESCAPE '\')
                  AND ($2='' OR d.agency_code=$2) AND ($5='' OR d.id::text=$5) AND ($4='' OR EXISTS(SELECT 1 FROM inventory i WHERE i.device_id=d.id AND i.product_id=$4))
                """,
            "licenses" => """
                SELECT l.*,p.name,p.publisher,(SELECT count(*) FROM software_license_assignments a WHERE a.license_id=l.id AND a.released_at_utc IS NULL) AS assigned,
                  CASE WHEN NOT l.active THEN 'INACTIVE' WHEN l.starts_on>CURRENT_DATE THEN 'FUTURE' WHEN l.expires_on<CURRENT_DATE THEN 'EXPIRED'
                    WHEN l.expires_on<=CURRENT_DATE+30 THEN 'EXPIRING' ELSE 'CURRENT' END status
                FROM software_licenses l JOIN software_products p ON p.id=l.product_id WHERE ($1='' OR p.name ILIKE $1 ESCAPE '\' OR l.reference ILIKE $1 ESCAPE '\')
                  AND ($4='' OR l.product_id=$4) AND ($2='' OR EXISTS(SELECT 1 FROM software_license_assignments a JOIN devices d ON d.id=a.device_id WHERE a.license_id=l.id AND a.released_at_utc IS NULL AND d.agency_code=$2))
                  AND ($5='' OR EXISTS(SELECT 1 FROM software_license_assignments a WHERE a.license_id=l.id AND a.released_at_utc IS NULL AND a.device_id::text=$5))
                """,
            "actions" => """
                SELECT a.*,p.name,d.machine_name,d.agency_code FROM software_actions a LEFT JOIN software_products p ON p.id=a.product_id LEFT JOIN devices d ON d.id=a.device_id
                WHERE ($1='' OR a.title ILIKE $1 ESCAPE '\' OR a.owner ILIKE $1 ESCAPE '\') AND ($2='' OR d.agency_code=$2) AND ($4='' OR a.product_id=$4) AND ($5='' OR a.device_id::text=$5)
                """,
            "changes" => """
                SELECT c.*,p.name,p.publisher,d.machine_name,d.agency_code FROM software_changes c JOIN software_products p ON p.id=c.product_id JOIN devices d ON d.id=c.device_id
                WHERE ($1='' OR p.name ILIKE $1 ESCAPE '\' OR d.machine_name ILIKE $1 ESCAPE '\') AND ($2='' OR d.agency_code=$2) AND ($4='' OR c.product_id=$4) AND ($5='' OR c.device_id::text=$5) AND ($3='' OR c.kind=$3)
                """,
            _ => "SELECT id,period,executed_by,validated_by,reference,conclusion,created_at_utc,next_due_on,sha256 FROM software_reviews WHERE ($1='' OR period ILIKE $1 ESCAPE '\\' OR conclusion ILIKE $1 ESCAPE '\\') AND $2='' AND $4='' AND $5=''"
        };
        if (q.View is "devices" or "licenses" or "actions") sql = $"SELECT * FROM ({sql}) classified WHERE ($3='' OR status=$3)";
        string order = q.View switch { "products" => "name,id", "installations" or "findings" => "machine_name,name,installation_key", "devices" => "machine_name,id", "licenses" => "name,reference,id", "actions" => "due_on,id", "reviews" => "created_at_utc DESC,id", _ => "received_at_utc DESC,id DESC" };
        await using var c = await Open(ct);
        return await Read(c, null, $"""
            WITH inventory AS ({Inventory}), filtered AS ({sql}), numbered AS (SELECT * FROM filtered ORDER BY {order} LIMIT $6 OFFSET $7)
            SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(to_jsonb(n)) FROM numbered n),'[]'::jsonb),'total',(SELECT count(*) FROM filtered),'page',$8::int,'pageSize',$6::int)::text
            """, ct, Pattern(q.Search.Trim()), q.Agency, q.Status, q.Product, q.Device, size, (q.Page - 1) * size, q.Page);
    }

    public async Task<JsonElement> Summary(CancellationToken ct)
    {
        await using var c = await Open(ct);
        return await Read(c, null, $"""
            WITH inventory AS ({Inventory})
            SELECT jsonb_build_object('activeDevices',(SELECT count(*) FROM devices WHERE enabled AND retired_at_utc IS NULL),
              'reportedDevices',(SELECT count(*) FROM software_devices s JOIN devices d ON d.id=s.device_id WHERE d.enabled AND d.retired_at_utc IS NULL),
              'products',(SELECT count(DISTINCT product_id) FROM inventory),'affectedDevices',(SELECT count(DISTINCT device_id) FROM inventory WHERE cardinality(issues)>0),
              'registeredLicenses',(SELECT count(*) FROM software_licenses),
              'expiringLicenses',(SELECT count(*) FROM software_licenses WHERE active AND expires_on<=CURRENT_DATE+30),
              'openActions',(SELECT count(*) FROM software_actions WHERE status<>'RESOLVED'),
              'reviewDue',NOT EXISTS(SELECT 1 FROM software_reviews WHERE next_due_on>=CURRENT_DATE),
              'lastReview',(SELECT max(created_at_utc) FROM software_reviews),
              'agencies',(SELECT COALESCE(jsonb_agg(a ORDER BY a),'[]'::jsonb) FROM (SELECT DISTINCT agency_code a FROM devices WHERE enabled AND retired_at_utc IS NULL) t))::text
            """, ct);
    }

    public async Task<JsonElement> ProductDetail(string id, CancellationToken ct)
    {
        Key(id); await using var c = await Open(ct);
        return await Read(c, null, """
            SELECT jsonb_build_object('product',to_jsonb(p),'rules',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY version) FROM software_rules r WHERE product_id=$1),'[]'::jsonb),
              'versions',COALESCE((SELECT jsonb_agg(v ORDER BY v) FROM (SELECT DISTINCT version v FROM software_installations WHERE product_id=$1 AND removed_at_utc IS NULL) versions),'[]'::jsonb))::text
              FROM software_products p WHERE id=$1
            """, ct, id);
    }

    public async Task<JsonElement> Assignments(Guid id, CancellationToken ct)
    {
        await using var c = await Open(ct);
        return await Read(c, null, "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY assigned_at_utc DESC),'[]'::jsonb)::text FROM (SELECT a.*,d.machine_name FROM software_license_assignments a LEFT JOIN devices d ON d.id=a.device_id WHERE license_id=$1) t", ct, id);
    }

    public async Task<string> CreateProduct(string name, string publisher, string actor, CancellationToken ct)
    {
        name = Text(name, 512, true); publisher = Text(publisher, 512);
        string id = SoftwareIdentity.Product(name, publisher);
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        if (await Exec(c, tx, "INSERT INTO software_products(id,name,publisher) VALUES($1,$2,$3) ON CONFLICT DO NOTHING", ct, id, name, publisher) == 1)
            await Audit(c, tx, actor, "product.created", id, new { name, publisher, origin = "MANUAL_CATALOG" }, ct);
        await tx.CommitAsync(ct); return id;
    }

    public async Task<JsonElement> CreateReview(SoftwareReviewRequest r, string actor, CancellationToken ct)
    {
        Text(r.Period, 7, true); Text(r.ExecutedBy, 255, true); Text(r.ValidatedBy, 255, true); Text(r.Reference, 1000, true); Text(r.Conclusion, 4000, true, multiline: true);
        var now = clock.GetUtcNow();
        if (r.RequestId == Guid.Empty || !System.Text.RegularExpressions.Regex.IsMatch(r.Period, @"^20\d{2}-S[12]$")) throw new ArgumentException("invalid_review_period");
        int year = int.Parse(r.Period[..4]); int half = r.Period[^1] - '0';
        DateOnly start = new(year, half == 1 ? 1 : 7, 1); DateOnly end = start.AddMonths(6).AddDays(-1);
        if (start > DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(operationalZone)).DateTime) || year < 2026) throw new ArgumentException("future_review_period");
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var previous = await Read(c, tx, "SELECT row_to_json(t)::text FROM (SELECT * FROM software_reviews WHERE id=$1 OR period=$2) t LIMIT 1", ct, r.RequestId, r.Period);
        if (previous.ValueKind != JsonValueKind.Null)
        {
            if (previous.GetProperty("id").GetGuid() == r.RequestId && previous.GetProperty("period").GetString() == r.Period &&
                previous.GetProperty("executed_by").GetString() == r.ExecutedBy && previous.GetProperty("validated_by").GetString() == r.ValidatedBy &&
                previous.GetProperty("reference").GetString() == r.Reference && previous.GetProperty("conclusion").GetString() == r.Conclusion) return previous;
            throw new InvalidOperationException("review_already_recorded");
        }
        var evidence = await Read(c, tx, $"""
            WITH inventory AS ({Inventory}) SELECT jsonb_build_object(
              'capturedAtUtc',$1::timestamptz,'scope','Todos los equipos activos; inventario observado al cerrar la revisión, no reconstrucción histórica del semestre',
              'devices',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',d.id,'machineName',d.machine_name,'agency',d.agency_code,'inventoryAtUtc',s.captured_at_utc,'sources',s.sources))
                 FROM devices d LEFT JOIN software_devices s ON s.device_id=d.id WHERE d.enabled AND d.retired_at_utc IS NULL),'[]'::jsonb),
              'inventory',COALESCE((SELECT jsonb_agg(to_jsonb(t)) FROM (SELECT product_id,name,publisher,version,agency_code,authorization_status,license_metric,support_ends_on,
                 count(DISTINCT device_id) devices,count(*) installations,count(*) FILTER(WHERE cardinality(issues)>0) pending FROM inventory GROUP BY product_id,name,publisher,version,agency_code,authorization_status,license_metric,support_ends_on) t),'[]'::jsonb),
              'rules',COALESCE((SELECT jsonb_agg(to_jsonb(r)) FROM software_rules r),'[]'::jsonb),
              'licenses',COALESCE((SELECT jsonb_agg(to_jsonb(l)) FROM software_licenses l),'[]'::jsonb),
              'assignments',COALESCE((SELECT jsonb_agg(to_jsonb(a)) FROM software_license_assignments a WHERE released_at_utc IS NULL),'[]'::jsonb),
              'actions',COALESCE((SELECT jsonb_agg(to_jsonb(a)) FROM software_actions a),'[]'::jsonb))::text
            """, ct, now);
        string snapshot = JsonSerializer.Serialize(evidence); if (snapshot.Length > 50_000_000) throw new ArgumentException("review_too_large");
        string hash = SoftwareIdentity.Hash(snapshot);
        await Exec(c, tx, "INSERT INTO software_reviews(id,period,executed_by,validated_by,reference,conclusion,created_at_utc,next_due_on,evidence,sha256) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10)",
            ct, r.RequestId, r.Period, r.ExecutedBy, r.ValidatedBy, r.Reference, r.Conclusion, now, start.AddMonths(12).AddDays(-1), snapshot, hash);
        await Audit(c, tx, actor, "review.recorded", r.RequestId.ToString(), new { r.Period, r.ExecutedBy, r.ValidatedBy, r.Reference, hash, anticipatoryPolicy = true }, ct);
        var saved = await Read(c, tx, "SELECT row_to_json(t)::text FROM (SELECT * FROM software_reviews WHERE id=$1) t", ct, r.RequestId);
        await tx.CommitAsync(ct); return saved;
    }

    public async Task<JsonElement> Review(Guid id, CancellationToken ct)
    { await using var c = await Open(ct); return await Read(c, null, "SELECT row_to_json(t)::text FROM (SELECT * FROM software_reviews WHERE id=$1) t", ct, id); }
}
