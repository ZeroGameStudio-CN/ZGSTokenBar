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

`npm run verify` is the graduation gate: static and Node contracts, .NET contracts, single-file App and NativeAOT CLI publication, isolated process-plugin acceptance, deterministic captures, and whitespace validation.

`scripts/build-ai-gateway-observer-plugin.ps1` builds the independently verifiable DeepSeek Harness package. The portable build embeds that exact package so a normal application start can idempotently install the missing bundled version before local-only Provider discovery runs. No runtime package or pricing download is required.

## Acceptance

All acceptance is non-interactive and CLI-only. Do not use desktop automation, injected pointer/keyboard input, or user-performed GUI interaction as required evidence.

The Luna delegation skill is external to TokenBar. Its source repository owns
policy, static prices, installation, migration and runtime-proof helpers.
TokenBar embeds no skill files and never writes its files or Codex configuration.

`economy status` and the optional Bar/settings panel only inspect local file
presence and configuration. This is not evidence that a running task has loaded
the skill. Legacy modes remain visible for diagnosis, but TokenBar does not
migrate them. Both `economy install` and `economy set` are retired and reject
requests without writing. The independent Bar visibility preference is retained.

The default .NET suite and published NativeAOT CLI acceptance use temporary
profiles to verify read-only inspection, external enable/disable handling,
preservation and rejection of former write commands. Skill tests belong with
the external skill source; tests never modify the user's Codex configuration.

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

## Token Ledger

`codex-token-usage-index.json` is the local per-session ledger. It retains accounted
sessions after source files move or disappear. Default and registered Cockpit
homes are combined before deduplication; unchanged files are not reparsed, and
appends resume from the last complete JSONL line. The total no longer uses an old
account-wide Profile counter as a floor. Keep this ledger with the app's data
directory when moving machines; do not add totals from separate copies together.
Transient startup read failures defer local recomputation until the ledger can
be loaded; they must not turn a protected on-disk baseline into a lower in-memory
total. Radar likewise retries restoring its cached supplemental models before
refreshing after a transient cache-read failure.

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
