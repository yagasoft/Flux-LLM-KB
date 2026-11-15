# Scoped search release, 12 November 2026

The scoped ANN search correction is deployed as `ec928805`. Production is
serving trained hybrid results through REST, MCP and CLI, with current citations
and lexical fallback. Full production performance and previous-answer
non-regression acceptance remain open.

## Deployment and verification

The required feature closeout squash-merged and pushed the qualified candidate.
It passed 5,448 executed .NET tests, with 46 existing opt-in skips, nine native
contract groups and zero build warnings. Independent Astra review covered the
complete code, staging qualification and the exact production deployment packet.

The incremental updater used its reviewed stopped-pool mode and a three-role
payload binding. Installed Web, Outlook and unchanged helper hashes matched
their reviewed values. Readiness returned HTTP 200 and the deployment hold was
released after retained-state validation. No schema migration, configuration
change, helper upgrade, model acquisition or projection rebuild ran. Existing
workspace pauses were preserved.

An outer invocation copied a native command's final exit value after the
PowerShell updater completed normally. The updater emitted `ok:true`; fresh
installed hashes, running pool, absent hold and readiness independently
confirmed application. The deployment was not repeated.

## Measured scope and remaining acceptance

| Check | Isolated staging | Production |
| --- | --- | --- |
| Published vectors / segments | 1,113,836 / 136 | 1,257,687 / 284 |
| Frozen workload | 40 requests, two callers | 40 requests, two callers |
| Trained hybrid responses | 40/40 | 40/40 |
| Exact cited reads | 400/400 | 400/400 |
| Answer presence per round | 19/20 | 18/20 |
| API p95 | 9.335 seconds | 10.804 seconds |
| Dense-selection p95 | 1.434 seconds | Not measured |
| Concurrent native indexing | Qualified | No production fixture injected |

Production passed the unchanged 90% answer-presence threshold. Another valid
identifier-lookup answer replaced an earlier citation. The CRM question lost
its previously returned source-specific prohibition; generic prohibitions from
other systems do not establish applicability. The registration-prerequisite
question remains unanswered. These misses remain in the denominator.

Production did not pass the ten-second API p95 target or previous-answer
non-regression. Its slowest measured request was 11.861 seconds; every request
remained within twenty-five seconds. The changed corpus and graph/segment
population are relevant differences, not a proved cause. The next latency
observation needed is a bounded phase breakdown on the exact serving worker.
Automatic approval review rejected the prepared metadata-only capture before
execution, stating `blocked by policy`; no collector or search from that command
ran. No capture bypass or unchanged benchmark repeat followed.

Initial preparation preserved usable lexical results. A subsequent cold queue
timeout also returned ten lexical hits with explicit semantic status. Durable
records show acknowledgement after the queue deadline, with ownership settled;
they do not measure native model load or scoring. Later standalone CLI and MCP
semantic checks completed in 7.13 and 7.72 seconds. MCP readback matched exactly.

The test covers published retained data. Complete original-file coverage,
whole-host overload and autonomous production indexing throughput are not
established by these results.

## Cleanup and recovery boundary

User-authorised cleanup uses exact resource manifests and fresh ownership,
registration, path and database checks. Obsolete worktrees and isolated staging
catalogues are purged without merging unrelated changes. Unique conflicted and
staged contents are preserved through named Git refs and private archives, with
working-file and index reconstruction verified before removal. The reusable
restricted SQL test configuration remains available.

Current production data, model cache, deployed and predecessor payloads,
configuration and native recovery references remain outside the obsolete set.
The ten existing recovery writers and 105 pins are preserved; an invalid audit
does not authorise deleting their evidence. Private deployment, qualification,
preservation and cleanup receipts are retained together outside purge targets.
These cleanup operations do not imply that open search acceptance has passed.

## Bounded follow-up, 14 November 2026

After renewed user authority, the reviewed phase collector completed normally
on the exact serving worker. Both original queries returned ten trained hybrid
hits in 9.525 and 9.607 seconds. Candidate payloads were ready in 2.589 and
2.527 seconds; ranking waits were 6.655 and 6.717 seconds. Dense selection took
2.113 and 1.925 seconds, while query embedding took 63 and 219 milliseconds.
Lexical retrieval overlapped dense work and completed before its awaited join.
The bounded trace lost no events. Shared GPU native events lacked per-request
attribution, so their durations cannot be assigned to either query. This pair
does not replace the frozen workload or establish p95 below ten seconds.

