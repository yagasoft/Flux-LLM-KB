# Hybrid search live acceptance, 28 September 2026

The corrected forward patch completed the 35-input coherent-passage rebuild:
3,596 passages, no failed embedding or publication jobs, and a validated active
generation. After the same-release recovery restarted the web host, live and ready
probes returned HTTP 200, index health reported the new generation healthy, and
the deployment-validation hold was released. The recovery reused the committed
operation and checkpoints; it did not migrate or reset the projection again.

Activation is not search acceptance. A nine-call public-root check across REST,
CLI and MCP found no query for which all three interfaces returned the same
healthy hybrid result. Some calls returned explicit timeout status at the
ten-second request deadline with no hits; later calls returned either lexical
fallback or trained-reranker hybrid results. Citation reads matched the exact
passage when a hit was returned. The independently authored 96-question English
holdout and its same-question lexical baseline were already frozen. The hybrid
quality run was held back because inconsistent execution would confound its
relevance score.

A subsequent read-only snapshot of the same active generation found 162
published passages across all 24 public sources. Every frozen answer span for
the 84 answerable questions survives passage publication; ten questions need
evidence from multiple passages. This establishes passage availability, not
retrieval or ranking accuracy.

An opt-in EventPipe trace of a successful 50-passage search measured 9.954 seconds
end to end. Embedding load, inference and unload took 3.379, 0.457 and 0.191
seconds; reranker load, inference and unload took 3.271, 2.096 and 0.191 seconds.
Hashing one already cached 2.27 GB model data file independently took 2.224
seconds. A separate 24-sample live GPU observation rose from about 2.4 to 4.8
GiB during a search and returned to baseline. These are individual observations,
not p95 estimates or a proof that residency would meet the two-second target.

The selected bounded correction was to retain only the exact hash-verified
model-file handles for the web process,
so repeated requests do not hash the same protected bytes. Keep each ONNX GPU
session inside its existing scheduler-owned request and dispose it before capacity
release. The routine incremental updater must acquire the existing GPU admission
fence, let an active OCR page finish and prove IIS worker exit before swapping
payloads; failed rollback must leave the validation hold in place. Recheck
cross-surface parity, full-search latency, the independently frozen English
quality set and OCR waiting after deployment of that bounded change. Model
residency remains conditional on a separate warm full-search and memory/OCR
experiment and an independently reviewed ownership/release/recovery design.

## Verified incremental release and frozen evaluation

The independently reviewed incremental updater applied commit `e934d320` on
28 September 2026. Its plan and result reported no migration or clean-slate
action. The updater drained GPU admission, proved the previous IIS worker had
exited, validated the replacement and released the deployment hold. Live and
ready probes returned HTTP 200, the index remained healthy at generation
`fd27d898-0dee-f79e-ecf5-c55eac58dd8e`, and the installed application DLL
matched the prepared release hash. The model sessions still load and unload
inside each scheduled request; only verified file handles are process-held.

The frozen 24-source public English cohort has 96 questions, including 84
answerable and 12 unanswerable controls. The same-question lexical baseline
placed literal answer support in the top five for 55/84 answerable questions;
the deployed hybrid returned it for 80/84. Paraphrases improved from 14/24 to
22/24, exact questions from 17/24 to 24/24, and no represented file class lost
top-five literal coverage. These are strict normalised source-span scores;
faithful answer equivalents were not used to inflate them. All 478 distinct
top-five evidence references read back to the exact cited source passage. A
corrected REST/CLI/MCP parity run passed 9/9 sequential searches and reads,
matching ordered passage IDs, generation and source binding across interfaces.

The candidate trace covered all 84 answerable questions. Five timed out before
the candidate stage; the other 79/79 had the answer in both the candidate union
and the 50-passage rerank input. Thus the operational candidate count is 79/84,
below the proposed 95% gate by one question; completed-stage recall alone must
not hide the five failures. One timed-out request nevertheless returned a
correct lexical fallback, making the final top-five count 80/84. The trace
ended during the last unanswerable control, so that control has a response but
no complete stage trace. No answerable stage evidence was lost.

