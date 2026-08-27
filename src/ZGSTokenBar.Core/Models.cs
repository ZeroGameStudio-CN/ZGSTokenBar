using System.Drawing;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public enum ProviderKind
{
    Claude,
    Codex,
    AiGateway,
}

public enum AiGatewayBalanceStatus
{
    Available,
    Unavailable,
    Stale,
    Unknown,
}

public enum Sub2ApiPoolStatus
{
    Available,
    Unavailable,
    Stale,
    Unknown,
}

public enum Sub2ApiUsageStatus
{
    Available,
    Unavailable,
    Stale,
    Unknown,
}

public enum Sub2ApiQuotaStatus
{
    Available,
    Unavailable,
    Stale,
    Unknown,
}

public enum Sub2ApiAccountAvailabilityCoverage
{
    Complete,
    Partial,
    None,
}

public enum Sub2ApiAccountAvailabilityState
{
    Available,
    Unavailable,
}

public sealed record AiGatewayBalance(
    AiGatewayBalanceStatus Status,
    string Currency,
    decimal? TotalBalance,
    decimal? ToppedUpBalance,
    decimal? GrantedBalance,
    DateTimeOffset? ObservedAt);

public sealed record Sub2ApiPoolAvailability(
    Sub2ApiPoolStatus Status,
    int? AvailableAccounts,
    int? TotalAccounts,
    int? RateLimitedAccounts,
    int? ErrorAccounts,
    int? FreeConcurrency,
    int? MaxConcurrency,
    DateTimeOffset? ObservedAt);

public sealed record Sub2ApiUsageSummary(
    Sub2ApiUsageStatus Status,
    long? TodayRequests,
    long? TodayInputTokens,
    long? TodayOutputTokens,
    long? TodayCacheCreationTokens,
    long? TodayCacheReadTokens,
    long? TodayTokens,
    long? TotalRequests,
    long? TotalInputTokens,
    long? TotalOutputTokens,
    long? TotalCacheCreationTokens,
    long? TotalCacheReadTokens,
    long? TotalTokens,
    DateTimeOffset? ObservedAt);

public sealed record Sub2ApiQuotaSummary(
    Sub2ApiQuotaStatus Status,
    int? AccountCount,
    int? FiveHourAccountCount,
    double? FiveHourRemainingPercent,
    double? FiveHourRemainingAccountEquivalents,
    int? SevenDayAccountCount,
    double? SevenDayRemainingPercent,
    double? SevenDayRemainingAccountEquivalents,
    DateTimeOffset? ObservedAt);

public sealed record Sub2ApiAccountAvailabilityEntry(
    int Slot,
    Sub2ApiAccountAvailabilityState State,
    double? RemainingPercent);

public sealed record Sub2ApiAccountAvailabilitySummary(
    Sub2ApiQuotaStatus Status,
    Sub2ApiAccountAvailabilityCoverage Coverage,
    int? EligibleAccountCount,
    int? ReadableAccountCount,
    double? AggregateRemainingPercent,
    double? RemainingAccountEquivalents,
    IReadOnlyList<Sub2ApiAccountAvailabilityEntry>? Accounts,
    DateTimeOffset? ObservedAt);

public sealed record Sub2ApiQuotaWindow(
    string Label,
    int AccountCount,
    double RemainingPercent,
    double RemainingAccountEquivalents);

public sealed record AiGatewayUsagePeriod(
    long RequestCount,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    long CacheUnknownTokens,
    decimal? CacheHitRatePercent,
    decimal EstimatedCostCny);

public sealed record AiGatewayUsageSummary(
    string Currency,
    AiGatewayBalanceStatus Status,
    AiGatewayUsagePeriod Today,
    AiGatewayUsagePeriod Total,
    DateTimeOffset ObservedAt,
    string DayBoundary = "UTC")
{
    public AiGatewayUsageSummary AsStale() =>
        Status == AiGatewayBalanceStatus.Stale
            ? this
            : this with { Status = AiGatewayBalanceStatus.Stale };
}

