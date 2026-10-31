using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.NetworkDiscovery;

public sealed partial class NetworkDiscoveryRepository
{
    private sealed record NetworkDeviceCandidate(Guid Id,string Name,string? Mac,DateTimeOffset CapturedAtUtc,string? Manufacturer,string? Model);
    private sealed record PreviousNetworkAsset(Guid Id,Guid? DeviceId,string? Mac,string Kind,string? Name,
        string Source,string Services,string? Os,string? Type,DateTimeOffset? ServicesAt,string ServicesStatus);

    public async Task RecordSuccessfulRunAsync(NetworkScopeDefinition scope,Guid runId,
        IReadOnlyList<NetworkObservation> observations,DateTimeOffset now,CancellationToken ct,
        Guid? owner=null,NetworkScanResult? outcome=null)
    {
        now=now.ToUniversalTime();
        var targets=NetworkTargets.Addresses(scope).ToHashSet(StringComparer.Ordinal);
        if(observations.Any(o=>!targets.Contains(o.IpAddress)||o.ResponseTimeMs is double ms&&(!double.IsFinite(ms)||ms<0)||o.Services.Count>64||o.Services.Any(s=>s.Port is <1 or >65535||s.Protocol is not("tcp" or "udp"))))
            throw new ArgumentException("network_observation_outside_scope");
        if(observations.Select(o=>o.IpAddress).Distinct().Count()!=observations.Count)throw new ArgumentException("duplicate_network_observation");
        outcome??=new(observations,true,"COMPLETE",[],["TCP"]);
        await using var connection=await dataSource.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var guard=new NpgsqlCommand("""
            SELECT r.id FROM network_scan_runs r JOIN network_scopes s ON s.code=r.scope_code
            WHERE r.id=$1 AND r.scope_code=$2 AND r.status IN ('QUEUED','RUNNING') AND s.enabled
                AND r.configuration_revision=s.configuration_revision AND r.configuration_revision=$3
                AND ($4::uuid IS NULL OR r.lease_owner=$4 AND r.status='RUNNING') FOR UPDATE OF r,s
            """,connection,tx))
        {
            guard.Parameters.AddWithValue(runId);guard.Parameters.AddWithValue(scope.Code);guard.Parameters.AddWithValue(scope.ConfigurationRevision);
            guard.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)owner??DBNull.Value});
            if(await guard.ExecuteScalarAsync(ct) is null)throw new InvalidOperationException("network_job_ownership_lost");
        }
        var devices=await ReadDeviceCandidatesAsync(connection,tx,scope,now,ct);
        await using(var identities=new NpgsqlCommand("INSERT INTO network_asset_identities SELECT id FROM network_assets WHERE scope_code=$1 ON CONFLICT DO NOTHING",connection,tx))
        {identities.Parameters.AddWithValue(scope.Code);await identities.ExecuteNonQueryAsync(ct);}
        var previous=new Dictionary<string,PreviousNetworkAsset>(StringComparer.Ordinal);
        await using(var select=new NpgsqlCommand("""
            SELECT host(ip_address),id,COALESCE(last_identified_device_id,linked_device_id),mac_address,device_kind,display_name,classification_source,services::text,
                operating_system_hint,device_type_hint,services_checked_at_utc,services_check_status
            FROM network_assets WHERE scope_code=$1 AND retired_at_utc IS NULL FOR UPDATE
            """,connection,tx))
        {
            select.Parameters.AddWithValue(scope.Code);await using var reader=await select.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))previous[reader.GetString(0)]=new(reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:ReadUtc(reader,10),reader.GetString(11));
        }
        var nas=new Dictionary<string,(Guid Id,string Name)>(StringComparer.Ordinal);
        await using(var select=new NpgsqlCommand("SELECT DISTINCT ON(host) host,id,display_name FROM nas_appliances WHERE enabled AND agency_code=$1 ORDER BY host,is_primary DESC,id",connection,tx))
        {select.Parameters.AddWithValue(scope.AgencyCode);await using var reader=await select.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))nas[reader.GetString(0)]=(reader.GetGuid(1),reader.GetString(2));}
        var newIps=new List<string>();var identityChanges=new List<string>();var ambiguous=new List<string>();var saved=new List<NetworkObservation>();
        foreach(var item in observations)
        {
            previous.TryGetValue(item.IpAddress,out var old);
            var candidates=devices.GetValueOrDefault(item.IpAddress)??[];
            bool cacheOnly=(item.PresenceSources??[]).Contains("ARP_CACHE");
            if(item.MacAddress is not null&&!cacheOnly)candidates=candidates.Where(c=>c.Mac is not null&&MacKey(c.Mac)==MacKey(item.MacAddress)).ToArray();
            var unique=candidates.DistinctBy(c=>c.Id).ToArray();
            Guid? deviceId=unique.Length==1?unique[0].Id:null;string? deviceName=unique.Length==1?unique[0].Name:null;
            bool conflicted=unique.Length>1;if(conflicted)ambiguous.Add(item.IpAddress);
            bool agentMac=unique.Length==1&&unique[0].Mac is not null&&(item.MacAddress is null||cacheOnly)&&
                candidates.Where(c=>c.Id==unique[0].Id&&c.Mac is not null).Select(c=>MacKey(c.Mac!)).Distinct().Count()==1;
            string? observedMac=agentMac?unique[0].Mac:item.MacAddress;
            string? macSource=observedMac is null?null:agentMac?"RECENT_AGENT":cacheOnly?"ARP_CACHE":"NMAP";
            DateTimeOffset? macAt=observedMac is null?null:agentMac?unique[0].CapturedAtUtc:now;
            string? hostname=item.Hostname??deviceName;
            string? hostnameSource=item.Hostname is null?(deviceName is null?null:"RECENT_AGENT"):item.HostnameSource??"SERVICES";
            string? vendor=item.Vendor??NetworkMacVendors.Find(observedMac);
            var storage=nas.GetValueOrDefault(item.IpAddress);
            Guid? nasId=storage.Id==Guid.Empty?null:storage.Id;
            var classification=Classify(scope,item,deviceId,deviceName,nasId,storage.Name);
            string basis=conflicted?"AMBIGUOUS":deviceId.HasValue?"RECENT_AGENT":nasId.HasValue?"REGISTERED_NAS":item.IpAddress==scope.Gateway?"REGISTERED_GATEWAY":classification.Kind=="UNKNOWN"?"UNKNOWN":"INFERRED";
            bool changed=old is not null && ((old.DeviceId.HasValue&&deviceId.HasValue&&old.DeviceId!=deviceId)||
                ((!cacheOnly||agentMac)&&old.Mac is not null&&observedMac is not null&&MacKey(old.Mac)!=MacKey(observedMac)&&
                 !(old.DeviceId.HasValue&&old.DeviceId==deviceId)));
            if(changed)
            {
                await AddEventAsync(connection,tx,old!.Id,runId,"IDENTITY_CHANGED",new{ipAddress=item.IpAddress,previousMac=old.Mac,macAddress=observedMac,previousDeviceId=old.DeviceId,deviceId},now,ct);
                await using var retire=new NpgsqlCommand("INSERT INTO network_asset_archives(asset_id,archived_at_utc,snapshot) SELECT id,$2,to_jsonb(a) FROM network_assets a WHERE id=$1",connection,tx);
                retire.Parameters.AddWithValue(old.Id);retire.Parameters.AddWithValue(now);await retire.ExecuteNonQueryAsync(ct);
                identityChanges.Add(item.IpAddress);old=null;
            }
            Guid asset=old?.Id??Guid.NewGuid();
            await using(var identity=new NpgsqlCommand("INSERT INTO network_asset_identities VALUES($1) ON CONFLICT DO NOTHING",connection,tx))
            {identity.Parameters.AddWithValue(asset);await identity.ExecuteNonQueryAsync(ct);}
            if(old is null)newIps.Add(item.IpAddress);
            bool manual=old?.Source=="MANUAL";
            string displayBasis=conflicted?"AMBIGUOUS":manual&&!deviceId.HasValue?"MANUAL":basis;
            string kind=manual?old!.Kind:classification.Kind;
            string? name=manual?old!.Name:classification.Name;
            string services=old?.Services??"[]";
            DateTimeOffset? servicesAt=old?.ServicesAt;
            string servicesState=old?.ServicesStatus??"NOT_RUN";
            if(item.ServicesChecked)
            {
                services=JsonSerializer.Serialize(item.ServicesCheckStatus=="COMPLETE"?item.Services:
                    DeserializeServices(services).Concat(item.Services).GroupBy(s=>(s.Protocol,s.Port)).Select(g=>g.Last()).ToArray());
                servicesAt=now;servicesState=item.ServicesCheckStatus;
            }
            else if(outcome.IdentificationStatus=="PARTIAL")servicesState="PARTIAL";
            string? os=item.OperatingSystemHint??old?.Os,type=item.DeviceTypeHint??old?.Type;
            await using(var upsert=new NpgsqlCommand("""
                INSERT INTO network_assets(id,scope_code,ip_address,hostname,mac_address,vendor,device_kind,display_name,first_seen_at_utc,last_seen_at_utc,
                    consecutive_misses,last_scan_id,linked_device_id,linked_nas_id,classification_source,services,operating_system_hint,device_type_hint,
                    identification_confidence,enriched_at_utc,presence_sources,identification_basis,services_checked_at_utc,services_check_status,last_identified_device_id,
                    response_time_ms,hostname_source,mac_source,mac_checked_at_utc)
                VALUES($1,$2,$3::inet,$4,$5,$6,$7,$8,$9,$9,0,$10,$11,$12,$13,$14::jsonb,$15,$16,$17,$18,$19,$20,$18,$21,$11,$22,$23,$24,$25)
                ON CONFLICT(scope_code,ip_address) DO UPDATE SET id=EXCLUDED.id,hostname=EXCLUDED.hostname,
                    first_seen_at_utc=CASE WHEN network_assets.id<>EXCLUDED.id THEN EXCLUDED.first_seen_at_utc ELSE network_assets.first_seen_at_utc END,
                    classified_at_utc=CASE WHEN network_assets.id<>EXCLUDED.id THEN NULL ELSE network_assets.classified_at_utc END,
                    classified_by=CASE WHEN network_assets.id<>EXCLUDED.id THEN NULL ELSE network_assets.classified_by END,
                    last_identified_device_id=CASE WHEN network_assets.id<>EXCLUDED.id THEN EXCLUDED.linked_device_id ELSE COALESCE(EXCLUDED.linked_device_id,network_assets.last_identified_device_id,network_assets.linked_device_id) END,
                    mac_address=CASE WHEN network_assets.id<>EXCLUDED.id THEN EXCLUDED.mac_address ELSE COALESCE(EXCLUDED.mac_address,network_assets.mac_address) END,
                    vendor=CASE WHEN network_assets.id<>EXCLUDED.id THEN EXCLUDED.vendor ELSE COALESCE(EXCLUDED.vendor,network_assets.vendor) END,
                    mac_source=CASE WHEN EXCLUDED.mac_address IS NOT NULL OR network_assets.id<>EXCLUDED.id THEN EXCLUDED.mac_source ELSE network_assets.mac_source END,
                    mac_checked_at_utc=CASE WHEN EXCLUDED.mac_address IS NOT NULL OR network_assets.id<>EXCLUDED.id THEN EXCLUDED.mac_checked_at_utc ELSE network_assets.mac_checked_at_utc END,
                    response_time_ms=EXCLUDED.response_time_ms,hostname_source=EXCLUDED.hostname_source,
                    device_kind=EXCLUDED.device_kind,display_name=EXCLUDED.display_name,last_seen_at_utc=EXCLUDED.last_seen_at_utc,
                    consecutive_misses=0,last_scan_id=EXCLUDED.last_scan_id,linked_device_id=EXCLUDED.linked_device_id,linked_nas_id=EXCLUDED.linked_nas_id,
                    classification_source=EXCLUDED.classification_source,services=EXCLUDED.services,operating_system_hint=EXCLUDED.operating_system_hint,
                    device_type_hint=EXCLUDED.device_type_hint,identification_confidence=EXCLUDED.identification_confidence,
                    enriched_at_utc=EXCLUDED.enriched_at_utc,presence_sources=EXCLUDED.presence_sources,identification_basis=EXCLUDED.identification_basis,
                    services_checked_at_utc=EXCLUDED.services_checked_at_utc,services_check_status=EXCLUDED.services_check_status
                """,connection,tx))
            {
                object[] values=[asset,scope.Code,item.IpAddress,(object?)hostname??DBNull.Value,(object?)observedMac??DBNull.Value,(object?)vendor??DBNull.Value,
                    kind,(object?)name??DBNull.Value,now,runId,(object?)deviceId??DBNull.Value,(object?)nasId??DBNull.Value,manual?"MANUAL":"AUTOMATIC",services,
                    (object?)os??DBNull.Value,(object?)type??DBNull.Value,classification.Confidence,(object?)servicesAt??DBNull.Value,
                    (item.PresenceSources??[]).ToArray(),displayBasis,servicesState,(object?)item.ResponseTimeMs??DBNull.Value,
                    (object?)hostnameSource??DBNull.Value,(object?)macSource??DBNull.Value,(object?)macAt??DBNull.Value];
                for(int i=0;i<values.Length;i++)
                {
                    var parameter=new NpgsqlParameter{Value=values[i]};
                    if(i is 10 or 11)parameter.NpgsqlDbType=NpgsqlDbType.Uuid;
                    else if(i is 17 or 24)parameter.NpgsqlDbType=NpgsqlDbType.TimestampTz;
                    else if(i==21)parameter.NpgsqlDbType=NpgsqlDbType.Double;
                    else if(values[i] is DBNull)parameter.NpgsqlDbType=NpgsqlDbType.Text;
                    upsert.Parameters.Add(parameter);
                }
                await upsert.ExecuteNonQueryAsync(ct);
            }
            saved.Add(item with{MacAddress=observedMac,MacSource=macSource,Hostname=hostname,HostnameSource=hostnameSource,Vendor=vendor,
                AssetId=asset,DeviceKind=kind,DeviceName=deviceName??name,
                ComputerManufacturer=unique.Length==1?unique[0].Manufacturer:null,ComputerModel=unique.Length==1?unique[0].Model:null,MacCheckedAtUtc=macAt,IdentificationBasis=displayBasis});
            string eventType=old is null?"DISCOVERED":old.Services!=services&&item.ServicesChecked?"SERVICES_CHANGED":"OBSERVED";
            await AddEventAsync(connection,tx,asset,runId,eventType,new{ipAddress=item.IpAddress,macAddress=observedMac,macSource,hostname,hostnameSource,item.ResponseTimeMs,sources=item.PresenceSources??[],identificationBasis=displayBasis,servicesStatus=servicesState,ports=item.Services.Select(s=>new{s.Port,s.Protocol})},now,ct);
        }
        // Absence counters only apply to addresses actually covered by a complete discovery.
        if(outcome.DiscoveryComplete&&observations.Count>0)
        {
            await using var misses=new NpgsqlCommand("UPDATE network_assets SET consecutive_misses=consecutive_misses+1 WHERE scope_code=$1 AND retired_at_utc IS NULL AND last_scan_id IS DISTINCT FROM $2 AND host(ip_address)=ANY($3)",connection,tx);
            misses.Parameters.AddWithValue(scope.Code);misses.Parameters.AddWithValue(runId);misses.Parameters.AddWithValue(targets.ToArray());await misses.ExecuteNonQueryAsync(ct);
        }
        bool complete=outcome.DiscoveryComplete&&outcome.IdentificationStatus!="PARTIAL";
        await using(var run=new NpgsqlCommand("""
            UPDATE network_scan_runs SET status=$2,completed_at_utc=$3,hosts_observed=$4,discovery_complete=$5,
                identification_status=$6,warnings=$7::jsonb,methods=$8,error_code=NULL,error_detail=NULL,
                lease_owner=NULL,lease_expires_at_utc=NULL,
                progress=jsonb_set(jsonb_set(progress,'{Phase}','"FINISHED"'::jsonb),'{Observations}',$9::jsonb),progress_at_utc=$3 WHERE id=$1
            """,connection,tx))
        {
            run.Parameters.AddWithValue(runId);run.Parameters.AddWithValue(complete?"SUCCEEDED":"PARTIAL");run.Parameters.AddWithValue(now);run.Parameters.AddWithValue(observations.Count);
            run.Parameters.AddWithValue(outcome.DiscoveryComplete);run.Parameters.AddWithValue(outcome.IdentificationStatus);run.Parameters.AddWithValue(JsonSerializer.Serialize(outcome.Warnings));run.Parameters.AddWithValue(outcome.Methods.ToArray());
            run.Parameters.AddWithValue(JsonSerializer.Serialize(saved));await run.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection,tx,"system:network-worker",complete?"admin.network.scan.completed":"admin.network.scan.partial",scope.Code,new{runId,status=complete?"SUCCEEDED":"PARTIAL",observations=observations.Count,outcome.DiscoveryComplete,outcome.IdentificationStatus},now,ct);
        string day=NotificationDay(now);
        if(newIps.Count>0)await AlertAsync(connection,tx,$"network-new:{scope.Code}:{day}","INFO","Dispositivos detectados en la red",$"{scope.DisplayName}: {newIps.Count} dirección(es) nuevas en este sondeo. Revise identificación y gestión: {string.Join(", ",newIps.Take(8))}.",scope.Code,now,ct);
        if(identityChanges.Count+ambiguous.Count>0)await AlertAsync(connection,tx,$"network-identity:{scope.Code}:{day}","WARNING","Revisar identidad de direcciones IP",$"{scope.DisplayName}: se detectaron cambios o coincidencias ambiguas. No demuestran un ataque. Revise el historial de red.",scope.Code,now,ct);
        if(!complete||observations.Count==0)await AlertAsync(connection,tx,$"network-coverage:{scope.Code}:{day}","WARNING","Revisar cobertura del sondeo",$"{scope.DisplayName}: {(observations.Count==0?"no se recibieron respuestas":"el sondeo fue parcial")}. No significa que las direcciones estén libres ni que la agencia esté fuera de servicio.",scope.Code,now,ct);
        await tx.CommitAsync(ct);
    }
    private async Task<Dictionary<string,NetworkDeviceCandidate[]>> ReadDeviceCandidatesAsync(NpgsqlConnection connection,NpgsqlTransaction tx,NetworkScopeDefinition scope,DateTimeOffset now,CancellationToken ct)
    {
        var found=new Dictionary<string,List<NetworkDeviceCandidate>>(StringComparer.Ordinal);
        await using var command=new NpgsqlCommand("""
            SELECT d.id,d.machine_name,inventory.payload::text,inventory.captured_at_utc FROM devices d
            JOIN LATERAL(SELECT payload,captured_at_utc FROM inventory_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC LIMIT 1) inventory ON TRUE
            WHERE d.enabled AND d.agency_code=$1 AND d.last_seen_at_utc>=$2 AND inventory.captured_at_utc>=$3 AND inventory.captured_at_utc<=$4
            """,connection,tx);
        command.Parameters.AddWithValue(scope.AgencyCode);command.Parameters.AddWithValue(now.AddMinutes(-15));
        command.Parameters.AddWithValue(now.AddMinutes(-(options?.Value.NetworkDiscoveryInventoryMaxAgeMinutes??30)));command.Parameters.AddWithValue(now.AddMinutes(5));
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            using var payload=JsonDocument.Parse(reader.GetString(2));
            string? manufacturer=null,model=null;
            if(payload.RootElement.TryGetProperty("computer",out var computer)&&computer.ValueKind==JsonValueKind.Object)
            {
                if(computer.TryGetProperty("manufacturer",out var make)&&make.ValueKind==JsonValueKind.String)manufacturer=make.GetString();
                if(computer.TryGetProperty("model",out var type)&&type.ValueKind==JsonValueKind.String)model=type.GetString();
            }
            if(!payload.RootElement.TryGetProperty("networkInterfaces",out var nics)||nics.ValueKind!=JsonValueKind.Array)continue;
            foreach(var nic in nics.EnumerateArray())
            {
                string? mac=nic.TryGetProperty("macAddress",out var m)&&m.ValueKind==JsonValueKind.String?m.GetString():null;
                if(!nic.TryGetProperty("ipv4Addresses",out var addresses)||addresses.ValueKind!=JsonValueKind.Array)continue;
                foreach(var address in addresses.EnumerateArray())if(address.ValueKind==JsonValueKind.String&&address.GetString() is string ip)
                {if(!found.TryGetValue(ip,out var candidates))found[ip]=candidates=[];candidates.Add(new(reader.GetGuid(0),reader.GetString(1),mac,ReadUtc(reader,3),manufacturer,model));}
            }
        }
        return found.ToDictionary(p=>p.Key,p=>p.Value.ToArray(),StringComparer.Ordinal);
    }
    private static string MacKey(string mac)=>new(mac.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());
    private string NotificationDay(DateTimeOffset now)
    {
        try{return TimeZoneInfo.ConvertTime(now,TimeZoneInfo.FindSystemTimeZoneById(options?.Value.OperationalTimeZoneId??"America/Guatemala")).ToString("yyyy-MM-dd");}
        catch(TimeZoneNotFoundException){return now.ToString("yyyy-MM-dd");}
    }
    private static async Task AddEventAsync(NpgsqlConnection connection,NpgsqlTransaction tx,Guid asset,Guid run,string type,object evidence,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("INSERT INTO network_asset_events(id,asset_id,run_id,event_type,occurred_at_utc,evidence) VALUES($1,$2,$3,$4,$5,$6::jsonb)",connection,tx);
        command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(asset);command.Parameters.AddWithValue(run);command.Parameters.AddWithValue(type);command.Parameters.AddWithValue(now);command.Parameters.AddWithValue(JsonSerializer.Serialize(evidence));await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task AlertAsync(NpgsqlConnection connection,NpgsqlTransaction tx,string fingerprint,string severity,string title,string message,string scope,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("""
            INSERT INTO admin_notifications(id,fingerprint,category,severity,title,message,target_view,target_entity_id,detected_at_utc,last_seen_at_utc)
            VALUES($1,$2,'NETWORK',$3,$4,$5,'network',$6,$7,$7) ON CONFLICT(fingerprint) DO UPDATE SET message=EXCLUDED.message,last_seen_at_utc=EXCLUDED.last_seen_at_utc
            """,connection,tx);
        command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(fingerprint);command.Parameters.AddWithValue(severity);command.Parameters.AddWithValue(title);command.Parameters.AddWithValue(message);command.Parameters.AddWithValue(scope);command.Parameters.AddWithValue(now);await command.ExecuteNonQueryAsync(ct);
    }
}
