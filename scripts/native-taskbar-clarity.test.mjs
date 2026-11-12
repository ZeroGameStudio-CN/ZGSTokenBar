import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { readPartialType } from './source-files.mjs';

const models = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const barForm = readPartialType('src/ZGSTokenBar.App/BarForm.cs');
const applicationContext = readPartialType('src/ZGSTokenBar.App/QuotaApplicationContext.cs', ['SettingsWindowPlacement.cs', 'SettingsChangeDetector.cs']);
const popoverForm = readPartialType('src/ZGSTokenBar.App/QuotaPopoverForm.cs');
const codexAccountsPopoverForm = fs.readFileSync('src/ZGSTokenBar.App/CodexAccountsPopoverForm.cs', 'utf8');
const planBadgePresentation = fs.readFileSync('src/ZGSTokenBar.App/PlanBadgePresentation.cs', 'utf8');
const hintPopoverForm = fs.readFileSync('src/ZGSTokenBar.App/TaskbarHintPopoverForm.cs', 'utf8');
const nativeProject = fs.readFileSync('src/ZGSTokenBar.App/ZGSTokenBar.App.csproj', 'utf8');
const uiKit = fs.readFileSync('src/ZGSTokenBar.App/UiKit.cs', 'utf8');
const settingsForm = readPartialType('src/ZGSTokenBar.App/SettingsForm.cs');
const nativeText = fs.readFileSync('src/ZGSTokenBar.App/NativeText.cs', 'utf8');
const appSettings = fs.readFileSync('src/ZGSTokenBar.Core/AppSettings.cs', 'utf8');
const backgroundPalette = fs.readFileSync('src/ZGSTokenBar.App/QuotaBackgroundPalette.cs', 'utf8');
const processActivity = fs.readFileSync('src/ZGSTokenBar.Core/ProviderProcessActivity.cs', 'utf8');
const cockpitAccounts = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexAccountDirectory.cs', 'utf8');
const cockpitInstanceActivity = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexInstanceActivity.cs', 'utf8');
const cockpitQuotaReader = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexQuotaReader.cs', 'utf8');
const codexQuotaService = fs.readFileSync('src/ZGSTokenBar.Core/CodexQuotaService.cs', 'utf8');
const quotaCoordinator = fs.readFileSync('src/ZGSTokenBar.Core/QuotaCoordinator.cs', 'utf8');
const modelsSource = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const aiGatewayBalance = fs.readFileSync('src/ZGSTokenBar.Core/AiGatewayBalanceService.cs', 'utf8');
const sub2ApiPool = fs.readFileSync('src/ZGSTokenBar.Core/Sub2ApiPoolService.cs', 'utf8');
const sub2ApiUsage = fs.readFileSync('src/ZGSTokenBar.Core/Sub2ApiUsageService.cs', 'utf8');
const sub2ApiQuota = fs.readFileSync('src/ZGSTokenBar.Core/Sub2ApiQuotaService.cs', 'utf8');
const sub2ApiAccountAvailability = fs.readFileSync('src/ZGSTokenBar.Core/Sub2ApiAccountAvailabilityService.cs', 'utf8');
const boundedHttpBodyReader = fs.readFileSync('src/ZGSTokenBar.Core/BoundedHttpBodyReader.cs', 'utf8');
const testProgram = fs.readFileSync('tests/ZGSTokenBar.Tests/Program.cs', 'utf8');

test('taskbar Mini reserves room for readable quota capsules', () => {
  assert.match(models, /public static class TaskbarMiniLayoutMath/);
  assert.match(models, /public const int CardWidth = 144;/);
  assert.match(models, /public const int CardGap = 3;/);
  assert.match(models, /public const int ControlGap = 4;/);
  assert.match(models, /"1w" or "week" => "7d"/);
  assert.match(models, /return \$"\{\(int\)remaining\.TotalDays\}d\{remaining\.Hours\}h";/);
});

test('available quota details stay bright while blocked and missing states are muted', () => {
  assert.match(popoverForm, /blocked \|\| content\.Window\.ResetsAt is null \? dimBrush : valueBrush/);
  assert.match(popoverForm, /var blocked = content\.WeeklyBlockResetAt is not null/);
  assert.match(popoverForm, /CardValue = UiPalette\.Slate200/);
});

