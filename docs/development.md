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
src/Plugins/                   four public built-in plugins
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

`npm run verify` is the graduation gate: static and Node contracts, .NET contracts, single-file App and NativeAOT CLI publication, isolated process-plugin acceptance, deterministic captures, and whitespace validation.

`scripts/build-ai-gateway-observer-plugin.ps1` builds the independently verifiable DeepSeek Harness package. The portable build embeds that exact package so a normal application start can idempotently install the missing bundled version before local-only Provider discovery runs. No runtime package or pricing download is required.

## Acceptance

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
prompts and responses are never written. The popover starts at the newest
summaries and pages through the retained records, while unfinished turns remain
in the live window. Plugin snapshot details continue to expose the bounded recent
view. The compact table
shows new input (input minus cache hits), output, cached input, API-equivalent
cost, turn-average output rate and first-token latency. Cost is summed per proven
request/model/tier; unknown coverage is unavailable or marked with a plus.
Project, identity, state and metric definitions live in hover details.

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
