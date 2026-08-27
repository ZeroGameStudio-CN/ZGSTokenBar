# Provider development

Providers are the extension boundary for data acquisition. The shell consumes typed contributions and does not require a Provider-specific renderer.

## Built-in Provider

1. Create a project under `src/Plugins/ZGSTokenBar.Plugin.<Name>`.
2. Reference `ZGSTokenBar.PluginSdk` and implement the built-in plugin contract.
3. Add a strict `plugin-manifest.v1.json` with a stable `zgstokenbar.*` id.
4. Run `npm run plugins:generate`, then `npm run verify`.

## Process Provider

A process Provider is packaged as `.zgsplugin`. Its manifest declares the executable, protocol, capabilities, files, hashes, timeouts, localization, and optional credential slots. The host communicates over bounded JSON frames on private standard I/O, resolves credentials without exposing them through the local API, and terminates the process with its Job Object.

Provider output uses `MiniCardContribution`, `DetailContribution`, and optional radar-style contributions from `ZGSTokenBar.PluginSdk`. Use stable ids and localization keys; do not hard-code host layout, access application settings directly, or write outside the plugin data root.

See `schemas/plugin-manifest.v1.json` for the package contract and the existing built-ins for minimal examples.