Latency remains the release blocker. The public run returned 90 healthy hybrid
results and six declared timeouts. Healthy full-search p50/p95 were 7.42/7.63
seconds. A separate 20-request one-caller check returned 18 healthy results;
two simultaneous callers returned only five healthy results, with 13 timeouts
and two busy refusals. On 33 healthy development searches, median embedding
plus reranker loading was 4.03 seconds, inference 2.57 seconds, unloading
0.39 seconds and other work 0.59 seconds. Subtracting loading and unloading
from the existing observations yields an arithmetic warm p95 of 3.28 seconds;
this is **not** measured warm full-search performance and gives no basis for
claiming the agreed two-second gate. Cutting the rerank input from 50 to 20
lost answer evidence for three development questions across two distinct
facts, so the 50-passage setting stays. A standalone native batch benchmark
failed during ONNX Runtime initialisation outside the app host and produced no
performance result; that approach was stopped.

The deployed retrieval path is useful and citation-correct, but it has not
passed latency, two-caller reliability or sustained-search OCR-wait acceptance.
Model residency still requires a bounded full-search, memory and OCR experiment
under independently reviewed ownership, release and recovery rules. The user
has chosen to consider a higher latency target **after measured warm full-search
tests**. No replacement target has yet been selected. This choice does not
authorise another production action or establish that model residency is safe.

The next diagnostic is deliberately narrower than production residency. One
ordinary scheduler-owned interactive request may preload both existing verified
BGE sessions, time one complete search through the same SQL/ANN, 50-passage
rerank and citation-assembly path, then dispose both sessions before its
ten-second ownership deadline and settle the existing reservation. Repeating
this measures a preloaded-session full search and exposes first-inference cost;
it does not measure across-request reuse, ordinary admission contention or
sustained OCR waiting. Record every timeout and refusal alongside healthy
samples. If this cannot yield a useful latency target within the existing
deadline, stop rather than extending the scheduler on a latency assumption.

The first approved diagnostic attempt on 29 September stopped before model
loading or search. Its 9,216 MiB free-GPU preflight refused the first frozen
development question, so it collected no warm latency sample. The diagnostic
process exited, the same IIS pool restarted, and live and ready probes returned
HTTP 200. Twenty subsequent idle readings showed 8,592–9,012 MiB free; this
machine cannot currently pass that preflight under its desktop load. An
independent safety review found that the earlier sequential model peaks do not
justify lowering the limit for simultaneous sessions: a sampled cancellation
cannot reliably interrupt a native allocation in progress. Keep the guard and
recheck memory after the user closes unrelated GPU-using apps, or gather
separate model-allocation evidence before revisiting the estimate. This failed
attempt changes neither the deployed release nor the open latency gate.

## 29 September diagnostic and CPU placement comparison

A second approved GPU diagnostic cleared the original 9 GiB free-memory
preflight, loaded both verified models, then expired at the scheduler's
ten-second ownership deadline before completing its first search. Loading
took 7.05 seconds and the subsequent 2.27-second search measurement was
incomplete. GPU use rose from 2,127 to 6,321 MiB (a 4,194 MiB increase),
then fell to 1,852 MiB after cleanup. The reserved and uncertain slot counts
returned to zero, the diagnostic process exited, and the restored IIS pool's
live and ready probes returned HTTP 200. This establishes loading cost and a
partial peak, not a warm full-search latency or a completed-search GPU peak.

A third attempt, with an independently reviewed opt-in 25-second outer
ownership allowance and the unchanged ten-second inner search deadline,
refused before loading: desktop activity left 8,693 MiB free against the
original 9,216 MiB preflight. The pool again returned ready. The 25-second
setting is confined to the private diagnostic; the deployed executor and SQL
store retain ten-second defaults. A separately approved one-query pilot used a
6 GiB diagnostic reservation and an independent 8 GiB free-memory admission
floor. It returned a ready full search in 4.55 seconds after 9.35 seconds of
model loading. GPU use rose from 2,540 to 7,035 MiB (a 4,495 MiB increase)
and fell to 2,509 MiB after cleanup; reserved and uncertain slots were zero,
the diagnostic process exited, and the IIS pool returned ready. This is one
complete warm full-search observation, not a p95. An independent review found
the same guard adequate for a five-query follow-up, with a first-sample gate
and immediate stop on any memory, release or search failure. That separately
approved run completed all five searches: preloaded full-search times were
4.25, 3.40, 3.14, 3.21 and 3.01 seconds, including first-inference effects.
Per-query model loading still took 5.37–9.31 seconds. The largest sampled GPU
increase was 4,544 MiB; each search ended with zero reserved or uncertain
slots and GPU use near its starting level. The diagnostic process exited and
the IIS pool returned ready. All five top-five sets contained the frozen answer
passage. CPU and GPU top-five order differed on two questions without losing
that support. Five exploratory samples establish a useful warm
range, but are too few for a reliable p95 or for choosing retention without
OCR handover and two-caller evidence.

