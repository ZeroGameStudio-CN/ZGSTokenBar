import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const models = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const barForm = fs.readFileSync('src/ZGSTokenBar.App/BarForm.cs', 'utf8');
const applicationContext = fs.readFileSync('src/ZGSTokenBar.App/QuotaApplicationContext.cs', 'utf8');
const popoverForm = fs.readFileSync('src/ZGSTokenBar.App/QuotaPopoverForm.cs', 'utf8');
const codexAccountsPopoverForm = fs.readFileSync('src/ZGSTokenBar.App/CodexAccountsPopoverForm.cs', 'utf8');
const planBadgePresentation = fs.readFileSync('src/ZGSTokenBar.App/PlanBadgePresentation.cs', 'utf8');
const hintPopoverForm = fs.readFileSync('src/ZGSTokenBar.App/TaskbarHintPopoverForm.cs', 'utf8');
const radarPopoverForm = fs.readFileSync('src/ZGSTokenBar.App/ProviderRadarPopoverForm.cs', 'utf8');
const radarRenderer = fs.readFileSync('src/ZGSTokenBar.App/RadarPopoverRenderer.cs', 'utf8');
const spendHistoryLayout = fs.readFileSync('src/ZGSTokenBar.Core/CodexSpendHistoryLayout.cs', 'utf8');
const radarResetTiming = fs.readFileSync('src/ZGSTokenBar.App/RadarResetTiming.cs', 'utf8');
const nativeProject = fs.readFileSync('src/ZGSTokenBar.App/ZGSTokenBar.App.csproj', 'utf8');
const settingsForm = fs.readFileSync('src/ZGSTokenBar.App/SettingsForm.cs', 'utf8');
const nativeText = fs.readFileSync('src/ZGSTokenBar.App/NativeText.cs', 'utf8');
const appSettings = fs.readFileSync('src/ZGSTokenBar.Core/AppSettings.cs', 'utf8');
const backgroundPalette = fs.readFileSync('src/ZGSTokenBar.App/QuotaBackgroundPalette.cs', 'utf8');
const processActivity = fs.readFileSync('src/ZGSTokenBar.Core/ProviderProcessActivity.cs', 'utf8');
const cockpitAccounts = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexAccountDirectory.cs', 'utf8');
const cockpitInstanceActivity = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexInstanceActivity.cs', 'utf8');
const cockpitQuotaReader = fs.readFileSync('src/ZGSTokenBar.Core/CockpitCodexQuotaReader.cs', 'utf8');
const codexQuotaService = fs.readFileSync('src/ZGSTokenBar.Core/CodexQuotaService.cs', 'utf8');
const quotaCoordinator = fs.readFileSync('src/ZGSTokenBar.Core/QuotaCoordinator.cs', 'utf8');
const taskbarGrouping = fs.readFileSync('src/ZGSTokenBar.App/TaskbarMiniGrouping.cs', 'utf8');
const codexPoolProjection = fs.readFileSync('src/ZGSTokenBar.App/CodexPoolCardProjection.cs', 'utf8');
const codexPoolPresentation = fs.readFileSync('src/ZGSTokenBar.App/CodexPoolPresentation.cs', 'utf8');
const modelsSource = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const radarAlertTracker = fs.readFileSync('src/ZGSTokenBar.Core/RadarAlertTracker.cs', 'utf8');
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

  const start = barForm.indexOf('    private void DrawTaskbarCapsule(');
  const end = barForm.indexOf('    private void DrawProviderLogo(', start);
  const body = barForm.slice(start, end);
  assert.notEqual(start, -1);
  assert.notEqual(end, -1);
  assert.match(body, /FormatWindowShort\(window\)/);
  assert.match(body, /QuotaDisplayFormatting\.WeeklyBlockReset\(card, window, now\)/);
  assert.match(body, /QuotaDisplayFormatting\.FormatResetShort\(weeklyBlockReset \?\? window\.ResetsAt, now\)/);
  assert.match(models, /return \$"\{\(int\)remaining\.TotalDays\}d\{remaining\.Hours\}h";/);
  assert.match(body, /DrawTaskbarLockIcon/);
  assert.match(body, /DrawResetClockIcon/);
});