public enum ProviderHealthCode
{
    Unknown,
    Current,
    Cached,
    Loading,
    Waiting,
    MissingCredentials,
    EndpointBlocked,
    OAuthExpired,
    OAuthRefreshFailed,
    RateLimited,
    HttpError,
    Timeout,
    Unavailable,
}

public sealed record QuotaWindow(
    string Label,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan Duration);

public sealed record QuotaCard(
    string Key,
    ProviderKind Provider,
    string Label,
    string? Badge,
    string Accent,
    bool Active,
    IReadOnlyList<QuotaWindow> Windows)
{
    public DateTimeOffset? CapturedAt { get; init; }
    public string? AccountHint { get; init; }
    public bool IsService { get; init; }
    public int ServiceCount { get; init; }
    public string? ServiceDisplayName { get; init; }
    public AiGatewayBalance? Balance { get; init; }
    public Sub2ApiPoolAvailability? Sub2ApiPool { get; init; }
    public Sub2ApiUsageSummary? Sub2ApiUsage { get; init; }
    public Sub2ApiQuotaSummary? Sub2ApiQuota { get; init; }
    public Sub2ApiAccountAvailabilitySummary? Sub2ApiAccountAvailability { get; init; }
    public string DisplayLabel => IsService && !string.IsNullOrWhiteSpace(ServiceDisplayName)
        ? ServiceDisplayName
        : Label;
}

public sealed record CodexAccountQuota(
    string AccountId,
    IReadOnlyList<QuotaWindow> Windows,
    DateTimeOffset? CapturedAt = null,
    string? Error = null)
{
    public string? CardKey { get; init; }
}

public sealed record CodexQuotaTokenCounter(
    string CardKey,
    DateTimeOffset CapturedAt,
    long LifetimeTokens,
    [property: JsonIgnore] long? RecentWeeklyAverageTokens = null);

public sealed record ProviderHealth(
    ProviderKind Provider,
    bool Connected,
    string Detail,
    ProviderHealthCode Code = ProviderHealthCode.Unknown,
    int? HttpStatus = null,
    DateTimeOffset? RetryAt = null);

public sealed record QuotaSnapshot(
    IReadOnlyList<QuotaCard> Cards,
    IReadOnlyList<ProviderHealth> Health,
    DateTimeOffset CapturedAt)
{
    public IReadOnlyList<CodexAccountQuota> CodexAccounts { get; init; } = [];
    public IReadOnlyList<CodexQuotaTokenCounter> CodexQuotaTokenCounters { get; init; } = [];

    public static QuotaSnapshot Empty(DateTimeOffset now) => new([], [], now);
}

public enum QuotaPaceStatus
{
    Unavailable,
    Learning,
    NoMeaningfulConsumption,
    WaitingForFreshData,
    Exhausted,
    WeeklyBlocked,
    ProjectedExhaustion,
    ResetsBeforeExhaustion,
}

public enum QuotaRateSampleSource
{
    Live,
    CodexRollout,
}

public enum QuotaTrendConfidence
{
    Stable,
    Normal,
    Provisional,
}

public sealed record QuotaRateSample(
    string CardKey,
    string WindowLabel,
    long DurationTicks,
    DateTimeOffset CapturedAt,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    QuotaRateSampleSource Source = QuotaRateSampleSource.Live);

public sealed class QuotaRateHistory
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<QuotaRateSample> Samples { get; set; } = [];
}

public sealed record QuotaCyclePace(
    double ExpectedUsedPercent,
    double DeltaPercent,
    double? PercentPerHour = null,
    DateTimeOffset? ProjectedExhaustedAt = null,
    bool ResetsBeforeExhaustion = false,
    double? SafeSpeedMultiplier = null);

public sealed record QuotaRecentTrend(
    TimeSpan ObservedSpan,
    double PercentPerHour,
    DateTimeOffset ProjectedExhaustedAt,
    bool ResetsBeforeExhaustion,
    QuotaTrendConfidence Confidence);

