using System.Globalization;
using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class NativeText
{
    private static readonly NativeText Chinese = new(true);
    private static readonly NativeText English = new(false);
    private readonly bool _zh;

    private NativeText(bool zh)
    {
        _zh = zh;
    }

    public static NativeText For(string? locale) =>
        string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? English : Chinese;

    public string Locale => _zh ? "zh-CN" : "en";
    public bool IsChinese => _zh;

    public string SettingsTitle => T("ZGSTokenBar 设置", "ZGSTokenBar settings");
    public string SettingsHeading => T("实时配额栏", "Live quota bar");
    public string SettingsSubtitle => T("Windows 原生实时配额栏", "Native live quota bar for Windows");
    public string General => T("常规", "General");
    public string Notifications => T("通知", "Notifications");
    public string Advanced => T("高级", "Advanced");
    public string About => T("关于", "About");
    public string Modules => T("模块", "Modules");
    public string SystemMetricsModuleHint => T(
        "显示 CPU、内存、磁盘与 GPU 占用；关闭后停止采样。",
        "Show CPU, memory, disk, and GPU usage; sampling stops while disabled.");
    public string ClaudeProviderHint => T("在配额栏中显示 Claude Code 配额。", "Show Claude Code limits in the quota bar.");
    public string CodexProviderHint => T("在配额栏中显示 Codex 配额。", "Show Codex limits in the quota bar.");
    public string CodexLocalUsageModule => T("Codex 本机用量", "Codex local usage");
    public string CodexLocalUsageModuleHint => T(
        "显示本机 Token、缓存命中率与历史估算；依赖 Codex。",
        "Show local tokens, cache hit rate, and history estimates; requires Codex.");
    public string AiGateway => T("AI 网关", "AI Gateway");
    public string AiGatewayProviderHint => T(
        "显示私有 AI 网关的只读余额；不发起模型调用。",
        "Show the private AI Gateway balance only; no model calls are made.");
    public string Sub2ApiModuleHint => T(
        "为活动 API 服务显示 Sub2API 聚合额度与可用性；依赖 Codex。",
        "Show Sub2API aggregate quota and availability for active API services; requires Codex.");
    public string AutomaticRefresh => T("自动刷新", "Automatic refresh");
    public string AutomaticRefreshHint => T("设置配额与使用数据的刷新频率。", "Choose how often quota and usage data refresh.");
    public string StartWithWindows => T("随 Windows 启动", "Start with Windows");
    public string StartWithWindowsHint => T("登录 Windows 时自动启动 ZGSTokenBar。", "Start ZGSTokenBar automatically when you sign in.");
    public string UsageAlertsTitle => T("用量提醒", "Usage alerts");
    public string UsageAlerts => T(
        "用量达到 25 / 50 / 75 / 90 / 100% 时提醒",
        "Usage alerts at 25 / 50 / 75 / 90 / 100%");
    public string Display => T("外观", "Appearance");
    public string BackgroundPalette => T("背景配色", "Background");
    public string BackgroundPaletteHint => T("仅改变配额界面的背景层级。", "Changes only the quota surfaces' background layers.");
    public string BackgroundPaletteName(string id) => id switch
    {
        "graphite" => T("石墨", "Graphite"),
        "navy" => T("深海", "Navy"),
        "plum" => T("紫夜", "Plum"),
        _ => T("原夜", "Midnight"),
    };
    public string Animations => T(
        "启用细微过渡与刷新动画",
        "Enable subtle transitions and refresh animation");
    public string AnimationsTitle => T("动画效果", "Animation effects");
    public string EmbedTaskbarTitle => T("嵌入任务栏（实验）", "Embed in taskbar (experimental)");
    public string EmbedTaskbarHint => T(
        "吸附在主任务栏时把 Bar 放进任务栏内部；失败、资源管理器无响应或重启时自动回到悬浮模式",
        "While docked on the primary taskbar, host the Bar inside it; falls back to the overlay if Explorer refuses, hangs or restarts");
    public string AllowClaudeRefresh => T(
        "Claude OAuth 过期时允许刷新",
        "Allow Claude OAuth refresh when expired");
    public string ClaudeRefreshHint => T(
        "默认开启；关闭后由 Claude Code 负责刷新。",
        "On by default; disable to leave refresh to Claude Code.");
    public string Save => T("保存", "Save");
    public string Cancel => T("取消", "Cancel");
    public string Language => T("语言", "Language");
    public string LanguageHint => T("选择界面显示语言；保存后生效。", "Choose the interface language; applies after saving.");
    public string ChineseLanguage => "简体中文";
    public string EnglishLanguage => "English";
    public string AboutDescription => T(
        "轻量、常驻的 Claude 与 Codex 配额查看器。",
        "A lightweight always-ready viewer for Claude and Codex limits.");
    public string LocalFirstPrivacy => T(
        "本地优先：设置与快照留在设备上；本页不保存 OAuth 凭据。",
        "Local-first: settings and snapshots stay on this device; this page stores no OAuth credentials.");
    public string VersionUnknown => T("版本未知", "Version unavailable");
    public string UnsavedChanges => T("有未保存的更改", "Unsaved changes");
    public string DiscardChangesTitle => T("放弃更改？", "Discard changes?");
    public string DiscardChangesMessage => T(
        "当前设置尚未保存。要放弃这些更改吗？",
        "These settings have not been saved. Discard the changes?");

    public string RefreshNow => T("立即刷新", "Refresh now");
    public string UpdateTo(Version version) => T($"更新到 v{version.ToString(3)}", $"Update to v{version.ToString(3)}");
    public string UpdateAvailableTitle(Version version) => T(
        $"ZGSTokenBar v{version.ToString(3)} 可更新",
        $"ZGSTokenBar v{version.ToString(3)} is available");
    public string UpdateAvailableBody => T(
        "点击通知或托盘菜单查看已签名发布包。",
        "Click this notification or the tray menu to view the signed release.");
    public string OpenSettingsHint => T("打开设置面板", "Open settings panel");
    public string Settings => T("设置", "Settings");
    public string Quit => T("退出", "Quit");
    public string TrayText => T("ZGSTokenBar — 实时配额", "ZGSTokenBar — live limits");
    public string SettingsNotSaved => T("设置未保存", "Settings not saved");
    public string SettingsSaveFailed => T(
        "ZGSTokenBar 无法保存设置。请检查 App 数据目录权限后重试。",
        "ZGSTokenBar could not save settings. Check access to the app data folder and try again.");
    public string PluginBundleTrustFailedTitle => T(
        "Provider 信任校验失败",
        "Provider trust validation failed");
    public string PluginBundleTrustFailedBody => T(
        "DeepSeek Provider 未加载；请运行 ZGSTokenBar.Cli.exe plugin doctor 查看安全诊断。",
        "DeepSeek Provider was not loaded. Run ZGSTokenBar.Cli.exe plugin doctor for the safe diagnostic.");
    public string Quota => T("配额", "Quota");
    public string Refreshing => T("刷新中", "refreshing");
    public string LiveLimits => T("实时限制", "live limits");
    public string Sync => T("同步", "sync");
    public string Wait => T("等待", "wait");
    public string ApiServiceConfigured => T("API 服务已配置", "API service configured");
    public string ApiServiceConfiguredShort => T("已配置", "SET");
    public string ApiServiceNoQuota => T(
        "此服务不提供 Codex 订阅配额。",
        "This service does not expose Codex subscription quota.");
    public string Sub2ApiUnavailable => T("额度暂不可用", "Quota unavailable");
    public string Sub2ApiPool => T("Sub2API 池", "Sub2API pool");
    public string Sub2ApiUsage => T("Sub2API 用量", "Sub2API usage");
    public string Sub2ApiQuota => T("Sub2API 额度", "Sub2API quota");
    public string Sub2ApiAccountAvailability => T("当前可用", "Current availability");
    public string Sub2ApiAccountAvailabilityCompact(Sub2ApiAccountAvailabilitySummary availability) =>
        Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is { } remaining
            ? ObserverPercent(remaining)
            : availability.EligibleAccountCount is { } eligible
            && availability.ReadableAccountCount is { } readable
                ? $"{readable}/{eligible}"
                : Sub2ApiUnavailable;
    public string Sub2ApiAccountAvailabilityHeadline(Sub2ApiAccountAvailabilitySummary availability) => T(
        Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is not null
            ? $"可用 {Sub2ApiAccountAvailabilityCompact(availability)}"
            : availability.EligibleAccountCount is not null
                ? $"可读 {Sub2ApiAccountAvailabilityCompact(availability)}"
                : Sub2ApiUnavailable,
        Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is not null
            ? $"{Sub2ApiAccountAvailabilityCompact(availability)} available"
            : availability.EligibleAccountCount is not null
                ? $"Readable {Sub2ApiAccountAvailabilityCompact(availability)}"
                : Sub2ApiUnavailable);
    public string Sub2ApiAccountAvailabilityCoverage(Sub2ApiAccountAvailabilitySummary availability) =>
        availability.EligibleAccountCount is { } eligible
        && availability.ReadableAccountCount is { } readable
            ? T($"候选账号 {eligible} · 可读 {readable}", $"Eligible accounts {eligible} · readable {readable}")
            : Sub2ApiUnavailable;
    public string Sub2ApiAccountAvailabilitySlot(int slot) => $"#{slot}";
    public string Sub2ApiAccountAvailabilityPercent(Sub2ApiAccountAvailabilityEntry account) =>
        account.RemainingPercent is { } remaining
            ? ObserverPercent(remaining)
            : T("未知", "Unknown");
    public string Sub2ApiLegacyQuotaCompact(Sub2ApiQuotaWindow window) =>
        ObserverPercent(window.RemainingPercent);
    public string Sub2ApiLegacyQuotaHeadline(Sub2ApiQuotaWindow window) => T(
        $"可用 {Sub2ApiLegacyQuotaCompact(window)}",
        $"{Sub2ApiLegacyQuotaCompact(window)} available");
    public string Sub2ApiLegacyQuotaDetails(Sub2ApiQuotaWindow window) => T(
        $"额度汇总 {ObserverQuotaEquivalent(window.RemainingAccountEquivalents)}/{window.AccountCount} 账号份额",
        $"Quota total {ObserverQuotaEquivalent(window.RemainingAccountEquivalents)}/{window.AccountCount} acct. shares");
    public string Sub2ApiPresentationSummary(
        QuotaCard card,
        Sub2ApiServicePresentationState presentation) => presentation.Kind switch
    {
        Sub2ApiServicePresentationKind.CompleteAvailability
            or Sub2ApiServicePresentationKind.PartialAvailability
            or Sub2ApiServicePresentationKind.KnownNoneAvailability
            when presentation.Availability is { } availability =>
                $"{Sub2ApiAccountAvailabilityCompact(availability)} · {Sub2ApiQuotaStatusShort(availability.Status)}",
        Sub2ApiServicePresentationKind.LegacyAggregateQuota
            when presentation.LegacyQuota is { } legacy => Sub2ApiLegacyQuotaCompact(legacy),
        Sub2ApiServicePresentationKind.Usage
            when presentation.Usage is { } usage => Sub2ApiUsageSummaryShort(usage),
        Sub2ApiServicePresentationKind.Pool
            when presentation.Pool is { } pool =>
                $"{Sub2ApiPoolAvailableAccounts(pool)} · {Sub2ApiPoolStatusShort(pool.Status)}",
        _ => Sub2ApiUnavailable,
    };
    public string Sub2ApiQuotaCompact(Sub2ApiQuotaSummary quota) => Sub2ApiQuotaFormatting.PreferredWindow(quota) is { } window
        ? ObserverPercent(window.RemainingPercent)
        : "—";
    public string Sub2ApiQuotaSummaryShort(Sub2ApiQuotaSummary quota) => T(
        $"{Sub2ApiQuotaCompact(quota)} · {Sub2ApiQuotaStatusShort(quota.Status)}",
        $"{Sub2ApiQuotaCompact(quota)} · {Sub2ApiQuotaStatusShort(quota.Status)}");
    public string Sub2ApiQuotaHeadline(Sub2ApiQuotaWindow window) => T(
        $"{window.Label} 剩余 {ObserverPercent(window.RemainingPercent)}",
        $"{window.Label} remaining {ObserverPercent(window.RemainingPercent)}");
    public string Sub2ApiQuotaWindowDetails(Sub2ApiQuotaWindow window) => T(
        $"{window.Label} 额度汇总 {ObserverQuotaEquivalent(window.RemainingAccountEquivalents)}/{window.AccountCount} 账号份额",
        $"{window.Label} quota total {ObserverQuotaEquivalent(window.RemainingAccountEquivalents)}/{window.AccountCount} acct. shares");
    public string Sub2ApiQuotaProxyTokens(Sub2ApiUsageSummary usage) => T(
        $"Token 今日 {ObserverTokenCount(usage.TodayTokens)} · 累计 {ObserverTokenCount(usage.TotalTokens)}",
        $"Token {ObserverTokenCount(usage.TodayTokens)} today · {ObserverTokenCount(usage.TotalTokens)} total");
    public string Sub2ApiQuotaUpdatedShort(DateTimeOffset? observedAt) => observedAt is { } value
        ? T($"更新 {value.ToLocalTime():HH:mm}", $"Updated {value.ToLocalTime():HH:mm}")
        : T("更新时间未知", "Update unavailable");
    public string Sub2ApiQuotaStatus(Sub2ApiQuotaStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Available => T("可用", "Available"),
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Unavailable => T("不可用", "Unavailable"),
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Stale => T("已过期", "Stale"),
        _ => T("未知", "Unknown"),
    };
    public string Sub2ApiQuotaStatusShort(Sub2ApiQuotaStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Available => T("可用", "OK"),
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Unavailable => T("不可用", "OFF"),
        ZGSTokenBar.Core.Sub2ApiQuotaStatus.Stale => T("过期", "OLD"),
        _ => T("未知", "N/A"),
    };
    public string Sub2ApiUsageCompact(Sub2ApiUsageSummary usage) => ObserverTokenCount(usage.TodayTokens);
    public string Sub2ApiUsageSummaryShort(Sub2ApiUsageSummary usage) => T(
        $"今日 {ObserverTokenCount(usage.TodayTokens)} · {Sub2ApiUsageStatusShort(usage.Status)}",
        $"Today {ObserverTokenCount(usage.TodayTokens)} · {Sub2ApiUsageStatusShort(usage.Status)}");
    public string Sub2ApiUsageTodayTokens(Sub2ApiUsageSummary usage) => T(
        $"今日 Token {ObserverTokenCount(usage.TodayTokens)}",
        $"Today tokens {ObserverTokenCount(usage.TodayTokens)}");
    public string Sub2ApiUsageTotalTokens(Sub2ApiUsageSummary usage) => T(
        $"累计 Token {ObserverTokenCount(usage.TotalTokens)}",
        $"Total tokens {ObserverTokenCount(usage.TotalTokens)}");
    public string Sub2ApiUsageRequests(Sub2ApiUsageSummary usage) => T(
        $"今日请求 {ObserverCount(usage.TodayRequests)} · 累计 {ObserverCount(usage.TotalRequests)}",
        $"Today {ObserverCount(usage.TodayRequests)} req · total {ObserverCount(usage.TotalRequests)}");
    public string Sub2ApiUsagePool(Sub2ApiPoolAvailability pool) => T(
        $"可用账号 {Sub2ApiPoolFormatting.AccountPair(pool)} · 并发 {Sub2ApiPoolFormatting.ConcurrencyPair(pool)}",
        $"Available accounts {Sub2ApiPoolFormatting.AccountPair(pool)} · conc. {Sub2ApiPoolFormatting.ConcurrencyPair(pool)}");
    public string Sub2ApiUsageUpdatedShort(DateTimeOffset? observedAt) => observedAt is { } value
        ? T($"更新 {value.ToLocalTime():HH:mm}", $"Updated {value.ToLocalTime():HH:mm}")
        : T("更新时间未知", "Update unavailable");
    public string Sub2ApiUsageStatus(Sub2ApiUsageStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Available => T("可用", "Available"),
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Unavailable => T("不可用", "Unavailable"),
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Stale => T("已过期", "Stale"),
        _ => T("未知", "Unknown"),
    };
    public string Sub2ApiUsageStatusShort(Sub2ApiUsageStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Available => T("可用", "OK"),
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Unavailable => T("不可用", "OFF"),
        ZGSTokenBar.Core.Sub2ApiUsageStatus.Stale => T("过期", "OLD"),
        _ => T("未知", "N/A"),
    };
    public string Sub2ApiPoolAvailableAccounts(Sub2ApiPoolAvailability pool) => T(
        $"可用账号 {Sub2ApiPoolFormatting.AccountPair(pool)}",
        $"Available accounts {Sub2ApiPoolFormatting.AccountPair(pool)}");
    public string Sub2ApiPoolFreeConcurrency(Sub2ApiPoolAvailability pool) => T(
        $"空闲并发 {Sub2ApiPoolFormatting.ConcurrencyPair(pool)}",
        $"Free concurrency {Sub2ApiPoolFormatting.ConcurrencyPair(pool)}");
    public string Sub2ApiPoolIssues(Sub2ApiPoolAvailability pool) => T(
        $"限流 {PoolCount(pool.RateLimitedAccounts)} · 错误 {PoolCount(pool.ErrorAccounts)}",
        $"Rate limited {PoolCount(pool.RateLimitedAccounts)} · Errors {PoolCount(pool.ErrorAccounts)}");
    public string Sub2ApiPoolUpdatedShort(DateTimeOffset? observedAt) => observedAt is { } value
        ? T($"更新 {value.ToLocalTime():HH:mm}", $"Updated {value.ToLocalTime():HH:mm}")
        : T("更新时间未知", "Update unavailable");
    public string Sub2ApiPoolStatus(Sub2ApiPoolStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Available => T("可用", "Available"),
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Unavailable => T("不可用", "Unavailable"),
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Stale => T("已过期", "Stale"),
        _ => T("未知", "Unknown"),
    };
    public string Sub2ApiPoolStatusShort(Sub2ApiPoolStatus status) => status switch
    {
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Available => T("可用", "OK"),
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Unavailable => T("不可用", "OFF"),
        ZGSTokenBar.Core.Sub2ApiPoolStatus.Stale => T("过期", "OLD"),
        _ => T("未知", "N/A"),
    };
    public string AiGatewayModel => T(
        "deepseek-v4-flash · 只读",
        "deepseek-v4-flash · read-only");
    public string AiGatewayTotalBalance => T("账户余额", "Account balance");
    public string AiGatewayToppedUpBalance => T("充值余额", "Topped up");
    public string AiGatewayGrantedBalance => T("赠送余额", "Granted");
    public string AiGatewayUpdated(DateTimeOffset? observedAt) => observedAt is { } value
        ? T($"更新于 {value.ToLocalTime():MM-dd HH:mm}", $"Updated {value.ToLocalTime():MM-dd HH:mm}")
        : T("更新时间未知", "Update time unavailable");
    public string AiGatewayUpdatedShort(DateTimeOffset? observedAt) => observedAt is { } value
        ? T($"更新 {value.ToLocalTime():HH:mm}", $"Updated {value.ToLocalTime():HH:mm}")
        : T("更新时间未知", "Update unavailable");
    public string AiGatewayStatus(AiGatewayBalanceStatus status) => status switch
    {
        AiGatewayBalanceStatus.Available => T("可用", "Available"),
        AiGatewayBalanceStatus.Unavailable => T("不可用", "Unavailable"),
        AiGatewayBalanceStatus.Stale => T("已过期", "Stale"),
        _ => T("未知", "Unknown"),
    };
    public string AiGatewayStatusShort(AiGatewayBalanceStatus status) => status switch
    {
        AiGatewayBalanceStatus.Available => T("可用", "OK"),
        AiGatewayBalanceStatus.Unavailable => T("不可用", "OFF"),
        AiGatewayBalanceStatus.Stale => T("过期", "OLD"),
        _ => T("未知", "N/A"),
    };

    private static string ObserverTokenCount(long? value) => value is { } tokens
        ? FormatTokenCount(tokens)
        : "—";
    private static string ObserverCount(long? value) => value is { } count
        ? count.ToString("N0", CultureInfo.InvariantCulture)
        : "—";
    private static string ObserverPercent(double value) =>
        $"{value.ToString("0.#", CultureInfo.InvariantCulture)}%";
    private static string ObserverQuotaEquivalent(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string PoolCount(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
    public string MiniCardCollapseHint(bool collapsed) => collapsed
        ? T("展开此区域", "Expand this area")
        : T("收起此区域；拖动箭头左侧细线调宽", "Collapse this area; drag the line left of the chevron to resize");
    public string MiniCardReorderHint => T(
        "拖动排序；其他位置可拖动整条栏",
        "Drag to reorder; drag elsewhere to move the whole bar");
    public string AiGatewayTodayUsage(AiGatewayUsageSummary usage)
    {
        var requests = Math.Max(0, usage.Today.RequestCount)
            .ToString("N0", CultureInfo.InvariantCulture);
        var tokens = FormatTokenCount(usage.Today.TotalTokens);
        return T(
            $"UTC 今日 {requests} 次 · {tokens} Token",
            $"UTC today {requests} req · {tokens} tokens");
    }
    public string AiGatewayUsageDetail(AiGatewayUsageSummary usage) => T(
        $"成本 {FormatCnyCost(usage.Today.EstimatedCostCny)} · 缓存 {FormatDecimalCacheHitPercent(usage.Today.CacheHitRatePercent)}",
        $"Cost {FormatCnyCost(usage.Today.EstimatedCostCny)} · Cache {FormatDecimalCacheHitPercent(usage.Today.CacheHitRatePercent)}");
    public string RefreshingLiveLimits => T("正在刷新实时限制…", "Refreshing live limits…");
    public string QuotaWaiting => T("配额数据等待中。", "Quota data is waiting.");
    public string ProviderLoading(ProviderKind provider) =>
        provider == ProviderKind.AiGateway
            ? T("AI 网关余额加载中。", "AI Gateway balance is loading.")
            : T($"{provider} 配额加载中。", $"{provider} quota is loading.");

    public string RefreshChoice(int minutes) => minutes == 1
        ? T("每分钟", "Every minute")
        : T($"每 {minutes} 分钟", $"Every {minutes} min");

    public string RefreshUpdated(TimeSpan age)
    {
        var ageLabel = age.TotalMinutes < 1
            ? T("刚刚", "now")
            : T($"{Math.Max(1, (int)age.TotalMinutes)} 分钟前", $"{Math.Max(1, (int)age.TotalMinutes)}m ago");
        return T($"立即刷新 · 更新于{ageLabel}", $"Refresh now · updated {ageLabel}");
    }

    public string RefreshUpdatedDetail(TimeSpan age)
    {
        var ageLabel = age.TotalMinutes < 1
            ? T("刚刚", "now")
            : T($"{Math.Max(1, (int)age.TotalMinutes)} 分钟前", $"{Math.Max(1, (int)age.TotalMinutes)}m ago");
        return T($"更新于{ageLabel}", $"Updated {ageLabel}");
    }

    public string RefreshFailures(IEnumerable<string> failures) =>
        T("立即刷新", "Refresh now") + " · " + string.Join(" · ", failures);

    public string WindowWaiting(string label) => T($"{label}：等待中", $"{label}: waiting");
    public string WeeklyQuotaBlocked => T("受周额度限制", "blocked by weekly limit");

    public string WindowSummary(string label, string remaining, string reset) => _zh
        ? $"{label}：剩余 {remaining}，{reset}"
        : $"{label}: {remaining} remaining, {reset}";

    public string WindowReset(DateTimeOffset? reset, DateTimeOffset now) => reset is null
        ? T("暂无重置时间", "reset unavailable")
        : T(
            $"{FormatResetCountdown(reset.Value, now)}重置",
            $"resets {FormatResetCountdown(reset.Value, now)}");

    public string Health(ProviderHealth health, DateTimeOffset now)
    {
        if (health.Provider == ProviderKind.AiGateway)
        {
            var gatewayCode = health.Code == ProviderHealthCode.Unknown
                ? health.Connected ? ProviderHealthCode.Current : ProviderHealthCode.Unavailable
                : health.Code;
            return gatewayCode switch
            {
                ProviderHealthCode.Current => T("AI 网关余额可用。", "AI Gateway balance is available."),
                ProviderHealthCode.Cached => T("AI 网关余额已过期，显示最近一次结果。", "AI Gateway balance is stale; showing the last result."),
                ProviderHealthCode.MissingCredentials => T("未配置 AI 网关观察凭据。", "AI Gateway observer credentials are not configured."),
                ProviderHealthCode.EndpointBlocked => T("AI 网关地址不在允许的私有范围内。", "The AI Gateway endpoint is outside the allowed private range."),
                ProviderHealthCode.Timeout => T("AI 网关余额请求超时。", "AI Gateway balance request timed out."),
                ProviderHealthCode.HttpError => T("AI 网关余额接口返回错误。", "The AI Gateway balance endpoint returned an error."),
                _ => T("AI 网关余额暂不可用。", "AI Gateway balance is unavailable."),
            };
        }

        var provider = health.Provider.ToString();
        var code = health.Code == ProviderHealthCode.Unknown
            ? health.Connected ? ProviderHealthCode.Current : ProviderHealthCode.Unavailable
            : health.Code;
        return code switch
        {
            ProviderHealthCode.Current => T($"{provider} 配额已更新。", $"{provider} quota is current."),
            ProviderHealthCode.Cached => T($"{provider} 配额来自缓存。", $"{provider} quota is cached."),
            ProviderHealthCode.Loading => T($"{provider} 配额加载中。", $"{provider} quota is loading."),
            ProviderHealthCode.Waiting => T($"{provider} 配额等待中。", $"{provider} quota is waiting."),
            ProviderHealthCode.MissingCredentials => T(
                $"未找到 {provider} OAuth 凭据。",
                $"{provider} OAuth credentials were not found."),
            ProviderHealthCode.EndpointBlocked => T(
                $"配置的 {provider} 用量端点不受允许。",
                $"The configured {provider} usage endpoint is not allowed."),
            ProviderHealthCode.OAuthExpired => T(
                $"{provider} OAuth 已过期，请重新登录。",
                $"{provider} OAuth expired. Re-authenticate and try again."),
            ProviderHealthCode.OAuthRefreshFailed => T(
                $"{provider} OAuth 刷新失败，请重新登录。",
                $"{provider} OAuth refresh failed. Re-authenticate and try again."),
            ProviderHealthCode.RateLimited => RateLimited(provider, health.RetryAt, now),
            ProviderHealthCode.HttpError => T(
                $"{provider} API 返回 HTTP {health.HttpStatus?.ToString() ?? "?"}。",
                $"{provider} API returned HTTP {health.HttpStatus?.ToString() ?? "?"}."),
            ProviderHealthCode.Timeout => T(
                $"{provider} API 请求超时。",
                $"{provider} API request timed out."),
            _ => T($"{provider} 配额暂不可用。", $"{provider} quota is unavailable."),
        };
    }

    public string QuotaDetailsTitle => T("ZGSTokenBar 配额详情", "ZGSTokenBar quota details");
    public string LiveQuota => T("实时配额", "Live quota");
    public string SystemUsageTitle => T("系统占用", "System usage");
    public string SystemUsageCpu => "CPU";
    public string SystemUsageMemory => T("内存", "Memory");
    public string SystemUsageDisk => T("磁盘", "Disk");
    public string SystemUsageGpu => "GPU";
    public string SystemUsagePopoverSubtitle(bool pinned) => pinned
        ? T("已固定 · Esc 关闭", "Pinned · Esc to close")
        : PinHint;
    public string SystemUsageCpuDetail(int logicalProcessors) => T(
        $"{logicalProcessors} 个逻辑处理器",
        $"{logicalProcessors} logical processors");
    public string SystemUsageMemoryDetail(ulong? used, ulong? total, ulong? available)
    {
        if (used is null || total is null || available is null)
        {
            return T("系统内存数据不可用", "System memory data unavailable");
        }
        return T(
            $"{FormatGigabytes(used.Value)} / {FormatGigabytes(total.Value)} GB · 可用 {FormatGigabytes(available.Value)} GB",
            $"{FormatGigabytes(used.Value)} / {FormatGigabytes(total.Value)} GB · {FormatGigabytes(available.Value)} GB available");
    }
    public string SystemUsageGpuDetail(double? percent, string? engine, int processCount)
    {
        if (percent is null) return T("性能计数器不可用", "Performance counter unavailable");
        if (percent < .1 || string.IsNullOrWhiteSpace(engine))
        {
            return T("暂无活动引擎", "No active engine");
        }
        return T(
            $"最忙 {engine} · {processCount} 个活动进程",
            $"Busiest {engine} · {processCount} active processes");
    }
    public string SystemUsageDiskDetail(
        double? activePercent,
        double? readBytesPerSecond,
        double? writeBytesPerSecond)
    {
        if (activePercent is null && readBytesPerSecond is null && writeBytesPerSecond is null)
        {
            return T("磁盘性能计数器不可用", "Disk performance counters unavailable");
        }

        return T(
            $"读 {FormatBytesPerSecond(readBytesPerSecond)} · 写 {FormatBytesPerSecond(writeBytesPerSecond)}",
            $"Read {FormatBytesPerSecond(readBytesPerSecond)} · Write {FormatBytesPerSecond(writeBytesPerSecond)}");
    }
    public string SystemUsageCapturedAt(DateTimeOffset capturedAt) => T(
        $"每秒采样 · {capturedAt.ToLocalTime():HH:mm:ss}",
        $"1s sampling · {capturedAt.ToLocalTime():HH:mm:ss}");
    public string SystemUsageTopProcesses => T("同名进程合计", "Grouped totals");
    public string SystemUsageTopProcessesUnavailable => T(
        "正在采集进程详情…",
        "Collecting process details…");
    public string SystemUsageProcessName(string name, int processCount) => processCount > 1
        ? $"{name} ×{processCount}"
        : name;
    public string Pinned => T("已固定", "PINNED");
    public string Preview => T("预览", "PREVIEW");
    public string Left(string value) => T($"剩余 {value}", $"{value} left");
    public string Used(string value) => T($"已用 {value}", $"{value} used");
    public string ClosePinnedHint => T("Esc / 点击外部", "Esc / click outside");
    public string PinHint => T("点击固定", "Click to pin");
    public string ResetUnavailable => T("暂无重置时间", "Reset unavailable");
    public string CodexTokenTitle => T("本机用量", "Local usage");
    public string CodexTokenPopoverSubtitle(bool pinned) => pinned
        ? T("已固定", "PINNED")
        : T("点击固定", "CLICK TO PIN");
    public (string Label, string Value) CodexTodayTokens(long tokens) =>
        (T("今日 Token", "Today tokens"), FormatTokenCount(tokens));
    public (string Label, string Value) CodexLocalTokens(long tokens) =>
        (T("本机累计", "Local total"), FormatTokenCount(tokens));
    public (string Label, string Value) CodexTodayCacheHitRate(double? percent) =>
        (T("今日命中率", "Today cache hit"), FormatCacheHitPercent(percent));
    public (string Label, string Value) CodexTotalCacheHitRate(double? percent) =>
        (T("总计命中率", "Total cache hit"), FormatCacheHitPercent(percent));
    public string CodexTokenMetricTitle => T("Token 用量", "Tokens");
    public string CodexCacheMetricTitle => T("缓存命中率", "Cache hit");
    public string CodexTodayMetricLabel => T("今日", "Today");
    public string CodexYesterdayMetricLabel => T("昨日", "Yesterday");
    public string CodexTotalMetricLabel => T("累计", "Total");
    public string CodexLast7DaysMetricLabel => T("7 日", "7d");
    public string CodexLast30DaysMetricLabel => T("30 日", "30d");
    public string CodexSpendMetricTitle => T("API 等值估算", "API equivalent estimate");
    public string CodexCompactSpendMetricTitle => T("API 等值", "API equivalent");
    public string CodexGlobalStatistics => T("全局统计 · 不随筛选变化", "Global totals · unaffected by filters");
    public string CodexSpendHistoryTitle => T("消费历史", "Spend history");
    public string CodexSpendHistoryBack => T("‹ 返回", "‹ Back");
    public string CodexSpendHistoryAction => T("历史 ›", "History ›");
    public string CodexSpendTrendTitle => T("30 日趋势", "30-day trend");
    public string CodexSpendModelsTitle => T("模型构成 · 30 日", "Models · 30d");
    public string CodexUnknownModel => T("未知模型", "Unknown model");
    public string CodexSpendHistorySubtitle(int sessionCount) => T(
        $"{CodexSessionCount(sessionCount)} · 本机日志 · API 等值估算",
        $"{CodexSessionCount(sessionCount)} · local logs · API equivalent");
    public string CodexSpendDate(string localDate)
    {
        if (!DateOnly.TryParseExact(
                localDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return localDate;
        }
        return _zh
            ? $"{date.Month}月{date.Day}日"
            : date.ToString("MMM d", CultureInfo.InvariantCulture);
    }
    public string CodexTokenScope(int sessionCount)
    {
        var count = sessionCount.ToString("N0", CultureInfo.InvariantCulture);
        return T($"{count} 个会话 · 未按账号拆分", $"{count} sessions · not split by account");
    }
    public string CodexTokenScope(
        int sessionCount,
        CodexSpendPeriod? todaySpend,
        CodexSpendPeriod? last30DaysSpend)
    {
        return T(
            $"{CodexSessionCount(sessionCount)} · API等值 今{CodexApiEquivalent(todaySpend)} · 30日{CodexApiEquivalent(last30DaysSpend)}",
            $"{CodexSessionCount(sessionCount)} · API eq. Today {CodexApiEquivalent(todaySpend)} · 30d {CodexApiEquivalent(last30DaysSpend)}");
    }
    public string CodexSessionCount(int sessionCount)
    {
        var count = sessionCount.ToString("N0", CultureInfo.InvariantCulture);
        return T($"{count} 会话", $"{count} sessions");
    }
    public string CodexApiEquivalent(CodexSpendPeriod? period)
    {
        if (period is null or { HasUsage: false }) return "—";
        if (period.ApiEquivalentUsd is not { } value) return T("未定价", "unpriced");
        var amount = value == 0m
            ? "$0.00"
            : value < 0.01m
                ? "<$0.01"
                : "$" + value.ToString(value >= 100m ? "#,##0.0" : "0.00", CultureInfo.InvariantCulture);
        return "≈" + amount + (period.HasUnpricedUsage ? "+" : string.Empty);
    }
    public string CodexAccountsHeading => T("Codex 账号", "Codex accounts");
    public (string Label, string Value, string Detail)? CodexQuotaTokenUsage(CodexQuotaTokenSummary? summary)
    {
        if (summary is not { HasCurrentObservation: true, CurrentObservedTokens: { } tokens }
            || tokens < 0)
        {
            return null;
        }

        var value = FormatTokenCount(tokens);
        var span = summary.CurrentObservedSpanPercent;
        if (tokens > 0
            && summary.CurrentCapacityTokens is { } capacity && capacity > 0
            && span is { } samplePercent && double.IsFinite(samplePercent)
            && samplePercent >= CodexQuotaTokenTracker.MinimumCurrentSpanPercent && samplePercent <= 100)
        {
            var sample = samplePercent.ToString("0.#", CultureInfo.InvariantCulture);
            var source = summary.IsCurrentLocalFallback
                ? T("本机", "Local")
                : summary.CoversCycleStart ? T("已用", "Used") : T("部分记录", "Partial");
            return (T("100%额度估算", "100% estimate"), "≈" + FormatTokenCount(capacity),
                T($"{source} {value} Token · 样本 {sample}%", $"{source} {value} tokens · {sample}% sample"));
        }

        var pending = span is >= 0 and < CodexQuotaTokenTracker.MinimumCurrentSpanPercent
            ? T("样本不足1%，待估算", "Estimate after a 1% sample")
            : null;
        if (summary.IsCurrentLocalFallback)
        {
            return (T("本机 Token", "Local tokens"), value,
                pending ?? (summary.CoversCycleStart
                    ? T("本周期记录 · 仅本机", "This cycle · this device only")
                    : T("部分周期记录 · 仅本机", "Partial cycle · this device only")));
        }

        return summary.CoversCycleStart
            ? (T("已用 Token", "Used tokens"), value, pending ?? T("本周期完整记录", "Full cycle · account usage"))
            : (T("已记录 Token", "Recorded tokens"), value, pending ?? T("本周期部分用量", "Partial cycle · account usage"));
    }

    internal static string FormatTokenCount(long tokens)
    {
        tokens = Math.Max(0, tokens);
        if (tokens < 1_000) return tokens.ToString(CultureInfo.InvariantCulture);
        var (divisor, suffix) = tokens switch
        {
            >= 1_000_000_000_000 => (1_000_000_000_000d, "T"),
            >= 1_000_000_000 => (1_000_000_000d, "B"),
            >= 1_000_000 => (1_000_000d, "M"),
            _ => (1_000d, "K"),
        };
        var value = tokens / divisor;
        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + suffix;
    }

    internal static string FormatCacheHitPercent(double? percent) => percent is { } value
        ? Math.Clamp(value, 0, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
        : "—";

    internal static string FormatCnyCost(decimal? amount)
    {
        if (amount is not { } value || value < 0) return "—";
        if (value == 0) return "¥0.00";
        var format = value < 0.01m ? "0.####" : "0.00";
        return "¥" + value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string FormatDecimalCacheHitPercent(decimal? percent) => percent is { } value
        ? FormatCacheHitPercent((double)value)
        : "—";

    private static string FormatGigabytes(ulong bytes)
    {
        var value = bytes / 1_073_741_824d;
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    private static string FormatBytesPerSecond(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0) return "--";
        var (divisor, suffix) = value switch
        {
            >= 1_073_741_824 => (1_073_741_824d, "GB/s"),
            >= 1_048_576 => (1_048_576d, "MB/s"),
            >= 1_024 => (1_024d, "KB/s"),
            _ => (1d, "B/s"),
        };
        var scaled = value / divisor;
        var format = scaled >= 100 ? "0" : scaled >= 10 ? "0.0" : "0.00";
        return $"{scaled.ToString(format, CultureInfo.InvariantCulture)} {suffix}";
    }

    public (string Left, string Right) QuotaPace(
        QuotaPaceEstimate? estimate,
        DateTimeOffset now,
        DateTimeOffset? resetsAt = null)
    {
        estimate = estimate?.ForDisplay(now);
        if (estimate is null)
        {
            return (T("等待额度", "Waiting for quota"), string.Empty);
        }
        var stale = estimate.ValidUntil is { } validUntil && validUntil < now;

        if (estimate.Recent is { } recent)
        {
            return (
                PaceRate(
                    stale ? T("最近预估", "Recent estimate") : TrendPrefix(recent.ObservedSpan, recent.Confidence),
                    recent.PercentPerHour),
                ProjectionText(recent.ProjectedExhaustedAt, recent.ResetsBeforeExhaustion, now,
                    recent.PercentPerHour, resetsAt));
        }

        if (estimate.Status == QuotaPaceStatus.NoMeaningfulConsumption)
        {
            var prefix = TrendPrefix(
                estimate.ObservedSpan,
                ConfidenceForSpan(estimate.ObservedSpan));
            return (T($"{prefix} · 平稳", $"{prefix} · steady"), string.Empty);
        }
        if (estimate.Status == QuotaPaceStatus.WaitingForFreshData)
        {
            if (estimate.ValidUntil is { } cycleValidUntil
                && cycleValidUntil >= now
                && estimate.Cycle is { PercentPerHour: { } fallbackRate } fallbackCycle)
            {
                return (
                    PaceRate(T("周期均速", "Cycle avg"), fallbackRate),
                    ProjectionText(
                        fallbackCycle.ProjectedExhaustedAt,
                        fallbackCycle.ResetsBeforeExhaustion,
                        now,
                        fallbackRate,
                        resetsAt));
            }
            return (T("等待新数据", "Waiting for data"), string.Empty);
        }
        if (estimate.Status == QuotaPaceStatus.Exhausted)
        {
            return (T("额度已用完", "Quota exhausted"), string.Empty);
        }
        if (estimate.Status == QuotaPaceStatus.WeeklyBlocked)
        {
            return (T("周额度受限", "Weekly limit blocked"), string.Empty);
        }
        if (estimate.Cycle is { PercentPerHour: { } rate } cycle)
        {
            return (
                PaceRate(T("周期均速", "Cycle avg"), rate),
                ProjectionText(cycle.ProjectedExhaustedAt, cycle.ResetsBeforeExhaustion, now,
                    rate, resetsAt));
        }
        return (T("趋势样本收集中", "Building trend"), string.Empty);
    }

    private string PaceRate(string prefix, double rate)
    {
        var format = rate < 10 ? "0.0" : "0";
        var value = rate.ToString(format, CultureInfo.InvariantCulture);
        return T($"{prefix} · {value}%/时", $"{prefix} · {value}%/h");
    }

    private static string CompactPercent(double remaining) => PercentFormat.Remaining(remaining);

    private string TrendPrefix(TimeSpan? span, QuotaTrendConfidence confidence)
    {
        if (span >= TimeSpan.FromHours(18)) return T("24h趋势", "24h trend");
        if (span >= TimeSpan.FromHours(4.5)) return T("6h趋势", "6h trend");
        return confidence switch
        {
            QuotaTrendConfidence.Stable => "1h",
            QuotaTrendConfidence.Normal => "30m",
            _ => T("15m初步", "15m early"),
        };
    }

    private static QuotaTrendConfidence ConfidenceForSpan(TimeSpan? span) =>
        span >= TimeSpan.FromMinutes(45)
            ? QuotaTrendConfidence.Stable
            : span >= TimeSpan.FromMinutes(24)
                ? QuotaTrendConfidence.Normal
                : QuotaTrendConfidence.Provisional;

    private string ProjectionText(
        DateTimeOffset? projectedAt,
        bool resetsBeforeExhaustion,
        DateTimeOffset now,
        double percentPerHour,
        DateTimeOffset? resetsAt)
    {
        if (resetsBeforeExhaustion)
        {
            if (resetsAt is { } reset && reset > now
                && projectedAt is { } projected && projected >= reset
                && double.IsFinite(percentPerHour) && percentPerHour > 0)
            {
                var remaining = percentPerHour * (projected - reset).TotalHours;
                if (double.IsFinite(remaining))
                {
                    var percent = CompactPercent(remaining);
                    return T($"重置约剩 {percent}", $"~{percent} at reset");
                }
            }
            return T("可撑到重置", "until reset");
        }
        if (projectedAt is not { } exhaustedAt || exhaustedAt <= now) return string.Empty;

        var compact = FormatCompactReset(exhaustedAt, now);
        return _zh
            ? $"约{compact}用完"
            : $"~{compact.Replace(" ", string.Empty, StringComparison.Ordinal)} empty";
    }

    public string ResetAt(DateTimeOffset reset, DateTimeOffset now)
    {
        var local = reset.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return T($"今天 {local:HH:mm} 重置", $"Resets today {local:HH:mm}");
        if (local.Date == today.AddDays(1)) return T($"明天 {local:HH:mm} 重置", $"Resets tomorrow {local:HH:mm}");
        return T(
            $"{local:M月d日 HH:mm} 重置",
            $"Resets {local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}");
    }

    public string Freshness(DateTimeOffset capturedAt, DateTimeOffset now)
    {
        var age = now - capturedAt;
        if (age < TimeSpan.FromMinutes(1)) return T("刚刚更新", "Updated now");
        if (age < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)age.TotalMinutes);
            return T($"{minutes} 分钟前更新", $"Updated {minutes}m ago");
        }
        return T(
            $"{capturedAt.ToLocalTime():HH:mm} 更新",
            $"Updated {capturedAt.ToLocalTime():HH:mm}");
    }

    public string FormatCompactReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "--";
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return T("现在", "now");
        if (remaining.TotalDays >= 1)
        {
            return T(
                $"{(int)remaining.TotalDays}天{remaining.Hours}时",
                $"{(int)remaining.TotalDays}d {remaining.Hours}h");
        }
        if (remaining.TotalHours >= 10)
        {
            return T($"{(int)remaining.TotalHours}时", $"{(int)remaining.TotalHours}h");
        }
        if (remaining.TotalHours >= 1)
        {
            return T(
                $"{(int)remaining.TotalHours}时{remaining.Minutes}分",
                $"{(int)remaining.TotalHours}h {remaining.Minutes}m");
        }
        var minutes = Math.Max(1, (int)Math.Round(remaining.TotalMinutes));
        return T($"{minutes}分", $"{minutes}m");
    }

    public string FormatResetCountdown(DateTimeOffset reset, DateTimeOffset now)
    {
        var compact = FormatCompactReset(reset, now);
        if (reset <= now) return compact;
        return T($"{compact}后", $"in {compact}");
    }

    public string CodexRecentTurnsTitle => T("最近对话", "Recent chats");
    public string CodexConversationsIndexing(int count) => T($"读取 {count}", $"Reading {count}");
    public string CodexHistoryCoverage(int selectedCount, int observedCount) => T(
        $"当前筛选 {selectedCount} 轮 · 已观测 {observedCount} 轮",
        $"Current filter: {selectedCount} turns · {observedCount} observed");
    public string CodexHistoryPeriod(CodexHistoryPeriod period) => period switch
    {
        ZGSTokenBar.Core.CodexHistoryPeriod.Today => T("今天", "Today"),
        ZGSTokenBar.Core.CodexHistoryPeriod.Days7 => T("近7天", "Last 7 days"),
        ZGSTokenBar.Core.CodexHistoryPeriod.Days30 => T("近30天", "Last 30 days"),
        _ => T("全部记录", "Observed"),
    };
    public string CodexMatchingTurns => T("筛选明细", "Matching turns");
    public string CodexNoMatchingTurns => T("没有匹配的对话记录", "No matching conversations");
    public string CodexAllModels => T("所有模型", "All models");
    public string CodexAllProjects => T("所有项目", "All projects");
    public string CodexHistorySearch => T("搜索对话…", "Search chats…");
    public string CodexSelectedTurns => T("筛选轮次", "Selected turns");
    public string CodexSelectedOutput => T("已记录输出", "Recorded output");
    public string CodexSelectedCost => T("API 等值估算", "API equivalent");
    public string CodexWholeTurnAverage => T("整轮均速", "Whole-turn avg");
    public string CodexTerminalCount(int count) => T($"{count} 轮已结束", $"{count} terminal turns");
    public string CodexMeasuredCount(int count) => T($"{count} 轮有记录", $"{count} measured turns");
    public string CodexSpeedSampleCount(int count) => T($"{count} 个完整样本", $"{count} complete samples");
    public string CodexEstimateOnly => T("非订阅账单 · + 为部分", "Not billed cost · + partial");
    public string CodexFilteredModelSpeeds(int count) => T("筛选模型速度 · 整轮均速", "Filtered models · whole-turn speeds")
        + (count > 6 ? T($" · 最近6/{count}组", $" · latest 6/{count} groups") : "");
    public string CodexFilteredSpeedHint => T("当前筛选中完整结束、有输出的轮次，按设置模型、强度与档位分组；未知强度或档位不进入卡片。\n均速=输出总量÷整轮总耗时；单轮最高包含思考、工具与等待。卡片显示最近6组、每组最多4种设置；下方列出全部分组。",
        "Complete output-producing selected turns, grouped by configured model, effort and tier; unknown effort/tier is omitted from cards.\nAverage = total output / total whole-turn time; best-turn speed includes reasoning, tools and waits. Cards show six recent models and four settings each; all groups are listed below.");
    public string CodexHistoryHint => T("只查询已观测并留存的轮次，最多500轮、90天，不代表全部 API 请求。日期按本机时区的开始日筛选；点击表头排序。\n实时速度与总 tok/s 使用同一 3 秒采样，≈含估算；总值不随筛选和翻页变化。整轮均速含思考、工具与等待。CSV 保留整轮指标，不保存实时值。",
        "Observed retained turns only, up to 500 turns within 90 days, not all API requests. Dates filter local turn starts; click a column to sort.\nLive speed and total tok/s share one 3-second sample; ≈ includes estimates. Filters/pages do not change the total. Whole-turn average includes reasoning, tools and waits. CSV retains whole-turn metrics, not transient live values.");
    public string CodexExportHistory => T("导出筛选记录 CSV", "Export matching rows as CSV");
    public string CodexHistoryCost(decimal? cost, bool partial)
    {
        if (cost is null) return "—";
        var amount = cost is > 0 and < 0.0001m ? "<$0.0001"
            : "$" + cost.Value.ToString(cost >= 100 ? "0" : cost >= 1 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
        return amount + (partial ? "+" : "");
    }
    public string CodexNoRecentTurns => T("暂无对话记录", "No recent conversations");
    public string[] CodexPrimaryConversationHeaders =>
        [T("对话 / 设置模型", "Chat / Configured model"), T("状态", "State"), T("开始时间", "Started"), T("新增输入", "Input"),
         T("输出", "Output"), T("缓存", "Cached"), T("缓存率", "Cache hit"), T("估算 $", "Est. $"),
         T("整轮均速", "Turn avg"), T("首字 s", "First s"), T("实时 tok/s", "Live tok/s")];
    public string[] CodexConversationHeaders =>
        [T("开始时间", "Started"), T("对话 / 模型", "Chat / Model"), T("新增输入", "Input"), T("输出", "Output"), T("缓存", "Cached"),
         T("估算 $", "Est. $"), T("整轮均速", "Turn avg"), T("首字 s", "First s")];
    public string CodexConversationHint => T(
        "实时速度使用最近 3 秒窗口，≈含生成中估算；每个会话仅最新一行参与总 tok/s。总值汇总所有已发现会话，不随筛选或翻页变化；生成和工具数按日志阶段统计。整轮均速单位也是 tok/s，包含思考、工具和等待。首字仅使用日志记录。",
        "Live speed uses the last 3 seconds; ≈ includes in-flight estimates. Only the latest row per session contributes to total tok/s, across all discovered sessions and independent of filters/pages. Generating and tools counts follow recorded phases. Turn average is also tok/s and includes reasoning, tools and waits. First-token latency requires a recorded measurement.");
    public string CodexTurnTime(CodexConversationTurn turn) =>
        turn.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string CodexTurnDate(CodexConversationTurn turn) =>
        turn.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string CodexTurnCost(CodexConversationTurn turn)
    {
        if (turn.ApiEquivalentUsd is not { } cost) return "—";
        var amount = cost is > 0 and < 0.0001m ? "<$0.0001"
            : "$" + cost.ToString(cost >= 100 ? "0" : cost >= 1 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
        return amount + (turn.CostIsPartial ? "+" : "");
    }
    public string CodexTurnLabel(CodexConversationTurn turn) => JoinKnown(turn.ProjectName, turn.Model, turn.Effort);
    private static string JoinKnown(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    public string CodexKnownTier(CodexConversationTurn turn) => turn.ServiceTier is "standard" or "fast" or "ultrafast" or "flex" or "batch"
        ? CodexTurnTier(turn) : "";
    public string CodexTurnModelDetails(CodexConversationTurn turn) => JoinKnown(turn.Model, turn.Effort, CodexKnownTier(turn));
    public string CodexTurnName(CodexConversationTurn turn) => turn.Title
        ?? $"{turn.ProjectName ?? T("对话", "Chat")} · {turn.SessionId[^Math.Min(8, turn.SessionId.Length)..]}";
    public string CodexTurnTier(CodexConversationTurn turn) => CodexServiceTier(turn.ServiceTier);
    private string CodexServiceTier(string? tier) => tier switch
    {
        "fast" => "Fast",
        "standard" => "Standard",
        "ultrafast" => "Ultrafast",
        "flex" => "Flex",
        "batch" => "Batch",
        "mixed" => T("混合档位", "Mixed"),
        _ => T("档位未知", "Unknown"),
    };
    public string CodexModelSpeedsTitle(int count) => T("近24h模型速度 · 整轮均速", "Model speeds · last 24h · whole turns")
        + (count > 6 ? T($" · 最近6/{count}组", $" · latest 6/{count} groups") : "");
    public string CodexModelSpeedLabel(CodexModelSpeed row) => $"{row.Model} · {row.Effort ?? "—"} · {CodexServiceTier(row.ServiceTier)}";
    public string CodexModelSpeedSetting(CodexModelSpeed row) => $"{row.Effort ?? "—"} · {CodexServiceTier(row.ServiceTier)}";
    public string CodexModelSpeedCompactValue(CodexModelSpeed row) => T(
        $"均 {row.AverageTokensPerSecond:0.#} · 单轮最高 {row.MaximumTurnTokensPerSecond:0.#} · {row.SampleCount}轮",
        $"Avg {row.AverageTokensPerSecond:0.#} · max turn {row.MaximumTurnTokensPerSecond:0.#} · n={row.SampleCount}");
    public string CodexModelSpeedValue(CodexModelSpeed row) => T(
        $"均≈{row.AverageTokensPerSecond:0.#} · 单轮最高≈{row.MaximumTurnTokensPerSecond:0.#} tok/s · {row.SampleCount}轮",
        $"Avg ≈{row.AverageTokensPerSecond:0.#} · best turn ≈{row.MaximumTurnTokensPerSecond:0.#} tok/s · n={row.SampleCount}");
    public string CodexModelSpeedTooltip(IReadOnlyList<CodexModelSpeed> rows) =>
        T("近24小时已记录、完整结束且有输出的轮次；按模型、强度和档位分组。\n均速=输出总量÷总耗时；最高值是单轮均速，包含工具和等待。\n",
            "Recorded complete turns with output in the last 24h, grouped by model, effort and tier.\nAverage is total output / total time; the maximum is a turn average, including tools and waits.\n")
        + string.Join('\n', CodexConversationSpeeds.GroupModels(rows).SelectMany(group => group.Rows)
            .Select(row => CodexModelSpeedLabel(row) + "\n" + CodexModelSpeedValue(row)));
    public string CodexTurnState(CodexConversationTurn turn) => turn.State switch
    {
        "interrupted" => T("已中断", "Interrupted"),
        "completed" => T("已结束", "Ended"),
        "stale" => T("待更新", "Waiting"),
        _ => T("运行中", "Running"),
    };
    public string CodexTurnSpeedNote => T("含思考、工具和等待时间；不是纯生成速度", "Includes reasoning, tools and waiting; not decode speed");
    public string CodexTurnLiveSpeed(CodexConversationTurn turn) => turn.LiveRate is { } live
        ? (live.Estimated > 0 ? "≈" : "") + live.Total.ToString("0.#", CultureInfo.InvariantCulture) : "—";
    public string CodexTurnDetail(CodexConversationTurn turn) => T(
        $"{turn.SessionId[^Math.Min(8, turn.SessionId.Length)..]} · {CodexTurnDate(turn)} {CodexTurnTime(turn)} · 输出 {turn.OutputTokens:N0} · {CodexTurnState(turn)}",
        $"{turn.SessionId[^Math.Min(8, turn.SessionId.Length)..]} · {CodexTurnDate(turn)} {CodexTurnTime(turn)} · out {turn.OutputTokens:N0} · {CodexTurnState(turn)}");
    public string CodexTurnTooltip(CodexConversationTurn turn)
    {
        var lines = new List<string> { CodexTurnName(turn) };
        var model = CodexTurnModelDetails(turn);
        if (model.Length > 0) lines.Add(T("设置模型 ", "Configured model ") + model);
        lines.Add(turn.SessionId);
        lines.Add($"{CodexTurnDate(turn)} {CodexTurnTime(turn)} · {CodexTurnState(turn)}");
        var metrics = new List<string>();
        if (turn.NewInputTokens is { } input) metrics.Add(T($"新增输入 {input:N0}", $"New input {input:N0}"));
        if (turn.UsageComplete || turn.OutputTokens > 0)
            metrics.Add(T($"输出 {turn.OutputTokens:N0}", $"Output {turn.OutputTokens:N0}") + (turn.UsageComplete ? "" : "+"));
        if (turn.CachedInputTokens is { } cached) metrics.Add(T($"缓存 {cached:N0}", $"Cached {cached:N0}"));
        if (turn.ApiEquivalentUsd is not null) metrics.Add(T("估算 ", "Estimate ") + CodexTurnCost(turn));
        if (metrics.Count > 0) lines.Add(string.Join(" · ", metrics));
        if (turn.CacheHitPercent is not null) lines.Add(T("缓存率 ", "Cache hit ") + FormatCacheHitPercent(turn.CacheHitPercent));
        if (turn.ResponseModels is { Length: > 0 } response) lines.Add(T("响应模型 ", "Response model ") + response);
        if (turn.RoutedToModel is { } target) lines.Add(T("最近路由通知 ", "Latest routing notice ")
            + $"{turn.RoutedFromModel} → {target}" + (turn.RoutingReason is { Length: > 0 } reason ? " · " + reason : ""));
        if (double.IsFinite(turn.ElapsedSeconds) && turn.ElapsedSeconds > 0)
            lines.Add(T($"整轮耗时 {turn.ElapsedSeconds:0.0}s", $"Turn duration {turn.ElapsedSeconds:0.0}s"));
        if (turn.FirstTokenSeconds is { } first) lines.Add(T($"首 Token {first:0.0}s", $"First token {first:0.0}s"));
        if (turn.LiveRate is { } live)
            lines.Add(T($"实时 {CodexTurnLiveSpeed(turn)} tok/s · 实测 {live.Measured:0.#} · 估算 {live.Estimated:0.#}（3 秒窗口）",
                $"Live {CodexTurnLiveSpeed(turn)} tok/s · recorded {live.Measured:0.#} · estimated {live.Estimated:0.#} (3-second window)"));
        if (turn.OutputTokensPerSecond is { } average) lines.Add(T($"整轮均速 {average:0.#} tok/s · ", $"Whole-turn average {average:0.#} tok/s · ") + CodexTurnSpeedNote);
        return string.Join('\n', lines);
    }

    public string QuotaMilestoneTitle(
        string card,
        string window,
        int threshold) => T(
        $"{card} {WindowLimit(window)}达到 {threshold}%",
        $"{card} {WindowLimit(window)} reached {threshold}%");

    public string QuotaMilestonesTitle(int count) => T(
        $"{count} 个配额里程碑已达到",
        $"{count} quota milestones reached");

    public string QuotaMilestoneDetail(
        int threshold,
        int used,
        int remaining,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        bool includeCurrent)
    {
        var current = includeCurrent
            ? T($"当前已用 {used}%", $"Now {used}% used")
            : T($"{threshold}% 里程碑", $"{threshold}% milestone");
        var reset = resetsAt is null
            ? string.Empty
            : resetsAt <= now
                ? T(" · 现在重置", " · resets now")
                : T($" · {FormatCompactReset(resetsAt, now)}后重置", $" · resets in {FormatCompactReset(resetsAt, now)}");
        return T(
            $"{current} · 剩余 {remaining}%{reset}",
            $"{current} · {remaining}% left{reset}");
    }

    public string MoreAlerts(int count) => T($"+ 另有 {count} 个", $"+{count} more");
    public string WindowLimit(string label) => label is "1w" or "week"
        ? T("每周限制", "weekly limit")
        : T($"{label} 限制", $"{label} limit");

    public static string TruncateTextElements(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        if (maxLength <= 0) return string.Empty;
        if (maxLength == 1) return "…";
        var builder = new StringBuilder(maxLength);
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (builder.Length + element.Length > maxLength - 1) break;
            builder.Append(element);
        }
        return builder.Append('…').ToString();
    }

    private string RateLimited(string provider, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        if (retryAt is null)
        {
            return T($"{provider} API 受到速率限制。", $"{provider} API is rate limited.");
        }
        return T(
            $"{provider} API 受到速率限制，{FormatCompactReset(retryAt, now)}后重试。",
            $"{provider} API is rate limited. Retry in {FormatCompactReset(retryAt, now)}.");
    }

    private string T(string zh, string en) => _zh ? zh : en;
}
