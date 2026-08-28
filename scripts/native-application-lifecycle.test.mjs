import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const program = fs.readFileSync('src/ZGSTokenBar.App/Program.cs', 'utf8');
const application = fs.readFileSync('src/ZGSTokenBar.App/QuotaApplicationContext.cs', 'utf8');
const settings = fs.readFileSync('src/ZGSTokenBar.App/SettingsForm.cs', 'utf8');
const startup = fs.readFileSync('src/ZGSTokenBar.App/StartupManager.cs', 'utf8');
const watchdog = fs.readFileSync('src/ZGSTokenBar.App/WatchdogManager.cs', 'utf8');
const update = fs.readFileSync('src/ZGSTokenBar.App/ReleaseUpdateChecker.cs', 'utf8');
const quit = application.match(/private async void Quit\(\)([\s\S]*?)protected override void Dispose/)?.[1] ?? '';
const dispose = application.match(/protected override void Dispose\(bool disposing\)([\s\S]*?)private static QuotaSnapshot/)?.[1] ?? '';

test('application context has one owner and menu-backed controls survive until the message loop exits', () => {
  assert.match(program, /var context = new QuotaApplicationContext/);
  assert.doesNotMatch(program, /using var context = new QuotaApplicationContext/);
  assert.match(quit, /_tray\.Visible = false;[\s\S]*?ExitThread\(\);/);
  assert.doesNotMatch(quit, /_tray\.Dispose\(\)|_bar\.Dispose\(\)/);
  assert.match(dispose, /_tray\.Dispose\(\);[\s\S]*?_bar\.Dispose\(\);/);
});

test('settings dropdown stays alive until WinForms finishes closing it', () => {
  assert.match(settings, /private ContextMenuStrip\? _menu;/);
  assert.match(settings, /_menu = menu;[\s\S]*?menu\.Show/);
  assert.doesNotMatch(settings, /menu\.Closed \+= [^\r\n]*menu\.Dispose/);
  assert.match(settings, /protected override void Dispose\(bool disposing\)[\s\S]*?_menu\?\.Dispose\(\)/);
});

test('single-instance settings activation is queued until the Bar handle is ready', () => {
  assert.ok(
    program.indexOf('new EventWaitHandle(') < program.indexOf('new Mutex('),
    'the named activation event must exist before another instance can lose the mutex');
  assert.doesNotMatch(program, /EventWaitHandle\.OpenExisting/);
  assert.match(program, /if \(!firstInstance\)[\s\S]*?activationEvent\.Set\(\);[\s\S]*?return;/);
  assert.ok(
    application.indexOf('_bar.Show();') < application.indexOf('ThreadPool.RegisterWaitForSingleObject('),
    'the Bar handle must exist before activation callbacks can marshal to it');
  assert.ok(
    application.indexOf('_bar.Show();') < application.indexOf('_apiServer.Start();'),
    'the Bar handle must exist before the desktop API accepts UI requests');
  assert.ok(
    application.indexOf('_bar.Show();') < application.indexOf('_pluginEventTask = WatchPluginEventsAsync();'),
    'the Bar handle must exist before plugin events can marshal to it');
});

test('optional keep-running mode supervises the app without entering the UI path', () => {
  assert.ok(
    program.indexOf('WatchdogManager.IsWatchdogRequest(args)') < program.indexOf('new EventWaitHandle('),
    'watchdog mode must branch before the main app creates activation resources');
  assert.match(program, /WatchdogManager\.Run\(\);[\s\S]*?return;/);
  assert.match(startup, /keepRunning \? \$"\{command\} --watchdog" : command/);
  assert.match(watchdog, /new Mutex\(true, MutexName, out var firstInstance\)/);
  assert.match(watchdog, /if \(!firstInstance\) return;/);
  assert.match(watchdog, /if \(!IsApplicationRunning\(\)\)[\s\S]*?KeepRunningEnabled\(store\)[\s\S]*?StartApplication\(\)/);
  assert.match(watchdog, /stopEvent\.WaitOne\(PollMilliseconds\)/);
  assert.match(watchdog, /if \(stopEvent\.WaitOne\(PollMilliseconds\)\) return;/);
  assert.match(application, /Interval = 30_000,[\s\S]*?Enabled = _settings\.KeepRunning/);
  assert.match(application, /_watchdogTimer\.Tick \+= \(_, _\) => WatchdogManager\.EnsureRunning\(\)/);
  assert.match(application, /args\.CloseReason != CloseReason\.WindowsShutDown[\s\S]*?_sessionEnding = true;[\s\S]*?WatchdogManager\.Stop\(\)/);
  assert.match(quit, /if \(_settings\.KeepRunning && !_sessionEnding\) WatchdogManager\.EnsureRunning\(\);/);
  assert.match(settings, /if \(keepRunningChanged && _keepRunning\.Checked\)[\s\S]*?_openAtLogin\.Checked = true/);
  assert.match(settings, /!keepRunningChanged && !_openAtLogin\.Checked[\s\S]*?_keepRunning\.Checked = false/);
});

