using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Miguel.Server.Assets;

public sealed record AssetCustodyProposal(Guid DeviceId, string MachineName, string AgencyCode, string AgencyName,
    string AssignedTo, string Custodian, string Location, string? ObservedAccount, DateTimeOffset? ObservedAtUtc,
    string? ResolutionSource, string? SessionType, string Version, bool Ready, IReadOnlyList<string> Warnings);
public sealed record AssetCustodyProposalPage(IReadOnlyList<AssetCustodyProposal> Items, Guid? NextAfter);
public sealed record AssetCustodyBulkItem(Guid DeviceId, string SuggestionVersion, string AssignedTo, string Custodian, string Location);
public sealed record AssetCustodyBulkRequest(IReadOnlyList<AssetCustodyBulkItem>? Items, bool Reviewed, string Reason);
public sealed record AssetCustodyBulkResult(Guid DeviceId, bool Saved, string? Error);

public sealed partial class AssetCustodyService
{
    // Initial inventory confirmation (V4 pp.6/16), not evidence of an historical physical delivery.
    // Freshness and generic-account rules are operational suggestions, not policy mandates.
    private const string ProposalSelect = """
        SELECT d.id,d.machine_name,d.agency_code,COALESCE(a.display_name,d.agency_code),a.location,
            d.interactive_user_account,d.interactive_user_display_name,d.interactive_user_checked_at_utc,
            d.interactive_user_resolution_source,d.interactive_user_session_type,d.backup_provider
        FROM devices d LEFT JOIN agencies a ON a.code=d.agency_code
        """;

    private AssetCustodyProposal Proposal(NpgsqlDataReader r)
    {
        var id=r.GetGuid(0);string machine=r.GetString(1),agency=r.GetString(2),agencyName=r.GetString(3);
        string location=Str(r,4)?.Trim() is {Length: > 0} place?place:agencyName;
        string? account=Str(r,5),display=Str(r,6),source=Str(r,8),session=Str(r,9);
        var observed=Date(r,7);string provider=r.GetString(10);
        string assignee=string.IsNullOrWhiteSpace(display)?account?.Trim()??"":display.Trim();
        List<string> warnings=[];
        if(assignee.Length<2)warnings.Add("missing_user");
        if(observed is null || observed<clock.GetUtcNow().AddDays(-7) || observed>clock.GetUtcNow().AddMinutes(5))warnings.Add("stale_user");
        string user=(account??assignee).Split('\\').Last().Split('@')[0].Trim().ToLowerInvariant();
        string[] generic=["admin","administrator","administrador","system","local service","network service","soporte","soporte tecnico","guest","invitado","defaultaccount","wdagutilityaccount"];
        if(generic.Contains(user) || user.EndsWith('$'))warnings.Add("generic_user");
        if(session!="CONSOLE")warnings.Add("unconfirmed_session");
        if(provider=="SHARED_FOLDER")warnings.Add("shared_workstation");
        if(location.Length<2)warnings.Add("missing_location");
        // Repeated heartbeats with the same identity do not invalidate an operator's open review.
        // Identity, location, provider and freshness changes do invalidate it.
        string version=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,machine,agency,agencyName,location,account,assignee,source,session,provider,warnings},Json))));
        return new(id,machine,agency,agencyName,assignee,assignee,location,account,observed,source,session,version,warnings.Count==0,warnings);
    }

    private async Task<AssetCustodyProposal?> ReadProposal(NpgsqlConnection c,NpgsqlTransaction tx,Guid id,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand(ProposalSelect+" WHERE d.id=$1",c,tx);
        cmd.Parameters.AddWithValue(id);await using var reader=await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?Proposal(reader):null;
    }

    public async Task<AssetCustodyProposalPage> ReadProposals(Guid? after,string? agency,CancellationToken ct,int pageSize=200)
    {
        if(pageSize is <1 or >200 || agency?.Length>64)throw new ArgumentException("invalid_proposal_filter");
        await using var cmd=db.CreateCommand(ProposalSelect+"\n"+"""
            WHERE d.enabled AND ($1::uuid IS NULL OR d.id>$1) AND ($2::text IS NULL OR d.agency_code=$2)
            AND NOT EXISTS(SELECT 1 FROM asset_custody c WHERE c.device_id=d.id OR c.current_device_id=d.id)
            AND NOT EXISTS(SELECT 1 FROM asset_custody_identities i WHERE i.device_id=d.id)
            ORDER BY d.id LIMIT $3
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)after??DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,string.IsNullOrWhiteSpace(agency)?DBNull.Value:agency.Trim());
        cmd.Parameters.AddWithValue(pageSize+1);
        List<AssetCustodyProposal> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(Proposal(reader));
        bool more=rows.Count>pageSize;if(more)rows.RemoveAt(rows.Count-1);
        return new(rows,more?rows[^1].DeviceId:null);
    }

    public async Task<IReadOnlyList<AssetCustodyBulkResult>> RegisterMany(AssetCustodyBulkRequest request,string actor,string role,CancellationToken ct)
    {
        if(!request.Reviewed)throw new ArgumentException("custody_review_required");
        if(request.Items is null || request.Items.Count is <1 or >100 || request.Items.Any(i=>i is null || i.DeviceId==Guid.Empty) || request.Items.Select(i=>i.DeviceId).Distinct().Count()!=request.Items.Count)
            throw new ArgumentException("invalid_custody_batch");
        string reason=AssetCustodyInput.Text(request.Reason,1000,"reason");
        List<AssetCustodyBulkResult> results=[];
        foreach(var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var view=await Read(item.DeviceId,ct)??throw new AssetCustodyConflict("device_not_found");
                if(view.Custody.State!="UNREGISTERED")throw new AssetCustodyConflict("custody_already_registered");
                if(item.SuggestionVersion?.Length!=64)throw new ArgumentException("invalid_suggestion_version");
                await Change(item.DeviceId,new("REGISTER",view.Version,reason,AssignedTo:item.AssignedTo,Custodian:item.Custodian,
                    Location:item.Location,SuggestionVersion:item.SuggestionVersion),actor,role,ct);
                results.Add(new(item.DeviceId,true,null));
            }
            catch(AssetCustodyConflict ex){results.Add(new(item.DeviceId,false,ex.Message));}
            catch(ArgumentException ex){results.Add(new(item.DeviceId,false,ex.Message));}
            catch(PostgresException ex)when(ex.SqlState is "40001" or "40P01" or "23505"){results.Add(new(item.DeviceId,false,"custody_changed"));}
        }
        return results;
    }
}
