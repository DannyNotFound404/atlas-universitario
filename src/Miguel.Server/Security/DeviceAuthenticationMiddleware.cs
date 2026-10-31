using System.Net.Http.Headers;
using Miguel.Contracts;
using Miguel.Server.Storage;

namespace Miguel.Server.Security;

public sealed class DeviceAuthenticationMiddleware(
    RequestDelegate next,
    ILogger<DeviceAuthenticationMiddleware> logger)
{
    public const string DeviceIdItemKey = "Miguel.DeviceId";

    public async Task InvokeAsync(HttpContext context, MiguelRepository repository)
    {
        if (!TryReadCredentials(context.Request, out Guid deviceId, out string deviceKey) ||
            !await repository.ValidateDeviceCredentialAsync(deviceId, deviceKey, context.RequestAborted))
        {
            logger.LogWarning(
                "Autenticación rechazada para el endpoint del agente desde {RemoteAddress}.",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new { error = "device_authentication_failed" },
                cancellationToken: context.RequestAborted);
            return;
        }

        context.Items[DeviceIdItemKey] = deviceId;
        await next(context);
    }

    private static bool TryReadCredentials(
        HttpRequest request,
        out Guid deviceId,
        out string deviceKey)
    {
        deviceId = Guid.Empty;
        deviceKey = string.Empty;

        if (!Guid.TryParse(request.Headers[ApiContract.DeviceIdHeader].ToString(), out deviceId) ||
            !AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out AuthenticationHeaderValue? authorization) ||
            !string.Equals(
                authorization.Scheme,
                ApiContract.AuthorizationScheme,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter) ||
            authorization.Parameter.Length is < 32 or > 256)
        {
            return false;
        }

        deviceKey = authorization.Parameter;
        return true;
    }
}