test('startup registration is reconciled idempotently before the single-instance exit path', () => {
  assert.ok(
    program.indexOf('StartupManager.ReconcileRegistration(') < program.indexOf('new Mutex('),
    'a newly launched version must migrate the login command even while an older instance is running');
  assert.match(startup, /GetValue\([\s\S]*?RequiredAction\(currentCommand, command\)/);
  assert.match(startup, /case StartupRegistrationAction\.Set:[\s\S]*?SetValue/);
  assert.match(startup, /case StartupRegistrationAction\.Delete:[\s\S]*?DeleteValue/);
  assert.match(startup, /string\.Equals\(currentCommand, desiredCommand, StringComparison\.Ordinal\)[\s\S]*?StartupRegistrationAction\.None/);
  assert.doesNotMatch(program, /StartupApproved/);
  assert.doesNotMatch(startup, /StartupApproved/);
});

test('stable GitHub releases surface one periodic update notification without owning installation', () => {
  assert.match(update, /releases\/latest/);
  assert.match(update, /ZGSTokenBar-Portable-v\{versionText\}\.zip/);
  assert.match(update, /ZGSTokenBar-v\{versionText\}-SHA256\.txt/);
  assert.match(update, /parsed\.Scheme == Uri\.UriSchemeHttps/);
  assert.match(update, /string\.Equals\(parsed\.Host, "github\.com"/);
  assert.match(application, /Interval = 6 \* 60 \* 60 \* 1000/);
  assert.match(application, /_bar\.BeginInvoke\(\(\) => _ = CheckForUpdatesAsync\(\)\)/);
  assert.match(application, /_notifiedUpdateVersion == update\.Version/);
  assert.match(application, /_updateMenuItem\.Visible = true/);
  assert.match(application, /_updateTimer\.Dispose\(\)/);
  assert.doesNotMatch(application, /DownloadFile|ExtractToDirectory|apply-update/);
});

test('settings restore covers off-screen windows and normal-window z-order', () => {
  assert.match(application, /RestoredSettingsLocation\(/);
  assert.match(application, /Screen\.AllScreens/);
  assert.match(application, /SetWindowPos\(/);
  assert.match(application, /SwpNoActivate \| SwpShowWindow/);
  assert.match(application, /dialog\.BringToFront\(\);[\s\S]*?dialog\.Activate\(\);/);
});

test('enabled AI Gateway is present before its first network result', () => {
  assert.match(
    application,
    /if \(settings\.IsEnabled\(ProviderKind\.AiGateway\)\)[\s\S]*?cards\.Add\(AiGatewayBalanceService\.UnavailableCard\(DateTimeOffset\.UtcNow\)\)/);
});

test('legacy optional providers are ignored when their built-ins are not installed', () => {
  assert.match(application, /private bool HasPlugin\(string pluginId\) => _pluginHost\.DescribePlugin\(pluginId\) is not null/);
  assert.match(application, /if \(!HasPlugin\(pluginId\)\) return;[\s\S]*?CorePluginProjection\.Provider/);
  assert.match(application, /const string pluginId = "zgstokenbar\.provider\.ai-gateway";[\s\S]*?if \(!HasPlugin\(pluginId\)\) return;/);
  assert.match(application, /if \(HasPlugin\("zgstokenbar\.intelligence\.radar"\) && _radarViewState\.Snapshot/);
  assert.match(application, /if \(HasPlugin\("zgstokenbar\.intelligence\.radar"\)\)[\s\S]*?CorePluginProjection\.Radar/);
});
