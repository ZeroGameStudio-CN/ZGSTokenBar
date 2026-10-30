# Development

Default agent closeout and standing local-update authorization are defined in [AGENTS.md](../AGENTS.md). This page owns the technical build and verification details.

## Requirements

- Windows 10 or 11
- .NET 10 SDK
- Node.js 24 or newer, used only as the lightweight test/task runner
- A terminal environment for the CLI-only acceptance workflow

## Layout

```text
src/ZGSTokenBar.Core/          provider, settings, cache, pace, Radar
src/ZGSTokenBar.App/           WinForms bar, popovers, tray, settings
src/ZGSTokenBar.PluginSdk/     stable plugin and local API contracts
src/ZGSTokenBar.Host/          plugin lifecycle and typed dispatcher
src/ProcessPlugins/            isolated packaged Provider processes
src/ZGSTokenBar.Transport.NamedPipe/ current-user local transport
src/Plugins/                   typed built-in plugins, including optional Router observation
tests/ZGSTokenBar.Tests/       executable contract suite and visual captures
tools/ZGSTokenBar.Cli/         shipped command-line controller
scripts/                       static UI contracts and portable packaging
```

There is one production runtime. The repository does not contain Electron main, renderer, preload, bridge, installer, or macOS targets.

## Commands

```powershell
npm ci
npm test
npm run build
npm run settings-captures
npm run cli -- status --json
npm run dist
npm run verify
```

For a focused Cockpit API-service check, run:

```powershell
dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release -- --cockpit-api-service
```

The persistence and plugin lifecycle regressions can be run independently with
`dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release -- --persistence-regressions`.
They use synthetic settings, ledgers and Providers in temporary directories.

System metrics sampling and presentation can be checked with
`dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release -- --system-usage-regressions`.
Append a temporary capture directory to render Chinese/English populated,
unavailable and long-name cases at 96, 144 and 192 DPI.

`npm run verify` is the graduation gate: static and Node contracts, .NET contracts, single-file App and NativeAOT CLI publication, isolated process-plugin acceptance, deterministic captures, and whitespace validation.

`scripts/build-ai-gateway-observer-plugin.ps1` builds the independently verifiable DeepSeek Harness package. The portable build embeds that exact package so a normal application start can idempotently install the missing bundled version before local-only Provider discovery runs. No runtime package or pricing download is required.

## Acceptance

The optional `zgstokenbar.monitor.router` module is a client of the separately
installed Router monitor at `127.0.0.1:18761`. It is disabled by default and has
no project/journal/Unity dependency. An enabled module refreshes its compact card
every two seconds; the native popup reads viewport-sized queue pages while visible
and refreshes recent records at most every ten seconds outside text/menu interaction.
Explicit paging/filtering remains on demand. The monitor caches observations across clients. Closing
Token Bar or disabling this module does not stop the monitor or any Editor job.
Enable it in Settings or with `ZGSTokenBar.Cli plugin enable zgstokenbar.monitor.router --json`.

