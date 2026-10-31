using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miguel.Contracts;

public sealed record AgentPolicyValues(int HeartbeatSeconds=300,int InventoryHours=24,int PostureHours=6,int EventSeconds=300,int EventBatchSize=250);
public sealed record AgentPolicyEnvelope(int Protocol,long Revision,Guid DeviceId,string Scope,string Target,
    AgentPolicyValues Values,string Sha256);
public sealed record AgentPolicyReceipt(long Revision,string Sha256,string Status,string? ErrorCode=null);
public static class AgentPolicyContract
{
    public const int Protocol=1;
    public static bool Valid(AgentPolicyValues? v)=>v is not null && v.HeartbeatSeconds is >=60 and <=3600 &&
        v.InventoryHours is >=1 and <=72 && v.PostureHours is >=1 and <=24 && v.EventSeconds is >=60 and <=900 && v.EventBatchSize is >=50 and <=500;
    public static string Hash(AgentPolicyValues values)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values,new JsonSerializerOptions(JsonSerializerDefaults.Web)))));
    public static bool Valid(AgentPolicyEnvelope? e,Guid device)=>e is not null && e.Protocol==Protocol && e.DeviceId==device && e.Revision>=0 && Valid(e.Values) && e.Sha256==Hash(e.Values);
}
