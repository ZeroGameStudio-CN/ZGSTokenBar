# Features

- Docked or floating compact Mini with topology-aware placement.
- Claude and Codex quota windows with reset time, health, caching, and pace estimates.
- Bundled DeepSeek Harness balance Provider with local credential discovery, no duplicate API-key entry, and account-bound cached fallback.
- Codex multi-account display ordered by plan and remaining quota.
- Local Codex token and cache-hit summaries plus Today, Yesterday, and 30-day API-equivalent USD estimates without conversation-content retention.
- Recent Codex conversations show whole-turn average output tokens/s, generated output and elapsed time, with project labels, pagination and recorded first-token latency in the tooltip. This includes reasoning, tools and waiting; it is not streaming decode speed. The visible popover refreshes incremental local measurements every five seconds without rescanning the lifetime ledger. Large conversations catch up progressively; interrupted and dormant sessions have explicit states.
- Codex Radar switches between the current Codex-harness trial and the historical 150-point feed, displaying the trial's /100 scale, scored sample count and source timestamp. API snapshots preserve the same separation. Small trials do not feed recommendations; DeepSeek keeps its historical measurements.
- Exact known-model pricing accounts for uncached input, cache reads, cache writes, output, and proven long-context requests. Unknown or unsupported pricing remains visible as unpriced or as a partial `≈$…+` lower bound.
- Local CPU, memory, disk, and GPU metrics.
- Start-at-login registration.
- Reorderable, collapsible, independently resizable Mini areas.
- Typed built-in and process Provider contracts with strict validation.
- Current-user named-pipe API and a single-file NativeAOT CLI.
