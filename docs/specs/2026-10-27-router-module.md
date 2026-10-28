# Native Router module

The user approved moving the Router's human interface into Token Bar on 2026-10-27.
Router remains an independently installed executor. Token Bar is an optional local
client; neither application's lifetime, settings nor release owns the other's jobs.

Use the existing fixed loopback monitor and public Editor-job controls. Do not read
private journals in C#, launch Unity, duplicate scheduling/recovery rules, introduce
a source freeze, or infer release from a cancellation acknowledgement.

The native module must cover project selection, current execution and freshness,
source chat, queue paging and guarded ordering/cancellation, qualified stop and
cleanup inspection, filtered/paged history, timings/statistics and CSV export.
Failed reads retain unavailable/stale state. Lost control replies are observed on
the original request; controls are never automatically retried.

Migration: publish a versioned monitor API while retaining the page; verify the
native implementation with synthetic HTTP/control fixtures and production-renderer
captures; install the native app and verify live read-only state. Only then remove
the page and NiceGUI dependency. Keep the same backend entry, address, queue IDs,
receipts and CLI recovery route. Rollback replaces presentation code, never jobs.

Acceptance includes app-off/job-on independence, stale and incompatible-server
states, original-instance/request/project binding, stale queue refusal, safe stop
qualification, pagination beyond the first page, filter/statistic parity, bounded
payloads, and DPI rendering. Production controls are not exercised on others' jobs.

The user's refined presentation contract is an icon-only bar module with a small
queue count, matching the existing Codex hover/pin interaction. Hover opens an
anchored, borderless preview without activation; clicking the module pins it.
Leaving both anchor and popup dismisses the preview after the shared grace period;
outside click/Escape dismisses a pinned view. Execution controls require pinning.
Reuse the bar's hover timer, pointer supervision, screen-edge placement and palette.
Keep task names and state explanations in the popup rather than the compact bar.

The 2026-10-28 visual refinement keeps this interaction and transport contract.
Use the existing palette with a distinct current-task summary, quiet rounded
toolbars, a dark search field, lighter horizontal table separators and semantic
result badges. Keep complete timestamps in row details/hover while showing a
compact local date/time in the table. Idle views disable irrelevant actions;
unavailable observations keep their explicit warning and disabled controls.
Verify queue/history/hover/unavailable/empty states at 96/144/192 DPI, including
header and viewport bounds, and one additional existing palette. All fixtures
use synthetic data; production execution controls remain outside visual checks.

The user's later refinement requires a single compact page like the Codex history
surface, with current execution, queue and recent records visible together. No
tab navigation or scrollbars: render only the number of rows fitting each
viewport and page older records through the existing bounded API. Keep current
execution actions in the existing dark context menu, queue actions/pagers as
small glyphs, and filters collapsed until requested. Show execution duration by
default; retain waiting/cleanup timings in row hover and original detail data.
Replace the project dropdown with flat short labels for the current projects.
Highlight selection, retain pending counts and full-name hover text, and switch
using the original opaque project key. Keep a compact More menu for a larger
catalog so every registered project remains reachable without a scrolling strip.

The subsequent readability/performance change uses a short human-readable method
title, a separate native-progress line and a separate source-chat line. Preserve
the exact task/source in hover and accessibility descriptions. Queue navigation
reads fresh pages directly, independently of full snapshot polling. History keeps
one bounded 100-record read-only window per instance/project/filter context and
slices adjacent pages locally; share matching in-flight reads and cancel obsolete
reads only. Refresh the first page every ten seconds, preserve older snapshot
ordering during navigation, and retain explicit refresh with snapshot timestamps.
No cached history can authorize controls. Verify local page hits, window bounds,
context changes, rapid navigation, read cancellation, DPI layout and a live
read-only paging benchmark. The Router API and scheduling remain unchanged.
