import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const barForm = fs.readFileSync('src/ZGSTokenBar.App/BarForm.cs', 'utf8');
const placement = fs.readFileSync('src/ZGSTokenBar.App/TaskbarPlacement.cs', 'utf8');

test('taskbar Mini restores a system-minimized window during its placement tick', () => {
  const start = barForm.indexOf('    public void SyncTaskbarPlacement()');
  const end = barForm.indexOf('    public void ClampToVisibleScreen()', start);
  const body = barForm.slice(start, end);

  assert.notEqual(start, -1);
  assert.notEqual(end, -1);
  assert.match(body, /RestoreBounds\.Size/);
  assert.match(body, /WindowState != FormWindowState\.Normal/);
  assert.match(body, /WindowState = FormWindowState\.Normal/);
  assert.match(body, /TaskbarPlacement\.ShowAt\(Handle, location, placementSize\)/);
  assert.match(body, /TaskbarPlacement\.ShouldHideForFullscreen\(\)/);
  assert.match(body, /\+\+_taskbarPlacementMisses < TaskbarPlacementMissThreshold/);
  assert.match(body, /_taskbarPlacementEstablished = true/);
});

test('taskbar Mini placement reasserts topmost without forcing a visible window to show again', () => {
  const start = placement.indexOf('    public static bool ShowAt(');
  const end = placement.indexOf('    public static bool ShouldHideForFullscreen', start);
  const body = placement.slice(start, end);

  assert.notEqual(start, -1);
  assert.notEqual(end, -1);
  assert.match(body, /SetWindowPos\(/);
  assert.match(body, /TopMostWindow/);
  assert.match(body, /SetWindowPositionNoActivate/);
  assert.doesNotMatch(body, /SetWindowPositionShowWindow/);
});

test('taskbar Mini treats maximized windows separately from real fullscreen windows', () => {
  assert.match(placement, /if \(IsZoomed\(foreground\)\) return false/);
  assert.match(placement, /Screen\.FromHandle\(foreground\)\.Bounds/);
  assert.match(placement, /Windows\.UI\.Core\.CoreWindow/);
  assert.match(placement, /XamlExplorerHostIslandWindow/);
});

test('taskbar Mini stays resident through Shell transitions and reasserts topmost after animations settle', () => {
  assert.match(barForm, /SetWinEventHook\(\s*EventSystemForeground,\s*EventSystemForeground/);
  assert.match(barForm, /WinEventOutOfContext \| WinEventSkipOwnProcess/);
  assert.match(barForm, /Interlocked\.Exchange\(ref _taskbarSyncQueued, 1\)/);
  assert.match(barForm, /StabilizeTaskbarPlacement\(\)/);
  assert.match(barForm, /_shellSettleTimer\.Stop\(\);\s+_shellSettleTimer\.Start\(\)/);
  assert.match(barForm, /RegisterWindowMessage\("TaskbarCreated"\)/);
  assert.match(barForm, /private void StabilizeTaskbarPlacement\(\)\s+\{\s+if \(!_taskbarDocked \|\| IsDisposed \|\| !IsHandleCreated\) return;\s+SyncTaskbarPlacement\(\)/);
  assert.doesNotMatch(barForm, /taskbarSuppressedForShell|taskbarLifted|TryGetLiftedTarget/);
  assert.doesNotMatch(placement, /LiftedLocationAt/);
  assert.match(barForm, /UnhookWinEvent\(_foregroundChangedHook\)/);
  assert.doesNotMatch(barForm, /SetWindowsHookEx/);
  assert.doesNotMatch(barForm, /GWLP_HWNDPARENT|SetParent\(/);
});
