# Privacy and security

ZGSTokenBar is local-first. It has no telemetry or cloud sync.

Built-in Claude and Codex integrations read their official local CLI credential stores and contact only the corresponding provider APIs while enabled. The bundled DeepSeek process Provider reads only the documented credential locations in the current user's DeepSeek Harness home and sends the credential only to the fixed official balance endpoint. Provider discovery reuses those same credential loaders, performs no network request, and returns only whether a usable local credential exists. Raw credentials are not copied into settings, caches, host protocol messages, CLI output, logs, or the repository.

The DeepSeek last-success cache contains sanitized balance fields, the original observation time, and a one-way SHA-256 credential fingerprint. The fingerprint prevents a balance obtained with one credential from being shown after an account switch; it is never exposed through the host snapshot or CLI. Missing or unreadable credentials cannot authorize cached fallback.

Local Codex rollout logs are scanned on the machine, and only bounded date/model/pricing-class/token aggregates are retained. Prompt and response text, file paths, session IDs, and account IDs are not retained in the usage index. The displayed USD value is an API-equivalent estimate from a bundled dated price table, not a subscription invoice or proof of an amount charged by OpenAI.

Application state is stored under `%APPDATA%\ZGSTokenBar`. Explicit plugin credential slots use Windows Credential Manager targets under `ZGSTokenBar:plugin:<plugin-id>:<slot>`. The current-user named-pipe API never returns credentials.

Process plugins are installed from either an explicit local `.zgsplugin` package with an explicit digest or a package embedded in the application build. The bundled DeepSeek package is installed only when its version is missing; an existing same-version directory is never overwritten. Archive paths, sizes, declared files, hashes, schemas, timeouts, and post-install drift are validated. Plugin processes are attached to a kill-on-close Job Object, but they still run with the current user's permissions; install only trusted packages.

Disabled Providers make no Provider requests. The public build contains no organization-private endpoints, credentials, deployment topology, or machine-specific paths.
