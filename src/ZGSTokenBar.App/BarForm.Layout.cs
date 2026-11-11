using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Mini-area layout: widths, collapse, order and snapshot layout.</summary>
internal sealed partial class BarForm
{
    internal void SetAllMiniAreasCollapsedFromCommand(bool collapsed)
    {
        SetAllMiniAreasCollapsed(collapsed, preserveAnchor: true);
    }

    internal bool SetMiniAreaFromCommand(string areaId, bool? collapsed, int? width)
    {
        if (!VisibleMiniAreaIds().Contains(areaId, StringComparer.Ordinal)) return false;
        return ApplyMiniAreaLayout(areaId, collapsed, width, preserveAnchor: true);
    }

    internal IReadOnlyList<string> GetReorderableMiniAreaIds() =>
        _visibleTaskbarAreas
            .Select(area => area.AreaId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal bool MoveMiniAreaFromCommand(string areaId, string? beforeAreaId) =>
        ApplyMiniAreaOrderMove(areaId, beforeAreaId, preserveAnchor: true);

    internal void SetMiniAreaOrder(IReadOnlyList<string> order, bool preserveAnchor = false)
    {
        var next = AppSettings.CopyMiniAreaOrder(order);
        if (_miniAreaOrder.SequenceEqual(next, StringComparer.Ordinal)) return;
        var anchor = ScreenLocation;
        _miniAreaOrder = next;
        ApplySnapshotLayout();
        if (preserveAnchor && !_renderOnly) PreserveMiniProviderAreaAnchor(anchor);
        Invalidate();
    }

    internal void SetMiniAreaLayouts(
        IReadOnlyDictionary<string, MiniAreaLayout> layouts,
        bool preserveAnchor = false)
    {
        var next = AppSettings.CopyMiniAreaLayouts(layouts);
        if (MiniAreaLayoutsEqual(_miniAreaLayouts, next)) return;
        var anchor = ScreenLocation;
        _miniAreaLayouts = next;
        ApplySnapshotLayout();
        if (preserveAnchor && !_renderOnly) PreserveMiniProviderAreaAnchor(anchor);
        Invalidate();
    }

    internal IReadOnlyList<MiniAreaState> GetMiniAreaStates() =>
        VisibleMiniAreas()
            .Select(area =>
            {
                var layout = AreaLayout(area.AreaId);
                return new MiniAreaState(
                    area.AreaId,
                    area.Title,
                    layout.Collapsed,
                    layout.Width ?? area.DefaultWidth,
                    TaskbarMiniLayoutMath.MinimumAreaContentWidthFor(area.AreaId),
                    TaskbarMiniLayoutMath.MaximumAreaContentWidth);
            })
            .ToArray();

    private void ToggleMiniAreaCollapsed(string areaId)
    {
        var previous = AreaLayout(areaId);
        if (!ApplyMiniAreaLayout(areaId, !previous.Collapsed, null, preserveAnchor: true)) return;
        MiniAreaLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetAllMiniAreasCollapsed(bool collapsed, bool preserveAnchor)
    {
        var areaIds = VisibleMiniAreaIds();
        if (areaIds.Count == 0 || areaIds.All(areaId => AreaLayout(areaId).Collapsed == collapsed)) return;
        var anchor = ScreenLocation;
        HidePopovers();
        foreach (var areaId in areaIds)
        {
            var current = AreaLayout(areaId);
            _miniAreaLayouts[areaId] = current with { Collapsed = collapsed };
        }
        ApplySnapshotLayout();
        if (preserveAnchor && !_renderOnly) PreserveMiniProviderAreaAnchor(anchor);
        Invalidate();
    }

    private bool ApplyMiniAreaLayout(
        string areaId,
        bool? collapsed,
        int? width,
        bool preserveAnchor,
        bool commitPlacement = true)
    {
        var previous = AreaLayout(areaId);
        var next = previous with
        {
            Collapsed = collapsed ?? previous.Collapsed,
            Width = width is { } requested
                ? TaskbarMiniLayoutMath.NormalizeAreaContentWidth(requested, areaId)
                : previous.Width,
        };
        if (next == previous) return false;
        var anchor = ScreenLocation;
        HidePopovers();
        _miniAreaLayouts[areaId] = next;
        ApplySnapshotLayout();
        if (preserveAnchor && !_renderOnly) PreserveMiniProviderAreaAnchor(anchor, commitPlacement);
        Invalidate();
        return true;
    }

    private bool ApplyMiniAreaOrderMove(string areaId, string? beforeAreaId, bool preserveAnchor)
    {
        var reorderable = GetReorderableMiniAreaIds();
        if (!reorderable.Contains(areaId, StringComparer.Ordinal)
            || (beforeAreaId is not null
                && !string.Equals(beforeAreaId, areaId, StringComparison.Ordinal)
                && !reorderable.Contains(beforeAreaId, StringComparer.Ordinal)))
        {
            return false;
        }
        if (string.Equals(areaId, beforeAreaId, StringComparison.Ordinal)) return false;

        var working = MiniAreaOrderWorkingList();
        working.RemoveAll(id => string.Equals(id, areaId, StringComparison.Ordinal));
        var insertion = beforeAreaId is null
            ? working.FindLastIndex(id => reorderable.Contains(id, StringComparer.Ordinal)) + 1
            : working.FindIndex(id => string.Equals(id, beforeAreaId, StringComparison.Ordinal));
        if (insertion < 0) return false;
        working.Insert(insertion, areaId);

        var reordered = OrderTaskbarContentAreas(_taskbarContentAreas, working)
            .Select(area => area.AreaId)
            .ToArray();
        if (reordered.SequenceEqual(_taskbarContentAreas.Select(area => area.AreaId), StringComparer.Ordinal)) return false;

        var anchor = ScreenLocation;
        HidePopovers();
        _miniAreaOrder = working.ToArray();
        ApplySnapshotLayout();
        if (preserveAnchor && !_renderOnly) PreserveMiniProviderAreaAnchor(anchor);
        Invalidate();
        return true;
    }

    private void PreserveMiniProviderAreaAnchor(Point anchor, bool commitPlacement = true)
    {
        if (!_taskbarDocked)
        {
            Location = anchor;
            ClampToVisibleScreen();
            if (commitPlacement && _placementCoordinator.CommitFloating(Bounds) is { } floatingCommit)
            {
                PlacementCommitted?.Invoke(this, floatingCommit);
            }
            return;
        }

        var topology = _placementCoordinator.ActiveTopology ?? _pendingTopology;
        if (topology is null) return;
        var profile = _placementCoordinator.ActiveProfile
            ?? _placementCoordinator.Preview(topology, Size);
        var preference = _placementCoordinator.DockedPreference(topology, profile);
        if (!TaskbarPlacement.TryConstrain(
                Size,
                anchor,
                preference.PreferredMonitorName,
                out var location,
                out var relativePosition,
                out var resolvedMonitor))
        {
            return;
        }

        _resolvedTaskbarMonitor = resolvedMonitor;
        if (!TaskbarPlacement.ShowAt(Handle, location, Size)) Location = location;
        if (commitPlacement
            && _placementCoordinator.CommitDocked(resolvedMonitor, relativePosition, Size) is { } dockedCommit)
        {
            PlacementCommitted?.Invoke(this, dockedCommit);
        }
    }

    private void ApplySnapshotLayout(bool updatePlacement = true)
    {
        _scale = Math.Max(1, EffectiveDpi() / 96f);
        var allCards = _snapshot.Cards
            .Where(card => _activeProviders.Contains(card.Provider))
            .ToArray();
        var layoutCards = _codexMiniDisplayMode == CodexMiniDisplayModes.Pool
            && _activeProviders.Contains(ProviderKind.Codex)
                ? CodexPoolCardProjection.Create(
                    allCards,
                    _codexAccounts,
                    _snapshot.CodexAccounts).ToArray()
                : allCards;
        var defaultAreas = TaskbarMiniGrouping.Create(layoutCards, _codexMiniDisplayMode)
            .Select(TaskbarMiniAreaContent.ForGroup)
            .Concat(_pluginMiniCards.Select(TaskbarMiniAreaContent.ForPlugin))
            .ToList();
        if (_showSystemMetrics)
        {
            defaultAreas.Add(TaskbarMiniAreaContent.ForSystem(_text.SystemUsageTitle));
            if (_systemUsage.OutputRateCapturedAt is not null)
                defaultAreas.Add(TaskbarMiniAreaContent.ForLiveMetrics(_text.IsChinese ? "实时速率" : "Live rates"));
        }
        _taskbarContentAreas = OrderTaskbarContentAreas(defaultAreas, _miniAreaOrder)
            .ToArray();
        _visibleTaskbarAreas = SelectVisibleTaskbarAreas(_taskbarContentAreas);
        _visibleCards = _visibleTaskbarAreas
            .Where(area => area.Group is not null)
            .SelectMany(area => area.Group!.Cards)
            .ToArray();
        _hiddenCardCount = Math.Max(0, layoutCards.Length - _visibleCards.Length)
            + Math.Max(0, _pluginMiniCards.Length - _visibleTaskbarAreas.Count(area => area.Plugin is not null));
        var miniLogicalWidth = _glassDock
            ? CurrentGlassPlan().Width
            : TaskbarMiniLayoutMath.ModuleContentWidth(
                _visibleTaskbarAreas.Select(TaskbarAreaWidth)
                    .ToArray(),
                _hiddenCardCount > 0);
        using (PerfTrace.Measure("layout.client-size")) ClientSize = new Size(Scale(miniLogicalWidth), Scale(TaskbarMiniLayoutMath.Height));
        using (PerfTrace.Measure("layout.region")) UpdateWindowRegion();
        if (!_renderOnly && updatePlacement)
        {
            using var placement = PerfTrace.Measure("layout.placement");
            if (_taskbarDocked)
            {
                if (_resizingMiniAreaId is null) SyncTaskbarPlacement();
            }
            else RestoreActiveFloatingPosition();
        }
    }

    private int TaskbarGroupWidth(TaskbarMiniCardGroup group)
    {
        var areaId = TaskbarGroupAreaId(group);
        var layout = AreaLayout(areaId);
        var defaultWidth = group.IsCodexPool
            ? TaskbarMiniLayoutMath.CodexPoolCardWidth
            : group.IsStackedCodex
                ? TaskbarMiniLayoutMath.CardWidth
                : TaskbarMiniLayoutMath.CardWidthFor(group.Cards[0]);
        return TaskbarMiniLayoutMath.AreaWidth(layout.Width ?? defaultWidth, layout.Collapsed)
            + CodexStrategyExtraWidth(areaId, layout.Collapsed);
    }

    private int PluginMiniCardWidth(PluginMiniCardView view)
    {
        var layout = AreaLayout(view.PluginId);
        var defaultWidth = view.PluginId == MiniAreaIds.Router ? RouterCardRenderer.Width : view.Card.Kind is ContributionKind.Balance or ContributionKind.Metric
            ? TaskbarMiniLayoutMath.ServiceCardWidth
            : TaskbarMiniLayoutMath.CardWidth;
        return TaskbarMiniLayoutMath.AreaWidth(layout.Width ?? defaultWidth, layout.Collapsed, view.PluginId);
    }

    private int TaskbarAreaWidth(TaskbarMiniAreaContent area)
    {
        if (area.Group is { } group) return TaskbarGroupWidth(group);
        if (area.Plugin is { } plugin) return PluginMiniCardWidth(plugin);
        var layout = AreaLayout(area.AreaId);
        return TaskbarMiniLayoutMath.AreaWidth(
            layout.Width ?? area.DefaultWidth,
            layout.Collapsed,
            area.AreaId);
    }

    private MiniAreaLayout AreaLayout(string areaId)
    {
        var normalized = _miniAreaLayouts.TryGetValue(areaId, out var layout)
            ? layout.Normalized(areaId)
            : new MiniAreaLayout(Collapsed: areaId is MiniAreaIds.Router or MiniAreaIds.LiveMetrics
                || ProviderCatalog.StartsCollapsed(areaId));
        return normalized;
    }

    private int AreaDefaultWidth(string areaId) =>
        VisibleMiniAreas()
            .FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal))
            ?.DefaultWidth
        ?? TaskbarMiniLayoutMath.CardWidth;

