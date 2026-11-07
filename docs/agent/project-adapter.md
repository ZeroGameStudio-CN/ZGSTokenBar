# Development content adapter

- Project ID: `zgstokenbar`.
- Writable registry: `docs/agent/development-content-plan.json` (portable JSON schema from the `development-content` skill).
- Status mapping: use the schema's `stage` and `availability` tokens directly.
- Preview entry: run the local portable app; the two collapsed taskbar cards open their own quota popup and can be switched independently in Settings.
- Release evidence: repository commit and CI result, plus the installed executable and live `ZGSTokenBar.Cli window inspect --json` module IDs. Source submission or a local build alone is not a public release.
- Writer/checks: edit the local JSON record with unrelated records preserved; validate JSON, unique IDs, target references and diff before handoff.
