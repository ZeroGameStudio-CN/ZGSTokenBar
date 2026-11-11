using ZGSTokenBar.Core;

sealed class GrokRefreshTransport(string? body, int status = 200) : IUsageTransport
{
    public List<UsageHttpRequest> Requests { get; } = [];

    public Task<UsageHttpResult> SendAsync(UsageHttpRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new UsageHttpResult(status, body, null));
    }
}
