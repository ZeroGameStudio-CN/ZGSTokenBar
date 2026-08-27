import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const barForm = fs.readFileSync('src/ZGSTokenBar.App/BarForm.cs', 'utf8');
const applicationContext = fs.readFileSync('src/ZGSTokenBar.App/QuotaApplicationContext.cs', 'utf8');
const codexQuotaService = fs.readFileSync('src/ZGSTokenBar.Core/CodexQuotaService.cs', 'utf8');
const models = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const productReadme = fs.readFileSync('README.md', 'utf8');
const popoverPath = 'src/ZGSTokenBar.App/QuotaPopoverForm.cs';
const popover = fs.existsSync(popoverPath) ? fs.readFileSync(popoverPath, 'utf8') : '';
const accountsPopover = fs.readFileSync('src/ZGSTokenBar.App/CodexAccountsPopoverForm.cs', 'utf8');
const hintPopover = fs.readFileSync('src/ZGSTokenBar.App/TaskbarHintPopoverForm.cs', 'utf8');
const nativeText = fs.readFileSync('src/ZGSTokenBar.App/NativeText.cs', 'utf8');
const radarPresentation = fs.readFileSync('src/ZGSTokenBar.Core/RadarPresentation.cs', 'utf8');
const radarLayout = fs.readFileSync('src/ZGSTokenBar.Core/RadarPopoverLayout.cs', 'utf8');
const radarRenderer = fs.readFileSync('src/ZGSTokenBar.App/RadarPopoverRenderer.cs', 'utf8');
const radarPopover = fs.readFileSync('src/ZGSTokenBar.App/ProviderRadarPopoverForm.cs', 'utf8');
const radarService = fs.readFileSync('src/ZGSTokenBar.Core/RadarService.cs', 'utf8');
const tokenUsageReader = fs.readFileSync('src/ZGSTokenBar.Core/CodexTokenUsageReader.cs', 'utf8');
const usageService = fs.readFileSync('src/ZGSTokenBar.Core/AiGatewayUsageService.cs', 'utf8');
const boundedHttpBodyReader = fs.readFileSync('src/ZGSTokenBar.Core/BoundedHttpBodyReader.cs', 'utf8');
const popoverMath = fs.readFileSync('src/ZGSTokenBar.App/TaskbarPopoverMath.cs', 'utf8');
const radarScenarioEvaluator = fs.readFileSync('src/ZGSTokenBar.Core/RadarScenarioEvaluator.cs', 'utf8');
const radarCli = fs.readFileSync('tests/ZGSTokenBar.Tests/RadarDeveloperCli.cs', 'utf8');
const nativeProject = fs.readFileSync('src/ZGSTokenBar.App/ZGSTokenBar.App.csproj', 'utf8');

const boundedSlice = (source, startMarker, endMarker) => {
  const start = source.indexOf(startMarker);
  assert.notEqual(start, -1, `missing source marker: ${startMarker}`);
  const end = source.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(end, -1, `missing source marker: ${endMarker}`);
  return source.slice(start, end);
};

test('taskbar Mini discovers real quota capsules and previews after a deliberate hover', () => {
  assert.match(barForm, /_taskbarWindowBounds/);
  assert.match(barForm, /_popoverHoverTimer = new\(\) \{ Interval = 350 \}/);
  assert.match(barForm, /TaskbarMiniLayoutMath\.VisibleWindows\(card\.Windows\)/);
  assert.match(barForm, /ShowQuotaPopover\(target, pinned: false\)/);
  assert.match(barForm, /string\.Equals\(_popoverTargetId, nextId, StringComparison\.Ordinal\)/);
  assert.match(barForm, /_hoverQuotaTarget = null;\s+_popoverTargetId = null/);
});

