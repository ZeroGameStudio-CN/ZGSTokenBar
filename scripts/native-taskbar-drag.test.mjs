import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const settings = fs.readFileSync('src/ZGSTokenBar.Core/AppSettings.cs', 'utf8');
const settingsForm = fs.readFileSync('src/ZGSTokenBar.App/SettingsForm.cs', 'utf8');
const barForm = fs.readFileSync('src/ZGSTokenBar.App/BarForm.cs', 'utf8');
const appContext = fs.readFileSync('src/ZGSTokenBar.App/QuotaApplicationContext.cs', 'utf8');
const nativeText = fs.readFileSync('src/ZGSTokenBar.App/NativeText.cs', 'utf8');
const placement = fs.readFileSync('src/ZGSTokenBar.App/TaskbarPlacement.cs', 'utf8');
const topology = fs.readFileSync('src/ZGSTokenBar.App/DisplayTopology.cs', 'utf8');
const coordinator = fs.readFileSync('src/ZGSTokenBar.App/WindowPlacementCoordinator.cs', 'utf8');
const productReadme = fs.readFileSync('README.md', 'utf8');

test('taskbar Mini persists topology-scoped placement profiles', () => {
  assert.match(settings, /public double\? TaskbarPosition \{ get; set; \}/);
  assert.match(settings, /public string\? TaskbarMonitor \{ get; set; \}/);
  assert.match(settings, /public bool TaskbarDocked \{ get; set; \} = true;/);
  assert.match(settings, /public Dictionary<string, double> TaskbarPositions/);
  assert.match(settings, /public int PlacementSchemaVersion/);
  assert.match(settings, /public PlacementMigrationSeed\? PlacementMigrationSeed/);
  assert.match(settings, /public Dictionary<string, WindowPlacementProfile> PlacementProfiles/);
  assert.match(settings, /CapturePlacementMigrationSeed/);
  assert.match(settings, /private const int SettingsLoadAttempts = 3;/);
  assert.match(settings, /TryLoadSettingsFile\(SettingsPath, out var invalidContents\)/);
  assert.match(settings, /TryLoadSettingsFile\(SettingsPath \+ "\.corrupt\.bak", out _\)/);
  assert.match(settings, /WindowPlacementProfilesJsonConverter/);
  assert.match(settingsForm, /settings\.CopyPlacementStateFrom\(_original\)/);
  assert.match(appContext, /nextSettings\.CopyPlacementStateFrom\(_settings\)/);
  assert.match(appContext, /_settings\.PlacementProfiles\[commit\.TopologyKey\] = commit\.Profile\.Copy\(\)/);
  assert.match(barForm, /public event EventHandler<WindowPlacementCommit>\? PlacementCommitted/);
  assert.doesNotMatch(barForm, /PositionCommitted|TaskbarPositionCommitted/);
});

