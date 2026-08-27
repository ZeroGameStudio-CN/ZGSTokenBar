import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const read = (path) => fs.readFileSync(path, 'utf8');
const settings = read('src/ZGSTokenBar.Core/AppSettings.cs');
const models = read('src/ZGSTokenBar.Core/Models.cs');
const text = read('src/ZGSTokenBar.App/NativeText.cs');
const settingsForm = read('src/ZGSTokenBar.App/SettingsForm.cs');
const bar = read('src/ZGSTokenBar.App/BarForm.cs');
const quota = read('src/ZGSTokenBar.App/QuotaPopoverForm.cs');
const systemUsage = read('src/ZGSTokenBar.App/SystemUsagePopoverForm.cs');
const radar = read('src/ZGSTokenBar.App/RadarPopoverRenderer.cs');
const application = read('src/ZGSTokenBar.App/QuotaApplicationContext.cs');

test('native settings preserve migration behavior and normalize supported locales', () => {
  assert.match(settings, /public string Locale \{ get; set; \} = "zh-CN"/);
  assert.match(settings, /if \(!hasLocale\) loaded\.Locale = "en"/);
  assert.match(settings, /TryGetProperty\("locale"/);
  assert.match(settings, /"en"\s*:\s*"zh-CN"/);
});

test('native UI routes user-facing copy through the typed bilingual catalog', () => {
  assert.match(text, /private static readonly NativeText Chinese/);
  assert.match(text, /private static readonly NativeText English/);
  assert.match(settingsForm, /NativeText\.For\(settings\.Locale\)/);
  assert.match(bar, /NativeText\.For\(settings\.Locale\)/);
  assert.match(quota, /NativeText text/);
  assert.match(systemUsage, /NativeText text/);
  assert.match(radar, /NativeText text/);
  assert.match(application, /_text = NativeText\.For\(_settings\.Locale\)/);
  assert.match(application, /var localeOnlyChange = IsLocaleOnlyChange/);
  assert.match(application, /if \(!localeOnlyChange\)[\s\S]*?RefreshAsync\(\)[\s\S]*?RefreshRadarAsync\(\)/);
});

test('Native settings completion copy stays typed, bilingual, and setting-local', () => {
  assert.match(text, /public string SettingsSubtitle => T\(/);
  assert.match(text, /public string General => T\("常规", "General"\)/);
  assert.match(text, /public string Notifications => T\("通知", "Notifications"\)/);
  assert.match(text, /public string Advanced => T\("高级", "Advanced"\)/);
  assert.match(text, /public string About => T\("关于", "About"\)/);
  assert.match(text, /public string UnsavedChanges => T\(/);
  assert.match(text, /public string DiscardChangesMessage => T\(/);
  assert.match(text, /public string VersionUnknown => T\(/);
  assert.match(settingsForm, /_text\.RadarTestNotificationHint/);
  assert.match(settingsForm, /_text\.RadarNetworkHint/);
  assert.match(settingsForm, /_text\.LocalFirstPrivacy/);
});

test('Native settings version comes only from assembly metadata', () => {
  const start = settingsForm.indexOf('    internal static string? ReadSemanticVersion()');
  const end = settingsForm.indexOf('    private static string? FormatNumericVersion', start);
  const body = settingsForm.slice(start, end);
  assert.notEqual(start, -1);
  assert.notEqual(end, -1);
  assert.match(body, /typeof\(SettingsForm\)\.Assembly/);
  assert.match(body, /AssemblyInformationalVersionAttribute/);
  assert.match(body, /Split\('\+', 2\)/);
  assert.match(body, /AssemblyFileVersionAttribute/);
  assert.match(body, /assembly\.GetName\(\)\.Version/);
  assert.doesNotMatch(body, /FileVersionInfo|Assembly\.Location|AppContext\.BaseDirectory|Application\.ExecutablePath/);
});

test('health, Radar errors, local scenarios, and alerts cross the UI boundary as semantic codes', () => {
  assert.match(models, /enum ProviderHealthCode/);
  assert.match(models, /ProviderHealthCode Code/);
  assert.match(text, /health\.Code/);
  assert.doesNotMatch(bar, /health\.Detail/);
  assert.match(text, /RadarDailyScenarioTitle/);
  assert.match(text, /RadarPlanningScenarioTitle/);
  assert.match(text, /RadarExecutionScenarioTitle/);
  assert.match(text, /RadarBackgroundScenarioTitle/);
  assert.doesNotMatch(text, /RadarRecommendationTitle|RadarRecommendationDelta|RadarValueTitle/);
  assert.doesNotMatch(text, /RadarFactKind/);
  assert.match(text, /RadarErrorCode/);
  assert.match(text, /RadarAlertChangeKind/);
  assert.doesNotMatch(application, /decision\.Reason/);
});

test('renderers do not reintroduce the previous hard-coded English UI copy', () => {
  const renderers = [settingsForm, bar, quota, systemUsage, radar, application].join('\n');
  for (const phrase of [
    'Live quota bar',
    'Automatic refresh',
    'Refresh now',
    'Quota data is waiting.',
    'Loading Codex Radar',
    'Primary result changed',
    'Settings not saved',
  ]) {
    assert.equal(renderers.includes(`"${phrase}`), false, phrase);
  }
});
