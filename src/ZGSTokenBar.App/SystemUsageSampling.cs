using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal static class SystemUsageSampling
{
    public static async Task<SystemUsageSnapshot?> TrySampleAsync(
        SemaphoreSlim gate,
        Func<bool, SystemUsageSnapshot> sample,
        bool includeProcesses,
        CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            return await Task.Run(
                    () => sample(includeProcesses),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
