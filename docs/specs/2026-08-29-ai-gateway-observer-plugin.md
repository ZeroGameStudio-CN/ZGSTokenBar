# DeepSeek Harness Provider Plugin

Status: Implemented

Date: 2026-08-29

Last updated: 2026-08-30. The verified `1.1.0` rollout evidence is preserved below. The bundled `1.2.3` auto-discovery, account-bound-cache, and host-hardening extension is governed by `docs/specs/2026-08-30-provider-autodiscovery-codex-cost.md`, where its final gate and local rollout evidence are recorded.

## Summary

Replace the private AI Gateway Observer integration with an independently packaged DeepSeek Provider integration. The process plugin reuses the DeepSeek credential already managed by DeepSeek Harness, calls the official read-only balance endpoint, and publishes only normalized balance data to ZGSTokenBar. The portable build embeds version `1.2.3`, which can be enabled by a local-only credential probe on a fresh settings root. Users do not enter or copy an API key into ZGSTokenBar, and the desktop application no longer depends on the AI Gateway endpoint or observer protocol.

## Goals

- Show the real DeepSeek account balance after an ordinary ZGSTokenBar start without requiring AI Gateway or a running Harness process.
- Make the bundled Provider available from an existing Harness credential without a separate package-install or key-entry step, while preserving an explicit module choice.
- Reuse the credential reference and managed credential already configured in the current user's DeepSeek Harness home.
- Keep DeepSeek endpoint, credential-file, response-schema, and cache knowledge inside one independently packaged process Provider.
- Never persist, serialize, log, or return a provider key through the ZGSTokenBar host protocol.
- Preserve the existing plugin ID so enablement and Mini layout migrate without a second module toggle.

## Non-goals

- Do not expose an API-key field in ZGSTokenBar Settings or copy the key into Windows Credential Manager.
- Do not aggregate the existing Harness `tokenUsage` projection as DeepSeek usage: it is provider-neutral and may include other Providers or forked session history.
- Do not scan arbitrary project `.env` files or session message logs.
- Do not change, deploy, restart, or provision the AI Gateway.
- Do not delete the historical Observer Key from Windows Credential Manager without separate destructive-action authority.
- Do not publish a public package or change Stable in this implementation step.

## Architecture

### Desktop host

- Process plugin ID remains `zgstokenbar.provider.ai-gateway` for state migration; its user-facing name becomes `DeepSeek Harness`.
- The `1.2.3` manifest declares `balance` and `local-credentials`, with no credential slots or Settings contributions.
- The host continues to provide the plugin's private data root and standard bounded process protocol. It does not resolve or transmit a DeepSeek credential.
- The portable build embeds the build-produced `.zgsplugin` bytes. Startup verifies and idempotently installs the version only when it is missing, then uses the existing highest-valid-version selection; it never overwrites a same-version directory.
- On a fresh settings root, the host may invoke the strict `plugin.probe` operation only because `local-credentials` is declared. The probe reuses the refresh credential loader, performs no network request, and returns only a boolean. A user's explicit off or on decision always wins.
- The generic balance card renders the normalized Provider result. If no valid package can be installed or selected, the application makes no DeepSeek or AI Gateway request and continues serving other Providers.
- Optional manifests are composed with the built-ins before discovery or desktop-host construction. Duplicate IDs/namespaces and missing dependencies are rejected. Runtime command/settings conflicts are revalidated on startup and dynamic enablement; the conflicting process is stopped, its contributions are cleared, and only that Provider reports `trust_failed`.
- Plugin-catalog enumeration, bundle-marker, and optional plugin-data filesystem failures degrade to bounded trust/unavailable health. A valid `ZGSTOKENBAR_DATA_DIRECTORY` or explicit `--data-directory` launch never reconciles the global Run key or starts, stops, or signals the global watchdog.

### Harness discovery and credential resolution

- On every probe and refresh, the plugin resolves the Harness home from `DSH_HOME` when explicitly inherited, otherwise from the current user's known profile directory plus `.dsh`.
- The credential reference defaults to `DEEPSEEK_API_KEY`. A simple `llm-deepseek.apiKeyEnv` value in `settings.yaml` may override the reference.
- The plugin reads the Harness-managed `.credentials.yaml` strict top-level mapping first, then the Harness-home `.env` fallback. It does not inspect an invocation project because a login-started tray process has no trustworthy Harness invocation directory.
- Existing Harness process-environment credentials are intentionally not copied through the generic process-plugin host, where doing so would disclose them to every installed process plugin. Users whose credential exists only in a one-off Harness process environment must store it through Harness before this Provider can observe it.
- Credential bytes live only in the isolated plugin process. A probe checks only whether the configured source yields a usable value; a refresh sends it only in the HTTPS `Authorization` header to the fixed official endpoint. Raw credentials are never included in errors, diagnostics, protocol frames, or files.

### Official DeepSeek balance