test('AI Gateway whale icon opens a separate DeepSeek Radar view', () => {
  assert.match(barForm, /card\.Provider == ProviderKind\.AiGateway[\s\S]*?_taskbarRadarBounds\.Add\(new MiniRadarTarget/);
  assert.match(barForm, /WindowKey\(card\.Key, "__deepseek-radar"\)/);
  assert.match(barForm, /ProviderKind\.Codex,[\s\S]*?true,[\s\S]*?RadarSurfaceIds\.DeepSeek\)\);/);
  assert.match(barForm, /target\.SourceProvider/);
  assert.match(barForm, /var tokenUsage = target\.DeepSeekOnly/);
  assert.match(barForm, /target\.SourceProvider == ProviderKind\.Codex[\s\S]*?_codexTokenUsage/);
  assert.match(barForm, /var aiGatewayUsage = target\.DeepSeekOnly \? _aiGatewayUsage : null/);
  assert.match(barForm, /target\.Card\.Provider == ProviderKind\.AiGateway \? _aiGatewayUsage : null/);
  assert.match(barForm, /SetAiGatewayUsage\(AiGatewayUsageSummary\? summary\)/);
  assert.match(barForm, /SourceProvider: ProviderKind\.Codex, DeepSeekOnly: false/);
  assert.match(radarPopover, /deepSeekOnly/);
  assert.match(radarPresentation, /DeepSeekOnly/);
  assert.match(radarPresentation, /DeepSeekFamilyOrder/);
  assert.match(radarPresentation, /DeepSeekLaneOrder/);
  assert.match(radarPresentation, /CodexOnly/);
  assert.match(radarPopover, /RadarPresentation\.CodexOnly/);
  assert.match(radarPopover, /AiGatewayUsageSummary\? aiGatewayUsage/);
  assert.match(radarRenderer, /DrawGatewayUsageRadarFooter/);
  assert.match(radarRenderer, /AiGatewayTokenRadarMetricTitle/);
  assert.match(radarRenderer, /AiGatewayCacheRadarMetricTitle/);
  assert.match(radarRenderer, /radarTitle/);
  assert.match(nativeText, /DeepSeekRadarTitle/);
  assert.match(popover, /AiGatewayUsageSummary\? AiGatewayUsage/);
  assert.match(popover, /AiGatewayTodayUsage/);
  assert.match(popover, /AiGatewayUsageDetail/);
});

test('DeepSeek Radar reads only the private aggregate usage contract', () => {
  assert.match(usageService, /\/internal\/v1\/usage/);
  assert.match(usageService, /connection\.Token/);
  assert.match(usageService, /schema_version/);
  assert.match(usageService, /cache_hit_tokens/);
  assert.match(usageService, /cache_miss_tokens/);
  assert.match(usageService, /cache_unknown_tokens/);
  assert.match(usageService, /estimated_cost_cny/);
  assert.match(usageService, /BoundedHttpBodyReader\.ReadAsync/);
  assert.match(boundedHttpBodyReader, /MaximumBytes = 32 \* 1024/);
  assert.doesNotMatch(usageService, /HttpMethod\.Post|producer_token|producer/i);
  assert.match(radarRenderer, /tokenUsage is null && aiGatewayUsage is null/);
  assert.match(radarRenderer, /usage\.Today\.TotalTokens/);
  assert.match(radarRenderer, /usage\.Today\.CacheHitRatePercent/);
});

test('taskbar popovers share pure motion and tail geometry without a form base class', () => {
  assert.match(popoverMath, /OffsetFromAnchor/);
  assert.match(popoverMath, /EntranceEase/);
  assert.match(popoverMath, /ExitEase/);
  assert.match(popoverMath, /BodyBounds/);
  assert.match(popoverMath, /TailPoints/);
  for (const source of [popover, accountsPopover, hintPopover, radarPopover]) {
    assert.match(source, /TaskbarPopoverMath\./);
  }
});

test('taskbar Mini click pins the selected window without removing drag support', () => {
  assert.match(barForm, /TogglePinnedQuotaPopover/);
  assert.match(barForm, /ContinueTaskbarDrag\(e\)/);
  assert.match(barForm, /ClearHoverStateForTaskbarDrag\(\);\s+TaskbarPlacement\.InvalidateCache\(\);\s+_taskbarDragMoved = true/);
});