public sealed record QuotaPaceEstimate(
    QuotaPaceStatus Status,
    QuotaCyclePace? Cycle = null,
    QuotaRecentTrend? Recent = null,
    TimeSpan? ObservedSpan = null,
    DateTimeOffset? ValidUntil = null);

public sealed record ProviderResult(
    ProviderKind Provider,
    IReadOnlyList<QuotaCard> Cards,
    ProviderHealth Health)
{
    public IReadOnlyList<CodexAccountQuota> CodexAccounts { get; init; } = [];
    public IReadOnlyList<CodexQuotaTokenCounter> CodexQuotaTokenCounters { get; init; } = [];
    public bool ReplaceCachedCards { get; init; }
}

public static class BarLayoutMath
{
    public const int Height = 42;
    public const int CardWidth = 192;
    public const int CardGap = 5;
    public const int LabelWidth = 82;
    public const int ControlsWidth = 72;
    public const int SectionGap = 7;
    public const int OuterPadding = 5;
    public const int HealthDotWidth = 6;
    public const int HealthDotGap = 4;
    public const int OverflowWidth = 36;
    public const int MinimumWidth = 323;
    public const int MaximumWidth = 840;

    public static int ContentWidth(int cardCount, int healthCount, bool hasOverflow = false)
    {
        cardCount = Math.Max(1, cardCount);
        healthCount = Math.Max(1, healthCount);
        var cards = cardCount * CardWidth + Math.Max(0, cardCount - 1) * CardGap;
        if (hasOverflow) cards += CardGap + OverflowWidth;
        var health = healthCount * HealthDotWidth + Math.Max(0, healthCount - 1) * HealthDotGap;
        return Math.Clamp(RawContentWidth(cards, health), MinimumWidth, MaximumWidth);
    }

    public static int RawContentWidthForCounts(int cardCount, int healthCount, bool hasOverflow = false)
    {
        cardCount = Math.Max(1, cardCount);
        healthCount = Math.Max(1, healthCount);
        var cards = cardCount * CardWidth + Math.Max(0, cardCount - 1) * CardGap;
        if (hasOverflow) cards += CardGap + OverflowWidth;
        var health = healthCount * HealthDotWidth + Math.Max(0, healthCount - 1) * HealthDotGap;
        return RawContentWidth(cards, health);
    }

    private static int RawContentWidth(int cards, int health) =>
        OuterPadding * 2 + LabelWidth + SectionGap * 3 + cards + health + ControlsWidth;
}

public static class TaskbarMiniLayoutMath
{
    public const int Height = 44;
    public const int CardWidth = 144;
    public const int CodexPoolCardWidth = 184;
    public const int ServiceCardWidth = 104;
    public const int RadarResetContentWidth = 92;
    public const int CodexEconomyContentWidth = 44;
    public const int CollapsedCardWidth = 34;
    public const int ProviderCollapseHandleWidth = 9;
    public const int MinimumAreaContentWidth = 88;
    public const int MaximumAreaContentWidth = 240;
    public const int AreaResizeGripWidth = 4;
    public const int CardGap = 3;
    public const int ModuleGap = 4;
    public const int OuterPadding = 6;
    public const int ControlsWidth = 24;
    public const int ControlGap = 4;
    public const int SystemUsageContentWidth = MinimumAreaContentWidth;
    public const int SystemUsageGap = 4;
    public const int OverflowWidth = 28;
    public const int MaximumCards = 4;
    public const int MaximumWindows = 3;

    public static int ContentWidth(int cardCount, bool hasOverflow = false)
    {
        cardCount = Math.Clamp(cardCount, 1, MaximumCards);
        var width = OuterPadding * 2
            + cardCount * AreaWidth(CardWidth, collapsed: false)
            + Math.Max(0, cardCount - 1) * CardGap;
        if (hasOverflow) width += CardGap + OverflowWidth;
        return width
            + SystemUsageGap
            + AreaWidth(SystemUsageContentWidth, collapsed: false)
            + ControlGap
            + ControlsWidth;
    }

