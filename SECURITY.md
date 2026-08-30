# Security Policy

Enabled ZGSTokenBar Providers read local Claude, Codex, and DeepSeek Harness credentials only to request their documented quota or read-only balance snapshots. The DeepSeek credential stays inside its isolated process Provider and is never returned to the desktop host. Report vulnerabilities through a private GitHub security advisory.

Include the affected version, reproduction steps, relevant files or requests, and redacted logs. Never attach tokens, complete credential files, conversation contents, or personal paths.

The supported security posture is: no telemetry, no cloud sync, no separate credential backup, atomic credential refresh, credential-bound balance caches, disabled-provider network isolation, and an opt-in credential-free Radar feed.