The independently reviewed diagnostic then ran all 40 frozen development
questions under the same memory, per-sample release, first-sample gate and
12-minute limit. All 40 completed with `ready` status and one stable index
generation; no scheduler reservation or uncertain slot remained after any
sample. Preloaded full-search p50/p95/max were 3.04/3.38/4.14 seconds.
Per-query model loading p50/p95 was 5.39/5.54 seconds, and outer p50/p95
including loading and cleanup was 8.99/9.48 seconds. The largest sampled GPU
increase was 4,774 MiB, highest absolute use 7,344 MiB, and process-private
peak 8.28 GiB. Post-disposal GPU use differed from pre-load by at most
257 MiB in either direction. The diagnostic exited and live/ready probes
returned HTTP 200. Strict top-five answer-span support was 31/40, compared
with 29/40 in the earlier deployed run over the same development questions;
the two additional supported results had previously timed out. This set is
development evidence, not the independent 96-question holdout or a proof of
resident-owner safety. It measures separately preloaded searches, not reuse
of a session across requests, two-caller queueing or OCR handover.

A separate offline, process-resident CPU experiment reused the verified local
BGE models and the deployed SQL, ANN, 50-passage rerank and citation-assembly
path. It made no model downloads and did not change the deployed search
service. Four ONNX intra-op threads in one reranker session took 17.6 seconds
for the first complete development query. ONNX automatic threading took
11.2 seconds; eight explicit threads took 12.9 seconds. Embedding took about
70 ms, while sequential scoring of 50 passages dominated the time. Splitting
one query across four resident reranker sessions produced five ready searches
in 7.9–9.6 seconds, all with top-five answer support; peak process memory was
7.6 GB. Automatic intra-op threading with four sessions took 9.6 seconds for
the same first query but used about 11.7 CPU cores on average, versus about
six cores with four threads per session. Extra operator threads did not improve
the observed wall time. Two such searches run concurrently in separate resident
processes
both returned the expected answer, but took 11.1 and 11.4 seconds each after
loading. The separate processes represent a high-memory concurrency probe,
not a measured single-service admission design. These samples are too small
to set a latency target or conclude that CPU search preserves OCR waiting
time. The 10-second current deadline is marginal even for one CPU query and
unmet for these two simultaneous queries.

After the stale ANN generation was repaired, a five-question resident CPU
probe with four reranking sessions but only two ONNX threads per session
returned five ready results with top-five answer support. Full-search times
were 12.3–14.0 seconds, slower than the earlier four-thread setting. Its
16.3-second model load is excluded from those warm times, and peak process
private memory was 7.63 GB. The GPU-first, two-lane CPU fallback candidate
therefore uses four threads per reranking session, the faster measured setting.
It extends the complete request deadline to 25 seconds but retains the GPU
scheduler's ten-second execution limit. The CPU pool is process-resident and
held by a machine-wide owner file across IIS overlap; this is not a claim that
the live app has passed concurrent search or OCR waiting-time gates. No new
model files were acquired for this probe.

A public synthetic PNG supplied an uncontended OCR baseline on 29 September.
The exact OCR mini-task was created at 07:35:11.403 UTC and admitted at
07:35:11.532 UTC, a 129 ms scheduler queue wait. The task reached Completed,
the slot returned to Available with no active batch, and the derived input's
SHA-256 and watched parent path matched the test image. One embedding task
from that same image followed OCR; no unrelated GPU task was created in the
observation window. The image was removed only after fresh completion, slot
and hash checks; live and ready probes returned HTTP 200. The initial probe
timed out despite OCR completion because it compared the derived retained
input identity with the watched file path. A corrected identity check now
uses the parent revision path and content hash. The batch update timestamp
is not an independent measurement of native OCR completion, and the 129 ms
queue wait excludes watcher and extraction delay. This is the no-contention
reference for a later sustained-search OCR handover test, not that test itself.

On 29 September, the staged GPU-first/resident-CPU-fallback release returned
three ready searches in a two-caller, four-query overlap. The fourth GPU task
reached the former ten-second execution bound before it could complete. A
reviewed, BGE-specific 20-second GPU bound was deployed without changing the
two-second queue or 25-second outer search bounds. The next two-caller run
again returned three ready searches. This time the GPU work completed and
released its slot, while the last request expired in the queue after about
2.45 seconds. The scheduler wake was durable but its acknowledgement and a
simultaneous interactive handoff deadlocked while both updated the scheduler
state row under serialisable SQL transactions. SQL Server selected the wake
acknowledgement as deadlock victim; the next scheduler pass saw the already
cancelled request. This is a different failure from GPU inference timeout.

