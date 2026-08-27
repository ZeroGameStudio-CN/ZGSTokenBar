using System.IO.Pipes;
using System.Text.Json;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Transport.NamedPipe;

public sealed class ZgsNamedPipeServer : IAsyncDisposable
{
    private readonly ZgsTokenBarHost _host;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _connections = [];
    private readonly object _sync = new();
    private Task? _acceptLoop;

    public ZgsNamedPipeServer(ZgsTokenBarHost host, string? pipeName = null)
    {
        _host = host;
        _pipeName = pipeName ?? ZgsPipeNaming.ForCurrentSession();
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        lock (_sync)
        {
            if (_acceptLoop is not null) return;
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                var connection = HandleConnectionAsync(pipe, cancellationToken);
                pipe = null;
                lock (_sync) _connections.Add(connection);
                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (_sync) _connections.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellation)
    {
        await using (pipe)
        {
            ApiRequestEnvelope request;
            try
            {
                request = await PipeProtocol.ReadAsync(
                        pipe,
                        ApiJsonContext.Default.ApiRequestEnvelope,
                        serverCancellation)
                    ?? throw new InvalidDataException("Request is missing.");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or EndOfStreamException)
            {
                var failure = new ApiResponseEnvelope(
                    1,
                    "invalid",
                    false,
                    null,
                    new("invalid_request", "Request frame is invalid."));
                try
                {
                    await PipeProtocol.WriteAsync(
                        pipe,
                        failure,
                        ApiJsonContext.Default.ApiResponseEnvelope,
                        serverCancellation);
                }
                catch { }
                return;
            }

            if (string.Equals(request.Method, "events.watch", StringComparison.Ordinal))
            {
                if (request.SchemaVersion != ZgsHostApi.SchemaVersion
                    || !PluginValidation.IsRequestId(request.RequestId))
                {
                    await PipeProtocol.WriteAsync(
                        pipe,
                        new ApiResponseEnvelope(
                            1,
                            request.RequestId ?? "invalid",
                            false,
                            null,
                            new(
                                request.SchemaVersion == ZgsHostApi.SchemaVersion
                                    ? "invalid_request"
                                    : "api_version_unsupported",
                                "events.watch envelope is invalid.")),
                        ApiJsonContext.Default.ApiResponseEnvelope,
                        serverCancellation);
                    return;
                }
                await WatchAsync(pipe, request, serverCancellation);
                return;
            }

            var response = await _host.DispatchAsync(request, serverCancellation);
            await PipeProtocol.WriteAsync(
                pipe,
                response,
                ApiJsonContext.Default.ApiResponseEnvelope,
                serverCancellation);
        }
    }

    private async Task WatchAsync(
        NamedPipeServerStream pipe,
        ApiRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var includeValues = false;
        if (request.Params is JsonElement parameters)
        {
            if (parameters.ValueKind is not JsonValueKind.Object
                || parameters.EnumerateObject().Any(property => property.Name != "includeValues")
                || parameters.TryGetProperty("includeValues", out var value)
                    && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                await PipeProtocol.WriteAsync(
                    pipe,
                    new ApiResponseEnvelope(
                        1,
                        request.RequestId,
                        false,
                        null,
                        new("invalid_request", "events.watch params are invalid.")),
                    ApiJsonContext.Default.ApiResponseEnvelope,
                    cancellationToken);
                return;
            }
            includeValues = parameters.TryGetProperty("includeValues", out var include)
                && include.GetBoolean();
        }

        var subscription = _host.Subscribe(includeValues);
        try
        {
            await PipeProtocol.WriteAsync(
                pipe,
                new ApiResponseEnvelope(
                    1,
                    request.RequestId,
                    true,
                    JsonSerializer.SerializeToElement(
                        subscription.Initial,
                        ApiJsonContext.Default.SnapshotSummary),
                    null),
                ApiJsonContext.Default.ApiResponseEnvelope,
                cancellationToken);
            await foreach (var hostEvent in subscription.Events.ReadAllAsync(cancellationToken))
            {
                await PipeProtocol.WriteAsync(
                    pipe,
                    hostEvent,
                    ApiJsonContext.Default.HostEvent,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _host.Unsubscribe(subscription);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        Task? acceptLoop;
        Task[] connections;
        lock (_sync)
        {
            acceptLoop = _acceptLoop;
            connections = _connections.ToArray();
        }
        if (acceptLoop is not null)
        {
            try { await acceptLoop; }
            catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }
        _shutdown.Dispose();
    }
}
