import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const read = (path) => fs.readFileSync(path, 'utf8');
const sampler = read('src/ZGSTokenBar.App/SystemUsageSampler.cs');
const bar = read('src/ZGSTokenBar.App/BarForm.cs');
const popover = read('src/ZGSTokenBar.App/SystemUsagePopoverForm.cs');
const application = read('src/ZGSTokenBar.App/QuotaApplicationContext.cs');
const sampling = read('src/ZGSTokenBar.App/SystemUsageSampling.cs');
const models = read('src/ZGSTokenBar.Core/Models.cs');
const text = read('src/ZGSTokenBar.App/NativeText.cs');
const project = read('src/ZGSTokenBar.App/ZGSTokenBar.App.csproj');

test('system usage stays local and samples CPU, physical memory, disk, and GPU without dependencies', () => {
  assert.match(sampler, /GetSystemTimes/);
  assert.match(sampler, /GlobalMemoryStatusEx/);
  assert.match(sampler, /PdhAddEnglishCounter/);
  assert.match(sampler, /PhysicalDisk\(_Total\)\\% Disk Time/);
  assert.match(sampler, /PhysicalDisk\(_Total\)\\Disk Read Bytes\/sec/);
  assert.match(sampler, /PhysicalDisk\(_Total\)\\Disk Write Bytes\/sec/);
  assert.match(sampler, /\\GPU Engine\(\*\)\\Utilization Percentage/);
  assert.match(sampler, /DiskUsageCounter/);
  assert.match(sampler, /AggregateGpu/);
  assert.doesNotMatch(sampler, /HttpClient|Process\.Start|powershell/i);
  assert.doesNotMatch(sampler, /CommandLine|MainModule|ExecutablePath|MainWindowTitle/i);
  assert.doesNotMatch(project, /PackageReference/);
});

test('GPU overview reuses native storage and avoids per-counter managed strings', () => {
  assert.match(sampler, /Marshal\.ReAllocHGlobal/);
  assert.match(sampler, /NullTerminatedSpan/);
  assert.match(sampler, /TryParseGpuInstance\(\s*instance/);
  assert.doesNotMatch(sampler, /Marshal\.PtrToStringUni/);
  assert.doesNotMatch(sampler, /new List<GpuCounterSample>/);
});

test('taskbar Mini exposes system usage as one independently resizable and sortable area', () => {
  assert.match(models, /public const int SystemUsageContentWidth = MinimumAreaContentWidth;/);
  assert.match(models, /public const int ModuleGap = 4;/);
  assert.match(models, /public const string SystemMetrics = "zgstokenbar\.metrics\.system";/);
  assert.match(bar, /TaskbarMiniAreaContent\.ForSystem\(_text\.SystemUsageTitle\)/);
  assert.match(bar, /DrawSystemUsageCard\(graphics, bounds, layout\.Collapsed\)/);
  assert.match(bar, /DrawTaskbarReorderGrip\(graphics, target\)/);
  assert.match(bar, /SelectVisibleTaskbarAreas\(_taskbarContentAreas\)/);
  assert.match(bar, /DrawCollapsedSystemUsage/);
  assert.match(bar, /DrawSystemUsageRow\(graphics, bounds, 0, "CPU"/);
  assert.match(bar, /DrawSystemUsageRow\(graphics, bounds, 1, "RAM"/);
  assert.match(bar, /DrawSystemUsageRow\(graphics, bounds, 2, "I\/O"/);
  assert.match(bar, /DrawSystemUsageRow\(graphics, bounds, 3, "GPU"/);
  assert.match(bar, /MiniAreaIds\.SystemMetrics/);
});

test('system usage follows the existing delayed hover and temporary pin lifecycle', () => {
  assert.match(bar, /ShowSystemUsagePopover\(pinned: false\)/);
  assert.match(bar, /TogglePinnedSystemUsagePopover/);
  assert.match(bar, /private void MonitorSystemUsagePopover[\s\S]*?outsideClick[\s\S]*?escapePressed/);
  assert.match(popover, /ShowWithoutActivation => true/);
  assert.match(popover, /ToolWindowStyle \| NoActivateStyle/);
  assert.match(popover, /TaskbarMiniPopoverMath\.Place/);
});

test('process-heavy details are sampled only for hover or pin and rendered as a compact Top 5 table', () => {
  assert.match(bar, /WantsSystemUsageDetails/);
  assert.match(bar, /SystemUsageDetailsRequested/);
  assert.match(application, /_bar\.WantsSystemUsageDetails\) _ = RefreshSystemUsageDetailsAsync\(\)/);
  assert.match(application, /SystemUsageSampling\.TrySampleAsync\([\s\S]*?includeProcesses: true/);
  assert.match(sampling, /Task\.Run\(/);
  assert.match(sampling, /gate\.WaitAsync\(0, cancellationToken\)/);
  assert.match(sampler, /Sample\(bool includeProcesses = false\)/);
  assert.match(sampler, /includeProcesses\s*\? ReadTopProcesses/);
  assert.match(sampler, /NtQuerySystemInformation/);
  assert.match(sampler, /SystemProcessInformation/);
  assert.match(sampler, /process->WorkingSetPrivateSize/);
  assert.doesNotMatch(sampler, /\(ulong\)process->WorkingSetSize/);
  assert.match(sampler, /AggregateProcesses/);
  assert.match(sampler, /new GroupGpuEngineKey\(group, pair\.Key\.Engine\)/);
  assert.match(popover, /Math\.Min\(5, snapshot\.TopProcesses\.Count\)/);
  assert.match(popover, /SystemUsageTopProcesses/);
  assert.match(popover, /SystemUsageDiskDetail/);
  assert.match(popover, /DrawMetricRow/);
});

test('system usage refreshes once per Mini clock tick and degrades unavailable metrics to dashes', () => {
  assert.match(application, /if \(_bar\.IsTaskbarMode\)[\s\S]*?RefreshSystemUsageOverviewAsync\(\)/);
  assert.doesNotMatch(application, /_bar\.SetSystemUsage\(_systemUsageSampler\.Sample\(\)\)/);
  assert.match(application, /_clockTimer\.Interval = _bar\.IsTaskbarMode \? 1_000 : 30_000/);
  assert.match(bar, /if \(percent is null\) return "--"/);
  assert.match(text, /Performance counter unavailable/);
});
