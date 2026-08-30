# Provider 自动发现与 Codex 美元等值成本

Status: Final

Last updated: 2026-08-30

Baseline: `dc87bc4a2fe2a9cb505614c74eef7116ed541c82` plus the uncommitted DeepSeek Harness, startup, and Settings scrollbar implementation already present in the primary workspace.

Execution authorization: Authorized by the user's 2026-08-30 request to absorb the OpenUsage improvements, optimize the existing implementation, and show how many US dollars the observed use is equivalent to.

Terminal-action authorization: Local build, verification, packaging, installation into the current user's existing ZGSTokenBar data root, and local restart are authorized. The user's 2026-08-30 request to “收尾干净到最新” additionally authorizes a scoped Git commit and push to `origin/main` after the required checks. Public release, credential deletion, and remote deployment remain unauthorized.

## Goal and non-goals

Make locally configured Providers work without a second ZGSTokenBar credential flow, prevent one account's cached balance from appearing under another account, and add truthful Today, Yesterday, Last 7 Days, Last 30 Days, daily-history, and model-composition Codex API-equivalent USD estimates from local rollout logs.

This work does not add a ZGSTokenBar API-key field, claim that subscription usage is an actual invoice, send local usage data away from the machine, estimate unknown model prices as zero, add a localhost HTTP API, add telemetry or cloud sync, or implement Claude local spend in this increment.

## Current and intended behavior

- Claude and Codex already reuse their native local credentials; DeepSeek already reuses the Harness-managed Provider credential.
- Process Providers are separately packaged and isolated, but the DeepSeek package is not yet part of the portable app artifact and Provider enablement cannot distinguish an automatic default from an explicit user decision.
- DeepSeek's sanitized last-success cache is not tied to the credential that produced it.
- Codex local usage currently retains cumulative and current-day token/cache counters with fork replay correction, but it does not retain model-attributed daily buckets or calculate USD.

After this change, a bundled Provider with a local-only credential probe is installed and enabled automatically only when its own existing credential loader reports a usable local credential. A user's explicit on/off choice always wins. DeepSeek cache fallback is accepted only for the same credential fingerprint. Codex usage shows measured tokens plus an API-equivalent USD estimate based on the model, service tier, cache-read input, cache-write input, uncached input, output, and proven long-context classification recorded in local rollout logs. On startup, the valid persisted index is projected and published immediately so amount and history are available before the bounded background rollout scan completes; that scan then calibrates the live result. The existing amount card opens an in-place, pinned 30-day history view instead of creating a second popover.

The verified implementation contains rollback-compatible settings-schema-2 provenance, local-only probes, embedded Provider `1.2.3`, DeepSeek cache schema v2, bounded Codex daily/model pricing buckets, structured Today/Yesterday/30-day spend rows, and compact Today/30-day native text. Focused and complete gate evidence is recorded below; final artifact hashes and live values are recorded only after the last packaged restart.

## Confirmed decisions and invariants