test('Codex pool segments remain first-class quota hover and pin targets', () => {
  const drawPool = boundedSlice(
    barForm,
    'private void DrawTaskbarCodexPool(',
    'private void DrawTaskbarCodexPoolRow(');
  const buildTargets = boundedSlice(
    barForm,
    'private static CodexPoolTargetRow[] TaskbarCodexPoolTargetRows(',
    'private void DrawTaskbarCodexGroup(');
  const resolveTarget = boundedSlice(
    barForm,
    'private MiniQuotaTarget? ResolveQuotaTarget(',
    'private Rectangle QuotaTargetScreenBounds(');

  assert.match(drawPool, /_taskbarWindowBounds\.AddRange\(targetRow\.Targets\)/);
  assert.match(buildTargets, /row\.Segments[\s\S]*?new MiniQuotaTarget\([\s\S]*?segment\.Card,[\s\S]*?window\)/);
  assert.match(
    resolveTarget,
    /group\.IsCodexPool[\s\S]*?TaskbarCodexPoolTargetRows\([\s\S]*?SelectMany\(row => row\.Targets\)[\s\S]*?string\.Equals\(target\.Id, id, StringComparison\.Ordinal\)[\s\S]*?return target/);
  assert.match(barForm, /private void MonitorQuotaPopover\(\)[\s\S]*?ResolveQuotaTarget\(_popoverTargetId\)/);
  assert.match(barForm, /private void RefreshQuotaPopover\(\)[\s\S]*?ResolveQuotaTarget\(_popoverTargetId\)/);
  assert.match(
    barForm,
    /SetCodexAccounts\([\s\S]*?CodexMiniDisplayModes\.Pool\)[\s\S]*?ApplySnapshotLayout\(\);\s+RefreshQuotaPopover\(\);/);
});

test('Radar logo hover does not dismiss a pinned quota bubble', () => {
  assert.match(barForm, /_taskbarRadarBounds/);
  assert.match(barForm, /TaskbarRadarTargetAt/);
  assert.match(barForm, /private void UpdateRadarHover[\s\S]*?if \(_popoverPinned\) return;/);
  assert.match(barForm, /ShowRadarPopover\(radarTarget, pinned: false\)/);
});

test('Codex account hover uses the shared dark popover chrome and ordinal anchor', () => {
  assert.match(barForm, /TaskbarAccountOrdinalRowBounds\(bounds, index, group\.Cards\.Count\)/);
  assert.match(barForm, /var quotaTarget = radarTarget is null && codexAccountTarget is null/);
  assert.match(barForm, /private void ShowCodexAccountsPopover/);
  assert.match(accountsPopover, /TaskbarMiniPopoverMath\.Place/);
  assert.match(accountsPopover, /_backgroundTheme\.Popover/);
  assert.match(accountsPopover, /CodexAccountFormatting\.MaskEmail/);
  assert.match(accountsPopover, /QuotaText\(account\)/);
  assert.match(accountsPopover, /API key/);
  assert.match(hintPopover, /TaskbarMiniPopoverMath\.Place/);
  assert.match(hintPopover, /_backgroundTheme\.Popover/);
  assert.match(barForm, /UpdateHintHover\(next\)/);
  assert.match(barForm, /MiniAreaLayoutChanged/);
  assert.match(barForm, /MiniAreaOrderChanged/);
  assert.match(barForm, /ToggleMiniAreaCollapsed/);
  assert.match(nativeText, /MiniCardCollapseHint/);
  assert.match(nativeText, /MiniCardReorderHint/);
  assert.match(applicationContext, /nextSettings\.CopyMiniAreaLayoutsFrom\(_settings\)/);
  assert.doesNotMatch(barForm, /new\(\) \{ InitialDelay = 350/);
});

test('Radar and token overview clicks share quota-style temporary pin behavior', () => {
  assert.match(barForm, /TogglePinnedRadarPopover/);
  assert.match(barForm, /ShowRadarPopover\(target, pinned: true, requestRefresh: !sameVisibleTarget\)/);
  assert.match(barForm, /private void MonitorRadarPopover[\s\S]*?if \(_popoverPinned\)[\s\S]*?outsideClick[\s\S]*?escapePressed/);
  assert.match(barForm, /ResolveRadarTarget\(target\.Id\)/);
  assert.match(radarPopover, /bool pinned/);
  assert.match(radarRenderer, /RadarPopoverSubtitle\(pinned\)/);
  assert.match(radarRenderer, /CodexTokenPopoverSubtitle\(pinned\)/);
});

test('Codex logo hover owns local token totals and cache hit rate without coupling them to quota or Radar fetches', () => {
  assert.match(barForm, /HasProviderOverview\(card\.Provider, radarEnabled, _codexTokenUsage\)/);
  assert.match(barForm, /radarEnabled \|\| provider == ProviderKind\.Codex && codexTokenUsage is not null/);
  assert.match(barForm, /if \(requestRefresh && radarEnabled\)[\s\S]*?RadarPreviewRequested\?\.Invoke/);
  assert.doesNotMatch(popover, /TokenUsage|LogicalTokenBodyHeight/);
  assert.match(radarLayout, /CreateTokenUsage/);
  assert.match(radarRenderer, /DrawTokenOverview/);
  assert.match(radarRenderer, /CodexTodayCacheHitRate/);
  assert.match(radarRenderer, /CodexTotalCacheHitRate/);
  assert.match(radarRenderer, /DrawTokenMetricGroup/);
  assert.match(radarRenderer, /DrawTokenMetricRow/);
  assert.match(nativeText, /CodexTokenMetricTitle/);
  assert.match(nativeText, /CodexCacheMetricTitle/);
  assert.match(radarRenderer, /DrawTokenRadarFooter/);
  assert.match(radarRenderer, /DrawTokenRadarFooterGroup/);
  assert.match(radarRenderer, /DrawTokenRadarFooterField/);
  assert.match(nativeText, /CodexTokenRadarMetricTitle/);
  assert.match(nativeText, /CodexCacheRadarMetricTitle/);
  assert.match(tokenUsageReader, /cached_input_tokens/);
  assert.match(tokenUsageReader, /cachedInputTokens\.Value \* 100d \/ inputTokens\.Value/);
  assert.match(tokenUsageReader, /TodayCacheHitPercent/);
  assert.match(tokenUsageReader, /TotalCacheHitPercent/);
});

test('Codex quota hover separates source-qualified raw-token references', () => {
  assert.match(popover, /LogicalCodexTokenBodyHeight = 207/);
  assert.match(popover, /DrawQuotaTokenCapacity/);
  assert.match(popover, /const int columnCount = 2/);
  assert.match(popover, /row \* 15/);
  assert.match(popover, /content\.Card\.Provider == ProviderKind\.Codex && !content\.Card\.IsService/);
  assert.doesNotMatch(popover, /CodexQuotaTokens\?\.HasData/);
  assert.match(nativeText, /Token · 原始用量/);
  assert.match(nativeText, /Tokens · raw usage/);
  assert.match(nativeText, /已用\{percent\}/);
  assert.match(nativeText, /样本100%/);
  assert.match(nativeText, /近4周\/周/);
  assert.match(nativeText, /本机下限/);
  assert.match(nativeText, /IsCurrentLocalFallback/);
  assert.match(nativeText, /RecentWeeklyAverageTokens/);
  assert.match(nativeText, /"≥"/);
  assert.match(nativeText, /EstimateUsedTokens/);
  assert.match(popover, /CodexQuotaObservationEvidence/);
  assert.doesNotMatch(nativeText, /Token · 本机估算/);
  assert.match(barForm, /SetCodexQuotaTokenSummaries/);
  assert.match(applicationContext, /CodexQuotaTokenObservations\(next, observedAt\)/);
  assert.match(applicationContext, /recentWeeklyByCard/);
  assert.match(applicationContext, /RecentWeeklyAverageTokens/);
  assert.match(applicationContext, /ProfileLifetimeSourceKey/);
  assert.match(applicationContext, /MergeImported\(completed\.Result\.Samples, now\)/);
  assert.match(applicationContext, /completed\.Result\.Observations/);
  assert.match(applicationContext, /IsRolloutFallbackEligible\(observation, now\)/);
  assert.match(applicationContext, /ToRolloutFallbackSourceKey\(/);
  assert.match(applicationContext, /Merge\(fallbackObservations, now\)/);
  assert.match(codexQuotaService, /stats\.Value\.TryGetProperty\(\s*"daily_usage_buckets"/);
  assert.match(codexQuotaService, /"start_date"/);
  assert.match(codexQuotaService, /"tokens"/);
  assert.match(models, /\[property: JsonIgnore\] long\? RecentWeeklyAverageTokens/);
});

test('Radar uses upstream measurements but keeps exactly four local scenario picks', () => {
  assert.match(radarService, /RecommendationsUri = new\("https:\/\/codexradar\.com\/api\/radar-insights"\)/);
  assert.match(radarService, /MeasurementsUri = new\("https:\/\/codexradar\.com\/data\/intelligence-efficiency\.json"\)/);
  assert.match(radarService, /MaxResponseContentBufferSize = 4 \* 1024 \* 1024/);
  assert.match(radarService, /class RadarRecommendationsParser/);
  assert.match(radarService, /class RadarMeasurementsParser/);
  assert.match(radarService, /ArrayProperty\("points"\)/);
  assert.match(radarService, /ArrayProperty\("history"\)/);
  assert.match(radarService, /ArrayProperty\("recommendations"\)/);
  assert.match(radarService, /groupValue\.StringProperty\("key"\)/);
  assert.match(radarService, /groupValue\.StringProperty\("title"\)/);
  assert.match(radarService, /groupValue\.StringProperty\("rule"\)/);
  assert.match(radarService, /record RadarResetWindow/);
  assert.match(radarService, /ObjectProperty\("window"\)/);
  assert.match(radarPresentation, /RecommendationGroupIndexes/);
  assert.match(radarPresentation, /RadarScenarioEvaluator\.Evaluate\(sourceModels\)/);
  assert.match(radarPresentation, /RadarScenarioEvaluator\.DailyDevelopmentKey/);
  assert.match(radarPresentation, /RadarScenarioEvaluator\.HardProblemsKey/);
  assert.match(radarPresentation, /RadarScenarioEvaluator\.TaskExecutionKey/);
  assert.match(radarPresentation, /RadarScenarioEvaluator\.BackgroundAutomationKey/);
  assert.match(radarPresentation, /GroupBy\(model => model\.Model/);
  assert.doesNotMatch(radarPresentation, /MaxDisplayRows/);
  assert.match(radarPresentation, /ThenByDescending\(row => EffortRank\(row\.Model\.ReasoningEffort\)\)/);
  assert.match(radarPresentation, /ThenBy\(row => DeepSeekLaneOrder\(row\.Model\)\)/);
  assert.match(radarPresentation, /FormatIqComparison/);
  assert.doesNotMatch(radarLayout, /IqDirection/);
  assert.match(radarRenderer, /comparison\.Value\.DirectionText/);
  assert.doesNotMatch(radarPresentation, /snapshot\.RecommendationFeed|lobster_tasks|SelectionFor/);
  assert.match(radarScenarioEvaluator, /PolicyVersion = "local-scenarios-v7"/);
  assert.match(radarScenarioEvaluator, /MinimumValidTasks = 50/);
  assert.match(radarScenarioEvaluator, /HasSufficientSamples/);
  assert.match(radarScenarioEvaluator, /ConfidenceLowerBoundIq/);
  assert.match(radarScenarioEvaluator, /HistoryDownsideIq/);
  assert.match(radarScenarioEvaluator, /TaskExecutionIqWeight = 0\.45/);
  assert.match(radarScenarioEvaluator, /TaskExecutionCostWeight = 0\.45/);
  assert.match(radarScenarioEvaluator, /TaskExecutionTimeWeight = 0\.10/);
  assert.match(radarScenarioEvaluator, /BackgroundIqWeight = 0\.25/);
  assert.match(radarScenarioEvaluator, /BackgroundCostWeight = 0\.70/);
  assert.match(radarScenarioEvaluator, /BackgroundTimeWeight = 0\.05/);
  assert.match(radarScenarioEvaluator, /DailyIqRetention = 0\.80/);
  assert.match(radarScenarioEvaluator, /DailyMaximumIqLoss = 20\.0/);
  assert.match(radarScenarioEvaluator, /DailyIqWeight = 0\.50/);
  assert.match(radarScenarioEvaluator, /OrderByDescending\(candidate => candidate\.GuardedIq\)/);
  assert.match(radarScenarioEvaluator, /BackgroundIqRetention = 0\.70/);
  assert.doesNotMatch(radarScenarioEvaluator, /"(?:ultra|max|xhigh|high|medium|low|sol|terra|luna)"/i);
  assert.doesNotMatch(radarService, /MaxGroups|MaxItemsPerGroup|\.Take\(Max(?:Groups|ItemsPerGroup)\)/);
  assert.doesNotMatch(`${radarPresentation}\n${radarService}`, /RadarRecommendationEvaluator|RadarValueEvaluator|daily-balanced|work-value/);
  assert.doesNotMatch(radarPresentation + radarRenderer, /RadarFactKind|DrawFacts|FastestAverage|LowestAverageCost/);
  assert.doesNotMatch(radarLayout, /StrongestBounds|OverallBounds|ValueBounds/);
  assert.match(radarLayout, /RadarPopoverColumn Marker/);
  assert.doesNotMatch(radarLayout, /RecommendationBounds|LogicalRecommendationStep/);
  assert.doesNotMatch(radarRenderer, /DrawRecommendations|group\.Title|group\.Rule|group\.Items/);
  assert.match(radarRenderer, /RecommendationColors/);
  assert.match(radarRenderer, /RadarExecutionScenarioTitle/);
  assert.match(radarRenderer, /RadarSampleHeader/);
  assert.match(radarRenderer, /row\.SampleCountText/);
  assert.match(radarRenderer, /RadarStatusIndicator\.Unknown/);
  assert.match(radarRenderer, /text\.RadarUnknownStatusLegend/);
  assert.match(radarRenderer, /text\.RadarConfidenceNote/);
  assert.match(radarRenderer, /layout\.ResetBounds/);
  assert.match(nativeText, /RadarResetWindow\(RadarResetWindow\?/);
  assert.match(radarRenderer, /ResetOpenColor = Color\.FromArgb\(251, 113, 133\)/);
  assert.match(radarRenderer, /private static void DrawFilledCircle/);
  assert.match(radarRenderer, /new Rectangle\(dotX, dotY, diameter, diameter\),\s+ResetOpenColor/);
  assert.match(radarRenderer, /DrawDistinctionIcons/);
  assert.doesNotMatch(radarRenderer, /RadarPassHeader|row\.PassText|DrawDistinctionBadge/);
  assert.doesNotMatch(radarRenderer, /DrawRankMedal/);
  assert.doesNotMatch(radarRenderer, /BEST OVERALL|BEST VALUE|综合最优|性价比优先/);
  assert.doesNotMatch(radarService, /api.?key|Authorization/i);
  assert.doesNotMatch(
    radarService,
    /Headers\.(?:Add|TryAddWithoutValidation)\(\s*["']Cookie/i,
  );
});

test('Radar open reset window uses the upstream page clock and a visible countdown timer', () => {
  assert.match(radarService, /SiteUri = new\(SiteUrl\)/);
  assert.match(radarService, /class RadarHomePageParser/);
  assert.match(radarService, /data-window-closes-at/);
  assert.match(radarService, /FetchTextAsync\(SiteUri, "text\/html", cancellationToken\)/);
  assert.match(radarService, /window\.TargetAt is \{ \} suppliedTarget/);
  assert.match(radarService, /UseCookies = false/);
  assert.doesNotMatch(radarService, /2026-08-24T05:00:00/);
  assert.match(nativeText, /距离预计重置 \{FormatResetCountdown\(remaining\)\}/);
  assert.match(nativeText, /EXPECTED RESET IN \{FormatResetCountdown\(remaining\)\}/);
  assert.match(nativeText, /AWAITING OFFICIAL RESET CONFIRMATION/);
  assert.match(radarRenderer, /RadarResetWindow\(window, now\)/);
  assert.match(radarPopover, /_countdownTimer = new\(\) \{ Interval = 1_000 \}/);
  assert.match(radarPopover, /HasActiveResetCountdown/);
  assert.match(radarPopover, /public void HidePopover\(\)[\s\S]*?_countdownTimer\.Stop\(\)/);
  assert.match(radarPopover, /_countdownTimer\.Dispose\(\)/);
});

test('Radar developer CLI emits raw data plus the local scenario policy', () => {
  assert.match(radarCli, /RadarParser\.Parse\(json, DateTimeOffset\.UnixEpoch\)/);
  assert.match(radarCli, /snapshot\.RecommendationFeed/);
  assert.match(radarCli, /SchemaVersion = 6/);
  assert.match(radarCli, /Recommendations = feed\.Groups/);
  assert.match(radarCli, /RadarScenarioEvaluator\.Evaluate\(models\)/);
  assert.match(radarCli, /LocalRecommendations/);
  assert.match(radarCli, /MaximumInputBytes = 1024 \* 1024/);
  assert.doesNotMatch(radarCli, /api.?key|Authorization|Cookie/i);
  assert.doesNotMatch(nativeProject, /ProjectReference[^>]+ZGSTokenBar\.ContractTests/);
});

test('pinned quota bubble closes on outside click or Escape without global hooks', () => {
  assert.match(barForm, /Control\.MouseButtons/);
  assert.match(barForm, /GetAsyncKeyState\(VkEscape\)/);
  assert.doesNotMatch(barForm + popover, /RegisterHotKey|SetWindowsHookEx/);
});

test('quota bubble is a topmost no-activation tool window anchored by placement math', () => {
  assert.match(popover, /ShowWithoutActivation => true/);
  assert.match(popover, /ToolWindowStyle \| NoActivateStyle/);
  assert.match(popover, /ShowInTaskbar = false/);
  assert.match(popover, /TopMost = true/);
  assert.match(popover, /TaskbarMiniPopoverMath\.Place/);
  assert.match(models, /public static class TaskbarMiniPopoverMath/);
});

test('quota bubble exposes exact reset, countdown, and freshness details', () => {
  assert.match(popover, /_text\.ResetAt/);
  assert.match(popover, /_text\.FormatResetCountdown/);
  assert.match(popover, /_text\.Freshness/);
  assert.match(nativeText, /public string ResetAt/);
  assert.match(popover, /CapturedAt/);
  assert.match(popover, /remaining/);
  assert.match(popover, /used/);
  assert.match(productReadme, /Hover a real Mini quota capsule/);
  assert.match(productReadme, /Click outside or press Escape/);
});

test('Sub2API popover dispatch and renderers use the shared anonymous presentation state', () => {
  const contentBodyHeight = boundedSlice(
    popover,
    'private int ContentBodyHeight',
    'internal static int AccountAvailabilityBodyHeight');
  assert.equal(
    (contentBodyHeight.match(/Sub2ApiServicePresentation\.Resolve\(\s*content\.Card,\s*_renderNow \?\? content\.CapturedAt\)/g) ?? []).length,
    1,
    'ContentBodyHeight resolves the shared presentation once');
  assert.match(contentBodyHeight, /CompleteAvailability[\s\S]*?includeProgressRail: true/);
  assert.match(
    contentBodyHeight,
    /PartialAvailability[\s\S]*?KnownNoneAvailability[\s\S]*?AccountAvailabilityBodyHeight\(accounts\.Count\)/);

  const serviceContent = boundedSlice(
    popover,
    'private void DrawServiceContent(',
    'private void DrawBalanceServiceContent(');
  assert.equal(
    (serviceContent.match(/Sub2ApiServicePresentation\.Resolve\(\s*content\.Card,\s*_renderNow \?\? content\.CapturedAt\)/g) ?? []).length,
    1,
    'DrawServiceContent resolves the shared presentation once');
  assert.match(serviceContent, /switch \(presentation\.Kind\)/);

  const completeCase = boundedSlice(
    serviceContent,
    'case Sub2ApiServicePresentationKind.CompleteAvailability',
    'case Sub2ApiServicePresentationKind.PartialAvailability');
  assert.match(completeCase, /DrawSub2ApiAccountAvailabilityServiceContent/);
  assert.match(completeCase, /includeProgressRail: true/);

  const partialCase = boundedSlice(
    serviceContent,
    'case Sub2ApiServicePresentationKind.PartialAvailability',
    'case Sub2ApiServicePresentationKind.LegacyAggregateQuota');
  assert.match(partialCase, /Sub2ApiServicePresentationKind\.KnownNoneAvailability/);
  assert.match(partialCase, /DrawSub2ApiAccountAvailabilityServiceContent/);
  assert.doesNotMatch(partialCase, /includeProgressRail/);

  const legacyCase = boundedSlice(
    serviceContent,
    'case Sub2ApiServicePresentationKind.LegacyAggregateQuota',
    'case Sub2ApiServicePresentationKind.Usage');
  assert.match(legacyCase, /DrawSub2ApiLegacyQuotaServiceContent\([\s\S]*?legacy\)/);

  const usageCase = boundedSlice(
    serviceContent,
    'case Sub2ApiServicePresentationKind.Usage',
    'case Sub2ApiServicePresentationKind.Pool');
  assert.match(usageCase, /DrawSub2ApiUsageServiceContent\([\s\S]*?usage\)/);

  const poolCase = boundedSlice(
    serviceContent,
    'case Sub2ApiServicePresentationKind.Pool',
    'default:');
  assert.match(poolCase, /DrawSub2ApiPoolServiceContent\([\s\S]*?pool\)/);

  const unavailableCase = boundedSlice(serviceContent, 'default:', 'if (content.Card.Sub2ApiAccountAvailability');
  assert.match(unavailableCase, /DrawSub2ApiUnavailableServiceContent/);
  assert.match(serviceContent, /_text\.ApiServiceNoQuota/);

  const availabilityRenderer = boundedSlice(
    popover,
    'private void DrawSub2ApiAccountAvailabilityServiceContent(',
    'private void DrawSub2ApiLegacyQuotaServiceContent(');
  assert.match(availabilityRenderer, /_text\.Sub2ApiAccountAvailabilityCoverage\(availability\)/);
  assert.match(availabilityRenderer, /_text\.Sub2ApiAccountAvailabilityPercent\(account\)/);
  assert.match(availabilityRenderer, /includeProgressRail[\s\S]*?MeanRemainingPercent\(availability\)/);
  assert.match(availabilityRenderer, /account\.RemainingPercent is \{ \} remaining[\s\S]*?ForRemaining\(remaining\)/);
  assert.match(availabilityRenderer, /account\.RemainingPercent is \{ \} remaining[\s\S]*?Color\.FromArgb/);
  assert.doesNotMatch(availabilityRenderer, /ForRemaining\(account\.RemainingPercent/);
  assert.match(nativeText, /account\.RemainingPercent is \{ \} remaining[\s\S]*?T\("未知", "Unknown"\)/);

  const legacyRenderer = boundedSlice(
    popover,
    'private void DrawSub2ApiLegacyQuotaServiceContent(',
    'private void DrawSub2ApiUnavailableServiceContent(');
  assert.match(legacyRenderer, /_text\.Sub2ApiLegacyQuotaHeadline\(legacy\)/);
  assert.match(legacyRenderer, /DrawSub2ApiProgressRail/);
  assert.doesNotMatch(
    legacyRenderer,
    /Sub2ApiQuotaHeadline|Sub2ApiQuotaWindowDetails|ResetAt|FormatResetCountdown|5h|7d|content\.Window/);

  const unavailableRenderer = boundedSlice(
    popover,
    'private void DrawSub2ApiUnavailableServiceContent(',
    'private static void DrawSub2ApiProgressRail(');
  assert.match(unavailableRenderer, /_text\.Sub2ApiUnavailable/);
  assert.doesNotMatch(unavailableRenderer, /ApiServiceNoQuota/);

  const usageRenderer = boundedSlice(
    popover,
    'private void DrawSub2ApiUsageServiceContent(',
    'private void DrawSub2ApiAccountAvailabilityServiceContent(');
  assert.match(usageRenderer, /content\.Card\.Sub2ApiPool is \{ \} pool/);
  assert.match(usageRenderer, /_text\.Sub2ApiUsagePool\(pool\)/);
});