    public static int ContentWidth(IReadOnlyList<int> cardWidths, bool hasOverflow = false)
    {
        return ContentWidth(
            cardWidths,
            AreaWidth(SystemUsageContentWidth, collapsed: false),
            hasOverflow);
    }

    public static int ContentWidth(
        IReadOnlyList<int> cardWidths,
        int systemAreaWidth,
        bool hasOverflow = false)
    {
        var width = cardWidths.Count == 0
            ? OuterPadding * 2 + AreaWidth(CardWidth, collapsed: false)
            : OuterPadding * 2
                + cardWidths.Sum()
                + Math.Max(0, cardWidths.Count - 1) * CardGap;
        if (hasOverflow) width += CardGap + OverflowWidth;
        return width + SystemUsageGap + systemAreaWidth + ControlGap + ControlsWidth;
    }

    public static int ModuleContentWidth(
        IReadOnlyList<int> areaWidths,
        bool hasOverflow = false)
    {
        var width = areaWidths.Count == 0
            ? OuterPadding * 2 + AreaWidth(CardWidth, collapsed: false)
            : OuterPadding * 2
                + areaWidths.Sum()
                + Math.Max(0, areaWidths.Count - 1) * ModuleGap;
        if (hasOverflow) width += ModuleGap + OverflowWidth;
        return width + ControlGap + ControlsWidth;
    }

    public static int CardWidthFor(QuotaCard card) => card.IsService
        ? ServiceCardWidth
        : CardWidth;

    public static int NormalizeAreaContentWidth(int width) =>
        Math.Clamp(width, MinimumAreaContentWidth, MaximumAreaContentWidth);

    public static int NormalizeAreaContentWidth(int width, string? areaId) =>
        Math.Clamp(width, MinimumAreaContentWidthFor(areaId), MaximumAreaContentWidth);

    public static int MinimumAreaContentWidthFor(string? areaId) =>
        string.Equals(areaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
            ? CodexEconomyContentWidth
            : MinimumAreaContentWidth;

    public static int AreaWidth(int contentWidth, bool collapsed) =>
        ProviderCollapseHandleWidth
        + (collapsed ? CollapsedCardWidth : NormalizeAreaContentWidth(contentWidth));

    public static int AreaWidth(int contentWidth, bool collapsed, string? areaId) =>
        ProviderCollapseHandleWidth
        + (collapsed ? CollapsedCardWidth : NormalizeAreaContentWidth(contentWidth, areaId));

    public static IReadOnlyList<QuotaWindow> VisibleWindows(IReadOnlyList<QuotaWindow> windows)
    {
        var available = windows
            .Where(window => window.UsedPercent is not null || window.ResetsAt is not null)
            .Take(MaximumWindows)
            .ToArray();
        return available.Length > 0
            ? available
            : windows.Take(MaximumWindows).ToArray();
    }
}


public enum PopoverTailSide
{
    Top,
    Right,
    Bottom,
    Left,
}

public readonly record struct TaskbarMiniPopoverPlacement(
    Point Location,
    Size WindowSize,
    PopoverTailSide TailSide,
    int TailOffset);

public static class TaskbarMiniPopoverMath
{
    public static TaskbarMiniPopoverPlacement Place(
        Rectangle anchor,
        Size bodySize,
        int tailSize,
        int gap,
        Rectangle workingArea)
    {
        if (anchor.Width <= 0 || anchor.Height <= 0) throw new ArgumentOutOfRangeException(nameof(anchor));
        if (bodySize.Width <= 0 || bodySize.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bodySize));
        if (workingArea.Width <= 0 || workingArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workingArea));
        tailSize = Math.Max(1, tailSize);
        gap = Math.Max(0, gap);

        var tolerance = Math.Max(2, gap);
        var side = anchor.Top >= workingArea.Bottom - tolerance
            ? PopoverTailSide.Bottom
            : anchor.Bottom <= workingArea.Top + tolerance
                ? PopoverTailSide.Top
                : anchor.Right <= workingArea.Left + tolerance
                    ? PopoverTailSide.Left
                    : anchor.Left >= workingArea.Right - tolerance
                        ? PopoverTailSide.Right
                        : BestAvailableSide(anchor, bodySize, tailSize, gap, workingArea);