1. Provider probing is an optional SDK capability implemented by the Provider. It is local-only, performs no network request, returns only a boolean, and reuses the same credential loader as refresh.
2. Successful automatic enablement is remembered. Missing credentials are probed again on later launches; removal of credentials does not silently undo a previously enabled module. Any Settings toggle becomes an explicit decision and permanently overrides automatic seeding until the user changes it again.
3. Settings continue to write schema 2. The loader accepts schema 1/2 and the transitional provenance schema 3, normalizes accepted state back to schema 2, and preserves exact migration backups as `.v2.bak` or `.v3.bak`. Provenance-field presence, rather than the numeric schema alone, determines whether an old value already records an explicit or automatic decision. Legacy enabled Providers remain explicit. The retired gateway's legacy default-off DeepSeek value is the sole exception: it remains undecided for one successful Harness auto-discovery probe, while an explicit transitional-schema-3 off decision remains off and is never probed.
4. Portable packaging embeds the verified Provider `1.2.3` `.zgsplugin` bytes in `ZGSTokenBar.exe`. Startup installs missing valid bundled versions through the existing package verifier. It never deletes or overwrites a same-version directory; a same-ID/version content conflict is rejected and recorded as `trust_failed`, and an optional bundle failure cannot prevent Claude/Codex startup.
5. Optional process Providers are admitted only when their manifest catalog composes with the built-ins. Duplicate IDs or command namespaces, missing dependencies, and runtime command/settings conflicts fail closed. A conflicting process is stopped, its contributions are cleared, and its health becomes `trust_failed` both during initial startup and dynamic enablement; the rest of the desktop remains operational.
6. Plugin-catalog enumeration, bundled-failure-marker persistence, and optional `plugin-data/<id>` creation treat filesystem access failures as bounded trust/unavailable states rather than desktop-fatal exceptions. An explicit `--data-directory` or valid `ZGSTOKENBAR_DATA_DIRECTORY` root is isolated from the global Run registration, watchdog lifecycle, and watchdog stop event.
7. DeepSeek stores a one-way SHA-256 credential fingerprint with the sanitized balance. The raw credential is never serialized, logged, returned, or sent to the host. Missing/unreadable credentials cannot authorize an old cache; authentication or transient failures may reuse only a matching cache.
8. Codex spend remains a local-machine history, not an account bill. It reads only rollout metadata and token counters; it does not retain prompts, responses, file paths, session IDs, account IDs, or raw model events.
9. Today and Yesterday are local calendar days. Last 30 Days includes today and the preceding 29 local calendar days. Daily/model buckets retain 32 days to make midnight rollover and verification deterministic.
10. Cost is labelled “API-equivalent estimate.” For a priced request:

   `USD = (uncached input × input rate + cache-read input × cache-read rate + cache-write input × cache-write rate + output × output rate) / 1,000,000`.

   Cache-read and cache-write input are both subtracted from total input before the uncached term. Reasoning output is already included in output and is never charged twice. For catalog models whose official page declares it (currently GPT-5.4, GPT-5.5, and the priced GPT-5.6 variants), requests above 272K input tokens apply the published long-context multipliers to the full request. Smaller-context variants such as GPT-5.4 Mini do not inherit the flagship surcharge. Only standard/default/auto tier use is priced; an explicitly different tier remains unpriced rather than borrowing the standard rate.
