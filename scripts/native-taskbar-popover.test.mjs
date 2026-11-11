import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { readPartialType } from './source-files.mjs';

const barForm = readPartialType('src/ZGSTokenBar.App/BarForm.cs');
const applicationContext = readPartialType('src/ZGSTokenBar.App/QuotaApplicationContext.cs');
const codexQuotaService = fs.readFileSync('src/ZGSTokenBar.Core/CodexQuotaService.cs', 'utf8');
const models = fs.readFileSync('src/ZGSTokenBar.Core/Models.cs', 'utf8');
const productReadme = fs.readFileSync('README.md', 'utf8');
const popoverPath = 'src/ZGSTokenBar.App/QuotaPopoverForm.cs';
const popover = fs.existsSync(popoverPath) ? fs.readFileSync(popoverPath, 'utf8') : '';
const accountsPopover = fs.readFileSync('src/ZGSTokenBar.App/CodexAccountsPopoverForm.cs', 'utf8');
const hintPopover = fs.readFileSync('src/ZGSTokenBar.App/TaskbarHintPopoverForm.cs', 'utf8');
const nativeText = fs.readFileSync('src/ZGSTokenBar.App/NativeText.cs', 'utf8');
const tokenUsageReader = fs.readFileSync('src/ZGSTokenBar.Core/CodexTokenUsageReader.cs', 'utf8');
const tokenUsageLedger = fs.readFileSync('src/ZGSTokenBar.Core/CodexTokenUsageLedger.cs', 'utf8');
const usageService = fs.readFileSync('src/ZGSTokenBar.Core/AiGatewayUsageService.cs', 'utf8');
const boundedHttpBodyReader = fs.readFileSync('src/ZGSTokenBar.Core/BoundedHttpBodyReader.cs', 'utf8');
const popoverMath = fs.readFileSync('src/ZGSTokenBar.App/TaskbarPopoverMath.cs', 'utf8');
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

test('taskbar popovers share pure motion and tail geometry without a form base class', () => {
  assert.match(popoverMath, /OffsetFromAnchor/);
  assert.match(popoverMath, /EntranceEase/);
  assert.match(popoverMath, /ExitEase/);
  assert.match(popoverMath, /BodyBounds/);
  assert.match(popoverMath, /TailPoints/);
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
    /SetCodexAccounts\([\s\S]*?ApplySnapshotLayout\(\);[\s\S]*?CodexMiniDisplayModes\.Pool\)[\s\S]*?RefreshQuotaPopover\(\);/);
});

test('Codex account hover uses the shared dark popover chrome and ordinal anchor', () => {
  assert.match(barForm, /TaskbarAccountOrdinalRowBounds\(bounds, index, group\.Cards\.Count\)/);
  assert.match(barForm, /private void ShowCodexAccountsPopover/);
  assert.match(accountsPopover, /TaskbarMiniPopoverMath\.Place/);
  assert.match(accountsPopover, /UiTokens\.CardSurface/);
  assert.match(accountsPopover, /CodexAccountFormatting\.MaskEmail/);
  assert.match(accountsPopover, /QuotaText\(account\)/);
  assert.match(accountsPopover, /API key/);
  assert.match(hintPopover, /TaskbarMiniPopoverMath\.Place/);
  assert.match(hintPopover, /UiTokens\.CardSurface/);
  assert.match(barForm, /UpdateHintHover\(next\)/);
  assert.match(barForm, /MiniAreaLayoutChanged/);
  assert.match(barForm, /MiniAreaOrderChanged/);
  assert.match(barForm, /ToggleMiniAreaCollapsed/);
  assert.match(nativeText, /MiniCardCollapseHint/);
  assert.match(nativeText, /MiniCardReorderHint/);
  assert.match(applicationContext, /nextSettings\.CopyMiniAreaLayoutsFrom\(_settings\)/);
  assert.doesNotMatch(barForm, /new\(\) \{ InitialDelay = 350/);
});

test('Codex quota hover combines the 100 percent estimate with its recorded sample', () => {
  assert.match(popover, /DrawQuotaTokenUsage/);
  assert.match(popover, /QuotaTokenUsage is not null/);
  assert.match(popover, /content\.Card\.Provider == ProviderKind\.Codex && !content\.Card\.IsService/);
  const tokenUsage = boundedSlice(nativeText,
    'public (string Label, string Value, string Detail)? CodexQuotaTokenUsage',
    'internal static string FormatTokenCount');
  assert.match(tokenUsage, /HasCurrentObservation: true, CurrentObservedTokens/);
  assert.match(tokenUsage, /IsCurrentLocalFallback/);
  assert.match(tokenUsage, /本周期部分用量/);
  assert.match(tokenUsage, /this device only/);
  assert.match(tokenUsage, /CurrentCapacityTokens/);
  assert.match(tokenUsage, /MinimumCurrentSpanPercent/);
  assert.doesNotMatch(tokenUsage, /EstimateUsedTokens|AverageCapacityTokens|MaxCapacityTokens/);
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
  assert.match(availabilityRenderer, /account\.RemainingPercent is \{ \} remaining[\s\S]*?UiPalette\./);
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
