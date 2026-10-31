using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Atlas.Nas.Provisioner;
using Miguel.Contracts;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Atlas.Nas.Provisioner solo puede ejecutarse en Linux.");
    return 3;
}
if (Environment.UserName != "root")
{
    Console.Error.WriteLine("Atlas.Nas.Provisioner requiere UID root.");
    return 3;
}

string socketPath = Environment.GetEnvironmentVariable(
    "ATLAS_NAS_PROVISIONER_SOCKET") ??
    "/run/atlas-nas-provisioner/provisioner.sock";
string socketDirectory = Path.GetDirectoryName(socketPath) ??
    throw new InvalidDataException("El socket no tiene un directorio valido.");
Directory.CreateDirectory(socketDirectory);
if (File.Exists(socketPath))
{
    File.Delete(socketPath);
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using Socket listener = new(
    AddressFamily.Unix,
    SocketType.Stream,
    ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
File.SetUnixFileMode(
    socketPath,
    UnixFileMode.UserRead | UnixFileMode.UserWrite |
    UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
listener.Listen(8);

SystemdMountManager manager = new();
Console.WriteLine("ATLAS NAS Provisioner listo.");
try
{
    while (!shutdown.IsCancellationRequested)
    {
        Socket client;
        try
        {
            client = await listener.AcceptAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        _ = HandleClientAsync(client, manager, shutdown.Token);
    }
}
finally
{
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }
}
return 0;

static async Task HandleClientAsync(
    Socket client,
    SystemdMountManager manager,
    CancellationToken shutdown)
{
    using Socket ownedClient = client;
    using CancellationTokenSource requestTimeout =
        CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    requestTimeout.CancelAfter(TimeSpan.FromSeconds(75));
    CancellationToken cancellationToken = requestTimeout.Token;
    NasProvisioningResponse response;
    try
    {
        byte[] lengthBuffer = new byte[4];
        await ReceiveExactlyAsync(client, lengthBuffer, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (length is < 2 or > ProvisioningValidator.MaximumMessageBytes)
        {
            throw new InvalidDataException("Longitud de solicitud no permitida.");
        }
        byte[] payload = new byte[length];
        await ReceiveExactlyAsync(client, payload, cancellationToken);
        NasProvisioningRequest? request = JsonSerializer.Deserialize(
            payload,
            ProvisioningJsonContext.Default.NasProvisioningRequest);
        NasProvisioningRequest normalized = request!;
        string errorCode = "INVALID_REQUEST";
        if (request is null || !ProvisioningValidator.TryNormalize(
                request,
                out normalized,
                out errorCode))
        {
            response = new NasProvisioningResponse(
                false,
                request?.Code ?? "",
                "REJECTED",
                request is null ? "INVALID_JSON" : errorCode,
                null,
                null,
                request?.Protocol);
        }
        else
        {
            response = await manager.ProvisionAsync(
                normalized,
                cancellationToken);
        }
    }
    catch (Exception exception) when (
        exception is not OutOfMemoryException and
        not StackOverflowException)
    {
        response = new NasProvisioningResponse(
            false,
            "",
            "FAILED",
            exception is OperationCanceledException
                ? "PROVISION_TIMEOUT"
                : "PROVISION_FAILED",
            SafeDetail(exception.Message),
            null,
            null);
    }

    try
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            response,
            ProvisioningJsonContext.Default.NasProvisioningResponse);
        byte[] lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, payload.Length);
        await SendExactlyAsync(client, lengthBuffer, cancellationToken);
        await SendExactlyAsync(client, payload, cancellationToken);
        client.Shutdown(SocketShutdown.Both);
    }
    catch (Exception exception) when (
        exception is SocketException or OperationCanceledException)
    {
        // The caller disconnected. Secrets and partial requests are not logged.
    }
}

static async Task SendExactlyAsync(
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

static async Task ReceiveExactlyAsync(
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
            throw new EndOfStreamException("Conexion local incompleta.");
        }
        received += count;
    }
}

static string SafeDetail(string value)
{
    string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return singleLine.Length <= 512 ? singleLine : singleLine[..512];
}