        var verticalTail = side is PopoverTailSide.Top or PopoverTailSide.Bottom;
        var windowSize = verticalTail
            ? new Size(bodySize.Width, bodySize.Height + tailSize)
            : new Size(bodySize.Width + tailSize, bodySize.Height);
        var anchorCenterX = anchor.Left + anchor.Width / 2;
        var anchorCenterY = anchor.Top + anchor.Height / 2;

        int x;
        int y;
        int tailOffset;
        switch (side)
        {
            case PopoverTailSide.Top:
                x = ClampCoordinate(anchorCenterX - windowSize.Width / 2, workingArea.Left, workingArea.Right - windowSize.Width);
                y = ClampCoordinate(anchor.Bottom + gap, workingArea.Top, workingArea.Bottom - windowSize.Height);
                tailOffset = ClampTail(anchorCenterX - x, windowSize.Width, tailSize);
                break;
            case PopoverTailSide.Right:
                x = ClampCoordinate(anchor.Left - gap - windowSize.Width, workingArea.Left, workingArea.Right - windowSize.Width);
                y = ClampCoordinate(anchorCenterY - windowSize.Height / 2, workingArea.Top, workingArea.Bottom - windowSize.Height);
                tailOffset = ClampTail(anchorCenterY - y, windowSize.Height, tailSize);
                break;
            case PopoverTailSide.Left:
                x = ClampCoordinate(anchor.Right + gap, workingArea.Left, workingArea.Right - windowSize.Width);
                y = ClampCoordinate(anchorCenterY - windowSize.Height / 2, workingArea.Top, workingArea.Bottom - windowSize.Height);
                tailOffset = ClampTail(anchorCenterY - y, windowSize.Height, tailSize);
                break;
            default:
                x = ClampCoordinate(anchorCenterX - windowSize.Width / 2, workingArea.Left, workingArea.Right - windowSize.Width);
                y = ClampCoordinate(anchor.Top - gap - windowSize.Height, workingArea.Top, workingArea.Bottom - windowSize.Height);
                tailOffset = ClampTail(anchorCenterX - x, windowSize.Width, tailSize);
                break;
        }

        return new TaskbarMiniPopoverPlacement(new Point(x, y), windowSize, side, tailOffset);
    }

    private static PopoverTailSide BestAvailableSide(
        Rectangle anchor,
        Size bodySize,
        int tailSize,
        int gap,
        Rectangle workingArea)
    {
        var verticalSize = bodySize.Height + tailSize;
        var horizontalSize = bodySize.Width + tailSize;
        if (anchor.Top - workingArea.Top - gap >= verticalSize) return PopoverTailSide.Bottom;
        if (workingArea.Bottom - anchor.Bottom - gap >= verticalSize) return PopoverTailSide.Top;
        if (workingArea.Right - anchor.Right - gap >= horizontalSize) return PopoverTailSide.Left;
        if (anchor.Left - workingArea.Left - gap >= horizontalSize) return PopoverTailSide.Right;

        var spaces = new[]
        {
            (Side: PopoverTailSide.Bottom, Space: anchor.Top - workingArea.Top),
            (Side: PopoverTailSide.Top, Space: workingArea.Bottom - anchor.Bottom),
            (Side: PopoverTailSide.Left, Space: workingArea.Right - anchor.Right),
            (Side: PopoverTailSide.Right, Space: anchor.Left - workingArea.Left),
        };
        return spaces.MaxBy(item => item.Space).Side;
    }

    private static int ClampCoordinate(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, Math.Max(minimum, maximum));

    private static int ClampTail(int value, int axisLength, int tailSize)
    {
        var inset = Math.Min(axisLength / 2, Math.Max(1, tailSize * 2));
        return Math.Clamp(value, inset, Math.Max(inset, axisLength - inset));
    }
}