test('taskbar Mini separates desired placement from resolved fallback', () => {
  const syncStart = barForm.indexOf('    public void SyncTaskbarPlacement()');
  const syncEnd = barForm.indexOf('    public void ClampToVisibleScreen()', syncStart);
  const syncBody = barForm.slice(syncStart, syncEnd);
  assert.notEqual(syncStart, -1);
  assert.notEqual(syncEnd, -1);
  assert.match(syncBody, /_taskbarDragging/);
  assert.match(syncBody, /_placementCoordinator\.DockedPreference\(topology, profile\)/);
  assert.match(syncBody, /_placementCoordinator\.PositionForResolvedMonitor/);
  assert.match(syncBody, /_resolvedTaskbarMonitor = resolvedMonitor/);
  assert.doesNotMatch(syncBody, /ActiveProfile\s*=/);

  assert.match(barForm, /_taskbarDragStartScreen = Cursor\.Position/);
  assert.match(barForm, /_taskbarDragCurrentLocation = _taskbarDragStartLocation/);
  assert.match(barForm, /_dragTopologyKey = _placementCoordinator\.ActiveTopology\?\.Key/);
  assert.match(barForm, /_topologyChangedDuringDrag/);
  assert.match(barForm, /_deferredTopologyDuringDrag/);
  const refreshStart = barForm.indexOf('    private void RequestTopologyRefresh');
  const refreshEnd = barForm.indexOf('    private void HandleShellSettleTick', refreshStart);
  assert.notEqual(refreshStart, -1);
  assert.notEqual(refreshEnd, -1);
  assert.doesNotMatch(barForm.slice(refreshStart, refreshEnd), /_topologyChangedDuringDrag\s*=\s*true/);
  assert.match(
    barForm.slice(refreshStart, refreshEnd),
    /if \(_taskbarDragging\) _deferredTopologyDuringDrag = null;/,
  );
  assert.match(barForm, /if \(_taskbarDragging && !Capture\)[\s\S]*Control\.MouseButtons & MouseButtons\.Left[\s\S]*Capture = true/);
  const activationStart = barForm.indexOf('    private void ActivateTopology');
  const activationEnd = barForm.indexOf('    private void RestoreActiveFloatingPosition', activationStart);
  const activationBody = barForm.slice(activationStart, activationEnd);
  assert.notEqual(activationStart, -1);
  assert.notEqual(activationEnd, -1);
  assert.match(activationBody, /if \(_taskbarDragging\)[\s\S]*_deferredTopologyDuringDrag = topology;[\s\S]*return;/);
  assert.ok(
    activationBody.indexOf('if (_taskbarDragging)')
      < activationBody.indexOf('_placementCoordinator.Activate(topology, Size)'),
    'topology activation is deferred before it can replace the active drag profile',
  );
  assert.match(barForm, /TaskbarPlacement\.TryGetDockTarget\(/);
  assert.match(barForm, /if \(!_popoverPinned\) HidePopovers\(\);\s+ClearHoverStateForTaskbarDrag\(\);/);
  assert.match(barForm, /ClearHoverStateForTaskbarDrag\(\);\s+TaskbarPlacement\.InvalidateCache\(\);\s+_taskbarDragMoved = true/);
  assert.match(barForm, /var currentScreen = Cursor\.Position/);
  assert.match(barForm, /private void ClearHoverStateForTaskbarDrag\(\)/);
  assert.match(barForm, /_taskbarDocked = false/);
  assert.match(barForm, /TaskbarPlacement\.MoveAt\(Handle, requested, Size\)/);
  assert.match(barForm, /_suppressClickUntil = DateTime\.UtcNow\.AddMilliseconds\(250\)/);
  assert.match(barForm, /_placementCoordinator\.CommitDocked\(resolvedMonitor, relativePosition, Size\)/);
  assert.match(barForm, /_placementCoordinator\.CommitFloating\(new Rectangle\(floatingLocation, Size\)\)/);
  assert.match(
    barForm,
    /CanCommitTaskbarDrag\([\s\S]*?_topologyRefreshPending,[\s\S]*?_topologyMayHaveChangedPending/,
  );
  assert.match(barForm, /_topologyMayHaveChangedPending \|= topologyMayHaveChanged/);
  assert.match(barForm, /\(!topologyRefreshPending \|\| !topologyMayHaveChangedPending\)/);
  assert.match(barForm, /CompleteTaskbarDrag\(deferredTopology, restorePlacement: false\)/);
  assert.match(barForm, /PlacementCommitted\?\.Invoke\(this, commit\)/);
  assert.match(coordinator, /public double PositionForResolvedMonitor/);
  assert.match(coordinator, /ActiveProfile = profile/);
});

test('taskbar drag range excludes the notification area and clamps both orientations', () => {
  assert.match(placement, /trayBounds\.Left - windowSize\.Width - Gap/);
  assert.match(placement, /trayBounds\.Top - windowSize\.Height - Gap/);
  assert.match(placement, /taskbarBounds\.Top \+ \(taskbarBounds\.Height - windowSize\.Height\) \/ 2/);
  assert.match(placement, /Math\.Clamp\(Horizontal \? location\.X : location\.Y, Minimum, Maximum\)/);
  assert.match(placement, /Math\.Clamp\(\(coordinate - Minimum\) \/ \(double\)\(Maximum - Minimum\), 0, 1\)/);
});

test('taskbar drag enumerates secondary taskbars and selects a persisted monitor', () => {
  assert.match(placement, /Shell_SecondaryTrayWnd/);
  assert.match(placement, /EnumWindows\(callback, 0\)/);
  assert.match(placement, /var hasTray = tray != 0 && GetWindowRect\(tray, out trayBounds\)/);
  assert.match(placement, /: taskbarMaximum/);
  assert.match(placement, /_cachedTracks/);
  assert.match(placement, /CanReuseCachedTracks\(/);
  assert.match(placement, /requiredMonitor/);
  assert.match(placement, /SetWindowPositionNoZOrder/);
  assert.match(placement, /DistanceToTaskbar/);
  assert.match(placement, /DockThreshold = 32/);
  assert.match(placement, /SelectTrack\(tracks, preferredMonitor/);
  assert.match(placement, /Screen\.FromRectangle\(taskbarBounds\.Rectangle\)/);
  assert.match(barForm, /TaskbarPlacement\.MoveAt\(Handle, dockedLocation, Size\)/);
  assert.match(barForm, /TaskbarPlacement\.InvalidateCache\(\)/);
  assert.match(barForm, /TaskbarPlacement\.TryConstrain\(\s+Size,\s+_taskbarDragCurrentLocation,\s+_resolvedTaskbarMonitor/);
  assert.match(barForm, /_taskbarDragCurrentLocation = dockedLocation/);
  assert.match(barForm, /_taskbarDragCurrentLocation = requested/);
  assert.match(barForm, /CommitFloating\(new Rectangle\(floatingLocation, Size\)\)/);
  assert.match(topology, /QueryDisplayConfig/);
  assert.match(topology, /WtsClientProtocolType/);
  assert.match(topology, /monitor-v1:/);
  assert.match(topology, /topology-v1:/);
  assert.match(topology, /MaximumBufferAttempts = 3/);
  assert.match(barForm, /IdentitySignature/);
  assert.match(barForm, /RequestTopologyRefresh\(message\.Msg == WmDisplayChange\)/);
  assert.match(barForm, /Task\.Run\(CaptureTopologyLoop\)/);
  assert.match(barForm, /_topologyCaptureRequested/);
  assert.match(barForm, /generation != _topologyCaptureGeneration/);
  assert.doesNotMatch(barForm, /var captured = DisplayTopology\.Capture\(\)/);
});

test('product help documents Mini dragging and the Radar entry point', () => {
  assert.match(productReadme, /Drag the Mini's card area/);
  assert.match(productReadme, /hover a Codex logo in Mini/);
  assert.match(appContext, /_text\.OpenRadarWebsite/);
  assert.match(nativeText, /Open Codex Radar website/);
});
