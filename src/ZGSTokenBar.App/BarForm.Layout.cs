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
                out _,
                out var relativePosition,
                out var resolvedMonitor))
        {
            return;
        }

        _resolvedTaskbarMonitor = resolvedMonitor;
        if (commitPlacement
            && _placementCoordinator.CommitDocked(resolvedMonitor, relativePosition, Size) is { } dockedCommit)
        {
            PlacementCommitted?.Invoke(this, dockedCommit);
        }
        if (!_hiddenToTray && IsHandleCreated && TaskbarPlacement.TryGetTrack(Size, resolvedMonitor, out var track))
            ApplyDockedTarget(track, Size, track.Constrain(anchor), preserveAdjacentPosition: false);
    }

    private void ApplySnapshotLayout(bool updatePlacement = true)
    {
        _scale = Math.Max(1, EffectiveDpi() / 96f);
        var allCards = _snapshot.Cards
            .Where(card => _activeProviders.Contains(card.Provider))
            .ToArray();
        var defaultAreas = QuotaCardAreas(allCards)
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
        var miniLogicalWidth = CurrentGlassPlan().Width;
        var previousClientSize = ClientSize;
        var newSize = new Size(Scale(miniLogicalWidth), Scale(TaskbarMiniLayoutMath.Height));
        if (EmbeddedInTaskbar && (newSize.Width > previousClientSize.Width || newSize.Height > previousClientSize.Height))
            Hide(); // Expand only after checking a safe target for the new size.
        using (PerfTrace.Measure("layout.client-size")) ClientSize = newSize;
        var sizeChanged = ClientSize != previousClientSize;
        if (sizeChanged) _glassFrameSize = Size.Empty;
        using (PerfTrace.Measure("layout.region")) UpdateWindowRegion();
        if (!_renderOnly && (updatePlacement || sizeChanged))
        {
            using var placement = PerfTrace.Measure("layout.placement");
            if (_taskbarDocked)
            {
                SyncTaskbarPlacement();
            }
            else RestoreActiveFloatingPosition();
        }
    }

    /// <summary>
    /// Quota cards as named areas for the control API and dock directory: all Codex accounts share
    /// one area (Pro, then Plus, then others, each by remaining quota); every other provider has its
    /// own. The glass dock draws rings, not these areas.
    /// </summary>
    private static IEnumerable<TaskbarMiniAreaContent> QuotaCardAreas(IReadOnlyList<QuotaCard> cards)
    {
        var codex = OrderCodexCards(cards.Where(card => card.Provider == ProviderKind.Codex).ToArray());
        var codexAdded = false;
        foreach (var card in cards)
        {
            if (card.Provider == ProviderKind.Codex)
            {
                if (codexAdded) continue;
                codexAdded = true;
                yield return TaskbarMiniAreaContent.ForCards(MiniAreaIds.Codex, codex);
                continue;
            }
            var areaId = card.Provider switch
            {
                ProviderKind.Claude => MiniAreaIds.Claude,
                ProviderKind.AiGateway => MiniAreaIds.AiGateway,
                _ => card.Provider.ToString().ToLowerInvariant(),
            };
            yield return TaskbarMiniAreaContent.ForCards(areaId, [card]);
        }
    }

    private static QuotaCard[] OrderCodexCards(IReadOnlyList<QuotaCard> cards) => cards
        .Select((card, index) => (Card: card, Index: index, Remaining: DisplayedRemainingPercent(card)))
        .OrderBy(item => CodexPlanSortRank(item.Card))
        .ThenByDescending(item => item.Remaining ?? double.NegativeInfinity)
        .ThenBy(item => item.Index)
        .Select(item => item.Card)
        .ToArray();

    private static int CodexPlanSortRank(QuotaCard card)
    {
        var plan = CodexAccountFormatting.PlanLabel(card.Badge);
        if (string.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(plan, "plus", StringComparison.OrdinalIgnoreCase)) return 1;
        return card.IsService ? 3 : 2;
    }

    private static double? DisplayedRemainingPercent(QuotaCard card)
    {
        var window = TaskbarMiniLayoutMath.VisibleWindows(card.Windows).LastOrDefault();
        return window?.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    }

    private MiniAreaLayout AreaLayout(string areaId)
    {
        var normalized = _miniAreaLayouts.TryGetValue(areaId, out var layout)
            ? layout.Normalized(areaId)
            : new MiniAreaLayout(Collapsed: areaId is MiniAreaIds.Router or MiniAreaIds.Intake or MiniAreaIds.LiveMetrics
                || ProviderCatalog.StartsCollapsed(areaId));
        return normalized;
    }

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
        visibleIds.Add(MiniAreaIds.Intake);
        foreach (var providerId in ProviderCatalog.PersistentMiniIds) visibleIds.Add(providerId);
        // The dock window no longer sizes itself from this set. GlassDockLayout.Width
        // stays fixed while these areas remain available to the existing directory.
        return areas.Where(area => visibleIds.Contains(area.AreaId)).ToArray();
    }

    private static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);
}