test('available quota details stay bright while blocked and missing states are muted', () => {
  const start = barForm.indexOf('    private void DrawTaskbarCapsule(');
  const end = barForm.indexOf('    private void DrawTaskbarProgressRail(', start);
  const body = barForm.slice(start, end);
  assert.match(body, /labelBrush = new SolidBrush\(blockedByWeekly[\s\S]*?100, 116, 139[\s\S]*?226, 232, 240/);
  assert.match(body, /resetBrush = new SolidBrush\(blockedByWeekly \|\| window\.ResetsAt is null[\s\S]*?100, 116, 139[\s\S]*?226, 232, 240/);
  assert.match(popoverForm, /resetValueBrush = new SolidBrush\([\s\S]*?WeeklyBlockResetAt[\s\S]*?Window\.ResetsAt is null[\s\S]*?100, 116, 139[\s\S]*?226, 232, 240/);

  const fullStart = barForm.indexOf('    private void DrawQuotaRow(');
  const fullEnd = barForm.indexOf('    private void DrawHealth(', fullStart);
  const fullBody = barForm.slice(fullStart, fullEnd);
  assert.match(fullBody, /QuotaDisplayFormatting\.WeeklyBlockReset\(card, window, now\)/);
  assert.match(fullBody, /blockedByWeekly[\s\S]*?DrawTaskbarLockIcon/);
  assert.match(fullBody, /remainingColor = blockedByWeekly[\s\S]*?100, 116, 139[\s\S]*?QuotaColorScale\.ForRemaining/);
  assert.match(fullBody, /FormatCompactReset\(resetAt, now\)/);
});

test('Radar row emphasis follows distinctions instead of source position', () => {
  const start = radarRenderer.indexOf('    private static void DrawRows(');
  const end = radarRenderer.indexOf('    private static void DrawDistinctionIcons(', start);
  const body = radarRenderer.slice(start, end);
  assert.match(body, /distinctionCount = \(strongest \? 1 : 0\)[\s\S]*?row\.RecommendationGroupIndexes\.Count/);
  assert.match(body, /multipleDistinctions = distinctionCount > 1/);
  assert.match(body, /distinguished = distinctionCount > 0/);
  assert.match(body, /labelColor = Color\.FromArgb\(226, 232, 240\)/);
  assert.match(body, /modelFont = distinguished \? fonts\.EmphasizedModel : fonts\.Model/);
  assert.match(body, /recommendationColor = row\.RecommendationGroupIndexes\.Count[\s\S]*?RecommendationColor/);
  assert.match(body, /modelColor = strongest[\s\S]*?\? StrongestColor[\s\S]*?: recommendationColor/);
  assert.match(body, /if \(multipleDistinctions\)[\s\S]*?DrawRainbowText\([\s\S]*?row\.ModelText/);
  assert.doesNotMatch(body, /SourceIndex == 0/);
});

test('Codex spend history reserves a compact 30-day chart and three-model composition', () => {
  assert.match(spendHistoryLayout, /LogicalNarrowWidth = 360/);
  assert.match(spendHistoryLayout, /LogicalWideWidth = RadarPopoverLayout\.LogicalWidth/);
  assert.match(spendHistoryLayout, /LogicalHeight = 270/);
  assert.match(spendHistoryLayout, /new List<Rectangle>\(4\)/);
  assert.match(spendHistoryLayout, /CreateBarBounds\(chartBounds, dayCount/);
  assert.match(spendHistoryLayout, /Rect\(12, 201, innerWidth, 18\)[\s\S]*?Rect\(12, 222, innerWidth, 18\)[\s\S]*?Rect\(12, 243, innerWidth, 18\)/);
  assert.match(radarRenderer, /var firstRecentIndex = Math\.Max\(0, count - 7\)/);
  assert.match(radarRenderer, /isRecent[\s\S]*?Color\.FromArgb\(190, 129, 140, 248\)/);
  assert.match(radarRenderer, /HasUnpricedUsage[\s\S]*?Color\.FromArgb\(251, 191, 36\)/);
  assert.match(radarRenderer, /\.Take\(layout\.ModelRowBounds\.Count\)/);
});

test('developer captures keep separate narrow and wide bilingual spend-history references', () => {
  assert.match(testProgram, /static CodexTokenUsageSummary SpendHistoryCaptureUsage\(/);
  assert.match(testProgram, /new CodexSpendHistory\(days, models, last7Days\)/);
  assert.match(testProgram, /new CodexSpendModel\("gpt-5\.6-sol"/);
  assert.match(testProgram, /new CodexSpendModel\("gpt-5\.6-terra"/);
  assert.match(testProgram, /new CodexSpendModel\("gpt-5\.6-luna"/);
  assert.match(testProgram, /renderer\.DrawSpendHistory\(/);
  assert.match(testProgram, /taskbar-mini-codex-spend-history-\{locale\}-\{dpi\}dpi\.png/);
  assert.match(testProgram, /native-localization-spend-history-\{locale\}-\{dpi\}dpi\.png/);
});

test('Radar uses distinct local scenario markers in the compact footer', () => {
  const rowsStart = radarRenderer.indexOf('    private static void DrawRows(');
  const rowsEnd = radarRenderer.indexOf('    private static void DrawDistinctionIcons(', rowsStart);
  const rowsBody = radarRenderer.slice(rowsStart, rowsEnd);
  const footerStart = radarRenderer.indexOf('    private static void DrawFooter(');
  const footerEnd = radarRenderer.indexOf('    private static void DrawColumnText(', footerStart);
  const footerBody = radarRenderer.slice(footerStart, footerEnd);
  assert.match(radarRenderer, /RecommendationColors =[\s\S]*?52, 211, 153[\s\S]*?34, 211, 238[\s\S]*?244, 114, 182/);
  assert.match(radarRenderer, /MultiDistinctionColors =[\s\S]*?StrongestColor[\s\S]*?RecommendationColors/);
  assert.match(footerBody, /RadarStrongestTitle[\s\S]*?RadarDailyScenarioTitle[\s\S]*?RadarPlanningScenarioTitle[\s\S]*?RadarBackgroundScenarioTitle/);
  assert.match(footerBody, /DrawStar[\s\S]*?DrawRecommendationMarker/);
  assert.match(radarRenderer, /case 1:[\s\S]*?FillPolygon/);
  assert.match(radarRenderer, /case 2:[\s\S]*?FillRectangle/);
  assert.match(radarRenderer, /default:[\s\S]*?FillEllipse/);
  assert.match(rowsBody, /multipleDistinctions = distinctionCount > 1/);
  assert.match(rowsBody, /else if \(distinguished\)[\s\S]*?highlightColor = strongest[\s\S]*?recommendationColor/);
  assert.match(rowsBody, /DrawRainbowSurface\([\s\S]*?highlightBounds[\s\S]*?168/);
  assert.match(rowsBody, /DrawRainbowText\([\s\S]*?row\.ModelText/);
  assert.match(radarRenderer, /new LinearGradientBrush\([\s\S]*?LinearGradientMode\.Horizontal/);
  assert.match(radarRenderer, /InterpolationColors = new ColorBlend/);
  assert.match(radarRenderer, /DrawRainbowText\([\s\S]*?MeasureString\([\s\S]*?RainbowBrush\(spectrumBounds, 255\)[\s\S]*?graphics\.DrawString/);
  assert.match(radarRenderer, /DrawDistinctionIcons\([\s\S]*?strongest,[\s\S]*?row\.RecommendationGroupIndexes/);
  assert.doesNotMatch(radarRenderer, /DrawRecommendations/);
});

test('taskbar Mini prefers real quota windows and keeps labels for a fully unavailable provider', () => {
  assert.match(models, /public static IReadOnlyList<QuotaWindow> VisibleWindows\(/);
  assert.match(models, /window\.UsedPercent is not null \|\| window\.ResetsAt is not null/);
  assert.match(models, /return available\.Length > 0[\s\S]*windows\.Take\(MaximumWindows\)/);
  assert.match(barForm, /TaskbarMiniLayoutMath\.VisibleWindows\(card\.Windows\)/);
  assert.doesNotMatch(barForm, /fallbackLabel/);
  assert.doesNotMatch(barForm, /DrawQuotaRing/);
});

test('taskbar Mini rails keep current remaining primary and mark the daily budget goal', () => {
  const capsuleStart = barForm.indexOf('    private void DrawTaskbarCapsule(');
  const capsuleEnd = barForm.indexOf('    private void DrawTaskbarProgressRail(', capsuleStart);
  const capsuleBody = barForm.slice(capsuleStart, capsuleEnd);
  assert.notEqual(capsuleStart, -1);
  assert.notEqual(capsuleEnd, -1);
  assert.match(capsuleBody, /budgetMarkerRemaining = !blockedByWeekly/);
  assert.match(capsuleBody, /QuotaDisplayFormatting\.BudgetMarkerRemaining\(window, pace\?\.Cycle, now\)/);
  assert.match(capsuleBody, /DrawTaskbarProgressRail\(graphics, bounds, remaining, valueColor, budgetMarkerRemaining\)/);

  const railEnd = barForm.indexOf('    private void DrawResetClockIcon(', capsuleEnd);
  const railBody = barForm.slice(capsuleEnd, railEnd);
  assert.notEqual(railEnd, -1);
  assert.match(railBody, /new Pen\(Color\.FromArgb\(30, 41, 59\), 3\)/);
  assert.match(railBody, /new Pen\(valueColor, 2\)/);
  assert.match(railBody, /new Pen\(Color\.FromArgb\(71, 85, 105\), 2\)/);
  assert.match(railBody, /EndCap = clampedRemaining >= 100 \? LineCap\.Round : LineCap\.Flat/);
  assert.match(railBody, /StartCap = clampedRemaining <= 0 \? LineCap\.Round : LineCap\.Flat/);
  assert.match(railBody, /DrawBudgetMarkerPointer/);
  assert.match(railBody, /Color\.FromArgb\(253, 230, 138\)/);
  assert.match(railBody, /markerCoreBrush = new SolidBrush\(Color\.FromArgb\(30, 41, 59\)\)/);
  const pointerStart = railBody.indexOf('    private static void DrawBudgetMarkerPointer(');
  assert.notEqual(pointerStart, -1);
  assert.doesNotMatch(railBody.slice(pointerStart), /DrawLine/);

  const dualStart = barForm.indexOf('    private void DrawTaskbarDualMetric(');
  const dualEnd = barForm.indexOf('    private void DrawTaskbarCapsule(', dualStart);
  const dualBody = barForm.slice(dualStart, dualEnd);
  assert.notEqual(dualStart, -1);
  assert.notEqual(dualEnd, -1);
  assert.match(dualBody, /QuotaDisplayFormatting\.BudgetMarkerRemaining\(/);
  assert.match(dualBody, /DrawTaskbarQuotaRail/);
});

test('native quota surfaces use one opaque four-level background palette', () => {
  assert.match(appSettings, /DefaultBackgroundPalette = "midnight"/);
  assert.match(appSettings, /"midnight" or "graphite" or "navy" or "plum"/);
  assert.match(backgroundPalette, /Color Outer,[\s\S]*?Color ProviderGroup,[\s\S]*?Color QuotaGroup,[\s\S]*?Color Popover/);
  assert.match(backgroundPalette, /"midnight"[\s\S]*?2, 6, 23[\s\S]*?6, 11, 22[\s\S]*?10, 18, 32[\s\S]*?7, 12, 24/);
  assert.match(backgroundPalette, /"graphite"[\s\S]*?8, 9, 11[\s\S]*?18, 19, 22[\s\S]*?27, 28, 31[\s\S]*?16, 17, 20/);
  assert.match(backgroundPalette, /"navy"/);
  assert.match(backgroundPalette, /"plum"/);
  assert.match(barForm, /panelBrush = new SolidBrush\(_backgroundTheme\.Outer\)/);
  assert.match(barForm, /fill = new SolidBrush\(_backgroundTheme\.ProviderGroup\)/);
  assert.match(barForm, /_backgroundTheme\.QuotaGroup/);
  assert.match(popoverForm, /fill = new SolidBrush\(_backgroundTheme\.Popover\)/);
  assert.match(popoverForm, /statusFill = new SolidBrush\(_backgroundTheme\.QuotaGroup\)/);
  assert.match(settingsForm, /QuotaBackgroundPalette\.All/);
  assert.match(settingsForm, /new PaletteChoiceButton\([\s\S]*?palette\.QuotaGroup/);
  assert.match(settingsForm, /BackgroundPalette = _backgroundPalette/);
  assert.match(settingsForm, /BorderSize = selected \? 2 : 1/);
  assert.match(nativeText, /背景配色/);
  assert.doesNotMatch(`${barForm}\n${popoverForm}`, /WindowBackdrop|Acrylic|_glassBackdropEnabled/);
  assert.doesNotMatch(nativeProject, /PackageReference/);
});

test('stacked Codex accounts use one adaptive module with a four-account grid', () => {
  assert.match(barForm, /TaskbarCodexCapsuleBounds\(bounds, index, cards\.Count\)/);
  assert.match(barForm, /DrawTaskbarAccountOrdinalColumn\(graphics, bounds, cards\)/);
  assert.match(barForm, /new RectangleF\(cardBounds\.X \+ 31, cardBounds\.Y \+ 2, 10, 32\)/);
  assert.match(barForm, /var slotHeight = 32f \/ accountCount/);
  assert.match(barForm, /TaskbarCompactCodexTileBounds\(cardBounds, index, accountCount\)/);
  assert.match(barForm, /var column = index % 2/);
  assert.match(barForm, /var row = index \/ 2/);
  assert.match(barForm, /DrawTaskbarCompactCodexCapsule\(graphics, target\)/);
  assert.match(taskbarGrouping, /Cards\.Count >= 2/);
  assert.match(taskbarGrouping, /OrderBy\(item => PlanSortRank\(item\.Card\)\)/);
  assert.match(taskbarGrouping, /ThenByDescending\(item => item\.Remaining/);
  assert.match(barForm, /PlanBadgePresentation\.TryGetStyle\(label, out var style\)/);
  assert.doesNotMatch(taskbarGrouping, /group\.Count < 2/);
  assert.match(taskbarGrouping, /card\.Windows\.Count > 0[\s\S]*?new QuotaWindow\("7d", null, null/);
  const groupStart = barForm.indexOf('    private void DrawTaskbarCodexGroup(');
  const groupEnd = barForm.indexOf('    private void DrawTaskbarAccountOrdinalColumn(', groupStart);
  assert.doesNotMatch(barForm.slice(groupStart, groupEnd), /DrawAccountOrdinalBadge/);
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
  assert.match(barForm, /Sub2ApiServicePresentation\.IsSub2ApiService\(target\.Card\)[\s\S]*?DrawTaskbarSub2ApiMetric\(graphics, target, hover\)/);
  const sub2ApiMiniStart = barForm.indexOf('    private void DrawTaskbarSub2ApiMetric(');
  const sub2ApiMiniEnd = barForm.indexOf('    private static Color Sub2ApiPresentationCompactStatusColor(', sub2ApiMiniStart);
  const sub2ApiMini = barForm.slice(sub2ApiMiniStart, sub2ApiMiniEnd);
  assert.notEqual(sub2ApiMiniStart, -1);
  assert.notEqual(sub2ApiMiniEnd, -1);
  assert.match(sub2ApiMini, /Sub2ApiServicePresentation\.Resolve\(target\.Card, _snapshot\.CapturedAt\)/);
  assert.match(sub2ApiMini, /CompleteAvailability[\s\S]*?DrawTaskbarProgressRail/);
  assert.match(sub2ApiMini, /PartialAvailability[\s\S]*?KnownNoneAvailability[\s\S]*?Sub2ApiAccountAvailabilityCompact/);
  assert.match(sub2ApiMini, /LegacyAggregateQuota[\s\S]*?Sub2ApiLegacyQuotaCompact/);
  assert.match(sub2ApiMini, /_text\.Sub2ApiUnavailable/);
  assert.doesNotMatch(sub2ApiMini, /Sub2ApiQuota(?:Headline|WindowDetails|SummaryShort)|FormatReset|\b(?:5h|7d)\b/);
  assert.match(barForm, /_text\.Sub2ApiAccountAvailabilityCompact\(accountAvailability!\)/);
  assert.match(barForm, /_text\.Sub2ApiQuotaCompact\(quota!?\)/);
  assert.match(barForm, /_text\.Sub2ApiUsageCompact\(usage\)/);
  assert.match(barForm, /Sub2ApiPoolFormatting\.AccountPair\(pool\)/);
  assert.match(barForm, /Sub2ApiQuotaCompactStatusColor/);
  assert.match(barForm, /Sub2ApiUsageCompactStatusColor/);
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

test('Codex Mini groups use deterministic unique plugin-compatible area ids', () => {
  assert.match(taskbarGrouping, /record TaskbarMiniCardGroup\([\s\S]*?string AreaId\)/);
  assert.match(taskbarGrouping, /var codexGroupIndex = 0/);
  assert.match(taskbarGrouping, /ProviderKind\.Codex => CodexAreaId\(\+\+codexGroupIndex\)/);
  assert.match(taskbarGrouping, /\$\"\{MiniAreaIds\.Codex\}\.\{groupIndex\}\"/);
  assert.match(barForm, /private static string TaskbarGroupAreaId\(TaskbarMiniCardGroup group\) => group\.AreaId;/);
  assert.match(barForm, /public static TaskbarMiniAreaContent ForGroup\(TaskbarMiniCardGroup group\) => new\([\s\S]*?TaskbarGroupAreaId\(group\)/);
});

test('Codex Mini display mode defaults to accounts and safely normalizes persisted values', () => {
  const modesStart = appSettings.indexOf('public static class CodexMiniDisplayModes');
  const modesEnd = appSettings.indexOf('public sealed class AppSettings', modesStart);
  assert.notEqual(modesStart, -1);
  assert.notEqual(modesEnd, -1);
  const modes = appSettings.slice(modesStart, modesEnd);

  assert.match(modes, /public const string Accounts = "accounts";/);
  assert.match(modes, /public const string Pool = "pool";/);
  assert.match(modes, /public static string Normalize\(string\? value\)/);
  assert.match(modes, /Pool => Pool/);
  assert.match(modes, /_ => Accounts/);
  assert.match(appSettings, /CodexMiniDisplayMode \{ get; set; \} = CodexMiniDisplayModes\.Accounts;/);
  assert.match(appSettings, /CodexMiniDisplayMode = CodexMiniDisplayModes\.Normalize\(CodexMiniDisplayMode\);/);
});

test('settings persist Codex Mini display mode as an editable choice', () => {
  assert.match(settingsForm, /_codexDisplayMode\.Tag = "settings\.codex\.display-mode";/);
  assert.match(
    settingsForm,
    /_codexDisplayMode\.Items\.Add\([\s\S]*?CodexMiniDisplayModes\.Accounts[\s\S]*?_codexDisplayMode\.Items\.Add\([\s\S]*?CodexMiniDisplayModes\.Pool/);

  const buildStart = settingsForm.indexOf('    private AppSettings BuildSettings()');
  const editableStart = settingsForm.indexOf('    private static bool EditableEquals(', buildStart);
  const copyStart = settingsForm.indexOf('    private static AppSettings Copy(', editableStart);
  const copyEnd = settingsForm.indexOf('    private string PluginDescription(', copyStart);
  assert.notEqual(buildStart, -1);
  assert.notEqual(editableStart, -1);
  assert.notEqual(copyStart, -1);
  assert.notEqual(copyEnd, -1);

  const buildSettings = settingsForm.slice(buildStart, editableStart);
  const editableEquals = settingsForm.slice(editableStart, copyStart);
  const copySettings = settingsForm.slice(copyStart, copyEnd);
  assert.match(buildSettings, /CodexMiniDisplayMode = \(_codexDisplayMode\.SelectedItem as CodexMiniDisplayModeChoice\)\?\.Mode/);
  assert.match(editableEquals, /string\.Equals\(left\.CodexMiniDisplayMode, right\.CodexMiniDisplayMode, StringComparison\.Ordinal\)/);
  assert.match(copySettings, /CodexMiniDisplayMode = CodexMiniDisplayModes\.Normalize\(settings\.CodexMiniDisplayMode\)/);
});

test('Codex pool grouping replaces only the Codex area and keeps the Mini height contract', () => {
  assert.match(modelsSource, /public const int CodexPoolCardWidth = 184;/);
  assert.match(taskbarGrouping, /string codexMiniDisplayMode = CodexMiniDisplayModes\.Accounts/);
  assert.match(taskbarGrouping, /CodexMiniDisplayModes\.Normalize\(codexMiniDisplayMode\) == CodexMiniDisplayModes\.Pool[\s\S]*?CreatePoolGroups\(cards\)/);
  assert.match(taskbarGrouping, /public bool IsCodexPool \{ get; init; \}/);
  assert.match(taskbarGrouping, /new TaskbarMiniCardGroup\(poolCards, MiniAreaIds\.Codex\)[\s\S]*?IsCodexPool = true/);
  assert.match(taskbarGrouping, /CodexServiceAreaId = "zgstokenbar\.provider\.codex-service"/);
  assert.match(taskbarGrouping, /ProviderKind\.Codex => CodexServiceAreaId/);

  const drawStart = barForm.indexOf('    private void DrawTaskbarCards(');
  const drawEnd = barForm.indexOf('    private void DrawTaskbarCard(', drawStart);
  const layoutStart = barForm.indexOf('    private void ApplySnapshotLayout()');
  const layoutEnd = barForm.indexOf('    private void UpdateWindowRegion()', layoutStart);
  assert.notEqual(drawStart, -1);
  assert.notEqual(drawEnd, -1);
  assert.notEqual(layoutStart, -1);
  assert.notEqual(layoutEnd, -1);

  const drawCards = barForm.slice(drawStart, drawEnd);
  const applyLayout = barForm.slice(layoutStart, layoutEnd);
  const poolRowStart = barForm.indexOf('    private void DrawTaskbarCodexPoolRow(');
  const poolRowEnd = barForm.indexOf('    private static CodexPoolTargetRow[]', poolRowStart);
  const poolRowDrawing = barForm.slice(poolRowStart, poolRowEnd);
  assert.notEqual(poolRowStart, -1);
  assert.notEqual(poolRowEnd, -1);
  assert.match(drawCards, /group\.IsCodexPool[\s\S]*?DrawTaskbarCodexPool\(graphics, bounds, group\.Cards\)/);
  assert.match(barForm, /group\.IsCodexPool[\s\S]*?TaskbarMiniLayoutMath\.CodexPoolCardWidth/);
  assert.match(poolRowDrawing, /CodexPoolPresentation\.CapacitySummary\(targetRow\.Row\)/);
  assert.doesNotMatch(poolRowDrawing, /FormatResetShort|NextResetAt|ResetBounds/);
  assert.match(poolRowDrawing, /DrawTaskbarCodexPoolCapacitySummary\(graphics, targetRow, aggregateColor\)/);
  assert.match(poolRowDrawing, /var capacityText = summary\[separatorIndex\.\.\]/);
  assert.match(poolRowDrawing, /new SolidBrush\(Color\.FromArgb\(148, 163, 184\)\)/);
  assert.match(codexPoolPresentation, /equivalents \* 100:0/);
  assert.match(codexPoolPresentation, /row\.AvailableAccountCount \* 100/);
  assert.match(barForm, /var singleRow = rows\.Count == 1;/);
  assert.match(barForm, /var rowHeight = singleRow \? 32f : 15f;/);
  assert.match(barForm, /var railHeight = targetRow\.Bounds\.Height > 20 \? 8f : 5f;/);
  assert.match(testProgram, /taskbar-mini-four-codex-pool\{scenario\.Suffix\}-zh-CN-\{dpi\}dpi\.png/);
  assert.match(barForm, /group\.IsCodexPool \? "Codex" : group\.Cards\[0\]\.DisplayLabel/);
  assert.match(applyLayout, /CodexPoolCardProjection\.Create\([\s\S]*?_codexAccounts,[\s\S]*?_snapshot\.CodexAccounts/);
  assert.match(applyLayout, /TaskbarMiniGrouping\.Create\(layoutCards, _codexMiniDisplayMode\)/);
  assert.match(barForm, /SetCodexAccounts\([\s\S]*?_codexMiniDisplayMode == CodexMiniDisplayModes\.Pool\)[\s\S]*?ApplySnapshotLayout\(\)/);
  assert.match(codexPoolProjection, /GroupBy\(account => account\.AccountId, StringComparer\.Ordinal\)/);
  assert.match(codexPoolProjection, /quotaByAccount\.TryGetValue\(account\.AccountId, out var quota\)/);
  assert.match(codexPoolProjection, /TakeExactSourceCard\([\s\S]*?TakeFallbackSourceCard\(/);
  assert.match(codexPoolProjection, /AccountHint, hint[\s\S]*?CardPlanMatches\(card, targetPlan\)/);
  assert.doesNotMatch(codexPoolProjection, /projected\.AddRange\(unusedCards/);
  assert.match(codexPoolProjection, /OrderByDescending\(group => group\.Count\(\)\)/);
  assert.match(codexPoolProjection, /projectedKey = !string\.IsNullOrWhiteSpace\(quota\?\.CardKey\)/);
  assert.match(codexPoolProjection, /if \(!HasQuotaData\(windows\)\) windows = \[\];/);
  assert.match(codexPoolProjection, /label is "pro" or "plus" or "free"/);
  assert.match(codexPoolPresentation, /futureResets[\s\S]*?reset > now[\s\S]*?futureResets\.Min\(\)/);
  assert.match(codexPoolPresentation, /visibleRows[\s\S]*?HasWindowData\(segment\.Window\)/);
  assert.match(modelsSource, /record CodexAccountQuota\([\s\S]*?public string\? CardKey \{ get; init; \}/);
  assert.match(applyLayout, /ClientSize = new Size\([\s\S]*?TaskbarMiniLayoutMath\.Height\)/);
});

test('quota pace rows use concise labels, readable type, and distinct icons', () => {
  assert.match(popoverForm, /LogicalBodyWidth = 240/);
  assert.match(popoverForm, /LogicalBodyHeight = 144/);
  assert.match(popoverForm, /new\("Segoe UI Semibold", 10f, FontStyle\.Regular, GraphicsUnit\.Pixel\)/);
  assert.match(popoverForm, /DrawTrendPaceIcon/);
  assert.match(popoverForm, /DrawCyclePaceIcon/);
  assert.match(popoverForm, /DrawBudgetMarker/);
  assert.match(popoverForm, /QuotaDisplayFormatting\.BudgetMarkerRemaining\(/);
  assert.match(popoverForm, /var recentTooFast = content\.Pace\?\.Recent/);
  assert.match(popoverForm, /remaining!\.Value,[\s\S]*recentTooFast\)/);
  assert.match(popoverForm, /troughBrush = new SolidBrush\(Color\.FromArgb\(30, 41, 59\)\)/);
  assert.match(popoverForm, /trackBrush = new SolidBrush\(Color\.FromArgb\(71, 85, 105\)\)/);
  assert.match(popoverForm, /Color\.FromArgb\(253, 230, 138\)/);
  assert.match(popoverForm, /coreBrush = new SolidBrush\(Color\.FromArgb\(30, 41, 59\)\)/);
  assert.match(popoverForm, /body\.Right - 104, y \+ 94, 92, 16/);
  assert.match(popoverForm, /detailValueBrush = new SolidBrush\(Color\.FromArgb\(203, 213, 225\)\)/);
  assert.match(nativeText, /"1h"/);
  assert.match(nativeText, /15m初步/);
  assert.match(nativeText, /Cycle OK/);
  assert.match(nativeText, /今晚目标/);
  assert.match(nativeText, /Midnight goal/);
  assert.match(nativeText, /compact\.Replace\(" ", string\.Empty, StringComparison\.Ordinal\)/);
});

test('taskbar Mini uses unmodified embedded provider logos', () => {
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\claude-icon-rounded\.png"/);
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\openai-official-ios-icon\.png"/);
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\deepseek-whale-icon\.png"/);
  assert.match(barForm, /private readonly Image _claudeLogo/);
  assert.match(barForm, /private readonly Image _openAiLogo/);
  assert.match(barForm, /private readonly Image _deepSeekLogo/);
  assert.match(barForm, /DrawProviderLogo\(graphics, logoBounds, card\)/);
  assert.doesNotMatch(barForm, /private static void DrawClaudeMark/);
  assert.doesNotMatch(barForm, /private static void DrawCodexMark/);
});

test('taskbar Mini reset affordance comes from the Fluent icon asset', () => {
  assert.match(nativeProject, /EmbeddedResource Include="Assets\\fluent-clock-20-regular\.png"/);
  assert.match(barForm, /private readonly Image _resetClockIcon/);
  assert.match(barForm, /DrawResetClockIcon/);
});

test('taskbar Mini optically balances reset icons and marks unread Radar data', () => {
  assert.match(barForm, /MiniResetGlyphSize = 9f/);
  assert.match(barForm, /MiniResetClockSize = 11f/);
  assert.match(barForm, /CenteredSquare\(bounds, MiniResetClockSize\)/);
  assert.match(barForm, /_radarState\.HasUnreadFor\(RadarSurfaceIds\.Codex\)/);
  assert.match(barForm, /_radarState\.HasUnreadFor\(RadarSurfaceIds\.DeepSeek\)/);
  assert.match(barForm, /DrawRadarUnreadDot\(graphics, logoBounds\)/);
  assert.match(barForm, /public string\? VisibleRadarSurfaceId/);
  assert.match(applicationContext, /RequestRadarPreview\(request\.Provider, request\.SurfaceId\)/);
  assert.match(applicationContext, /RadarAlertTracker\.RecordViewed\(_radarState, _radarViewState\.Snapshot, surfaceId\)/);
  assert.match(applicationContext, /_bar\.VisibleRadarSurfaceId is \{ \} visibleSurfaceId/);
  assert.match(radarAlertTracker, /ViewedEventIdsBySurface/);
  assert.match(radarAlertTracker, /public const string Codex = "zgstokenbar\.radar\.codex"/);
  assert.match(radarAlertTracker, /public const string DeepSeek = "zgstokenbar\.radar\.deepseek"/);
});

test('collapsed quota and balance providers share a compact semantic summary without another fetch path', () => {
  assert.match(barForm, /var primaryCard = PrimaryTaskbarCard\(group\)/);
  assert.match(barForm, /DrawCollapsedAiGatewayCard\(graphics, bounds, primaryCard\)/);
  assert.match(barForm, /DrawCollapsedQuotaCard\(graphics, bounds, primaryCard\)/);
  assert.match(barForm, /DrawCollapsedProviderSummary\(/);
  assert.match(barForm, /DrawTaskbarProviderLogoAt\(graphics, logoBounds, card, showOrdinal: false\)/);
  assert.match(barForm, /internal static \(string Value, Color Color\) CollapsedQuotaSummary\(QuotaCard card\)/);
  assert.match(barForm, /TaskbarMiniGrouping\.CodexRowWindows\(card\)/);
  assert.match(barForm, /CompactRemaining\(remaining\)/);
  assert.match(barForm, /AiGatewayBalanceFormatting\.CompactAmount\(balance\?\.TotalBalance\)/);
  assert.match(barForm, /AiGatewayCompactStatusColor\(balance\.Status\)/);
  assert.match(aiGatewayBalance, /public static string CompactAmount\(decimal\? amount\)/);
  assert.doesNotMatch(barForm, /FetchAiGateway|HttpClient/);
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
  assert.match(barForm, /_taskbarCodexAccountBounds\.Add\(\([\s\S]*?TaskbarAccountOrdinalRowBounds\(bounds, index, group\.Cards\.Count\)/);
  assert.match(barForm, /TaskbarCodexAccountTargetAt/);
  assert.match(barForm, /ShowCodexAccountsPopover\(codexAccountTarget\)/);
  assert.doesNotMatch(barForm, /CodexAccountsTooltip/);
  assert.doesNotMatch(barForm, /ToolTip|TooltipFor|_lastToolTip/);
  assert.match(codexAccountsPopoverForm, /class CodexAccountsPopoverForm/);
  assert.match(codexAccountsPopoverForm, /TaskbarMiniPopoverMath\.Place/);
  assert.match(codexAccountsPopoverForm, /_backgroundTheme\.Popover/);
  assert.match(codexAccountsPopoverForm, /_text\.CodexAccountsHeading/);
  assert.doesNotMatch(codexAccountsPopoverForm, /CodexAccountMapping|LogicalMappingHeight/);
  assert.match(nativeText, /public string CodexAccountsHeading/);
  assert.doesNotMatch(nativeText, /CodexAccountsTooltip|CodexAccountMapping/);
  assert.match(codexAccountsPopoverForm, /QuotaDisplayFormatting\.FormatWindowShort/);
  assert.match(popoverForm, /DrawAccountSubtitle/);
  assert.match(popoverForm, /AccountSubtitle\(card, text\)/);
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
  assert.match(hintPopoverForm, /_backgroundTheme\.Popover/);
  assert.match(barForm, /ShowHintPopover\(hintTarget\)/);
  assert.match(barForm, /OpenSettingsHint/);
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
  assert.match(taskbarGrouping, /if \(card\.IsService\)/);
  assert.match(barForm, /if \(target\.Card\.IsService\)/);
  const serviceMetricStart = barForm.indexOf('    private void DrawTaskbarServiceMetric(');
  const serviceMetricEnd = barForm.indexOf('    private void DrawTaskbarLockIcon(', serviceMetricStart);
  const serviceMetric = barForm.slice(serviceMetricStart, serviceMetricEnd);
  assert.notEqual(serviceMetricStart, -1);
  assert.notEqual(serviceMetricEnd, -1);
  assert.match(serviceMetric, /var quotaColor = Sub2ApiQuotaCompactStatusColor\(quota!\.Status\);[\s\S]*?DrawTaskbarProgressRail\([\s\S]*?quotaWindow\.RemainingPercent,[\s\S]*?quotaColor,[\s\S]*?null\);/);
  assert.match(serviceMetric, /DrawTaskbarServiceCompactMetric\([\s\S]*?target\.Card\.DisplayLabel,[\s\S]*?_text\.Sub2ApiQuotaCompact\(quota!\),[\s\S]*?quotaColor\);/);
  assert.match(serviceMetric, /var accountAvailabilityRemaining = Sub2ApiAccountAvailabilityFormatting\.MeanRemainingPercent\(accountAvailability\);/);
  assert.match(serviceMetric, /var availabilityColor = Sub2ApiQuotaCompactStatusColor\(accountAvailability!\.Status\);[\s\S]*?DrawTaskbarProgressRail\([\s\S]*?accountAvailabilityRemaining,[\s\S]*?availabilityColor,[\s\S]*?null\);/);
  assert.match(serviceMetric, /DrawTaskbarServiceCompactMetric\([\s\S]*?target\.Card\.DisplayLabel,[\s\S]*?_text\.Sub2ApiAccountAvailabilityCompact\(accountAvailability!\),[\s\S]*?availabilityColor\);/);
  assert.match(serviceMetric, /private void DrawTaskbarServiceCompactMetric\([\s\S]*?if \(bounds\.Width < 84f\)[\s\S]*?StringAlignment\.Near[\s\S]*?StringAlignment\.Far/);
  assert.doesNotMatch(serviceMetric, /bounds\.Y \+ 16/);
  assert.match(serviceMetric, /target\.Card\.DisplayLabel,[\s\S]*?new RectangleF\(bounds\.X \+ 7, bounds\.Y, bounds\.Width - 12, bounds\.Height\),[\s\S]*?StringAlignment\.Center/);
  assert.doesNotMatch(serviceMetric, /bounds\.Width - 53/);
  assert.match(barForm, /_text\.ApiServiceConfiguredShort/);
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
  assert.match(barForm, /TaskbarControlWidth = TaskbarMiniLayoutMath\.ControlsWidth;/);
  assert.match(barForm, /TaskbarControlHeight = 18f/);
  const start = barForm.indexOf('    private void DrawTaskbarCards(');
  const end = barForm.indexOf('    private void DrawTaskbarCard(', start);
  const body = barForm.slice(start, end);
  assert.notEqual(start, -1);
  assert.notEqual(end, -1);
  assert.match(body, /DrawTaskbarModuleShell\(graphics, target\)/);
  assert.match(body, /new RectangleF\(x, 4, areaWidth, 36\)/);
  assert.match(body, /DrawSystemUsageCard\(graphics, bounds, layout\.Collapsed\)/);
  assert.match(body, /DrawTaskbarControlGroup\(graphics\)/);
  assert.match(body, /_refreshBounds = new RectangleF\(controlsX, 4, TaskbarControlWidth, TaskbarControlHeight\)/);
  assert.doesNotMatch(body, /_modeBounds|HoverTarget\.Mode/);
  assert.match(body, /_taskbarAreaBounds\.Clear\(\)/);
  assert.match(body, /_settingsBounds = new RectangleF\([\s\S]*?controlsX,[\s\S]*?_refreshBounds\.Bottom,[\s\S]*?TaskbarControlWidth,[\s\S]*?TaskbarControlHeight\)/);
  assert.match(body, /var areaWidth = TaskbarAreaWidth\(area\)/);
  assert.match(body, /DrawSettingsIcon\([\s\S]*?taskbarCompact: true/);
  assert.match(barForm, /private void DrawTaskbarControlGroup\(Graphics graphics, bool drawShell = true\)/);
  assert.match(barForm, /DrawTaskbarControlSegment\(graphics, _settingsBounds, HoverTarget\.Settings\)/);
  assert.doesNotMatch(barForm, /DrawTaskbarControlSegment\(graphics, .*Collapse/);
  assert.doesNotMatch(barForm, /MiniCollapseTarget/);
  assert.match(body, /DrawTaskbarCollapseHandle\(graphics, target\)/);
  assert.match(body, /DrawTaskbarReorderGrip\(graphics, target\)/);
  assert.match(body, /var handleBounds = TaskbarCollapseHandleBounds\(areaBounds\)/);
  assert.match(body, /var bounds = TaskbarAreaContentBounds\(areaBounds\)/);
  assert.match(body, /MiniAreaIds\.SystemMetrics/);
  assert.match(barForm, /TaskbarCollapseTargetAt\(logical\)/);
  assert.match(barForm, /TaskbarReorderTargetAt\(logical\)/);
  assert.match(barForm, /BeginMiniAreaReorder\(reorderTarget, e\.Location\)/);
  assert.match(barForm, /ContinueMiniAreaReorder\(e\)/);
  assert.match(barForm, /MiniAreaOrderChanged/);
  const mouseDown = barForm.slice(barForm.indexOf('    protected override void OnMouseDown('), barForm.indexOf('    protected override void OnMouseUp('));
  assert.ok(mouseDown.indexOf('TaskbarReorderTargetAt(logical)') < mouseDown.indexOf('_taskbarDragging = true;'));
  assert.match(barForm, /ApplyMiniAreaLayout/);
  assert.match(barForm, /TaskbarMiniAreaContent\.ForSystem\(_text\.SystemUsageTitle\)/);
  assert.match(barForm, /SelectVisibleTaskbarAreas\(_taskbarContentAreas\)/);
  assert.match(barForm, /TaskbarMiniLayoutMath\.ModuleContentWidth/);
  assert.match(barForm, /TaskbarResizeTargetAt/);
  assert.match(barForm, /PreserveMiniProviderAreaAnchor/);
  assert.match(barForm, /private static RectangleF TaskbarCollapseHandleBounds\(RectangleF areaBounds\) => new\([\s\S]*?areaBounds\.Right - TaskbarMiniLayoutMath\.ProviderCollapseHandleWidth/);
  assert.match(barForm, /private static RectangleF TaskbarReorderHandleBounds\(RectangleF areaBounds\) => new\([\s\S]*?areaBounds\.Top \+ areaBounds\.Height \/ 2/);
  assert.match(barForm, /private static RectangleF TaskbarAreaContentBounds\(RectangleF areaBounds\) =>[\s\S]*?areaBounds\.Right - TaskbarMiniLayoutMath\.ProviderCollapseHandleWidth/);
  assert.match(barForm, /var separatorX = bounds\.Left \+ 1\.2f/);
  assert.match(barForm, /var x = bounds\.X \+ bounds\.Width \/ 2;/);
  assert.doesNotMatch(barForm, /var x = bounds\.X \+ 3\.2f/);
  assert.match(nativeText, /拖动箭头左侧细线调宽/);
  assert.match(nativeText, /拖动排序；其他位置可拖动整条栏/);
  assert.match(barForm, /graphics\.DrawLine\(separator, groupBounds\.Left \+ 5, _settingsBounds\.Top, groupBounds\.Right - 5, _settingsBounds\.Top\)/);
  assert.match(barForm, /var value = target\.Card\.Balance is \{ \} balance/);
  assert.match(barForm, /StringAlignment\.Center/);
});

test('open Radar reset windows keep a persistent honest Mini countdown', () => {
  assert.match(models, /public const string RadarReset = "zgstokenbar\.radar\.reset";/);
  assert.ok(
    radarResetTiming.indexOf('window.TargetAt is { } exactTarget')
      < radarResetTiming.indexOf('window.OpenedAt is not { } openedAt'),
    'an exact upstream target must win before any date inference',
  );
  assert.match(radarResetTiming, /BeijingDate\(openedAt\)/);
  assert.match(radarResetTiming, /AddDays\(openedDate, 1\)/);
  assert.match(radarResetTiming, /catch \(ArgumentOutOfRangeException\)/);
  assert.match(radarResetTiming, /TargetWeekday\(window\.Scope\)/);
  assert.doesNotMatch(radarResetTiming, /DateTimeOffset\.UtcNow|DateTime\.Now/);
  assert.match(nativeText, /RadarResetMiniTitle/);
  assert.match(nativeText, /RadarResetMiniValue/);
  assert.match(nativeText, /\$"~\{days\}d"/);
  assert.match(nativeText, /推测重置/);
  assert.match(nativeText, /时间未定/);
  assert.match(barForm, /ShouldShowRadarResetArea\(\)/);
  assert.match(barForm, /TaskbarMiniAreaContent\.ForRadarReset\(_text\.RadarResetMiniAreaTitle\)/);
  assert.match(barForm, /DrawRadarResetCard\(graphics, bounds, layout\.Collapsed\)/);
  assert.match(models, /public const int RadarResetContentWidth = 92;/);
  assert.match(barForm, /graphics\.DrawImage\(ProviderLogo\(ProviderKind\.Codex\), iconBounds\)/);
  assert.match(barForm, /_taskbarRadarBounds\.Add\(new MiniRadarTarget\(\s*bounds,\s*MiniAreaIds\.RadarReset,[\s\S]*?RadarSurfaceIds\.Codex\)\)/);
  assert.match(barForm, /visibleIds\.Add\(MiniAreaIds\.RadarReset\)/);
  assert.match(barForm, /RadarResetTiming\.RefreshIntervalMilliseconds\(CurrentRadarResetWindow\(\), _utcNow\(\)\)/);
  assert.match(barForm, /_radarResetTimer\.Dispose\(\)/);
  assert.match(radarPopoverForm, /RadarResetTiming\.RefreshIntervalMilliseconds\(window, now\)/);
  assert.doesNotMatch(barForm, /data-window-closes-at|opened_at|scheduled_at/);
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
  assert.match(barForm, /private float HoverProgress\(HoverTarget target\) => _animationsEnabled/);
  assert.match(barForm, /_quotaPopover\.ShowFor\([\s\S]*?_animationsEnabled\);/);
  assert.match(popoverForm, /animateEntrance/);
  assert.match(popoverForm, /TotalMilliseconds \/ 130/);
  assert.match(popoverForm, /ExitDurationMs = 90/);
  assert.match(radarPopoverForm, /EntranceDurationMs = 130/);
  assert.match(radarPopoverForm, /ExitDurationMs = 90/);
  assert.match(radarPopoverForm, /RadarPopoverRenderer\.CreateWindowRegion/);
  assert.match(radarRenderer, /internal static Region CreateWindowRegion/);
  assert.match(radarRenderer, /Scale\(layout\.Dpi, 10\)/);
  assert.match(radarRenderer, /StrongestColor = Color\.FromArgb\(246, 196, 83\)/);
  assert.match(radarRenderer, /RecommendationColors/);
  assert.match(radarRenderer, /var modelColor = strongest[\s\S]*?\? StrongestColor[\s\S]*?: recommendationColor/);
  assert.match(radarRenderer, /row\.ModelText, modelFont, modelColor/);
  assert.match(settingsForm, /_text\.Animations/);
  assert.match(nativeText, /Enable subtle transitions and refresh animation/);
});

test('native animated refresh control uses a centered geometric path', () => {
  const refreshStart = barForm.indexOf('    private void DrawRefreshIcon(');
  const refreshEnd = barForm.indexOf('    private void DrawSettingsIcon(', refreshStart);
  const refreshBody = barForm.slice(refreshStart, refreshEnd);
  assert.notEqual(refreshStart, -1);
  assert.notEqual(refreshEnd, -1);
  assert.match(refreshBody, /float iconSize = ControlIconSize/);
  assert.match(refreshBody, /CenteredSquare\(bounds, iconSize\)/);
  assert.match(refreshBody, /graphics\.TranslateTransform\(center\.X, center\.Y\)/);
  assert.match(refreshBody, /graphics\.RotateTransform\(rotation\)/);
  assert.match(refreshBody, /graphics\.DrawArc\(/);

  assert.doesNotMatch(barForm, /DrawModeIcon|DrawExpandIcon|HoverTarget\.Mode/);
  assert.doesNotMatch(barForm, /RefreshIconGlyph/);
  assert.doesNotMatch(barForm, /ExpandIconGlyph/);
});

test('native static controls keep official Windows Fluent glyphs', () => {
  assert.match(barForm, /SettingsIconGlyph = "\\uE713"/);
  assert.doesNotMatch(barForm, /DockBottomIconGlyph|DockBottom/);
  assert.match(barForm, /SystemIconVerticalOffset = 1f/);
  assert.match(barForm, /iconBounds\.Offset\(0, SystemIconVerticalOffset\)/);
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
  assert.match(settingsForm, /AddNavigation\("radar"/);
  assert.match(settingsForm, /AddNavigation\("advanced"/);
  assert.match(settingsForm, /AddNavigation\("about"/);
  assert.doesNotMatch(settingsForm, /SettingsCard|BadgeLabel|NATIVE BAR/);
  assert.match(settingsForm, /ConstrainOuterSize\(Size desiredOuterSize, Rectangle workingArea, int safeMarginPixels\)/);
  assert.doesNotMatch(settingsForm, /MinimumSize\s*=/);
  assert.match(settingsForm, /_save\.Enabled = dirty/);
  assert.match(settingsForm, /_radarAlertsBeforeDisable/);
  assert.match(settingsForm, /_radar\.Checked && _radarAlerts\.Checked/);
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
  assert.match(settingsForm, /Color\.FromArgb\(24, 24, 28\)/);
  assert.match(settingsForm, /Color\.FromArgb\(18, 18, 20\)/);
  assert.match(settingsForm, /Color\.FromArgb\(44, 44, 49\)/);
  assert.match(settingsForm, /Color\.FromArgb\(242, 243, 245\)/);
  assert.match(settingsForm, /Color\.FromArgb\(160, 164, 173\)/);
  assert.match(settingsForm, /Color\.FromArgb\(76, 141, 255\)/);
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
    '    private CodexEconomyStatus? InspectRecommendedCodexEconomyProfile()',
    openSettingsStart);
  assert.notEqual(openSettingsStart, -1);
  assert.notEqual(openSettingsEnd, -1);
  const openSettings = applicationContext.slice(openSettingsStart, openSettingsEnd);
  assert.match(openSettings, /dialog\.Show\(\)/);
  assert.doesNotMatch(openSettings, /dialog\.ShowDialog/);
  assert.match(settingsForm, /e\.Graphics\.Clear\(_theme\.Sidebar\)/);
  assert.match(settingsForm, /e\.Graphics\.Clear\(_theme\.Content\)/);
  assert.match(settingsForm, /BackColor = theme\.Content/);
  assert.doesNotMatch(settingsForm, /_hover \? Theme\.Hover/);
  assert.doesNotMatch(nativeProject, /PackageReference/);
});