The native window covers project selection, source chat, progress/freshness,
queued-only cancellation and version-bound order changes, qualified stop and
cleanup inspection, filtered/paged history, phase timings/P95 and original CSV
export. Unavailable observations are never rendered as idle. Controls are
instance/request/project bound and are never retried after a lost reply.
The compact bar area contains a state glyph and optional queue-count badge, with
full task information kept in the popup. Hover uses the existing bar's delayed, anchored popup;
clicking pins it. The popup has no taskbar entry or window chrome. The shared
pointer timer provides the same leave grace, outside-click and Escape dismissal
as Codex; dialogs/dropdowns retain interaction. Only a pinned view enables
execution controls. Data-only card refreshes do not reset window placement or
dismiss a pinned popup. Screen placement, palette and DPI geometry reuse the bar.
Current execution, queue and recent history share one page with no tab navigation
or scrollbars. Queue pages request the visible row count and bypass background
snapshot polling. History reads one bounded 100-record window and slices visible
pages locally, keyed by server instance, project and filters. Obsolete read requests
are cancelled; matching in-flight history reads are shared. The first history page
refreshes every ten seconds, while older pages retain their snapshot until explicit
refresh or context change. The snapshot time is available with history statistics.
History is never used as execution or release authority. Current execution
actions use the existing dark context-menu renderer. Filters are collapsed by
default; full timestamps and waiting/cleanup durations remain in row hover/details.
Wheel gestures page the table under the pointer, including hover previews;
high-resolution partial deltas accumulate and page boundaries do not reread.
The preferred popup body is 760 by 796 logical pixels, with equal queue/history
height. Short desktops clamp it to their working area and retain whole-row pages.
Current, queue and history each have a named rounded surface with consistent
headings and spacing. The bar defaults to the compact icon; the existing expand
control reveals project/phase/elapsed metadata, while full task details remain
in the hover popup. Saved expanded preferences remain effective; old narrow
expanded widths normalize to the readable minimum.
History labels the retained dispatch-to-cleanup interval as resource occupancy.
Known starter refusals have their own result badge; row details show preflight,
dispatch-recovery wait and reported native-runner time separately. Older servers
without those optional fields remain readable.
The project selector has its own full-width surface below the observation/actions
row. Projects sort by cumulative recorded task count, then pending count and a
stable name/key tie-break. The top three remain visible; a selected lower-ranked
project replaces the last visible slot, preserving the two busiest projects.
Overflow follows the same ranking in the styled More menu.
The registered project selector uses flat chips for the current small catalog,
with selected state and pending counts. Switching uses the opaque registered key;
short display labels never select a path. A larger catalog retains every other
project behind the existing styled More menu, without horizontal scrolling.
`ZGSTokenBar.Cli router show --json`, `router hide --json` and `window inspect
--json` expose presentation-only acceptance. Synthetic HTTP, control and capture
checks run with `dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj
-c Release -- --router-module <temporary-capture-directory>`.
The current task uses a readable method title, separate progress/source lines and
the exact full task/source in hover and accessibility descriptions. An explicit
read-only local performance probe runs with the same command and
`--router-history-benchmark <temporary-output-directory>`; it measures live reads
and cached native page rendering without execution controls or production settings.

All acceptance is non-interactive and CLI-only. Do not use desktop automation, injected pointer/keyboard input, or user-performed GUI interaction as required evidence.

Delegation skills and their policy, prices and installation belong entirely to
their external source repositories. TokenBar has no skill discovery, status UI,
configuration inspector or delegation commands. Removed commands use the normal
unknown-command response without accessing a Codex profile.

The default .NET suite verifies that old preferences cannot restore removed
controls; published CLI acceptance verifies removed commands have no side effects.

- Behavior: deterministic .NET executable tests and Node source contracts.
- Rendering: CLI-generated captures produced by the production renderers.
- Packaged-window properties: `ZGSTokenBar.Cli window inspect`.
- Final gate: `npm run verify`.

If an acceptance requirement has no CLI evidence route, add a focused test, fixture, capture command, or probe before treating it as passed. Starting or replacing a running app is deployment, not acceptance.

The app is self-contained and single-file. Unsigned packages are supported for local use; public releases require Authenticode signing and timestamp verification.

For local replacement builds, publish with a fresh temporary `--artifacts-path`
and output directory. Verify live behavior after restarting the installed file;
its on-disk hash alone does not prove the running behavior matches the source.
`window inspect` reports the loaded App/Core module IDs, the actual UI Token
total, and actual/open versus cached Radar model groups. Compare module IDs
with the built DLLs instead of relying on a hash computed from a path after launch.
It also reports `radarTrialObservedAt`, `recentConversationCount` and
`latestTurnOutputTokensPerSecond`. The latter is output tokens divided by the
entire turn's wall time, including tools and waiting, not server decode throughput.

