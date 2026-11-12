# Features

- Docked or floating compact Mini with topology-aware placement.
- Claude and Codex quota windows with reset time, health, caching, and pace estimates.
- Separate, default-collapsed Grok and Antigravity cards have independent Settings switches. Antigravity reads its model quota through the signed-in local CLI; Grok reports unavailable when its local CLI cannot expose plan usage.
- Bundled DeepSeek Harness balance Provider with local credential discovery, no duplicate API-key entry, and account-bound cached fallback.
- Codex multi-account display ordered by plan and remaining quota.
- Local Codex token and cache-hit summaries plus Today, Yesterday, and 30-day API-equivalent USD estimates without conversation-content retention.
- Recent Codex conversations use a paged table backed by bounded local turn summaries: model, new input, output, cache hits, estimated cost, average output tokens/s and recorded first-token latency. Project, state and timing details are available on hover. Average speed includes reasoning, tools and waiting; it is not streaming decode speed. Local measurements refresh incrementally every five seconds without rescanning the lifetime ledger. Cold starts read a complete recent turn directly; long turns catch up progressively. Prompts and responses are never retained.
- Exact known-model pricing accounts for uncached input, cache reads, cache writes, output, and proven long-context requests. Unknown or unsupported pricing remains visible as unpriced or as a partial `≈$…+` lower bound.
- Local CPU, memory, disk, and GPU metrics.
- Start-at-login registration.
- Reorderable, collapsible, independently resizable Mini areas.
- Typed built-in and process Provider contracts with strict validation.
- Current-user named-pipe API and a single-file NativeAOT CLI.
