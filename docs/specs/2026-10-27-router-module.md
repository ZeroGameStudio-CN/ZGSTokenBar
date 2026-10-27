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
