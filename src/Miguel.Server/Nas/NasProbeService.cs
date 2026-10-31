using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Miguel.Server.Admin;

namespace Miguel.Server.Nas;

public sealed class NasProbeService(
    InfrastructureRepository repository,
    NasPathPolicy pathPolicy,
    IOptions<ServerOptions> optionsAccessor,
    TimeProvider timeProvider,
    ILogger<NasProbeService> logger) : BackgroundService
{
    private readonly SemaphoreSlim probeLock = new(1, 1);

    public async Task<NasProbeResult?> ProbeNowAsync(
        Guid nasId,
        CancellationToken cancellationToken)
    {
        AdminNasDefinition? appliance =
            await repository.FindNasAsync(nasId, cancellationToken);
        if (appliance is null || !appliance.Enabled)
        {
            return null;
        }
        await probeLock.WaitAsync(cancellationToken);
        try
        {
            appliance = await repository.FindNasAsync(nasId, cancellationToken);
            return appliance is null || !appliance.Enabled ? null : await ProbeAndRecordAsync(appliance, cancellationToken);
        }
        finally
        {
            probeLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalMinutes = Math.Clamp(
            optionsAccessor.Value.NasProbeIntervalMinutes,
            1,
            60);
        using PeriodicTimer timer = new(
            TimeSpan.FromMinutes(intervalMinutes),
            timeProvider);
        try
        {
            await ProbeAllSafelyAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProbeAllSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El servidor se esta deteniendo.
        }
    }

    private async Task ProbeAllSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProbeAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "El ciclo de sondeo NAS se interrumpio temporalmente; " +
                "ATLAS lo reintentara en el siguiente intervalo.");
        }
    }

    private async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        if (!await probeLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }
        try
        {
            IReadOnlyList<AdminNasDefinition> appliances =
                await repository.ReadNasAsync(cancellationToken);
            foreach (AdminNasDefinition appliance in appliances.Where(item => item.Enabled))
            {
                try
                {
                    var current = await repository.FindNasAsync(appliance.Id, cancellationToken);
                    if (current?.Enabled == true) await ProbeAndRecordAsync(current, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "No se pudo registrar el sondeo del NAS {NasCode}.",
                        appliance.Code);
                }
            }
        }
        finally
        {
            probeLock.Release();
        }
    }

    private async Task<NasProbeResult> ProbeAndRecordAsync(
        AdminNasDefinition appliance,
        CancellationToken cancellationToken)
    {
        NasProbeResult result = await ProbeAsync(appliance, cancellationToken);
        await repository.RecordProbeAsync(
            appliance.Id,
            result,
            timeProvider.GetUtcNow(),
            cancellationToken, appliance.LifecycleRevision);
        return result;
    }

    private async Task<NasProbeResult> ProbeAsync(
        AdminNasDefinition appliance,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = Math.Clamp(
            optionsAccessor.Value.NasProbeTimeoutSeconds,
            1,
            30);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(
                appliance.Host,
                appliance.SmbPort,
                timeout.Token);
            stopwatch.Stop();
        }
        catch (Exception exception) when (
            exception is SocketException or OperationCanceledException)
        {
            return new NasProbeResult(
                NasProbeStatuses.Offline,
                null,
                null,
                null,
                exception is OperationCanceledException
                    ? "SMB_TIMEOUT"
                    : "SMB_UNREACHABLE");
        }

        int latency = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        string mountPath = pathPolicy.GetMountPath(appliance);
        if (!Directory.Exists(mountPath))
        {
            return new NasProbeResult(
                NasProbeStatuses.Degraded,
                latency,
                null,
                null,
                "MOUNT_NOT_AVAILABLE");
        }

        try
        {
            _ = File.GetAttributes(mountPath);
            if (!NasMountInspector.IsSupportedReadOnlyMount(mountPath))
            {
                return new NasProbeResult(
                    NasProbeStatuses.Degraded,
                    latency,
                    null,
                    null,
                    "MOUNT_NOT_SUPPORTED_READ_ONLY");
            }

            DriveInfo? drive = DriveInfo.GetDrives()
                .Where(item => item.IsReady && NasMountInspector.PathsEqual(
                    item.RootDirectory.FullName,
                    mountPath))
                .FirstOrDefault();
            return new NasProbeResult(
                NasProbeStatuses.Online,
                latency,
                drive?.TotalSize,
                drive?.AvailableFreeSpace,
                null);
        }
        catch (IOException)
        {
            return new NasProbeResult(
                NasProbeStatuses.Degraded,
                latency,
                null,
                null,
                "MOUNT_NOT_READABLE");
        }
        catch (UnauthorizedAccessException)
        {
            return new NasProbeResult(
                NasProbeStatuses.Degraded,
                latency,
                null,
                null,
                "MOUNT_ACCESS_DENIED");
        }
    }

}
