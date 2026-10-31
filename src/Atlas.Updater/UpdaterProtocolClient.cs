using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed class UpdaterProtocolClient : IDisposable
{
    private readonly HttpClient http;
    internal UpdaterProtocolClient(AgentIdentity identity, HttpMessageHandler? handler = null)
    {
        if (identity.ServerUrl.Scheme != "https" && handler is null) throw new InvalidOperationException("https_required");
        http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(identity.ServerUrl, "/api/v1/agent/updater/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add(ApiContract.DeviceIdHeader, identity.DeviceId.ToString("D"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiContract.AuthorizationScheme, identity.DeviceKey);
        http.DefaultRequestHeaders.Add("X-Atlas-Updater-Protocol", "2");
    }
    public Task<UpdaterCheckResponse> CheckAsync(UpdaterCheckRequest request, CancellationToken ct) => PostAsync<UpdaterCheckResponse>("check", request, ct);
    public Task<UpdaterLeaseResponse> LeaseAsync(UpdaterLeaseRequest request, CancellationToken ct) => PostAsync<UpdaterLeaseResponse>("lease", request, ct);
    public async Task<bool> InstallAsync(UpdaterInstallRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("install", request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode(); return true;
    }
    public async Task ReportAsync(UpdaterEvent item, CancellationToken ct)
    { using var response = await http.PostAsJsonAsync("events", item, ct); response.EnsureSuccessStatusCode(); }
    public async Task<UpdaterConfirmation> ConfirmAsync(Guid attempt, CancellationToken ct) =>
        await http.GetFromJsonAsync<UpdaterConfirmation>($"attempts/{attempt:D}/confirmation", ct) ?? throw new InvalidDataException("empty_confirmation");
    private async Task<T> PostAsync<T>(string path, object request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("server_upgrade_required");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct) ?? throw new InvalidDataException("empty_update_response");
    }
    public void Dispose() => http.Dispose();
}