- Route: `GET https://api.deepseek.com/user/balance`.
- The client disables redirects, cookies, automatic decompression, and system proxy use; applies short connect/call deadlines; and caps the response at 64 KiB.
- It strictly validates `is_available` and `balance_infos`, non-negative decimal strings, and three-letter currency codes. It selects CNY when present, then USD, otherwise the sole valid currency.
- The contribution contains available, topped-up, and granted balance plus the Provider observation time. It contains no request, prompt, response, token, or identity data.

### Last-success cache

- A versioned sanitized JSON cache lives under `ZGSTOKENBAR_PLUGIN_DATA` and contains currency, three balance values, `observedAt`, and a one-way SHA-256 fingerprint of the credential that produced the value. The raw credential is never serialized.
- Successful responses atomically replace the cache. Missing credentials, authentication failures, timeouts, network failures, and malformed responses never overwrite the last successful value.
- A current response reports `current`. When a refresh fails but a valid last-success cache for the same fingerprint exists, the amount remains visible with `cached` health and its original observation time. Missing/unreadable credentials or a different credential cannot authorize the old cache. With neither a current response nor an account-matching valid cache, the plugin publishes no fabricated amount and reports a bounded unavailable state.
- Cache parsing is size-bounded and fails closed if secret- or content-shaped fields such as `apiKey`, `authorization`, `prompt`, or `response` are present.

## Compatibility and migration

- Reusing `zgstokenbar.provider.ai-gateway` preserves the current `PluginEnabled` value, contribution identity, ordering, and Mini layout.
- Package version advanced from `1.0.0` to the verified `1.1.0` Provider replacement. The current extension advances to `1.2.3`, adds local credential discovery, the account-bound cache, and fail-closed host integration, and is embedded in the portable executable; normal highest-version selection remains authoritative.
- App settings continue to write schema 2. Provenance-free schema-2 settings retain exact `.v2.bak` backups; transitional schema 3 is accepted, backed up as `.v3.bak`, and normalized back to schema 2. Legacy enabled DeepSeek remains explicit. The retired gateway's legacy default-off value is left undecided for one successful Harness auto-discovery probe, while a schema-3 explicit off remains disabled.
- The old private endpoint asset, `observer-key` slot, Settings field, and `host.credential.resolve` exchange are removed.
- Historical Observer credentials remain inert and unread. No automatic deletion or repurposing occurs.

## Acceptance criteria

1. With a valid Harness-managed DeepSeek credential, a fresh settings root enables the bundled Provider after a local-only probe, and an ordinary plugin refresh calls only the official balance endpoint and publishes the exact selected-currency amount.
2. Settings shows the `DeepSeek Harness` module switch and no API-key or Observer Key input, clear button, or credential mutation.
3. The manifest has `balance` and `local-credentials`, an empty `credentialSlots` array, no endpoint asset, and version `1.2.3` matching the executable handshake.
4. No protocol request named `host.credential.resolve` is emitted. No raw credential is stored in AppSettings, plugin data, CLI output, localization, or diagnostics; the cache's one-way credential fingerprint is the only permitted credential-derived persisted value.
5. Missing Harness, missing credential, unreadable/invalid credential documents, 401/403, timeout, network failure, oversized body, and invalid balance schema produce deterministic bounded health without showing zero as real data.
6. A valid cached balance remains visible and is marked cached after a transient refresh failure only for the same credential fingerprint; a missing or switched credential, invalid cache, or secret-shaped cache is rejected.
7. Multiple balance currencies choose CNY, then USD; inconsistent, duplicate, negative, or malformed monetary fields fail closed.
8. Provider-neutral Harness token projections are not displayed as DeepSeek usage.
9. Focused tests, bundled and isolated packaged-process acceptance, the complete graduation gate, local portable publication, and diff checks pass from clean exit statuses.
10. The locally installed `1.2.3` package survives a real application restart and a CLI refresh shows either the live amount or a truthful bounded failure without asking for a key.
11. An explicit disabled state remains disabled after credential detection, restart, and Provider upgrade; opening and saving Settings without changing the switch does not turn an automatic state into an explicit decision.
12. Settings write schema 2, preserve exact `.v2.bak`/`.v3.bak` migration backups, accept and downgrade transitional schema 3, leave legacy default-off DeepSeek eligible for one successful automatic discovery, and preserve an explicit schema-3 off decision.
13. Manifest and runtime catalog conflicts, including dynamic enablement, fail closed by rejecting or stopping only the conflicting optional process; commands/settings never leak into the active host catalog.
14. Custom data roots and plugin filesystem faults remain isolated from global Run/watchdog/stop-event state and cannot prevent unrelated Providers or the desktop from starting.

## Verification

- Unit-test Harness home/default reference discovery, safe scalar parsing, missing and malformed credential sources, and absence of serialized secrets.
- Unit-test official response parsing and currency selection with a loopback/fake HTTP handler; cover authentication, timeout, size, and schema failures.
- Unit-test atomic last-success caching, same-credential stale fallback, account-switch rejection, missing-credential rejection, secret-field rejection, and observed-time preservation.
- Build the `.zgsplugin`, install it into an isolated data root, complete handshake/describe/probe/refresh, assert zero Settings fields and zero credential-broker calls, and detect package drift.
- Publish the portable application, inspect the embedded package, and verify missing-version installation plus same-version no-overwrite behavior.
- Run the focused .NET tests, `npm test`, `npm run verify`, local portable publication, local app/CLI smoke checks, `git diff --check`, and complete task-diff review.

