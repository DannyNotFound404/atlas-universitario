using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Miguel.Agent.Transport;
using Miguel.Contracts;

namespace Miguel.Agent;

public sealed class RemotePolicyClient(AgentPaths paths,AgentApiClient api,ILogger<RemotePolicyClient> logger)
{
    private sealed record CachedPolicy(AgentPolicyEnvelope Policy,string Authentication);
    private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    private bool loaded;
    public AgentPolicyValues? Current { get; private set; }
    private AgentPolicyEnvelope? active;
    private string FilePath=>Path.Combine(Path.GetDirectoryName(paths.IdentityFile)!,"agent-policy.json");
    private string Sign(AgentIdentity identity,AgentPolicyEnvelope policy)=>Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(identity.DeviceKey),Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policy,json))));
    public async Task<bool> RefreshAsync(AgentIdentity identity,CancellationToken ct)
    {
        var previous=Current;
        if(!loaded)
        {
            loaded=true;
            try{if(File.Exists(FilePath)){var cache=JsonSerializer.Deserialize<CachedPolicy>(await File.ReadAllTextAsync(FilePath,ct),json);
                if(cache is not null && cache.Authentication?.Length==64 && AgentPolicyContract.Valid(cache.Policy,identity.DeviceId) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cache.Authentication),Encoding.ASCII.GetBytes(Sign(identity,cache.Policy)))){active=cache.Policy;Current=active.Revision==0?null:active.Values;}}}
            catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException){logger.LogWarning("No se pudo recuperar la política local; se conservan los valores de instalación.");}
        }
        // Policy transport requires the enrolled HTTPS server and its normal certificate validation.
        if(identity.ServerUrl.Scheme!="https")return Current!=previous;
        try
        {
            var policy=await api.GetPolicyAsync(identity,ct);if(policy is null)return Current!=previous;
            if(!AgentPolicyContract.Valid(policy,identity.DeviceId))
            {await api.SendPolicyReceiptAsync(identity,new(policy.Revision,policy.Sha256,"REJECTED","INVALID_POLICY"),ct);return Current!=previous;}
            if(active?.Revision!=policy.Revision || active.Sha256!=policy.Sha256)
            {
                // A lower global revision can be selected after moving agencies; precedence is resolved by the authenticated server.
                string temporary=FilePath+".tmp";
                try
                {
                    paths.EnsureDirectories();
                    await using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){await JsonSerializer.SerializeAsync(file,new CachedPolicy(policy,Sign(identity,policy)),json,ct);await file.FlushAsync(ct);file.Flush(true);}
                    File.Move(temporary,FilePath,true);
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException)
                {await api.SendPolicyReceiptAsync(identity,new(policy.Revision,policy.Sha256,"REJECTED","CACHE_WRITE_FAILED"),ct);return Current!=previous;}
                active=policy;Current=policy.Revision==0?null:policy.Values;
            }
            await api.SendPolicyReceiptAsync(identity,new(policy.Revision,policy.Sha256,"APPLIED"),ct);
        }
        catch(Exception e)when(e is HttpRequestException or JsonException || e is OperationCanceledException&&!ct.IsCancellationRequested)
        {logger.LogWarning("No se pudo comprobar la política remota; se mantiene la última configuración válida.");}
        return Current!=previous;
    }
}
