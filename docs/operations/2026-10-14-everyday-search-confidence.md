# Everyday search confidence

Status: the increment is deployed as `60b9dc0a`; frozen-workload acceptance failed
and remains open. Further testing and diagnostics must use an isolated staging
deployment. Production actions require fresh, explicit user approval.

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

Canonical feature closeout passed all 21 steps. Each checkout passed 3,211 tests
with zero failures, 24 default opt-in skips and zero build warnings. The new browser
case passed separately. Independent review approved the change and exact incremental
deployment. Installed application bytes matched the reviewed candidate. The first
companion comparison omitted the updater's existing runner; the sole additional
file matched the repository and preserved predecessor, and independent review
cleared the corrected comparison. No deployed bytes were changed for that correction.

After deployment, six exact current full method/documentation reads passed across
MCP, REST and CLI. All 1,108 eligible tracked files were published with matching
current bytes. These observations preceded the later production approval boundary.

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

The single subsequent frozen run stopped after three searches: two hybrid-ready
responses in 14.7 and 13.8 seconds, and a semantic timeout with lexical fallback in
20.9 seconds. The other 37 observations remained unstarted. This does not meet
the 40/40 or p95 acceptance gates. During the bounded observer, the user required
all further trials and diagnostics to move to a separate staging deployment.
Only the agent-owned runner and collector were terminated. No later production
probes or native accounting were performed, so post-run native cleanup remains
unverified. Retain the failed observations and incomplete trace; do not claim
timeout causation, native cleanup or crash prevention from this run.

Release 2 retains workspace lifecycle acceptance and refreshed eligibility for the
exact 17 historical generations. Nothing in this increment stages or deletes that
set. Release 3 retains document fidelity/OCR retry and selected mail capture. Models,
source content, canonical embeddings, checkpoints, scheduled task/hidden launcher
and recovery evidence remain protected. Dashboard manuals are outside this increment.

Isolated staging now has its own database, restricted runtime login, private
filesystem, process and loopback endpoint. One synthetic workspace passed folder
preview, durable scan/publication, browser search and exact current REST/CLI/MCP
citation/readback after the documented text normalisation. This initial host has
real semantic models, GPU, OCR and mail disabled. It is a functional baseline;
the frozen hybrid workload and production risk disposition remain open. See the
[staging procedure](2026-10-14-isolated-staging.md) for its explicit bindings and
the separate restricted disposable-SQL test path.
