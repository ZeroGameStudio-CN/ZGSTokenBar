using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;

sealed class RadarResetCountdownRequestHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _pageCalls;

    public DateTimeOffset HomeTarget { get; set; } =
        DateTimeOffset.Parse("2030-04-05T06:00:00+08:00");
    public DateTimeOffset? JsonTarget { get; init; }
    public DateTimeOffset? OpenedAt { get; set; } =
        DateTimeOffset.Parse("2030-04-04T14:00:00+08:00");
    public bool FailPage { get; init; }
    public int PageCalls => Volatile.Read(ref _pageCalls);
    public bool PageAcceptsHtml { get; private set; } = true;
    public bool PageSawNoCache { get; private set; } = true;
    public bool PageHasProductUserAgent { get; private set; } = true;
    public bool HasAuthorization { get; private set; }
    public bool HasCookie { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var isPage = request.RequestUri?.AbsoluteUri == RadarService.SiteUri.AbsoluteUri;
        lock (_gate)
        {
            HasAuthorization |= request.Headers.Authorization is not null;
            HasCookie |= request.Headers.Contains("Cookie");
            if (isPage)
            {
                Interlocked.Increment(ref _pageCalls);
                PageAcceptsHtml &= request.Headers.Accept.Any(value =>
                    string.Equals(value.MediaType, "text/html", StringComparison.OrdinalIgnoreCase));
                PageSawNoCache &= request.Headers.CacheControl?.NoCache == true;
                PageHasProductUserAgent &= request.Headers.UserAgent.Any(value =>
                    string.Equals(value.Product?.Name, "ZGSTokenBar", StringComparison.Ordinal));
            }
        }

        if (isPage && FailPage)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        var content = isPage
            ? FormattableString.Invariant(
                $"<html><div data-window-clock data-window-closes-at='{HomeTarget:O}'></div></html>")
            : request.RequestUri?.AbsoluteUri == RadarService.SummaryUri.AbsoluteUri
                ? Summary()
                : "{}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                isPage ? "text/html" : "application/json"),
        });
    }

    private string Summary() => JsonSerializer.Serialize(new
    {
        window = new
        {
            open = true,
            opened_at = OpenedAt?.ToString("O", CultureInfo.InvariantCulture),
            source_url = "https://x.com/example/status/reset-window",
            target_at = JsonTarget?.ToString("O", CultureInfo.InvariantCulture),
        },
        model_iq = new
        {
            updated_at = "2030-04-04T20:00:00Z",
            latest = new
            {
                date = "2030-04-04",
                model = "gpt-test",
                reasoning_effort = "test",
                score = 100,
                status = "green",
            },
        },
    });
}
