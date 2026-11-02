using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class PluginCardEventWatcher
{
    internal static async Task RunAsync(ZgsTokenBarHost host, ZgsTokenBarHost.Subscription subscription,
        Func<HostEvent?, bool> update, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var item in subscription.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        if (!update(item)) return;
                    return;
                }
                catch (HostCommandException error) when (error.Code == "event_backpressure")
                {
                    host.Unsubscribe(subscription);
                    cancellationToken.ThrowIfCancellationRequested();
                    // Subscribe before reading the latest cards so no subsequent change is missed.
                    subscription = host.Subscribe(includeValues: true);
                    if (!update(null)) return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { host.Unsubscribe(subscription); }
    }
}
