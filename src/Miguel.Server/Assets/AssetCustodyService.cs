using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miguel.Server.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Assets;

public sealed partial class AssetCustodyService(NpgsqlDataSource db, TimeProvider clock)
{
    public sealed record HeldDevice(Guid DeviceId,string MachineName,string Custodian,bool BackupsPaused,DateTimeOffset? ReviewDueUtc);
    public async Task<IReadOnlyList<HeldDevice>> ReadHeld(CancellationToken ct)
    {
        List<HeldDevice> held=[];
        await using var cmd=db.CreateCommand("SELECT d.id,d.machine_name,c.custodian,c.backups_paused,c.review_due_utc FROM asset_custody c JOIN devices d ON d.id=c.current_device_id WHERE d.enabled AND c.state<>'IN_USE'");
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))held.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),Date(r,4)));
        return held;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string StateColumns = "device_id,revision,state,assigned_to,custodian,location,receipt::text,preparation::text,backups_paused,pause_reason,review_due_utc,tracking_since_utc,updated_at_utc,updated_by,current_device_id";
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static T? Decode<T>(string json) => json is "{}" or "null" ? default : JsonSerializer.Deserialize<T>(json, Json);
    private static AssetCustodyState State(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetInt64(1), r.GetString(2), Str(r,3),
        r.GetString(4), r.GetString(5), Decode<AssetReceipt>(r.GetString(6)), Decode<AssetPreparation>(r.GetString(7)),
        r.GetBoolean(8), Str(r,9), Date(r,10), Date(r,11), Date(r,12), Str(r,13),r.GetGuid(14));
    private static AssetCustodyState Empty(Guid id) => new(id,0,"UNREGISTERED",null,"","",null,null,false,null,null,null,null,null,id);

    public async Task<IReadOnlyList<AssetCustodyState>> ReadSummaries(Guid[] ids, CancellationToken ct)
    {
        List<AssetCustodyState> states = [];
        await using var cmd = db.CreateCommand($"SELECT {StateColumns} FROM asset_custody WHERE current_device_id=ANY($1) OR device_id=ANY($1)");
        cmd.Parameters.AddWithValue(ids);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) {var state=State(r);states.Add(state with{DeviceId=state.CurrentDeviceId});if(state.DeviceId!=state.CurrentDeviceId && ids.Contains(state.DeviceId))states.Add(state);}
        return states;
    }

    public async Task<AssetCustodyView?> Read(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        return await Read(c,tx,id,false,ct);
    }

    private async Task<AssetCustodyView?> Read(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, bool locked, CancellationToken ct)
    {
        await using(var canonical=new NpgsqlCommand("SELECT asset_id FROM asset_custody_identities WHERE device_id=$1",c,tx))
        {canonical.Parameters.AddWithValue(id);if(await canonical.ExecuteScalarAsync(ct) is Guid root)id=root;}
        Guid currentId=id;
        await using(var current=new NpgsqlCommand("SELECT current_device_id FROM asset_custody WHERE device_id=$1",c,tx))
        {current.Parameters.AddWithValue(id);if(await current.ExecuteScalarAsync(ct) is Guid currentDevice)currentId=currentDevice;}
        string machine, agency, provider; string? serial, nas; bool enabled;
        await using (var cmd = new NpgsqlCommand("""
            SELECT d.machine_name,d.agency_code,d.enabled,d.backup_provider,d.nas_appliance_id::text,
                (SELECT payload#>>'{bios,serialNumber}' FROM inventory_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC LIMIT 1)
            FROM devices d WHERE d.id=$1
            """ + (locked ? " FOR UPDATE OF d" : ""), c,tx))
        {
            cmd.Parameters.AddWithValue(currentId); await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            machine=r.GetString(0);agency=r.GetString(1);enabled=r.GetBoolean(2);provider=r.GetString(3);nas=Str(r,4);serial=Str(r,5);
        }
        var state = Empty(id);
        await using (var cmd = new NpgsqlCommand($"SELECT {StateColumns} FROM asset_custody WHERE device_id=$1" + (locked ? " FOR UPDATE" : ""),c,tx))
        {
            cmd.Parameters.AddWithValue(id);await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) state=State(r);
        }
        List<AssetBackupContext> backups=[];
        await using(var cmd=new NpgsqlCommand("""
            SELECT p.id,p.code,p.provider,p.protected_scope,
              CASE WHEN p.nas_appliance_id IS NOT NULL THEN concat('\\',n.host,'\',n.share_name) ELSE p.expected_destination END,
              p.enabled,p.approval_status,p.updated_at_utc,
              CASE WHEN p.provider='ATLAS_BACKUPNAS' THEN COALESCE(n.enabled,false) AND n.agency_code=p.agency_code
                   ELSE length(trim(COALESCE(p.expected_destination,'')))>0 END
            FROM backup_control_profiles p LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id
            WHERE p.device_id=$1 ORDER BY p.id
            """ + (locked ? " FOR SHARE OF p" : ""),c,tx))
        {
            cmd.Parameters.AddWithValue(currentId);await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))backups.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),Str(r,4),r.GetBoolean(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),r.GetBoolean(8)));
        }
        List<AssetCustodyEvent> events=[];
        await using(var cmd=new NpgsqlCommand("SELECT revision,action,occurred_at_utc,actor,detail::text FROM asset_custody_events WHERE device_id=$1 ORDER BY revision DESC",c,tx))
        {
            cmd.Parameters.AddWithValue(id);await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) events.Add(new(r.GetInt64(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),r.GetString(3),JsonSerializer.Deserialize<JsonElement>(r.GetString(4))));
        }
        string version=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{state,enabled,provider,nas,agency,backups},Json))));
        List<AssetIdentityCandidate> candidates=[];
        if(state.State=="PREPARING" && !string.IsNullOrWhiteSpace(serial) && serial.Length>=4 &&
            !new[]{"unknown","default string","to be filled by o.e.m.","system serial number","none"}.Contains(serial.Trim().ToLowerInvariant()))
        {
            await using var cmd=new NpgsqlCommand("""
                SELECT d.id,d.machine_name,i.serial FROM devices d JOIN LATERAL
                  (SELECT payload#>>'{bios,serialNumber}' serial FROM inventory_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC LIMIT 1) i ON true
                WHERE d.enabled AND d.id<>$1 AND d.agency_code=$2 AND lower(trim(i.serial))=lower(trim($3))
                  AND NOT EXISTS(SELECT 1 FROM asset_custody_identities a WHERE a.device_id=d.id)
                  AND NOT EXISTS(SELECT 1 FROM asset_custody a WHERE a.device_id=d.id OR a.current_device_id=d.id)
                  AND NOT EXISTS(SELECT 1 FROM backup_control_profiles p WHERE p.device_id=d.id)
                ORDER BY d.id LIMIT 20
                """,c,tx);
            cmd.Parameters.AddWithValue(currentId);cmd.Parameters.AddWithValue(agency);cmd.Parameters.AddWithValue(serial);
            await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))candidates.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2)));
        }
        var suggestion=await ReadProposal(c,tx,currentId,ct);
        return new(machine,agency,serial,enabled,provider,version,state,backups,events,candidates,suggestion);
    }

    public async Task Change(Guid id, AssetCustodyRequest request, string actor, string role, CancellationToken ct)
    {
        string reason=AssetCustodyInput.Text(request.Reason,1000,"reason");
        if(request.ExpectedVersion?.Length!=64)throw new ArgumentException("invalid_version");
        await using var c=await db.OpenConnectionAsync(ct);
        await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
        var view=await Read(c,tx,id,true,ct) ?? throw new AssetCustodyConflict("device_not_found");
        if(!view.Enabled && request.Action!="LINK_IDENTITY")throw new AssetCustodyConflict("device_retired");
        if(view.Version!=request.ExpectedVersion)throw new AssetCustodyConflict("custody_changed");
        if(request.SuggestionVersion is not null && (request.Action!="REGISTER" || view.Suggestion?.Version!=request.SuggestionVersion))
            throw new AssetCustodyConflict("custody_suggestion_changed");
        var before=view.Custody;id=before.DeviceId;var after=before;var now=clock.GetUtcNow();
        string Required(string? value,int max,string field)=>AssetCustodyInput.Text(value,max,field);
        DateTimeOffset ReviewDate()
        {
            if(request.ReviewDueUtc is not { } due || due<=now || due>now.AddDays(90))throw new ArgumentException("invalid_review_date");
            return due;
        }
        switch(request.Action)
        {
            case "REGISTER" when before.State=="UNREGISTERED":
                after=before with{State="IN_USE",AssignedTo=Required(request.AssignedTo,160,"assignee"),Custodian=Required(request.Custodian,160,"custodian"),Location=Required(request.Location,200,"location")};
                break;
            case "RETURN" when before.State is "UNREGISTERED" or "IN_USE":
                var receipt=request.Receipt ?? throw new ArgumentException("invalid_receipt");
                receipt=new(before.AssignedTo ?? Required(receipt.PreviousAssignee,160,"previous_assignee"),
                    Required(receipt.Condition,1000,"condition"),Required(receipt.Accessories,1000,"accessories"),Required(receipt.Reference,200,"receipt_reference"));
                after=before with{State="RECEIVED",AssignedTo=null,Custodian=Required(request.Custodian,160,"custodian"),Location=Required(request.Location,200,"location"),Receipt=receipt,Preparation=null,
                    BackupsPaused=request.PauseBackups,PauseReason=request.PauseBackups?reason:null,ReviewDueUtc=ReviewDate()};
                break;
            case "START_PREPARATION" when before.State=="RECEIVED":
                after=before with{State="PREPARING"};break;
            case "SAVE_PREPARATION" when before.State=="PREPARING":
                after=before with{Preparation=AssetCustodyInput.Preparation(request.Preparation,false)};break;
            case "MAKE_AVAILABLE" when before.State=="PREPARING":
                after=before with{State="AVAILABLE",Preparation=AssetCustodyInput.Preparation(request.Preparation,true)};break;
            case "REOPEN_PREPARATION" when before.State=="AVAILABLE":
                after=before with{State="PREPARING",Preparation=before.Preparation is {} prep ? prep with{ValidatedBy=null}:null};break;
            case "REVIEW_CUSTODY" when before.State is "RECEIVED" or "PREPARING" or "AVAILABLE":
                after=before with{Custodian=Required(request.Custodian,160,"custodian"),Location=Required(request.Location,200,"location"),ReviewDueUtc=ReviewDate()};break;
            case "LINK_IDENTITY" when before.State=="PREPARING":
                if(!request.PhysicalIdentityVerified || request.NewDeviceId is not {} newId || !view.IdentityCandidates.Any(x=>x.DeviceId==newId))
                    throw new AssetCustodyConflict("identity_verification_required");
                await using(var cmd=new NpgsqlCommand("SELECT id FROM devices WHERE id=$1 AND enabled FOR UPDATE",c,tx))
                {cmd.Parameters.AddWithValue(newId);if(await cmd.ExecuteScalarAsync(ct) is not Guid)throw new AssetCustodyConflict("custody_changed");}
                await using(var cmd=new NpgsqlCommand("UPDATE devices SET enabled=false,retired_at_utc=$2,retired_by=$3,retirement_reason='Identidad sustituida durante preparación; historial conservado' WHERE id=$1 AND enabled",c,tx))
                {cmd.Parameters.AddWithValue(before.CurrentDeviceId);cmd.Parameters.AddWithValue(now);cmd.Parameters.AddWithValue(actor);await cmd.ExecuteNonQueryAsync(ct);}
                await using(var cmd=new NpgsqlCommand("UPDATE backup_control_profiles SET device_id=$2,system_name=$3,updated_at_utc=$4,updated_by=$5 WHERE device_id=$1",c,tx))
                {cmd.Parameters.AddWithValue(before.CurrentDeviceId);cmd.Parameters.AddWithValue(newId);cmd.Parameters.AddWithValue(view.IdentityCandidates.Single(x=>x.DeviceId==newId).MachineName);cmd.Parameters.AddWithValue(now);cmd.Parameters.AddWithValue(actor);await cmd.ExecuteNonQueryAsync(ct);}
                after=before with{CurrentDeviceId=newId,Preparation=null};
                break;
            case "ASSIGN" when before.State=="AVAILABLE":
                if(!request.BackupReviewed)throw new ArgumentException("backup_review_required");
                var active=view.Backups.Where(b=>b.Enabled && b.Status is "ACTIVE" or "APPROVED").ToArray();
                if(view.BackupProvider!="SHARED_FOLDER" && active.Length==0)throw new AssetCustodyConflict("backup_control_required");
                string expected=view.BackupProvider=="ATLAS"?"ATLAS_BACKUPNAS":view.BackupProvider;
                if(active.Any(b=>!b.DestinationValid || (view.BackupProvider!="SHARED_FOLDER" && b.Provider!=expected)))throw new AssetCustodyConflict("backup_configuration_required");
                after=before with{State="IN_USE",AssignedTo=Required(request.AssignedTo,160,"assignee"),Custodian=Required(request.Custodian,160,"custodian"),Location=Required(request.Location,200,"location"),
                    BackupsPaused=false,PauseReason=null,ReviewDueUtc=null,TrackingSinceUtc=now};
                break;
            default:throw new AssetCustodyConflict("invalid_custody_transition");
        }
        after=after with{Revision=before.Revision+1,UpdatedAtUtc=now,UpdatedBy=actor};
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO asset_custody(device_id,revision,state,assigned_to,custodian,location,receipt,preparation,backups_paused,pause_reason,review_due_utc,tracking_since_utc,updated_at_utc,updated_by,current_device_id)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            ON CONFLICT(device_id) DO UPDATE SET revision=EXCLUDED.revision,state=EXCLUDED.state,assigned_to=EXCLUDED.assigned_to,custodian=EXCLUDED.custodian,
              location=EXCLUDED.location,receipt=EXCLUDED.receipt,preparation=EXCLUDED.preparation,backups_paused=EXCLUDED.backups_paused,pause_reason=EXCLUDED.pause_reason,
              review_due_utc=EXCLUDED.review_due_utc,tracking_since_utc=EXCLUDED.tracking_since_utc,updated_at_utc=EXCLUDED.updated_at_utc,updated_by=EXCLUDED.updated_by,current_device_id=EXCLUDED.current_device_id
            """,c,tx))
        {
            cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(after.Revision);cmd.Parameters.AddWithValue(after.State);
            cmd.Parameters.AddWithValue((object?)after.AssignedTo??DBNull.Value);cmd.Parameters.AddWithValue(after.Custodian);cmd.Parameters.AddWithValue(after.Location);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(after.Receipt,Json));cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(after.Preparation,Json));
            cmd.Parameters.AddWithValue(after.BackupsPaused);cmd.Parameters.AddWithValue((object?)after.PauseReason??DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz,(object?)after.ReviewDueUtc??DBNull.Value);cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz,(object?)after.TrackingSinceUtc??DBNull.Value);
            cmd.Parameters.AddWithValue(now);cmd.Parameters.AddWithValue(actor);cmd.Parameters.AddWithValue(after.CurrentDeviceId);await cmd.ExecuteNonQueryAsync(ct);
        }
        await using(var cmd=new NpgsqlCommand("INSERT INTO asset_custody_identities(device_id,asset_id) VALUES($1,$2) ON CONFLICT(device_id) DO NOTHING",c,tx))
        {cmd.Parameters.AddWithValue(after.CurrentDeviceId);cmd.Parameters.AddWithValue(id);await cmd.ExecuteNonQueryAsync(ct);}
        // Identical before/after evidence and audit commit with the state; a failure rolls back all three.
        var detail=new{reason,before,after,backupReviewed=request.Action=="ASSIGN"&&request.BackupReviewed,
            backupContext=view.Backups,initialSuggestion=request.SuggestionVersion is not null?view.Suggestion:null,
            policyReference="V4 pp.6,9-10,13-16,23-26; aplicación anticipada"};
        await using(var cmd=new NpgsqlCommand("INSERT INTO asset_custody_events(id,device_id,revision,action,occurred_at_utc,actor,detail) VALUES($1,$2,$3,$4,$5,$6,$7)",c,tx))
        {
            cmd.Parameters.AddWithValue(Guid.NewGuid());cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(after.Revision);cmd.Parameters.AddWithValue(request.Action);
            cmd.Parameters.AddWithValue(now);cmd.Parameters.AddWithValue(actor);cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(detail,Json));await cmd.ExecuteNonQueryAsync(ct);
        }
        await new AdminRepository(db).WriteAuditAsync(now,role,actor,"admin.device.custody."+request.Action.ToLowerInvariant(),"Device",id.ToString(),detail,ct,tx);
        await tx.CommitAsync(ct);
    }
}
