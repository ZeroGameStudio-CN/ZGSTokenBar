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
    public string KeepRunning => T("常驻运行", "Keep running");
    public string KeepRunningHint => T(
        "同时随 Windows 启动；退出或异常结束后自动重启。关闭此选项后恢复普通退出。",
        "Also start with Windows and restart after exit or failure. Turn this off to allow a normal exit.");
    public string UsageAlertsTitle => T("用量提醒", "Usage alerts");
    public string UsageAlerts => T(
        "用量达到 25 / 50 / 75 / 90 / 100% 时提醒",
        "Usage alerts at 25 / 50 / 75 / 90 / 100%");
    public string Display => T("外观", "Appearance");
    public string CodexDisplayMode => T("Codex 显示模式", "Codex display mode");
    public string CodexDisplayModeHint => T(
        "总池保持现有高度，并汇总多个 Codex 账号的用量。",
        "Pool keeps the current height and aggregates usage across multiple Codex accounts.");
    public string CodexDisplayModeAccounts => T("分账号", "Per account");
    public string CodexDisplayModePool => T("总池", "Pool");
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
    public string RadarSectionTitle => "Codex Radar";
    public string RadarNavigation => T("雷达", "Radar");
    public string RadarPreviewBadge => T("预览", "PREVIEW");
    public string RadarNetworkHint => T(
        "第三方、非官方；仅启用 Radar 时请求公开快照。",
        "Third-party, unofficial; public snapshots are requested only while Radar is enabled.");
    public string ShowRadarTitle => T("显示 Radar", "Show Radar");
    public string ShowRadar => T(
        "悬停 Codex 图标显示 Radar（第三方、非官方）",
        "Show Codex Radar on logo hover (third-party, unofficial)");
    public string RadarAlerts => T(
        "App 运行时每分钟检查并发送 Windows 提醒",
        "Check every minute and show Windows alerts while this app runs");
    public string RadarAlertsTitle => T("Radar 告警", "Radar alerts");
    public string TestRadarNotification => T("测试 Radar 通知", "Test Radar notification");
    public string RadarTestNotificationHint => T(
        "立即发送一次测试；无需先保存，不会启动轮询。",
        "Sends one test now; no save required and polling is not started.");
    public string RadarRunsWhileOpen => T(
        "仅在 ZGSTokenBar 运行时工作。",
        "Only runs while ZGSTokenBar is open.");
    public string AllowClaudeRefresh => T(
        "Claude OAuth 过期时允许刷新",
        "Allow Claude OAuth refresh when expired");
    public string ClaudeRefreshHint => T(
        "默认开启；关闭后由 Claude Code 负责刷新。",
        "On by default; disable to leave refresh to Claude Code.");
    public string CodexEconomyBarTitle => T("Bar 快捷组件", "Bar quick control");
    public string CodexEconomyBarHint => T(
        "在 Bar 显示可独立折叠、排序并快速切换关闭 / 询问 / 开启的组件。",
        "Show an independently collapsible and reorderable Bar control for switching Off / Ask / On.");
    public string CodexEconomyBarAreaTitle => T("经济", "Economy");
    public string CodexEconomyBarMenuTitle => T("经济模式", "Economy mode");
    public string CodexEconomyBarMenuHint => T("仅影响后续新任务", "Affects new tasks only");
    public string CodexEconomyBarModeDescription(CodexEconomyMode mode) => mode switch
    {
        CodexEconomyMode.Off => T("不自动使用经济子代理", "Don't auto-use economy subagents"),
        CodexEconomyMode.Ask => T("使用前先询问", "Ask before using one"),
        CodexEconomyMode.On => T("优先使用 Luna Max", "Prefer Luna Max"),
        _ => string.Empty,
    };
    public string CodexEconomyDialogTitle => T("Codex 经济模式管理", "Manage Codex economy mode");
    public string CodexEconomyDialogDescription => T(
        "每次应用只更新下方明确选中的 Codex Profile，并仅影响后续新任务。",
        "Each Apply updates only the explicitly selected Codex profile below and affects new tasks only.");
    public string CodexEconomyProfileLabel => "Codex Profile";
    public string CodexEconomyProfileHint => T(
        "选择要读取和管理的本机 Codex Home。",
        "Choose the local Codex Home to inspect and manage.");
    public string CodexEconomyModeLabel => T("模式", "Mode");
    public string CodexEconomyModeHint => T(
        "选择后不会立即写入；只有点击“应用”才会更改配置。",
        "Choosing a mode does not write immediately; configuration changes only after Apply.");
    public string CodexEconomyCurrentStatus => T("当前状态", "Current status");
    public string CodexEconomyConfigPath => T("基础配置路径", "Base config path");
    public string CodexEconomySkillPath => T("用户级 Skill 路径", "User-level Skill path");
    public string CodexEconomyApply => T("应用", "Apply");
    public string CodexEconomyApplyHint => T(
        "将所选模式写入当前 Profile，并立即回读验证。",
        "Write the selected mode to the current profile and immediately verify it by reading it back.");
    public string CodexEconomyNoProfiles => T(
        "未发现可管理的 Codex Profile。",
        "No manageable Codex profiles were found.");
    public string CodexEconomyReadFailed(string detail) => T(
        $"无法读取当前 Profile：{detail}",
        $"Could not inspect the current profile: {detail}");
    public string CodexEconomyApplyFailedTitle => T("未能应用 Codex 经济模式", "Codex economy mode was not applied");
    public string CodexEconomyApplyFailed(string detail) => T(
        $"目标未通过写入后回读验证。请检查路径和配置后重试。\n\n{detail}",
        $"The target did not pass post-write read-back verification. Check the paths and configuration, then try again.\n\n{detail}");
    public string CodexEconomyReadBackMismatch(CodexEconomyMode expected, CodexEconomyMode actual) => T(
        $"回读模式不匹配：期望 {CodexEconomyModeName(expected)}，实际 {CodexEconomyModeName(actual)}。",
        $"Read-back mode mismatch: expected {CodexEconomyModeName(expected)}, got {CodexEconomyModeName(actual)}.");
    public string CodexEconomyProfileChoice(CodexEconomyProfile profile) =>
        $"{profile.DisplayName} — {profile.HomeDirectory}";
    public string CodexEconomyModeName(CodexEconomyMode mode) => mode switch
    {
        CodexEconomyMode.Off => T("关闭", "Off"),
        CodexEconomyMode.Ask => T("询问", "Ask"),
        CodexEconomyMode.On => T("开启", "On"),
        CodexEconomyMode.Inconsistent => T("配置冲突", "Inconsistent"),
        _ => T("未配置", "Unconfigured"),
    };
    public string CodexEconomyModeDescription(CodexEconomyMode mode) => mode switch
    {
        CodexEconomyMode.Off => T(
            "禁用该 Skill，并移除本工具管理的子代理默认值。",
            "Disable the Skill and remove subagent defaults managed by this tool."),
        CodexEconomyMode.Ask => T(
            "启用该 Skill，但不设置默认子代理模型。",
            "Enable the Skill without setting a default subagent model."),
        CodexEconomyMode.On => T(
            "启用该 Skill，并为后续子代理设置 Luna Max 默认值。",
            "Enable the Skill and set Luna Max defaults for future subagents."),
        _ => string.Empty,
    };
    public string CodexEconomyInstalled(bool installed) => installed
        ? T("已安装", "Installed")
        : T("未安装", "Not installed");
    public string CodexEconomyNamedLayers(bool found) => found
        ? T("命名层：覆盖警告", "Named-layer warning")
        : T("无命名层", "No named layers");
    public string CodexEconomyNamedLayersDetail(bool found) => found
        ? T(
            "检测到顶层 *.config.toml；使用命名配置层的任务可能覆盖基础 config.toml。",
            "Top-level *.config.toml files were found; tasks using a named layer may override the base config.toml.")
        : T(
            "当前 Codex Home 未检测到顶层 *.config.toml。",
            "No top-level *.config.toml files were detected in this Codex Home.");
    public string CodexEconomyNamedLayersUnknown => T(
        "命名配置层状态未知。",
        "Named config layer status is unavailable.");
    public string CodexEconomyStatusSummary(CodexEconomyStatus? status)
    {
        if (status is null)
        {
            return T(
                "模式：不可用 · Skill：未知 · 命名配置层：未知",
                "Mode: unavailable · Skill: unknown · named layers: unknown");
        }

        return T(
            $"模式：{CodexEconomyModeName(status.Mode)} · Skill：{CodexEconomyInstalled(status.SkillInstalled)} · {CodexEconomyNamedLayers(status.HasNamedConfigLayers)}",
            $"Mode: {CodexEconomyModeName(status.Mode)} · Skill: {CodexEconomyInstalled(status.SkillInstalled)} · {CodexEconomyNamedLayers(status.HasNamedConfigLayers)}");
    }
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
    public string OpenSettingsHint => T("打开设置面板", "Open settings panel");
    public string OpenRadarWebsite => T("打开 Codex Radar 网站", "Open Codex Radar website");
    public string Settings => T("设置", "Settings");
    public string Quit => T("退出", "Quit");
    public string TrayText => T("ZGSTokenBar — 实时配额", "ZGSTokenBar — live limits");
    public string SettingsNotSaved => T("设置未保存", "Settings not saved");
    public string SettingsSaveFailed => T(
        "ZGSTokenBar 无法保存设置。请检查 App 数据目录权限后重试。",
        "ZGSTokenBar could not save settings. Check access to the app data folder and try again.");

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
        ? T("已固定 · Esc / 点击外部", "PINNED · ESC / CLICK OUTSIDE")
        : T("每秒采样 · 点击固定", "1S SAMPLE · CLICK TO PIN");
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
            $"最忙引擎 {engine} · {processCount} 个活动进程",
            $"Busiest {engine} engine · {processCount} active processes");
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
            $"活动 {FormatUsagePercent(activePercent)} · 读 {FormatBytesPerSecond(readBytesPerSecond)} · 写 {FormatBytesPerSecond(writeBytesPerSecond)}",
            $"Active {FormatUsagePercent(activePercent)} · R {FormatBytesPerSecond(readBytesPerSecond)} · W {FormatBytesPerSecond(writeBytesPerSecond)}");
    }
    public string SystemUsageCapturedAt(DateTimeOffset capturedAt) => T(
        $"采样 {capturedAt.ToLocalTime():HH:mm:ss}",
        $"Sampled {capturedAt.ToLocalTime():HH:mm:ss}");
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
    public string CodexTokenTitle => T("本机 Codex Token", "Local Codex tokens");
    public string CodexTokenPopoverSubtitle(bool pinned) => pinned
        ? T("已固定 · Esc / 点击外部", "PINNED · ESC / CLICK OUTSIDE")
        : T("本机日志 · 点击固定", "LOCAL LOG · CLICK TO PIN");
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
    public string CodexTotalMetricLabel => T("累计", "Total");
    public string CodexTokenRadarMetricTitle => T("Token", "Tokens");
    public string CodexCacheRadarMetricTitle => T("缓存", "Cache");
    public string AiGatewayTokenRadarMetricTitle => T("Token", "Tokens");
    public string AiGatewayCacheRadarMetricTitle => T("缓存", "Cache");
    public string CodexTokenScope(int sessionCount)
    {
        var count = sessionCount.ToString("N0", CultureInfo.InvariantCulture);
        return T($"{count} 个会话 · 未按账号拆分", $"{count} sessions · not split by account");
    }
    public string CodexAccountsHeading => T("Codex 账号", "Codex accounts");
    public string CodexQuotaCapacityTitle => T("Token · 原始用量", "Tokens · raw usage");
    public string[] CodexQuotaCapacityMetrics(
        CodexQuotaTokenSummary? summary,
        double? currentUsedPercent = null) =>
    [
        CodexQuotaUsedMetric(summary, currentUsedPercent),
        T(
            $"样本100% {EstimatedTokenValue(summary?.CurrentCapacityTokens, collecting: true)}",
            $"Sample 100% {EstimatedTokenValue(summary?.CurrentCapacityTokens, collecting: true)}"),
        T(
            summary?.RecentWeeklyAverageTokens is { } recentWeeklyAverageTokensZh
                ? $"近4周/周 ≈{CapacityTokenValue(recentWeeklyAverageTokensZh)}"
                : "近4周/周 —",
            summary?.RecentWeeklyAverageTokens is { } recentWeeklyAverageTokensEn
                ? $"4wk/week ≈{CapacityTokenValue(recentWeeklyAverageTokensEn)}"
                : "4wk/week —"),
        T(
            $"周期平均 {EstimatedTokenValue(summary?.AverageCapacityTokens)}",
            $"Cycle avg {EstimatedTokenValue(summary?.AverageCapacityTokens)}"),
        T(
            $"周期最高 {EstimatedTokenValue(summary?.MaxCapacityTokens)}",
            $"Cycle max {EstimatedTokenValue(summary?.MaxCapacityTokens)}"),
        T(
            $"完整周期 {summary?.CompletedCycleCount ?? 0}",
            $"Full cycles {summary?.CompletedCycleCount ?? 0}"),
    ];

    internal string CodexQuotaObservationEvidence(CodexQuotaTokenSummary? summary)
    {
        if (summary?.HasCurrentObservation != true
            || summary.CurrentObservedTokens is null)
        {
            return string.Empty;
        }

        if (summary.IsCurrentLocalFallback)
        {
            return T("本机下限", "Local floor");
        }

        return summary.CoversCycleStart
            ? T("账号实录", "Account seen")
            : T("账号估算", "Account est.");
    }

    private string CodexQuotaUsedMetric(
        CodexQuotaTokenSummary? summary,
        double? currentUsedPercent)
    {
        var percent = FormatUsagePercent(
            currentUsedPercent
            ?? (summary?.CoversCycleStart == true
                ? summary.CurrentObservedSpanPercent
                : null));
        var observedTokens = summary?.HasCurrentObservation == true
            ? summary.CurrentObservedTokens
            : null;
        var estimatedTokens = summary?.HasCurrentObservation == true
            ? summary.EstimateUsedTokens(currentUsedPercent)
            : null;
        var (tokens, approximation) = summary?.IsCurrentLocalFallback == true
            ? (CapacityTokenValue(observedTokens, collecting: true), observedTokens is not null ? "≥" : string.Empty)
            : summary?.CoversCycleStart == true
                ? (CapacityTokenValue(observedTokens, collecting: true), string.Empty)
                : (CapacityTokenValue(estimatedTokens, collecting: true), estimatedTokens is not null ? "≈" : string.Empty);
        return T(
            $"已用{percent} {approximation}{tokens}",
            $"Used {percent} {approximation}{tokens}");
    }

    private string EstimatedTokenValue(double? tokens, bool collecting = false) =>
        tokens is not null
            ? "≈" + CapacityTokenValue(tokens)
            : CapacityTokenValue(null, collecting);
    private string CapacityTokenValue(double? tokens, bool collecting = false) => tokens is { } value
        ? FormatTokenCount((long)Math.Round(Math.Max(0, value), MidpointRounding.AwayFromZero))
        : collecting ? T("记录中", "collecting") : "—";

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

    private static string FormatUsagePercent(double? percent) => percent is { } value
        ? $"{Math.Clamp(value, 0, 100).ToString("0.#", CultureInfo.InvariantCulture)}%"
        : "--";

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
        DateTimeOffset now)
    {
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
                ProjectionText(recent.ProjectedExhaustedAt, recent.ResetsBeforeExhaustion, now));
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
                        now));
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
                ProjectionText(cycle.ProjectedExhaustedAt, cycle.ResetsBeforeExhaustion, now));
        }
        return (T("趋势样本收集中", "Building trend"), string.Empty);
    }

    public (string Left, string Right) QuotaCycle(QuotaPaceEstimate? estimate)
    {
        if (estimate?.Status == QuotaPaceStatus.WeeklyBlocked)
        {
            return (T("周重置后恢复", "Returns after weekly reset"), string.Empty);
        }
        if (estimate?.Cycle is not { } cycle)
        {
            return (T("周期不可用", "No cycle"), string.Empty);
        }

        var absoluteDelta = Math.Abs(cycle.DeltaPercent);
        var delta = absoluteDelta.ToString(absoluteDelta < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        var left = absoluteDelta < 2
            ? T("周期正常", "Cycle OK")
            : cycle.DeltaPercent > 0
                ? T($"周期超额 {delta}%", $"Cycle {delta}% over")
                : T($"周期余量 {delta}%", $"Cycle {delta}% spare");
        if (estimate.Recent is { ResetsBeforeExhaustion: false })
        {
            return (left, T("近期过快", "Recent too fast"));
        }
        var actualUsed = cycle.ExpectedUsedPercent + cycle.DeltaPercent;
        if (actualUsed <= .001)
        {
            return (left, T("可维持", "Can maintain"));
        }
        if (cycle.SafeSpeedMultiplier is { } multiplier
            && multiplier < 1
            && !cycle.ResetsBeforeExhaustion)
        {
            var safe = Math.Clamp(multiplier, .1, 9.9)
                .ToString("0.0", CultureInfo.InvariantCulture);
            return (left, T($"需≤{safe}×", $"Need ≤{safe}×"));
        }
        return cycle.PercentPerHour is null
            ? (left, string.Empty)
            : (left, T("可维持", "Pace OK"));
    }

    public (string Left, string Right) QuotaDailyGoal(
        double targetRemaining,
        double actualRemaining,
        bool recentTooFast = false)
    {
        var target = CompactPercent(targetRemaining);
        var left = T($"今晚目标 {target}", $"Midnight goal {target}");
        if (recentTooFast)
        {
            return (left, T("近期过快", "Recent too fast"));
        }

        var difference = actualRemaining - targetRemaining;
        if (Math.Abs(difference) < .05)
        {
            return (left, T("已达标", "On target"));
        }

        var gap = CompactPercent(Math.Abs(difference));
        return difference > 0
            ? (left, T($"余量 {gap}", $"{gap} spare"))
            : (left, T($"超额 {gap}", $"{gap} over"));
    }

    private string PaceRate(string prefix, double rate)
    {
        var format = rate < 10 ? "0.0" : "0";
        var value = rate.ToString(format, CultureInfo.InvariantCulture);
        return T($"{prefix} · {value}%/时", $"{prefix} · {value}%/h");
    }

    private static string CompactPercent(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped > 0 && clamped < 1) return "<1%";
        return $"{clamped.ToString(clamped < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)}%";
    }

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
        DateTimeOffset now)
    {
        if (resetsBeforeExhaustion) return T("可撑到重置", "until reset");
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

    public string RadarTitle => "Codex Radar";
    public string DeepSeekRadarTitle => "DeepSeek Radar";
    public string RadarPopoverSubtitle(bool pinned) => pinned
        ? T("已固定 · Esc / 点击外部", "PINNED · ESC / CLICK OUTSIDE")
        : T("非官方 · 点击固定", "UNOFFICIAL · CLICK TO PIN");
    public string RadarLoading => T("正在加载 Codex Radar…", "Loading Codex Radar…");
    public string RadarHoverToFetch => T(
        "悬停后获取最新快照。",
        "Hover to fetch the latest snapshot.");
    public string RadarNever => T("从未", "never");
    public string RadarModelHeader => T("模型", "MODEL");
    public string RadarIqHeader => "IQ";
    public string RadarIqAverageHeader => T("24H均", "24H AVG");
    public string RadarSampleHeader => T("样本", "N");
    public string RadarPassHeader => T("通过/有效", "PASS/VALID");
    public string RadarAverageHeader => T("平均耗时", "AVG/TASK");
    public string RadarCostHeader => T("单任务成本", "$/TASK");
    public string RadarSource => T("来源", "source");
    public string RadarStable => T("稳定", "stable");
    public string RadarWatch => T("关注", "watch");
    public string RadarDegraded => T("降级", "degraded");
    public string RadarUnknown => T("未知", "unknown");
    public string RadarUnknownStatusLegend => T("状态未知", "status unknown");
    public string RadarConfidenceNote => T("N≥50 · 95%下界评分", "N≥50 · 95% lower-bound picks");
    public string RadarStrongestTitle => T("最强", "STRONGEST");
    public string RadarDailyScenarioTitle => T("日常", "DAILY");
    public string RadarPlanningScenarioTitle => T("规划", "PLAN");
    public string RadarExecutionScenarioTitle => T("执行", "EXEC");
    public string RadarBackgroundScenarioTitle => T("后台", "BG");

    public string RadarState(RadarViewState state, DateTimeOffset now)
    {
        var fresh = !state.IsStale(now) && state.Error is null;
        return state.Loading
            ? T("同步中", "SYNC")
            : state.Error is not null
                ? T("错误", "ERROR")
                : fresh
                    ? T("最新", "FRESH")
                    : T("已过期", "STALE");
    }

    public string RadarChecked(DateTimeOffset? checkedAt, DateTimeOffset now)
    {
        if (checkedAt is null) return RadarNever;
        var local = checkedAt.Value.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : T(
                local.ToString("M月d日 HH:mm"),
                local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    public string RadarResetWindow(RadarResetWindow? window, DateTimeOffset now)
    {
        if (window is null) return string.Empty;
        if (window.Open)
        {
            var title = RadarResetWindowTitle(window);
            var detail = RadarResetWindowDetail(window, now);
            return detail.Length == 0 ? title : $"{title} · {detail}";
        }

        var resetAt = window.ClosedAt ?? window.OpenedAt;
        if (resetAt is null) return string.Empty;
        var local = resetAt.Value.ToLocalTime();
        return T(
            $"硬重置 · {local:M/d HH:mm}",
            $"RESET · {local:MM-dd HH:mm}");
    }

    public string RadarResetWindowTitle(RadarResetWindow? window) =>
        window?.Open == true
            ? T("重置窗口已开启", "RESET WINDOW OPEN")
            : string.Empty;

    public string RadarResetMiniAreaTitle => T("重置", "Reset");

    public string RadarResetMiniTitle(RadarResetWindow? window, DateTimeOffset now)
    {
        var timing = RadarResetTiming.Resolve(window);
        return timing.Kind == RadarResetTimingKind.EstimatedDate
            && timing.EstimatedDate is { } estimatedDate
                ? T(
                    $"推测 {estimatedDate.ToString("M/d", CultureInfo.InvariantCulture)}",
                    $"EST {estimatedDate.ToString("MM-dd", CultureInfo.InvariantCulture)}")
                : T("重置", "RESET");
    }

    public string RadarResetMiniValue(RadarResetWindow? window, DateTimeOffset now)
    {
        var timing = RadarResetTiming.Resolve(window);
        return timing.Kind switch
        {
            RadarResetTimingKind.Exact when timing.ExactTargetAt is { } exactTarget =>
                exactTarget <= now
                    ? T("待确认", "AWAITING")
                    : FormatResetCountdown(exactTarget - now),
            RadarResetTimingKind.EstimatedDate => RadarEstimatedDateValue(timing, now),
            _ => T("时间未定", "TIME TBA"),
        };
    }

    public string RadarResetMiniCompact(RadarResetWindow? window, DateTimeOffset now)
    {
        var timing = RadarResetTiming.Resolve(window);
        if (timing.Kind == RadarResetTimingKind.Exact && timing.ExactTargetAt is { } exactTarget)
        {
            var remaining = exactTarget - now;
            if (remaining <= TimeSpan.Zero) return "!";
            if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h";
            if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m";
            return "<1m";
        }
        if (timing.Kind == RadarResetTimingKind.EstimatedDate)
        {
            return timing.CalendarDaysUntil(now) switch
            {
                > 0 and var days => $"~{days}d",
                0 => "~0d",
                _ => "!",
            };
        }
        return "?";
    }

    public string RadarResetWindowDetail(RadarResetWindow? window, DateTimeOffset now)
    {
        if (window?.Open != true) return string.Empty;
        var timing = RadarResetTiming.Resolve(window);
        if (timing.Kind == RadarResetTimingKind.Exact && timing.ExactTargetAt is { } targetAt)
        {
            var remaining = targetAt - now;
            if (remaining <= TimeSpan.Zero)
            {
                return T(
                    "等待官方确认重置完成",
                    "AWAITING OFFICIAL RESET CONFIRMATION");
            }

            return T(
                $"距离预计重置 {FormatResetCountdown(remaining)}",
                $"EXPECTED RESET IN {FormatResetCountdown(remaining)}");
        }

        if (timing.Kind == RadarResetTimingKind.EstimatedDate
            && timing.EstimatedDate is { } estimatedDate)
        {
            var remaining = RadarEstimatedDateValue(timing, now);
            return T(
                $"推测重置 {estimatedDate.ToString("M/d", CultureInfo.InvariantCulture)} · {remaining}",
                $"ESTIMATED RESET {estimatedDate.ToString("MM-dd", CultureInfo.InvariantCulture)} · {remaining}");
        }

        return T("时间未定", "TIME TBA");
    }

    private string RadarEstimatedDateValue(RadarResetTiming timing, DateTimeOffset now)
    {
        return timing.CalendarDaysUntil(now) switch
        {
            > 0 and var days => T($"约{days}天", $"~{days}d"),
            0 => T("今天", "TODAY"),
            _ => T("待确认", "AWAITING"),
        };
    }

    private static string FormatResetCountdown(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (long)Math.Floor(remaining.TotalSeconds));
        return FormattableString.Invariant(
            $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}");
    }

    public string RadarAverageTime(RadarModel model)
    {
        if (model.AverageTaskSeconds is { } seconds
            && double.IsFinite(seconds)
            && seconds >= 0)
        {
            if (seconds < 60) return T("<1分", "<1m");
            var minutes = Math.Max(
                1,
                (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero));
            return minutes < 60
                ? T($"{minutes}分", $"{minutes}m")
                : T($"{minutes / 60}时{minutes % 60:00}分", $"{minutes / 60}h {minutes % 60:00}m");
        }
        return string.IsNullOrWhiteSpace(model.WallTime) ? "—" : model.WallTime;
    }

    public string RadarError(RadarErrorCode code) => code switch
    {
        RadarErrorCode.Timeout => T("Radar 请求超时。", "Radar request timed out."),
        RadarErrorCode.SchemaChanged => T("Radar 响应格式已变化。", "Radar response format changed."),
        RadarErrorCode.StateSaveFailed => T("Radar 状态无法保存。", "Radar state could not be saved."),
        _ => T("Codex Radar 暂不可用。", "Codex Radar is unavailable."),
    };

    public string RadarChange(RadarAlertChange change)
    {
        var previous = Value(change.PreviousValue);
        var current = Value(change.CurrentValue);
        return change.Kind switch
        {
            RadarAlertChangeKind.Model => T($"模型 {previous} → {current}", $"model {previous} → {current}"),
            RadarAlertChangeKind.Effort => T($"强度 {previous} → {current}", $"effort {previous} → {current}"),
            RadarAlertChangeKind.Status => T(
                $"状态 {RadarStatusLabel(previous)} → {RadarStatusLabel(current)}",
                $"status {previous} → {current}"),
            _ => $"IQ {previous} → {current}",
        };
    }

    public string RadarPrimaryChanged => T("主结果已变化", "Primary result changed");
    public string RadarSourceUpdated(DateTimeOffset sourceAt) => T(
        $"来源 {sourceAt.ToLocalTime():M月d日 HH:mm}",
        $"source {sourceAt.ToLocalTime():MM-dd HH:mm}");
    public string RadarSourceTime(DateTimeOffset? sourceAt) => sourceAt is null
        ? RadarUnknown
        : T(
            sourceAt.Value.ToLocalTime().ToString("M月d日 HH:mm"),
            sourceAt.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
    public string RadarTestTitle => T("Codex Radar 测试", "Codex Radar test");
    public string RadarTestBody => T(
        "Windows 通知可用；未读取 Radar 数据，也未修改告警状态。",
        "Windows notifications are available. No Radar data or alert state was changed.");

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

    public string RadarStatusLabel(string? value)
    {
        var normalized = Value(value);
        return normalized.ToLowerInvariant() switch
        {
            "green" or "stable" => RadarStable,
            "yellow" or "watch" => RadarWatch,
            "red" or "degraded" => RadarDegraded,
            "n/a" => RadarUnknown,
            _ => normalized,
        };
    }

    private string Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? T("未知", "n/a") : value;

    private string T(string zh, string en) => _zh ? zh : en;
}
