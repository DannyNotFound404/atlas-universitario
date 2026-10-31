namespace Miguel.Agent;

internal static class AgentCycle
{
    internal static async Task RunAsync(Func<Task> collectAndFlush, Func<Task> heartbeat, CancellationToken token)
    {
        Exception? collectionError = null;
        try { await collectAndFlush(); }
        catch (Exception error) { collectionError = error; }
        try { if (!token.IsCancellationRequested) await heartbeat(); }
        catch (Exception heartbeatError) when (collectionError is not null)
        { throw new AggregateException("Collection and heartbeat failed independently.", collectionError, heartbeatError); }
        if (collectionError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(collectionError).Throw();
    }
}
