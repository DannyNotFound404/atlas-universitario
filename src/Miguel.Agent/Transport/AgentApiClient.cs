using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Miguel.Contracts;

namespace Miguel.Agent.Transport;

public sealed class AgentApiClient(HttpClient httpClient)
{
    public async Task<AgentPolicyEnvelope?> GetPolicyAsync(AgentIdentity identity,CancellationToken cancellationToken)
    {
        using var request=CreateRequest(identity,HttpMethod.Get,"/api/v1/agent/policy",null);
        using var response=await httpClient.SendAsync(request,cancellationToken);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>16384)throw new HttpRequestException("Policy response exceeds the size limit.");
        await response.Content.LoadIntoBufferAsync(16384,cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgentPolicyEnvelope>(cancellationToken);
    }
    public async Task SendPolicyReceiptAsync(AgentIdentity identity,AgentPolicyReceipt receipt,CancellationToken cancellationToken)
    {
        using var request=CreateRequest(identity,HttpMethod.Post,"/api/v1/agent/policy/receipt",JsonContent.Create(receipt));
        using var response=await httpClient.SendAsync(request,cancellationToken);response.EnsureSuccessStatusCode();
    }
    public async Task EnviarHeartbeatAsync(
        AgentIdentity identity,
        AgentHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            identity,
            HttpMethod.Post,
            ApiContract.HeartbeatRoute,
            JsonContent.Create(heartbeat));
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendOutboxEnvelopeAsync(
        AgentIdentity identity,
        OutboxEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string route = envelope.EventType switch
        {
            OutboxEventTypes.Inventory => ApiContract.InventoryRoute,
            OutboxEventTypes.BackupRun => ApiContract.BackupRunsRoute,
            OutboxEventTypes.SecurityPosture => ApiContract.SecurityPostureRoute,
            OutboxEventTypes.SecurityEvents => ApiContract.SecurityEventsRoute,
            _ => throw new InvalidOperationException(
                $"Tipo de evento no reconocido: {envelope.EventType}")
        };

        using HttpRequestMessage request = CreateRequest(
            identity,
            HttpMethod.Post,
            route,
            new StringContent(
                envelope.Payload.GetRawText(),
                System.Text.Encoding.UTF8,
                "application/json"));
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(
        AgentIdentity identity,
        HttpMethod method,
        string relativeRoute,
        HttpContent? content)
    {
        HttpRequestMessage request = new(
            method,
            new Uri(identity.ServerUrl, relativeRoute.TrimStart('/')))
        {
            Content = content
        };
        request.Headers.Add(ApiContract.DeviceIdHeader, identity.DeviceId.ToString("D"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            ApiContract.AuthorizationScheme,
            identity.DeviceKey);
        request.Headers.UserAgent.ParseAdd($"Miguel.Agent/{AgentVersion.Current}");
        return request;
    }
}
