# Provider development

Providers are the extension boundary for data acquisition. The shell consumes typed contributions and does not require a Provider-specific renderer.

## Built-in Provider

1. Create a project under `src/Plugins/ZGSTokenBar.Plugin.<Name>`.
2. Reference `ZGSTokenBar.PluginSdk` and implement the built-in plugin contract.
3. Add a strict `plugin-manifest.v1.json` with a stable `zgstokenbar.*` id.
4. Run `npm run plugins:generate`, then `npm run verify`.

## Process Provider

A process Provider is packaged as `.zgsplugin`. Its manifest declares the executable, protocol, capabilities, files, hashes, timeouts, localization, and optional credential slots. The host communicates over bounded JSON frames on private standard I/O, resolves credentials without exposing them through the local API, and terminates the process with its Job Object.

A Provider that can reuse credentials from its own fixed local store may declare `local-credentials` and implement `ILocalCredentialProbe`. The probe must use the same credential loader as refresh, perform no network request, and return only a boolean. Process Providers expose the equivalent strict `plugin.probe` operation after the verified handshake. Never return a credential, credential path, fingerprint, account identifier, or diagnostic detail from a probe.

On a fresh settings root, successful local probes seed Provider enablement. Missing credentials are checked again on later starts, while a user's explicit on/off choice always takes precedence. Opening and saving Settings without changing a switch does not convert an automatic state into an explicit choice. This mechanism is discovery, not a second credential store; Provider-specific keys do not belong in ZGSTokenBar Settings.

Provider output uses `MiniCardContribution` and `DetailContribution` from `ZGSTokenBar.PluginSdk`. Use stable ids and localization keys; do not hard-code host layout, access application settings directly, or write outside the plugin data root.

See `schemas/plugin-manifest.v1.json` for the package contract and the existing built-ins for minimal examples.

## Unified usage providers

Mainstream quota readers share one model in `ZGSTokenBar.Core/Providers`: a provider definition, N local accounts, quota windows, prepaid or on-demand credits, a today/7d/30d token ledger, and a freshness or error key. Adding a tool means a catalog row plus a parser and a small read-only fetcher. The named-pipe plugin contracts are unchanged. Claude, Codex, and the DeepSeek Harness balance keep their existing refresh paths and are projected into the same board.

| Provider | Account source | Read |
| --- | --- | --- |
| Grok | `~/.grok/auth.json` entries, `GROK_OAUTH_TOKEN`, or `~/.zgstokenbar/providers/zgstokenbar.provider.grok-local/accounts.json` | `GET https://cli-chat-proxy.grok.com/v1/billing?format=credits`. If that login is missing, `grok usage --json`. Local tokens come from `~/.grok/logs/unified.jsonl` and session `usage.json`. |
| Claude | Existing OAuth refresh for the primary login. Additional `~/.claude/accounts/*.json` files are read-only and are not refreshed. | `GET https://api.anthropic.com/api/oauth/usage` |
| Codex | Existing per-account quota snapshots | `GET chatgpt.com/backend-api/wham/usage` |
| Cursor | Cursor `state.vscdb` `cursorAuth/accessToken`, or an account file | `GET https://cursor.com/api/usage-summary` |
| Antigravity / Gemini | Existing valid `~/.gemini/antigravity-cli/antigravity-oauth-token` | `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`. Expired, missing or failed reads stay unavailable; background refresh never launches `agy` or browser sign-in. |
| GitHub Copilot | GitHub CLI `hosts.yml` | `GET https://api.github.com/copilot_internal/user` |
| DeepSeek | `DEEPSEEK_API_KEY` or an account file. Harness balance still uses its process plugin. | `GET https://api.deepseek.com/user/balance` |
| OpenCode | OpenCode `auth.json` | `GET https://opencode.ai/zen/go/v1/usage` |
| xAI platform | `XAI_API_KEY` plus `XAI_TEAM_ID`, or `~/.xai/config.json` | `GET https://management-api.x.ai/v1/billing/teams/{team}/prepaid/balance` |
| OpenRouter | `OPENROUTER_API_KEY` or an account file | `GET https://openrouter.ai/api/v1/credits` |

Every provider accepts up to 16 accounts. Declared accounts live beside the app as `accounts.json` plus per-account credential files; those files are never written by TokenBar, copied into snapshots, or sent to the local API. Requests are allow-listed HTTPS reads. The only POST is Antigravity's quota summary. Tokens are not redeemed, refreshed, or logged.

The usage panel is a liquid-glass surface. On Windows 11 it asks DWM for acrylic (`DWMWA_SYSTEMBACKDROP_TYPE`) and rounded corners; older Windows falls back to the accent blur policy, and if both are unavailable the panel paints a tinted gradient. Provider groups are frosted cards. Accounts are rows inside those cards, with a collapsible ledger.

The taskbar pill is 44 logical pixels high. Its collapsed row shows up to four signed-in, active accounts; a lower `GlassDockMaxRings` setting is respected. Idle accounts remain reachable through the neutral `+N` count, while signed-out or installed-only placeholders stay in Settings. Quota, balance, warnings and failed quota reads change account information, not primary membership or ordering. Active accounts with zero or unknown quota remain visible. Manual hiding takes precedence, including during expansion.

Taskbar layout notices do not discard a recent clear-space reading merely because another notice arrived during its collection. Such a read can retain only the existing, unchanged seat while it is less than 500 ms old and still has breathing room around Shell targets. It cannot authorize attachment, movement or growth; those require the latest confirmed layout. Actual overlap, unavailable readings and expired evidence keep the safe adjacent fallback.

Settings → Display provides the Codex dock-tier selection: Free, Plus, Pro, and Other/unknown. `CodexDockVisibleTiers` defaults to all four for new and existing settings; an explicit empty selection is allowed. This filter only removes accounts from the dock and `+N`, including its expanded state. It does not change account discovery, quota reads, published data, statistics, conversation history or logs. Filtered accounts remain in Settings for later selection. Codex API service cards and other providers are unaffected. Manual hiding and the normal activity/idle rules still apply after tier selection.

Each provider declares its recognized clients/CLIs in `ProviderDefinition.ActivityProcessNames`. One read-only process snapshot excludes TokenBar and its entire descendant tree, so its own usage probes never count as activity. Codex also requires the account's current binding. API-only accounts without a reliable local activity signal begin in overflow. A failed activity observation preserves the previous state rather than guessing from quota. Stable provider/account identities determine order and capacity selection; quota changes and response ordering cannot rotate the primary four. Expansion retains those positions and survives temporarily empty startup readings.

Router, work intake, CPU/RAM/I/O/GPU and recorded tok/s use the remaining pill area. The collapsed width follows its account and overflow slots; explicit expansion reveals additional accounts. Hover and click retain the shared panel routes. DPI, drag and safe taskbar-gap placement use the same logical layout.

Fixture tests cover the parsers, account cap, secret exclusion, and the light, dark, compact, and taskbar mockups in `docs/providers/assets`. Live calls still need the corresponding local login on a Windows machine. Grok's browser gRPC-web fallback and ACP `grok agent stdio` path are not implemented; the CLI billing JSON is the supported fallback.
