namespace ZGSTokenBar.App;

internal static class SystemUsageSampling
{
    public static async Task TrySampleAsync(
        SemaphoreSlim gate,
        Func<bool, SystemUsageSnapshot> sample,
        Action<SystemUsageSnapshot> publish,
        bool includeProcesses,
        CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var snapshot = await Task.Run(() => sample(includeProcesses), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Keep the gate until the caller's UI context has applied this sample.
            publish(snapshot);
        }
        finally
        {
            gate.Release();
        }
    }
}