Run the synthetic Radar/current-pricing/conversation cases and render their
production UI with `dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj
-c Release -- --radar-usage-update <temporary-capture-directory>`.
Conversation measurement incrementally scans at most twelve recent sessions,
with an 8 MiB per-file budget per refresh. A complete turn in the last 2 MiB
bootstraps immediately; longer turns catch up across refreshes. Only complete
catch-up is published. Subsequent refreshes parse new
bytes, retaining partial JSONL lines in a bounded memory buffer and validating
file checkpoints before reuse. No conversation content is persisted. Real turn
start/completion times and cumulative counter deltas determine the average;
explicit first-token latency remains a separate metric. Interrupted turns freeze
their timing; dormant unfinished turns show unavailable speed. The lifetime
baseline is unchanged. Completed turn summaries are retained in the existing
local token index for up to 90 days or 500 turns, keyed by session and turn;
prompts and responses are never written. Unchanged terminal turns reuse the
in-memory history instead of
reloading the full token ledger on every fast refresh. New turns, failed-save
retries and due retention still merge with the latest durable index under its
existing cross-process lock. The opt-in `--history-refresh-memory-probe
<temporary-index-copy> <report>` test command compares this refresh path using
an isolated session and ledger, with unchanged on-disk bytes checked.
The popover starts at the newest summaries and pages through the retained records,
while unfinished turns remain
in the live window. Plugin snapshot details continue to expose the bounded recent
view. The Codex surface opens the conversation list as its primary view, with
spend, Token and overall cache summaries beneath it. Its optional Radar opens as
an owned window to the right, retaining its own model folding, scrolling and
benchmark selection. Opening the conversation view does not request a Radar fetch;
expanding Radar requests one without resetting conversation paging. Closing the
primary view closes its extension, and pointer containment includes both windows.
The compact two-line table caps the title column at approximately 200 logical
units and ellipsizes longer names, preserving their full hover text. It groups
the chat name above known model/effort/tier metadata;
state has a dedicated centered column. Each turn retains its full local start
date/time and adds cached input divided by total input as its cache hit percentage.
Missing/zero input remains unavailable. Long names use ellipsis and row hover
shows the full name and cache percentage. Names are read
from a bounded, change-cached 2 MiB tail of each registered home's session index
and are never persisted to the token ledger. A missing title displays the project
and short session ID rather than an indistinguishable unnamed label. The main
view uses larger title/body/number fonts and 44-unit two-line rows; the Radar
extension keeps its separate sizing and has its own collapse control.
Missing metric cells are blank; columns with no measured values anywhere in the
retained display list are removed, reducing the whole popup width. Rows without
any measured usage/timing are omitted. Display paging and hover use the same
filtered list, leaving the ledger and exported measurements unchanged. The
primary hover lists only recorded fields, including response/routing evidence
when present, without repeated unknown or unavailable messages.
The primary model label is the session setting. Timestamped `response.completed`
records are accepted as response evidence only with an explicit matching turn,
matching thread when present, a completed response envelope and a response ID.
Explicit `model_rerouted` / `model/rerouted` records remain a separate routing
notice, preserving from/to/reason. Settings, catalog headers and sub-agent
activity are never promoted into response evidence. Hover and CLI report the
recorded evidence or explicit unavailability. The sampled local sources did
not retain actual response-model or routing records; this reader does not change
client logging/proxies or recover missing past responses. Explicit context or
`thread_settings_applied` events identify Fast/Standard; missing records remain
unknown and turns with multiple observed tiers show mixed.
Alternating row shading and aligned numeric columns
show new input (input minus cache hits), output, cached input, API-equivalent
cost, turn-average output rate and first-token latency. Cost is summed per proven
request/model/tier; unknown coverage is unavailable or marked with a plus.
The state column distinguishes running, ended,
interrupted or unknown after two minutes without usage updates or on a log read failure. Unknown does not
assert that a turn has stopped. First-token headings and values are centered.
Project, identity and metric definitions live in hover details.

The primary view includes local-start-date presets (observed history, today, seven
or thirty local calendar days), exact recorded project-name and configured-model
filters, metadata search and sortable column headings. Filters are in-memory;
background refresh preserves the selection and typing. Changing a filter resets
paging. The four summary cards describe matching observed turns, recorded output,
API-equivalent cost (including partial coverage) and whole-turn average speed.
They are not all-request totals; the current live window and retained terminal
history remain bounded. Empty matches never fall back to unfiltered records.
The same popup keeps whole-row paging, the separate Radar extension and the
existing global token/cache/fee summaries. On short screens the per-model cards
may be omitted to retain a usable table without adding scrollbars; full filtered
model summaries remain in the speed tooltip.

The primary view also summarizes matching complete output-producing turns,
grouped by configured model, effort and service tier. Average speed is
summed output divided by summed whole-turn duration; the maximum is the highest
individual turn average, not instantaneous decoding speed. Running, interrupted,
stale, partial and invalid-duration turns are excluded. Up to six most recently
active models are visible, with each model's effort/tier rows collected under
one heading. Model identifiers and effort names independently map to stable,
automatically generated high-contrast text accents, without per-model settings
or runtime-random hashes. Unknown/mixed service tiers and missing effort are
excluded from the comparison display; the raw measurements remain available.
Up to four setting combinations are shown
per model, with sample counts; hover lists every combination and the measurement
definition. This comparison alone does not establish model routing.