11. The initial bundled OpenAI price snapshot is dated 2026-08-21 and comes from the official [OpenAI API pricing](https://developers.openai.com/api/docs/pricing), [model comparison](https://developers.openai.com/api/docs/models/compare), and model pages such as [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol). Prices live in a versioned Core catalog and can be replaced by an app update. No third-party runtime price download is introduced. Exact aliases only; an unknown or research-preview model remains unpriced. A request that reports cache-write tokens for a catalog entry without a cache-write rate also remains unpriced.
12. A period with only unpriced use displays “Unpriced,” not `$0`. Mixed priced/unpriced use displays an approximate lower bound such as `≈$1.23+` and exposes the unpriced token count. A period with no measured use displays `—` in compact text and `no-data` in structured snapshots.
13. Existing incremental file-length/mtime indexing and fork/subagent replay correction remain authoritative. Spend accounting version 3 stores only bounded daily aggregates and a bounded model/tier/token cursor, advances its byte cursor only through the last complete JSONL newline, treats missing/null cache-write counters as unknown rather than zero, and prunes closed spend scans outside the 32-day window without discarding compatible lifetime totals.
14. Spend history projects exactly 30 local calendar days, including explicit empty days, a 7-day period including today, and canonicalized privacy-safe model totals. The amount card exposes a localized History action only when measured history exists. Clicking it pins the existing popover and switches it in place; Back returns to the overview while Escape, outside click, and the taskbar target retain their existing close behavior without activating the window.

## Scope and affected components

- SDK/host: `src/ZGSTokenBar.PluginSdk/PluginContracts.cs`, `src/ZGSTokenBar.Host/ProcessPluginProxy.cs`, `src/ZGSTokenBar.Host/PluginPackageManager.cs`, `src/ZGSTokenBar.Host/PluginCatalogComposer.cs`, and `src/ZGSTokenBar.Host/ZgsTokenBarHost.cs`.
- Provider bootstrap and settings migration: `src/ZGSTokenBar.Core/AppSettings.cs`, `src/ZGSTokenBar.App/Program.cs`, `src/ZGSTokenBar.App/QuotaApplicationContext.cs`, `src/ZGSTokenBar.App/SettingsForm.cs`, and a small app-owned bootstrap helper.
- Provider implementations/manifests: Claude, Codex, and DeepSeek Harness Provider projects and generated manifests.
- DeepSeek cache: `src/ProcessPlugins/ZGSTokenBar.Plugin.AiGatewayObserver/AiGatewayObserverClient.cs` and its protocol entrypoint.
- Codex spend: `src/ZGSTokenBar.Core/CodexTokenUsageReader.cs`, a Core price catalog, Core-to-plugin projection, native text, and Codex usage popover rendering.
- Distribution/docs/tests: portable and Provider build scripts, feature/privacy/provider docs, .NET tests, Node contract tests, and the graduation gate.

## Interfaces, data, and state flow

- `ILocalCredentialProbe.HasLocalCredentialsAsync()` is callable only for manifests declaring `local-credentials`.
- A process Provider handles `plugin.probe` after the normal verified handshake. The host validates that the capability is declared and accepts a strict boolean result.
- Settings persist explicit Provider decisions and successfully auto-enabled Provider IDs separately from effective `PluginEnabled` values while continuing to serialize schema 2. Transitional schema 3 is input compatibility only and is downgraded atomically after an exact `.v3.bak` is retained.
- `PluginCatalogComposer.SelectOptional` rejects incompatible optional manifests before auto-discovery or desktop-host construction. `ZgsTokenBarHost` revalidates process runtime contributions after start on both boot and dynamic enablement.
- The Codex usage index schema v7 adds per-file bounded daily/model/token buckets, cache-write counters, and incremental model/service-tier cursor state. Spend accounting version 3 forces a safe one-time rebuild from live files while retaining compatible lifetime totals and a separate append-safe `SpendScannedLength` cursor.
- `CodexTokenUsageSummary` adds three optional spend periods plus an in-memory 30-day/model history projection. The application builds this projection directly from the validated persisted index during startup, sends it to the native UI, and publishes the local Codex plugin snapshot before scheduling the background file refresh. The refreshed result replaces the cached projection when scanning completes. The plugin snapshot continues to expose decimal USD and unpriced-token metadata without changing existing token/cache keys; the native UI consumes the privacy-safe Core summary and never reads rollout files itself.

## Compatibility, failure, recovery, and rollback

- Settings schema v1/v2 remain readable, and the transitional schema 3 is accepted and persisted back as schema 2. Provenance-free v2 state is backed up to `.v2.bak`; transitional v3 is backed up to `.v3.bak`. Legacy values migrate conservatively as explicit decisions except the old default-off DeepSeek gateway value, which remains eligible for successful local discovery; a schema-3 explicit off remains authoritative.
- Codex index schemas v1-v6 remain readable. Live files rebuild the new spend fields; deleted legacy files keep lifetime tokens but do not fabricate historical cost.
- Corrupt daily buckets or unsupported pricing state cause a safe rebuild from live rollout files. File read failures preserve the previous valid index.
- Bundled Provider installation is additive. Highest valid version selection remains unchanged. Same-version content conflicts and persisted bundle-failure markers block the conflicting version; a failed bundle leaves the app operational and exposes no credential.
- Optional manifest/runtime conflicts and plugin filesystem failures are surfaced as `trust_failed` or `unavailable` without taking down built-ins. Dynamic enablement rolls back the enabled state if runtime validation fails.
- Custom data roots remain test/acceptance-local: they never reconcile the global Run key or start, stop, or signal the global watchdog.
- Rollback is the prior executable and prior Provider package selection. New settings fields and the v2 DeepSeek cache are ignored by older builds; no automatic deletion is required.

## Security, privacy, reliability, accessibility, and localization

- Provider probes may inspect only their documented fixed local credential sources. They must not scan projects or arbitrary `.env` files.
- Credential fingerprints are one-way, provider-local, and never exposed through snapshots or CLI.
- Spend scans remain bounded by the existing line-size limits and retain aggregate local date, model, pricing tier, long-context class, and token counters only. They do not retain session/account IDs, paths, prompts, responses, or raw events.
- The estimate covers logged model-token classes only. It does not infer subscription charges or separately price tools, searches, containers, storage, or other billable services that are absent from the rollout token counters.
- UI strings are localized in Chinese and English and distinguish estimate, partial pricing, unpriced data, and no data. The 30-day history uses four compact summary cards, a recent-emphasis daily chart, amber unpriced markers, and the top three canonical model rows at 96/144/192 DPI. Existing keyboard/focus behavior remains intact, and every Settings page uses the shared themed scrollbar rather than exposing an inconsistent native scrollbar.
- No new external endpoint is added. Price changes arrive through normal signed application updates.

## Implementation order

1. Add settings provenance, the probe contract/protocol, local Provider implementations, and auto-seeding.
2. Add generic bundled-package installation and embed the DeepSeek package in portable builds.
3. Upgrade DeepSeek credential-stamped cache and package version.
4. Extend Codex incremental indexing, add the pricing catalog and period summaries, then wire plugin/UI/CLI output.
5. Add the in-place 30-day spend-history view, bilingual multi-DPI captures, and interaction contracts.
6. Update docs and run focused, full, packaging, restart, and CLI verification.

## Verification

- Focused .NET tests cover settings migration/provenance, local-only probes, process protocol validation, bundle idempotency/fail-closed behavior, filesystem fault isolation, manifest/runtime catalog conflicts including dynamic enablement, credential-stamped caches, pricing arithmetic (including cache writes and GPT-5.4/5.5/5.6 long context), unknown models, local-day windows, 30-day/7-day history projection, canonical model grouping, privacy-safe labels, multi-DPI history layout, append resume, partial-tail recovery, fork replay, historical pruning, and schema migration.
- Node contract tests cover localization, native UI wiring, in-place history/pin/back/no-activate behavior, no new key field, data-root supervision isolation, optional catalog isolation, and distribution contents.
- `npm test`, `npm run verify`, local portable publication, isolated process-package acceptance, `git diff --check`, and complete task-diff review must pass.
- A locally restarted packaged app must keep the DeepSeek Provider automatically available from Harness credentials and show a Codex API-equivalent USD value or a truthful unpriced/no-data state. The CLI snapshot must expose the same result without credentials or conversation content.

Final evidence on 2026-08-30:

- The complete `npm run verify` graduation gate passed `104/104` Node contracts and `109/109` .NET tests, together with native HWND lifecycle, app/CLI publication, isolated process-plugin acceptance, deterministic multi-DPI captures, and diff checks.
- Source, manifest, and packaging declare the bundled DeepSeek Harness Provider as `1.2.3`, with `balance` and `local-credentials`, no credential slots, and no ZGSTokenBar key flow. The final Provider package SHA-256 is `7A3C0ED1A823A01565494D5628E97EF6949F4C77DCDF328B3F632C6F6CA979F0`; same-version conflicts are rejected rather than overwritten.
- Portable UI-only rebuilds can reuse an already verified `1.2.3` package through `BundledPluginPackagePath`, so changing the desktop presentation does not regenerate or relabel the independent Provider payload. The default release path still builds the Provider from source.
- Independent final review covered manifest composition, runtime contributions at startup and dynamic enablement, custom-data-root supervision, marker/catalog enumeration faults, and optional plugin-data creation. After the reviewed fixes and regressions, no P1/P2 finding remains in those paths.
- The restarted portable artifacts are `ZGSTokenBar.exe` SHA-256 `AA5507FF734DF542DCCB750D5FD46EC0556B7EE8FEC0A2D530A5DE72FC3C288C` and ZIP SHA-256 `9FD5C1CEEFD2CFE4E6407A768002D5DE4767A87EB9FC1069B9FD83E6A344A73C`.
- At the first successful CLI status after final packaged startup, the local Codex plugin had already published cached `dataRevision=1`: Today `≈$1642.82419868+`, Yesterday `≈$1276.49020968+`, Last 30 Days `≈$33767.59524960+`, Today `3,391,942,267` tokens, and lifetime `285,388,598,151` tokens. Its health was truthfully `cached` with the persisted observation time rather than startup time. This proves amount and history no longer wait for the full rollout scan. The bounded background refresh then published `dataRevision=2` with `current` health: Today `≈$1680.48622348+`, Last 30 Days `≈$33805.25727440+`, and Today `3,448,095,851` tokens. The partial/`+` marker means these Codex values are lower bounds with additional unpriced use, not complete invoices.
- With the complete Radar surface enabled, Today/30-day API-equivalent spend is presented as a dedicated summary card with a larger primary Today value, grouped thousands, secondary 30-day context, and a muted session count. The card and right-aligned task-cost column remain visible in both `zh-CN` and `en` captures at 96, 144, and 192 DPI without clipping or overlap.
- The amount-card History entry and its in-place 30-day page were captured in `zh-CN` and `en` at 96, 144, and 192 DPI. The history captures verify four summary cards, exactly 30 ordered day bars with recent emphasis and unpriced markers, the top three canonical model rows, and the Back affordance without clipping or overlap.
- The restarted app reported a responsive topmost taskbar window at 192 DPI, a running watchdog, healthy bundled Provider `1.2.3`, and current DeepSeek/Codex snapshots. The borderless taskbar tool window is intentionally omitted from the Windows accessibility window list, so packaged interaction is proven by the native HWND lifecycle gate plus the pin/back/no-activate interaction contracts and deterministic renderer captures rather than accessibility-driven clicking.

## Acceptance criteria

1. A fresh settings root with usable Claude, Codex, or bundled DeepSeek credentials auto-enables exactly those probe-capable Providers without a network request or ZGSTokenBar key entry.
2. An explicit disabled Provider remains disabled across normalize/save/load, app restart, Provider upgrade, and successful local credential detection.
3. Opening and saving Settings without changing a Provider toggle does not convert an automatic state into an explicit decision.
4. The portable app contains and idempotently installs the verified DeepSeek process package `1.2.3` while retaining process isolation; missing/corrupt embedded or installed bytes and same-version content conflicts fail closed without blocking the app.
5. Switching the DeepSeek credential makes the old cached balance unusable. A transient failure with the same credential may show only that credential's last successful balance and original observation time.
6. No raw Provider key or credential-derived value other than the one-way fingerprint appears in settings, host protocol, CLI, diagnostics, localization, or usage indexes.
7. Codex Today, Yesterday, and Last 30 Days token totals are grouped by local calendar date, model, and relevant pricing class without replaying fork/subagent history.
8. Known standard-tier models calculate decimal USD using official uncached-input, cache-read, cache-write, and output rates. Cache classes and reasoning output are not double-counted; documented GPT-5.4/5.5/5.6 long-context multipliers apply only when a request exceeds the recorded threshold and only to eligible model entries. Explicit nonstandard tiers remain unpriced.
9. Unknown/research-preview models retain token counts and produce `Unpriced` or a partial `≈$…+` lower bound, never a false `$0`.
10. Native UI and plugin/CLI snapshots label the value as an API-equivalent estimate rather than an actual subscription charge.
11. Existing quota, token/cache-hit, normal startup/watchdog, Settings scrolling, plugin trust, and named-pipe behavior remain passing; explicit custom data roots do not touch global startup or watchdog state.
12. Settings continue to write schema 2, preserve exact `.v2.bak`/`.v3.bak` migration backups, accept and downgrade transitional schema 3, give legacy default-off DeepSeek one successful automatic discovery opportunity, and preserve every explicit schema-3 off decision.
13. Manifest conflicts, runtime command/settings conflicts during startup or dynamic enablement, bundle markers, plugin-catalog enumeration failures, and optional plugin-data filesystem failures all fail closed without taking down unrelated Providers or the desktop.
14. The graduation gate passes `104/104` Node and `109/109` .NET checks, and the final diff contains only the authorized implementation, its evolving spec, tests, and docs.
15. When measured Codex history exists, the amount card exposes a bilingual History action; opening it shows Today, Yesterday, 7-day, and 30-day values, 30 ordered local-day bars, partial/unpriced markers, and the top three canonical model totals without another data scan or popover.
16. The history and overview render without clipping or overlap in `zh-CN` and `en` at 96, 144, and 192 DPI; the entry click pins the existing no-activate popover, Back restores the overview, and existing Escape/outside/anchor close behavior remains intact.