test('taskbar Mini prefers real quota windows and keeps labels for a fully unavailable provider', () => {
  assert.match(models, /public static IReadOnlyList<QuotaWindow> VisibleWindows\(/);
  assert.match(models, /window\.UsedPercent is not null \|\| window\.ResetsAt is not null/);
  assert.match(models, /return available\.Length > 0[\s\S]*windows\.Take\(MaximumWindows\)/);
  assert.doesNotMatch(barForm, /fallbackLabel/);
  assert.doesNotMatch(barForm, /DrawQuotaRing/);
});

test('taskbar Mini rails show remaining quota without the removed budget goal', () => {
  assert.doesNotMatch(barForm, /BudgetMarkerRemaining|DrawBudgetMarkerPointer|budgetMarkerRemaining/);
});

test('native quota surfaces use one opaque four-level background palette', () => {
  assert.match(appSettings, /DefaultBackgroundPalette = "midnight"/);
  assert.match(appSettings, /"midnight" or "graphite" or "navy" or "plum"/);
  assert.match(backgroundPalette, /Color Outer,[\s\S]*?Color ProviderGroup,[\s\S]*?Color QuotaGroup,[\s\S]*?Color Popover/);
  assert.match(backgroundPalette, /"midnight"[\s\S]*?7, 7, 7[\s\S]*?15, 15, 15[\s\S]*?25, 25, 25[\s\S]*?12, 12, 12/);
  assert.match(backgroundPalette, /"graphite"[\s\S]*?8, 9, 11[\s\S]*?18, 19, 22[\s\S]*?27, 28, 31[\s\S]*?16, 17, 20/);
  assert.match(backgroundPalette, /"navy"/);
  assert.match(backgroundPalette, /"plum"/);
  assert.match(popoverForm, /UiPaint\.TailedSurface\(graphics, body, TailPoints\(body\), UiTokens\.CardSurface, UiTokens\.Hairline/);
  assert.match(popoverForm, /statusFill = new SolidBrush\(_backgroundTheme\.QuotaGroup\)/);
  assert.match(settingsForm, /QuotaBackgroundPalette\.All/);
  assert.match(settingsForm, /new PaletteChoiceButton\([\s\S]*?palette\.QuotaGroup/);
  assert.match(settingsForm, /BackgroundPalette = _backgroundPalette/);
  assert.match(settingsForm, /BorderSize = selected \? 2 : 1/);
  assert.match(nativeText, /背景配色/);
  // The glass dock is a per-pixel-alpha layered window painting its own opaque card; no DWM blur/acrylic.
  assert.doesNotMatch(`${barForm}\n${popoverForm}`, /Acrylic|_glassBackdropEnabled|WindowBackdrop\.(?!TryPrepareLayered\b)/);
  assert.doesNotMatch(nativeProject, /PackageReference/);
});

test('Sub2API observer renders anonymous account availability with aggregate fallback on the existing service card', () => {
  assert.match(models, /Sub2ApiPoolAvailability/);
  assert.match(models, /Sub2ApiUsageSummary/);
  assert.match(models, /Sub2ApiQuotaSummary/);
  assert.match(models, /Sub2ApiAccountAvailabilitySummary/);
  assert.match(models, /Sub2ApiAccountAvailability \{ get; init; \}/);
  assert.match(appSettings, /EnableSub2ApiPool/);
  assert.match(applicationContext, /EnableSub2ApiPool/);
  assert.doesNotMatch(quotaCoordinator, /EnsureSub2ApiServiceCard/);
  assert.match(sub2ApiPool, /\/internal\/v1\/sub2api-pool/);
  assert.match(sub2ApiUsage, /\/internal\/v1\/sub2api-usage/);
  assert.match(sub2ApiQuota, /\/internal\/v1\/sub2api-quota/);
  assert.match(sub2ApiAccountAvailability, /\/internal\/v1\/sub2api-account-availability/);
  assert.match(boundedHttpBodyReader, /MaximumBytes = 32 \* 1024/);
  for (const service of [sub2ApiPool, sub2ApiUsage, sub2ApiQuota, sub2ApiAccountAvailability]) {
    assert.match(service, /BoundedHttpBodyReader\.ReadAsync/);
    assert.doesNotMatch(service, /ReadAsStreamAsync/);
  }
  assert.match(sub2ApiPool, /"available_accounts"/);
  assert.match(sub2ApiPool, /"free_concurrency"/);
  assert.match(sub2ApiUsage, /"today_tokens"/);
  assert.match(sub2ApiUsage, /"total_tokens"/);
  assert.match(sub2ApiQuota, /"seven_day_remaining_percent"/);
  assert.match(sub2ApiQuota, /"seven_day_remaining_account_equivalents"/);
  assert.match(sub2ApiAccountAvailability, /"accounts"/);
  assert.match(sub2ApiAccountAvailability, /"remaining_percent"/);
  assert.match(sub2ApiPool, /HttpMethod\.Get/);
  assert.match(sub2ApiUsage, /HttpMethod\.Get/);
  assert.match(sub2ApiQuota, /HttpMethod\.Get/);
  assert.match(sub2ApiAccountAvailability, /HttpMethod\.Get/);
  assert.doesNotMatch(sub2ApiPool, /HttpMethod\.Post|auth\/login|account_name/i);
  assert.doesNotMatch(sub2ApiUsage, /HttpMethod\.Post|auth\/login|account_name|api_key|total_cost/i);
  assert.doesNotMatch(sub2ApiQuota, /HttpMethod\.Post|auth\/login|account_id|account_name|api_key|used_percent|total_cost/i);
  assert.doesNotMatch(sub2ApiAccountAvailability, /HttpMethod\.Post|auth\/login|account_id|account_name|api_key|total_cost/i);
  const sub2ApiMiniStart = barForm.indexOf('    private void DrawTaskbarSub2ApiMetric(');
  const sub2ApiMiniEnd = barForm.indexOf('    private static Color Sub2ApiPresentationCompactStatusColor(', sub2ApiMiniStart);
  const sub2ApiMini = barForm.slice(sub2ApiMiniStart, sub2ApiMiniEnd);
  assert.doesNotMatch(sub2ApiMini, /Sub2ApiQuota(?:Headline|WindowDetails|SummaryShort)|FormatReset|\b(?:5h|7d)\b/);
  assert.match(popoverForm, /DrawSub2ApiQuotaServiceContent/);
  assert.match(popoverForm, /DrawSub2ApiAccountAvailabilityServiceContent/);
  assert.match(popoverForm, /DrawSub2ApiUsageServiceContent/);
  assert.match(popoverForm, /DrawSub2ApiPoolServiceContent/);
  assert.match(popoverForm, /LogicalSub2ApiQuotaBodyHeight/);
  assert.match(popoverForm, /LogicalSub2ApiAccountAvailabilityBodyHeight/);
  assert.match(popoverForm, /var poolRowY = other is null \? y \+ 94 : y \+ 130/);
  assert.match(nativeText, /Sub2ApiQuotaWindowDetails/);
  assert.match(nativeText, /Sub2ApiAccountAvailabilityCompact/);
  assert.match(nativeText, /Sub2ApiAccountAvailabilitySlot/);
  assert.match(nativeText, /Sub2ApiUnavailable/);
  assert.match(nativeText, /Sub2ApiAccountAvailabilityPercent[\s\S]*?Unknown/);
  assert.match(nativeText, /Sub2ApiLegacyQuotaCompact/);
  assert.match(nativeText, /Sub2ApiQuotaCompact[\s\S]*?\? ObserverPercent\(window\.RemainingPercent\)/);
  assert.match(nativeText, /额度汇总/);
  assert.match(nativeText, /Sub2ApiUsageTodayTokens/);
  assert.match(nativeText, /Sub2ApiPoolAvailableAccounts/);
  assert.match(nativeText, /可用账号/);
  assert.match(testProgram, /Name: "complete"[\s\S]*?Name: "partial"[\s\S]*?Name: "known-none"[\s\S]*?Name: "unavailable"[\s\S]*?Name: "legacy-complete"[\s\S]*?Name: "generic-api"/);
  assert.match(testProgram, /foreach \(var dpi in new\[\] \{ 96, 144, 192 \}\)[\s\S]*?taskbar-mini-sub2api-\{scenario\.Name\}-\{locale\}-\{dpi\}dpi\.png/);
});

test('the retired Codex display mode leaves no setting, UI or strategy page behind', () => {
  assert.doesNotMatch(appSettings, /CodexMiniDisplayMode/);
  assert.doesNotMatch(settingsForm, /_codexDisplayMode|CodexMiniDisplayMode/);
  assert.doesNotMatch(nativeText, /CodexDisplayMode/);
  assert.doesNotMatch(barForm, /CodexStrategy|_codexMiniDisplayMode|TaskbarMiniGrouping/);
});

test('Codex accounts share one Mini area and the dock keeps the Mini height contract', () => {
  const layoutStart = barForm.indexOf('    private void ApplySnapshotLayout(');
  const layoutEnd = barForm.indexOf('    private void UpdateWindowRegion()', layoutStart);
  assert.notEqual(layoutStart, -1);
  assert.notEqual(layoutEnd, -1);
  const applyLayout = barForm.slice(layoutStart, layoutEnd);
  assert.match(barForm, /TaskbarMiniAreaContent\.ForCards\(MiniAreaIds\.Codex, codex\)/);
  assert.match(barForm, /SetCodexAccounts\([\s\S]*?_codexAccounts = accounts\.ToArray\(\);[\s\S]*?ApplySnapshotLayout\(\)/);
  assert.match(modelsSource, /record CodexAccountQuota\([\s\S]*?public string\? CardKey \{ get; init; \}/);
  assert.match(applyLayout, /ClientSize = new Size\([\s\S]*?TaskbarMiniLayoutMath\.Height\)/);
});

test('quota popover keeps the pace forecast without the redundant budget row', () => {
  assert.match(popoverForm, /LogicalBodyWidth = 240/);
  assert.match(popoverForm, /new\("Segoe UI Semibold", 10f, FontStyle\.Regular, GraphicsUnit\.Pixel\)/);
  assert.match(popoverForm, /UiIcons\.Trend\(/);
  assert.doesNotMatch(popoverForm, /DrawCyclePaceIcon|DrawBudgetMarker|QuotaCurrentGoal|QuotaCycle/);
  assert.match(popoverForm, /content\.Pace\?\.ForDisplay\(now\)/);
  assert.match(popoverForm, /UiPaint\.ProgressBar\([\s\S]*?UiPalette\.Slate800\)/);
  assert.match(popoverForm, /UiPaint\.ProgressBar\(graphics, rail, [\s\S]*?Color\.White\.WithAlpha\(18\)/);
  assert.match(popoverForm, /QuotaDisplayFormatting\.ExpectedRemaining\(content\.Window, now\)[\s\S]*?UiPaint\.PaceMarker\(graphics, rail/);
  assert.match(nativeText, /"1h"/);
  assert.match(nativeText, /15m初步/);
  assert.match(nativeText, /重置约剩/);
  assert.match(nativeText, /compact\.Replace\(" ", string\.Empty, StringComparison\.Ordinal\)/);
});

test('taskbar Mini uses unmodified embedded provider logos', () => {
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\claude-icon-rounded\.png"/);
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\openai-official-ios-icon\.png"/);
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\deepseek-whale-icon\.png"/);
  assert.match(barForm, /private readonly Image _claudeLogo/);
  assert.match(barForm, /private readonly Image _openAiLogo/);
  assert.match(barForm, /private readonly Image _deepSeekLogo/);
  assert.doesNotMatch(barForm, /private static void DrawClaudeMark/);
  assert.doesNotMatch(barForm, /private static void DrawCodexMark/);
});

test('taskbar Mini reset affordance comes from the Fluent icon asset', () => {
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\fluent-clock-20-regular\.png"/);
  assert.match(barForm, /private readonly Image _resetClockIcon/);
});

test('collapsed quota and balance providers share a compact semantic summary without another fetch path', () => {
  assert.match(aiGatewayBalance, /public static string CompactAmount\(decimal\? amount\)/);
  assert.doesNotMatch(barForm, /FetchAiGateway|HttpClient/);
});

test('collapsed generic balance plugins retain their primary amount', () => {
  assert.match(barForm, /view\.Card\.Kind == ContributionKind\.Balance/);
  assert.match(barForm, /AiGatewayBalanceFormatting\.CompactAmount\(currency\)/);
});

test('provider visibility follows active App or CLI processes', () => {
  assert.match(processActivity, /\[ProviderKind\.Claude\] = \["claude", "claude-code", "claude_desktop"\]/);
  assert.match(processActivity, /\[ProviderKind\.Codex\] = \["chatgpt", "codex", "codex-cli"\]/);
  assert.match(applicationContext, /_providerActivityTimer = new System\.Windows\.Forms\.Timer \{ Interval = 5_000 \}/);
  assert.match(applicationContext, /activeProviders: _activeProviders/);
  assert.match(barForm, /_snapshot\.Cards[\s\S]*?Where\(card => _activeProviders\.Contains\(card\.Provider\)\)/);
  assert.match(barForm, /public void SetActiveProviders\(IReadOnlySet<ProviderKind> providers\)/);
});

test('Codex account activity follows running instance bindings', () => {
  assert.match(cockpitInstanceActivity, /codex_instances\.json/);
  assert.match(cockpitInstanceActivity, /bindAccountId/);
  assert.match(cockpitInstanceActivity, /lastPid/);
  assert.match(cockpitInstanceActivity, /Process\.GetProcessById/);
  assert.match(cockpitInstanceActivity, /CreateToolhelp32Snapshot/);
  assert.match(cockpitInstanceActivity, /activeRootCount > activeManagedProcessCount/);
  assert.match(cockpitAccounts, /CockpitCodexInstanceActivity\.ReadActiveAccountIds\(home\)/);
  assert.match(cockpitQuotaReader, /CockpitCodexInstanceActivity\.ReadActiveAccountIds\(home\)/);
  assert.match(codexQuotaService, /account\.Credential\.Plan \?\? usage\.Plan/);
});

test('Codex row hover exposes a privacy-safe Cockpit account summary', () => {
  assert.match(cockpitAccounts, /codex_accounts\.json/);
  assert.match(cockpitAccounts, /public sealed record CodexAccountInfo/);
  assert.match(cockpitAccounts, /int AccountCount = 1/);
  assert.match(cockpitAccounts, /public static string MaskEmail/);
  assert.doesNotMatch(cockpitAccounts, /access_token|refresh_token|id_token/);
  assert.match(codexQuotaService, /var cockpitCredentials = cockpitAccounts[\s\S]*?Select\(account => new CodexCredential/);
  assert.match(codexQuotaService, /BuildAccountQuotas\(cockpitAccounts, accounts, now\)/);
  assert.match(modelsSource, /public sealed record CodexAccountQuota/);
  assert.match(modelsSource, /public string\? AccountHint \{ get; init; \}/);
  assert.match(codexQuotaService, /AccountHint = CodexAccountFormatting\.MaskEmail/);
  assert.doesNotMatch(barForm, /MiniAccountTarget|DrawCodexAccountsIcon|_taskbarAccountBounds/);
  assert.match(barForm, /TaskbarCodexAccountTargetAt/);
  assert.match(barForm, /ShowCodexAccountsPopover\(codexAccountTarget\)/);
  assert.doesNotMatch(barForm, /CodexAccountsTooltip/);
  assert.doesNotMatch(barForm, /ToolTip|TooltipFor|_lastToolTip/);
  assert.match(codexAccountsPopoverForm, /class CodexAccountsPopoverForm/);
  assert.match(codexAccountsPopoverForm, /TaskbarMiniPopoverMath\.Place/);
  assert.match(codexAccountsPopoverForm, /UiTokens\.CardSurface/);
  assert.match(codexAccountsPopoverForm, /_text\.CodexAccountsHeading/);
  assert.doesNotMatch(codexAccountsPopoverForm, /CodexAccountMapping|LogicalMappingHeight/);
  assert.match(nativeText, /public string CodexAccountsHeading/);
  assert.doesNotMatch(nativeText, /CodexAccountsTooltip|CodexAccountMapping/);
  assert.match(codexAccountsPopoverForm, /QuotaDisplayFormatting\.FormatWindowShort/);
  assert.match(popoverForm, /PlanBadgeLabel/);
  assert.match(popoverForm, /PlanBadgePresentation\.TryGetStyle/);
  assert.match(codexAccountsPopoverForm, /DrawPlanBadge/);
  assert.match(codexAccountsPopoverForm, /PlanBadgePresentation\.Width/);
  assert.match(codexAccountsPopoverForm, /API · \{Math\.Max\(1, account\.AccountCount\)\}/);
  assert.match(planBadgePresentation, /"PLUS"/);
  assert.match(planBadgePresentation, /"PRO"/);
  assert.match(planBadgePresentation, /"FREE" or "API KEY"/);
  assert.match(popoverForm, /accountHint/);
  assert.match(hintPopoverForm, /class TaskbarHintPopoverForm/);
  assert.match(hintPopoverForm, /UiTokens\.CardSurface/);
});

test('Cockpit API services are displayed as informational cards without network access', () => {
  assert.match(cockpitQuotaReader, /CodexPlanNormalization\.Normalize/);
  assert.match(cockpitAccounts, /"apikey" => "api_key"/);
  assert.match(cockpitAccounts, /"chatgptpro" => "pro"/);
  assert.match(cockpitQuotaReader, /string.Equals\(plan, "api_key"/);
  assert.match(cockpitQuotaReader, /public string\? ApiProviderName \{ get; init; \}/);
  assert.match(cockpitQuotaReader, /ApiProviderName = account\.StringProperty\("api_provider_name"\)/);
  assert.doesNotMatch(cockpitQuotaReader, /openai_api_key|api_base_url/i);
  assert.match(modelsSource, /public bool IsService \{ get; init; \}/);
  assert.match(modelsSource, /public int ServiceCount \{ get; init; \}/);
  assert.match(modelsSource, /public string\? ServiceDisplayName \{ get; init; \}/);
  assert.match(modelsSource, /public string DisplayLabel => IsService[\s\S]*?ServiceDisplayName[\s\S]*?: Label;/);
  assert.match(codexQuotaService, /var apiServices = cockpitAccounts[\s\S]*?account\.IsApiService && account\.Active/);
  assert.match(codexQuotaService, /IsService = true/);
  assert.match(codexQuotaService, /ServiceDisplayName = ApiServiceDisplayName\(services\)/);
  assert.match(codexQuotaService, /services\.Count != 1/);
  assert.match(codexQuotaService, /ToServiceCard\(apiServices\)/);
  assert.match(codexQuotaService, /new QuotaWindow\("API", null, null, TimeSpan\.Zero\)/);
  assert.match(codexQuotaService, /apiServices\.Length == 0/);
  assert.match(popoverForm, /if \(content\.Card\.IsService\)/);
  assert.match(popoverForm, /content\.Card\.DisplayLabel/);
  assert.match(nativeText, /ApiServiceNoQuota/);
  assert.doesNotMatch(codexQuotaService, /api_base_url|openai_api_key/i);
});

test('open Codex account popover follows refreshed snapshot data', () => {
  assert.match(barForm, /RefreshQuotaPopover\(\);\s+RefreshCodexAccountsPopover\(\);\s+RefreshHintPopover\(\);/);
  assert.match(barForm, /private void RefreshCodexAccountsPopover\(\)[\s\S]*?_codexAccountsPopover\?\.Visible != true[\s\S]*?ShowCodexAccountsPopover\(target\)/);
  assert.match(barForm, /SetCodexAccounts\(IReadOnlyList<CodexAccountInfo> accounts\)[\s\S]*?_codexAccounts = accounts\.ToArray\(\);[\s\S]*?RefreshCodexAccountsPopover\(\);/);
});

test('refresh settings use a practical background cadence', () => {
  assert.match(settingsForm, /foreach \(var minutes in new\[\] \{ 5, 10, 30, 60 \}\)/);
  assert.doesNotMatch(settingsForm, /new\[\] \{ 1, 5, 10, 15, 30, 60 \}/);
  assert.match(settingsForm, /FirstOrDefault\(choice => choice\.Minutes == settings\.RefreshMinutes\)[\s\S]*?\?\? _refreshMinutes\.Items\[0\]/);
});

test('taskbar Mini balances narrow cards with readable vertical controls', () => {
  assert.match(models, /public const int Height = 44;/);
  assert.match(models, /public const int CardWidth = 144;/);
  assert.match(models, /public const int ServiceCardWidth = 104;/);
  assert.match(models, /public const int CollapsedCardWidth = 34;/);
  assert.match(models, /public const int ProviderCollapseHandleWidth = 9;/);
  assert.match(models, /public const int ControlsWidth = 24;/);
  assert.match(models, /public const int SystemUsageContentWidth = MinimumAreaContentWidth;/);
  assert.match(models, /public const int ModuleGap = 4;/);
  assert.match(models, /public const int OuterPadding = 6;/);
  assert.match(models, /public const int ControlGap = 4;/);
  assert.match(barForm, /TaskbarControlIconSize = 12f/);
  assert.doesNotMatch(barForm, /_modeBounds|HoverTarget\.Mode/);
  assert.doesNotMatch(barForm, /DrawTaskbarControlSegment\(graphics, .*Collapse/);
  assert.doesNotMatch(barForm, /MiniCollapseTarget/);
  assert.match(barForm, /ApplyMiniAreaLayout/);
  assert.match(barForm, /TaskbarMiniAreaContent\.ForSystem\(_text\.SystemUsageTitle\)/);
  assert.match(barForm, /SelectVisibleTaskbarAreas\(_taskbarContentAreas\)/);
  assert.match(barForm, /PreserveMiniProviderAreaAnchor/);
  assert.doesNotMatch(barForm, /var x = bounds\.X \+ 3\.2f/);
  assert.match(nativeText, /拖动箭头左侧细线调宽/);
  assert.match(nativeText, /拖动排序；其他位置可拖动整条栏/);
  assert.match(barForm, /StringAlignment\.Center/);
});

test('explicit refresh can recover an expired Claude OAuth token', () => {
  assert.match(applicationContext, /RefreshRequested \+= \(_, _\) => _ = RefreshAsync\(userInitiated: true\)/);
  assert.match(applicationContext, /allowClaudeOAuthRefresh: userInitiated/);
  assert.match(applicationContext, /forceProviderRefresh: userInitiated \|\| forceProviderRefresh/);
  assert.match(applicationContext, /RefreshAsync\(forceProviderRefresh: true\)/);
});

test('native bar micro-motion stays subtle and follows the animation preference', () => {
  assert.match(barForm, /private const float HoverAnimationStep = \.32f/);
  assert.match(barForm, /SnapshotPulseAnimationMs = 560/);
  assert.match(barForm, /_quotaPopover\.ShowFor\([\s\S]*?_animationsEnabled\);/);
  assert.match(popoverForm, /animateEntrance/);
  assert.match(popoverForm, /TotalMilliseconds \/ 130/);
  assert.match(popoverForm, /ExitDurationMs = 90/);
  assert.match(settingsForm, /_text\.Animations/);
  assert.match(nativeText, /Enable subtle transitions and refresh animation/);
});

test('native animated refresh control uses a centered geometric path', () => {
  assert.doesNotMatch(barForm, /DrawModeIcon|DrawExpandIcon|HoverTarget\.Mode/);
  assert.doesNotMatch(barForm, /RefreshIconGlyph/);
  assert.doesNotMatch(barForm, /ExpandIconGlyph/);
});

test('native static controls keep official Windows Fluent glyphs', () => {
  assert.doesNotMatch(barForm, /DockBottomIconGlyph|DockBottom/);
  assert.match(barForm, /CreateSystemIconFont\(float size = 14f\)/);
  assert.match(barForm, /new Font\(familyName, size, FontStyle\.Regular, GraphicsUnit\.Pixel\)/);
});

test('the Mini is the only bar layout and docking has no mode toggle', () => {
  assert.match(appSettings, /public bool TaskbarDocked \{ get; set; \} = true;/);
  assert.match(barForm, /public bool IsTaskbarMode => true/);
  assert.match(barForm, /public bool IsTaskbarDocked => _taskbarDocked/);
  assert.doesNotMatch(settingsForm, /_text\.CompactTaskbar|_taskbarRings|CompactTaskbarTitle/);
  assert.doesNotMatch(barForm, /ModeToggle|SwitchToMini|ExpandFloatingBar|HoverTarget\.Mode/);
  assert.doesNotMatch(nativeText, /Compact quota capsules|Switch to Mini|Expand floating bar/);
  assert.doesNotMatch(settingsForm, /dual rings/i);
  assert.doesNotMatch(barForm, /taskbar rings/i);
});

test('settings use a compact navigation shell with explicit draft semantics', () => {
  assert.match(settingsForm, /LogicalClientWidth = 720/);
  assert.match(settingsForm, /LogicalClientHeight = 540/);
  assert.match(settingsForm, /LogicalNavigationWidth = 176/);
  assert.match(settingsForm, /LogicalCompactNavigationWidth = 72/);
  assert.match(settingsForm, /LogicalResponsiveBreakpoint = 640/);
  assert.match(settingsForm, /RowStyle\(SizeType\.Absolute, Scale\(64\)\)/);
  assert.match(settingsForm, /AutoScroll = false/);
  assert.match(settingsForm, /private sealed class SettingsScrollBar : Control/);
  assert.match(settingsForm, /Tag = "settings\.scrollbar"/);
  assert.match(settingsForm, /AddNavigation\("general"/);
  assert.match(settingsForm, /AddNavigation\("providers"/);
  assert.match(settingsForm, /AddNavigation\("notifications"/);
  assert.match(settingsForm, /AddNavigation\("display"/);
  assert.match(settingsForm, /AddNavigation\("advanced"/);
  assert.match(settingsForm, /AddNavigation\("about"/);
  assert.doesNotMatch(settingsForm, /SettingsCard|BadgeLabel|NATIVE BAR/);
  assert.match(settingsForm, /ConstrainOuterSize\(Size desiredOuterSize, Rectangle workingArea, int safeMarginPixels\)/);
  assert.doesNotMatch(settingsForm, /MinimumSize\s*=/);
  assert.match(settingsForm, /_save\.Enabled = dirty/);
  assert.match(settingsForm, /_pageHost\.SuspendLayout\(\)[\s\S]*?_pageHost\.ResumeLayout\(true\)[\s\S]*?_pageHost\.Invalidate\(true\)/);
  assert.match(settingsForm, /WmSetRedraw = 0x000B/);
  assert.match(settingsForm, /SendMessage\(_pageHost\.Handle, WmSetRedraw/);
  assert.match(settingsForm, /RedrawWindow\(_pageHost\.Handle/);
  const equalityStart = settingsForm.indexOf('    private static bool EditableEquals(');
  const equalityEnd = settingsForm.indexOf('    private static AppSettings Copy(', equalityStart);
  const equalityBody = settingsForm.slice(equalityStart, equalityEnd);
  assert.doesNotMatch(equalityBody, /WindowX|WindowY|TaskbarPosition/);
});

test('settings visual treatment uses the approved dark palette and local controls only', () => {
  assert.match(settingsForm, /UiTokens\.WindowContent,\s*UiTokens\.WindowSidebar,\s*UiTokens\.WindowFooter/);
  assert.match(settingsForm, /UiTokens\.TextStrong,\s*UiTokens\.Muted,\s*UiTokens\.AccentFill/);
  assert.match(uiKit, /WindowContent = Color\.FromArgb\(24, 24, 28\)/);
  assert.match(uiKit, /WindowSidebar = Color\.FromArgb\(18, 18, 20\)/);
  assert.match(uiKit, /WindowBorder = Color\.FromArgb\(44, 44, 49\)/);
  assert.match(settingsForm, /SystemInformation\.HighContrast/);
  assert.match(settingsForm, /points \* \(96f \/ 72f\) \* scale[\s\S]*?GraphicsUnit\.Pixel/);
  assert.doesNotMatch(settingsForm, /GraphicsUnit\.Point/);
  assert.match(settingsForm, /private sealed class ToggleSwitch : CheckBox/);
  assert.match(settingsForm, /private sealed class PaletteChoiceButton : Button/);
  assert.match(settingsForm, /private sealed class SettingsComboBox : Button/);
  assert.match(settingsForm, /AccessibleRole = AccessibleRole\.ComboBox/);
  assert.match(settingsForm, /"Segoe Fluent Icons"/);
  assert.match(settingsForm, /DoubleBuffered = true/);
  assert.match(settingsForm, /ShowInTaskbar = true/);
  assert.match(settingsForm, /TopMost = false/);
  const openSettingsStart = applicationContext.indexOf('    private void OpenSettings()');
  const openSettingsEnd = applicationContext.indexOf(
    '    private void SettingsDialogClosed(',
    openSettingsStart);
  assert.notEqual(openSettingsStart, -1);
  assert.notEqual(openSettingsEnd, -1);
  const openSettings = applicationContext.slice(openSettingsStart, openSettingsEnd);
  assert.match(openSettings, /RestoreSettingsWindow\(dialog, area\)/);
  assert.match(applicationContext, /internal static class SettingsWindowPlacement[\s\S]*?dialog\.Show\(\)/);
  assert.doesNotMatch(openSettings, /dialog\.ShowDialog/);
  assert.match(settingsForm, /e\.Graphics\.Clear\(_theme\.Sidebar\)/);
  assert.match(settingsForm, /e\.Graphics\.Clear\(_theme\.Content\)/);
  assert.match(settingsForm, /BackColor = theme\.Content/);
  assert.doesNotMatch(settingsForm, /_hover \? Theme\.Hover/);
  assert.doesNotMatch(nativeProject, /PackageReference/);
});