The missing CRM answer remains current, published and exactly citable. A
read-only call of the actual lexical reader returned 100 candidates in 986
milliseconds, without that passage. A private heap load of its one existing
8.39 MiB segment found the target second among all 2,075 segment vectors, and
in both independent unfiltered ANN-100 and ANN-400 calls. This does not establish
global rank, the serving scope filter or admission to the model shortlist.
The same natural question scoped to its G3 folder returned the answer first;
explicit literal mode also found it and passed exact cited readback. These are
demonstrated temporary retrieval workarounds, not full-workspace acceptance.

The user declined a new snapshot, restore and large copy. No such operation
started. The older saved staging corpus differs from the serving corpus and
cannot stand in for current-corpus acceptance. Its prior qualification remains
historical evidence only. Existing files and scoring packets are preserved for
reuse; no new model or graph trial is justified by the current observations.

The normal expanded adapter discarded internal candidate diagnostics. A small
opt-in correction now emits metadata-only Event 14 at lexical, hydrated dense,
disclosure, shortlist and ranked boundaries, plus existing Event 2 after owned
cleanup. Each stage is bounded to 400 ordered chunk IDs; no query, document,
source path or vector is recorded. Four test-first diagnostic regressions and
all 111 related retrieval tests passed, and independent Astra code review
approved the change. It has not yet been deployed. A complete normal-interface
CRM trace must show both ranked and successful completion events before its
shortlist can be treated as consumed model inputs. Absence from the hydrated
dense pool would still leave raw ANN selection versus hydration unresolved.

## Candidate selection follow-up, 15 November 2026

The observer is now deployed and the service is ready. Its incremental update
initially stopped safely because the reviewed Outlook publish fingerprint omitted
the canonical launcher added by the updater. A separately reviewed and explicitly
approved forward recovery included that launcher and restored the compatible
payload set. Configuration, database, publication, paused roots, disabled Outlook
task and running SourceAccess helper were preserved. No projection rebuild or
corpus restore occurred.

One lossless normal-route CRM trace returned ten trained hybrid hits in 8.505
seconds. The missing answering passage was dense-pool item 148, but was absent
from the 223 planned disclosure bindings. Only 125 bindings passed disclosure and
65 distinct inputs reached scoring. This establishes the first loss at expansion
planning, before model scoring. It does not establish a model-ranking failure.

The correction preserves the entire existing disclosure plan and appends safe
dense-pool bindings in their original order until the existing 250-binding cap.
Fresh disclosure, the original baseline prefix, verified-input deduplication,
the 50-addition limit, model caps and final citation checks remain in force.
Neither deadlines nor lexical fallback change.

A bounded read-only replay of the exact 486 captured candidate identities used
the native publication, code proof, safety and disclosure checks. Its original
223 planned bindings, 125 approvals and 65 ordered model inputs matched the trace
exactly. Refill produced 250 bindings, 148 approvals and 81 model inputs, including
the answer at input position 70. An offline FP32 replay scored that answer seventh.
The regression fixture replaces all source identifiers and passage text with
synthetic values while preserving the observed grouping and order.

A separate offline component comparison grouped pairs by length within each
existing call of at most 50. All 81 FP32 logits and the top ten were identical;
CPU scoring took 23.595 seconds in original order and 20.267 seconds grouped.
This single comparison is neither a GPU speed result nor an end-to-end latency
qualification. Length grouping is not enabled by this correction.

The full-current-corpus staged acceptance and production p95 remain open. The
older successful staging indexing run remains historical evidence; its catalogue
was removed during authorised cleanup, and the user declined a replacement
snapshot or large restore. The new candidate correction has not yet been deployed
or subjected to fresh final cited-rank validation. A production admission lock
alone does not safely isolate a concurrent GPU trial: loss of its SQL session can
admit new production work while native staging work is still running, and held
locks can delay foreground settlement. Production remains running; no such trial
or additional service pause has occurred.

## Refill validation and dispatch latency, 15 November 2026

The refill correction completed canonical qualification with 5,456 passing tests,
46 existing opt-in skips, nine native contract groups and zero build warnings.
It was squash-merged as `2c6ce837` and incrementally deployed from the qualified
feature source `1c2f01c7`. The service returned ready with the reviewed payloads,
unchanged configuration, publication, root pauses and companion state.

