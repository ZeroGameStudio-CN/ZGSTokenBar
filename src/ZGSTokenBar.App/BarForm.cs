using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class RadarPreviewRequest(ProviderKind provider, string surfaceId) : EventArgs
{
    public ProviderKind Provider { get; } = provider;
    public string SurfaceId { get; } = surfaceId;
}

internal sealed class CodexEconomyModeRequest(CodexEconomyMode mode) : EventArgs
{
    public CodexEconomyMode Mode { get; } = mode;
}

internal sealed class BarForm : Form
{
    private const string SettingsIconGlyph = "\uE713";
    private const string LockIconGlyph = "\uE72E";
    private const float ControlIconSize = 13f;
    private const float TaskbarControlIconSize = 12f;
    private const float TaskbarControlWidth = TaskbarMiniLayoutMath.ControlsWidth;
    private const float TaskbarControlHeight = 18f;
    private const float ControlIconStrokeWidth = 1.55f;
    private const float SystemIconVerticalOffset = 1f;
    private const float MiniResetGlyphSize = 9f;
    private const float MiniResetClockSize = 11f;
    private const double SnapshotValueAnimationMs = 280;
    private const double SnapshotPulseAnimationMs = 560;
    private const float HoverAnimationStep = .32f;
    private static readonly HoverTarget[] AnimatedHoverTargets =
        [
            HoverTarget.Refresh,
            HoverTarget.MiniCollapse,
            HoverTarget.MiniReorder,
            HoverTarget.Settings,
            HoverTarget.CodexEconomy,
            HoverTarget.QuotaWindow,
            HoverTarget.SystemUsage,
            HoverTarget.CodexAccounts,
        ];
    private const int VkEscape = 0x001B;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int TaskbarPlacementMissThreshold = 3;
    private const int ShellSettleIntervalMs = 120;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private static readonly int TaskbarCreatedMessage =
        unchecked((int)RegisterWindowMessage("TaskbarCreated"));
    private readonly Font _titleFont = new("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _subtitleFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _cardTitleFont = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _badgeFont = new("Cascadia Mono", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _rowFont = new("Cascadia Mono", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _resetFont = new("Cascadia Mono", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _overflowFont = new("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _accountOrdinalFont = new("Cascadia Mono", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _systemIconFont = CreateSystemIconFont();
    private readonly Font _miniSystemIconFont = CreateSystemIconFont(MiniResetGlyphSize);
    private readonly Font _taskbarControlIconFont = CreateSystemIconFont(TaskbarControlIconSize);
    private readonly AlignedStringFormats _textFormats = new();
    private readonly StringFormat _systemIconFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };
    private readonly Image _claudeLogo = LoadEmbeddedImage("ZGSTokenBar.App.Assets.claude-icon-rounded.png");
    private readonly Image _openAiLogo = LoadEmbeddedImage("ZGSTokenBar.App.Assets.openai-official-ios-icon.png");
    private readonly Image _deepSeekLogo = LoadEmbeddedImage("ZGSTokenBar.App.Assets.deepseek-whale-icon.png");
    private readonly Image _resetClockIcon = LoadEmbeddedImage("ZGSTokenBar.App.Assets.fluent-clock-20-regular.png");
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _radarResetTimer = new();
    private readonly System.Windows.Forms.Timer _popoverHoverTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _popoverStateTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _shellSettleTimer = new() { Interval = ShellSettleIntervalMs };
    private readonly WinEventDelegate _foregroundChangedHandler;
    private readonly List<(RectangleF Bounds, QuotaCard Card)> _cardBounds = [];
    private readonly List<MiniQuotaTarget> _taskbarWindowBounds = [];
    private readonly List<MiniRadarTarget> _taskbarRadarBounds = [];
    private readonly List<(RectangleF Bounds, PluginMiniCardView Card)> _taskbarPluginBounds = [];
    private readonly List<(RectangleF Bounds, QuotaCard Card)> _taskbarCodexAccountBounds = [];
    private readonly List<MiniAreaTarget> _taskbarAreaBounds = [];
    private readonly HashSet<string> _taskbarRenderFailureAreaIds = new(StringComparer.Ordinal);
    private readonly Dictionary<HoverTarget, float> _hoverProgress = [];
    private Dictionary<string, double> _animationFrom = new(StringComparer.Ordinal);
    private HashSet<string> _animatedWindowKeys = new(StringComparer.Ordinal);
    private QuotaSnapshot _snapshot;
    private HashSet<ProviderKind> _activeProviders;
    private PluginMiniCardView[] _pluginMiniCards = [];
    private readonly Dictionary<string, Image> _pluginIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _pluginIconBytes = new(StringComparer.Ordinal);
    private Dictionary<string, MiniAreaLayout> _miniAreaLayouts;
    private string[] _miniAreaOrder;
    private string _codexMiniDisplayMode;
    private bool _showSystemMetrics;
    private bool _showCodexEconomyBar;
    private CodexEconomyStatus? _codexEconomyStatus;
    private CodexAccountInfo[] _codexAccounts;
    private QuotaCard[] _visibleCards = [];
    private TaskbarMiniAreaContent[] _taskbarContentAreas = [];
    private TaskbarMiniAreaContent[] _visibleTaskbarAreas = [];
    private int _hiddenCardCount;
    private RectangleF _refreshBounds;
    private RectangleF _settingsBounds;
    private RectangleF _systemUsageBounds;
    private RectangleF _codexEconomyBounds;
    private HoverTarget _hoverTarget;
    private HoverTarget _pressedTarget;
    private bool _refreshing;
    private bool _snapshotAnimating;
    private bool _taskbarDocked;
    private bool _animationsEnabled;
    private readonly bool _renderOnly;
    private readonly int? _renderDpi;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DisplayTopologySnapshot?> _captureTopology;
    private readonly Func<string, Exception?>? _renderFaultForAcceptance;
    private readonly object _topologyCaptureSync = new();
    private float _scale = 1;
    private float _refreshRotation;
    private DateTime _snapshotAnimationStarted;
    private DateTime _lastDragClickAt;
    private DateTime _suppressClickUntil;
    private Point _lastDragClickPosition;
    private Point _taskbarDragStartLocation;
    private Point _taskbarDragStartScreen;
    private Point _taskbarDragCurrentLocation;
    private readonly WindowPlacementCoordinator _placementCoordinator;
    private DisplayTopologySnapshot? _pendingTopology;
    private DisplayTopologySnapshot? _deferredTopologyDuringDrag;
    private string? _resolvedTaskbarMonitor;
    private string? _dragTopologyKey;
    private bool _topologyRefreshPending;
    private bool _topologyMayHaveChangedPending;
    private bool _topologyCaptureActive;
    private bool _topologyCaptureRequested;
    private bool _topologyCaptureRunning;
    private bool _topologyCaptureDisposed;
    private long _topologyCaptureGeneration;
    private bool _topologyChangedDuringDrag;
    private bool _taskbarDragging;
    private bool _taskbarDragMoved;
    private int _taskbarPlacementMisses;
    private bool _taskbarPlacementEstablished;
    private nint _foregroundChangedHook;
    private int _taskbarSyncQueued;
    private bool _popoverPinned;
    private bool _popoverMouseWasDown;
    private bool _popoverEscapeWasDown;
    private DateTime? _popoverLeaveStarted;
    private MiniQuotaTarget? _hoverQuotaTarget;
    private (RectangleF Bounds, QuotaCard Card)? _hoverCodexAccountTarget;
    private MiniRadarTarget? _hoverRadarTarget;
    private (RectangleF Bounds, PluginMiniCardView Card)? _hoverPluginTarget;
    private string? _hoverMiniAreaId;
    private string? _hoverReorderAreaId;
    private string? _hoverResizeAreaId;
    private string? _pressedMiniAreaId;
    private string? _resizingMiniAreaId;
    private int _resizeStartScreenX;
    private int _resizeStartWidth;
    private MiniAreaLayout? _resizeStartLayout;
    private bool _resizeMoved;
    private string? _reorderingMiniAreaId;
    private string? _reorderBeforeAreaId;
    private Point _reorderStartScreen;
    private bool _reorderMoved;
    private HoverTarget _hoverHintTarget;
    private bool _hoverSystemUsage;
    private string? _popoverTargetId;
    private QuotaPopoverForm? _quotaPopover;
    private CodexAccountsPopoverForm? _codexAccountsPopover;
    private TaskbarHintPopoverForm? _hintPopover;
    private ProviderRadarPopoverForm? _radarPopover;
    private SystemUsagePopoverForm? _systemUsagePopover;
    private ContextMenuStrip? _codexEconomyMenu;
    private RadarViewState _radarState = new(null, null, false, null);
    private HashSet<ProviderKind> _radarProviders;
    private IReadOnlyDictionary<string, QuotaPaceEstimate> _quotaPaceEstimates =
        new Dictionary<string, QuotaPaceEstimate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CodexQuotaTokenSummary> _codexQuotaTokenSummaries =
        new Dictionary<string, CodexQuotaTokenSummary>(StringComparer.Ordinal);
    private CodexTokenUsageSummary? _codexTokenUsage;
    private AiGatewayUsageSummary? _aiGatewayUsage;
    private SystemUsageSnapshot _systemUsage = new(
        null,
        null,
        null,
        null,
        null,
        null,
        0,
        Environment.ProcessorCount,
        DateTimeOffset.UtcNow);
    private NativeText _text;
    private QuotaBackgroundTheme _backgroundTheme;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<WindowPlacementCommit>? PlacementCommitted;
    public event EventHandler<RadarPreviewRequest>? RadarPreviewRequested;
    public event EventHandler? SystemUsageDetailsRequested;
    public event EventHandler? CodexEconomyStatusRefreshRequested;
    public event EventHandler<CodexEconomyModeRequest>? CodexEconomyModeRequested;
    public event EventHandler? MiniAreaLayoutChanged;
    public event EventHandler? MiniAreaOrderChanged;

    public BarForm(
        AppSettings settings,
        QuotaSnapshot snapshot,
        IEnumerable<ProviderKind>? radarProviders = null,
        bool renderOnly = false,
        int? renderDpi = null,
        IReadOnlyList<CodexAccountInfo>? codexAccounts = null,
        IReadOnlySet<ProviderKind>? activeProviders = null,
        Func<DisplayTopologySnapshot?>? topologyCapture = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, Exception?>? renderFaultForAcceptance = null)
    {
        _foregroundChangedHandler = OnForegroundChanged;
        _snapshot = snapshot;
        _activeProviders = activeProviders?.ToHashSet() ?? Enum.GetValues<ProviderKind>().ToHashSet();
        _codexAccounts = (codexAccounts ?? []).ToArray();
        _renderOnly = renderOnly;
        _renderDpi = renderDpi;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _captureTopology = topologyCapture ?? DisplayTopology.Capture;
        _renderFaultForAcceptance = renderOnly ? renderFaultForAcceptance : null;
        _text = NativeText.For(settings.Locale);
        _backgroundTheme = QuotaBackgroundPalette.Resolve(settings.BackgroundPalette);
        _placementCoordinator = new WindowPlacementCoordinator(settings);
        _taskbarDocked = settings.TaskbarDocked;
        _miniAreaLayouts = AppSettings.CopyMiniAreaLayouts(settings.MiniAreaLayouts);
        _miniAreaOrder = AppSettings.CopyMiniAreaOrder(settings.MiniAreaOrder);
        _codexMiniDisplayMode = CodexMiniDisplayModes.Normalize(settings.CodexMiniDisplayMode);
        _showSystemMetrics = settings.IsPluginEnabled("zgstokenbar.metrics.system", true);
        _showCodexEconomyBar = settings.EnableCodexEconomyBar;
        _animationsEnabled = settings.EnableAnimations && SystemInformation.IsMenuAnimationEnabled;
        _radarProviders = settings.EnableRadar
            ? new HashSet<ProviderKind>(radarProviders ?? [])
            : [];
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
        _radarResetTimer.Tick += (_, _) => RefreshRadarResetClock();
        _popoverHoverTimer.Tick += (_, _) => ShowHoveredPopover();
        _popoverStateTimer.Tick += (_, _) => MonitorPopover();
        _shellSettleTimer.Tick += (_, _) =>
        {
            _shellSettleTimer.Stop();
            HandleShellSettleTick();
        };
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _backgroundTheme.Outer;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "ZGSTokenBar";
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);

        ApplySnapshotLayout();
        if (!_renderOnly) RestorePosition(settings);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindowStyle | NoActivateStyle;
            return parameters;
        }
    }

    public bool IsTaskbarMode => true;
    public bool IsTaskbarDocked => _taskbarDocked;
    public bool IsRadarPopoverVisible => _radarPopover?.Visible == true;
    public string? VisibleRadarSurfaceId => IsRadarPopoverVisible
        ? _hoverRadarTarget?.SurfaceId
        : null;
    public bool AreAllMiniAreasCollapsed
    {
        get
        {
            var areaIds = VisibleMiniAreaIds();
            return areaIds.Count > 0 && areaIds.All(areaId => AreaLayout(areaId).Collapsed);
        }
    }
    public IReadOnlyDictionary<string, MiniAreaLayout> MiniAreaLayouts =>
        AppSettings.CopyMiniAreaLayouts(_miniAreaLayouts);
    public IReadOnlyList<string> MiniAreaOrder =>
        AppSettings.CopyMiniAreaOrder(_miniAreaOrder);
    public bool WantsSystemUsageDetails =>
        _showSystemMetrics && (_hoverSystemUsage || _systemUsagePopover?.Visible == true);
    internal bool IsRadarResetTimerEnabled => _radarResetTimer.Enabled;
    internal int RadarResetTimerInterval => _radarResetTimer.Interval;
    internal int CodexPoolAccountCountForAcceptance => _taskbarContentAreas
        .FirstOrDefault(area => area.Group?.IsCodexPool == true)
        ?.Group?.Cards.Count ?? 0;
    internal IReadOnlySet<string> TaskbarRenderFailureAreaIdsForAcceptance =>
        _taskbarRenderFailureAreaIds;
    internal DisplayTopologySnapshot? ActiveTopologyForAcceptance =>
        _placementCoordinator.ActiveTopology;
    internal bool TopologyCaptureRunningForAcceptance
    {
        get
        {
            lock (_topologyCaptureSync) return _topologyCaptureRunning;
        }
    }

    public void SetSnapshot(
        QuotaSnapshot snapshot,
        IReadOnlyDictionary<string, QuotaPaceEstimate>? quotaPaceEstimates = null)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        if (quotaPaceEstimates is not null) _quotaPaceEstimates = quotaPaceEstimates;
        StartSnapshotAnimation(previous, snapshot);
        ApplySnapshotLayout();
        RefreshQuotaPopover();
        RefreshCodexAccountsPopover();
        RefreshHintPopover();
        Invalidate();
    }

    public void SetActiveProviders(IReadOnlySet<ProviderKind> providers)
    {
        var next = providers.ToHashSet();
        if (next.SetEquals(_activeProviders)) return;
        _activeProviders = next;
        HidePopovers();
        ApplySnapshotLayout();
        Invalidate();
    }

    public void SetCodexAccounts(IReadOnlyList<CodexAccountInfo> accounts)
    {
        _codexAccounts = accounts.ToArray();
        if (_codexMiniDisplayMode == CodexMiniDisplayModes.Pool)
        {
            ApplySnapshotLayout();
            RefreshQuotaPopover();
        }
        RefreshCodexAccountsPopover();
        Invalidate();
    }
    internal ContextMenuStrip CreateCodexEconomyMenuForAcceptance()
    {
        if (!_renderOnly) throw new InvalidOperationException("Economy menu inspection is render-only.");
        return CreateCodexEconomyMenu();
    }

    internal (RectangleF Button, RectangleF Collapse, RectangleF Reorder, RectangleF Resize)
        GetCodexEconomyHitBoundsForAcceptance()
    {
        if (!_renderOnly) throw new InvalidOperationException("Economy hit testing is render-only.");
        var target = _taskbarAreaBounds.Single(item => string.Equals(
            item.AreaId,
            MiniAreaIds.CodexEconomy,
            StringComparison.Ordinal));
        return (_codexEconomyBounds, target.HandleBounds, target.ReorderBounds, target.ResizeBounds);
    }

    internal bool IsCodexEconomyButtonPointForAcceptance(PointF point)
    {
        if (!_renderOnly) throw new InvalidOperationException("Economy hit testing is render-only.");
        return CodexEconomyTargetAt(point);
    }

    public void SetCodexEconomyStatus(CodexEconomyStatus? status)
    {
        _codexEconomyStatus = status;
        Invalidate();
    }

    public void SetQuotaPaceEstimates(IReadOnlyDictionary<string, QuotaPaceEstimate> estimates)
    {
        _quotaPaceEstimates = estimates;
        RefreshQuotaPopover();
        Invalidate();
    }

    public void SetCodexQuotaTokenSummaries(
        IReadOnlyDictionary<string, CodexQuotaTokenSummary> summaries)
    {
        _codexQuotaTokenSummaries = summaries;
        RefreshQuotaPopover();
        Invalidate();
    }

    public void SetCodexTokenUsage(CodexTokenUsageSummary? summary)
    {
        _codexTokenUsage = summary;
        if (_radarPopover?.Visible == true
            && _hoverRadarTarget is { SourceProvider: ProviderKind.Codex, DeepSeekOnly: false } target)
        {
            if (summary is null && !_radarProviders.Contains(ProviderKind.Codex)) HideRadarPopover();
            else ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
        Invalidate();
    }

    public void SetPluginMiniCards(IReadOnlyList<PluginMiniCardView> cards)
    {
        var ordered = cards
            .OrderBy(card => card.Card.Order)
            .ThenBy(card => card.Card.Id, StringComparer.Ordinal)
            .ToArray();
        if (_pluginMiniCards.SequenceEqual(ordered)) return;

        var nextIcons = ordered
            .Where(card => card.IconPng is not null)
            .DistinctBy(card => card.PluginId)
            .ToDictionary(card => card.PluginId, card => card.IconPng!, StringComparer.Ordinal);
        foreach (var pluginId in _pluginIcons.Keys.Where(id => !nextIcons.ContainsKey(id)).ToArray())
        {
            _pluginIcons.Remove(pluginId, out var stale);
            stale?.Dispose();
            _pluginIconBytes.Remove(pluginId);
        }
        foreach (var card in nextIcons)
        {
            if (_pluginIconBytes.TryGetValue(card.Key, out var existing)
                && ReferenceEquals(existing, card.Value)) continue;
            try
            {
                using var stream = new MemoryStream(card.Value, writable: false);
                using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                var image = new Bitmap(source);
                if (_pluginIcons.Remove(card.Key, out var previous)) previous.Dispose();
                _pluginIcons[card.Key] = image;
                _pluginIconBytes[card.Key] = card.Value;
            }
            catch (ArgumentException)
            {
                // Invalid package images are rejected at install; retain the safe monogram fallback.
                if (_pluginIcons.Remove(card.Key, out var previous)) previous.Dispose();
                _pluginIconBytes.Remove(card.Key);
            }
        }
        _pluginMiniCards = ordered;
        ApplySnapshotLayout();
        Invalidate();
    }

    public void SetAiGatewayUsage(AiGatewayUsageSummary? summary)
    {
        _aiGatewayUsage = summary;
        if (_radarPopover?.Visible == true
            && _hoverRadarTarget is { SourceProvider: ProviderKind.AiGateway, DeepSeekOnly: true } target)
        {
            if (summary is null && !_radarProviders.Contains(ProviderKind.Codex)) HideRadarPopover();
            else ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
        Invalidate();
    }

    internal void SetAllMiniAreasCollapsedFromCommand(bool collapsed)
    {
        SetAllMiniAreasCollapsed(collapsed, preserveAnchor: true);
    }

    internal bool SetMiniAreaFromCommand(string areaId, bool? collapsed, int? width)
    {
        if (!VisibleMiniAreaIds().Contains(areaId, StringComparer.Ordinal)) return false;
        if (string.Equals(areaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
            && width is not null)
        {
            return false;
        }
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
        var anchor = Location;
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
        var anchor = Location;
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
                    string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
                        ? TaskbarMiniLayoutMath.CodexEconomyContentWidth
                        : TaskbarMiniLayoutMath.MaximumAreaContentWidth);
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
        var anchor = Location;
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
        var anchor = Location;
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

        var anchor = Location;
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

    public void SetSystemUsage(SystemUsageSnapshot snapshot)
    {
        _systemUsage = snapshot;
        if (_systemUsagePopover?.Visible == true)
        {
            _systemUsagePopover.UpdateContent(new SystemUsagePopoverContent(snapshot, _popoverPinned), _text);
        }
        Invalidate();
    }

    public void SetRefreshing(bool refreshing)
    {
        _refreshing = refreshing;
        if (refreshing && _animationsEnabled) EnsureAnimationTimer();
        else if (!refreshing)
        {
            _refreshRotation = 0;
            if (!_snapshotAnimating) _animationTimer.Stop();
        }
        RefreshHintPopover();
        Invalidate();
    }

    public void SetRadarState(RadarViewState state)
    {
        var resetAreaWasVisible = ShouldShowRadarResetArea();
        _radarState = state;
        var resetAreaIsVisible = ShouldShowRadarResetArea();
        if (resetAreaWasVisible != resetAreaIsVisible)
        {
            ApplySnapshotLayout();
        }
        if (!resetAreaIsVisible
            && string.Equals(_hoverRadarTarget?.Id, MiniAreaIds.RadarReset, StringComparison.Ordinal))
        {
            HideRadarPopover();
        }
        else if (_radarPopover?.Visible == true && _hoverRadarTarget is { } target)
        {
            ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
        UpdateRadarResetTimer();
        Invalidate();
    }

    public void SetRadarProviders(IEnumerable<ProviderKind> providers)
    {
        var resetAreaWasVisible = ShouldShowRadarResetArea();
        _radarProviders = new HashSet<ProviderKind>(providers);
        var resetAreaIsVisible = ShouldShowRadarResetArea();
        if (resetAreaWasVisible != resetAreaIsVisible) ApplySnapshotLayout();
        if (_radarProviders.Count == 0
            || (!resetAreaIsVisible
                && string.Equals(_hoverRadarTarget?.Id, MiniAreaIds.RadarReset, StringComparison.Ordinal)))
        {
            HideRadarPopover();
        }
        UpdateRadarResetTimer();
        Invalidate();
    }

    public void ApplySettings(AppSettings settings)
    {
        _text = NativeText.For(settings.Locale);
        _backgroundTheme = QuotaBackgroundPalette.Resolve(settings.BackgroundPalette);
        BackColor = _backgroundTheme.Outer;
        _quotaPopover?.ApplyTheme(_backgroundTheme);
        _systemUsagePopover?.ApplyTheme(_backgroundTheme);
        _hintPopover?.ApplyTheme(_backgroundTheme);
        var previousDocked = _taskbarDocked;
        var previousMiniAreaLayouts = _miniAreaLayouts;
        var previousMiniAreaOrder = _miniAreaOrder;
        var previousCodexMiniDisplayMode = _codexMiniDisplayMode;
        var previousShowSystemMetrics = _showSystemMetrics;
        var previousShowCodexEconomyBar = _showCodexEconomyBar;
        _miniAreaLayouts = AppSettings.CopyMiniAreaLayouts(settings.MiniAreaLayouts);
        _miniAreaOrder = AppSettings.CopyMiniAreaOrder(settings.MiniAreaOrder);
        _codexMiniDisplayMode = CodexMiniDisplayModes.Normalize(settings.CodexMiniDisplayMode);
        _showSystemMetrics = settings.IsPluginEnabled("zgstokenbar.metrics.system", true);
        _showCodexEconomyBar = settings.EnableCodexEconomyBar;
        _placementCoordinator.Reload(settings);
        WindowPlacementActivation? activation = null;
        if (_placementCoordinator.ActiveTopology is { } topology)
        {
            activation = _placementCoordinator.Activate(topology, Size);
            _taskbarDocked = activation.Profile.IsDocked;
        }
        else
        {
            _taskbarDocked = settings.TaskbarDocked;
        }
        var dockStateChanged = previousDocked != _taskbarDocked;
        if (dockStateChanged
            || !MiniAreaLayoutsEqual(previousMiniAreaLayouts, _miniAreaLayouts)
            || !previousMiniAreaOrder.SequenceEqual(_miniAreaOrder, StringComparer.Ordinal)
            || !string.Equals(previousCodexMiniDisplayMode, _codexMiniDisplayMode, StringComparison.Ordinal)
            || previousShowSystemMetrics != _showSystemMetrics
            || previousShowCodexEconomyBar != _showCodexEconomyBar)
        {
            HidePopovers();
            _codexEconomyMenu?.Close();
        }
        if (!settings.EnableRadar) SetRadarProviders([]);
        _animationsEnabled = settings.EnableAnimations && SystemInformation.IsMenuAnimationEnabled;
        if (!_animationsEnabled)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            _hoverProgress.Clear();
            _pressedTarget = HoverTarget.None;
            _animationTimer.Stop();
            _refreshRotation = 0;
        }
        ApplySnapshotLayout();
        RepositionPopovers();
        if (_taskbarDocked) SyncTaskbarPlacement();
        else
        {
            RestoreActiveFloatingPosition();
            if (!Visible) Show();
        }
        if (activation?.MigrationCommit is { } migration) PlacementCommitted?.Invoke(this, migration);
        RefreshHintPopover();
        Invalidate();
    }

    public void SyncTaskbarPlacement()
    {
        if (!_taskbarDocked || _taskbarDragging || _reorderingMiniAreaId is not null || IsDisposed || !IsHandleCreated)
        {
            if (!_taskbarDocked)
            {
                _taskbarPlacementMisses = 0;
                _taskbarPlacementEstablished = false;
            }
            return;
        }
        if (TaskbarPlacement.ShouldHideForFullscreen())
        {
            _taskbarPlacementMisses = 0;
            _taskbarPlacementEstablished = false;
            HidePopovers();
            if (Visible) Hide();
            return;
        }
        var topology = _placementCoordinator.ActiveTopology ?? _pendingTopology;
        if (topology is null) return;
        var profile = _placementCoordinator.ActiveProfile
            ?? _placementCoordinator.Preview(topology, Size);
        if (!profile.IsDocked) return;

        var placementSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        if (placementSize.Width <= 0 || placementSize.Height <= 0) placementSize = Size;
        var preference = _placementCoordinator.DockedPreference(topology, profile);
        var placementPosition = preference.RelativePosition;
        if (TaskbarPlacement.TryGetTarget(
                placementSize,
                placementPosition,
                preference.PreferredMonitorName,
                out var location,
                out var resolvedMonitor))
        {
            var resolvedPosition = _placementCoordinator.PositionForResolvedMonitor(
                topology,
                profile,
                resolvedMonitor,
                placementPosition);
            if (resolvedMonitor is { } monitor
                && resolvedPosition != placementPosition
                && TaskbarPlacement.TryGetTarget(
                    placementSize,
                    resolvedPosition,
                    monitor,
                    out var savedLocation,
                    out _))
            {
                location = savedLocation;
            }
            _taskbarPlacementMisses = 0;
            _taskbarPlacementEstablished = true;
            _resolvedTaskbarMonitor = resolvedMonitor;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            if (!TaskbarPlacement.ShowAt(Handle, location, placementSize)) Location = location;
            RepositionPopovers();
            return;
        }

        if (_taskbarPlacementEstablished
            && ++_taskbarPlacementMisses < TaskbarPlacementMissThreshold)
        {
            return;
        }
        _taskbarPlacementEstablished = false;
        HidePopovers();
        if (Visible) Hide();
    }

    public void ClampToVisibleScreen()
    {
        if (_taskbarDocked) return;
        var screen = Screen.FromRectangle(Bounds);
        var area = screen.WorkingArea;
        var x = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_renderOnly)
        {
            _foregroundChangedHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                0,
                _foregroundChangedHandler,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_foregroundChangedHook == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "ZGSTokenBar: foreground WinEvent hook unavailable; using placement timer fallback.");
            }
        }
        UpdateDpiAndRegion();
        if (!_renderOnly) RequestTopologyRefresh(topologyMayHaveChanged: false);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_foregroundChangedHook != 0)
        {
            _ = UnhookWinEvent(_foregroundChangedHook);
            _foregroundChangedHook = 0;
        }
        _shellSettleTimer.Stop();
        _topologyRefreshPending = false;
        _topologyMayHaveChangedPending = false;
        _pendingTopology = null;
        InvalidateTopologyCapture(handleActive: false);
        base.OnHandleDestroyed(e);
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (!_taskbarDocked || IsDisposed || !IsHandleCreated) return;
        if (Interlocked.Exchange(ref _taskbarSyncQueued, 1) != 0) return;
        try
        {
            _ = BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _taskbarSyncQueued, 0);
                StabilizeTaskbarPlacement();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _taskbarSyncQueued, 0);
            // The handle can be destroyed between the guard and BeginInvoke during shutdown.
        }
    }

    private void StabilizeTaskbarPlacement()
    {
        if (!_taskbarDocked || IsDisposed || !IsHandleCreated) return;
        SyncTaskbarPlacement();
        _shellSettleTimer.Stop();
        _shellSettleTimer.Start();
    }

    private void RequestTopologyRefresh(bool topologyMayHaveChanged)
    {
        if (_renderOnly || IsDisposed || !IsHandleCreated) return;
        _topologyRefreshPending = true;
        _topologyMayHaveChangedPending |= topologyMayHaveChanged;
        _pendingTopology = null;
        if (_taskbarDragging) _deferredTopologyDuringDrag = null;
        TaskbarPlacement.InvalidateCache();
        CaptureTopologyCandidate();
    }

    private void HandleShellSettleTick()
    {
        if (_topologyRefreshPending)
        {
            CaptureTopologyCandidate();
            return;
        }
        if (_taskbarDocked) SyncTaskbarPlacement();
    }

    private void CaptureTopologyCandidate()
    {
        if (!_topologyRefreshPending || IsDisposed || !IsHandleCreated) return;
        var startCapture = false;
        lock (_topologyCaptureSync)
        {
            if (_topologyCaptureDisposed) return;
            _topologyCaptureActive = true;
            _topologyCaptureRequested = true;
            _topologyCaptureGeneration++;
            if (!_topologyCaptureRunning)
            {
                _topologyCaptureRunning = true;
                startCapture = true;
            }
        }
        if (startCapture) _ = Task.Run(CaptureTopologyLoop);
    }

    private void CaptureTopologyLoop()
    {
        while (true)
        {
            long generation;
            lock (_topologyCaptureSync)
            {
                if (_topologyCaptureDisposed || !_topologyCaptureActive)
                {
                    _topologyCaptureRunning = false;
                    return;
                }
                generation = _topologyCaptureGeneration;
                _topologyCaptureRequested = false;
            }

            DisplayTopologySnapshot? captured = null;
            try
            {
                captured = _captureTopology();
            }
            catch
            {
                // A failed native topology probe is retried by the settle timer.
            }

            lock (_topologyCaptureSync)
            {
                if (_topologyCaptureDisposed || !_topologyCaptureActive)
                {
                    _topologyCaptureRunning = false;
                    return;
                }
                if (_topologyCaptureRequested) continue;
                _topologyCaptureRunning = false;
            }

            PostTopologyCandidate(generation, captured);
            return;
        }
    }

    private void PostTopologyCandidate(long generation, DisplayTopologySnapshot? captured)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            _ = BeginInvoke((Action)(() => CommitTopologyCandidate(generation, captured)));
        }
        catch (InvalidOperationException)
        {
            // The handle can be destroyed between the guard and BeginInvoke.
        }
    }

    private void CommitTopologyCandidate(long generation, DisplayTopologySnapshot? captured)
    {
        lock (_topologyCaptureSync)
        {
            if (_topologyCaptureDisposed
                || !_topologyCaptureActive
                || generation != _topologyCaptureGeneration)
            {
                return;
            }
        }
        if (!_topologyRefreshPending || IsDisposed || !IsHandleCreated) return;
        if (captured is null)
        {
            RestartShellSettleTimer();
            return;
        }

        if (_pendingTopology is not null
            && string.Equals(
                _pendingTopology.IdentitySignature,
                captured.IdentitySignature,
                StringComparison.Ordinal))
        {
            _topologyRefreshPending = false;
            _topologyMayHaveChangedPending = false;
            _pendingTopology = null;
            ActivateTopology(captured);
            return;
        }

        _pendingTopology = captured;
        if (_placementCoordinator.ActiveTopology is null)
        {
            var preview = _placementCoordinator.Preview(captured, Size);
            _taskbarDocked = preview.IsDocked;
            if (_taskbarDocked) SyncTaskbarPlacement();
            else MoveToFloatingPosition(captured, preview);
        }
        RestartShellSettleTimer();
    }

    private void InvalidateTopologyCapture(bool handleActive)
    {
        lock (_topologyCaptureSync)
        {
            _topologyCaptureActive = handleActive;
            _topologyCaptureRequested = false;
            _topologyCaptureGeneration++;
        }
    }

    private void ActivateTopology(DisplayTopologySnapshot topology)
    {
        if (_taskbarDragging)
        {
            _deferredTopologyDuringDrag = topology;
            if (!string.Equals(_dragTopologyKey, topology.Key, StringComparison.Ordinal))
            {
                _topologyChangedDuringDrag = true;
            }
            return;
        }

        var previousKey = _placementCoordinator.ActiveTopology?.Key;
        var activation = _placementCoordinator.Activate(topology, Size);
        var dockStateChanged = _taskbarDocked != activation.Profile.IsDocked;
        _taskbarDocked = activation.Profile.IsDocked;
        _resolvedTaskbarMonitor = null;
        if (dockStateChanged || !string.Equals(previousKey, topology.Key, StringComparison.Ordinal)) HidePopovers();
        if (_taskbarDocked) SyncTaskbarPlacement();
        else
        {
            MoveToFloatingPosition(topology, activation.Profile);
            if (!Visible) Show();
        }
        if (activation.MigrationCommit is { } migration) PlacementCommitted?.Invoke(this, migration);
    }

    private void RestoreActiveFloatingPosition()
    {
        if (_taskbarDragging
            || _placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile
            || profile.IsDocked)
        {
            return;
        }
        MoveToFloatingPosition(topology, profile);
    }

    private void MoveToFloatingPosition(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile)
    {
        var location = _placementCoordinator.FloatingLocation(topology, profile, Size);
        if (IsHandleCreated && !TaskbarPlacement.MoveAt(Handle, location, Size)) Location = location;
        else if (!IsHandleCreated) Location = location;
        RepositionPopovers();
    }

    private void RestartShellSettleTimer()
    {
        _shellSettleTimer.Stop();
        _shellSettleTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        HidePopovers();
        UpdateDpiAndRegion();
        ApplySnapshotLayout();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) HidePopovers();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);

        var logicalWidth = ClientSize.Width / _scale;
        var logicalHeight = ClientSize.Height / _scale;
        using var panelPath = RoundedRectangle(
            new RectangleF(0.5f, 0.5f, logicalWidth - 1, logicalHeight - 1),
            8);
        using var panelBrush = new SolidBrush(_backgroundTheme.Outer);
        using var panelBorder = new Pen(Color.FromArgb(56, 148, 163, 184), 1);
        graphics.FillPath(panelBrush, panelPath);
        graphics.DrawPath(panelBorder, panelPath);

        try
        {
            DrawTaskbarCards(graphics, logicalWidth);
            _taskbarRenderFailureAreaIds.Remove("bar");
        }
        catch (Exception exception) when (IsRecoverableRenderException(exception))
        {
            TraceTaskbarRenderFailure("bar", exception);
        }
    }

    private void DrawLabel(Graphics graphics)
    {
        using var primary = new SolidBrush(Color.FromArgb(248, 250, 252));
        using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
        DrawString(graphics, _text.Quota, _titleFont, primary, new RectangleF(6, 6, 78, 15), StringAlignment.Near);
        DrawString(
            graphics,
            _refreshing ? _text.Refreshing : _text.LiveLimits,
            _subtitleFont,
            muted,
            new RectangleF(6, 22, 78, 12),
            StringAlignment.Near);
    }

    private void DrawCards(Graphics graphics)
    {
        _cardBounds.Clear();
        var x = BarLayoutMath.OuterPadding + BarLayoutMath.LabelWidth + BarLayoutMath.SectionGap;
        foreach (var card in _visibleCards)
        {
            var bounds = new RectangleF(x, 4, BarLayoutMath.CardWidth, 34);
            DrawCard(graphics, bounds, card);
            _cardBounds.Add((bounds, card));
            x += BarLayoutMath.CardWidth + BarLayoutMath.CardGap;
        }

        if (_hiddenCardCount > 0)
        {
            var bounds = new RectangleF(x, 9, BarLayoutMath.OverflowWidth, 24);
            using var path = RoundedRectangle(bounds, 6);
            using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
            using var border = new Pen(Color.FromArgb(42, 148, 163, 184));
            using var text = new SolidBrush(Color.FromArgb(148, 163, 184));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            DrawString(graphics, $"+{_hiddenCardCount}", _overflowFont, text, bounds, StringAlignment.Center);
        }
    }

    private void DrawTaskbarCards(Graphics graphics, float logicalWidth)
    {
        _cardBounds.Clear();
        _taskbarWindowBounds.Clear();
        _taskbarRadarBounds.Clear();
        _taskbarPluginBounds.Clear();
        _taskbarCodexAccountBounds.Clear();
        _taskbarAreaBounds.Clear();
        _codexEconomyBounds = RectangleF.Empty;
        var x = (float)TaskbarMiniLayoutMath.OuterPadding;
        foreach (var area in _visibleTaskbarAreas)
        {
            var layout = AreaLayout(area.AreaId);
            var areaWidth = TaskbarAreaWidth(area);
            var areaBounds = new RectangleF(x, 4, areaWidth, 36);
            var handleBounds = TaskbarCollapseHandleBounds(areaBounds);
            var bounds = TaskbarAreaContentBounds(areaBounds);
            var target = new MiniAreaTarget(
                area.AreaId,
                area.Title,
                areaBounds,
                handleBounds,
                TaskbarReorderHandleBounds(areaBounds),
                string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
                    ? RectangleF.Empty
                    : ResizeBounds(areaBounds, layout.Collapsed),
                layout.Collapsed,
                Reorderable: true);
            _taskbarAreaBounds.Add(target);
            DrawTaskbarAreaSafely(graphics, area, layout, target, bounds);
            x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
        }

        if (_hiddenCardCount > 0)
        {
            var overflowBounds = new RectangleF(x, 10, TaskbarMiniLayoutMath.OverflowWidth, 24);
            using var path = RoundedRectangle(overflowBounds, 6);
            using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
            using var border = new Pen(Color.FromArgb(42, 148, 163, 184));
            using var text = new SolidBrush(Color.FromArgb(148, 163, 184));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            DrawString(graphics, $"+{_hiddenCardCount}", _overflowFont, text, overflowBounds, StringAlignment.Center);
        }

        DrawMiniReorderInsertionMarker(graphics);
        var controlsX = logicalWidth
            - TaskbarMiniLayoutMath.OuterPadding
            - TaskbarMiniLayoutMath.ControlsWidth;
        _refreshBounds = new RectangleF(controlsX, 4, TaskbarControlWidth, TaskbarControlHeight);
        _settingsBounds = new RectangleF(
            controlsX,
            _refreshBounds.Bottom,
            TaskbarControlWidth,
            TaskbarControlHeight);
        DrawTaskbarControlGroup(graphics);
        DrawRefreshIcon(
            graphics,
            PressedIconBounds(_refreshBounds, HoverTarget.Refresh),
            _refreshing,
            _refreshRotation,
            HoverProgress(HoverTarget.Refresh),
            TaskbarControlIconSize);
        DrawSettingsIcon(
            graphics,
            PressedIconBounds(_settingsBounds, HoverTarget.Settings),
            HoverProgress(HoverTarget.Settings),
            taskbarCompact: true);
    }

    private void DrawTaskbarAreaSafely(
        Graphics graphics,
        TaskbarMiniAreaContent area,
        MiniAreaLayout layout,
        MiniAreaTarget target,
        RectangleF bounds)
    {
        var hitTargetCounts = CaptureTaskbarHitTargetCounts();
        var graphicsState = graphics.Save();
        var graphicsStateRestored = false;
        try
        {
            if (_renderFaultForAcceptance?.Invoke(area.AreaId) is { } fault) throw fault;
            DrawTaskbarModuleShell(graphics, target);
            if (area.Group is { } group)
            {
                var primaryCard = PrimaryTaskbarCard(group);
                if (layout.Collapsed)
                {
                    if (group.IsCodexPool)
                    {
                        DrawCollapsedCodexPool(graphics, bounds, group.Cards);
                    }
                    else if (primaryCard.Provider == ProviderKind.AiGateway)
                    {
                        DrawCollapsedAiGatewayCard(graphics, bounds, primaryCard);
                    }
                    else
                    {
                        DrawCollapsedQuotaCard(graphics, bounds, primaryCard);
                    }
                    _cardBounds.Add((bounds, primaryCard));
                }
                else if (group.IsCodexPool)
                {
                    DrawTaskbarCodexPool(graphics, bounds, group.Cards);
                    _cardBounds.Add((bounds, primaryCard));
                }
                else if (group.IsStackedCodex)
                {
                    DrawTaskbarCodexGroup(graphics, bounds, group.Cards);
                    for (var index = 0; index < group.Cards.Count; index++)
                    {
                        var rowBounds = TaskbarAccountRowBounds(bounds, index, group.Cards.Count);
                        _cardBounds.Add((rowBounds, group.Cards[index]));
                        _taskbarCodexAccountBounds.Add((
                            TaskbarAccountOrdinalRowBounds(bounds, index, group.Cards.Count),
                            group.Cards[index]));
                    }
                }
                else
                {
                    DrawTaskbarCard(graphics, bounds, group.Cards[0]);
                    _cardBounds.Add((bounds, group.Cards[0]));
                }
            }
            else if (area.Plugin is { } pluginCard)
            {
                DrawPluginMiniCard(graphics, bounds, pluginCard);
                _taskbarPluginBounds.Add((bounds, pluginCard));
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.RadarReset, StringComparison.Ordinal))
            {
                DrawRadarResetCard(graphics, bounds, layout.Collapsed);
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal))
            {
                _systemUsageBounds = bounds;
                DrawSystemUsageCard(graphics, bounds, layout.Collapsed);
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal))
            {
                _codexEconomyBounds = bounds;
                DrawCodexEconomyCard(graphics, bounds, layout.Collapsed);
            }
            DrawTaskbarCollapseHandle(graphics, target);
            DrawTaskbarReorderGrip(graphics, target);
            DrawTaskbarResizeGrip(graphics, target);
            _taskbarRenderFailureAreaIds.Remove(area.AreaId);
        }
        catch (Exception exception) when (IsRecoverableRenderException(exception))
        {
            graphicsStateRestored = true;
            graphics.Restore(graphicsState);
            RestoreTaskbarHitTargetCounts(hitTargetCounts);
            if (string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal))
            {
                _systemUsageBounds = RectangleF.Empty;
            }
            else if (string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal))
            {
                _codexEconomyBounds = RectangleF.Empty;
            }
            TraceTaskbarRenderFailure(area.AreaId, exception);
            try
            {
                DrawTaskbarModuleFailure(graphics, target);
                DrawTaskbarCollapseHandle(graphics, target);
                DrawTaskbarReorderGrip(graphics, target);
                DrawTaskbarResizeGrip(graphics, target);
            }
            catch (Exception fallbackException) when (IsRecoverableRenderException(fallbackException))
            {
                TraceTaskbarRenderFailure($"{area.AreaId}:fallback", fallbackException);
            }
        }
        finally
        {
            if (!graphicsStateRestored) graphics.Restore(graphicsState);
        }
    }

    private void DrawRadarResetCard(Graphics graphics, RectangleF bounds, bool collapsed)
    {
        var now = _utcNow();
        var window = CurrentRadarResetWindow();
        var timing = RadarResetTiming.Resolve(window);
        var stale = _radarState.Error is not null || _radarState.IsStale(now);
        var statusColor = stale
            ? Color.FromArgb(148, 163, 184)
            : timing.Kind switch
            {
                RadarResetTimingKind.Exact when timing.ExactTargetAt <= now => Color.FromArgb(251, 191, 36),
                RadarResetTimingKind.Exact => Color.FromArgb(52, 211, 153),
                RadarResetTimingKind.EstimatedDate => Color.FromArgb(251, 191, 36),
                _ => Color.FromArgb(148, 163, 184),
            };

        using var path = RoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.ProviderGroup,
            statusColor,
            stale ? .04f : .08f));
        using var border = new Pen(Color.FromArgb(stale ? 62 : 104, statusColor), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var iconBounds = collapsed
            ? new RectangleF(bounds.X + (bounds.Width - 16) / 2, bounds.Y + 3, 16, 16)
            : new RectangleF(bounds.X + 5, bounds.Y + 8, 20, 20);
        graphics.DrawImage(ProviderLogo(ProviderKind.Codex), iconBounds);
        if (_radarState.HasUnreadFor(RadarSurfaceIds.Codex))
        {
            DrawRadarUnreadDot(graphics, iconBounds);
        }

        if (collapsed)
        {
            using var compactBrush = new SolidBrush(statusColor);
            DrawString(
                graphics,
                _text.RadarResetMiniCompact(window, now),
                _badgeFont,
                compactBrush,
                new RectangleF(bounds.X + 2, bounds.Bottom - 13, bounds.Width - 4, 10),
                StringAlignment.Center);
        }
        else
        {
            using var titleBrush = new SolidBrush(stale
                ? Color.FromArgb(148, 163, 184)
                : Color.FromArgb(203, 213, 225));
            using var valueBrush = new SolidBrush(statusColor);
            DrawString(
                graphics,
                _text.RadarResetMiniTitle(window, now),
                _subtitleFont,
                titleBrush,
                new RectangleF(bounds.X + 29, bounds.Y + 5, bounds.Width - 34, 12),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.RadarResetMiniValue(window, now),
                _badgeFont,
                valueBrush,
                new RectangleF(bounds.X + 29, bounds.Y + 18, bounds.Width - 34, 11),
                StringAlignment.Near);
        }

        _taskbarRadarBounds.Add(new MiniRadarTarget(
            bounds,
            MiniAreaIds.RadarReset,
            ProviderKind.Codex,
            ProviderKind.Codex,
            false,
            RadarSurfaceIds.Codex));
    }

    private void DrawPluginMiniCard(
        Graphics graphics,
        RectangleF bounds,
        PluginMiniCardView view)
    {
        DrawTaskbarCardShell(graphics, bounds);
        var iconBounds = new RectangleF(bounds.X + 5, bounds.Y + 6, 24, 24);
        if (_pluginIcons.TryGetValue(view.PluginId, out var image))
        {
            graphics.DrawImage(image, iconBounds);
        }
        else
        {
        using var iconFill = new SolidBrush(view.Card.Kind switch
        {
            ContributionKind.Balance => Color.FromArgb(42, 157, 143),
            ContributionKind.Metric => Color.FromArgb(37, 99, 235),
            _ => Color.FromArgb(124, 58, 237),
        });
        using var iconText = new SolidBrush(Color.White);
        graphics.FillEllipse(iconFill, iconBounds);
        DrawString(
            graphics,
            PluginMonogram(view.Title),
            _badgeFont,
            iconText,
            iconBounds,
            StringAlignment.Center);
        }
        if (AreaLayout(view.PluginId).Collapsed) return;

        using var title = new SolidBrush(Color.FromArgb(203, 213, 225));
        using var value = new SolidBrush(Color.FromArgb(248, 250, 252));
        DrawString(
            graphics,
            view.Title,
            _subtitleFont,
            title,
            new RectangleF(bounds.X + 34, bounds.Y + 5, bounds.Width - 39, 12),
            StringAlignment.Near);
        var summary = view.Card.Summary.FirstOrDefault();
        DrawString(
            graphics,
            summary is null ? "—" : PluginValue(summary.Value),
            _cardTitleFont,
            value,
            new RectangleF(bounds.X + 34, bounds.Y + 17, bounds.Width - 39, 14),
            StringAlignment.Near);
    }

    private void DrawSystemUsageCard(Graphics graphics, RectangleF bounds, bool collapsed)
    {
        DrawSystemUsageFrame(graphics, bounds);
        if (collapsed) DrawCollapsedSystemUsage(graphics, bounds);
        else DrawSystemUsageRows(graphics, bounds);
    }

    private void DrawCollapsedSystemUsage(Graphics graphics, RectangleF bounds)
    {
        var trackWidth = Math.Min(20f, bounds.Width - 12);
        var trackX = bounds.Left + (bounds.Width - trackWidth) / 2;
        using var trackBrush = new SolidBrush(Color.FromArgb(44, 100, 116, 139));
        for (var index = 0; index < 4; index++)
        {
            var track = new RectangleF(trackX, bounds.Top + 8 + index * 5.5f, trackWidth, 2.5f);
            graphics.FillRoundedRectangle(trackBrush, track, 1.25f);
            var value = index switch
            {
                0 => _systemUsage.CpuPercent,
                1 => _systemUsage.MemoryPercent,
                2 => _systemUsage.DiskActivePercent,
                _ => _systemUsage.GpuPercent,
            };
            if (value is not { } usage || usage <= 0) continue;
            using var valueBrush = new SolidBrush(Color.FromArgb(130, UsageColor(usage)));
            graphics.FillRoundedRectangle(
                valueBrush,
                new RectangleF(
                    track.X,
                    track.Y,
                    Math.Max(1.5f, track.Width * (float)Math.Clamp(usage / 100, 0, 1)),
                    track.Height),
                1.25f);
        }
    }

    private void DrawSystemUsageFrame(Graphics graphics, RectangleF bounds)
    {
        var hover = HoverProgress(HoverTarget.SystemUsage);
        using var path = RoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(26, 42, 66),
            hover * .45f));
        using var border = new Pen(Color.FromArgb(
            (int)Math.Round(42 + hover * 68),
            100,
            116,
            139));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawSystemUsageRows(Graphics graphics, RectangleF bounds)
    {
        DrawSystemUsageRow(graphics, bounds, 0, "CPU", _systemUsage.CpuPercent);
        DrawSystemUsageRow(graphics, bounds, 1, "RAM", _systemUsage.MemoryPercent);
        DrawSystemUsageRow(graphics, bounds, 2, "I/O", _systemUsage.DiskActivePercent);
        DrawSystemUsageRow(graphics, bounds, 3, "GPU", _systemUsage.GpuPercent);
    }

    private void DrawSystemUsageRow(
        Graphics graphics,
        RectangleF cardBounds,
        int index,
        string label,
        double? percent)
    {
        var y = cardBounds.Y + 3 + index * 8.2f;
        var rowHeight = 6.5f;
        var track = new RectangleF(cardBounds.X + 25, y, cardBounds.Width - 29, rowHeight);
        using var trackBrush = new SolidBrush(Color.FromArgb(38, 100, 116, 139));
        graphics.FillRoundedRectangle(trackBrush, track, 3);
        if (percent is { } usage && usage > 0)
        {
            var color = UsageColor(usage);
            using var usageBrush = new SolidBrush(Color.FromArgb(82, color));
            graphics.FillRoundedRectangle(
                usageBrush,
                new RectangleF(
                    track.X,
                    track.Y,
                    Math.Max(1.5f, track.Width * (float)Math.Clamp(usage / 100, 0, 1)),
                    track.Height),
                3);
        }

        using var labelBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(percent is null
            ? Color.FromArgb(100, 116, 139)
            : Color.FromArgb(226, 232, 240));
        DrawString(
            graphics,
            label,
            _accountOrdinalFont,
            labelBrush,
            new RectangleF(cardBounds.X + 3, y, 20, rowHeight),
            StringAlignment.Near);
        DrawString(
            graphics,
            CompactUsage(percent),
            _accountOrdinalFont,
            valueBrush,
            new RectangleF(track.X + 2, track.Y - .25f, track.Width - 4, rowHeight + .5f),
            StringAlignment.Far);
    }

    private void DrawTaskbarCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        DrawTaskbarCardShell(graphics, bounds);
        DrawTaskbarProviderLogo(graphics, bounds, card);

        var targets = TaskbarQuotaTargets(card, bounds);
        if (targets.Length == 3)
        {
            DrawTaskbarCapsule(graphics, targets[0]);
            DrawTaskbarDualCapsule(
                graphics,
                TaskbarCapsuleBounds(bounds, 2, 1),
                targets[1],
                targets[2]);
        }
        else
        {
            foreach (var target in targets)
            {
                DrawTaskbarCapsule(graphics, target);
            }
        }
        _taskbarWindowBounds.AddRange(targets);
    }

    private void DrawCollapsedAiGatewayCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        var balance = card.Balance;
        var color = balance is null
            ? Color.FromArgb(100, 116, 139)
            : AiGatewayCompactStatusColor(balance.Status);
        DrawCollapsedProviderSummary(
            graphics,
            bounds,
            card,
            AiGatewayBalanceFormatting.CompactAmount(balance?.TotalBalance),
            color);
    }

    private void DrawCollapsedQuotaCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        var summary = CollapsedQuotaSummary(card);
        DrawCollapsedProviderSummary(graphics, bounds, card, summary.Value, summary.Color);
    }

    private void DrawCollapsedCodexPool(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        var weekly = CodexPoolPresentation.Create(cards, DateTimeOffset.UtcNow)[0];
        var remaining = weekly.AggregateRemainingPercent;
        var value = CompactRemaining(remaining);
        var color = remaining is not null
            ? QuotaColorScale.ForRemaining(remaining.Value)
            : Color.FromArgb(100, 116, 139);
        var primaryCard = cards.FirstOrDefault(card => card.Active) ?? cards[0];
        DrawCollapsedProviderSummary(graphics, bounds, primaryCard, value, color);
    }

    private void DrawCollapsedProviderSummary(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        string value,
        Color color)
    {
        DrawTaskbarCardShell(graphics, bounds);
        var logoBounds = new RectangleF(bounds.X + 3, bounds.Y + 3, 15, 15);
        DrawTaskbarProviderLogoAt(graphics, logoBounds, card, showOrdinal: false);

        using var statusBrush = new SolidBrush(color);
        graphics.FillEllipse(statusBrush, bounds.Right - 7, bounds.Top + 4, 4, 4);

        using var valueBrush = new SolidBrush(color);
        DrawString(
            graphics,
            value,
            _badgeFont,
            valueBrush,
            new RectangleF(bounds.X + 2, bounds.Bottom - 12, bounds.Width - 4, 9),
            StringAlignment.Center);
    }

    internal static (string Value, Color Color) CollapsedQuotaSummary(QuotaCard card)
    {
        var window = CollapsedQuotaWindow(card);
        var remaining = window?.UsedPercent is { } used
            ? Math.Clamp(100 - used, 0, 100)
            : (double?)null;
        return (
            CompactRemaining(remaining),
            remaining is null
                ? Color.FromArgb(100, 116, 139)
                : QuotaColorScale.ForRemaining(remaining.Value));
    }

    internal static QuotaWindow? CollapsedQuotaWindow(QuotaCard card)
    {
        var windows = card.Provider == ProviderKind.Codex
            ? TaskbarMiniGrouping.CodexRowWindows(card)
            : TaskbarMiniLayoutMath.VisibleWindows(card.Windows);
        return windows
            .Where(window => window.UsedPercent is not null)
            .OrderBy(window => window.Duration > TimeSpan.Zero ? window.Duration : TimeSpan.MaxValue)
            .FirstOrDefault()
            ?? windows.FirstOrDefault();
    }

    private static Color AiGatewayCompactStatusColor(AiGatewayBalanceStatus? status) => status switch
    {
        AiGatewayBalanceStatus.Available => Color.FromArgb(52, 211, 153),
        AiGatewayBalanceStatus.Stale => Color.FromArgb(251, 191, 36),
        AiGatewayBalanceStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(100, 116, 139),
    };

    private static Color Sub2ApiPoolCompactStatusColor(Sub2ApiPoolStatus status) => status switch
    {
        Sub2ApiPoolStatus.Available => Color.FromArgb(52, 211, 153),
        Sub2ApiPoolStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiPoolStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(100, 116, 139),
    };

    private static Color Sub2ApiUsageCompactStatusColor(Sub2ApiUsageStatus status) => status switch
    {
        Sub2ApiUsageStatus.Available => Color.FromArgb(52, 211, 153),
        Sub2ApiUsageStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiUsageStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(100, 116, 139),
    };

    private static Color Sub2ApiQuotaCompactStatusColor(Sub2ApiQuotaStatus status) => status switch
    {
        Sub2ApiQuotaStatus.Available => Color.FromArgb(52, 211, 153),
        Sub2ApiQuotaStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiQuotaStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(100, 116, 139),
    };

    private void DrawTaskbarCodexPool(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        var now = DateTimeOffset.UtcNow;
        DrawTaskbarCardShell(graphics, bounds);
        var logoBounds = DrawTaskbarProviderLogo(graphics, bounds, cards[0], showOrdinal: false);
        DrawAccountOrdinalBadge(
            graphics,
            new RectangleF(logoBounds.Right - 7, logoBounds.Bottom - 6, 10, 8),
            cards.Count.ToString());

        foreach (var targetRow in TaskbarCodexPoolTargetRows(bounds, cards, now))
        {
            DrawTaskbarCodexPoolRow(graphics, targetRow);
            _taskbarWindowBounds.AddRange(targetRow.Targets);
        }
    }

    private void DrawTaskbarCodexPoolRow(
        Graphics graphics,
        CodexPoolTargetRow targetRow)
    {
        using var rowPath = RoundedRectangle(targetRow.Bounds, 4);
        using var rowFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var rowBorder = new Pen(Color.FromArgb(58, 51, 65, 85), 1);
        graphics.FillPath(rowFill, rowPath);
        graphics.DrawPath(rowBorder, rowPath);

        using var labelBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        DrawString(
            graphics,
            targetRow.Row.Label,
            _rowFont,
            labelBrush,
            new RectangleF(targetRow.Bounds.X + 2, targetRow.Bounds.Y, 15, targetRow.Bounds.Height),
            StringAlignment.Near);

        for (var index = 0; index < targetRow.Targets.Count; index++)
        {
            var target = targetRow.Targets[index];
            var segment = targetRow.Row.Segments[index];
            var railHeight = targetRow.Bounds.Height > 20 ? 8f : 5f;
            var railBounds = new RectangleF(
                target.Bounds.X,
                target.Bounds.Y + (target.Bounds.Height - railHeight) / 2,
                target.Bounds.Width,
                railHeight);
            using var trackBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
            graphics.FillRoundedRectangle(trackBrush, railBounds, 2);

            double? remaining = null;
            if (segment.RemainingPercent is not null)
            {
                var used = AnimatedUsed(segment.Card.Key, target.Window);
                remaining = used is null
                    ? segment.RemainingPercent
                    : Math.Clamp(100 - used.Value, 0, 100);
            }
            if (remaining is { } segmentRemaining && segmentRemaining > 0)
            {
                var color = MixColor(
                    QuotaColorScale.ForRemaining(segmentRemaining),
                    Color.FromArgb(248, 250, 252),
                    SnapshotPulse(target.Id) * .22f);
                using var activeBrush = new SolidBrush(color);
                graphics.FillRoundedRectangle(
                    activeBrush,
                    new RectangleF(
                        railBounds.X,
                        railBounds.Y,
                        railBounds.Width * (float)(segmentRemaining / 100),
                        railBounds.Height),
                    2);
            }

            var emphasis = MiniTargetEmphasis(target.Id);
            if (emphasis > 0)
            {
                using var hoverPen = new Pen(Color.FromArgb(
                    (int)Math.Round(70 + 120 * emphasis),
                    226,
                    232,
                    240), 1);
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(railBounds, .5f, .5f), 2);
                graphics.DrawPath(hoverPen, hoverPath);
            }
        }

        var aggregate = targetRow.Row.AggregateRemainingPercent;
        var aggregateColor = aggregate is { } remainingPercent
            ? QuotaColorScale.ForRemaining(remainingPercent)
            : Color.FromArgb(100, 116, 139);
        DrawTaskbarCodexPoolCapacitySummary(graphics, targetRow, aggregateColor);
    }

    private void DrawTaskbarCodexPoolCapacitySummary(
        Graphics graphics,
        CodexPoolTargetRow targetRow,
        Color remainingColor)
    {
        var summary = CodexPoolPresentation.CapacitySummary(targetRow.Row);
        var separatorIndex = summary.IndexOf('/');
        if (separatorIndex <= 0 || targetRow.Row.RemainingAccountEquivalents is null)
        {
            using var fallbackBrush = new SolidBrush(remainingColor);
            DrawString(
                graphics,
                summary,
                _rowFont,
                fallbackBrush,
                targetRow.ValueBounds,
                StringAlignment.Far);
            return;
        }

        var remainingText = summary[..separatorIndex];
        var capacityText = summary[separatorIndex..];
        var capacityWidth = Math.Min(
            targetRow.ValueBounds.Width,
            MeasureWidth(graphics, capacityText, _rowFont) + 1);
        var remainingBounds = new RectangleF(
            targetRow.ValueBounds.X,
            targetRow.ValueBounds.Y,
            Math.Max(1, targetRow.ValueBounds.Width - capacityWidth),
            targetRow.ValueBounds.Height);

        using var remainingBrush = new SolidBrush(remainingColor);
        using var capacityBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        DrawString(
            graphics,
            remainingText,
            _rowFont,
            remainingBrush,
            remainingBounds,
            StringAlignment.Far);
        DrawString(
            graphics,
            capacityText,
            _rowFont,
            capacityBrush,
            targetRow.ValueBounds,
            StringAlignment.Far);
    }

    private static CodexPoolTargetRow[] TaskbarCodexPoolTargetRows(
        RectangleF cardBounds,
        IReadOnlyList<QuotaCard> cards,
        DateTimeOffset now)
    {
        var rows = CodexPoolPresentation.Create(cards, now);
        var singleRow = rows.Count == 1;
        return rows
            .Select((row, rowIndex) =>
            {
                var rowHeight = singleRow ? 32f : 15f;
                var bounds = new RectangleF(
                    cardBounds.X + 33,
                    cardBounds.Y + 2 + (singleRow ? 0 : rowIndex * 17),
                    cardBounds.Width - 37,
                    rowHeight);
                var valueBounds = new RectangleF(bounds.Right - 50, bounds.Y, 48, bounds.Height);
                var trackLeft = bounds.Left + 17;
                var trackRight = valueBounds.Left - 2;
                var trackWidth = Math.Max(1, trackRight - trackLeft);
                var gap = row.Segments.Count > 1
                    && trackWidth >= row.Segments.Count * 2 - 1
                        ? 1f
                        : 0f;
                var totalGap = gap * Math.Max(0, row.Segments.Count - 1);
                var segmentWidth = row.Segments.Count == 0
                    ? 0
                    : Math.Max(0, (trackWidth - totalGap) / row.Segments.Count);
                var targets = row.Segments
                    .Select((segment, segmentIndex) =>
                    {
                        var window = segment.Window
                            ?? new QuotaWindow(row.Label, null, null, row.Duration);
                        return new MiniQuotaTarget(
                            new RectangleF(
                                trackLeft + segmentIndex * (segmentWidth + gap),
                                bounds.Y,
                                segmentWidth,
                                bounds.Height),
                            segment.Card,
                            window);
                    })
                    .ToArray();
                return new CodexPoolTargetRow(row, bounds, valueBounds, targets);
            })
            .ToArray();
    }

    private void DrawTaskbarCodexGroup(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        DrawTaskbarCardShell(graphics, bounds);
        DrawTaskbarProviderLogo(graphics, bounds, cards[0], showOrdinal: false);
        DrawTaskbarAccountOrdinalColumn(graphics, bounds, cards);
        for (var index = 0; index < cards.Count; index++)
        {
            var rowBounds = TaskbarCodexCapsuleBounds(bounds, index, cards.Count);
            if (cards.Count > 2)
            {
                var target = TaskbarCompactCodexRowTarget(cards[index], rowBounds);
                DrawTaskbarCompactCodexCapsule(graphics, target);
                _taskbarWindowBounds.Add(target);
                continue;
            }

            var targets = TaskbarCodexRowTargets(cards[index], rowBounds);
            if (targets.Length == 1)
            {
                DrawTaskbarCapsule(graphics, targets[0]);
            }
            else
            {
                DrawTaskbarDualCapsule(graphics, rowBounds, targets[0], targets[1]);
            }
            _taskbarWindowBounds.AddRange(targets);
        }
    }

    private void DrawTaskbarAccountOrdinalColumn(
        Graphics graphics,
        RectangleF cardBounds,
        IReadOnlyList<QuotaCard> cards)
    {
        if (cards.Count >= 4)
        {
            for (var index = 0; index < cards.Count; index++)
            {
                var cell = TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count);
                var style = TaskbarAccountPlanStyle(cards[index]);
                using var cellPath = RoundedRectangle(cell, Math.Min(3, cell.Height / 3));
                using var gridFill = new SolidBrush(style?.Fill ?? _backgroundTheme.Outer);
                using var gridBorder = new Pen(style?.Border ?? Color.FromArgb(71, 85, 105), 1);
                using var gridText = new SolidBrush(style?.Text ?? Color.FromArgb(153, 246, 228));
                graphics.FillPath(gridFill, cellPath);
                graphics.DrawPath(gridBorder, cellPath);
                DrawString(
                    graphics,
                    CodexAccountOrdinal(cards[index]) ?? (index + 1).ToString(),
                    _accountOrdinalFont,
                    gridText,
                    cell,
                    StringAlignment.Center);
            }
            return;
        }

        var bounds = new RectangleF(cardBounds.X + 31, cardBounds.Y + 2, 10, 32);
        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(_backgroundTheme.Outer);
        using var border = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var separator = new Pen(Color.FromArgb(48, 71, 85, 105), 1);
        graphics.FillPath(fill, path);
        var clipState = graphics.Save();
        graphics.SetClip(path);
        for (var index = 0; index < cards.Count; index++)
        {
            var style = TaskbarAccountPlanStyle(cards[index]);
            if (style is null) continue;
            using var rowFill = new SolidBrush(style.Value.Fill);
            graphics.FillRectangle(
                rowFill,
                TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count));
        }
        graphics.Restore(clipState);
        graphics.DrawPath(border, path);
        for (var index = 1; index < cards.Count; index++)
        {
            var separatorY = bounds.Top + bounds.Height * index / cards.Count;
            graphics.DrawLine(separator, bounds.Left + 2, separatorY, bounds.Right - 2, separatorY);
        }

        for (var index = 0; index < cards.Count; index++)
        {
            var row = TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count);
            var style = TaskbarAccountPlanStyle(cards[index]);
            using var text = new SolidBrush(style?.Text ?? Color.FromArgb(153, 246, 228));
            DrawString(
                graphics,
                CodexAccountOrdinal(cards[index]) ?? (index + 1).ToString(),
                _accountOrdinalFont,
                text,
                row,
                StringAlignment.Center);
        }
    }

    private static PlanBadgeStyle? TaskbarAccountPlanStyle(QuotaCard card)
    {
        var label = PlanBadgePresentation.Label(card.Badge);
        return PlanBadgePresentation.TryGetStyle(label, out var style) ? style : null;
    }

    private void DrawTaskbarCardShell(Graphics graphics, RectangleF bounds)
    {
        using var path = RoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(_backgroundTheme.ProviderGroup);
        using var border = new Pen(Color.FromArgb(38, 51, 65, 85), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarModuleShell(Graphics graphics, MiniAreaTarget target)
    {
        using var path = RoundedRectangle(target.Bounds, 7);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.Outer,
            _backgroundTheme.QuotaGroup,
            .72f));
        using var border = new Pen(Color.FromArgb(96, 100, 116, 139), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarModuleFailure(Graphics graphics, MiniAreaTarget target)
    {
        using var path = RoundedRectangle(target.Bounds, 7);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.Outer,
            Color.FromArgb(127, 29, 29),
            .28f));
        using var border = new Pen(Color.FromArgb(128, 251, 113, 133), 1);
        using var mark = new Pen(Color.FromArgb(220, 251, 113, 133), 1.5f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        var centerX = target.Bounds.Left + target.Bounds.Width / 2;
        var centerY = target.Bounds.Top + target.Bounds.Height / 2;
        graphics.DrawLine(mark, centerX - 3, centerY - 3, centerX + 3, centerY + 3);
        graphics.DrawLine(mark, centerX + 3, centerY - 3, centerX - 3, centerY + 3);
    }

    private TaskbarHitTargetCounts CaptureTaskbarHitTargetCounts() => new(
        _cardBounds.Count,
        _taskbarWindowBounds.Count,
        _taskbarRadarBounds.Count,
        _taskbarPluginBounds.Count,
        _taskbarCodexAccountBounds.Count);

    private void RestoreTaskbarHitTargetCounts(TaskbarHitTargetCounts counts)
    {
        TrimToCount(_cardBounds, counts.Cards);
        TrimToCount(_taskbarWindowBounds, counts.Windows);
        TrimToCount(_taskbarRadarBounds, counts.Radar);
        TrimToCount(_taskbarPluginBounds, counts.Plugins);
        TrimToCount(_taskbarCodexAccountBounds, counts.CodexAccounts);
    }

    private void TraceTaskbarRenderFailure(string areaId, Exception exception)
    {
        if (!_taskbarRenderFailureAreaIds.Add(areaId)) return;
        System.Diagnostics.Trace.TraceWarning(
            "Taskbar render fault isolated in {0}: {1}",
            areaId,
            exception.GetType().Name);
    }

    private static bool IsRecoverableRenderException(Exception exception) =>
        exception is not OutOfMemoryException
        and not AccessViolationException
        and not StackOverflowException;

    private static void TrimToCount<T>(List<T> items, int count)
    {
        if (items.Count > count) items.RemoveRange(count, items.Count - count);
    }

    private void DrawCodexEconomyCard(Graphics graphics, RectangleF bounds, bool collapsed)
    {
        var mode = _codexEconomyStatus?.Mode ?? CodexEconomyMode.Unconfigured;
        var color = mode switch
        {
            CodexEconomyMode.On => Color.FromArgb(52, 211, 153),
            CodexEconomyMode.Ask => Color.FromArgb(251, 191, 36),
            CodexEconomyMode.Inconsistent => Color.FromArgb(251, 113, 133),
            _ => Color.FromArgb(148, 163, 184),
        };
        var hover = HoverProgress(HoverTarget.CodexEconomy);
        var pressed = _pressedTarget == HoverTarget.CodexEconomy;
        var buttonBounds = RectangleF.Inflate(bounds, -1, -1);
        var fillColor = MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(37, 55, 82),
            hover * .72f);
        if (pressed) fillColor = MixColor(fillColor, Color.FromArgb(49, 46, 129), .55f);
        using var path = RoundedRectangle(buttonBounds, 6);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(MixColor(
            Color.FromArgb(64, 100, 116, 139),
            Color.FromArgb(132, 147, 197, 253),
            hover));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        using var accent = new SolidBrush(color);
        using var primary = new SolidBrush(Color.FromArgb(226, 232, 240));
        var label = collapsed
            ? EconomyCompactLabel(mode)
            : _text.CodexEconomyModeName(mode);
        var labelWidth = Math.Min(
            Math.Max(1, bounds.Width - 18),
            MeasureWidth(graphics, label, _badgeFont) + 4);
        var contentWidth = 6 + 4 + labelWidth;
        var contentLeft = bounds.Left + Math.Max(4, (bounds.Width - contentWidth) / 2);
        graphics.FillEllipse(accent, contentLeft, bounds.Top + (bounds.Height - 6) / 2, 6, 6);
        DrawString(
            graphics,
            label,
            _badgeFont,
            primary,
            new RectangleF(contentLeft + 10, bounds.Top, labelWidth, bounds.Height),
            StringAlignment.Center);
    }

    private static string EconomyCompactLabel(CodexEconomyMode mode) => mode switch
    {
        CodexEconomyMode.Off => "O",
        CodexEconomyMode.Ask => "A",
        CodexEconomyMode.On => "N",
        CodexEconomyMode.Inconsistent => "!",
        _ => "?",
    };

    private RectangleF DrawTaskbarProviderLogo(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        bool showOrdinal = true)
    {
        var logoBounds = new RectangleF(bounds.X + 5, bounds.Y + 6, 24, 24);
        return DrawTaskbarProviderLogoAt(graphics, logoBounds, card, showOrdinal);
    }

    private RectangleF DrawTaskbarProviderLogoAt(
        Graphics graphics,
        RectangleF logoBounds,
        QuotaCard card,
        bool showOrdinal)
    {
        if (showOrdinal) DrawProviderLogo(graphics, logoBounds, card);
        else DrawProviderLogo(graphics, logoBounds, card, false);
        var radarEnabled = _radarProviders.Contains(card.Provider);
        var deepSeekRadarEnabled = card.Provider == ProviderKind.AiGateway
            && _radarProviders.Contains(ProviderKind.Codex);
        if (deepSeekRadarEnabled)
        {
            _taskbarRadarBounds.Add(new MiniRadarTarget(
                logoBounds,
                WindowKey(card.Key, "__deepseek-radar"),
                ProviderKind.AiGateway,
                ProviderKind.Codex,
                true,
                RadarSurfaceIds.DeepSeek));
            if (_radarState.HasUnreadFor(RadarSurfaceIds.DeepSeek)
                && _radarState.Snapshot?.Provider == ProviderKind.Codex)
            {
                DrawRadarUnreadDot(graphics, logoBounds);
            }
        }
        else if (HasProviderOverview(card.Provider, radarEnabled, _codexTokenUsage))
        {
            _taskbarRadarBounds.Add(new MiniRadarTarget(
                logoBounds,
                card.Key,
                card.Provider,
                card.Provider,
                false,
                RadarSurfaceIds.Codex));
            if (radarEnabled
                && _radarState.HasUnreadFor(RadarSurfaceIds.Codex)
                && _radarState.Snapshot?.Provider == card.Provider)
            {
                DrawRadarUnreadDot(graphics, logoBounds);
            }
        }
        return logoBounds;
    }

    internal static bool HasProviderOverview(
        ProviderKind provider,
        bool radarEnabled,
        CodexTokenUsageSummary? codexTokenUsage) =>
        radarEnabled || provider == ProviderKind.Codex && codexTokenUsage is not null;

    private static RectangleF TaskbarCapsuleBounds(RectangleF cardBounds, int windowCount, int index)
    {
        var capsuleX = cardBounds.X + 33;
        var capsuleWidth = cardBounds.Width - 37;
        var capsuleY = windowCount == 1 ? cardBounds.Y + 10.5f : cardBounds.Y + 2;
        return new RectangleF(capsuleX, capsuleY + index * 17, capsuleWidth, 15);
    }

    private static RectangleF TaskbarCodexCapsuleBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount <= 2)
        {
            return new RectangleF(
                cardBounds.X + 44,
                cardBounds.Y + 2 + index * 17,
                cardBounds.Width - 48,
                15);
        }

        if (accountCount >= 4)
        {
            var tile = TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
            return RectangleF.FromLTRB(tile.Left + 12, tile.Top, tile.Right, tile.Bottom);
        }

        var slotHeight = 32f / accountCount;
        return new RectangleF(
            cardBounds.X + 44,
            cardBounds.Y + 2 + index * slotHeight,
            cardBounds.Width - 48,
            Math.Max(1, slotHeight - 1));
    }

    private static RectangleF TaskbarAccountRowBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount >= 4)
        {
            return TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
        }

        var rowHeight = cardBounds.Height / accountCount;
        return new RectangleF(
            cardBounds.X,
            cardBounds.Y + index * rowHeight,
            cardBounds.Width,
            rowHeight);
    }

    private static RectangleF TaskbarAccountOrdinalRowBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount <= 2)
        {
            return new RectangleF(cardBounds.X + 31, cardBounds.Y + 2 + index * 17, 10, 15);
        }

        if (accountCount >= 4)
        {
            var tile = TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
            return new RectangleF(tile.X, tile.Y, 10, tile.Height);
        }

        var slotHeight = 32f / accountCount;
        return new RectangleF(
            cardBounds.X + 31,
            cardBounds.Y + 2 + index * slotHeight,
            10,
            Math.Max(1, slotHeight - 1));
    }

    private static RectangleF TaskbarCompactCodexTileBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        var rowCount = (accountCount + 1) / 2;
        var column = index % 2;
        var row = index / 2;
        const float gap = 2;
        var contentWidth = cardBounds.Width - 35;
        var tileWidth = (contentWidth - gap) / 2;
        var tileHeight = (32f - (rowCount - 1) * gap) / rowCount;
        return new RectangleF(
            cardBounds.X + 31 + column * (tileWidth + gap),
            cardBounds.Y + 2 + row * (tileHeight + gap),
            tileWidth,
            Math.Max(1, tileHeight));
    }

    private static MiniQuotaTarget TaskbarCompactCodexRowTarget(
        QuotaCard card,
        RectangleF rowBounds)
    {
        var windows = TaskbarMiniGrouping.CodexRowWindows(card);
        return new MiniQuotaTarget(rowBounds, card, windows[^1]);
    }

    private static MiniQuotaTarget[] TaskbarCodexRowTargets(
        QuotaCard card,
        RectangleF rowBounds)
    {
        var windows = TaskbarMiniGrouping.CodexRowWindows(card);
        if (windows.Count == 1)
        {
            return [new MiniQuotaTarget(rowBounds, card, windows[0])];
        }

        var midpoint = rowBounds.X + rowBounds.Width / 2;
        return
        [
            new MiniQuotaTarget(
                RectangleF.FromLTRB(rowBounds.Left, rowBounds.Top, midpoint, rowBounds.Bottom),
                card,
                windows[0]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(midpoint, rowBounds.Top, rowBounds.Right, rowBounds.Bottom),
                card,
                windows[1]),
        ];
    }

    private void DrawTaskbarCompactCodexCapsule(Graphics graphics, MiniQuotaTarget target)
    {
        var used = AnimatedUsed(target.Card.Key, target.Window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? Color.FromArgb(100, 116, 139)
            : QuotaColorScale.ForRemaining(remaining.Value);
        valueColor = MixColor(
            valueColor,
            Color.FromArgb(248, 250, 252),
            SnapshotPulse(target.Id) * .22f);

        using var path = RoundedRectangle(target.Bounds, Math.Min(3, target.Bounds.Height / 3));
        using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var border = new Pen(Color.FromArgb(58, 51, 65, 85), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var railStart = target.Bounds.X + 4;
        var railWidth = Math.Max(1, target.Bounds.Width - 8);
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            target.Bounds.Bottom - 1.5f,
            remaining,
            valueColor,
            null);

        if (target.Bounds.Height < 9) return;
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _accountOrdinalFont,
            valueBrush,
            new RectangleF(
                target.Bounds.X + 5,
                target.Bounds.Y - .5f,
                target.Bounds.Width - 10,
                target.Bounds.Height - 1),
            StringAlignment.Center);
    }

    private static MiniQuotaTarget[] TaskbarQuotaTargets(
        QuotaCard card,
        RectangleF cardBounds)
    {
        var windows = TaskbarMiniLayoutMath.VisibleWindows(card.Windows);
        if (windows.Count != 3)
        {
            return windows
                .Select((window, index) => new MiniQuotaTarget(
                    TaskbarCapsuleBounds(cardBounds, windows.Count, index),
                    card,
                    window))
                .ToArray();
        }

        var firstBounds = TaskbarCapsuleBounds(cardBounds, 2, 0);
        var sharedBounds = TaskbarCapsuleBounds(cardBounds, 2, 1);
        var midpoint = sharedBounds.X + sharedBounds.Width / 2;
        return
        [
            new MiniQuotaTarget(firstBounds, card, windows[0]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(sharedBounds.Left, sharedBounds.Top, midpoint, sharedBounds.Bottom),
                card,
                windows[1]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(midpoint, sharedBounds.Top, sharedBounds.Right, sharedBounds.Bottom),
                card,
                windows[2]),
        ];
    }

    private void DrawTaskbarDualCapsule(
        Graphics graphics,
        RectangleF bounds,
        MiniQuotaTarget first,
        MiniQuotaTarget second)
    {
        var firstHover = MiniTargetEmphasis(first.Id);
        var secondHover = MiniTargetEmphasis(second.Id);
        var firstPulse = SnapshotPulse(first.Id);
        var secondPulse = SnapshotPulse(second.Id);

        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
        graphics.FillPath(fill, path);

        var state = graphics.Save();
        graphics.SetClip(path);
        DrawTaskbarDualHover(graphics, first.Bounds, firstHover);
        DrawTaskbarDualHover(graphics, second.Bounds, secondHover);
        graphics.Restore(state);

        using var border = new Pen(MixColor(
            Color.FromArgb(58, 51, 65, 85),
            Color.FromArgb(154, 226, 232, 240),
            Math.Max(Math.Max(firstHover, secondHover) * .45f, Math.Max(firstPulse, secondPulse) * .55f)), 1);
        using var separator = new Pen(Color.FromArgb(58, 71, 85, 105), 1);
        graphics.DrawPath(border, path);
        graphics.DrawLine(separator, first.Bounds.Right, bounds.Top + 3, first.Bounds.Right, bounds.Bottom - 3);

        DrawTaskbarDualMetric(graphics, first);
        DrawTaskbarDualMetric(graphics, second);
    }

    private float MiniTargetEmphasis(string id)
    {
        if (string.Equals(_popoverTargetId, id, StringComparison.Ordinal)) return 1;
        return string.Equals(_hoverQuotaTarget?.Id, id, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.QuotaWindow)
            : 0;
    }

    private static void DrawTaskbarDualHover(Graphics graphics, RectangleF bounds, float emphasis)
    {
        if (emphasis <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(
            (int)Math.Round(32 * emphasis),
            148,
            163,
            184));
        graphics.FillRectangle(brush, bounds);
    }

    private void DrawTaskbarDualMetric(Graphics graphics, MiniQuotaTarget target)
    {
        if (target.Card.IsService)
        {
            DrawTaskbarServiceMetric(graphics, target);
            return;
        }

        var used = AnimatedUsed(target.Card.Key, target.Window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? Color.FromArgb(100, 116, 139)
            : MixColor(
                QuotaColorScale.ForRemaining(remaining.Value),
                Color.FromArgb(248, 250, 252),
                SnapshotPulse(target.Id) * .22f);
        var label = QuotaDisplayFormatting.FormatWindowTiny(target.Window);
        var labelWidth = label.Length > 1 ? 18f : 12f;
        using var labelBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            label,
            _badgeFont,
            labelBrush,
            new RectangleF(target.Bounds.X + 4, target.Bounds.Y, labelWidth, target.Bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(
                target.Bounds.X + labelWidth,
                target.Bounds.Y,
                target.Bounds.Width - labelWidth - 3,
                target.Bounds.Height),
            StringAlignment.Far);

        var railStart = target.Bounds.X + 4;
        var railWidth = target.Bounds.Width - 8;
        var railY = target.Bounds.Bottom - 2;
        var now = DateTimeOffset.UtcNow;
        double? budgetMarkerRemaining = null;
        if (QuotaDisplayFormatting.WeeklyBlockReset(
                target.Card,
                target.Window,
                now) is null
            && remaining is not null
            && _quotaPaceEstimates.TryGetValue(
                target.PaceKey,
                out var pace))
        {
            budgetMarkerRemaining = QuotaDisplayFormatting.BudgetMarkerRemaining(
                target.Window,
                pace.Cycle,
                now);
        }
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            railY,
            remaining,
            valueColor,
            budgetMarkerRemaining);
    }

    private void DrawTaskbarCapsule(Graphics graphics, MiniQuotaTarget target)
    {
        if (target.Card.IsService)
        {
            DrawTaskbarServiceMetric(graphics, target);
            return;
        }

        var bounds = target.Bounds;
        var window = target.Window;
        var card = target.Card;
        var cardKey = card.Key;
        var windowKey = target.Id;
        var hovered = string.Equals(_hoverQuotaTarget?.Id, windowKey, StringComparison.Ordinal);
        var selected = string.Equals(_popoverTargetId, windowKey, StringComparison.Ordinal);
        var hover = selected
            ? 1
            : hovered
                ? HoverProgress(HoverTarget.QuotaWindow)
                : 0;
        var updatePulse = SnapshotPulse(windowKey);
        var now = DateTimeOffset.UtcNow;
        var weeklyBlockReset = QuotaDisplayFormatting.WeeklyBlockReset(card, window, now);
        var blockedByWeekly = weeklyBlockReset is not null;
        var used = AnimatedUsed(cardKey, window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = blockedByWeekly || remaining is null
            ? Color.FromArgb(100, 116, 139)
            : QuotaColorScale.ForRemaining(remaining.Value);
        valueColor = MixColor(valueColor, Color.FromArgb(248, 250, 252), updatePulse * .22f);

        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(226, 232, 240),
            hover * .08f));
        using var border = new Pen(MixColor(
            Color.FromArgb(58, 51, 65, 85),
            Color.FromArgb(154, valueColor.R, valueColor.G, valueColor.B),
            Math.Max(hover * .62f, updatePulse * .85f)), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var accentPen = new Pen(valueColor, 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);
        _quotaPaceEstimates.TryGetValue(target.PaceKey, out var pace);
        double? budgetMarkerRemaining = !blockedByWeekly && remaining is not null
            ? QuotaDisplayFormatting.BudgetMarkerRemaining(window, pace?.Cycle, now)
            : null;
        DrawTaskbarProgressRail(graphics, bounds, remaining, valueColor, budgetMarkerRemaining);

        using var labelBrush = new SolidBrush(blockedByWeekly
            ? Color.FromArgb(100, 116, 139)
            : Color.FromArgb(226, 232, 240));
        using var valueBrush = new SolidBrush(valueColor);
        using var resetBrush = new SolidBrush(blockedByWeekly || window.ResetsAt is null
            ? Color.FromArgb(100, 116, 139)
            : Color.FromArgb(226, 232, 240));
        DrawString(
            graphics,
            QuotaDisplayFormatting.FormatWindowShort(window),
            _badgeFont,
            labelBrush,
            new RectangleF(bounds.X + 7, bounds.Y, 16, bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(bounds.X + 21, bounds.Y, 28, bounds.Height),
            StringAlignment.Far);

        var resetIconBounds = new RectangleF(bounds.X + 49, bounds.Y, 12, bounds.Height);
        if (blockedByWeekly)
        {
            DrawTaskbarLockIcon(graphics, resetIconBounds);
        }
        else
        {
            DrawResetClockIcon(graphics, resetIconBounds);
        }
        DrawString(
            graphics,
            QuotaDisplayFormatting.FormatResetShort(weeklyBlockReset ?? window.ResetsAt, now),
            _resetFont,
            resetBrush,
            new RectangleF(bounds.X + 60, bounds.Y, bounds.Width - 63, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawTaskbarServiceMetric(Graphics graphics, MiniQuotaTarget target)
    {
        var bounds = target.Bounds;
        var hovered = string.Equals(_hoverQuotaTarget?.Id, target.Id, StringComparison.Ordinal);
        var selected = string.Equals(_popoverTargetId, target.Id, StringComparison.Ordinal);
        var hover = selected
            ? 1
            : hovered
                ? HoverProgress(HoverTarget.QuotaWindow)
                : 0;
        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(226, 232, 240),
            hover * .08f));
        using var border = new Pen(MixColor(
            Color.FromArgb(58, 51, 65, 85),
            Color.FromArgb(154, 100, 116, 139),
            Math.Max(hover * .62f, .1f)), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        if (Sub2ApiServicePresentation.IsSub2ApiService(target.Card))
        {
            DrawTaskbarSub2ApiMetric(graphics, target, hover);
            return;
        }

        var accountAvailability = target.Card.Sub2ApiAccountAvailability;
        var accountAvailabilityRemaining = Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(accountAvailability);
        var quota = target.Card.Sub2ApiQuota;
        var quotaWindow = Sub2ApiQuotaFormatting.PreferredWindow(quota);
        var usage = target.Card.Sub2ApiUsage;
        var pool = target.Card.Sub2ApiPool;
        using var accentPen = new Pen(accountAvailabilityRemaining is not null
            ? Sub2ApiQuotaCompactStatusColor(accountAvailability!.Status)
            : quotaWindow is not null
            ? Sub2ApiQuotaCompactStatusColor(quota!.Status)
            : usage is not null
            ? Sub2ApiUsageCompactStatusColor(usage.Status)
            : pool is not null
                ? Sub2ApiPoolCompactStatusColor(pool.Status)
                : Color.FromArgb(100, 116, 139), 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);
        if (accountAvailabilityRemaining is not null)
        {
            var availabilityColor = Sub2ApiQuotaCompactStatusColor(accountAvailability!.Status);
            DrawTaskbarProgressRail(
                graphics,
                bounds,
                accountAvailabilityRemaining,
                availabilityColor,
                null);
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiAccountAvailabilityCompact(accountAvailability!),
                availabilityColor);
            return;
        }
        if (quotaWindow is not null)
        {
            var quotaColor = Sub2ApiQuotaCompactStatusColor(quota!.Status);
            DrawTaskbarProgressRail(
                graphics,
                bounds,
                quotaWindow.RemainingPercent,
                quotaColor,
                null);
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiQuotaCompact(quota!),
                quotaColor);
            return;
        }
        if (usage is not null)
        {
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiUsageCompact(usage),
                Sub2ApiUsageCompactStatusColor(usage.Status));
            return;
        }
        if (pool is not null)
        {
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                Sub2ApiPoolFormatting.AccountPair(pool),
                Sub2ApiPoolCompactStatusColor(pool.Status));
            return;
        }
        if (target.Card.Provider != ProviderKind.AiGateway)
        {
            using var labelBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
            DrawString(
                graphics,
                target.Card.DisplayLabel,
                _rowFont,
                labelBrush,
                new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - 12, bounds.Height),
                StringAlignment.Center);
            return;
        }

        using var valueBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        var value = target.Card.Balance is { } balance
            ? AiGatewayBalanceFormatting.Amount(balance.TotalBalance)
            : _text.ApiServiceConfiguredShort;
        DrawString(
            graphics,
            value,
            _rowFont,
            valueBrush,
            new RectangleF(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height),
            StringAlignment.Center);
    }

    private void DrawTaskbarSub2ApiMetric(
        Graphics graphics,
        MiniQuotaTarget target,
        float hover)
    {
        var bounds = target.Bounds;
        var presentation = Sub2ApiServicePresentation.Resolve(target.Card, _snapshot.CapturedAt);
        var valueColor = Sub2ApiPresentationCompactStatusColor(target.Card, presentation);
        using var accentPen = new Pen(valueColor, 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);

        switch (presentation.Kind)
        {
            case Sub2ApiServicePresentationKind.CompleteAvailability
                when presentation.Availability is { } availability
                && Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is { } remaining:
                DrawTaskbarProgressRail(graphics, bounds, remaining, valueColor, null);
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiAccountAvailabilityCompact(availability),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.PartialAvailability
                or Sub2ApiServicePresentationKind.KnownNoneAvailability
                when presentation.Availability is { } coverage:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiAccountAvailabilityCompact(coverage),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.LegacyAggregateQuota
                when presentation.LegacyQuota is { } legacy:
                DrawTaskbarProgressRail(graphics, bounds, legacy.RemainingPercent, valueColor, null);
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiLegacyQuotaCompact(legacy),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.Usage
                when presentation.Usage is { } usage:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiUsageCompact(usage),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.Pool
                when presentation.Pool is { } pool:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    Sub2ApiPoolFormatting.AccountPair(pool),
                    valueColor);
                return;
            default:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiUnavailable,
                    valueColor);
                return;
        }
    }

    private static Color Sub2ApiPresentationCompactStatusColor(
        QuotaCard card,
        Sub2ApiServicePresentationState presentation) => presentation.Kind switch
    {
        Sub2ApiServicePresentationKind.CompleteAvailability
            or Sub2ApiServicePresentationKind.PartialAvailability
            or Sub2ApiServicePresentationKind.KnownNoneAvailability
            when presentation.Availability is { } availability =>
                Sub2ApiQuotaCompactStatusColor(availability.Status),
        Sub2ApiServicePresentationKind.LegacyAggregateQuota
            when card.Sub2ApiQuota is { } quota => Sub2ApiQuotaCompactStatusColor(quota.Status),
        Sub2ApiServicePresentationKind.Usage
            when presentation.Usage is { } usage => Sub2ApiUsageCompactStatusColor(usage.Status),
        Sub2ApiServicePresentationKind.Pool
            when presentation.Pool is { } pool => Sub2ApiPoolCompactStatusColor(pool.Status),
        _ => Color.FromArgb(251, 113, 133),
    };

    private void DrawTaskbarServiceCompactMetric(
        Graphics graphics,
        RectangleF bounds,
        string label,
        string value,
        Color valueColor)
    {
        const float valueWidth = 44f;
        using var labelBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        using var valueBrush = new SolidBrush(valueColor);
        if (bounds.Width < 84f)
        {
            DrawString(
                graphics,
                value,
                _badgeFont,
                valueBrush,
                new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - 12, bounds.Height),
                StringAlignment.Center);
            return;
        }

        DrawString(
            graphics,
            label,
            _badgeFont,
            labelBrush,
            new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - valueWidth - 12, bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            value,
            _badgeFont,
            valueBrush,
            new RectangleF(bounds.Right - valueWidth - 4, bounds.Y, valueWidth, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawTaskbarLockIcon(Graphics graphics, RectangleF bounds)
    {
        var state = graphics.Save();
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(Color.FromArgb(100, 116, 139));
        graphics.DrawString(LockIconGlyph, _miniSystemIconFont, brush, bounds, _systemIconFormat);
        graphics.Restore(state);
    }

    private void DrawTaskbarProgressRail(
        Graphics graphics,
        RectangleF bounds,
        double? remaining,
        Color valueColor,
        double? budgetMarkerRemaining)
    {
        var railStart = bounds.X + 7;
        var railWidth = bounds.Width - 10;
        var railY = bounds.Bottom - 2;
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            railY,
            remaining,
            valueColor,
            budgetMarkerRemaining);
    }

    private static void DrawTaskbarQuotaRail(
        Graphics graphics,
        float railStart,
        float railWidth,
        float railY,
        double? remaining,
        Color valueColor,
        double? budgetMarkerRemaining)
    {
        using var troughPen = new Pen(Color.FromArgb(30, 41, 59), 3)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(troughPen, railStart, railY, railStart + railWidth, railY);

        if (remaining is { } remainingValue)
        {
            var clampedRemaining = Math.Clamp(remainingValue, 0, 100);
            var splitX = railStart + railWidth * (float)(clampedRemaining / 100);
            if (clampedRemaining > 0)
            {
                using var activePen = new Pen(valueColor, 2)
                {
                    StartCap = LineCap.Round,
                    EndCap = clampedRemaining >= 100 ? LineCap.Round : LineCap.Flat,
                };
                graphics.DrawLine(activePen, railStart, railY, splitX, railY);
            }
            if (clampedRemaining < 100)
            {
                using var usedPen = new Pen(Color.FromArgb(71, 85, 105), 2)
                {
                    StartCap = clampedRemaining <= 0 ? LineCap.Round : LineCap.Flat,
                    EndCap = LineCap.Round,
                };
                graphics.DrawLine(usedPen, splitX, railY, railStart + railWidth, railY);
            }
        }

        if (budgetMarkerRemaining is { } targetRemaining)
        {
            DrawBudgetMarkerPointer(graphics, railStart, railWidth, railY, targetRemaining);
        }
    }

    private static void DrawBudgetMarkerPointer(
        Graphics graphics,
        float railStart,
        float railWidth,
        float railY,
        double targetRemaining)
    {
        var markerX = railStart + railWidth * (float)(Math.Clamp(targetRemaining, 0, 100) / 100);
        var markerY = railY - .5f;
        using var markerBrush = new SolidBrush(Color.FromArgb(253, 230, 138));
        using var markerCoreBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
        graphics.FillPolygon(markerBrush,
        [
            new PointF(markerX, markerY - 2),
            new PointF(markerX + 2, markerY),
            new PointF(markerX, markerY + 2),
            new PointF(markerX - 2, markerY),
        ]);
        graphics.FillPolygon(markerCoreBrush,
        [
            new PointF(markerX, markerY - .8f),
            new PointF(markerX + .8f, markerY),
            new PointF(markerX, markerY + .8f),
            new PointF(markerX - .8f, markerY),
        ]);
    }

    private void DrawResetClockIcon(Graphics graphics, RectangleF bounds) =>
        graphics.DrawImage(_resetClockIcon, CenteredSquare(bounds, MiniResetClockSize));

    private void DrawRadarUnreadDot(Graphics graphics, RectangleF logoBounds)
    {
        var dotBounds = new RectangleF(logoBounds.Right - 5, logoBounds.Top - 1, 7, 7);
        using var border = new SolidBrush(_backgroundTheme.Outer);
        using var fill = new SolidBrush(Color.FromArgb(248, 74, 88));
        graphics.FillEllipse(border, dotBounds);
        graphics.FillEllipse(
            fill,
            new RectangleF(dotBounds.X + 1, dotBounds.Y + 1, 5, 5));
    }

    private void DrawProviderLogo(Graphics graphics, RectangleF logoBounds, QuotaCard card) =>
        DrawProviderLogo(graphics, logoBounds, card, true);

    private void DrawProviderLogo(
        Graphics graphics,
        RectangleF logoBounds,
        QuotaCard card,
        bool showOrdinal)
    {
        graphics.DrawImage(ProviderLogo(card.Provider), logoBounds);

        if (!showOrdinal) return;
        var ordinal = CodexAccountOrdinal(card);
        if (ordinal is null) return;
        var badgeBounds = new RectangleF(logoBounds.Right - 7, logoBounds.Bottom - 6, 10, 8);
        DrawAccountOrdinalBadge(graphics, badgeBounds, ordinal);
    }

    private void DrawAccountOrdinalBadge(Graphics graphics, RectangleF badgeBounds, string ordinal)
    {
        using var badgeBrush = new SolidBrush(_backgroundTheme.Outer);
        using var border = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var textBrush = new SolidBrush(Color.FromArgb(153, 246, 228));
        graphics.FillEllipse(badgeBrush, badgeBounds);
        graphics.DrawEllipse(border, badgeBounds);
        DrawString(graphics, ordinal, _accountOrdinalFont, textBrush, badgeBounds, StringAlignment.Center);
    }

    private static string? CodexAccountOrdinal(QuotaCard card)
    {
        if (card.Provider != ProviderKind.Codex) return null;
        var separator = card.Label.IndexOf('·');
        if (separator < 0) return null;
        return card.Label.AsSpan(separator + 1).Trim().ToString();
    }

    private void DrawCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        using var path = RoundedRectangle(bounds, 7);
        using var fill = new SolidBrush(_backgroundTheme.ProviderGroup);
        using var border = new Pen(Color.FromArgb(36, 148, 163, 184));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var accent = ColorTranslator.FromHtml(card.Accent);
        using var accentBrush = new SolidBrush(accent);
        graphics.FillEllipse(accentBrush, bounds.X + 7, bounds.Y + 6, 6, 6);

        var badgeWidth = string.IsNullOrWhiteSpace(card.Badge)
            ? 0
            : Math.Min(48, Math.Max(22, MeasureWidth(graphics, card.Badge!, _badgeFont) + 6));
        var titleWidth = bounds.Width - 23 - badgeWidth;
        using var titleBrush = new SolidBrush(Color.FromArgb(248, 250, 252));
        DrawString(
            graphics,
            card.DisplayLabel,
            _cardTitleFont,
            titleBrush,
            new RectangleF(bounds.X + 17, bounds.Y + 2, titleWidth, 12),
            StringAlignment.Near);

        if (badgeWidth > 0)
        {
            var badgeBounds = new RectangleF(bounds.Right - badgeWidth - 6, bounds.Y + 3, badgeWidth, 10);
            using var badgePath = RoundedRectangle(badgeBounds, 3);
            using var badgeFill = new SolidBrush(Color.FromArgb(34, 51, 65, 85));
            using var badgeText = new SolidBrush(Color.FromArgb(203, 213, 225));
            graphics.FillPath(badgeFill, badgePath);
            DrawString(graphics, card.Badge!, _badgeFont, badgeText, badgeBounds, StringAlignment.Center);
        }

        if (card.IsService)
        {
            using var serviceBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
            if (card.Sub2ApiQuota is { } quota && Sub2ApiQuotaFormatting.PreferredWindow(quota) is not null)
            {
                DrawString(
                    graphics,
                    _text.Sub2ApiQuotaSummaryShort(quota),
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Sub2ApiUsage is { } usage)
            {
                DrawString(
                    graphics,
                    _text.Sub2ApiUsageSummaryShort(usage),
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Sub2ApiPool is { } pool)
            {
                DrawString(
                    graphics,
                    $"{_text.Sub2ApiPoolAvailableAccounts(pool)} · {_text.Sub2ApiPoolStatusShort(pool.Status)}",
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Balance is { } balance)
            {
                DrawString(
                    graphics,
                    $"{AiGatewayBalanceFormatting.Amount(balance.TotalBalance)} · {_text.AiGatewayStatusShort(balance.Status)}",
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            DrawString(
                graphics,
                _text.ApiServiceConfigured,
                _rowFont,
                serviceBrush,
                new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                StringAlignment.Near);
            return;
        }

        var windowCount = Math.Min(3, card.Windows.Count);
        if (windowCount == 3)
        {
            DrawQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 15,
                bounds.Width - 14,
                card,
                card.Windows[0]);
            DrawDualQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 24,
                bounds.Width - 14,
                card,
                card.Windows[1],
                card.Windows[2]);
            return;
        }

        for (var index = 0; index < windowCount; index++)
        {
            DrawQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 15 + index * 9,
                bounds.Width - 14,
                card,
                card.Windows[index]);
        }
    }

    private void DrawDualQuotaRow(
        Graphics graphics,
        float x,
        float y,
        float width,
        QuotaCard card,
        QuotaWindow first,
        QuotaWindow second)
    {
        var gap = 7f;
        var segmentWidth = (width - gap) / 2;
        DrawCompactQuotaMetric(
            graphics,
            new RectangleF(x, y - 1, segmentWidth, 9),
            card,
            first);
        using var separator = new Pen(Color.FromArgb(58, 71, 85, 105), 1);
        var separatorX = x + segmentWidth + gap / 2;
        graphics.DrawLine(separator, separatorX, y, separatorX, y + 6);
        DrawCompactQuotaMetric(
            graphics,
            new RectangleF(x + segmentWidth + gap, y - 1, segmentWidth, 9),
            card,
            second);
    }

    private void DrawCompactQuotaMetric(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        QuotaWindow window)
    {
        var label = QuotaDisplayFormatting.FormatWindowTiny(window);
        var labelWidth = label.Length > 1 ? 20f : 12f;
        var valueWidth = 27f;
        var trackX = bounds.X + labelWidth;
        var trackWidth = Math.Max(10, bounds.Width - labelWidth - valueWidth - 2);
        var used = AnimatedUsed(card.Key, window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? Color.FromArgb(100, 116, 139)
            : MixColor(
                QuotaColorScale.ForRemaining(remaining.Value),
                Color.FromArgb(248, 250, 252),
                SnapshotPulse(WindowKey(card.Key, window.Label)) * .22f);
        using var labelBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        using var trackBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            label,
            _rowFont,
            labelBrush,
            new RectangleF(bounds.X, bounds.Y, labelWidth, bounds.Height),
            StringAlignment.Near);
        var track = new RectangleF(trackX, bounds.Y + 2, trackWidth, 5);
        graphics.FillRoundedRectangle(trackBrush, track, 2);
        if (remaining is > 0)
        {
            using var activeBrush = new SolidBrush(valueColor);
            graphics.FillRoundedRectangle(
                activeBrush,
                new RectangleF(track.X, track.Y, track.Width * (float)(remaining.Value / 100), track.Height),
                2);
        }
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(track.Right + 2, bounds.Y, valueWidth, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawQuotaRow(
        Graphics graphics,
        float x,
        float y,
        float width,
        QuotaCard card,
        QuotaWindow window)
    {
        var cardKey = card.Key;
        var now = DateTimeOffset.UtcNow;
        var weeklyBlockReset = QuotaDisplayFormatting.WeeklyBlockReset(card, window, now);
        var blockedByWeekly = weeklyBlockReset is not null;
        var isWeek = window.Label is "1w" or "week" or "7d" or "30d";
        using var labelBrush = new SolidBrush(blockedByWeekly
            ? Color.FromArgb(100, 116, 139)
            : Color.FromArgb(226, 232, 240));
        if (blockedByWeekly)
        {
            DrawTaskbarLockIcon(graphics, new RectangleF(x, y - 1, 8, 9));
        }
        else if (isWeek)
        {
            using var markPen = new Pen(labelBrush.Color, 1);
            graphics.DrawRectangle(markPen, x, y + 2, 7, 4);
        }
        else
        {
            graphics.FillEllipse(labelBrush, x + 1, y + 2, 5, 4);
        }
        DrawString(graphics, window.Label, _rowFont, labelBrush, new RectangleF(x + 9, y - 1, 17, 9), StringAlignment.Near);

        var metricsWidth = 64f;
        var trackX = x + 28;
        var trackWidth = Math.Max(12, width - 28 - metricsWidth - 3);
        var track = new RectangleF(trackX, y + 1, trackWidth, 5);
        using var trackBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
        graphics.FillRoundedRectangle(trackBrush, track, isWeek ? 2 : 3);

        var used = AnimatedUsed(cardKey, window);
        if (used is not null)
        {
            var clampedUsed = Math.Clamp(used.Value, 0, 100);
            var remaining = 100 - clampedUsed;
            var resetAt = weeklyBlockReset ?? window.ResetsAt;
            var resetRemaining = resetAt is null ? (TimeSpan?)null : resetAt.Value - now;
            double? elapsedPercent = null;
            if (resetRemaining is { } reset && reset >= TimeSpan.Zero && reset <= window.Duration)
            {
                elapsedPercent = Math.Clamp((window.Duration - reset).TotalMilliseconds / window.Duration.TotalMilliseconds * 100, 0, 100);
                using var elapsedBrush = new SolidBrush(Color.FromArgb(51, 65, 85));
                graphics.FillRectangle(elapsedBrush, track.X, track.Y, track.Width * (float)(elapsedPercent.Value / 100), track.Height);
            }

            var remainingColor = blockedByWeekly
                ? Color.FromArgb(100, 116, 139)
                : MixColor(
                    QuotaColorScale.ForRemaining(remaining),
                    Color.FromArgb(248, 250, 252),
                    SnapshotPulse(WindowKey(cardKey, window.Label)) * .22f);
            using var remainingBrush = new SolidBrush(remainingColor);
            var remainingBounds = new RectangleF(track.X, track.Y, track.Width * (float)(remaining / 100), track.Height);
            if (remainingBounds.Width > 0) graphics.FillRoundedRectangle(remainingBrush, remainingBounds, isWeek ? 2 : 3);
            if (elapsedPercent is not null)
            {
                var markerX = track.X + track.Width * (float)(elapsedPercent.Value / 100);
                using var markerPen = new Pen(clampedUsed - elapsedPercent.Value > 10
                    ? Color.FromArgb(251, 113, 133)
                    : Color.FromArgb(100, 116, 139));
                graphics.DrawLine(markerPen, markerX, track.Y, markerX, track.Bottom);
            }

            using var percentBrush = new SolidBrush(remainingColor);
            using var separatorBrush = new SolidBrush(Color.FromArgb(72, 148, 163, 184));
            using var resetBrush = new SolidBrush(blockedByWeekly || resetAt is null
                ? Color.FromArgb(100, 116, 139)
                : Color.FromArgb(226, 232, 240));
            var metricsX = track.Right + 3;
            DrawString(
                graphics,
                FormatPercent(remaining),
                _rowFont,
                percentBrush,
                new RectangleF(metricsX, y - 1, 26, 9),
                StringAlignment.Far);
            DrawString(
                graphics,
                "·",
                _resetFont,
                separatorBrush,
                new RectangleF(metricsX + 26, y - 1, 4, 9),
                StringAlignment.Center);
            DrawString(
                graphics,
                _text.FormatCompactReset(resetAt, now),
                _resetFont,
                resetBrush,
                new RectangleF(metricsX + 30, y - 1, metricsWidth - 30, 9),
                StringAlignment.Far);
        }
        else
        {
            using var waitingBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            graphics.FillRectangle(waitingBrush, track.X, track.Y, Math.Max(8, track.Width * .18f), track.Height);
            DrawString(
                graphics,
                _refreshing ? _text.Sync : _text.Wait,
                _rowFont,
                waitingBrush,
                new RectangleF(track.Right + 3, y - 1, metricsWidth, 9),
                StringAlignment.Far);
        }
    }

    private void DrawHealth(Graphics graphics)
    {
        var cardsWidth = _visibleCards.Length * BarLayoutMath.CardWidth
            + Math.Max(0, _visibleCards.Length - 1) * BarLayoutMath.CardGap;
        if (_hiddenCardCount > 0) cardsWidth += BarLayoutMath.CardGap + BarLayoutMath.OverflowWidth;
        var x = BarLayoutMath.OuterPadding + BarLayoutMath.LabelWidth + BarLayoutMath.SectionGap
            + cardsWidth + BarLayoutMath.SectionGap;
        var health = _snapshot.Health.Count > 0
            ? _snapshot.Health
            : [new ProviderHealth(
                ProviderKind.Claude,
                false,
                _text.QuotaWaiting,
                ProviderHealthCode.Waiting)];
        foreach (var status in health)
        {
            using var brush = new SolidBrush(status.Connected
                ? Color.FromArgb(52, 211, 153)
                : Color.FromArgb(100, 116, 139));
            graphics.FillEllipse(brush, x, 18, BarLayoutMath.HealthDotWidth, BarLayoutMath.HealthDotWidth);
            x += BarLayoutMath.HealthDotWidth + BarLayoutMath.HealthDotGap;
        }
    }

    private void DrawControls(Graphics graphics, float logicalWidth)
    {
        var x = logicalWidth - BarLayoutMath.OuterPadding - BarLayoutMath.ControlsWidth;
        _taskbarAreaBounds.Clear();
        _refreshBounds = new RectangleF(x + 1, 10, 22, 22);
        _settingsBounds = new RectangleF(x + 25, 10, 22, 22);
        DrawControlButton(graphics, _refreshBounds, HoverTarget.Refresh);
        DrawControlButton(graphics, _settingsBounds, HoverTarget.Settings);
        DrawRefreshIcon(
            graphics,
            PressedIconBounds(_refreshBounds, HoverTarget.Refresh),
            _refreshing,
            _refreshRotation,
            HoverProgress(HoverTarget.Refresh));
        DrawSettingsIcon(
            graphics,
            PressedIconBounds(_settingsBounds, HoverTarget.Settings),
            HoverProgress(HoverTarget.Settings));
    }

    private void DrawControlButton(Graphics graphics, RectangleF bounds, HoverTarget target)
    {
        using var path = RoundedRectangle(bounds, 5);
        var (fillColor, borderEmphasis) = ControlVisual(target);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(MixColor(
            Color.FromArgb(42, 148, 163, 184),
            Color.FromArgb(130, 129, 140, 248),
            borderEmphasis));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawTaskbarControlGroup(Graphics graphics, bool drawShell = true)
    {
        var groupBounds = RectangleF.FromLTRB(
            _refreshBounds.Left,
            _refreshBounds.Top,
            _settingsBounds.Right,
            _settingsBounds.Bottom);
        using var path = RoundedRectangle(groupBounds, 6);
        var state = graphics.Save();
        graphics.SetClip(path);
        DrawTaskbarControlSegment(graphics, _refreshBounds, HoverTarget.Refresh);
        DrawTaskbarControlSegment(graphics, _settingsBounds, HoverTarget.Settings);
        graphics.Restore(state);

        using var separator = new Pen(Color.FromArgb(42, 100, 116, 139));
        if (drawShell)
        {
            using var border = new Pen(Color.FromArgb(54, 100, 116, 139));
            graphics.DrawPath(border, path);
        }
        graphics.DrawLine(separator, groupBounds.Left + 5, _settingsBounds.Top, groupBounds.Right - 5, _settingsBounds.Top);
    }

    private void DrawTaskbarControlSegment(Graphics graphics, RectangleF bounds, HoverTarget target)
    {
        var (fillColor, _) = ControlVisual(target);
        using var fill = new SolidBrush(fillColor);
        graphics.FillRectangle(fill, bounds);
    }

    private (Color Fill, float BorderEmphasis) ControlVisual(HoverTarget target)
    {
        var hover = HoverProgress(target);
        var pressed = _pressedTarget == target;
        var refreshPulse = target == HoverTarget.Refresh && _refreshing
            ? .35f + .18f * (float)Math.Sin(_refreshRotation * Math.PI / 180)
            : 0;
        var emphasis = Math.Max(hover, refreshPulse);
        var fillColor = MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(226, 232, 240),
            emphasis * .08f);
        if (pressed) fillColor = MixColor(fillColor, Color.FromArgb(49, 46, 129), .55f);
        return (fillColor, Math.Max(emphasis * .75f, pressed ? 1 : 0));
    }

    private void DrawRefreshIcon(
        Graphics graphics,
        RectangleF bounds,
        bool refreshing,
        float rotation,
        float hover,
        float iconSize = ControlIconSize)
    {
        var color = refreshing
            ? Color.FromArgb(129, 140, 248)
            : MixColor(Color.FromArgb(148, 163, 184), Color.FromArgb(226, 232, 240), hover);
        var iconBounds = CenteredSquare(bounds, iconSize);
        var center = new PointF(
            iconBounds.Left + iconBounds.Width / 2,
            iconBounds.Top + iconBounds.Height / 2);
        var state = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(rotation);

        using var pen = new Pen(color, ControlIconStrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var radius = iconSize * 4.8f / ControlIconSize;
        const float startAngle = 25f;
        const float sweepAngle = 290f;
        graphics.DrawArc(pen, -radius, -radius, radius * 2, radius * 2, startAngle, sweepAngle);

        var endAngle = (startAngle + sweepAngle) * Math.PI / 180;
        var tip = new PointF(
            radius * (float)Math.Cos(endAngle),
            radius * (float)Math.Sin(endAngle));
        var tangent = new PointF(
            -(float)Math.Sin(endAngle),
            (float)Math.Cos(endAngle));
        var normal = new PointF(-tangent.Y, tangent.X);
        var arrowLength = iconSize * 2f / ControlIconSize;
        var arrowHalfWidth = iconSize * 1.2f / ControlIconSize;
        var arrowBase = new PointF(
            tip.X - tangent.X * arrowLength,
            tip.Y - tangent.Y * arrowLength);
        graphics.DrawLines(pen,
        [
            new PointF(
                arrowBase.X + normal.X * arrowHalfWidth,
                arrowBase.Y + normal.Y * arrowHalfWidth),
            tip,
            new PointF(
                arrowBase.X - normal.X * arrowHalfWidth,
                arrowBase.Y - normal.Y * arrowHalfWidth),
        ]);
        graphics.Restore(state);
    }

    private void DrawSettingsIcon(
        Graphics graphics,
        RectangleF bounds,
        float hover,
        bool taskbarCompact = false)
    {
        var color = MixColor(
            Color.FromArgb(148, 163, 184),
            Color.FromArgb(226, 232, 240),
            hover);
        DrawSystemIcon(
            graphics,
            bounds,
            SettingsIconGlyph,
            color,
            taskbarCompact ? _taskbarControlIconFont : _systemIconFont);
    }

    private void DrawTaskbarCollapseHandle(Graphics graphics, MiniAreaTarget target)
    {
        var hover = string.Equals(_hoverMiniAreaId, target.AreaId, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.MiniCollapse)
            : 0;
        var bounds = target.HandleBounds;
        if (string.Equals(_pressedMiniAreaId, target.AreaId, StringComparison.Ordinal)) bounds.Offset(0, .7f);
        if (hover > 0)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(
                (int)Math.Round(28 * hover),
                148,
                163,
                184));
            graphics.FillRoundedRectangle(hoverBrush, bounds, 4);
        }
        var color = MixColor(
            Color.FromArgb(118, 148, 163, 184),
            Color.FromArgb(226, 232, 240),
            hover);
        using var separator = new Pen(Color.FromArgb(
            (int)Math.Round(42 + hover * 46),
            100,
            116,
            139), 1)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var separatorX = bounds.Left + 1.2f;
        var separatorBottom = target.Reorderable
            ? target.ReorderBounds.Bottom - 4
            : bounds.Bottom - 4;
        graphics.DrawLine(separator, separatorX, bounds.Top + 4, separatorX, separatorBottom);
        using var pen = new Pen(color, 1.5f + hover * .55f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var x = bounds.X + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        var direction = target.Collapsed ? 1f : -1f;
        var tipX = x + direction * 2.2f;
        graphics.DrawLines(pen,
        [
            new PointF(x, centerY - 2.6f),
            new PointF(tipX, centerY),
            new PointF(x, centerY + 2.6f),
        ]);
    }

    private void DrawTaskbarReorderGrip(Graphics graphics, MiniAreaTarget target)
    {
        if (!target.Reorderable) return;
        var hover = string.Equals(_hoverReorderAreaId, target.AreaId, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.MiniReorder)
            : 0;
        var active = string.Equals(_reorderingMiniAreaId, target.AreaId, StringComparison.Ordinal);
        var bounds = target.ReorderBounds;
        if (hover > 0 || active)
        {
            using var fill = new SolidBrush(Color.FromArgb(
                (int)Math.Round(active ? 36 : 26 * hover),
                96,
                165,
                250));
            graphics.FillRoundedRectangle(fill, bounds, 4);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(10, 148, 163, 184));
            graphics.FillRoundedRectangle(fill, bounds, 4);
        }
        var color = active
            ? Color.FromArgb(147, 197, 253)
            : MixColor(Color.FromArgb(176, 148, 163, 184), Color.FromArgb(226, 232, 240), hover);
        using var brush = new SolidBrush(color);
        var diameter = active || hover > 0 ? 2.2f : 2f;
        var left = bounds.Left + (bounds.Width - diameter * 2 - 1.35f) / 2;
        var top = bounds.Top + (bounds.Height - diameter * 3 - 2.5f) / 2;
        for (var row = 0; row < 3; row++)
        {
            var y = top + row * (diameter + 1.25f);
            graphics.FillEllipse(brush, left, y, diameter, diameter);
            graphics.FillEllipse(brush, left + diameter + 1.35f, y, diameter, diameter);
        }
    }

    private void DrawMiniReorderInsertionMarker(Graphics graphics)
    {
        if (!_reorderMoved || _reorderingMiniAreaId is null) return;
        var targets = _taskbarAreaBounds
            .Where(target => target.Reorderable
                && !string.Equals(target.AreaId, _reorderingMiniAreaId, StringComparison.Ordinal))
            .OrderBy(target => target.Bounds.Left)
            .ToArray();
        if (targets.Length == 0) return;
        var before = _reorderBeforeAreaId is null
            ? null
            : targets.FirstOrDefault(target => string.Equals(
                target.AreaId,
                _reorderBeforeAreaId,
                StringComparison.Ordinal));
        var x = before is null
            ? targets[^1].Bounds.Right + TaskbarMiniLayoutMath.ModuleGap / 2f
            : before.Bounds.Left - TaskbarMiniLayoutMath.ModuleGap / 2f;
        using var pen = new Pen(Color.FromArgb(210, 96, 165, 250), 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var markerTop = targets.Min(target => target.Bounds.Top) + 4;
        var markerBottom = targets.Max(target => target.Bounds.Bottom) - 4;
        graphics.DrawLine(pen, x, markerTop, x, markerBottom);
    }

    private void DrawTaskbarResizeGrip(Graphics graphics, MiniAreaTarget target)
    {
        if (target.Collapsed || target.ResizeBounds.IsEmpty) return;
        var active = string.Equals(_hoverResizeAreaId, target.AreaId, StringComparison.Ordinal)
            || string.Equals(_resizingMiniAreaId, target.AreaId, StringComparison.Ordinal);
        using var pen = new Pen(
            active
                ? Color.FromArgb(150, 148, 163, 184)
                : Color.FromArgb(34, 100, 116, 139),
            active ? 1.4f : 1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var x = target.ResizeBounds.Left + target.ResizeBounds.Width / 2;
        graphics.DrawLine(pen, x, target.Bounds.Top + 11, x, target.Bounds.Bottom - 11);
    }

    private void DrawSystemIcon(
        Graphics graphics,
        RectangleF bounds,
        string glyph,
        Color color,
        Font font,
        float rotation = 0)
    {
        var state = graphics.Save();
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var iconBounds = bounds;
        iconBounds.Offset(0, SystemIconVerticalOffset);
        if (rotation != 0)
        {
            graphics.TranslateTransform(iconBounds.Left + iconBounds.Width / 2, iconBounds.Top + iconBounds.Height / 2);
            graphics.RotateTransform(rotation);
            graphics.TranslateTransform(-(iconBounds.Left + iconBounds.Width / 2), -(iconBounds.Top + iconBounds.Height / 2));
        }
        using var brush = new SolidBrush(color);
        graphics.DrawString(glyph, font, brush, iconBounds, _systemIconFormat);
        graphics.Restore(state);
    }

    private static RectangleF CenteredSquare(RectangleF bounds, float size) => new(
        bounds.Left + (bounds.Width - size) / 2,
        bounds.Top + (bounds.Height - size) / 2,
        size,
        size);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_reorderingMiniAreaId is not null)
        {
            ContinueMiniAreaReorder(e);
            return;
        }
        if (_resizingMiniAreaId is not null)
        {
            ContinueMiniAreaResize(e);
            return;
        }
        if (_taskbarDragging)
        {
            ContinueTaskbarDrag(e);
            return;
        }

        var logical = ToLogical(e.Location);
        var resizeTarget = TaskbarResizeTargetAt(logical);
        if (resizeTarget is not null)
        {
            _hoverMiniAreaId = null;
            _hoverReorderAreaId = null;
            _hoverResizeAreaId = resizeTarget.AreaId;
            _hoverPluginTarget = null;
            UpdateSystemUsageHover(false);
            UpdateRadarHover(null);
            UpdateCodexAccountsHover(null);
            UpdateQuotaHover(null);
            UpdateHintHover(HoverTarget.None);
            _hoverTarget = HoverTarget.None;
            Cursor = Cursors.SizeWE;
            Invalidate();
            return;
        }
        _hoverResizeAreaId = null;
        var reorderTarget = TaskbarReorderTargetAt(logical);
        if (reorderTarget is not null)
        {
            _hoverMiniAreaId = null;
            _hoverReorderAreaId = reorderTarget.AreaId;
            _hoverPluginTarget = null;
            UpdateSystemUsageHover(false);
            UpdateRadarHover(null);
            UpdateCodexAccountsHover(null);
            UpdateQuotaHover(null);
            _hoverTarget = HoverTarget.MiniReorder;
            Cursor = Cursors.SizeAll;
            EnsureAnimationTimer();
            Invalidate();
            UpdateHintHover(HoverTarget.MiniReorder);
            return;
        }
        _hoverReorderAreaId = null;
        var economyTarget = CodexEconomyTargetAt(logical);
        var systemUsageTarget = !economyTarget && SystemUsageTargetAt(logical);
        var miniAreaTarget = TaskbarCollapseTargetAt(logical);
        var miniCollapseTarget = miniAreaTarget is not null;
        _hoverMiniAreaId = miniAreaTarget?.AreaId;
        var radarTarget = !economyTarget && !systemUsageTarget && !miniCollapseTarget
            ? TaskbarRadarTargetAt(logical)
            : null;
        var codexAccountTarget = radarTarget is null && !systemUsageTarget && !miniCollapseTarget
            ? TaskbarCodexAccountTargetAt(logical)
            : null;
        var pluginTarget = radarTarget is null && codexAccountTarget is null
            && !systemUsageTarget && !miniCollapseTarget
            ? TaskbarPluginTargetAt(logical)
            : null;
        var pluginChanged = !string.Equals(
            _hoverPluginTarget?.Card.Card.Id,
            pluginTarget?.Card.Card.Id,
            StringComparison.Ordinal);
        _hoverPluginTarget = pluginTarget;
        if (pluginChanged && _hintPopover?.Visible == true) HideHintPopover();
        var quotaTarget = radarTarget is null && codexAccountTarget is null
            && pluginTarget is null && !miniCollapseTarget
            ? TaskbarQuotaTargetAt(logical)
            : null;
        if (economyTarget || systemUsageTarget || miniCollapseTarget) quotaTarget = null;
        UpdateSystemUsageHover(systemUsageTarget);
        UpdateRadarHover(radarTarget);
        UpdateCodexAccountsHover(codexAccountTarget);
        UpdateQuotaHover(quotaTarget);
        var next = _refreshBounds.Contains(logical)
                ? HoverTarget.Refresh
            : miniCollapseTarget
                ? HoverTarget.MiniCollapse
            : economyTarget
                ? HoverTarget.CodexEconomy
            : _settingsBounds.Contains(logical)
                ? HoverTarget.Settings
                : codexAccountTarget is not null
                    ? HoverTarget.CodexAccounts
                    : quotaTarget is not null
                        ? HoverTarget.QuotaWindow
                        : pluginTarget is not null
                            ? HoverTarget.Plugin
                        : radarTarget is not null
                            ? HoverTarget.Radar
                            : systemUsageTarget
                                ? HoverTarget.SystemUsage
                                : HoverTarget.None;
        if (next != _hoverTarget)
        {
            _hoverTarget = next;
            Cursor = next != HoverTarget.None ? Cursors.Hand : Cursors.SizeAll;
            EnsureAnimationTimer();
            Invalidate();
        }
        else Cursor = next != HoverTarget.None ? Cursors.Hand : Cursors.SizeAll;
        UpdateHintHover(next);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_taskbarDragging || _reorderingMiniAreaId is not null || _resizingMiniAreaId is not null) return;
        UpdateSystemUsageHover(false);
        UpdateRadarHover(null);
        UpdateCodexAccountsHover(null);
        UpdateQuotaHover(null);
        _hoverPluginTarget = null;
        _hoverMiniAreaId = null;
        _hoverReorderAreaId = null;
        _hoverResizeAreaId = null;
        UpdateHintHover(HoverTarget.None);
        _hoverTarget = HoverTarget.None;
        _pressedTarget = HoverTarget.None;
        Cursor = Cursors.Default;
        EnsureAnimationTimer();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            HidePopovers();
            return;
        }
        var logical = ToLogical(e.Location);
        if (TaskbarResizeTargetAt(logical) is { } resizeTarget)
        {
            BeginMiniAreaResize(resizeTarget, e.Location);
            return;
        }
        if (TaskbarReorderTargetAt(logical) is { } reorderTarget)
        {
            BeginMiniAreaReorder(reorderTarget, e.Location);
            return;
        }
        var pressedTarget = ControlTargetAt(logical);
        if (pressedTarget != HoverTarget.None)
        {
            _pressedTarget = pressedTarget;
            _pressedMiniAreaId = TaskbarCollapseTargetAt(logical)?.AreaId;
            Invalidate();
            return;
        }

        var now = DateTime.UtcNow;
        var doubleClick = now - _lastDragClickAt <= TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime)
            && Math.Abs(e.X - _lastDragClickPosition.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(e.Y - _lastDragClickPosition.Y) <= SystemInformation.DoubleClickSize.Height;
        _lastDragClickAt = now;
        _lastDragClickPosition = e.Location;
        if (doubleClick)
        {
            HidePopovers();
            _lastDragClickAt = DateTime.MinValue;
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _taskbarDragging = true;
        _taskbarDragMoved = false;
        _taskbarDragStartLocation = Location;
        _taskbarDragCurrentLocation = _taskbarDragStartLocation;
        _taskbarDragStartScreen = Cursor.Position;
        _dragTopologyKey = _placementCoordinator.ActiveTopology?.Key;
        _topologyChangedDuringDrag = false;
        _deferredTopologyDuringDrag = null;
        if (!_popoverPinned) HidePopovers();
        ClearHoverStateForTaskbarDrag();
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_resizingMiniAreaId is not null)
        {
            FinishMiniAreaResize(commit: true);
            return;
        }
        if (_reorderingMiniAreaId is not null)
        {
            FinishMiniAreaReorder(commit: true);
            return;
        }
        if (_pressedTarget != HoverTarget.None)
        {
            _pressedTarget = HoverTarget.None;
            _pressedMiniAreaId = null;
            Invalidate();
        }
        FinishTaskbarDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_pressedTarget != HoverTarget.None)
        {
            _pressedTarget = HoverTarget.None;
            _pressedMiniAreaId = null;
            Invalidate();
        }
        if (_resizingMiniAreaId is not null && !Capture)
        {
            FinishMiniAreaResize(commit: (Control.MouseButtons & MouseButtons.Left) == 0);
        }
        if (_reorderingMiniAreaId is not null && !Capture)
        {
            if ((Control.MouseButtons & MouseButtons.Left) != 0)
            {
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                FinishMiniAreaReorder(commit: true);
            }
        }
        if (_taskbarDragging && !Capture)
        {
            if ((Control.MouseButtons & MouseButtons.Left) != 0)
            {
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                FinishTaskbarDrag();
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (DateTime.UtcNow <= _suppressClickUntil) return;
        var logical = ToLogical(e.Location);
        if (TaskbarReorderTargetAt(logical) is not null) return;
        if (TaskbarCollapseTargetAt(logical) is { } miniArea)
        {
            ToggleMiniAreaCollapsed(miniArea.AreaId);
            return;
        }
        if (CodexEconomyTargetAt(logical))
        {
            ShowCodexEconomyMenu();
            return;
        }
        if (SystemUsageTargetAt(logical))
        {
            TogglePinnedSystemUsagePopover();
            return;
        }
        var radarTarget = TaskbarRadarTargetAt(logical);
        if (radarTarget is not null)
        {
            TogglePinnedRadarPopover(radarTarget);
            return;
        }
        var quotaTarget = TaskbarQuotaTargetAt(logical);
        if (quotaTarget is not null)
        {
            TogglePinnedQuotaPopover(quotaTarget);
            return;
        }
        HidePopovers();
        if (_refreshBounds.Contains(logical)) RefreshRequested?.Invoke(this, EventArgs.Empty);
        else if (_settingsBounds.Contains(logical)) SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BeginMiniAreaResize(MiniAreaTarget target, Point location)
    {
        HidePopovers();
        _resizingMiniAreaId = target.AreaId;
        _hoverResizeAreaId = target.AreaId;
        _resizeStartScreenX = PointToScreen(location).X;
        _resizeStartWidth = AreaLayout(target.AreaId).Width ?? AreaDefaultWidth(target.AreaId);
        _resizeStartLayout = AreaLayout(target.AreaId);
        _resizeMoved = false;
        Capture = true;
        Cursor = Cursors.SizeWE;
    }

    private void ContinueMiniAreaResize(MouseEventArgs e)
    {
        if (_resizingMiniAreaId is not { } areaId) return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishMiniAreaResize(commit: true);
            return;
        }
        var delta = (int)Math.Round((PointToScreen(e.Location).X - _resizeStartScreenX) / _scale);
        var width = TaskbarMiniLayoutMath.NormalizeAreaContentWidth(_resizeStartWidth + delta, areaId);
        if (width == (AreaLayout(areaId).Width ?? AreaDefaultWidth(areaId))) return;
        _resizeMoved = true;
        ApplyMiniAreaLayout(
            areaId,
            collapsed: null,
            width,
            preserveAnchor: true,
            commitPlacement: false);
        Cursor = Cursors.SizeWE;
    }

    private void FinishMiniAreaResize(bool commit)
    {
        if (_resizingMiniAreaId is not { } areaId) return;
        var previous = _resizeStartLayout ?? AreaLayout(areaId);
        var moved = _resizeMoved;
        _resizingMiniAreaId = null;
        _hoverResizeAreaId = null;
        _resizeStartLayout = null;
        _resizeMoved = false;
        if (Capture) Capture = false;
        Cursor = Cursors.Default;
        if (!commit)
        {
            _miniAreaLayouts[areaId] = previous;
            ApplySnapshotLayout();
            Invalidate();
            return;
        }
        if (!moved || AreaLayout(areaId) == previous) return;
        if (!_renderOnly) PreserveMiniProviderAreaAnchor(Location);
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(120);
        MiniAreaLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginMiniAreaReorder(MiniAreaTarget target, Point location)
    {
        HidePopovers();
        _reorderingMiniAreaId = target.AreaId;
        _reorderBeforeAreaId = null;
        _reorderStartScreen = PointToScreen(location);
        _reorderMoved = false;
        _hoverReorderAreaId = target.AreaId;
        Capture = true;
        Cursor = Cursors.SizeAll;
        Invalidate();
    }

    private void ContinueMiniAreaReorder(MouseEventArgs e)
    {
        if (_reorderingMiniAreaId is not { } areaId) return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishMiniAreaReorder(commit: true);
            return;
        }
        if (IsEscapeDown())
        {
            FinishMiniAreaReorder(commit: false);
            return;
        }

        var current = PointToScreen(e.Location);
        var deltaX = current.X - _reorderStartScreen.X;
        var deltaY = current.Y - _reorderStartScreen.Y;
        if (!_reorderMoved)
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(deltaX) < Math.Max(1, dragSize.Width / 2)
                && Math.Abs(deltaY) < Math.Max(1, dragSize.Height / 2))
            {
                return;
            }
            _reorderMoved = true;
            _lastDragClickAt = DateTime.MinValue;
        }

        var nextBefore = ReorderBeforeAreaAt(ToLogical(e.Location), areaId);
        if (!string.Equals(_reorderBeforeAreaId, nextBefore, StringComparison.Ordinal))
        {
            _reorderBeforeAreaId = nextBefore;
            Invalidate();
        }
        Cursor = Cursors.SizeAll;
    }

    private string? ReorderBeforeAreaAt(PointF point, string sourceAreaId) =>
        _taskbarAreaBounds
            .Where(target => target.Reorderable
                && !string.Equals(target.AreaId, sourceAreaId, StringComparison.Ordinal))
            .OrderBy(target => target.Bounds.Left)
            .FirstOrDefault(target => point.X < target.Bounds.Left + target.Bounds.Width / 2)
            ?.AreaId;

    private void FinishMiniAreaReorder(bool commit)
    {
        if (_reorderingMiniAreaId is not { } areaId) return;
        if (commit && IsEscapeDown()) commit = false;
        var moved = _reorderMoved;
        var beforeAreaId = _reorderBeforeAreaId;
        _reorderingMiniAreaId = null;
        _reorderBeforeAreaId = null;
        _reorderMoved = false;
        _hoverReorderAreaId = null;
        if (Capture) Capture = false;
        Cursor = Cursors.Default;
        if (!commit || !moved)
        {
            Invalidate();
            return;
        }
        if (!ApplyMiniAreaOrderMove(areaId, beforeAreaId, preserveAnchor: true))
        {
            Invalidate();
            return;
        }
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(180);
        MiniAreaOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ContinueTaskbarDrag(MouseEventArgs e)
    {
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishTaskbarDrag();
            return;
        }

        var currentScreen = Cursor.Position;
        var deltaX = currentScreen.X - _taskbarDragStartScreen.X;
        var deltaY = currentScreen.Y - _taskbarDragStartScreen.Y;
        if (!_taskbarDragMoved)
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(deltaX) < Math.Max(1, dragSize.Width / 2)
                && Math.Abs(deltaY) < Math.Max(1, dragSize.Height / 2))
            {
                return;
            }

            HidePopovers();
            ClearHoverStateForTaskbarDrag();
            TaskbarPlacement.InvalidateCache();
            _taskbarDragMoved = true;
            _lastDragClickAt = DateTime.MinValue;
        }

        var requested = new Point(
            _taskbarDragStartLocation.X + deltaX,
            _taskbarDragStartLocation.Y + deltaY);
        var preferredMonitor = PreferredTaskbarMonitor();
        if (TaskbarPlacement.TryGetDockTarget(
                Size,
                requested,
                currentScreen,
                preferredMonitor,
                out var dockedLocation,
                out var relativePosition,
                out var resolvedMonitor))
        {
            _taskbarDocked = true;
            _resolvedTaskbarMonitor = resolvedMonitor;
            _taskbarDragCurrentLocation = dockedLocation;
            if (!TaskbarPlacement.MoveAt(Handle, dockedLocation, Size)) Location = dockedLocation;
            return;
        }

        _taskbarDocked = false;
        _taskbarDragCurrentLocation = requested;
        if (!TaskbarPlacement.MoveAt(Handle, requested, Size)) Location = requested;
    }

    private string? PreferredTaskbarMonitor()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedTaskbarMonitor)) return _resolvedTaskbarMonitor;
        if (_placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile)
        {
            return null;
        }
        return _placementCoordinator.DockedPreference(topology, profile).PreferredMonitorName;
    }

    internal static bool CanCommitTaskbarDrag(
        string? dragTopologyKey,
        string? activeTopologyKey,
        bool topologyChangedDuringDrag,
        bool topologyRefreshPending,
        bool topologyMayHaveChangedPending,
        bool topologyCandidateChanged)
    {
        return !topologyChangedDuringDrag
            && (!topologyRefreshPending || !topologyMayHaveChangedPending)
            && !topologyCandidateChanged
            && dragTopologyKey is not null
            && string.Equals(dragTopologyKey, activeTopologyKey, StringComparison.Ordinal);
    }

    private void FinishTaskbarDrag()
    {
        if (!_taskbarDragging) return;
        var moved = _taskbarDragMoved;
        var deferredTopology = _deferredTopologyDuringDrag;
        var validTopology = CanCommitTaskbarDrag(
            _dragTopologyKey,
            _placementCoordinator.ActiveTopology?.Key,
            _topologyChangedDuringDrag,
            _topologyRefreshPending,
            _topologyMayHaveChangedPending,
            _pendingTopology is { } candidate
                && !string.Equals(_dragTopologyKey, candidate.Key, StringComparison.Ordinal));
        _taskbarDragging = false;
        _taskbarDragMoved = false;
        _dragTopologyKey = null;
        _topologyChangedDuringDrag = false;
        _deferredTopologyDuringDrag = null;
        if (Capture) Capture = false;
        if (!moved)
        {
            CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
            return;
        }

        HidePopovers();
        ClearHoverStateForTaskbarDrag();
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(250);
        if (!validTopology)
        {
            CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
            return;
        }
        if (_taskbarDocked)
        {
            if (!TaskbarPlacement.TryConstrain(
                    Size,
                    _taskbarDragCurrentLocation,
                    _resolvedTaskbarMonitor,
                    out var location,
                    out var relativePosition,
                    out var resolvedMonitor))
            {
                CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
                return;
            }
            _resolvedTaskbarMonitor = resolvedMonitor;
            if (!TaskbarPlacement.ShowAt(Handle, location, Size)) Location = location;
            var commit = _placementCoordinator.CommitDocked(resolvedMonitor, relativePosition, Size);
            if (commit is not null)
            {
                PlacementCommitted?.Invoke(this, commit);
                CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
            }
            else CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
            return;
        }

        var floatingLocation = ClampFloatingLocation(_taskbarDragCurrentLocation);
        if (!TaskbarPlacement.MoveAt(Handle, floatingLocation, Size)) Location = floatingLocation;
        var floatingCommit = _placementCoordinator.CommitFloating(new Rectangle(floatingLocation, Size));
        if (floatingCommit is not null)
        {
            PlacementCommitted?.Invoke(this, floatingCommit);
            CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
        }
        else CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
    }

    private void CompleteTaskbarDrag(
        DisplayTopologySnapshot? deferredTopology,
        bool restorePlacement)
    {
        if (deferredTopology is not null)
        {
            ActivateTopology(deferredTopology);
            return;
        }
        if (restorePlacement) RestoreActivePlacement();
    }

    private Point ClampFloatingLocation(Point requestedLocation)
    {
        var bounds = new Rectangle(requestedLocation, Size);
        var area = Screen.FromRectangle(bounds).WorkingArea;
        return new Point(
            Math.Clamp(requestedLocation.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(requestedLocation.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void RestoreActivePlacement()
    {
        if (_placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile)
        {
            return;
        }
        _taskbarDocked = profile.IsDocked;
        if (_taskbarDocked) SyncTaskbarPlacement();
        else MoveToFloatingPosition(topology, profile);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == TaskbarCreatedMessage
            || message.Msg is WmSettingChange or WmDisplayChange)
        {
            RequestTopologyRefresh(message.Msg == WmDisplayChange);
        }
    }

    private void UpdateHintHover(HoverTarget target)
    {
        if (_popoverPinned) return;
        var next = target is HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.MiniReorder or HoverTarget.Settings or HoverTarget.Plugin
            ? target
            : HoverTarget.None;
        if (_hoverHintTarget == next)
        {
            if (next != HoverTarget.None && _hintPopover?.Visible != true)
            {
                _popoverHoverTimer.Start();
            }
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverHintTarget = next;
        if (next == HoverTarget.None)
        {
            HideHintPopover();
            return;
        }

        HideSystemUsagePopover();
        HideRadarPopover();
        HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _popoverHoverTimer.Start();
    }

    private (string Title, string Detail)? HintFor(HoverTarget target)
    {
        if (target == HoverTarget.MiniCollapse)
        {
            var area = HoveredMiniArea();
            if (area is null) return null;
            return (area.Title, _text.MiniCardCollapseHint(area.Collapsed));
        }
        if (target == HoverTarget.MiniReorder)
        {
            var area = HoveredReorderArea();
            return area is null ? null : (area.Title, _text.MiniCardReorderHint);
        }
        if (target == HoverTarget.Settings)
        {
            return (_text.Settings, _text.OpenSettingsHint);
        }
        if (target == HoverTarget.Plugin && _hoverPluginTarget is { } plugin)
        {
            var lines = plugin.Card.Card.Summary
                .Take(6)
                .Select(item =>
                {
                    var label = plugin.Card.Text.TryGetValue(item.LabelKey, out var localized)
                        ? localized
                        : item.LabelKey;
                    return $"{label}: {PluginValue(item.Value)}";
                })
                .ToArray();
            return (
                plugin.Card.Title,
                lines.Length == 0 ? plugin.Card.PluginId : string.Join("\n", lines));
        }
        if (target != HoverTarget.Refresh) return null;

        var failures = _snapshot.Health
            .Where(health => _activeProviders.Contains(health.Provider))
            .Where(health => !health.Connected)
            .Select(health => _text.Health(health, DateTimeOffset.UtcNow))
            .ToArray();
        if (!_refreshing && failures.Length > 0)
        {
            return (_text.RefreshNow, string.Join("\n", failures));
        }

        var age = DateTimeOffset.UtcNow - _snapshot.CapturedAt;
        return (_text.RefreshNow, _refreshing ? _text.RefreshingLiveLimits : _text.RefreshUpdatedDetail(age));
    }

    private MiniQuotaTarget? TaskbarQuotaTargetAt(PointF point) =>
        _taskbarWindowBounds.FirstOrDefault(target => target.Bounds.Contains(point));

    private MiniRadarTarget? TaskbarRadarTargetAt(PointF point) =>
        _taskbarRadarBounds.FirstOrDefault(target => target.Bounds.Contains(point));

    private (RectangleF Bounds, PluginMiniCardView Card)? TaskbarPluginTargetAt(PointF point)
    {
        foreach (var target in _taskbarPluginBounds)
        {
            if (target.Bounds.Contains(point)) return target;
        }
        return null;
    }

    private (RectangleF Bounds, QuotaCard Card)? TaskbarCodexAccountTargetAt(PointF point)
    {
        foreach (var target in _taskbarCodexAccountBounds)
        {
            if (target.Bounds.Contains(point)) return target;
        }

        return null;
    }

    private bool SystemUsageTargetAt(PointF point) =>
        _systemUsageBounds.Contains(point);

    private void UpdateSystemUsageHover(bool hovered)
    {
        if (_popoverPinned) return;
        if (!hovered && _systemUsagePopover?.Visible == true)
        {
            _popoverLeaveStarted ??= DateTime.UtcNow;
            return;
        }
        if (_hoverSystemUsage == hovered)
        {
            if (hovered) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverSystemUsage = hovered;
        if (!hovered) return;
        HideQuotaPopover();
        HideRadarPopover();
        _hoverSystemUsage = true;
        SystemUsageDetailsRequested?.Invoke(this, EventArgs.Empty);
        _popoverHoverTimer.Start();
    }

    private void UpdateRadarHover(MiniRadarTarget? target)
    {
        if (_popoverPinned) return;
        if (target is null && _radarPopover?.Visible == true)
        {
            _popoverLeaveStarted ??= DateTime.UtcNow;
            return;
        }
        var previousId = _hoverRadarTarget?.Id;
        var nextId = target?.Id;
        if (target is not null
            && _radarPopover?.Visible == true
            && string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            _hoverRadarTarget = target;
            _popoverLeaveStarted = null;
            return;
        }
        if (string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            if (target is not null) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverRadarTarget = target;
        if (target is null) return;
        HideSystemUsagePopover();
        HideQuotaPopover();
        _hoverRadarTarget = target;
        _popoverHoverTimer.Start();
    }

    private void UpdateCodexAccountsHover((RectangleF Bounds, QuotaCard Card)? target)
    {
        if (_popoverPinned) return;
        var previousKey = _hoverCodexAccountTarget?.Card.Key;
        var nextKey = target?.Card.Key;
        if (target is not null
            && _codexAccountsPopover?.Visible == true
            && string.Equals(previousKey, nextKey, StringComparison.Ordinal))
        {
            _hoverCodexAccountTarget = target;
            return;
        }
        if (string.Equals(previousKey, nextKey, StringComparison.Ordinal)) return;

        _popoverHoverTimer.Stop();
        _hoverCodexAccountTarget = target;
        EnsureAnimationTimer();
        Invalidate();
        if (target is null)
        {
            HideCodexAccountsPopover();
            return;
        }

        HideSystemUsagePopover();
        HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _popoverHoverTimer.Start();
    }

    private void UpdateQuotaHover(MiniQuotaTarget? target)
    {
        if (_popoverPinned) return;
        var previousId = _hoverQuotaTarget?.Id;
        var nextId = target?.Id;
        if (target is not null
            && _quotaPopover?.Visible == true
            && string.Equals(_popoverTargetId, nextId, StringComparison.Ordinal))
        {
            _hoverQuotaTarget = target;
            _popoverLeaveStarted = null;
            return;
        }
        if (string.Equals(previousId, nextId, StringComparison.Ordinal))
        {
            if (target is not null) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverQuotaTarget = target;
        EnsureAnimationTimer();
        Invalidate();
        if (target is null) return;
        HideSystemUsagePopover();
        HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _hoverQuotaTarget = target;
        _popoverHoverTimer.Start();
    }

    private void TogglePinnedSystemUsagePopover()
    {
        if (_popoverPinned && _systemUsagePopover?.Visible == true && _hoverSystemUsage)
        {
            HideSystemUsagePopover();
            return;
        }
        ShowSystemUsagePopover(pinned: true);
    }

    private void ShowCodexEconomyMenu()
    {
        CodexEconomyStatusRefreshRequested?.Invoke(this, EventArgs.Empty);
        _codexEconomyMenu?.Dispose();
        var menu = CreateCodexEconomyMenu();
        _codexEconomyMenu = menu;
        menu.Show(this, PointToClient(Cursor.Position));
    }

    private ContextMenuStrip CreateCodexEconomyMenu()
    {
        var available = _codexEconomyStatus is not null
            && _codexEconomyStatus.Mode != CodexEconomyMode.Inconsistent;
        var menuWidth = Scale(232);
        var headerHeight = Scale(42);
        var itemHeight = Scale(48);
        var actionHeight = Scale(32);
        var surface = _backgroundTheme.Popover;
        var hover = MixColor(surface, Color.FromArgb(37, 55, 82), .84f);
        var border = MixColor(surface, Color.FromArgb(100, 116, 139), .62f);
        var text = Color.FromArgb(226, 232, 240);
        var muted = Color.FromArgb(148, 163, 184);
        var titleFont = new Font("Segoe UI", 10.5f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
        var descriptionFont = new Font("Segoe UI", 9f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var renderer = new CodexEconomyMenuRenderer(surface, hover, border, text, muted, _scale);
        var menu = new ContextMenuStrip
        {
            AutoSize = false,
            Width = menuWidth,
            BackColor = surface,
            ForeColor = text,
            Font = titleFont,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(Scale(4)),
            Renderer = renderer,
            AccessibleName = _text.CodexEconomyBarMenuTitle,
        };
        menu.Disposed += (_, _) =>
        {
            titleFont.Dispose();
            descriptionFont.Dispose();
        };
        menu.Items.Add(new CodexEconomyMenuHeaderItem(
            _text.CodexEconomyBarMenuTitle,
            _text.CodexEconomyBarMenuHint,
            titleFont,
            descriptionFont)
        {
            AutoSize = false,
            Width = menuWidth - menu.Padding.Horizontal,
            Height = headerHeight,
            Tag = "bar.economy.header",
        });
        menu.Items.Add(new ToolStripSeparator());
        if (!available)
        {
            menu.Items.Add(new ToolStripMenuItem(_text.CodexEconomyStatusSummary(_codexEconomyStatus))
            {
                AutoSize = false,
                Width = menuWidth - menu.Padding.Horizontal,
                Height = actionHeight,
                Enabled = false,
            });
            menu.Items.Add(new ToolStripSeparator());
        }
        foreach (var mode in new[] { CodexEconomyMode.Off, CodexEconomyMode.Ask, CodexEconomyMode.On })
        {
            var current = _codexEconomyStatus?.Mode == mode;
            var accent = mode switch
            {
                CodexEconomyMode.On => Color.FromArgb(52, 211, 153),
                CodexEconomyMode.Ask => Color.FromArgb(251, 191, 36),
                _ => Color.FromArgb(148, 163, 184),
            };
            var item = new CodexEconomyModeMenuItem(
                mode,
                _text.CodexEconomyModeName(mode),
                _text.CodexEconomyBarModeDescription(mode),
                accent,
                current,
                titleFont,
                descriptionFont)
            {
                AutoSize = false,
                Width = menuWidth - menu.Padding.Horizontal,
                Height = itemHeight,
                Enabled = available,
                Tag = $"bar.economy.{mode.ToString().ToLowerInvariant()}",
            };
            item.Click += (_, _) => DismissCodexEconomyMenuAndRequestMode(menu, mode);
            menu.Items.Add(item);
        }
        if (!available)
        {
            menu.Items.Add(new ToolStripSeparator());
            var settings = new ToolStripMenuItem(_text.SettingsTitle)
            {
                AutoSize = false,
                Width = menuWidth - menu.Padding.Horizontal,
                Height = actionHeight,
            };
            settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(settings);
        }
        menu.Height = menu.Items.Cast<ToolStripItem>().Sum(item => item.Height) + menu.Padding.Vertical;
        void ApplyRegion() => renderer.ApplyRoundedRegion(menu);
        menu.HandleCreated += (_, _) => ApplyRegion();
        menu.SizeChanged += (_, _) => ApplyRegion();
        ApplyRegion();
        return menu;
    }

    private void DismissCodexEconomyMenuAndRequestMode(
        ContextMenuStrip menu,
        CodexEconomyMode mode)
    {
        menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            CodexEconomyModeRequested?.Invoke(this, new CodexEconomyModeRequest(mode));
        }));
    }

    private void ShowSystemUsagePopover(bool pinned)
    {
        var needsDetails = _systemUsage.TopProcesses.Count == 0;
        _popoverHoverTimer.Stop();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true || _popoverTargetId is not null) HideQuotaPopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        _systemUsagePopover ??= new SystemUsagePopoverForm();
        _systemUsagePopover.ApplyTheme(_backgroundTheme);
        _systemUsagePopover.ShowFor(
            this,
            QuotaTargetScreenBounds(_systemUsageBounds),
            new SystemUsagePopoverContent(_systemUsage, pinned),
            _text,
            _scale,
            _animationsEnabled);
        _hoverSystemUsage = true;
        _hoverRadarTarget = null;
        _popoverTargetId = null;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
        if (needsDetails) SystemUsageDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MonitorSystemUsagePopover()
    {
        if (_systemUsagePopover?.Visible != true || !_hoverSystemUsage)
        {
            _popoverStateTimer.Stop();
            return;
        }

        var cursor = Cursor.Position;
        var anchor = QuotaTargetScreenBounds(_systemUsageBounds);
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_systemUsagePopover.Bounds.Contains(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideSystemUsagePopover();
            return;
        }

        if (anchor.Contains(cursor) || _systemUsagePopover.Bounds.Contains(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }

        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150))
        {
            HideSystemUsagePopover();
        }
    }

    private void ShowHoveredPopover()
    {
        if (_hoverSystemUsage)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(_systemUsageBounds).Contains(Cursor.Position)) return;
            ShowSystemUsagePopover(pinned: false);
            return;
        }
        if (_hoverRadarTarget is { } radarTarget)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(radarTarget.Bounds).Contains(Cursor.Position)) return;
            ShowRadarPopover(radarTarget, pinned: false);
            return;
        }
        if (_hoverCodexAccountTarget is { } codexAccountTarget)
        {
            _popoverHoverTimer.Stop();
            if (!QuotaTargetScreenBounds(codexAccountTarget.Bounds).Contains(Cursor.Position)) return;
            ShowCodexAccountsPopover(codexAccountTarget);
            return;
        }
        if (_hoverHintTarget is HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.Settings or HoverTarget.Plugin)
        {
            var hintTarget = _hoverHintTarget;
            _popoverHoverTimer.Stop();
            var hintBounds = hintTarget switch
            {
                HoverTarget.Refresh => _refreshBounds,
                HoverTarget.MiniCollapse when HoveredMiniArea() is { } area => area.HandleBounds,
                HoverTarget.Plugin when _hoverPluginTarget is { } plugin => plugin.Bounds,
                _ => _settingsBounds,
            };
            if (!QuotaTargetScreenBounds(hintBounds).Contains(Cursor.Position)) return;
            ShowHintPopover(hintTarget);
            return;
        }
        ShowHoveredQuotaPopover();
    }

    private void ShowCodexAccountsPopover((RectangleF Bounds, QuotaCard Card) target)
    {
        _popoverHoverTimer.Stop();
        if (_popoverPinned) return;
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _codexAccountsPopover ??= new CodexAccountsPopoverForm();
        _codexAccountsPopover.ApplyTheme(_backgroundTheme);
        _codexAccountsPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(target.Bounds),
            _codexAccounts,
            target.Card,
            _text,
            _scale,
            _animationsEnabled,
            _snapshot.CodexAccounts);
        Invalidate();
    }

    private void ShowHintPopover(HoverTarget target)
    {
        _popoverHoverTimer.Stop();
        if (_popoverPinned) return;
        var content = HintFor(target);
        if (content is null) return;
        HideSystemUsagePopover();
        HideRadarPopover();
        HideCodexAccountsPopover();
        if (_quotaPopover?.Visible == true) HideQuotaPopover();
        _hintPopover ??= new TaskbarHintPopoverForm();
        _hintPopover.ApplyTheme(_backgroundTheme);
        var anchor = target switch
        {
            HoverTarget.Refresh => _refreshBounds,
            HoverTarget.MiniCollapse when HoveredMiniArea() is { } area => area.HandleBounds,
            HoverTarget.MiniReorder when HoveredReorderArea() is { } area => area.ReorderBounds,
            HoverTarget.Plugin when _hoverPluginTarget is { } plugin => plugin.Bounds,
            _ => _settingsBounds,
        };
        _hintPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(anchor),
            content.Value.Title,
            content.Value.Detail,
            _scale,
            _animationsEnabled);
        Invalidate();
    }

    private void ShowHoveredQuotaPopover()
    {
        _popoverHoverTimer.Stop();
        if (_popoverPinned || _hoverQuotaTarget is not { } target) return;
        if (!QuotaTargetScreenBounds(target.Bounds).Contains(Cursor.Position)) return;
        ShowQuotaPopover(target, pinned: false);
    }

    private void TogglePinnedQuotaPopover(MiniQuotaTarget target)
    {
        if (_popoverPinned && string.Equals(_popoverTargetId, target.Id, StringComparison.Ordinal))
        {
            HideQuotaPopover();
            return;
        }
        ShowQuotaPopover(target, pinned: true);
    }

    private void ShowQuotaPopover(MiniQuotaTarget target, bool pinned)
    {
        _popoverHoverTimer.Stop();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_radarPopover?.Visible == true || _hoverRadarTarget is not null) HideRadarPopover();
        _quotaPopover ??= new QuotaPopoverForm();
        _quotaPopover.ApplyTheme(_backgroundTheme);
        var anchor = QuotaTargetScreenBounds(target.Bounds);
        var logo = ProviderLogo(target.Card.Provider);
        _quotaPopover.ShowFor(
            this,
            anchor,
            new QuotaPopoverContent(
                target.Card,
                target.Window,
                QuotaDisplayFormatting.WeeklyBlockReset(
                    target.Card,
                    target.Window,
                    DateTimeOffset.UtcNow),
                target.Card.CapturedAt ?? _snapshot.CapturedAt,
                _quotaPaceEstimates.GetValueOrDefault(target.PaceKey),
                pinned,
                target.Card.Provider == ProviderKind.AiGateway ? _aiGatewayUsage : null,
                target.Card.Provider == ProviderKind.Codex
                    ? _codexQuotaTokenSummaries.GetValueOrDefault(target.PaceKey)
                    : null),
            _text,
            logo,
            _resetClockIcon,
            _scale,
            _animationsEnabled);
        _popoverTargetId = target.Id;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    private void MonitorQuotaPopover()
    {
        if (_quotaPopover?.Visible != true || _popoverTargetId is null)
        {
            _popoverStateTimer.Stop();
            return;
        }

        var cursor = Cursor.Position;
        var anchor = ResolveQuotaTarget(_popoverTargetId) is { } target
            ? QuotaTargetScreenBounds(target.Bounds)
            : Rectangle.Empty;
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_quotaPopover.Bounds.Contains(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideQuotaPopover();
            return;
        }

        if (anchor.Contains(cursor) || _quotaPopover.Bounds.Contains(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }

        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150)) HideQuotaPopover();
    }

    private void TogglePinnedRadarPopover(MiniRadarTarget target)
    {
        var sameVisibleTarget = _radarPopover?.Visible == true
            && string.Equals(_hoverRadarTarget?.Id, target.Id, StringComparison.Ordinal);
        if (_popoverPinned && sameVisibleTarget)
        {
            HideRadarPopover();
            return;
        }
        ShowRadarPopover(target, pinned: true, requestRefresh: !sameVisibleTarget);
    }

    private void ShowRadarPopover(
        MiniRadarTarget target,
        bool pinned,
        bool requestRefresh = true)
    {
        _popoverHoverTimer.Stop();
        if (_codexAccountsPopover?.Visible == true) HideCodexAccountsPopover();
        if (_hintPopover?.Visible == true || _hoverHintTarget != HoverTarget.None) HideHintPopover();
        if (_systemUsagePopover?.Visible == true || _hoverSystemUsage) HideSystemUsagePopover();
        if (_quotaPopover?.Visible == true || _popoverTargetId is not null) HideQuotaPopover();
        var radarEnabled = _radarProviders.Contains(target.SourceProvider);
        if (requestRefresh && radarEnabled)
        {
            RadarPreviewRequested?.Invoke(
                this,
                new RadarPreviewRequest(target.SourceProvider, target.SurfaceId));
        }
        if (_radarPopover is null)
        {
            _radarPopover = new ProviderRadarPopoverForm();
            _radarPopover.SpendHistoryRequested += (_, _) => PinVisibleRadarPopoverForHistory();
        }
        var tokenUsage = target.DeepSeekOnly
            ? null
            : target.SourceProvider == ProviderKind.Codex
                ? _codexTokenUsage
                : null;
        var aiGatewayUsage = target.DeepSeekOnly ? _aiGatewayUsage : null;
        _radarPopover.ShowFor(
            this,
            QuotaTargetScreenBounds(target.Bounds),
            _radarState,
            _text,
            ProviderLogo(target.Provider),
            _scale,
            _animationsEnabled,
            pinned,
            radarEnabled,
            tokenUsage,
            aiGatewayUsage,
            target.DeepSeekOnly);
        _hoverRadarTarget = target;
        _popoverTargetId = null;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    private void PinVisibleRadarPopoverForHistory()
    {
        if (_hoverRadarTarget is not { } target) return;
        ShowRadarPopover(
            ResolveRadarTarget(target.Id) ?? target,
            pinned: true,
            requestRefresh: false);
    }

    private void MonitorPopover()
    {
        if (_systemUsagePopover?.Visible == true && _hoverSystemUsage)
        {
            MonitorSystemUsagePopover();
            return;
        }
        if (_radarPopover?.Visible == true && _hoverRadarTarget is not null)
        {
            MonitorRadarPopover();
            return;
        }
        MonitorQuotaPopover();
    }

    private void MonitorRadarPopover()
    {
        if (_hoverRadarTarget is not { } target || _radarPopover?.Visible != true)
        {
            _popoverStateTimer.Stop();
            return;
        }
        var currentTarget = ResolveRadarTarget(target.Id);
        if (currentTarget is null)
        {
            HideRadarPopover();
            return;
        }
        _hoverRadarTarget = currentTarget;
        var cursor = Cursor.Position;
        var anchor = QuotaTargetScreenBounds(currentTarget.Bounds);
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        if (_popoverPinned)
        {
            var outsideClick = mouseDown
                && !_popoverMouseWasDown
                && !anchor.Contains(cursor)
                && !_radarPopover.Bounds.Contains(cursor);
            var escapePressed = escapeDown && !_popoverEscapeWasDown;
            _popoverMouseWasDown = mouseDown;
            _popoverEscapeWasDown = escapeDown;
            if (outsideClick || escapePressed) HideRadarPopover();
            return;
        }
        if (anchor.Contains(cursor) || _radarPopover.Bounds.Contains(cursor))
        {
            _popoverLeaveStarted = null;
            return;
        }
        _popoverLeaveStarted ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _popoverLeaveStarted >= TimeSpan.FromMilliseconds(150))
        {
            HideRadarPopover();
        }
    }

    private void RefreshQuotaPopover()
    {
        if (_quotaPopover?.Visible != true || _popoverTargetId is null) return;
        var target = ResolveQuotaTarget(_popoverTargetId);
        if (target is null)
        {
            HideQuotaPopover();
            return;
        }
        ShowQuotaPopover(target, _popoverPinned);
    }

    private void RefreshCodexAccountsPopover()
    {
        if (_codexAccountsPopover?.Visible != true || _hoverCodexAccountTarget is not { } target) return;
        ShowCodexAccountsPopover(target);
    }

    private void RefreshHintPopover()
    {
        if (_hintPopover?.Visible != true) return;
        if (_hoverHintTarget is not (HoverTarget.Refresh or HoverTarget.MiniCollapse or HoverTarget.Settings or HoverTarget.Plugin)) return;
        ShowHintPopover(_hoverHintTarget);
    }

    private void RepositionPopovers()
    {
        if (_systemUsagePopover?.Visible == true) ShowSystemUsagePopover(_popoverPinned);
        if (_quotaPopover?.Visible == true) RefreshQuotaPopover();
        if (_codexAccountsPopover?.Visible == true) RefreshCodexAccountsPopover();
        if (_radarPopover?.Visible == true && _hoverRadarTarget is { } target)
        {
            ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
    }

    private MiniRadarTarget? ResolveRadarTarget(string id) =>
        _taskbarRadarBounds.FirstOrDefault(target =>
            string.Equals(target.Id, id, StringComparison.Ordinal));

    private MiniQuotaTarget? ResolveQuotaTarget(string id)
    {
        var x = TaskbarMiniLayoutMath.OuterPadding;
        foreach (var area in _visibleTaskbarAreas)
        {
            var areaWidth = TaskbarAreaWidth(area);
            if (area.Group is not { } group)
            {
                x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
                continue;
            }
            var layout = AreaLayout(area.AreaId);
            var cardBounds = TaskbarAreaContentBounds(new RectangleF(x, 4, areaWidth, 36));
            if (layout.Collapsed)
            {
                x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
                continue;
            }
            if (group.IsCodexPool)
            {
                foreach (var target in TaskbarCodexPoolTargetRows(
                             cardBounds,
                             group.Cards,
                             DateTimeOffset.UtcNow)
                             .SelectMany(row => row.Targets))
                {
                    if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                }
            }
            else if (group.IsStackedCodex)
            {
                for (var index = 0; index < group.Cards.Count; index++)
                {
                    var rowBounds = TaskbarCodexCapsuleBounds(cardBounds, index, group.Cards.Count);
                    if (group.Cards.Count > 2)
                    {
                        var target = TaskbarCompactCodexRowTarget(group.Cards[index], rowBounds);
                        if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                        continue;
                    }
                    foreach (var target in TaskbarCodexRowTargets(group.Cards[index], rowBounds))
                    {
                        if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                    }
                }
            }
            else
            {
                foreach (var target in TaskbarQuotaTargets(group.Cards[0], cardBounds))
                {
                    if (string.Equals(target.Id, id, StringComparison.Ordinal)) return target;
                }
            }
            x += areaWidth + TaskbarMiniLayoutMath.ModuleGap;
        }
        return null;
    }

    private Rectangle QuotaTargetScreenBounds(RectangleF logicalBounds)
    {
        var topLeft = PointToScreen(new Point(
            (int)Math.Round(logicalBounds.Left * _scale),
            (int)Math.Round(logicalBounds.Top * _scale)));
        var bottomRight = PointToScreen(new Point(
            (int)Math.Round(logicalBounds.Right * _scale),
            (int)Math.Round(logicalBounds.Bottom * _scale)));
        return Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    private void HideQuotaPopover()
    {
        _popoverHoverTimer.Stop();
        _popoverStateTimer.Stop();
        _quotaPopover?.HidePopover();
        _popoverPinned = false;
        _hoverQuotaTarget = null;
        _popoverTargetId = null;
        _popoverLeaveStarted = null;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideCodexAccountsPopover()
    {
        _codexAccountsPopover?.HidePopover();
        _hoverCodexAccountTarget = null;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideHintPopover()
    {
        _hintPopover?.HidePopover();
        _hoverHintTarget = HoverTarget.None;
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HideRadarPopover()
    {
        var ownsPin = _hoverRadarTarget is not null && _popoverTargetId is null;
        _popoverHoverTimer.Stop();
        _radarPopover?.HidePopover();
        if (ownsPin) _popoverPinned = false;
        _hoverRadarTarget = null;
        _popoverLeaveStarted = null;
        if (_quotaPopover?.Visible != true) _popoverStateTimer.Stop();
    }

    private void HideSystemUsagePopover()
    {
        var ownsPin = _hoverSystemUsage
            && _popoverTargetId is null
            && _hoverRadarTarget is null;
        _popoverHoverTimer.Stop();
        _systemUsagePopover?.HidePopover();
        if (ownsPin) _popoverPinned = false;
        _hoverSystemUsage = false;
        _popoverLeaveStarted = null;
        if (_quotaPopover?.Visible != true && _radarPopover?.Visible != true)
        {
            _popoverStateTimer.Stop();
        }
        EnsureAnimationTimer();
        Invalidate();
    }

    private void HidePopovers()
    {
        HideSystemUsagePopover();
        HideQuotaPopover();
        HideCodexAccountsPopover();
        HideHintPopover();
        HideRadarPopover();
    }

    private void ClearHoverStateForTaskbarDrag()
    {
        _hoverTarget = HoverTarget.None;
        _hoverReorderAreaId = null;
        Cursor = Cursors.SizeAll;
        EnsureAnimationTimer();
        Invalidate();
    }

    private static bool IsEscapeDown() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    private void ApplySnapshotLayout()
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
        if (_showCodexEconomyBar)
        {
            defaultAreas.Add(TaskbarMiniAreaContent.ForCodexEconomy(_text.CodexEconomyBarAreaTitle));
        }
        if (_showSystemMetrics)
        {
            defaultAreas.Add(TaskbarMiniAreaContent.ForSystem(_text.SystemUsageTitle));
        }
        if (ShouldShowRadarResetArea())
        {
            var codexIndex = defaultAreas.FindLastIndex(area =>
                area.Group?.Cards.Any(card => card.Provider == ProviderKind.Codex) == true);
            defaultAreas.Insert(
                codexIndex >= 0 ? codexIndex + 1 : Math.Max(0, defaultAreas.Count - 1),
                TaskbarMiniAreaContent.ForRadarReset(_text.RadarResetMiniAreaTitle));
        }
        var effectiveOrder = MiniAreaOrderWithRadarDefault(_miniAreaOrder);
        _taskbarContentAreas = OrderTaskbarContentAreas(defaultAreas, effectiveOrder)
            .ToArray();
        _visibleTaskbarAreas = SelectVisibleTaskbarAreas(_taskbarContentAreas);
        _visibleCards = _visibleTaskbarAreas
            .Where(area => area.Group is not null)
            .SelectMany(area => area.Group!.Cards)
            .ToArray();
        _hiddenCardCount = Math.Max(0, layoutCards.Length - _visibleCards.Length)
            + Math.Max(0, _pluginMiniCards.Length - _visibleTaskbarAreas.Count(area => area.Plugin is not null));
        var miniLogicalWidth = TaskbarMiniLayoutMath.ModuleContentWidth(
            _visibleTaskbarAreas.Select(TaskbarAreaWidth)
                .ToArray(),
            _hiddenCardCount > 0);
        ClientSize = new Size(Scale(miniLogicalWidth), Scale(TaskbarMiniLayoutMath.Height));
        UpdateWindowRegion();
        if (!_renderOnly)
        {
            if (_taskbarDocked)
            {
                if (_resizingMiniAreaId is null) SyncTaskbarPlacement();
            }
            else RestoreActiveFloatingPosition();
        }
    }

    private void StartSnapshotAnimation(QuotaSnapshot previous, QuotaSnapshot next)
    {
        if (!_animationsEnabled)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            return;
        }

        var from = SnapshotValues(previous);
        var to = SnapshotValues(next);
        var changedKeys = to
            .Where(item => from.TryGetValue(item.Key, out var value)
                && Math.Abs(value - item.Value) >= .05)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (changedKeys.Count == 0)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            if (!_refreshing) _animationTimer.Stop();
            return;
        }

        _animationFrom = from;
        _animatedWindowKeys = changedKeys;
        _snapshotAnimationStarted = DateTime.UtcNow;
        _snapshotAnimating = true;
        EnsureAnimationTimer();
    }

    private double? AnimatedUsed(string cardKey, QuotaWindow window)
    {
        if (window.UsedPercent is not { } target || !_snapshotAnimating) return window.UsedPercent;
        if (!_animationFrom.TryGetValue(WindowKey(cardKey, window.Label), out var from)) return target;
        var progress = Math.Clamp(
            (DateTime.UtcNow - _snapshotAnimationStarted).TotalMilliseconds / SnapshotValueAnimationMs,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        return from + (target - from) * eased;
    }

    private float SnapshotPulse(string windowKey)
    {
        if (!_snapshotAnimating || !_animatedWindowKeys.Contains(windowKey)) return 0;
        var progress = Math.Clamp(
            (DateTime.UtcNow - _snapshotAnimationStarted).TotalMilliseconds / SnapshotPulseAnimationMs,
            0,
            1);
        return (float)Math.Sin(progress * Math.PI);
    }

    private void AdvanceAnimation()
    {
        if (_refreshing) _refreshRotation = (_refreshRotation + 10) % 360;
        if (_snapshotAnimating && DateTime.UtcNow - _snapshotAnimationStarted >= TimeSpan.FromMilliseconds(SnapshotPulseAnimationMs))
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
        }

        var hoverAnimating = AdvanceHoverAnimation();

        Invalidate();
        if (!_refreshing && !_snapshotAnimating && !hoverAnimating) _animationTimer.Stop();
    }

    private void EnsureAnimationTimer()
    {
        if (_animationsEnabled && !_animationTimer.Enabled) _animationTimer.Start();
    }

    private RadarResetWindow? CurrentRadarResetWindow() =>
        _radarState.Snapshot is { Provider: ProviderKind.Codex } snapshot
            ? snapshot.ResetWindow
            : null;

    private bool ShouldShowRadarResetArea() =>
        _radarProviders.Contains(ProviderKind.Codex)
        && CurrentRadarResetWindow()?.Open == true;

    private void RefreshRadarResetClock()
    {
        if (_radarPopover?.Visible == true
            && _hoverRadarTarget is { SourceProvider: ProviderKind.Codex } target
            && RadarResetTiming.Resolve(CurrentRadarResetWindow()).Kind == RadarResetTimingKind.EstimatedDate)
        {
            ShowRadarPopover(target, _popoverPinned, requestRefresh: false);
        }
        UpdateRadarResetTimer();
        Invalidate();
    }

    private void UpdateRadarResetTimer()
    {
        var interval = ShouldShowRadarResetArea()
            ? RadarResetTiming.RefreshIntervalMilliseconds(CurrentRadarResetWindow(), _utcNow())
            : null;
        if (interval is null)
        {
            _radarResetTimer.Stop();
            return;
        }
        if (_radarResetTimer.Interval != interval.Value)
        {
            _radarResetTimer.Stop();
            _radarResetTimer.Interval = interval.Value;
        }
        if (!_radarResetTimer.Enabled) _radarResetTimer.Start();
    }

    private bool AdvanceHoverAnimation()
    {
        var animating = false;
        foreach (var target in AnimatedHoverTargets)
        {
            var current = _hoverProgress.GetValueOrDefault(target);
            var destination = _hoverTarget == target ? 1f : 0f;
            var next = current + (destination - current) * HoverAnimationStep;
            if (Math.Abs(destination - next) < .01f) next = destination;
            _hoverProgress[target] = next;
            if (next != destination) animating = true;
        }
        return animating;
    }

    private float HoverProgress(HoverTarget target) => _animationsEnabled
        ? _hoverProgress.GetValueOrDefault(target)
        : _hoverTarget == target ? 1 : 0;

    private RectangleF PressedIconBounds(RectangleF bounds, HoverTarget target)
    {
        if (_pressedTarget == target) bounds.Offset(0, .7f);
        return bounds;
    }

    private HoverTarget ControlTargetAt(PointF point)
    {
        if (_refreshBounds.Contains(point)) return HoverTarget.Refresh;
        if (_settingsBounds.Contains(point)) return HoverTarget.Settings;
        if (TaskbarCollapseTargetAt(point) is not null) return HoverTarget.MiniCollapse;
        return HoverTarget.None;
    }

    private MiniAreaTarget? TaskbarCollapseTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target => target.HandleBounds.Contains(point));

    private bool CodexEconomyTargetAt(PointF point) =>
        !_codexEconomyBounds.IsEmpty && _codexEconomyBounds.Contains(point);

    private MiniAreaTarget? TaskbarReorderTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target =>
            target.Reorderable && target.ReorderBounds.Contains(point));

    private MiniAreaTarget? TaskbarResizeTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target => target.ResizeBounds.Contains(point));

    private MiniAreaTarget? HoveredMiniArea() => _hoverMiniAreaId is null
        ? null
        : _taskbarAreaBounds.FirstOrDefault(target =>
            string.Equals(target.AreaId, _hoverMiniAreaId, StringComparison.Ordinal));

    private MiniAreaTarget? HoveredReorderArea() => _hoverReorderAreaId is null
        ? null
        : _taskbarAreaBounds.FirstOrDefault(target =>
            string.Equals(target.AreaId, _hoverReorderAreaId, StringComparison.Ordinal));

    private static Dictionary<string, double> SnapshotValues(QuotaSnapshot snapshot)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is { } used)
                {
                    values[WindowKey(card.Key, window.Label)] = used;
                }
            }
        }

        return values;
    }

    private static string WindowKey(string cardKey, string windowLabel) => $"{cardKey}\0{windowLabel}";

    private void RestorePosition(AppSettings settings)
    {
        if (_taskbarDocked) return;
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            Location = new Point(x, y);
            ClampToVisibleScreen();
            return;
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Scale(16));
    }

    private void UpdateDpiAndRegion()
    {
        _scale = Math.Max(1, EffectiveDpi() / 96f);
        UpdateWindowRegion();
    }

    private int EffectiveDpi() => _renderDpi ?? DeviceDpi;

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedRectangle(
            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            Scale(8));
        Region?.Dispose();
        Region = new Region(path);
    }

    private PointF ToLogical(Point point) => new(point.X / _scale, point.Y / _scale);
    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawString(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment)
    {
        graphics.DrawString(text, font, brush, bounds, _textFormats.For(alignment));
    }

    private static float MeasureWidth(Graphics graphics, string text, Font font) =>
        graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;

    private static Color MixColor(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static string FormatPercent(double percent)
    {
        if (percent <= 0) return "0%";
        if (percent < 1) return "<1%";
        if (percent < 10) return $"{Math.Round(percent, 1):0.#}%";
        return $"{Math.Round(percent):0}%";
    }

    private static string CompactUsage(double? percent)
    {
        if (percent is null) return "--";
        if (percent < 1 && percent > 0) return "<1%";
        return $"{Math.Round(Math.Clamp(percent.Value, 0, 100)):0}%";
    }

    private static string CompactRemaining(double? percent)
    {
        if (percent is null) return "—";
        if (percent < 1 && percent > 0) return "<1%";
        return $"{Math.Round(Math.Clamp(percent.Value, 0, 100)):0}%";
    }

    private static Color UsageColor(double usage) => usage switch
    {
        >= 90 => Color.FromArgb(251, 113, 133),
        >= 70 => Color.FromArgb(251, 191, 36),
        _ => Color.FromArgb(52, 211, 153),
    };

    private static Image LoadEmbeddedImage(string logicalName)
    {
        using var stream = typeof(BarForm).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Missing embedded image resource: {logicalName}");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
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
        return TaskbarMiniLayoutMath.AreaWidth(layout.Width ?? defaultWidth, layout.Collapsed);
    }

    private int PluginMiniCardWidth(PluginMiniCardView view)
    {
        var layout = AreaLayout(view.PluginId);
        var defaultWidth = view.Card.Kind is ContributionKind.Balance or ContributionKind.Metric
            ? TaskbarMiniLayoutMath.ServiceCardWidth
            : TaskbarMiniLayoutMath.CardWidth;
        return TaskbarMiniLayoutMath.AreaWidth(layout.Width ?? defaultWidth, layout.Collapsed);
    }

    private int TaskbarAreaWidth(TaskbarMiniAreaContent area)
    {
        if (area.Group is { } group) return TaskbarGroupWidth(group);
        if (area.Plugin is { } plugin) return PluginMiniCardWidth(plugin);
        var layout = AreaLayout(area.AreaId);
        if (string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal))
        {
            return TaskbarMiniLayoutMath.AreaWidth(
                TaskbarMiniLayoutMath.CodexEconomyContentWidth,
                layout.Collapsed,
                area.AreaId);
        }
        return TaskbarMiniLayoutMath.AreaWidth(
            layout.Width ?? area.DefaultWidth,
            layout.Collapsed,
            area.AreaId);
    }

    private MiniAreaLayout AreaLayout(string areaId)
    {
        var normalized = _miniAreaLayouts.TryGetValue(areaId, out var layout)
            ? layout.Normalized(areaId)
            : new MiniAreaLayout();
        return string.Equals(areaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
            ? normalized with { Width = null }
            : normalized;
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

    private static IReadOnlyList<string> MiniAreaOrderWithRadarDefault(
        IReadOnlyList<string> order)
    {
        if (order.Contains(MiniAreaIds.RadarReset, StringComparer.Ordinal)) return order;
        var effectiveOrder = order.ToList();
        var codexIndex = effectiveOrder.FindLastIndex(areaId =>
            string.Equals(areaId, MiniAreaIds.Codex, StringComparison.Ordinal)
            || areaId.StartsWith($"{MiniAreaIds.Codex}.", StringComparison.Ordinal));
        if (codexIndex >= 0) effectiveOrder.Insert(codexIndex + 1, MiniAreaIds.RadarReset);
        return effectiveOrder;
    }

    private static TaskbarMiniAreaContent[] SelectVisibleTaskbarAreas(
        IReadOnlyList<TaskbarMiniAreaContent> areas)
    {
        var visibleIds = areas
            .Where(area => !string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal)
                && !string.Equals(area.AreaId, MiniAreaIds.RadarReset, StringComparison.Ordinal)
                && !string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal))
            .Take(TaskbarMiniLayoutMath.MaximumCards)
            .Select(area => area.AreaId)
            .ToHashSet(StringComparer.Ordinal);
        visibleIds.Add(MiniAreaIds.RadarReset);
        visibleIds.Add(MiniAreaIds.CodexEconomy);
        visibleIds.Add(MiniAreaIds.SystemMetrics);
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

    private static string PluginMonogram(string title)
    {
        var characters = title
            .Where(char.IsLetterOrDigit)
            .Take(2)
            .ToArray();
        return characters.Length == 0 ? "P" : new string(characters).ToUpperInvariant();
    }

    private static string PluginValue(ContributionValue value) =>
        value.Kind == "currency" && value.Decimal is { } currency
            ? value.Text switch
            {
                "CNY" => $"¥{currency:0.00}",
                "USD" => $"${currency:0.00}",
                { Length: > 0 } code => $"{code} {currency:0.00}",
                _ => currency.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            }
        : value.Kind == "percent" && value.Number is { } percent
            ? $"{percent:0.#}%"
        : value.Text
        ?? value.Decimal?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Number?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Integer?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        ?? value.Boolean?.ToString()
        ?? value.Timestamp?.ToLocalTime().ToString(
            "HH:mm",
            System.Globalization.CultureInfo.CurrentCulture)
        ?? "—";

    private Image ProviderLogo(ProviderKind provider) => provider switch
    {
        ProviderKind.Claude => _claudeLogo,
        ProviderKind.AiGateway => _deepSeekLogo,
        _ => _openAiLogo,
    };

    private static Font CreateSystemIconFont(float size = 14f)
    {
        const string fluent = "Segoe Fluent Icons";
        var familyName = FontFamily.Families.Any(family =>
            string.Equals(family.Name, fluent, StringComparison.OrdinalIgnoreCase))
            ? fluent
            : "Segoe MDL2 Assets";
        return new Font(familyName, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_topologyCaptureSync)
            {
                _topologyCaptureDisposed = true;
                _topologyCaptureActive = false;
                _topologyCaptureRequested = false;
                _topologyCaptureGeneration++;
            }
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _radarResetTimer.Stop();
            _radarResetTimer.Dispose();
            _popoverHoverTimer.Stop();
            _popoverHoverTimer.Dispose();
            _popoverStateTimer.Stop();
            _popoverStateTimer.Dispose();
            _shellSettleTimer.Stop();
            _shellSettleTimer.Dispose();
            _quotaPopover?.Dispose();
            _codexAccountsPopover?.Dispose();
            _hintPopover?.Dispose();
            _radarPopover?.Dispose();
            _systemUsagePopover?.Dispose();
            _codexEconomyMenu?.Dispose();
            _titleFont.Dispose();
            _subtitleFont.Dispose();
            _cardTitleFont.Dispose();
            _badgeFont.Dispose();
            _rowFont.Dispose();
            _resetFont.Dispose();
            _overflowFont.Dispose();
            _accountOrdinalFont.Dispose();
            _systemIconFont.Dispose();
            _miniSystemIconFont.Dispose();
            _taskbarControlIconFont.Dispose();
            _textFormats.Dispose();
            _systemIconFormat.Dispose();
            _claudeLogo.Dispose();
            _openAiLogo.Dispose();
            _deepSeekLogo.Dispose();
            _resetClockIcon.Dispose();
            foreach (var icon in _pluginIcons.Values) icon.Dispose();
            _pluginIcons.Clear();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    private sealed record MiniQuotaTarget(
        RectangleF Bounds,
        QuotaCard Card,
        QuotaWindow Window,
        string? IdOverride = null)
    {
        public string Id { get; } = IdOverride ?? WindowKey(Card.Key, Window.Label);
        public string PaceKey { get; } = QuotaPaceTracker.SeriesKey(Card, Window);
    }

    private sealed record CodexPoolTargetRow(
        CodexPoolRow Row,
        RectangleF Bounds,
        RectangleF ValueBounds,
        IReadOnlyList<MiniQuotaTarget> Targets);

    private sealed record MiniRadarTarget(
        RectangleF Bounds,
        string Id,
        ProviderKind Provider,
        ProviderKind SourceProvider,
        bool DeepSeekOnly,
        string SurfaceId);

    private sealed record MiniAreaTarget(
        string AreaId,
        string Title,
        RectangleF Bounds,
        RectangleF HandleBounds,
        RectangleF ReorderBounds,
        RectangleF ResizeBounds,
        bool Collapsed,
        bool Reorderable);

    private sealed record MiniAreaDefinition(string AreaId, string Title, int DefaultWidth);

    private readonly record struct TaskbarHitTargetCounts(
        int Cards,
        int Windows,
        int Radar,
        int Plugins,
        int CodexAccounts);

    private sealed record TaskbarMiniAreaContent(
        string AreaId,
        string Title,
        int DefaultWidth,
        TaskbarMiniCardGroup? Group,
        PluginMiniCardView? Plugin)
    {
        public static TaskbarMiniAreaContent ForGroup(TaskbarMiniCardGroup group) => new(
            TaskbarGroupAreaId(group),
            group.IsCodexPool ? "Codex" : group.Cards[0].DisplayLabel,
            group.IsCodexPool
                ? TaskbarMiniLayoutMath.CodexPoolCardWidth
                : group.IsStackedCodex
                    ? TaskbarMiniLayoutMath.CardWidth
                    : TaskbarMiniLayoutMath.CardWidthFor(group.Cards[0]),
            group,
            null);

        public static TaskbarMiniAreaContent ForPlugin(PluginMiniCardView plugin) => new(
            plugin.PluginId,
            plugin.Title,
            plugin.Card.Kind is ContributionKind.Balance or ContributionKind.Metric
                ? TaskbarMiniLayoutMath.ServiceCardWidth
                : TaskbarMiniLayoutMath.CardWidth,
            null,
            plugin);

        public static TaskbarMiniAreaContent ForSystem(string title) => new(
            MiniAreaIds.SystemMetrics,
            title,
            TaskbarMiniLayoutMath.SystemUsageContentWidth,
            null,
            null);

        public static TaskbarMiniAreaContent ForCodexEconomy(string title) => new(
            MiniAreaIds.CodexEconomy,
            title,
            TaskbarMiniLayoutMath.CodexEconomyContentWidth,
            null,
            null);

        public static TaskbarMiniAreaContent ForRadarReset(string title) => new(
            MiniAreaIds.RadarReset,
            title,
            TaskbarMiniLayoutMath.RadarResetContentWidth,
            null,
            null);
    }

    private enum HoverTarget
    {
        None,
        Refresh,
        MiniCollapse,
        MiniReorder,
        Settings,
        CodexEconomy,
        Plugin,
        QuotaWindow,
        CodexAccounts,
        Radar,
        SystemUsage,
    }
}

internal sealed record PluginMiniCardView(
    string PluginId,
    string Title,
    MiniCardContribution Card,
    IReadOnlyDictionary<string, string> Text,
    byte[]? IconPng = null);

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            graphics.FillRectangle(brush, bounds);
            return;
        }
        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
