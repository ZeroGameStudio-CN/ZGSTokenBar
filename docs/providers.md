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

Provider output uses `MiniCardContribution`, `DetailContribution`, and optional radar-style contributions from `ZGSTokenBar.PluginSdk`. Use stable ids and localization keys; do not hard-code host layout, access application settings directly, or write outside the plugin data root.

See `schemas/plugin-manifest.v1.json` for the package contract and the existing built-ins for minimal examples.

## Unified usage providers

Mainstream quota readers share one model in `ZGSTokenBar.Core/Providers`: a provider definition, N local accounts, quota windows, prepaid or on-demand credits, a today/7d/30d token ledger, and a freshness or error key. Adding a tool means a catalog row plus a parser and a small read-only fetcher. The named-pipe plugin contracts are unchanged. Claude, Codex, and the DeepSeek Harness balance keep their existing refresh paths and are projected into the same board.

| Provider | Account source | Read |
| --- | --- | --- |
| Grok | `~/.grok/auth.json` entries, `GROK_OAUTH_TOKEN`, or `~/.zgstokenbar/providers/zgstokenbar.provider.grok-local/accounts.json` | `GET https://cli-chat-proxy.grok.com/v1/billing?format=credits`. If that login is missing, `grok usage --json`. Local tokens come from `~/.grok/logs/unified.jsonl` and session `usage.json`. |
| Claude | Existing OAuth refresh for the primary login. Additional `~/.claude/accounts/*.json` files are read-only and are not refreshed. | `GET https://api.anthropic.com/api/oauth/usage` |
| Codex | Existing per-account quota snapshots | `GET chatgpt.com/backend-api/wham/usage` |
| Cursor | Cursor `state.vscdb` `cursorAuth/accessToken`, or an account file | `GET https://cursor.com/api/usage-summary` |
| Antigravity / Gemini | `agy` output, or `~/.gemini/antigravity-cli/antigravity-oauth-token` | Local `/usage` text, otherwise `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary` |
| GitHub Copilot | GitHub CLI `hosts.yml` | `GET https://api.github.com/copilot_internal/user` |
| DeepSeek | `DEEPSEEK_API_KEY` or an account file. Harness balance still uses its process plugin. | `GET https://api.deepseek.com/user/balance` |
| OpenCode | OpenCode `auth.json` | `GET https://opencode.ai/zen/go/v1/usage` |
| xAI platform | `XAI_API_KEY` plus `XAI_TEAM_ID`, or `~/.xai/config.json` | `GET https://management-api.x.ai/v1/billing/teams/{team}/prepaid/balance` |
| OpenRouter | `OPENROUTER_API_KEY` or an account file | `GET https://openrouter.ai/api/v1/credits` |

Every provider accepts up to 16 accounts. Declared accounts live beside the app as `accounts.json` plus per-account credential files; those files are never written by TokenBar, copied into snapshots, or sent to the local API. Requests are allow-listed HTTPS reads. The only POST is Antigravity's quota summary. Tokens are not redeemed, refreshed, or logged.

Fixture tests cover the parsers, account cap, secret exclusion, and the light, dark, and compact board mockups in `docs/providers/assets`. Live calls still need the corresponding local login on a Windows machine. Grok's browser gRPC-web fallback and ACP `grok agent stdio` path are not implemented; the CLI billing JSON is the supported fallback.