Quota depletion uses provider-reported percentage changes, independent of the
conversation token-speed table. Cycle fallback and rolling trend projections
remain estimates conditional on continued consumption. `--quota-pace-review`
runs the focused calculation, weighting, reset, freshness, history and UI cases.
Future samples and exhausted prior windows cannot establish current exhaustion.
Already-rendered forecasts expire at their window reset or maximum data age;
known exhausted/weekly-blocked quota can remain blocked until its known reset
while refresh is deferred. This prevents stale "until reset" claims after reset.

The Radar subtitle switches between the Codex trial (/100) and historical (/150)
views. API contributions keep those datasets separate and include sample counts.
Trial unread state uses the trial timestamp. The routing export retains its
existing historical ranking policy and identifies its benchmark, original source
timestamp and historical status explicitly; five-question trials do not drive it.

The model price table includes exact GPT-6 Astra/Sol/Luna and GPT-6.1 Sol prices
from the official model pages checked on 2026-10-23. Known 6-series Fast/Flex/Batch
and Astra/Sol 6.1 Ultrafast multipliers are explicit, using the
[official API price table](https://developers.openai.com/api/docs/pricing?latest-pricing=ultrafast);
unverified model/tier combinations
remain unpriced. These are present-price API equivalents, not subscription bills
or reconstructed historical invoices.

## Token Ledger

`codex-token-usage-index.json` is the local per-session ledger. It retains accounted
sessions after source files move or disappear. Default and registered Cockpit
homes are combined before deduplication; unchanged files are not reparsed, and
appends resume from the last complete JSONL line. The total no longer uses an old
account-wide Profile counter as a floor. Keep this ledger with the app's data
directory when moving machines; do not add totals from separate copies together.
Desktop and built-in/CLI refreshes use the same ledger transaction: acquire the
cross-process file lock, reload the latest index, scan, and atomically commit.
Index loading deserializes once, with a typed schema-header converter rejecting
unsupported headers before they can be normalized. It avoids a second full JSON
DOM without changing ledger locks, write order or accounting. The opt-in
`--ledger-memory-probe baseline|optimized <temporary-index-copy> <report>` test
route compares fresh processes using the same private snapshot and normalized
data fingerprint; it never forces collection in the running App.
Failed writes retain pending session records for retry even after sources disappear;
retry reconciles session watermarks without adding overlapping totals or baselines.
The built-in adapters receive the explicit host data root through `PluginStartContext.HostDataRoot`;
process Providers still receive only their own plugin data directory.
Transient startup read failures defer local recomputation until the ledger can
be loaded; they must not turn a protected on-disk baseline into a lower in-memory
total. Radar likewise retries restoring its cached supplemental models before
refreshing after a transient cache-read failure.

Transient settings read failures also defer auto-discovery, startup registration
and settings saves until the original file can be restored. Plugin stop/start and
refresh share a lifecycle gate; disable rejects queued refreshes and late results.

An explicitly accepted historical starting amount can be established once with
`--token-ledger-baseline <input-index> <tokens> <output-data-directory>` on the
test executable. Schema 8 retains the fixed amount, cutoff timestamp and per-session
watermarks. The displayed total is then the accepted amount plus post-cutover
increments, not the amount plus all observed historical sessions. Late-discovered
sessions are scanned once to exclude pre-cutover usage. The old historical amount
remains an accepted estimate; this operation does not prove it was accurate.
Back up the ledger before installing it; rolling back to a pre-schema-8 app also
requires its matching backup ledger.

For an explicit one-time cold-history import, prepare only missing or older-accounting
sessions from SHA-256 manifests using `scripts/prepare-token-history.ps1` with
`-PackageRoot`, `-IndexPath`, and a new `-StagingRoot`. Optional `-DependencyIdsPath`
selects parent session IDs needed to resolve forks. This command requires local
7-Zip and may hydrate online-only archives; it never deletes original packages.
Each package has a resumable verification receipt.

Run the test executable with `--token-ledger-import <input-index> <staging-home>
<output-data-directory>` to merge prepared JSONL and current local sessions into
a separate output ledger. Review the unresolved count, stop the app, back up its
ledger and merge the latest local increments before replacing it. Remove only
task-created extraction files after verifying the installed ledger. NAS cleanup
must use the storage provider's release-space operation, never package deletion.
Neither import tool is called during ordinary app refresh. Missing source data
cannot be reconstructed from a cumulative total alone.


## Observed conversation export

The CSV action exports every matching observed row, including older retained
rows beyond the visible page. UTF-8 CSV includes full identities, configured
model, recorded response/routing evidence, known metrics and completeness flags;
missing measurements remain empty, metadata formula prefixes are neutralized,
and costs remain API equivalents rather than subscription bills. Unknown
coverage and incomplete counters are never converted to measured zeros.

The fixed CLI command reads the active desktop cache without changing the
popup, rescanning logs or writing user settings:

```powershell
ZGSTokenBar.Cli codex-usage export --output C:/Temp/observed-turns.csv --range 7d --project POB --sort output --json
```

`--range` accepts `retained|today|7d|30d`; `--model`, `--project`, `--search`,
`--sort recent|name|state|started|input|output|cache|cache-hit|cost|speed|first`
and `--ascending` use the shared UI query semantics. Project names are recorded
metadata, not canonical workspace selectors. The destination must be new; CLI
never replaces an existing file. The desktop-only read API returns 4 KiB binary
CSV chunks with a SHA-256 snapshot identity, staying inside the 64 KiB pipe
frame limit. A changed snapshot aborts before destination creation. The CLI
checks the assembled body digest (excluding the output UTF-8 BOM); the existing
plugin-detail projection remains bounded to recent turns. Export construction
has a four-million-character budget and reports an explicit error rather than
truncating rows. No export opens an Editor operation or contacts a provider.

Focused synthetic query, aggregation, CSV, transport-budget and native-window
checks plus renderer captures:

```powershell
dotnet run --project tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj -c Release -- --conversation-history-panel C:/Temp/history-panel-captures
```


## Live taskbar readouts

The system area's CPU/RAM/disk/GPU tracks and resource/process hover are preserved.
The separate live-rates area defaults to collapsed and keeps a readable one-line
summed output tok/s. Expand it for activity counts and network RX/TX. Its hover
groups the headline rate and recorded/estimated split, chat activity, and system
network in three compact sections; coverage and sample time share the footer.
It reuses module ordering, resizing, pin/dismiss and anchor geometry;
its hover has independent rate content without CPU/process rows. No extra system
process collection runs for the rates hover.

Rates now use per-response log timestamps, not a difference of turn counters
between polls. Sum each response's token fraction overlapping the latest three
seconds and divide by three. Intervals under 500 ms use a 500 ms floor; missing,
future, reversed or over-fifteen-minute intervals are excluded. During generating
phases, estimate only the uncovered interval after the last response, using the
same session's latest three responses within ten minutes, then up to ten matching
model/source responses within thirty minutes as fallback. Tool/idle phases receive
no extrapolation. A tilde denotes estimated output; hover exposes recorded and
estimated values/share. These are response-window observations and estimates,
not server decode speed or a guarantee that every client/request is captured.

The existing bounded twelve-session incremental reader publishes a separate
in-memory rate feed; it does not change durable token accounting/history. The
existing one-second clock refreshes this feed under the existing non-overlap gate.
No second watcher/timer or ledger is added. Unavailable/expired feeds remain
unavailable; pending catch-up and observed session coverage are shown. Historical
records are positioned on their original timestamps, so discovery cannot produce
a current counter burst.

Network reads up, non-loopback/non-tunnel default-gateway adapter byte counters,
not model/process traffic. Discovery is cached thirty seconds, invalidated on read
failure; K/M/G use powers of 1024 per second. No packet interception or network
configuration changes occur. Existing taskbar drag-to-snap/float placement and
user settings remain intact. `window inspect --json` includes the three-second
window, recorded/estimated rates, generating/tools counts and existing module and
data identity checks.

The rate hover's Totals block shows cumulative receive/send/combined bytes beside
the existing Token total. Network totals start with the first valid observed
adapter delta and retain that start time across Bar restarts; no pre-existing
traffic is inferred from Token accounting or counters accumulated before startup.
Closed-Bar, reset, unavailable and over-ten-second sampling gaps are excluded.
This remains system-adapter traffic and is independent of the Token ledger.

`network-traffic-history.json` in the resolved user data root uses the existing
locked atomic-write primitive. Checkpoints run in the existing background sample
path every thirty seconds and on normal exit; an abrupt stop can lose the last
unsaved interval. Session checkpoints make retry/normal restart idempotent; an old
writer cannot overwrite a newer session. Invalid or unreadable history is retained
and reported as unavailable rather than replaced by zero. No settings, credentials
or Token data are rewritten. Cumulative byte units use powers of 1024; live rates
keep `/s`. `window inspect --json` exposes cumulative byte values, start/checkpoint
times and save-failure status for read-only verification.
