using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public sealed class ZgsTokenBarHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<IZgsPlugin> _plugins;
    private readonly Dictionary<string, IZgsPlugin> _pluginsById;
    private readonly Dictionary<string, bool> _enabled;
    private readonly Dictionary<string, PluginDataSnapshot> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _dataRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly Dictionary<string, CursorState> _cursors = new(StringComparer.Ordinal);
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly EffectiveProfile _profile;
    private readonly string _productVersion;
    private readonly string _dataRoot;
    private readonly IDesktopControl? _desktop;
    private readonly bool _persistProfileState;
    private long _revision;
    private long _configRevision;
    private long _uiRevision;
    private bool _started;
    private bool _disposed;

    public ZgsTokenBarHost(
        IReadOnlyList<IZgsPlugin> plugins,
        EffectiveProfile profile,
        string productVersion,
        string dataRoot,
        IDesktopControl? desktop = null,
        bool persistProfileState = true)
    {
        _plugins = plugins
            .OrderBy(plugin => plugin.Manifest.Order)
            .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
        _profile = profile;
        _productVersion = productVersion;
        _dataRoot = dataRoot;
        _desktop = desktop;
        _persistProfileState = persistProfileState;

        var catalogErrors = PluginValidation.ValidateCatalog(_plugins.Select(plugin => plugin.Manifest).ToArray());
        if (catalogErrors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid plugin catalog: {string.Join(", ", catalogErrors)}");
        }

        _pluginsById = _plugins.ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);
        _enabled = profile.Plugins.ToDictionary(
            plugin => plugin.Id,
            plugin => plugin.Enabled,
            StringComparer.Ordinal);
        if (profile.Plugins.Count != _enabled.Count
            || profile.Plugins.Any(item =>
                !_pluginsById.TryGetValue(item.Id, out var plugin)
                || !string.Equals(item.Version, plugin.Manifest.Version, StringComparison.Ordinal))
            || _plugins.Any(plugin => !_enabled.ContainsKey(plugin.Manifest.Id))
            || _plugins.Any(plugin =>
                plugin.Manifest.Required && !_enabled[plugin.Manifest.Id])
            || _plugins.Any(plugin =>
                _enabled[plugin.Manifest.Id]
                && plugin.Manifest.Requires.Any(dependency =>
                    !_enabled.TryGetValue(dependency, out var dependencyEnabled)
                    || !dependencyEnabled)))
        {
            throw new InvalidOperationException("Profile composition is invalid.");
        }
        foreach (var plugin in _plugins)
        {
            if (!_enabled.ContainsKey(plugin.Manifest.Id))
            {
                throw new InvalidOperationException($"Profile is missing plugin {plugin.Manifest.Id}.");
            }
            _dataRevisions[plugin.Manifest.Id] = 0;
            _refreshGates[plugin.Manifest.Id] = new SemaphoreSlim(1, 1);
            _data[plugin.Manifest.Id] = EmptySnapshot(plugin.Manifest, _enabled[plugin.Manifest.Id]);
        }
    }

    public string ProfileName => _profile.Name;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started) return;
            _started = true;
        }

        var started = new List<IZgsPlugin>();
        try
        {
            if (_persistProfileState) ProfileStateStore.SaveLastKnownGood(_dataRoot, GetProfile());
            foreach (var plugin in DependencyOrder())
            {
                if (!_enabled[plugin.Manifest.Id]) continue;
                if (plugin.Manifest.Requires.Any(dependency =>
                        !started.Any(item => item.Manifest.Id == dependency)))
                {
                    Publish(StartFailure(plugin.Manifest, PluginHealthCode.Unavailable));
                    continue;
                }
                var pluginRoot = Path.Combine(_dataRoot, "plugin-data", plugin.Manifest.Id);
                Directory.CreateDirectory(pluginRoot);
                try
                {
                    await plugin.StartAsync(
                        new PluginStartContext(_profile.Name, pluginRoot, DateTimeOffset.UtcNow),
                        cancellationToken);
                    started.Add(plugin);
                }
                catch (Exception exception) when (
                    !plugin.Manifest.Required && exception is not OperationCanceledException)
                {
                    Publish(StartFailure(
                        plugin.Manifest,
                        exception is HostCommandException { Code: "trust_failed" }
                            ? PluginHealthCode.TrustFailed
                            : PluginHealthCode.Unavailable));
                }
            }
            ValidateRuntimeCatalog();
        }
        catch
        {
            foreach (var plugin in started.AsEnumerable().Reverse())
            {
                try { await plugin.StopAsync(CancellationToken.None); }
                catch { }
            }
            lock (_sync) _started = false;
            throw;
        }
    }

    public HostDescription Describe()
    {
        lock (_sync)
        {
            return new(
                ZgsHostApi.Major,
                ZgsHostApi.Minor,
                _productVersion,
                Environment.ProcessId,
                _profile.Name,
                ["plugins", "snapshots", "commands", "events", .. _desktop is null ? [] : new[] { "desktop", "mini", "mini-order", "window" }],
                RevisionsLocked());
        }
    }

    public EffectiveProfile GetProfile()
    {
        lock (_sync)
        {
            return _profile with
            {
                Plugins = _profile.Plugins
                    .Select(plugin => plugin with { Enabled = _enabled[plugin.Id] })
                    .ToArray(),
            };
        }
    }

    public IReadOnlyList<PluginStatus> ListPlugins()
    {
        lock (_sync)
        {
            return _plugins.Select(plugin =>
            {
                var id = plugin.Manifest.Id;
                return new PluginStatus(
                    plugin.Manifest,
                    _enabled[id],
                    _dataRevisions[id],
                    _data[id].Health,
                    plugin.Commands,
                    plugin.Settings);
            }).ToArray();
        }
    }

    public PluginStatus? DescribePlugin(string pluginId) =>
        ListPlugins().FirstOrDefault(status =>
            string.Equals(status.Manifest.Id, pluginId, StringComparison.Ordinal));

    public byte[]? GetPluginIconPng(string pluginId)
    {
        ProcessPluginProxy process;
        lock (_sync)
        {
            if (!_enabled.TryGetValue(pluginId, out var enabled) || !enabled
                || !_pluginsById.TryGetValue(pluginId, out var plugin)
                || plugin is not ProcessPluginProxy)
            {
                return null;
            }
            process = (ProcessPluginProxy)plugin;
        }
        return process.ReadIconPng();
    }

    public IReadOnlyDictionary<string, string> GetPluginLocalization(
        string pluginId,
        string locale)
    {
        ProcessPluginProxy process;
        lock (_sync)
        {
            if (!_enabled.TryGetValue(pluginId, out var enabled) || !enabled
                || !_pluginsById.TryGetValue(pluginId, out var plugin)
                || plugin is not ProcessPluginProxy)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            process = (ProcessPluginProxy)plugin;
        }
        return process.ReadLocalization(locale);
    }

    public SnapshotSummary Snapshot(bool includeValues, string? pluginId = null)
    {
        lock (_sync)
        {
            if (pluginId is not null && !_pluginsById.ContainsKey(pluginId))
            {
                throw new HostCommandException("plugin_not_found", "Plugin was not found.");
            }
            IEnumerable<IZgsPlugin> selected = _plugins;
            if (pluginId is not null)
            {
                selected = selected.Where(plugin =>
                    string.Equals(plugin.Manifest.Id, pluginId, StringComparison.Ordinal));
            }
            return new(
                RevisionsLocked(),
                selected.Select(plugin =>
                {
                    var id = plugin.Manifest.Id;
                    return new PluginSnapshotSummary(
                        id,
                        _enabled[id],
                        _data[id].Health,
                        _dataRevisions[id],
                        includeValues ? _data[id].MiniCards : null);
                }).ToArray());
        }
    }

    public void Publish(PluginDataSnapshot snapshot)
    {
        if (!_pluginsById.TryGetValue(snapshot.PluginId, out var plugin))
        {
            throw new ArgumentException("Unknown plugin.", nameof(snapshot));
        }
        var validation = PluginValidation.ValidateSnapshot(plugin.Manifest, snapshot);
        if (validation.Count == 0)
        {
            lock (_sync)
            {
                var existingIds = _data
                    .Where(pair => !string.Equals(pair.Key, snapshot.PluginId, StringComparison.Ordinal))
                    .SelectMany(pair => ContributionIds(pair.Value))
                    .ToHashSet(StringComparer.Ordinal);
                if (ContributionIds(snapshot).Any(id => !existingIds.Add(id)))
                {
                    validation = ["duplicate_contribution_id"];
                }
            }
        }
        if (validation.Count > 0)
        {
            snapshot = snapshot with
            {
                MiniCards = [],
                Details = [],
                Radar = [],
                SafeMetadata = null,
                Health = new(
                    PluginHealthCode.InvalidContribution,
                    false,
                    false,
                    DateTimeOffset.UtcNow,
                    "plugin.invalid_contribution"),
            };
        }

        HostEvent hostEvent;
        lock (_sync)
        {
            ThrowIfDisposed();
            _data[snapshot.PluginId] = snapshot;
            _dataRevisions[snapshot.PluginId]++;
            _revision++;
            InvalidateCursorsLocked(snapshot.PluginId);
            hostEvent = EventLocked("plugin.data.changed", snapshot.PluginId);
        }
        PublishEvent(hostEvent);
    }

    public async ValueTask<PluginDataSnapshot> RefreshPluginAsync(
        string pluginId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!_pluginsById.TryGetValue(pluginId, out var plugin))
        {
            throw new HostCommandException("plugin_not_found", "Plugin was not found.");
        }
        lock (_sync)
        {
            if (!_enabled[pluginId])
            {
                throw new HostCommandException("plugin_disabled", "Plugin is disabled.");
            }
        }
        if (plugin is not IDataSource dataSource)
        {
            lock (_sync) return _data[pluginId];
        }

        var gate = _refreshGates[pluginId];
        await gate.WaitAsync(cancellationToken);
        try
        {
            long previousRevision;
            lock (_sync) previousRevision = _dataRevisions[pluginId];
            var snapshot = await dataSource.RefreshAsync(
                new PluginRefreshContext(DateTimeOffset.UtcNow, reason, previousRevision),
                cancellationToken);
            Publish(snapshot);
            return snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not HostCommandException)
        {
            PluginDataSnapshot previous;
            lock (_sync) previous = _data[pluginId];
            var failed = previous with
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Health = new(
                    PluginHealthCode.Unavailable,
                    false,
                    true,
                    DateTimeOffset.UtcNow,
                    "plugin.refresh_failed"),
            };
            Publish(failed);
            return failed;
        }
        catch (HostCommandException exception)
        {
            PluginDataSnapshot previous;
            lock (_sync) previous = _data[pluginId];
            Publish(previous with
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Health = new(
                    exception.Code switch
                    {
                        "timeout" => PluginHealthCode.Timeout,
                        "trust_failed" => PluginHealthCode.TrustFailed,
                        _ => PluginHealthCode.Unavailable,
                    },
                    false,
                    exception.Retryable,
                    DateTimeOffset.UtcNow,
                    "plugin.refresh_failed"),
            });
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SetEnabledAsync(
        string pluginId,
        bool enabled,
        long expectedConfigRevision,
        CancellationToken cancellationToken)
    {
        await _configGate.WaitAsync(cancellationToken);
        try
        {
            await SetEnabledCoreAsync(pluginId, enabled, expectedConfigRevision, cancellationToken);
        }
        finally
        {
            _configGate.Release();
        }
    }

    private async ValueTask SetEnabledCoreAsync(
        string pluginId,
        bool enabled,
        long expectedConfigRevision,
        CancellationToken cancellationToken)
    {
        if (!_pluginsById.TryGetValue(pluginId, out var plugin))
        {
            throw new HostCommandException("plugin_not_found", "Plugin was not found.");
        }
        if (!enabled && plugin.Manifest.Required)
        {
            throw new HostCommandException("plugin_required", "Required plugin cannot be disabled.");
        }

        bool changed;
        lock (_sync)
        {
            if (expectedConfigRevision != _configRevision)
            {
                throw HostCommandException.Conflict("config", _configRevision);
            }
            changed = _enabled[pluginId] != enabled;
            if (!changed) return;
            if (enabled)
            {
                var missing = plugin.Manifest.Requires
                    .FirstOrDefault(id => !_enabled.TryGetValue(id, out var dependencyEnabled) || !dependencyEnabled);
                if (missing is not null)
                {
                    throw new HostCommandException("plugin_disabled", "A required plugin dependency is disabled.");
                }
            }
            else
            {
                var dependent = _plugins.FirstOrDefault(candidate =>
                    _enabled[candidate.Manifest.Id]
                    && candidate.Manifest.Requires.Contains(pluginId, StringComparer.Ordinal));
                if (dependent is not null)
                {
                    throw new HostCommandException(
                        "plugin_required",
                        $"Disable dependent plugin {dependent.Manifest.Id} first.");
                }
            }
            _enabled[pluginId] = enabled;
        }

        try
        {
            if (_started)
            {
                if (enabled)
                {
                    var pluginRoot = Path.Combine(_dataRoot, "plugin-data", pluginId);
                    Directory.CreateDirectory(pluginRoot);
                    await plugin.StartAsync(
                        new PluginStartContext(_profile.Name, pluginRoot, DateTimeOffset.UtcNow),
                        cancellationToken);
                }
                else
                {
                    await plugin.StopAsync(cancellationToken);
                }
            }
        }
        catch
        {
            lock (_sync) _enabled[pluginId] = !enabled;
            throw;
        }

        if (_desktop is not null)
        {
            try
            {
                await _desktop.PersistPluginEnabledAsync(pluginId, enabled, cancellationToken);
            }
            catch
            {
                lock (_sync) _enabled[pluginId] = !enabled;
                if (_started)
                {
                    try
                    {
                        if (enabled) await plugin.StopAsync(CancellationToken.None);
                        else
                        {
                            var pluginRoot = Path.Combine(_dataRoot, "plugin-data", pluginId);
                            await plugin.StartAsync(
                                new PluginStartContext(_profile.Name, pluginRoot, DateTimeOffset.UtcNow),
                                CancellationToken.None);
                        }
                    }
                    catch { }
                }
                throw new HostCommandException("internal", "Plugin setting could not be saved.");
            }
        }

        HostEvent hostEvent;
        lock (_sync)
        {
            _configRevision++;
            _revision++;
            _dataRevisions[pluginId]++;
            _data[pluginId] = EmptySnapshot(plugin.Manifest, enabled);
            InvalidateCursorsLocked(pluginId);
            hostEvent = EventLocked("plugin.config.changed", pluginId);
        }
        PublishEvent(hostEvent);
    }

    public PagedPluginData ReadPluginData(string pluginId, string? cursor, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 32);
        lock (_sync)
        {
            if (!_pluginsById.ContainsKey(pluginId))
            {
                throw new HostCommandException("plugin_not_found", "Plugin was not found.");
            }

            var dataRevision = _dataRevisions[pluginId];
            var start = 0;
            if (cursor is not null)
            {
                if (!_cursors.TryGetValue(cursor, out var state)
                    || !string.Equals(state.PluginId, pluginId, StringComparison.Ordinal)
                    || state.DataRevision != dataRevision)
                {
                    throw new HostCommandException("data_changed", "Plugin data changed; restart pagination.");
                }
                start = state.NextIndex;
                _cursors.Remove(cursor);
            }

            var items = FlattenData(_data[pluginId]);
            var pageItems = new List<JsonElement>(pageSize);
            var estimatedBytes = 512;
            foreach (var item in items.Skip(start).Take(pageSize))
            {
                var itemBytes = Encoding.UTF8.GetByteCount(item.GetRawText()) + 2;
                if (pageItems.Count > 0
                    && estimatedBytes + itemBytes > ZgsHostApi.MaximumFrameBytes - 1024)
                {
                    break;
                }
                if (itemBytes > ZgsHostApi.MaximumFrameBytes - 2048)
                {
                    throw new HostCommandException(
                        "invalid_request",
                        "A plugin data item exceeds the frame limit.");
                }
                pageItems.Add(item);
                estimatedBytes += itemBytes;
            }
            var page = pageItems.ToArray();
            var nextIndex = start + page.Length;
            string? nextCursor = null;
            if (nextIndex < items.Count)
            {
                nextCursor = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                _cursors[nextCursor] = new(pluginId, dataRevision, nextIndex);
            }
            return new(pluginId, dataRevision, page, nextCursor);
        }
    }

    public Subscription Subscribe(bool includeValues)
    {
        lock (_sync)
        {
            var subscription = new Subscription(includeValues, Snapshot(includeValues));
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public void Unsubscribe(Subscription subscription)
    {
        lock (_sync) _subscriptions.Remove(subscription);
        subscription.Complete();
    }

    public async ValueTask<ApiResponseEnvelope> DispatchAsync(
        ApiRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1)
        {
            return Failure(request.RequestId, "api_version_unsupported", "API schema is unsupported.");
        }
        if (!ValidRequestId(request.RequestId))
        {
            return Failure(request.RequestId, "invalid_request", "requestId is invalid.");
        }
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return Failure(request.RequestId, "invalid_request", "method is required.");
        }

        try
        {
            var result = request.Method switch
            {
                "host.describe" => ToElement(Describe(), ApiJsonContext.Default.HostDescription),
                "profile.get" => ToElement(GetProfile(), ApiJsonContext.Default.EffectiveProfile),
                "plugin.list" => ToElement(ListPlugins().ToArray(), ApiJsonContext.Default.PluginStatusArray),
                "plugin.describe" => DescribePluginRequest(request.Params),
                "plugin.data.get" => ReadPluginDataRequest(request.Params),
                "plugin.setEnabled" => await SetEnabledRequestAsync(request.Params, cancellationToken),
                "plugin.refresh" => await RefreshRequestAsync(request.Params, cancellationToken),
                "plugin.reconcileCredentials" => await ReconcileCredentialsRequestAsync(
                    request.Params,
                    cancellationToken),
                "snapshot.get" => SnapshotRequest(request.Params),
                "commands.list" => CommandsListRequest(),
                "commands.invoke" => await InvokeCommandRequestAsync(request.Params, cancellationToken),
                "ui.mini.get" => await MiniGetAsync(cancellationToken),
                "ui.mini.setCollapsed" => await MiniSetAsync(request.Params, cancellationToken),
                "ui.mini.setArea" => await MiniAreaSetAsync(request.Params, cancellationToken),
                "ui.mini.moveArea" => await MiniAreaMoveAsync(request.Params, cancellationToken),
                "window.inspect" => await WindowInspectAsync(cancellationToken),
                "app.requestRefresh" => await RequestRefreshAsync(request.Params, cancellationToken),
                "app.openSettings" => await OpenSettingsAsync(cancellationToken),
                "app.requestExit" => await RequestExitAsync(cancellationToken),
                "events.watch" => throw new HostCommandException("invalid_request", "events.watch requires a streaming transport."),
                _ => throw new HostCommandException("unknown_method", "API method is unknown."),
            };
            return new(1, request.RequestId, true, result, null);
        }
        catch (HostCommandException exception)
        {
            return Failure(request.RequestId, exception.Code, exception.SafeMessage, exception.Retryable, exception.Details);
        }
        catch (OperationCanceledException)
        {
            return Failure(request.RequestId, "timeout", "Operation timed out.", true);
        }
        catch
        {
            return Failure(request.RequestId, "internal", "Operation failed.");
        }
    }

    private JsonElement DescribePluginRequest(JsonElement? parameters)
    {
        var pluginId = RequiredString(parameters, "pluginId", ["pluginId"]);
        var status = DescribePlugin(pluginId)
            ?? throw new HostCommandException("plugin_not_found", "Plugin was not found.");
        return ToElement(status, ApiJsonContext.Default.PluginStatus);
    }

    private JsonElement ReadPluginDataRequest(JsonElement? parameters)
    {
        EnsureFields(parameters, ["pluginId", "cursor", "pageSize"]);
        var pluginId = RequiredString(parameters, "pluginId");
        var cursor = OptionalString(parameters, "cursor");
        var pageSize = OptionalInt32(parameters, "pageSize") ?? 20;
        return ToElement(ReadPluginData(pluginId, cursor, pageSize), ApiJsonContext.Default.PagedPluginData);
    }

    private async ValueTask<JsonElement> SetEnabledRequestAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["pluginId", "enabled", "expectedConfigRevision"]);
        var pluginId = RequiredString(parameters, "pluginId");
        var enabled = RequiredBoolean(parameters, "enabled");
        var expected = RequiredInt64(parameters, "expectedConfigRevision");
        await SetEnabledAsync(pluginId, enabled, expected, cancellationToken);
        return ToElement(Describe(), ApiJsonContext.Default.HostDescription);
    }

    private async ValueTask<JsonElement> RefreshRequestAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["pluginId"]);
        var pluginId = OptionalString(parameters, "pluginId");
        if (pluginId is not null)
        {
            await RefreshPluginAsync(pluginId, "api", cancellationToken);
        }
        else
        {
            foreach (var plugin in _plugins.Where(plugin => _enabled[plugin.Manifest.Id]))
            {
                try
                {
                    await RefreshPluginAsync(plugin.Manifest.Id, "api", cancellationToken);
                }
                catch (HostCommandException)
                {
                    // A full refresh preserves per-plugin failure isolation.
                }
            }
        }
        return ToElement(Snapshot(false, pluginId), ApiJsonContext.Default.SnapshotSummary);
    }

    private async ValueTask<JsonElement> ReconcileCredentialsRequestAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var pluginId = RequiredString(parameters, "pluginId", ["pluginId"]);
        if (!_pluginsById.ContainsKey(pluginId))
        {
            throw new HostCommandException("plugin_not_found", "Plugin was not found.");
        }
        if (_enabled[pluginId])
        {
            await RefreshPluginAsync(pluginId, "credential_reconcile", cancellationToken);
        }
        return ObjectElement(("pluginId", pluginId), ("reconciled", true));
    }

    private JsonElement SnapshotRequest(JsonElement? parameters)
    {
        EnsureFields(parameters, ["pluginId", "includeValues"]);
        var pluginId = OptionalString(parameters, "pluginId");
        var includeValues = OptionalBoolean(parameters, "includeValues") ?? false;
        return ToElement(Snapshot(includeValues, pluginId), ApiJsonContext.Default.SnapshotSummary);
    }

    private JsonElement CommandsListRequest()
    {
        var commands = _plugins
            .Where(plugin => _enabled[plugin.Manifest.Id])
            .SelectMany(plugin => plugin.Commands)
            .OrderBy(command => command.Namespace, StringComparer.Ordinal)
            .ThenBy(command => command.Name, StringComparer.Ordinal)
            .ToArray();
        return ToElement(commands, PluginSdkJsonContext.Default.CommandDescriptorArray);
    }

    private async ValueTask<JsonElement> InvokeCommandRequestAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["commandId", "arguments", "params", "expectedRevision"]);
        var commandId = RequiredString(parameters, "commandId");
        var command = _plugins
            .SelectMany(plugin => plugin.Commands)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, commandId, StringComparison.Ordinal))
            ?? throw new HostCommandException("unknown_method", "Command was not found.");
        if (!_enabled[command.PluginId])
        {
            throw new HostCommandException("plugin_disabled", "Plugin is disabled.");
        }
        if (command.SecretSlots.Count > 0)
        {
            throw new HostCommandException("credential_forbidden", "Secret commands cannot cross the local API.");
        }
        var plugin = _pluginsById[command.PluginId];
        if (plugin is not ICommandContributor contributor)
        {
            throw new HostCommandException("unknown_method", "Command has no handler.");
        }
        var arguments = StringArray(parameters, "arguments");
        var commandParams = Child(parameters, "params");
        var expected = OptionalInt64(parameters, "expectedRevision");
        var result = await contributor.InvokeAsync(
            new CommandInvocation(commandId, arguments, commandParams, expected),
            cancellationToken);
        if (!result.Ok)
        {
            throw new HostCommandException(
                result.Error?.Code ?? "internal",
                result.Error?.Message ?? "Command failed.",
                result.Error?.Retryable ?? false,
                result.Error?.Details);
        }
        return result.Value ?? ObjectElement(("ok", true));
    }

    private async ValueTask<JsonElement> MiniGetAsync(CancellationToken cancellationToken)
    {
        var desktop = RequireDesktop();
        var state = await desktop.GetMiniStateAsync(cancellationToken);
        lock (_sync) _uiRevision = Math.Max(_uiRevision, state.UiRevision);
        return ToElement(state, ApiJsonContext.Default.MiniState);
    }

    private async ValueTask<JsonElement> MiniSetAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["collapsed", "expectedUiRevision"]);
        var collapsed = RequiredBoolean(parameters, "collapsed");
        var expected = RequiredInt64(parameters, "expectedUiRevision");
        var result = await RequireDesktop().SetMiniCollapsedAsync(collapsed, expected, cancellationToken);
        return RecordMiniMutation(result, collapsed ? "collapsed" : "expanded");
    }

    private async ValueTask<JsonElement> MiniAreaSetAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["areaId", "collapsed", "width", "expectedUiRevision"]);
        var areaId = RequiredString(parameters, "areaId");
        if (!PluginValidation.IsStableId(areaId))
        {
            throw new HostCommandException("invalid_request", "areaId is invalid.");
        }
        var collapsed = OptionalBoolean(parameters, "collapsed");
        var width = OptionalInt32(parameters, "width");
        if (collapsed is null && width is null)
        {
            throw new HostCommandException("invalid_request", "collapsed or width is required.");
        }
        var expected = RequiredInt64(parameters, "expectedUiRevision");
        var result = await RequireDesktop().SetMiniAreaAsync(
            areaId,
            collapsed,
            width,
            expected,
            cancellationToken);
        return RecordMiniMutation(result, areaId);
    }

    private async ValueTask<JsonElement> MiniAreaMoveAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["areaId", "beforeAreaId", "expectedUiRevision"]);
        var areaId = RequiredString(parameters, "areaId");
        var beforeAreaId = OptionalString(parameters, "beforeAreaId");
        if (!PluginValidation.IsStableId(areaId)
            || (beforeAreaId is not null && !PluginValidation.IsStableId(beforeAreaId)))
        {
            throw new HostCommandException("invalid_request", "areaId is invalid.");
        }
        var expected = RequiredInt64(parameters, "expectedUiRevision");
        var result = await RequireDesktop().MoveMiniAreaAsync(
            areaId,
            beforeAreaId,
            expected,
            cancellationToken);
        return RecordMiniMutation(result, areaId);
    }

    private JsonElement RecordMiniMutation(MiniMutationResult result, string eventValue)
    {
        HostEvent? hostEvent = null;
        lock (_sync)
        {
            var changed = result.UiRevision > _uiRevision;
            _uiRevision = result.UiRevision;
            if (changed)
            {
                _revision = Math.Max(_revision + 1, result.Revision);
                hostEvent = EventLocked("ui.mini.changed", eventValue);
            }
        }
        if (hostEvent is not null) PublishEvent(hostEvent);
        return ToElement(result, ApiJsonContext.Default.MiniMutationResult);
    }

    private async ValueTask<JsonElement> WindowInspectAsync(CancellationToken cancellationToken) =>
        ToElement(await RequireDesktop().InspectWindowAsync(cancellationToken), ApiJsonContext.Default.WindowInspection);

    private async ValueTask<JsonElement> RequestRefreshAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureFields(parameters, ["reloadSettings"]);
        var reloadSettings = OptionalBoolean(parameters, "reloadSettings") ?? false;
        await RequireDesktop().RequestRefreshAsync(reloadSettings, cancellationToken);
        return ObjectElement(("requested", true), ("settingsReloaded", reloadSettings));
    }

    private async ValueTask<JsonElement> OpenSettingsAsync(CancellationToken cancellationToken)
    {
        await RequireDesktop().OpenSettingsAsync(cancellationToken);
        return ObjectElement(("requested", true));
    }

    private async ValueTask<JsonElement> RequestExitAsync(CancellationToken cancellationToken)
    {
        await RequireDesktop().RequestExitAsync(cancellationToken);
        return ObjectElement(("requested", true));
    }

    private IDesktopControl RequireDesktop() =>
        _desktop ?? throw new HostCommandException(
            "command_requires_desktop",
            "Command requires the desktop profile.");

    private IReadOnlyList<IZgsPlugin> DependencyOrder()
    {
        var result = new List<IZgsPlugin>(_plugins.Count);
        var remaining = _plugins.ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(plugin => plugin.Manifest.Requires.All(id => result.Any(item => item.Manifest.Id == id)))
                .OrderBy(plugin => plugin.Manifest.Order)
                .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0) throw new InvalidOperationException("Plugin dependency cycle.");
            foreach (var plugin in ready)
            {
                result.Add(plugin);
                remaining.Remove(plugin.Manifest.Id);
            }
        }
        return result;
    }

    private void ValidateRuntimeCatalog()
    {
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        var commandNames = new HashSet<string>(StringComparer.Ordinal);
        var settingsIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in _plugins)
        {
            if (PluginValidation.ValidateCommands(plugin.Manifest, plugin.Commands).Count > 0
                || PluginValidation.ValidateSettings(plugin.Manifest, plugin.Settings).Count > 0)
            {
                throw new InvalidOperationException("Plugin runtime catalog is invalid.");
            }
            foreach (var command in plugin.Commands)
            {
                var fullName = $"{command.Namespace}.{command.Name}";
                if (!string.Equals(command.PluginId, plugin.Manifest.Id, StringComparison.Ordinal)
                    || !string.Equals(
                        command.Namespace,
                        plugin.Manifest.CommandNamespace,
                        StringComparison.Ordinal)
                    || !PluginValidation.IsStableId(command.Id)
                    || !commandIds.Add(command.Id)
                    || !commandNames.Add(fullName))
                {
                    throw new InvalidOperationException("Plugin command catalog is invalid.");
                }
            }
            foreach (var settings in plugin.Settings)
            {
                if (!string.Equals(settings.PluginId, plugin.Manifest.Id, StringComparison.Ordinal)
                    || !PluginValidation.IsStableId(settings.Id)
                    || !settingsIds.Add(settings.Id))
                {
                    throw new InvalidOperationException("Plugin settings catalog is invalid.");
                }
            }
        }
    }

    private static IEnumerable<string> ContributionIds(PluginDataSnapshot snapshot) =>
        snapshot.MiniCards.Select(card => card.Id)
            .Concat(snapshot.Details.Select(detail => detail.Id))
            .Concat(snapshot.Radar.Select(radar => radar.Id));

    private void PublishEvent(HostEvent hostEvent)
    {
        Subscription[] subscribers;
        lock (_sync) subscribers = _subscriptions.ToArray();
        foreach (var subscription in subscribers)
        {
            if (subscription.TryWrite(hostEvent)) continue;
            lock (_sync) _subscriptions.Remove(subscription);
            subscription.FailBackpressure();
        }
    }

    private HostEvent EventLocked(string type, string value) =>
        new(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            _revision,
            type,
            ObjectElement(("value", value)));

    private HostRevisions RevisionsLocked() =>
        new(
            _revision,
            _configRevision,
            _uiRevision,
            new Dictionary<string, long>(_dataRevisions, StringComparer.Ordinal));

    private void InvalidateCursorsLocked(string pluginId)
    {
        foreach (var key in _cursors
                     .Where(pair => pair.Value.PluginId == pluginId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cursors.Remove(key);
        }
    }

    private static IReadOnlyList<JsonElement> FlattenData(PluginDataSnapshot snapshot)
    {
        var items = new List<JsonElement>();
        foreach (var card in snapshot.MiniCards)
        {
            items.Add(JsonSerializer.SerializeToElement(card, PluginSdkJsonContext.Default.MiniCardContribution));
        }
        foreach (var detail in snapshot.Details)
        {
            items.Add(JsonSerializer.SerializeToElement(detail, PluginSdkJsonContext.Default.DetailContribution));
        }
        foreach (var radar in snapshot.Radar)
        {
            items.Add(JsonSerializer.SerializeToElement(radar, PluginSdkJsonContext.Default.RadarContribution));
        }
        return items;
    }

    private static PluginDataSnapshot EmptySnapshot(PluginManifest manifest, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            manifest.Id,
            now,
            new(
                enabled ? PluginHealthCode.Waiting : PluginHealthCode.Disabled,
                false,
                enabled,
                now,
                enabled ? "plugin.waiting" : "plugin.disabled"),
            [],
            [],
            []);
    }

    private static PluginDataSnapshot StartFailure(
        PluginManifest manifest,
        PluginHealthCode code)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            manifest.Id,
            now,
            new(code, false, false, now, "plugin.start_failed"),
            [],
            [],
            []);
    }

    public async ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Complete();
        if (_started)
        {
            foreach (var plugin in DependencyOrder().Reverse())
            {
                if (!_enabled[plugin.Manifest.Id]) continue;
                try { await plugin.StopAsync(CancellationToken.None); }
                catch { }
            }
        }
        foreach (var gate in _refreshGates.Values) gate.Dispose();
        _configGate.Dispose();
        foreach (var plugin in _plugins) await plugin.DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static ApiResponseEnvelope Failure(
        string requestId,
        string code,
        string message,
        bool retryable = false,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(1, requestId ?? string.Empty, false, null, new(code, message, retryable, details));

    private static bool ValidRequestId(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(character => character is >= '!' and <= '~');

    private static JsonElement ToElement<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    private static JsonElement ObjectElement(params (string Key, object Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case string text: writer.WriteStringValue(text); break;
                    case bool boolean: writer.WriteBooleanValue(boolean); break;
                    case int number: writer.WriteNumberValue(number); break;
                    case long number: writer.WriteNumberValue(number); break;
                    default: writer.WriteStringValue(value.ToString()); break;
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement Parameters(JsonElement? parameters)
    {
        if (parameters is not JsonElement value
            || value.ValueKind is not JsonValueKind.Object)
        {
            throw new HostCommandException("invalid_request", "params must be an object.");
        }
        return value;
    }

    private static void EnsureFields(JsonElement? parameters, IReadOnlyCollection<string> allowed)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return;
        var value = Parameters(parameters);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new HostCommandException("invalid_request", "params contains an unknown field.");
            }
        }
    }

    private static string RequiredString(
        JsonElement? parameters,
        string name,
        IReadOnlyCollection<string>? allowed = null)
    {
        if (allowed is not null) EnsureFields(parameters, allowed);
        var value = Parameters(parameters);
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new HostCommandException("invalid_request", $"{name} is required.");
        }
        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return null;
        var value = Parameters(parameters);
        return value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool RequiredBoolean(JsonElement? parameters, string name)
    {
        var value = Parameters(parameters);
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new HostCommandException("invalid_request", $"{name} is required.");
        }
        return property.GetBoolean();
    }

    private static bool? OptionalBoolean(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return null;
        var value = Parameters(parameters);
        return value.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;
    }

    private static long RequiredInt64(JsonElement? parameters, string name)
    {
        var value = Parameters(parameters);
        if (!value.TryGetProperty(name, out var property) || !property.TryGetInt64(out var number))
        {
            throw new HostCommandException("invalid_request", $"{name} is required.");
        }
        return number;
    }

    private static long? OptionalInt64(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return null;
        var value = Parameters(parameters);
        return value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static int? OptionalInt32(JsonElement? parameters, string name)
    {
        var value = parameters is null || parameters.Value.ValueKind is JsonValueKind.Null
            ? default
            : Parameters(parameters);
        return value.ValueKind is JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.TryGetInt32(out var number)
                ? number
                : null;
    }

    private static IReadOnlyList<string> StringArray(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return [];
        var value = Parameters(parameters);
        if (!value.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind is not JsonValueKind.Array)
        {
            throw new HostCommandException("invalid_request", $"{name} must be an array.");
        }
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String)
            {
                throw new HostCommandException("invalid_request", $"{name} must contain strings.");
            }
            result.Add(item.GetString() ?? string.Empty);
        }
        return result;
    }

    private static JsonElement? Child(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null) return null;
        var value = Parameters(parameters);
        return value.TryGetProperty(name, out var child) ? child.Clone() : null;
    }

    private sealed record CursorState(string PluginId, long DataRevision, int NextIndex);

    public sealed class Subscription
    {
        private readonly Channel<HostEvent> _channel = Channel.CreateBounded<HostEvent>(
            new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        internal Subscription(bool includeValues, SnapshotSummary initial)
        {
            IncludeValues = includeValues;
            Initial = initial;
        }

        public bool IncludeValues { get; }
        public SnapshotSummary Initial { get; }
        public ChannelReader<HostEvent> Events => _channel.Reader;
        internal bool TryWrite(HostEvent value) => _channel.Writer.TryWrite(value);
        internal void Complete() => _channel.Writer.TryComplete();
        internal void FailBackpressure() => _channel.Writer.TryComplete(
            new HostCommandException("event_backpressure", "Event subscriber fell behind.", true));
    }
}

public sealed class HostCommandException : Exception
{
    public HostCommandException(
        string code,
        string safeMessage,
        bool retryable = false,
        IReadOnlyDictionary<string, string>? details = null)
        : base(safeMessage)
    {
        Code = code;
        SafeMessage = safeMessage;
        Retryable = retryable;
        Details = details;
    }

    public string Code { get; }
    public string SafeMessage { get; }
    public bool Retryable { get; }
    public IReadOnlyDictionary<string, string>? Details { get; }

    public static HostCommandException Conflict(string domain, long currentRevision) =>
        new(
            "state_conflict",
            "State revision changed.",
            false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["domain"] = domain,
                ["currentRevision"] = currentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