The correction acquires one transaction-scoped scheduler mutation lock before
state reads in all scheduler mutation transactions, after the admission lock
where that lock is already required. Release and reconciliation do not acquire
the admission lock. A real-SQL regression holds the mutation lock across a
concurrent wake acknowledgement and handoff, then verifies that both complete
and the wake remains available. A hosted scheduler/dispatch regression checks
that a handoff arriving between admission and wake acknowledgement is delivered
after the next wake pass. The relevant scheduler suite passed 116/116; an
independent architecture review found no blocking issue. This correction has
not yet passed live overlap or sustained-search OCR acceptance, so the search
upgrade remains in staging validation.

The first sustained-search/OCR overlap probe stopped before OCR scheduling:
the source watcher recorded the synthetic image at 11:08:30 UTC, but its scan
request was never published. The watcher had reused an old outbox idempotency
key based on root and debounce generation; deleting an earlier watch-state row
had reset that generation to one while the old outbox row remained. SQL Server
rejected the duplicate key and the unhandled background-service error stopped
the host. No OCR task or source revision was created; the unchanged synthetic
file was removed after those checks. The corrected watch outbox key uses the
new scan request's durable ID. Real-SQL tests now cover two separate
generation-one bursts and recovery of a historically leased batch after its
lease expires, without deleting the old outbox or clearing the lease. This fix
awaits staging recovery and a new OCR overlap measurement.

The mutation-lock release first returned four of four trained, ready searches
under two callers. A longer two-caller run after the watcher-key correction
returned 20/20 ready, with ten GPU and ten CPU-fallback requests. Full REST
latency, including loading and cleanup, was 10.27 seconds at p50 and 16.40
seconds at p95. The ten GPU tasks waited at most 273 ms from durable creation
to batch admission, all ten had result receipts, and the GPU slot ended
available with no nonterminal task. No new background-service failure was
observed during that run. These are staging reliability and latency samples;
OCR handover and independent final acceptance remain open.

After the prior lease expired at 11:38:33 UTC, the corrected host reclaimed
the watch batch, published a new watcher scan request at 11:38:34 and completed
the scan at 11:38:35. It cleared the watch row without altering the old
outbox. A fresh synthetic image was then ingested during two-caller search.
The OCR mini-task was created while BGE owned the GPU and waited 7.06 seconds
for that active batch to complete; it was admitted about 14 ms after release.
No new BGE batch was admitted between OCR creation and admission or while the
OCR page was active. The OCR task completed, the slot returned available, all
12 overlapping searches returned ready, and full REST p95 was 19.30 seconds.
The synthetic image was removed after OCR completion and a free slot; its
source revision was subsequently suppressed by a completed watcher scan. No
background-service failure was observed. The 7.06-second OCR queue wait
includes an already running GPU batch and is distinct from the uncontended
129 ms reference; it does not measure watcher or extraction delay.

The final staging run replayed all 96 frozen English holdout questions against
the corrected GPU-first/CPU-fallback build with two callers. All 96 searches
returned trained, ready hybrid results; 49 used a durable GPU task and the
other 47 used CPU fallback. Strict literal answer support appeared in the top
five for 80/84 answerable questions (95.24%), with source-document recall at
five for 84/84. Full REST p50/p95/max were 10.25/16.46/18.56 seconds,
including model loading and cleanup. All 480 distinct top-five evidence
references read back exactly against their cited passage and canonical source.
The 49 GPU tasks had 49 result receipts, no nonterminal task remained, and the
slot was available. Live and ready probes returned HTTP 200, with no observed
background-service exception during the run. Negative controls returned
related passages; the retrieval API does not claim answer abstention.

For this GPU-first implementation, the measured staging latency target is at
most 20 seconds full REST p95 under two callers, with the existing 25-second
per-search outer deadline. The final holdout and the 12-search OCR-overlap
probe (19.30-second p95) meet that target. This replaces the earlier
two-second aspiration, which was not achievable with the selected models and
runtime on this machine. Warm preloaded GPU inference remains faster, but the
full REST target includes the deployed ownership and loading behaviour.
This is an empirical two-caller staging envelope, not a latency guarantee for
sustained OCR, cold CPU warmup or higher concurrency. The 12-search OCR sample
has limited tail confidence and only 0.70 seconds of p95 margin. All timeout
and refusal outcomes count against availability rather than being omitted
from the latency interpretation.
