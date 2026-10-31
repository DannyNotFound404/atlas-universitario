using Microsoft.Extensions.Options;

namespace Miguel.Server.NetworkDiscovery;

public sealed class NetworkDiscoveryService(
    NetworkDiscoveryRepository repository,
    INetworkDiscoveryEngine engine,
    IOptions<ServerOptions> options,
    TimeProvider timeProvider,
    ILogger<NetworkDiscoveryService> logger) : BackgroundService
{
    private readonly ServerOptions _options=options.Value;
    private int _active;
    private DateTimeOffset? _nextAutomaticScanAtUtc;
    public bool Enabled=>_options.NetworkDiscoveryEnabled;
    public bool AutomaticEnabled=>Enabled&&_options.NetworkDiscoveryAutomaticEnabled;
    public bool EngineAvailable=>engine.IsAvailable;
    public bool IcmpAvailable=>engine.IcmpAvailable;
    public bool ScanInProgress=>Volatile.Read(ref _active)>0;
    public DateTimeOffset? NextAutomaticScanAtUtc=>_nextAutomaticScanAtUtc;

    public async Task<QueueNetworkScanResponse?> QueueAsync(string? scopeCode,string requestedBy,CancellationToken ct,
        string? startAddress=null,string? endAddress=null,bool fullIdentification=true)
    {
        if(!Enabled||!EngineAvailable)return null;
        var queued=await repository.QueueDurableAsync(scopeCode,requestedBy,timeProvider.GetUtcNow(),false,ct,startAddress,endAddress,fullIdentification);
        if(queued.Queued+queued.AlreadyQueued>0)Interlocked.Exchange(ref _active,1);
        return queued;
    }
    protected override Task ExecuteAsync(CancellationToken ct)=>Task.WhenAll(
        Enumerable.Range(0,_options.NetworkDiscoveryWorkers).Select(index=>WorkerAsync(index,ct)));

    private async Task WorkerAsync(int index,CancellationToken ct)
    {
        Guid owner=Guid.NewGuid();
        DateTimeOffset automaticStart=timeProvider.GetUtcNow().AddSeconds(_options.NetworkDiscoveryStartupDelaySeconds);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(index==0)
                {
                    await repository.RecoverJobsAsync(timeProvider.GetUtcNow(),ct);
                    if(AutomaticEnabled&&EngineAvailable&&timeProvider.GetUtcNow()>=automaticStart)
                        await repository.QueueDurableAsync(null,"system:scheduler",timeProvider.GetUtcNow(),true,ct);
                    _nextAutomaticScanAtUtc=!AutomaticEnabled?null:timeProvider.GetUtcNow()<automaticStart?automaticStart:await repository.ReadNextAutomaticAsync(ct);
                }
                if(Enabled&&EngineAvailable)
                {
                    var claim=await repository.ClaimAsync(owner,timeProvider.GetUtcNow(),ct);
                    if(claim is not null){Interlocked.Exchange(ref _active,1);await ProcessAsync(claim,ct);continue;}
                }
                Interlocked.Exchange(ref _active,await repository.HasActiveJobsAsync(ct)?1:0);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception exception){logger.LogError(exception,"El trabajador de redes {Worker} continuará después de un fallo.",index);}
            try{await Task.Delay(TimeSpan.FromSeconds(5),timeProvider,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
    public async Task ProcessAsync(NetworkScanClaim claim,CancellationToken ct)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task watchdog=WatchClaimAsync(claim,lifetime);
        try
        {
            var targets=await repository.IdentifyTargetsAsync(claim.Scope,timeProvider.GetUtcNow(),lifetime.Token);
            var result=engine is INetworkProgressEngine live
                ? await live.DiscoverWithProgressAsync(claim.Scope,targets,async(progress,token)=>
                {
                    if(!await repository.RecordProgressAsync(claim,progress,timeProvider.GetUtcNow(),token))
                    {
                        lifetime.Cancel();
                        throw new OperationCanceledException("network_job_ownership_lost",token);
                    }
                },lifetime.Token)
                : await engine.DiscoverAsync(claim.Scope,targets,lifetime.Token);
            await repository.RecordSuccessfulRunAsync(claim.Scope,claim.RunId,result.Observations,timeProvider.GetUtcNow(),lifetime.Token,claim.Owner,result);
        }
        catch(OperationCanceledException)
        {await TryFailAsync(claim,NetworkScanStatuses.Interrupted,"worker_interrupted");}
        catch(Exception exception)
        {logger.LogWarning(exception,"El sondeo de {Scope} se conservará con su resultado de fallo.",claim.Scope.Code);await TryFailAsync(claim,exception is TimeoutException?NetworkScanStatuses.TimedOut:NetworkScanStatuses.Failed,"network_probe_failed");}
        finally
        {
            lifetime.Cancel();
            try{await watchdog;}catch(OperationCanceledException){}
        }
    }
    private async Task WatchClaimAsync(NetworkScanClaim claim,CancellationTokenSource lifetime)
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                if(!await repository.MaintainClaimAsync(claim,timeProvider.GetUtcNow(),lifetime.Token)){lifetime.Cancel();return;}
                await Task.Delay(TimeSpan.FromSeconds(5),timeProvider,lifetime.Token);
            }
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception exception){logger.LogWarning(exception,"Se cancela el sondeo al perder su control durable.");lifetime.Cancel();}
    }
    private async Task TryFailAsync(NetworkScanClaim claim,string status,string code)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try{await repository.FailClaimAsync(claim,status,code,timeProvider.GetUtcNow(),deadline.Token);}
        catch(Exception exception){logger.LogWarning(exception,"La recuperación por vencimiento cerrará el sondeo {Run}.",claim.RunId);}
    }
}
