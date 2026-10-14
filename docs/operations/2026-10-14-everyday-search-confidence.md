# Everyday search confidence

Status: focused local verification passed; canonical feature closeout, deployment
and final frozen-workload acceptance remain pending.

This Release 1 increment adds visible waiting and indexing/fallback status to the
search page. Pending searches prevent duplicate submissions. Refusals remove old
results and leave the form usable. Available fallback results remain visible with
their reason; empty degraded retrieval is not presented as a successful empty search.
MCP, REST and CLI retain their existing retrieval and citation contracts.

A fresh operational audit found a managed host stop after SQL pre-login error
`-2146893055` escaped retained-artifact inspection. The retained-processor deferral
policy now recognises only that additional error alongside its existing command
timeout/deadlock cases. It disposes the failed scope, waits the existing 30 seconds
and revalidates durable work in a fresh iteration. Mixed permission/integrity errors
and unclassified faults retain their existing refusal/StopHost behaviour. No SQL
connection-security setting, terminal job, checkpoint or native ownership record
is reset or replayed by this correction.

The inspection-stage regression failed before the correction and passed afterwards:
seven focused checks passed, including no promotion, claim or content read during
the failed inspection, scope disposal, later reconciliation and cancellation. A real
browser regression also failed before the UI correction and passed afterwards,
covering waiting, duplicate submission, indexing/busy/timeout/unavailable fallback,
refusal and a subsequent successful search.

Restore exposed advisories published on 13 October for transitive
`System.Security.Cryptography.Pkcs` and the test SignalR client. The central package
versions use the minimum patched servicing versions, 9.0.21 and 10.0.13 respectively;
restore then passed without suppressing vulnerability checks. See the upstream
[PKCS advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-846f-f764-76mx)
and [SignalR advisory](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-334f-vvp4-7g6p).

One bounded attributed two-caller diagnostic returned hybrid-ready in 13.0 and
15.2 seconds, with 20 exact current passages, matching cited reads and known native
result/committed cleanup. The root request completed first, so the conditional third
request remained unstarted. The observer completed with zero trace loss. A stale
generation literal in post-processing was corrected against the saved trace only;
no request was repeated. This did not reproduce or establish the historical timeout
cause and does not satisfy the full frozen 40-search gate. No speculative inference,
scheduler or dense-retrieval optimisation is included.

Independent review supports bounded continued delivery while keeping the native
IIS heap-crash risk unresolved. The preserved dump establishes detection during
native HTTP request-cache cleanup on listener stop, not the first invalid free/write
or its responsible component. Managed readiness/deferral fixes and successful
searches do not prove prevention. New crash, integrity, ownership or native-cleanup
uncertainty stops the affected operation; exact payload rollback remains subject
to the existing ownership and cleanup checks.

The unchanged acceptance contract remains 20 frozen queries twice at two callers:
40/40 hybrid-ready responses, exact current scope/citations/readback, stable settled
bindings, p95 strictly below 20 seconds and the existing 25-second outer deadline.
Current full method and documentation reads must also pass through MCP, REST and
CLI. Preserve first-pass failures; do not repeat an unchanged failed workload.

Release 2 retains workspace lifecycle acceptance and refreshed eligibility for the
exact 17 historical generations. Nothing in this increment stages or deletes that
set. Release 3 retains document fidelity/OCR retry and selected mail capture. Models,
source content, canonical embeddings, checkpoints, scheduled task/hidden launcher
and recovery evidence remain protected. Dashboard manuals are outside this increment.
