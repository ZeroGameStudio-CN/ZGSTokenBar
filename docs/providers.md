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
