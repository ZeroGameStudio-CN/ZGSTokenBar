using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm : Form
{
    private const float TaskbarControlIconSize = 12f;
    private const float MiniResetGlyphSize = 9f;
    private const double SnapshotPulseAnimationMs = 560;
    private const float HoverAnimationStep = .32f;
    private static readonly HoverTarget[] AnimatedHoverTargets =
        [
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
    private readonly System.Windows.Forms.Timer _popoverHoverTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _popoverStateTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _shellSettleTimer = new() { Interval = ShellSettleIntervalMs };
    private readonly WinEventDelegate _foregroundChangedHandler;
    private readonly List<(RectangleF Bounds, QuotaCard Card)> _cardBounds = [];
    private readonly List<MiniQuotaTarget> _taskbarWindowBounds = [];
    private readonly List<(RectangleF Bounds, PluginMiniCardView Card)> _taskbarPluginBounds = [];
    private readonly List<(RectangleF Bounds, QuotaCard Card)> _taskbarCodexAccountBounds = [];
    private readonly HashSet<string> _taskbarRenderFailureAreaIds = new(StringComparer.Ordinal);
    private readonly Dictionary<HoverTarget, float> _hoverProgress = [];
    private HashSet<string> _animatedWindowKeys = new(StringComparer.Ordinal);
    private QuotaSnapshot _snapshot;
    private HashSet<ProviderKind> _activeProviders;
    private PluginMiniCardView[] _pluginMiniCards = [];
    private readonly Dictionary<string, Image> _pluginIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _pluginIconBytes = new(StringComparer.Ordinal);
    private Dictionary<string, MiniAreaLayout> _miniAreaLayouts;
    private string[] _miniAreaOrder;
    private bool _showSystemMetrics;
    private CodexAccountInfo[] _codexAccounts;
    private TaskbarMiniAreaContent[] _taskbarContentAreas = [];
    private TaskbarMiniAreaContent[] _visibleTaskbarAreas = [];
    private RectangleF _systemUsageBounds;
    private RectangleF _liveMetricsBounds;
    private bool _systemAnchorIsLive;
    private RectangleF SystemUsageAnchorBounds => _systemAnchorIsLive ? _liveMetricsBounds : _systemUsageBounds;
    private HoverTarget _hoverTarget;
    private bool _refreshing;
    private bool _snapshotAnimating;
    private bool _taskbarDocked;
    internal event EventHandler? OverviewRequested;
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
    private bool _hiddenToTray;
    private nint _foregroundChangedHook;
    private int _taskbarSyncQueued;
    private bool _popoverPinned;
    private bool _popoverMouseWasDown;
    private bool _popoverEscapeWasDown;
    private DateTime? _popoverLeaveStarted;
    private MiniQuotaTarget? _hoverQuotaTarget;
    private (RectangleF Bounds, QuotaCard Card)? _hoverCodexAccountTarget;
    private (RectangleF Bounds, PluginMiniCardView Card)? _hoverPluginTarget;
    private HoverTarget _hoverHintTarget;
    private bool _hoverSystemUsage;
    private string? _popoverTargetId;
    private QuotaPopoverForm? _quotaPopover;
    private CodexAccountsPopoverForm? _codexAccountsPopover;
    private TaskbarHintPopoverForm? _hintPopover;
    private SystemUsagePopoverForm? _systemUsagePopover;
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

    internal void RequestUsageSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
    public event EventHandler<string>? PluginActivated;
    public event EventHandler<WindowPlacementCommit>? PlacementCommitted;
    public event EventHandler? SystemUsageDetailsRequested;
    internal SystemUsageSnapshot LiveMetrics => _systemUsage;
    internal long? DisplayedTokenTotal => _codexTokenUsage?.LocalTokens;

    public BarForm(
        AppSettings settings,
        QuotaSnapshot snapshot,
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
        _showSystemMetrics = settings.IsPluginEnabled("zgstokenbar.metrics.system", true);
        _glassMaxRings = NormalizeGlassMax(settings.GlassDockMaxRings);
        _hiddenDockItems = new HashSet<string>(AppSettings.CopyHiddenDockItems(settings.HiddenDockItems), StringComparer.Ordinal);
        _glassExpanded = settings.GlassDockExpanded;
        _animationsEnabled = settings.EnableAnimations && SystemInformation.IsMenuAnimationEnabled;
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
        _popoverHoverTimer.Tick += (_, _) => ShowHoveredPopover();
        _popoverStateTimer.Tick += (_, _) => MonitorPopover();
        _shellSettleTimer.Tick += (_, _) =>
        {
            _shellSettleTimer.Stop();
            HandleShellSettleTick();
        };
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(28, 22, 36);
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
        ApplyTaskbarEmbedSetting(settings.EmbedInTaskbar);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindowStyle | NoActivateStyle;
            if (GlassLayered) parameters.ExStyle |= LayeredWindow.ExStyle;
            return parameters;
        }
    }

    public bool IsTaskbarMode => true;
    public bool IsTaskbarDocked => _taskbarDocked;
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
        _showSystemMetrics && !_systemAnchorIsLive && (_hoverSystemUsage || _systemUsagePopover?.Visible == true);
    /// <summary>True while the pointer is on a bar item or any bar hover/popover is showing.</summary>
    public bool HasOpenHoverOrPopover =>
        _hoverTarget != HoverTarget.None || _hoverSystemUsage
        || _quotaPopover?.Visible == true || _codexAccountsPopover?.Visible == true
        || _hintPopover?.Visible == true || _systemUsagePopover?.Visible == true
        || _pluginPopover?.Visible == true;
    internal void SetGlassExpandedForAcceptance(bool expanded) => SetGlassExpanded(expanded);

    /// <summary>Renders the hover popover a glass ring opens, or null when the ring has no scoped quota card.</summary>
    internal Bitmap? RenderGlassRingPopoverForAcceptance(string providerId, int dpi, DateTimeOffset now)
    {
        var item = BuildGlassItems().FirstOrDefault(entry => string.Equals(entry.Ring.ProviderId, providerId, StringComparison.Ordinal));
        if (item?.Card is not { } card || item.Window is not { } window) return null;
        using var popover = new QuotaPopoverForm();
        return popover.RenderForTest(
            new QuotaPopoverContent(WithSubscription(card), window, null, card.CapturedAt ?? now, null, false),
            _text,
            item.Logo ?? ProviderLogo(card.Provider),
            _resetClockIcon,
            dpi,
            now);
    }
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
        ApplySnapshotLayout();
        RefreshCodexAccountsPopover();
        Invalidate();
    }

    internal RectangleF SystemUsageBoundsForAcceptance => _renderOnly
        ? _systemUsageBounds
        : throw new InvalidOperationException("System usage hit testing is render-only.");

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
        _systemUsage = _systemUsage with { CumulativeTokens = summary?.LocalTokens };
        Invalidate();
    }

    public void SetPluginMiniCards(IReadOnlyList<PluginMiniCardView> cards)
    {
        var ordered = cards
            .OrderBy(card => card.Card.Order)
            .ThenBy(card => card.Card.Id, StringComparer.Ordinal)
            .ToArray();
        if (_pluginMiniCards.SequenceEqual(ordered)) return;
        var layoutChanged = !_pluginMiniCards.Select(card => (card.PluginId, card.Card.Id, card.Card.Order, card.Card.Kind))
            .SequenceEqual(ordered.Select(card => (card.PluginId, card.Card.Id, card.Card.Order, card.Card.Kind)));

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
        ApplySnapshotLayout(updatePlacement: layoutChanged);
        Invalidate();
    }

    internal MiniCardContribution? RouterCard => _pluginMiniCards.FirstOrDefault(card => card.PluginId == MiniAreaIds.Router)?.Card;
    internal bool IsChinese => _text.IsChinese;
    internal float UiScale => _scale;
    internal QuotaBackgroundTheme UiTheme => _backgroundTheme;
    internal long? DisplayedRouterQueuedCount => _pluginMiniCards.FirstOrDefault(card => card.PluginId == MiniAreaIds.Router)
        ?.Card.Summary.FirstOrDefault(item => item.LabelKey == "router.queued")?.Value.Integer;
    internal string? DisplayedRouterState => _pluginMiniCards.FirstOrDefault(card => card.PluginId == MiniAreaIds.Router)
        ?.Card.Summary.FirstOrDefault(item => item.LabelKey == "router.state")?.Status;

    public void SetAiGatewayUsage(AiGatewayUsageSummary? summary)
    {
        _aiGatewayUsage = summary;
        Invalidate();
    }

    public void SetLiveOutputRate(LiveOutputRate rate)
    {
        var first = _systemUsage.OutputRateCapturedAt is null;
        _systemUsage = _systemUsage with { ObservedOutputTokensPerSecond = rate.Total, OutputRateCapturedAt = rate.CapturedAt, LiveRate = rate };
        if (first) ApplySnapshotLayout();
        Invalidate();
    }

    private SystemUsagePopoverContent SystemPopoverContent(SystemUsageSnapshot snapshot, bool pinned) =>
        new(snapshot, pinned, _systemAnchorIsLive)
        {
            Conversations = _systemAnchorIsLive ? LiveConversationRows(snapshot) : null,
            TodayTokens = _systemAnchorIsLive ? _codexTokenUsage?.TodayTokens : null,
            HourlyTokens = _systemAnchorIsLive ? HourlyTokens(DateTimeOffset.Now) : null,
            Cache = _systemAnchorIsLive && _codexTokenUsage is { } usage ? CacheStats(usage, DateTimeOffset.Now) : null,
            Activity = _systemAnchorIsLive && _codexTokenUsage is { } tokens
                ? TokenActivityCalendar.FromDailyTotals(
                    tokens.ActivityDays
                        .Where(day => DateOnly.TryParse(day.LocalDate, out _))
                        .GroupBy(day => DateOnly.Parse(day.LocalDate))
                        .ToDictionary(group => group.Key, group => group.Sum(day => day.Spend.TotalTokens)),
                    DateOnly.FromDateTime(DateTime.Now),
                    tokens.LocalTokens)
                : null,
        };

    private (DateTimeOffset Hour, CodexTokenUsageSummary? Usage, long[] Buckets)? _hourlyTokensCache;

    /// <summary>
    /// Tokens per local hour for the last 24 hours from the retained Codex turns (input + output),
    /// oldest first. Cached per hour and turn count, so hover refreshes cost nothing.
    /// </summary>
    private long[] HourlyTokens(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        if (_hourlyTokensCache is { } cached && cached.Hour == hour && ReferenceEquals(cached.Usage, _codexTokenUsage))
            return cached.Buckets;
        var buckets = BucketHourlyTokens(_codexTokenUsage?.ConversationTurns ?? [], hour);
        _hourlyTokensCache = (hour, _codexTokenUsage, buckets);
        return buckets;
    }

    /// <summary>Today's prompt-cache read/write (ledger read, retained turns' writes) and hit rates.</summary>
    internal static TokenCacheStats CacheStats(CodexTokenUsageSummary usage, DateTimeOffset now)
    {
        var today = now.Date;
        long? write = null;
        foreach (var turn in usage.ConversationTurns)
        {
            if (turn.ObservedAt.ToOffset(now.Offset).Date != today || turn.CacheWriteInputTokens is not { } tokens) continue;
            write = (write ?? 0) + Math.Max(0, tokens);
        }
        return new TokenCacheStats(usage.TodayCachedInputTokens, write, usage.TodayCacheHitPercent, usage.TotalCacheHitPercent);
    }

    internal static long[] BucketHourlyTokens(IReadOnlyList<CodexConversationTurn> turns, DateTimeOffset currentHour)
    {
        var buckets = new long[24];
        var start = currentHour.AddHours(-23);
        foreach (var turn in turns)
        {
            if (turn.ObservedAt < start || turn.ObservedAt >= currentHour.AddHours(1)) continue;
            var index = (int)Math.Floor((turn.ObservedAt - start).TotalHours);
            if (index is < 0 or > 23) continue;
            buckets[index] += Math.Max(0, turn.OutputTokens) + Math.Max(0, turn.InputTokens ?? 0);
        }
        return buckets;
    }

    /// <summary>Codex conversations joined with their live per-conversation output rates and taskbar ring identity.</summary>
    private IReadOnlyList<CodexConversationTurn> LiveConversationRows(SystemUsageSnapshot snapshot)
    {
        if (_codexTokenUsage is not { } usage || !_activeProviders.Contains(ProviderKind.Codex)) return [];
        var now = DateTimeOffset.UtcNow;
        return CodexConversationDisplay.Select(usage.ConversationTurns,
            snapshot.CurrentOutputRate is not null ? snapshot.LiveRate : null, now, _snapshot);
    }

    public void SetSystemUsage(SystemUsageSnapshot snapshot)
    {
        snapshot = snapshot with { ObservedOutputTokensPerSecond = _systemUsage.ObservedOutputTokensPerSecond, OutputRateCapturedAt = _systemUsage.OutputRateCapturedAt, LiveRate = _systemUsage.LiveRate, CumulativeTokens = _codexTokenUsage?.LocalTokens };
        _systemUsage = snapshot;
        if (_systemUsagePopover?.Visible == true)
        {
            var previousHeight = _systemUsagePopover.CurrentLogicalBodyHeight;
            _systemUsagePopover.UpdateContent(SystemPopoverContent(snapshot, _popoverPinned), _text);
            if (previousHeight != _systemUsagePopover.CurrentLogicalBodyHeight)
            {
                RepositionPopovers();
            }
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

    public void ApplySettings(AppSettings settings)
    {
        _text = NativeText.For(settings.Locale);
        _backgroundTheme = QuotaBackgroundPalette.Resolve(settings.BackgroundPalette);
        BackColor = Color.FromArgb(28, 22, 36);
        _quotaPopover?.ApplyTheme(_backgroundTheme);
        _systemUsagePopover?.ApplyTheme(_backgroundTheme);
        _hintPopover?.ApplyTheme(_backgroundTheme);
        var previousDocked = _taskbarDocked;
        var previousMiniAreaLayouts = _miniAreaLayouts;
        var previousMiniAreaOrder = _miniAreaOrder;
        var previousShowSystemMetrics = _showSystemMetrics;
        _miniAreaLayouts = AppSettings.CopyMiniAreaLayouts(settings.MiniAreaLayouts);
        _miniAreaOrder = AppSettings.CopyMiniAreaOrder(settings.MiniAreaOrder);
        _showSystemMetrics = settings.IsPluginEnabled("zgstokenbar.metrics.system", true);
        _glassMaxRings = NormalizeGlassMax(settings.GlassDockMaxRings);
        _hiddenDockItems = new HashSet<string>(AppSettings.CopyHiddenDockItems(settings.HiddenDockItems), StringComparer.Ordinal);
        _glassExpanded = settings.GlassDockExpanded;
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
            || previousShowSystemMetrics != _showSystemMetrics)
        {
            HidePopovers();
        }
        _animationsEnabled = settings.EnableAnimations && SystemInformation.IsMenuAnimationEnabled;
        if (!_animationsEnabled)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            _hoverProgress.Clear();
            _animationTimer.Stop();
            _refreshRotation = 0;
        }
        ApplySnapshotLayout();
        RepositionPopovers();
        ApplyTaskbarEmbedSetting(settings.EmbedInTaskbar);
        if (_taskbarDocked) SyncTaskbarPlacement();
        else
        {
            RestoreActiveFloatingPosition();
            if (!Visible && !_hiddenToTray) Show();
        }
        if (activation?.MigrationCommit is { } migration) PlacementCommitted?.Invoke(this, migration);
        RefreshHintPopover();
        Invalidate();
        ScheduleEmbedHealthCheck("settings");
    }

    internal string? ProbeFirstDockItemId => BuildGlassItems().Select(item => item.Id).FirstOrDefault(id => !_hiddenDockItems.Contains(id));

    private System.Windows.Forms.Timer? _embedHealthTimer;

    /// <summary>
    /// After a settings apply (or a click), re-verify the embedded window a moment later: still a
    /// visible child of the taskbar, raised above Explorer's content, and showing a fresh frame.
    /// </summary>
    private void ScheduleEmbedHealthCheck(string reason)
    {
        if (_renderOnly || IsDisposed) return;
        _embedHealthTimer ??= new System.Windows.Forms.Timer { Interval = 450 };
        _embedHealthTimer.Stop();
        _embedHealthTimer.Tag = reason;
        _embedHealthTimer.Tick -= EmbedHealthTick;
        _embedHealthTimer.Tick += EmbedHealthTick;
        _embedHealthTimer.Start();
    }

    private void EmbedHealthTick(object? sender, EventArgs e)
    {
        _embedHealthTimer?.Stop();
        if (IsDisposed || !IsHandleCreated || _hiddenToTray) return;
        var reason = _embedHealthTimer?.Tag as string ?? "";
        if (_taskbarDocked && !Visible && !TaskbarPlacement.ShouldHideForFullscreen()) Show();
        if (_embedRequested && _taskbarDocked && !_taskbarDragging)
        {
            if (_embedParent == 0 || TaskbarEmbedding.ParentOf(Handle) != _embedParent) SyncTaskbarPlacement();
            else TaskbarEmbedding.RaiseChild(Handle);
        }
        if (GlassLayered) RepushLayeredFrame();
        PerfTrace.Note($"embed.health {reason} visible={Visible} embedded={EmbeddedInTaskbar} status={TaskbarEmbedStatus}");
    }

    private static bool IsEscapeDown() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    private PointF ToLogical(Point point) => new(point.X / _scale, point.Y / _scale);
    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeTaskbarEmbedding();
            lock (_topologyCaptureSync)
            {
                _topologyCaptureDisposed = true;
                _topologyCaptureActive = false;
                _topologyCaptureRequested = false;
                _topologyCaptureGeneration++;
            }
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _popoverHoverTimer.Stop();
            _popoverHoverTimer.Dispose();
            _popoverStateTimer.Stop();
            _popoverStateTimer.Dispose();
            _shellSettleTimer.Stop();
            _shellSettleTimer.Dispose();
            _quotaPopover?.Dispose();
            _codexAccountsPopover?.Dispose();
            _hintPopover?.Dispose();
            _systemUsagePopover?.Dispose();
            _glassMenu?.Dispose();
            foreach (var icon in _glassIcons.Values) icon?.Dispose();
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
        string? IdOverride = null,
        Image? Logo = null)
    {
        public string Id { get; } = IdOverride ?? WindowKey(Card.Key, Window.Label);
        public string PaceKey { get; } = QuotaPaceTracker.SeriesKey(Card, Window);
    }

    private sealed record MiniAreaDefinition(string AreaId, string Title, int DefaultWidth);

    private readonly record struct TaskbarHitTargetCounts(
        int Cards,
        int Windows,
        int Plugins,
        int CodexAccounts);

    private sealed record TaskbarMiniAreaContent(
        string AreaId,
        string Title,
        int DefaultWidth,
        IReadOnlyList<QuotaCard>? Cards,
        PluginMiniCardView? Plugin)
    {
        /// <summary>Area for one provider's cards: several Codex accounts share one stacked area.</summary>
        public static TaskbarMiniAreaContent ForCards(string areaId, IReadOnlyList<QuotaCard> cards) => new(
            areaId,
            cards[0].DisplayLabel,
            cards.Count >= 2 ? TaskbarMiniLayoutMath.CardWidth : TaskbarMiniLayoutMath.CardWidthFor(cards[0]),
            cards,
            null);

        public static TaskbarMiniAreaContent ForPlugin(PluginMiniCardView plugin) => new(
            plugin.PluginId,
            plugin.Title,
            plugin.PluginId == MiniAreaIds.Router ? RouterCardRenderer.Width : plugin.Card.Kind is ContributionKind.Balance or ContributionKind.Metric
                ? TaskbarMiniLayoutMath.ServiceCardWidth
                : TaskbarMiniLayoutMath.CardWidth,
            null,
            plugin);

        public static TaskbarMiniAreaContent ForLiveMetrics(string title) => new(
            MiniAreaIds.LiveMetrics, title, 104, null, null);

        public static TaskbarMiniAreaContent ForSystem(string title) => new(
            MiniAreaIds.SystemMetrics,
            title,
            TaskbarMiniLayoutMath.SystemUsageContentWidth,
            null,
            null);

    }

    private enum HoverTarget
    {
        None,
        Plugin,
        QuotaWindow,
        CodexAccounts,
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
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius) =>
        UiPaint.FillRounded(graphics, brush, bounds, radius);
}
