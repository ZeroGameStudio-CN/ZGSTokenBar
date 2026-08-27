using System.IO.Pipes;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Transport.NamedPipe;

public sealed class ZgsNamedPipeClient
{
    private readonly string _pipeName;

    public ZgsNamedPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? ZgsPipeNaming.ForCurrentSession();
    }

    public async ValueTask<ApiResponseEnvelope> InvokeAsync(
        ApiRequestEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var pipe = CreateClient();
        try
        {
            await pipe.ConnectAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(request.RequestId);
        }
        catch (IOException)
        {
            return Unavailable(request.RequestId);
        }
        await PipeProtocol.WriteAsync(
            pipe,
            request,
            ApiJsonContext.Default.ApiRequestEnvelope,
            deadline.Token);
        return await PipeProtocol.ReadAsync(
                pipe,
                ApiJsonContext.Default.ApiResponseEnvelope,
                deadline.Token)
            ?? Unavailable(request.RequestId);
    }

    public async IAsyncEnumerable<object> WatchAsync(
        ApiRequestEnvelope request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pipe = CreateClient();
        var connected = false;
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            connected = true;
        }
        catch (IOException)
        {
        }
        if (!connected)
        {
            yield return Unavailable(request.RequestId);
            yield break;
        }
        await PipeProtocol.WriteAsync(
            pipe,
            request,
            ApiJsonContext.Default.ApiRequestEnvelope,
            cancellationToken);
        var response = await PipeProtocol.ReadAsync(
            pipe,
            ApiJsonContext.Default.ApiResponseEnvelope,
            cancellationToken);
        if (response is null) yield break;
        yield return response;
        if (!response.Ok) yield break;
        while (!cancellationToken.IsCancellationRequested)
        {
            HostEvent? hostEvent;
            try
            {
                hostEvent = await PipeProtocol.ReadAsync(
                    pipe,
                    ApiJsonContext.Default.HostEvent,
                    cancellationToken);
            }
            catch (EndOfStreamException)
            {
                yield break;
            }
            if (hostEvent is null) yield break;
            yield return hostEvent;
        }
    }

    private NamedPipeClientStream CreateClient() =>
        new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static ApiResponseEnvelope Unavailable(string requestId) =>
        new(1, requestId, false, null, new("app_not_running", "ZGSTokenBar is not running.", true));
}
