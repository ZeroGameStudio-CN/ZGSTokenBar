# Privacy and security

ZGSTokenBar is local-first. It has no telemetry or cloud sync.

Built-in Claude and Codex integrations read the official local CLI credential stores and contact only the corresponding provider APIs while enabled. Credentials are not copied into settings, caches, logs, or the repository. Local Codex session logs are used only for bounded usage counters; conversation text is not retained.

Application state is stored under `%APPDATA%\ZGSTokenBar`. Explicit plugin credential slots use Windows Credential Manager targets under `ZGSTokenBar:plugin:<plugin-id>:<slot>`. The current-user named-pipe API never returns credentials.

Process plugins are installed only from an explicit local `.zgsplugin` package with an explicit digest. Archive paths, sizes, declared files, hashes, schemas, timeouts, and post-install drift are validated. Plugin processes are attached to a kill-on-close Job Object, but they still run with the current user's permissions; install only trusted packages.

Disabled Providers make no Provider requests. The public build contains no organization-private endpoints, credentials, deployment topology, or machine-specific paths.
