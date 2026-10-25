using System.IO.Pipes;
using System.Text.Json;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Transport.NamedPipe;

public sealed class ZgsNamedPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan InitialFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnaryOperationTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumConnections = 64;
    private readonly ZgsTokenBarHost _host;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _connections = [];
    private readonly HashSet<NamedPipeServerStream> _pipes = [];
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
                lock (_sync)
                {
                    if (_connections.Count >= MaximumConnections)
                    {
                        pipe.Dispose();
                        pipe = null;
                        continue;
                    }
                    _pipes.Add(pipe);
                }
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
        try
        {
            await using (pipe)
            {
                ApiRequestEnvelope request;
                try
                {
                    using var initialFrameTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                    initialFrameTimeout.CancelAfter(InitialFrameTimeout);
                    request = await PipeProtocol.ReadAsync(
                            pipe,
                            ApiJsonContext.Default.ApiRequestEnvelope,
                            initialFrameTimeout.Token)
                        ?? throw new InvalidDataException("Request is missing.");
                }
                catch (OperationCanceledException) when (!serverCancellation.IsCancellationRequested)
                {
                    return;
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

                if (string.Equals(request.Method, "events.watch", StringComparison.Ordinal)
                    && (request.SchemaVersion != ZgsHostApi.SchemaVersion
                        || !PluginValidation.IsRequestId(request.RequestId)))
                {
                    await PipeProtocol.WriteAsync(
                        pipe,
                        new ApiResponseEnvelope(
                            1,
                            PluginValidation.IsRequestId(request.RequestId) ? request.RequestId! : "invalid",
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

                using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                var connectionToken = connectionCancellation.Token;
                var disconnectTask = MonitorDisconnectAsync(pipe, connectionToken);
                try
                {
                    if (string.Equals(request.Method, "events.watch", StringComparison.Ordinal))
                    {
                        var watchTask = WatchAsync(pipe, request, connectionToken);
                        if (await Task.WhenAny(watchTask, disconnectTask) == disconnectTask)
                        {
                            connectionCancellation.Cancel();
                        }
                        await watchTask;
                        return;
                    }

                    using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                    operationCancellation.CancelAfter(UnaryOperationTimeout);
                    var dispatchTask = _host.DispatchAsync(request, operationCancellation.Token).AsTask();
                    if (await Task.WhenAny(dispatchTask, disconnectTask) == disconnectTask)
                    {
                        operationCancellation.Cancel();
                        try { await dispatchTask; }
                        catch { }
                        return;
                    }
                    var response = await dispatchTask;
                    await WriteResponseOrFailureAsync(
                        pipe,
                        request.RequestId,
                        response,
                        connectionToken);
                }
                finally
                {
                    connectionCancellation.Cancel();
                    try { await disconnectTask; }
                    catch { }
                }
            }
        }
        finally
        {
            lock (_sync) _pipes.Remove(pipe);
        }
    }

    private static async Task MonitorDisconnectAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await pipe.ReadAsync(buffer, cancellationToken) == 0) return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
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
            var initialResponse = new ApiResponseEnvelope(
                1,
                request.RequestId,
                true,
                JsonSerializer.SerializeToElement(
                    subscription.Initial,
                    ApiJsonContext.Default.SnapshotSummary),
                null);
            if (!await WriteResponseOrFailureAsync(
                    pipe,
                    request.RequestId,
                    initialResponse,
                    cancellationToken)) return;
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
        catch (InvalidDataException)
        {
            // A single oversized event terminates this subscription without
            // taking down the host or other API connections.
        }
        finally
        {
            _host.Unsubscribe(subscription);
        }
    }

    private static async Task<bool> WriteResponseOrFailureAsync(
        Stream pipe,
        string? requestId,
        ApiResponseEnvelope response,
        CancellationToken cancellationToken)
    {
        try
        {
            await PipeProtocol.WriteAsync(
                pipe,
                response,
                ApiJsonContext.Default.ApiResponseEnvelope,
                cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            await PipeProtocol.WriteAsync(
                pipe,
                new ApiResponseEnvelope(
                    1,
                    PluginValidation.IsRequestId(requestId) ? requestId! : "invalid",
                    false,
                    null,
                    new("response_too_large", "The API response exceeds the frame limit.")),
                ApiJsonContext.Default.ApiResponseEnvelope,
                cancellationToken);
            return false;
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
            foreach (var pipe in _pipes.ToArray())
            {
                try { pipe.Dispose(); }
                catch { }
            }
        }
        if (acceptLoop is not null)
        {
            try { await acceptLoop; }
            catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { }
        _shutdown.Dispose();
    }
}