## Rollout and rollback

- Local rollout stops the exact running app/watchdog processes, builds the trusted `1.2.3` package into the portable app/CLI, then restarts through the existing watchdog contract. Startup installs only the missing verified bundled version before local discovery.
- Rollback restores the prior portable application and prior valid Provider package state only when explicitly authorized. The new sanitized cache may remain because it contains no raw secret; removing or replacing packages or caches requires explicit scope.

## Decision log

- 2026-08-29: Initially designed a private AI Gateway Observer endpoint and dedicated Observer Key.
- 2026-08-30: User replaced that decision with a DeepSeek Harness Provider integration modeled on OpenUsage-style local credential reuse; the original implementation task was absorbed and archived.
- 2026-08-30: Keep the existing plugin ID for migration, remove all TokenBar credential UI, and put Harness/DeepSeek coupling only in the process plugin.
- 2026-08-30: Prefer a self-refreshing process Provider over a Harness companion service so login-started TokenBar does not depend on Harness uptime.
- 2026-08-30: Ship exact account balance first and omit mixed Provider usage rather than mislabeling it as DeepSeek-only data.
- 2026-08-30: Preserve the last successful sanitized balance indefinitely but always expose its observation timestamp and cached health; this is a reversible availability default that never represents stale data as current.
- 2026-08-30: Advance the current package to `1.2.3`, embed the verified bytes in the portable application, and add a boolean local-only probe so existing Harness credentials require neither manual package installation nor duplicate key entry. Never reuse a same ID/version label for different package contents.
- 2026-08-30: Bind cached balance fallback to a one-way credential fingerprint so an account switch cannot display the prior account's value. Explicit Provider decisions override automatic discovery.

## Verified `1.1.0` implementation evidence

- Final package: `ZGSTokenBar.Plugin.AiGatewayObserver-v1.1.0.zgsplugin`, SHA-256 `7d2691634afc79700598e45ec13c15f87dce225323c6ba5f131a46e0389bfbe8`; verified-package acceptance passed.
- Complete graduation gate passed after the final credential-normalization change: 94 Node contract tests, 100 .NET tests, native window lifecycle, NativeAOT app/CLI publishing, isolated process-plugin acceptance, capture generation, and diff checks.
- Local `1.1.0` installation survived a watchdog restart. Desktop CLI refresh returned `current` health and the exact CNY balance from the official endpoint; runtime manifest inspection showed no Settings contributions and no credential slots.
- The persisted last-success cache matched the strict balance-only schema and contained no API-, key-, authorization-, token-, prompt-, or response-shaped field.
- The desktop process was responsive, visible, topmost, and registered under the current-user Windows Run key with the published executable plus `--watchdog`.

## Verified `1.2.3` extension evidence

- Source, manifest, and packaging declare `1.2.3`, `balance`, and `local-credentials`, with no credential slots or TokenBar key flow. The final Provider package SHA-256 is `7A3C0ED1A823A01565494D5628E97EF6949F4C77DCDF328B3F632C6F6CA979F0`; the same-version no-overwrite rule remains fail closed.
- The complete graduation gate passed `100/100` Node contracts and `106/106` .NET tests, together with native HWND lifecycle, app/CLI publication, isolated process-plugin acceptance, deterministic multi-DPI captures, and diff checks.
- Regression coverage verifies schema-2 provenance and schema-3 downgrade, the legacy DeepSeek migration exception, data-root supervision isolation, plugin-catalog/filesystem fault degradation, manifest/runtime conflicts at startup and dynamic enablement, and removal of rejected runtime contributions. Independent final review found no remaining P1/P2 issue in these paths.
- The restarted portable artifacts are `ZGSTokenBar.exe` SHA-256 `4C6B86BA9531D96149C5E8AE31DC6328B57F0421FDF44896BE4BC6CBD8462E43` and ZIP SHA-256 `D23C2AF80A0EF917E1C423FCA9C3C8295C6DD87F863149FF29263FBFC0DCCEF9`.
- The final live Provider snapshot returned DeepSeek `¥86.42` without a TokenBar key. The same restart reported Codex Today `≈$1158.15126016+`, Yesterday `≈$1276.49020968+`, and Last 30 Days `≈$33282.92231108+`; partial/`+` denotes a lower bound because some observed usage remains unpriced.
- With the complete Radar surface enabled, Today/30-day API-equivalent spend is presented as a dedicated summary card with a larger primary Today value, grouped thousands, secondary 30-day context, and a muted session count. The card and right-aligned task-cost column remain visible in both `zh-CN` and `en` captures at 96, 144, and 192 DPI without clipping or overlap.
