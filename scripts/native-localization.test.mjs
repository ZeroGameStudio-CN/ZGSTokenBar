import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { readPartialType } from './source-files.mjs';

const read = (path) => fs.readFileSync(path, 'utf8');
const settings = read('src/ZGSTokenBar.Core/AppSettings.cs');
const models = read('src/ZGSTokenBar.Core/Models.cs');
const text = read('src/ZGSTokenBar.App/NativeText.cs');
const settingsForm = read('src/ZGSTokenBar.App/SettingsForm.cs');
const bar = readPartialType('src/ZGSTokenBar.App/BarForm.cs');
const quota = read('src/ZGSTokenBar.App/QuotaPopoverForm.cs');
const systemUsage = read('src/ZGSTokenBar.App/SystemUsagePopoverForm.cs');
const application = readPartialType('src/ZGSTokenBar.App/QuotaApplicationContext.cs');

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
  assert.match(application, /_text = NativeText\.For\(_settings\.Locale\)/);
  assert.match(application, /var localeOnlyChange = IsLocaleOnlyChange/);
});

test('Codex spend history keeps its action, summaries, trend, and model copy bilingual', () => {
  assert.match(text, /CodexYesterdayMetricLabel => T\("昨日", "Yesterday"\)/);
  assert.match(text, /CodexLast7DaysMetricLabel => T\("7 日", "7d"\)/);
  assert.match(text, /CodexSpendTrendTitle => T\("30 日趋势", "30-day trend"\)/);
  assert.match(text, /CodexSpendModelsTitle => T\("模型构成 · 30 日", "Models · 30d"\)/);
  assert.match(text, /CodexUnknownModel => T\("未知模型", "Unknown model"\)/);
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
  assert.match(text, /public string UpdateAvailableTitle\(Version version\) => T\(/);
  assert.match(text, /public string UpdateAvailableBody => T\(/);
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

test('renderers do not reintroduce the previous hard-coded English UI copy', () => {
});