public static class QuotaDisplayFormatting
{
    private static readonly TimeSpan ShanghaiUtcOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan DailyGoalMinimumWindow = TimeSpan.FromDays(7);

    public static DateTimeOffset? WeeklyBlockReset(
        QuotaCard card,
        QuotaWindow window,
        DateTimeOffset now)
    {
        if (card.Provider != ProviderKind.Claude
            || window.Duration != TimeSpan.FromHours(5)
            || window.UsedPercent is null
            || window.ResetsAt is not null)
        {
            return null;
        }

        return card.Windows
            .FirstOrDefault(candidate =>
                candidate.Duration == TimeSpan.FromDays(7)
                && (candidate.Label.Trim().ToLowerInvariant() is "1w" or "week" or "7d")
                && candidate.UsedPercent is >= 100
                && candidate.ResetsAt > now)
            ?.ResetsAt;
    }

    public static string FormatWindowShort(QuotaWindow? window)
    {
        if (window is null) return "--";
        if (window.Duration == TimeSpan.FromHours(5)) return "5h";
        if (window.Duration == TimeSpan.FromDays(7)) return "7d";
        return window.Label.Trim().ToLowerInvariant() switch
        {
            "1w" or "week" => "7d",
            _ => window.Label.Trim(),
        };
    }

    public static string FormatWindowTiny(QuotaWindow? window) =>
        string.Equals(window?.Label, "Fable", StringComparison.OrdinalIgnoreCase)
            ? "Fb"
            : FormatWindowShort(window);

    public static string FormatResetShort(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "--";
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d{remaining.Hours}h";
        if (remaining.TotalHours >= 10) return $"{(int)remaining.TotalHours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h{remaining.Minutes}m";
        return $"{Math.Max(1, (int)Math.Round(remaining.TotalMinutes))}m";
    }

    public static double? BudgetMarkerRemaining(
        QuotaWindow window,
        QuotaCyclePace? cycle,
        DateTimeOffset now)
    {
        if (cycle is null) return null;
        if (!UsesShanghaiMidnightGoal(window))
        {
            return Math.Clamp(100 - cycle.ExpectedUsedPercent, 0, 100);
        }
        if (window.ResetsAt is not { } reset || reset <= now)
        {
            return null;
        }

        var shanghaiNow = now.ToOffset(ShanghaiUtcOffset);
        var nextShanghaiMidnight = new DateTimeOffset(
            shanghaiNow.Date.AddDays(1),
            ShanghaiUtcOffset);
        var targetAt = reset < nextShanghaiMidnight ? reset : nextShanghaiMidnight;
        var cycleStart = reset - window.Duration;
        var expectedUsed = Math.Clamp(
            (targetAt - cycleStart).TotalMilliseconds / window.Duration.TotalMilliseconds * 100,
            0,
            100);
        return 100 - expectedUsed;
    }

    public static bool UsesShanghaiMidnightGoal(QuotaWindow window) =>
        window.Duration >= DailyGoalMinimumWindow;
}

public static class CodexDisplayFormatting
{
    public static string AccountLabel(int accountCount, int displayIndex) => accountCount <= 1
        ? "Codex"
        : $"Codex · {Math.Max(0, displayIndex) + 1}";

    public static string ApiServiceLabel(int serviceCount) => $"API · {Math.Max(0, serviceCount)}";
}

public static class MiniAreaIds
{
    public const string Claude = "zgstokenbar.provider.claude";
    public const string Codex = "zgstokenbar.provider.codex";
    public const string AiGateway = "zgstokenbar.provider.ai-gateway";
    public const string RadarReset = "zgstokenbar.radar.reset";
    public const string CodexEconomy = "zgstokenbar.codex.economy";
    public const string SystemMetrics = "zgstokenbar.metrics.system";
}

public sealed record MiniAreaLayout(bool Collapsed = false, int? Width = null)
{
    public MiniAreaLayout Normalized(string? areaId = null) => this with
    {
        Width = Width is { } width
            ? TaskbarMiniLayoutMath.NormalizeAreaContentWidth(width, areaId)
            : null,
    };
}