The fresh two-caller, two-round frozen workload returned 40 trained hybrid
responses and 400 valid exact cited reads, without timeout or scope leakage.
Independent adjudication found answering passages for 19 of 20 questions in each
round. The CRM answer ranked seventh in both; the remaining miss concerns the
prerequisite for full registration. API p95 was 11.921 seconds and the maximum
was 12.741 seconds, so the ten-second latency requirement still failed.

Durable timestamps for the 20 ranking cohorts show a mean 1.492 seconds between
batch creation and dispatch acknowledgement, with p95 1.715 seconds. A single
read-only invocation of the native embedding recovery reader took 996.742 ms
and returned no candidates. Cached executions of its query averaged about one
second and used 3.17 million logical reads per execution. This discovery runs
before pending dispatch delivery in the serving loop. These observations identify
avoidable foreground delay; they do not attribute all model or request time.

The focused correction delivers already-admitted pending work before scanning
continuations. It retains the recovery scan on every loop, including startup
with no pending dispatches. Eligibility, claims, deadlines, model settings and
native settlement remain unchanged. All 80 focused scheduler/lifecycle checks
passed, including a real SQL owner remaining reserved while recovery observes a
callback that outlives the local delivery timeout. Independent concurrency review
approved canonical qualification. A prompt arriving during an existing recovery
scan can still wait for that scan; the resulting API improvement remains to be
measured after deployment. The full-current-corpus staging restriction above
still applies.

## Dispatch priority serving validation, 15 November 2026

The correction passed 5,459 executed tests, 46 existing opt-in skips, nine native
contract groups and a zero-warning build. Required closeout squash-merged and
pushed main `3c06612d`; the qualified source `64e7e17c` was incrementally deployed
as `20261115T121345Z-64e7e17c1dc0`. Independent checks matched the three installed
role fingerprints and preserved configuration, publication version 18291, its
1,257,687-vector/284-segment generation, both paused roots, disabled Outlook and
the unchanged SourceAccess process. The updater reported success and released its
hold despite the wrapper returning exit code 1; deployment was not repeated.

The first fixed workload ran before ANN preparation completed. All 40 responses
were lexical with `semanticStatus: warming`; 34 contained ten hits and six fewer.
The denominator guard stopped before citation readback. This failed startup run
is retained separately. HTTP readiness checks SQL and derived-index recovery,
and does not assert that the semantic projection has finished preparation.

A subsequent single control returned a trained hybrid result in 7.589 seconds
on the unchanged generation, establishing the transition to semantic readiness.
The separately reviewed warm two-caller workload then passed all 40 trained
hybrid responses and all 400 exact cited reads, without timeout, invalid citation
or scope leakage. API p95 was 9.637 seconds and the maximum was 9.870 seconds.
Every ordered passage and its nine authority/boundary fields matched the prior
independently adjudicated refill run. Answer presence therefore remains 19/20 in
each round, including the CRM answer at rank seven; the full-registration
prerequisite question remains a miss. This is warm serving performance, not a
cold-start or concurrent-indexing qualification.

For the 20 ranking cohorts created within this warm workload, durable
batch-to-acknowledgement delay averaged 567.722 ms, with p95 957.916 ms. The prior
workload averaged 1,491.953 ms. Acknowledgement-to-receipt time averaged 7.528
seconds; it includes the owned work and does not isolate model scoring. The
ordering regression proves admitted delivery no longer waits for the following
discovery scan, while a prompt arriving during an existing scan can still wait.
These same-host workload observations do not isolate every source of latency.

No production indexing was injected. Historical full-size staging evidence
includes actual GPU indexing, but the current staging catalogue is absent and
the user declined another snapshot or large restore. The current-release
full-corpus staging/indexing gate remains open; another unchanged live search
run cannot resolve that dependency.

The requested file cleanup includes obsolete staging backups. Canonical
`I:\FluxKnowledge\Recovery` remains held pending reconciliation: its invalid
audit retains 11 unfinished writers with `OwnerExitSufficient=false` and 105 pins
from two older manifests. Those pins do not inventory all interrupted-release
dependencies, so absence from the pin list does not prove an archive obsolete.
This does not establish that every archive is needed for serving search. Narrowing
the hold requires writer-specific completion/reconciliation evidence and an exact
archive dependency check; no SQL or retention bypass was performed.
