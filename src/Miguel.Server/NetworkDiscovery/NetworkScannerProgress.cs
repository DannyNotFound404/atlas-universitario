using System.Text.Json;
using Npgsql;

namespace Miguel.Server.NetworkDiscovery;

public sealed partial class NetworkDiscoveryRepository
{
    public async Task<byte[]> ExportScannerCsvAsync(string? scope,string? search,string? presence,string? sort,bool descending,
        Guid? runId,DateTimeOffset now,CancellationToken ct)
    {
        var live=runId.HasValue?await ReadProgressAsync(runId.Value,ct):null;
        if(scope is not null&&live?.Run.ScopeCode!=scope.Trim().ToUpperInvariant())live=null;
        bool snapshot=live?.Progress is not null;
        var page=await ReadAssetPageAsync(scope,snapshot?null:search,1,10000,ct,snapshot?"ALL":presence,sort,descending,runId);
        var assets=page.Items.ToDictionary(a=>a.IpAddress,StringComparer.Ordinal);
        var observed=snapshot?(live!.Progress!.Observations??[]).Where(o=>string.IsNullOrWhiteSpace(search)||
            new[]{o.IpAddress,o.DeviceName,o.Hostname,o.MacAddress,o.Vendor,o.DeviceKind,o.DeviceTypeHint}.Any(v=>v?.Contains(search,StringComparison.OrdinalIgnoreCase)==true)).ToList():null;
        if(observed is not null)
        {
            if(presence?.ToUpperInvariant()=="MISSING")observed.Clear();
            if(presence?.ToUpperInvariant()=="UNKNOWN")observed.RemoveAll(o=>
                (o.DeviceKind??assets.GetValueOrDefault(o.IpAddress)?.DeviceKind??"UNKNOWN")!="UNKNOWN"&&assets.GetValueOrDefault(o.IpAddress)?.IdentificationBasis!="AMBIGUOUS");
            observed.Sort((a,b)=>
            {
                int ip=ScannerAddressComparer.Instance.Compare(System.Net.IPAddress.Parse(a.IpAddress).GetAddressBytes(),System.Net.IPAddress.Parse(b.IpAddress).GetAddressBytes());
                if(sort=="latency"&&(a.ResponseTimeMs is null||b.ResponseTimeMs is null))return a.ResponseTimeMs is null&&b.ResponseTimeMs is null?ip:a.ResponseTimeMs is null?1:-1;
                int order=sort switch
                {"name"=>StringComparer.OrdinalIgnoreCase.Compare(a.DeviceName??a.Hostname??"",b.DeviceName??b.Hostname??""),
                 "vendor"=>StringComparer.OrdinalIgnoreCase.Compare(a.Vendor??"",b.Vendor??""),
                 "latency"=>Nullable.Compare(a.ResponseTimeMs,b.ResponseTimeMs),"state" or "seen"=>0,_=>ip};
                return order==0?ip:descending?-order:order;
            });
        }
        var csv=new System.Text.StringBuilder("\uFEFF");
        void Row(params string[] cells)=>csv.AppendLine(string.Join(',',cells.Select(Miguel.Server.Reports.ReportFormats.CsvCell)));
        Row("ATLAS · Escáner de red","Generado UTC",now.ToUniversalTime().ToString("O"));
        Row("Red",scope??"Todas","Búsqueda",search??"","Filtro",presence??"ALL","Registros",(observed?.Count??page.Items.Count).ToString());
        if(page.Total>page.Items.Count)Row("Límite","Exportación limitada a 10000 registros. Acota la red o los filtros.");
        Row("IP","Nombre","Red","MAC","Fuente MAC","Fecha MAC UTC","Fabricante equipo","Fabricante adaptador","Respuesta ms","Tipo","Fuentes presencia","Última observación UTC","Sondeos sin respuesta","Servicios","Fecha servicios UTC","Estado servicios");
        if(live is not null&&observed is not null)
        {
            foreach(var o in observed)
            {
                assets.TryGetValue(o.IpAddress,out var a);
                Row(o.IpAddress,o.DeviceName??o.Hostname??"",live.Run.ScopeCode,o.MacAddress??"",o.MacSource??"",o.MacCheckedAtUtc?.ToString("O")??"",
                    o.ComputerManufacturer??"",o.Vendor??"",o.ResponseTimeMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",o.DeviceKind??o.DeviceTypeHint??"UNKNOWN",
                    string.Join(" + ",o.PresenceSources??[]),(live.UpdatedAtUtc??live.Run.StartedAtUtc)?.ToString("O")??"","0",
                    string.Join("; ",o.Services.Select(s=>$"{s.Port}/{s.Protocol} {s.Name}")),a?.ServicesCheckedAtUtc?.ToString("O")??"",o.ServicesCheckStatus);
            }
            Row("Estado del escaneo",live.Run.Status,"Resultado","Respuestas conservadas en el sondeo; la identificación puede ser parcial.");
            return System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        }
        foreach(var a in page.Items)Row(a.IpAddress,a.LinkedDeviceName??a.LinkedNasName??a.Hostname??a.DisplayName??"",a.AgencyName,
            a.MacAddress??"",a.MacSource??"Anterior",a.MacCheckedAtUtc?.ToString("O")??"",a.ComputerManufacturer??"",a.Vendor??"",
            a.ResponseTimeMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",a.DeviceKind,string.Join(" + ",a.PresenceSources??[]),
            a.LastSeenAtUtc.ToString("O"),a.ConsecutiveMisses.ToString(),string.Join("; ",a.Services.Select(s=>$"{s.Port}/{s.Protocol} {s.Name}")),
            a.ServicesCheckedAtUtc?.ToString("O")??"",a.ServicesCheckStatus);
        return System.Text.Encoding.UTF8.GetBytes(csv.ToString());
    }
    private sealed class ScannerAddressComparer : IComparer<byte[]>
    {
        public static readonly ScannerAddressComparer Instance=new();
        public int Compare(byte[]? a,byte[]? b)=>a is null?-1:b is null?1:a.AsSpan().SequenceCompareTo(b);
    }
    public async Task<bool> RecordProgressAsync(NetworkScanClaim claim,NetworkScanProgress progress,DateTimeOffset now,CancellationToken ct)
    {
        var targets=NetworkTargets.Addresses(claim.Scope).ToHashSet(StringComparer.Ordinal);
        if(progress.IcmpCompleted<0||progress.IcmpCompleted>targets.Count||progress.IcmpTotal>targets.Count||
            progress.Observations.Count>targets.Count||progress.Observations.Any(o=>!targets.Contains(o.IpAddress)))
            throw new ArgumentException("invalid_network_progress");
        await using var command=dataSource.CreateCommand("""
            UPDATE network_scan_runs r SET progress=$3::jsonb,progress_at_utc=$4,hosts_observed=$5
            FROM network_scopes s WHERE r.id=$1 AND r.lease_owner=$2 AND r.status='RUNNING'
                AND s.code=r.scope_code AND s.enabled AND r.configuration_revision=s.configuration_revision
                AND r.configuration_revision=$6
            """);
        command.Parameters.AddWithValue(claim.RunId);command.Parameters.AddWithValue(claim.Owner);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(progress));command.Parameters.AddWithValue(now.ToUniversalTime());
        command.Parameters.AddWithValue(progress.Observations.Count);command.Parameters.AddWithValue(claim.Scope.ConfigurationRevision);
        return await command.ExecuteNonQueryAsync(ct)==1;
    }
    public async Task<NetworkScanLiveView?> ReadProgressAsync(Guid id,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            SELECT id,scope_code,status,requested_by,requested_at_utc,started_at_utc,completed_at_utc,addresses_targeted,
                hosts_observed,discovery_complete,identification_status,warnings::text,methods,error_code,progress::text,progress_at_utc
            FROM network_scan_runs WHERE id=$1
            """);command.Parameters.AddWithValue(id);
        await using var r=await command.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        var run=new NetworkScanRunView(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),ReadUtc(r,4),r.IsDBNull(5)?null:ReadUtc(r,5),
            r.IsDBNull(6)?null:ReadUtc(r,6),r.GetInt32(7),r.IsDBNull(8)?null:r.GetInt32(8),r.GetBoolean(9),r.GetString(10),
            JsonSerializer.Deserialize<NetworkScanWarning[]>(r.GetString(11))??[],r.GetFieldValue<string[]>(12),r.IsDBNull(13)?null:r.GetString(13));
        var progress=r.GetString(14)=="{}"?null:JsonSerializer.Deserialize<NetworkScanProgress>(r.GetString(14));
        return new(run,progress,r.IsDBNull(15)?null:ReadUtc(r,15));
    }
}
