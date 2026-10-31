using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Miguel.Contracts;
using Miguel.Server.Admin;

namespace Miguel.Server.Nas;

public sealed class NasProvisionerClient(
    IOptions<ServerOptions> optionsAccessor,
    ILogger<NasProvisionerClient> logger)
{
    private const int MaximumMessageBytes = 32 * 1024;

    public async Task<NasProvisioningResponse> ProvisionAsync(
        Admin.AdminNasDefinition appliance,
        AdminNasMountRequest request,
        CancellationToken cancellationToken)
    {
        string socketPath = optionsAccessor.Value.NasProvisionerSocketPath?.Trim() ?? "";
        if (socketPath.Length == 0)
        {
            socketPath = "/run/atlas-nas-provisioner/provisioner.sock";
        }
        if (!Path.IsPathFullyQualified(socketPath) || socketPath.Length > 255)
        {
            throw new NasProvisionerUnavailableException(
                "La ruta del aprovisionador NAS no es valida.");
        }

        NasProvisioningRequest payload = new(
            "PROVISION",
            appliance.Code,
            appliance.Host,
            appliance.SmbPort,
            appliance.ShareName,
            request.Protocol,
            request.Username,
            request.Password,
            request.Domain,
            request.SmbSecurityProfile,
            request.NfsExport);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (body.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("La solicitud de montaje es demasiado grande.");
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(80));
        using Socket socket = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(socketPath),
                timeout.Token);
            byte[] lengthBuffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, body.Length);
            await SendExactlyAsync(socket, lengthBuffer, timeout.Token);
            await SendExactlyAsync(socket, body, timeout.Token);

            await ReceiveExactlyAsync(socket, lengthBuffer, timeout.Token);
            int responseLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            if (responseLength is < 2 or > MaximumMessageBytes)
            {
                throw new InvalidDataException(
                    "El aprovisionador devolvio una respuesta invalida.");
            }
            byte[] responseBody = new byte[responseLength];
            await ReceiveExactlyAsync(socket, responseBody, timeout.Token);
            return JsonSerializer.Deserialize<NasProvisioningResponse>(responseBody) ??
                throw new InvalidDataException(
                    "El aprovisionador devolvio JSON invalido.");
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or
            OperationCanceledException)
        {
            logger.LogWarning(
                "El aprovisionador local NAS no esta disponible: {Reason}",
                exception.GetType().Name);
            throw new NasProvisionerUnavailableException(
                "El aprovisionador local NAS no esta disponible.",
                exception);
        }
    }

    private static async Task SendExactlyAsync(
        Socket socket,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        int sent = 0;
        while (sent < source.Length)
        {
            int count = await socket.SendAsync(
                source[sent..],
                SocketFlags.None,
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Conexion local interrumpida.");
            }
            sent += count;
        }
    }

    private static async Task ReceiveExactlyAsync(
        Socket socket,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int received = 0;
        while (received < destination.Length)
        {
            int count = await socket.ReceiveAsync(
                destination[received..],
                SocketFlags.None,
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Conexion local interrumpida.");
            }
            received += count;
        }
    }
}

public sealed class NasProvisionerUnavailableException : Exception
{
    public NasProvisionerUnavailableException(string message)
        : base(message)
    {
    }

    public NasProvisionerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
