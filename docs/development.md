# Development

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

- Behavior: deterministic .NET executable tests and Node source contracts.
- Rendering: CLI-generated captures produced by the production renderers.
- Packaged-window properties: `ZGSTokenBar.Cli window inspect`.
- Final gate: `npm run verify`.

If an acceptance requirement has no CLI evidence route, add a focused test, fixture, capture command, or probe before treating it as passed. Starting or replacing a running app is deployment, not acceptance.

The app is self-contained and single-file. Unsigned packages are supported for local use; public releases require Authenticode signing and timestamp verification.