    private IReadOnlyList<string> VisibleMiniAreaIds() =>
        VisibleMiniAreas().Select(area => area.AreaId).ToArray();

    private IReadOnlyList<MiniAreaDefinition> VisibleMiniAreas()
    {
        return _visibleTaskbarAreas
            .Select(area => new MiniAreaDefinition(area.AreaId, area.Title, area.DefaultWidth))
            .ToArray();
    }

    private List<string> MiniAreaOrderWorkingList()
    {
        var order = AppSettings.CopyMiniAreaOrder(_miniAreaOrder).ToList();
        foreach (var areaId in _taskbarContentAreas.Select(area => area.AreaId))
        {
            if (!order.Contains(areaId, StringComparer.Ordinal)) order.Add(areaId);
        }
        return order;
    }

    private static IReadOnlyList<TaskbarMiniAreaContent> OrderTaskbarContentAreas(
        IReadOnlyList<TaskbarMiniAreaContent> areas,
        IReadOnlyList<string> order)
    {
        if (areas.Count <= 1 || order.Count == 0) return areas.ToArray();
        var remaining = areas.ToList();
        var ordered = new List<TaskbarMiniAreaContent>(areas.Count);
        foreach (var areaId in order)
        {
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(remaining[index].AreaId, areaId, StringComparison.Ordinal)) continue;
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
        }
        ordered.AddRange(remaining);
        return ordered;
    }

    private static TaskbarMiniAreaContent[] SelectVisibleTaskbarAreas(
        IReadOnlyList<TaskbarMiniAreaContent> areas)
    {
        var visibleIds = areas
            .Where(area => !string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal))
            .Take(TaskbarMiniLayoutMath.MaximumCards)
            .Select(area => area.AreaId)
            .ToHashSet(StringComparer.Ordinal);
        visibleIds.Add(MiniAreaIds.SystemMetrics);
        foreach (var providerId in ProviderCatalog.PersistentMiniIds) visibleIds.Add(providerId);
        // The dock window no longer sizes itself from this set. GlassDockLayout.Width
        // stays fixed while these areas remain available to the existing directory.
        return areas.Where(area => visibleIds.Contains(area.AreaId)).ToArray();
    }

    private static string TaskbarGroupAreaId(TaskbarMiniCardGroup group) => group.AreaId;

    internal static QuotaCard PrimaryTaskbarCard(TaskbarMiniCardGroup group) =>
        group.Cards.FirstOrDefault(card => card.Active) ?? group.Cards[0];

    private static RectangleF TaskbarAreaContentBounds(RectangleF areaBounds) =>
        RectangleF.FromLTRB(
            areaBounds.Left,
            areaBounds.Top,
            areaBounds.Right - TaskbarMiniLayoutMath.ProviderCollapseHandleWidth,
            areaBounds.Bottom);

    private static RectangleF TaskbarCollapseHandleBounds(RectangleF areaBounds) => new(
        areaBounds.Right - TaskbarMiniLayoutMath.ProviderCollapseHandleWidth,
        areaBounds.Top,
        TaskbarMiniLayoutMath.ProviderCollapseHandleWidth,
        areaBounds.Height / 2);

    private static RectangleF TaskbarReorderHandleBounds(RectangleF areaBounds) => new(
        areaBounds.Right - TaskbarMiniLayoutMath.ProviderCollapseHandleWidth,
        areaBounds.Top + areaBounds.Height / 2,
        TaskbarMiniLayoutMath.ProviderCollapseHandleWidth,
        areaBounds.Height / 2);

    private static RectangleF ResizeBounds(RectangleF areaBounds, bool collapsed) => collapsed
        ? RectangleF.Empty
        : new RectangleF(
            areaBounds.Right
                - TaskbarMiniLayoutMath.ProviderCollapseHandleWidth
                - TaskbarMiniLayoutMath.AreaResizeGripWidth,
            areaBounds.Top + 4,
            TaskbarMiniLayoutMath.AreaResizeGripWidth,
            areaBounds.Height - 8);

    private static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);
}
