# Scoped semantic retrieval implementation plan

Status: normal-runtime accuracy, concurrent GPU indexing, publication and latency qualified in isolated staging on 12 November 2026, with independent Astra revalidation. Exact native fixture cleanup and Release/native checks passed. Canonical delivery uses the prescribed complete-feature sequence. One implementation owner; no search-feature production deployment.

## Normal runtime qualification, 12 November 2026

The normal search composition now uses prepared, workspace-filtered USearch
candidates, CPU query embedding and a shared scheduled FP16 reranking cohort.
SQL remains canonical. Indexed keyword retrieval remains independent and its
usable results survive semantic refusal or timeout; explicit literal mode retains
ordinal substring semantics. The original workspace SQL-vector scan and discarded
lexical results are covered by the scoped retrieval and deadline regressions.

The indexing trace identified three separate delays. A normal ranking request
expired in the two-second GPU queue before model loading; the explicit BGE queue
allowance is now four seconds within the unchanged twenty-second total owner
deadline. Scoped ANN searches now process at most two distinct segments together,
joining both actual native callbacks before releasing their views. Keyword SQL
selects the ranked candidate budget after the same scope and length filters,
without materialising and indexing all matching keys. A bounded alternating SQL
comparison preserved every ordered candidate ID and score while reducing the
observed lexical time from 1.96–2.19 seconds to 0.86–0.97 seconds. The normal
ANN default is aligned with the qualified search expansion of 512.

The frozen workload ran through the normal REST/native facade at two callers on
the complete copied published snapshot: 1,113,836 vectors in 136 segments. All
forty timed requests returned trained hybrid results during actual normal GPU
indexing. API p95 was 9,335.2909 ms and dense p95 was 1,433.641 ms; two API
requests exceeded ten seconds. All four hundred cited reads matched passage
text, authority and boundaries. Answer presence was nineteen of twenty questions
in each round, retaining every previous passing answer and the same prior miss.
The separately recorded cold warm-up also returned trained results in about
9.06 seconds, with dense selection below 1.86 seconds.

All forty requests overlapped native indexing progress. Both captured query
leases spanned an unrelated publication; later requests used the new generation.
All twenty-one ranking cohorts released ownership. Nine hundred and ninety
native intervals, including ninety-two GPU embedding intervals, showed no
simultaneous GPU execution. After the immutable trial cohort limit, the normal
endpoint still returned ten usable lexical hits with semantic status `busy`.
The owned host exited normally with an empty job and no forced termination.

This qualifies the full published snapshot and the specified two-caller workload.
It does not prove complete original-file coverage, overload behaviour, or
contention from every production background service. Autonomous source writers
were disabled; native GPU indexing and publication were exercised explicitly.
Staging allowed one pending CPU request while the normal default allows none;
both used the same two embedding lanes and model profile. No deployment,
production restart, production configuration change or model download occurred.
Private evidence is under the scoped semantic search packet's
`window-014-normal`; the complete code and runtime results passed independent
Astra review. Native cleanup initially refused at the preserved historical recovery
reference audit guard. The reviewed exact-target live proof then verified and
held the recovery files, sealed and invalidated the native audit, and completed
cleanup. The owned helper exited normally with an empty job; the synthetic
inputs, records, revisions and segment memberships are absent. Both original
roots, all sixteen foreign deletion records and all 136 ordered published
segments are unchanged. A fresh model-free normal-start guard confirms available
GPU capacity, settled history and no ordinary recovery work. Twenty-five owned
temporary targets remain blocked by automatic approval review; no
bypass was attempted. Preserve the exact canonical delivery receipt separately.

The Release candidate based on source `66b98b90` built with zero warnings and
passed 5,448 .NET tests, with 46 existing opt-in skips. Canonical closeout then
stopped on prohibited historical terminology in this design record and the
hybrid retrieval design. Those documentation-only corrections passed the native
repository contract. All eight remaining native contract checks then passed,
including isolated workflow failure, timeout, cancellation and cleanup cases.
These results qualify the unchanged implementation; the prescribed
complete-feature sequence verifies the final exact candidate and records its
canonical commit, push and cleanup receipts without production deployment.

## Integration and diagnosis, 2 November 2026

The dedicated task branch now includes main `26e5e6ec`; all 49 tracked changes and
28 untracked files were preserved in a private snapshot and retained Git stash.
The sole conflict was resolved by retaining both upstream physical archive
ancestry and the Full-Text readiness wait. Shared admission, recovery and bounded
metadata-append changes need combined verification with this search change.

The instrumented source-routing experiment reached its unchanged 180-second
bound during immutable binding preparation: 90 of 136 segments completed, no ANN
segment was opened and no routing case ran. Actual child exit and an empty owned
job were observed; query-lease cleanup remains unconfirmed and preserved. This
locates the preparation cost but establishes neither a successful routing policy
nor model, ANN-serving or answer-quality performance. No unchanged rerun or larger
budget follows from that result.

A provider-only collector passed isolated qualification. The subsequent reviewed
read-only workspace reproduction completed against unchanged release `b1852c3f`:
one search and one exact cited read, 247 events, zero lost events and no CPU/CLR
profiling. The request took 20.985 seconds and returned usable lexical fallback
with semantic timeout. Admission consumed 1.927 seconds, generation acquisition
59.6 ms and successful embedding 5.420 seconds, including 4.300 seconds loading.
Workspace dense selection then streamed 56,320 SQL vectors (220 MiB) before the
20-second semantic execution cutoff. It never reached hybrid candidate fusion or
trained ranking. The `NativeSearch` event identifies the inference batch; it does
not prove native ANN execution. Fallback assembly completed within the remaining
965 ms. Lexical completion before fallback was not separately instrumented.

The active generation/version were unchanged at both observations. The capacity
slot was idle afterwards, the callback receipt committed and all 569 earlier
uncertain-task identities survived; this request added one uncertain task. These
observations establish neither complete native/lease cleanup nor a leak. Shared
SQL CI had finished, but ordinary reconciliation continued during the request,
so page throughput cannot be attributed solely to SQL, storage or CPU. No source,
configuration, index or deployment change was made. An independent Astra review
confirmed that prepared scoped ANN and separate fallback budgets address this
failure; this trace supplies no reranker performance or relevance evidence.

Fresh current-main verification and independent review cover 560 distinct passing
test names across Domain, Web, native and SQL checks, including generation and
callback concurrency invariants. Publication fixtures now observe their automatic
Full-Text catch-up before keyword assertions; the original assertions remain.
The affected 153-test publication/scope suite passes, and the Release staging host
builds without warnings. Full runtime acceptance and canonical closeout remain.

The later source-routing observation completed all 136 binding/native preparations
in 348.8 seconds under a separate 480-second setup allowance. Its first warm case
was cancelled at the unchanged one-second limit (1.035 seconds including state
checks), with no supplementary input admitted. Internal source/ANN/hydration
boundaries were not timed, so the cancellation stage remains unknown. The other
two cases did not run. Manifest/cache disposal and all 136
native-owner release callbacks completed; actual child exit and an empty owned
job were observed at 391.3 seconds, inside the 600-second parent bound. No models,
downloads, production access or canonical/index/configuration changes occurred.
The experiment does not establish candidate rescue and is rejected for this
budget. No larger cap or unchanged retry follows. Exact source-capture versus
pre-borrow SQL timing remains unmeasured, and historical ownership evidence is
preserved. Serving deadlines and accuracy thresholds remain unchanged.

Native root observations show a registered root and available generation. Source
verification remains invalidated with incomplete read counts; these observations
do not prove all-source coverage. The historical staging results below remain
pinned-package evidence, not fresh qualification of the integrated branch.

The approved two-request offline FP32 GPU test has now run. Both three-second
callers cancelled with zero returned scores. Cold model loading took 4.192 seconds
and failed after cancellation; actual owned work settled at 5.069 seconds. The
file/process-cache repeat still reloaded the session: 2.057 seconds loading,
627 ms of incomplete scoring and 187 ms unloading, with settlement at 3.253
seconds. Four native batches ran before cancellation; fifty passages require
thirteen batches, so this does not establish full scoring throughput. The code
opens and disposes the GPU reranker on every call. Keeping a model loaded might
help, but neither its necessary throughput nor an ownership-preserving serving
design has been demonstrated. The real-GPU capacity-at-caller-timeout flag was
not measured; the synthetic control alone populates that field.

Actual owned child/launcher exit and an empty job were proved. The original pool
was restored, live/ready both returned 200, configuration/database identity were
unchanged, both roots remained paused at their original revisions, GPU admission
was released and capacity was available. The shared resource interval was
returned. No deployment, configuration, index, source or model changes occurred.
This is safe diagnostic completion with failed feasibility, not feature
acceptance. The two-request authority is exhausted.

The next useful observation is one complete fifty-passage scoring attempt after
loading within one continuously held, unchanged twenty-second GPU owner. Give
tokenisation, all thirteen batches and validation exactly three seconds after
loading, then await actual settlement and disposal. Stop on timeout or incomplete
output; do not retain a session beyond ownership or repeat unchanged work. This
observation requires fresh operational authority and scheduling. A pass would be
a necessary condition for reuse, not proof of the three-second callback or
ten-second API target. Candidate omissions remain independently unresolved:
ranking could recover at most four of the seven misses, reaching 17/20 (85%),
below the unchanged 90% answer target. No resident-model serving change is made.

The private one-request diagnostic is prepared and independently source-reviewed.
Ten model-free controls pass, including late native settlement and preservation
of the original scoring failure if diagnostic receipt writing also fails. The
build has zero warnings. No further SQL, GPU or production execution occurred.
There is no operational controller or runtime witness for this new observation;
a fresh operational packet, target review, authority and resource interval remain
required. These controls do not close semantic accuracy or latency acceptance.

## Next measurement preparation, 8 November 2026

The other release's shared window has been released. Its local completion record
identifies runtime `5838211e`, canonical deployment guards `a28ae00a`, paused root
revisions 6/15 and weekly metadata cadence. These saved observations are target
expectations; this task has not made a new production call. The search worktree
remains based on the reviewed 2 November integration, `26e5e6ec`.

The one-request GPU packet is now independently approved technically, pending
fresh specific user authority. A new disposable SQL control passed with a
simulated 3.265-second load followed by fifty aligned synthetic scores in 2.09 ms;
the caller took 4.166 seconds and actual work settled at 4.216 seconds. This
validates timing placement and ownership, not model performance. The independent
cancellation control retained capacity until actual settlement. Ten scoring and
observer controls, nine ordering/failure scenarios, 52 timestamp cases and an
atomic concurrent-intent control pass; the build has zero warnings. No actual
model, GPU or production operation occurred.

The approved approach keeps one twenty-second owner and one fifty-passage score,
with three seconds after loading for tokenisation, thirteen native batches and
validation. It preserves both paused roots, weekly cadence, running SourceAccess
and disabled companion. The original named IIS pool is restored only after
actual native/process/job settlement and unchanged safety boundaries; a separate
observer runs on every outcome. No deployment, source resumption, index/schema/
configuration/task changes, downloads, recovery or retry is authorised. This
necessary condition for reuse cannot close accuracy or publication qualification.

## GPU scoring result and next experiment, 9 November 2026

The specifically approved one-request GPU test completed. Its first preflight
refused a stale Windows adapter LUID before admission, pool stop or model use.
Fresh metadata matched the same physical GPU UUID/model; independent review
approved the refreshed binding with unchanged scope and a reduced work budget.

All fifty FP32 scores were returned and validated in 2.044 seconds after loading.
All thirteen native batches settled. Loading took 4.134 seconds, inference 2.048
seconds and unloading 238 ms; the full caller took 7.374 seconds and actual owned
work settled at 7.430 seconds. The frozen shortlist matches the saved CPU FP32
input hash, ordered passage identifiers and fingerprint. Its top ten results
agree completely; maximum logit difference is 0.000042 and the known complete
answer ranks second in both. This is one shortlist, with no new retrieval or
current citation validation. It does not change the measured 65% search accuracy.

Native/model and SQL ownership settled, with actual launcher/child exit and an
empty job proved. The original pool was restored; live/ready both returned 200,
the release/configuration/database identity matched, both roots remained paused
at revisions 6/15 with weekly cadence, SourceAccess remained running, the
companion remained disabled and admission/capacity were released. The operator
took 66.8 seconds. Both shared intervals were returned. No deployment, download,
source resumption or configuration/index/schema/task change was initiated.
The approved model request is used; no further GPU window is authorised.

The necessary after-load scoring condition passes, but the preserved three-second
complete callback remains unmet. Independent design review selected assessment of
a separate offline FP16 internal-graph derivative before changing ownership or
serving budgets. Existing cached tools can preserve INT64 inputs and FP32 logits;
the derived bytes must have a separate identity. The specifically approved
CPU-only conversion completed under the reviewed ten-minute, 12 GiB process/scratch
and 3 GiB output limits. It produced a 1,136,224,108-byte pair under J, with 393
FP16 initialisers, unchanged INT64 inputs/FP32 logits and a verified external-data
closure. Structural and finite-weight checks passed; actual child exit and an
empty owned job were observed. The original FP32 hashes are unchanged. No
download, GPU, production or private-corpus operation occurred. The derivative
remains disabled. The subsequent native trial below establishes compatibility
for one shortlist, while full serving qualification remains open.

Explicit staging-probe precision support now preserves the original FP32
constructors, overloads and default composition. The new fixed profile has its
own model fingerprint and owner runtime key. Cache/identity failures refuse
before allocation; request-scoped native ownership, cancellation and disposal
remain unchanged. The combined model/cache suite passed 105 cases and the real
disposable-SQL scheduler suite passed seven. Independent Astra review found no
blocking profile-code findings. These synthetic checks do not qualify the model.

The prepared narrow trial was a cold fifty-passage FP16 comparison with the saved FP32
reference, followed only on success by one warm callback with the unchanged
three-second caller deadline measured from submission. Freeze at least 90%
top-ten overlap and a known answering passage in the top ten. Stop on either
failure and observe actual settlement independently. It received specific approval
and ran on 10 November, as recorded below. Complete callback timing, broader ranking
quality, candidate rescue and full indexing/publication acceptance remain open.
Ranking alone still reaches at most 17/20 (85%), below 90%.

The separately instrumented staging metadata observation retained the original
one-second query allowance and reused the pinned prototype's exact source query.
Connection opening took 127 ms and reader execution 709 ms. Cancellation arrived
while streaming, after 8,351 artifact/path rows and 23.2 MB of conservatively
estimated metadata. The client command returned, connection closure completed and
the owned process exited without force. No ANN, models, production calls or
canonical writes occurred. This locates the cutoff in this new query-only run;
the earlier prototype's internal cancellation stage remains unknown.

Independent Astra review approved the next smallest experiment: enrich the
existing request-local live-binding capture once, without another metadata query
or persistent cache. Preserve the complete original canonical key set despite
path aliases. Invalid, ambiguous or over-budget metadata disables only optional
routing; overflow must discard metadata and continue the base capture within its
original deadline. Charge the enriched capture to the existing dense target and
all routing, ANN, mapping, hydration and Citable work to the extra one-second
allowance. Its metadata-only prototype now passed the comparison below; serving
integration is not implemented. Actual candidate admission,
combined latency and answer accuracy remain unproved; do not repeat cold
preparation merely to reach the metadata experiment.

## FP16 and shared-metadata observations, 10 November 2026

The approved two-request native FP16 trial completed in a 74.3-second operator
window. The cold request returned all fifty finite, aligned scores in 1.946
seconds after loading. Loading took 2.958 seconds, inference 1.950 and unloading
196 ms; the caller completed in 6.025 seconds and actual work settled at 6.077.
All thirteen native batches settled. The saved FP32 reference and frozen input
matched: top-ten overlap was 100%, the known answer ranked second, and maximum
logit difference was 0.08588. This qualifies that comparison only.

The same-process, new-session repeat failed the unchanged three-second whole
callback. Loading took 1.687 seconds; cancellation interrupted inference after
seven of thirteen batches. The caller returned no scores at 3.009 seconds;
actual work settled at 3.263 seconds, including successful model disposal. Full
warm scoring time was not observed. Both durable/native ownership checks passed,
and actual launcher/child exit plus an empty job were proved. Original app
restoration passed with live/ready 200, matching release/configuration/database,
paused roots 6/15 and weekly cadence, running SourceAccess, disabled companion
and released admission/capacity. The shared interval was returned promptly.
Both approved requests are used. FP16 serving remains disabled; no deployment,
configuration, index or task change occurred.

Independent Astra review found that increasing only the rerank cutoff would
leave two other barriers: SQL and the executor enforce a two-second queue, and
Web sends a busy caller to CPU after proven absence of handoff. CPU ranking takes
20–23 seconds in the saved trial. The existing mixed route also requires equal
GPU/CPU model fingerprints, so FP16 cannot simply be substituted for FP32 there.
The next smallest unit is a synthetic two-caller routing/admission and complete
request timeline under one explicit bounded policy. Preserve defaults, ownership,
fallback and indexing fairness; stop before another GPU trial if the ten-second
API target cannot fit. Residency is not yet established as necessary.

The separate metadata-only prototype reused the actual unchanged base reader and
captured its SQL command/parameters, then compared one enriched streaming
projection. Both captured exactly 11,214 canonical binding keys. The base took
694 ms and enrichment 477 ms, with complete metadata and a conservative 31.15 MB
peak estimate under 64 MiB. The order and warmed caches preclude claiming that
enrichment is faster. Thirteen pure controls cover duplicate/ambiguous aliases,
invalid paths, overflow and incomplete/cancelled captures. The child exited
normally. No ANN, model, production, cold preparation or canonical write ran.
This supports testing one shared request-local capture; it proves neither
candidate rescue nor combined dense/API latency. Overall process memory was not
measured in this run.

The first two-caller synthetic timing experiment rejected a bounded waiting-only
policy. One caller received trained scores in an illustrative 7.515-second
request envelope; the second reached 10.018 seconds without trained scores.
Those envelopes include simulated surrounding work, not HTTP measurements.
The model phase estimates combine observed components from different requests;
they are neither a measured complete warm callback nor a p95 guarantee. The
existing route sent one caller to CPU and both reached the three-second ranking
cutoff. Actual durable receipts and ownerless capacity passed in disposable SQL.
An independently reviewed bounded two-client cohort remains a design proposal,
with cancellation, durable settlement and indexing fairness still to implement
and verify. No additional model or production window follows from this result.

The source-supplement experiment still failed its one-second limit after a
diagnostic connection-pooling error was corrected. An interleaved six-pair
control measured median retirement-read totals of 130.05 ms with forced new
connections and 1.17 ms with configured pooling, returning identical sets. The
query session's separately held unpooled SQL lease was preserved. In the
corrected candidate run, complete live metadata took 336 ms, path matching
272 ms and the composite filtered ANN call cancelled after 735 ms. No mapping,
hydration or Citable stage completed; the other two questions were not run.
All 136 native owners released, the query/cache disposed, and actual child exit
with an empty job was observed. This does not identify native traversal as the
cause. The bounded in-memory observer subsequently completed 49 segments and
selected a fiftieth before the same cutoff. For the 34 completed nonempty
segments, combined borrowing/search took 690 ms and their borrow time was 46 ms,
leaving a 644 ms adapter interval. Selection and retirement reads totalled 7 and
46 ms respectively. The residual includes managed/native work and scheduling;
it is not instruction-level C++ profiling. No observer events were lost.

Independent review accepted a bounded sparse-set correction: compare at most
1,024 actual selected vectors per segment directly through the owned native
view; larger and unfiltered sets retain graph search. The existing FP32 cosine
profile uses the [pinned USearch C ABI](https://raw.githubusercontent.com/unum-cloud/usearch/v2.19.3/c/usearch.h)
for vector export and distance. The branch retains scope/retirement checks,
borrow lifetime, cancellation, deterministic ties, a top-100 heap and refusal on
missing/invalid native vectors. No SQL vector payload scan is introduced.
Test-first guards failed before the change; all 20 focused native cases then
passed. The combined native/SQL ownership/publication set passed 41 cases with
no failures or skips, and independent delta review found no blocking findings.
The full-size single-query run completed all 136 segment selections, requiring
99 native borrows, in 454 ms. Mapping and hydration completed, but Citable
processing reached the original matching-inclusive one-second cutoff. A final
diagnostic explicitly moved identical matching into the existing two-second
capture allowance: capture plus matching took 630 ms with a 38.65 MB retained
estimate; ANN completed in 432 ms, mapping/hydration in 18/24 ms and Citable
processing in 477 ms. The case nevertheless exceeded one second: its inner phase
timer read 1,001.6371 ms and its case receipt also failed the unchanged gate.
Late candidate values were discarded, not retained for an accuracy claim.
Cleanup and actual child/job exit passed. These are single-query observations,
not a p95 or full-retrieval qualification. No more source-only variants will run.

Independent review accepted a smaller integrated follow-up: preserve the global
ANN-100 population, add a separate bounded source-directed list, and select at
most 200 concrete bindings across the three ranked lists before one common fresh
disclosure check. Rebuild grouping/fusion from the validated survivors before the
unchanged final fifty-input model limit. This changes relevance policy when
source hints succeed and may displace base candidates or citation alternatives;
it cannot promise retention of every original binding. Source absence/ordinary
timeout must preserve the existing two-channel path. Identity conflicts must be
detected across the complete bounded population before truncation, and identical
text must never share disclosure authority. Pure selection/engine tests are the
next unit, followed by shared-capture integration and full frozen-query checks.
Candidate rescue, the two-second combined dense target, ten-second API p95,
full accuracy and indexing/publication acceptance remain open.

The integrated selection/engine unit now passes 107 Domain checks. Shared SQL
capture, source cancellation and generation/scope integration pass 118 combined
checks, including caller cancellation, actual delayed settlement and an epoch
change before a source timeout. Independent milestone and SQL reviews found no
blocking findings. Root/workspace queries use one enriched live capture; all-scope
and existing callers retain the original path. Publication predicates still permit
an older readable generation while unrelated publication advances. These are
functional checks, not full-corpus relevance or latency qualification.

A new isolated CPU-only staging payload loaded the cached models and prepared
the full existing native manifest. Its readiness check identified one missing
staging migration: `20261101195325_AddVectorPublicationMetadata` (79 expected,
78 applied, no extras). It only adds a covering metadata index. The host was
stopped with zero active searches, acknowledged native CPU cleanup and observed
process/listener exit before reviewing that staging-only migration. No corpus
request was issued against the unready endpoint and no production action ran.

## Current staging result, 31 October 2026

The authorised Windows-identity staging backup passed COPY_ONLY/CHECKSUM,
VERIFYONLY and header checks. Independent read-only reconciliation confirmed the
candidate activation committed. The active 136-segment projection contains
1,113,836 vectors. Both original and window-available exact-vector populations
pass at staging expansion 512: mean recall@100 99.6%, minimum 96%, recall@10
100%. Published-snapshot recall does not prove complete source-file coverage.

Model admission now covers only embedding and reranking. SQL, ANN and citation
work release CPU lanes; cancellation retains an entered callback until its actual
settlement, and uncertain native cleanup cannot become a ready semantic result.
Independent review accepted the ownership correction. The later keyword/literal
checks below include that behaviour.

A fixed staging-only canonical/embedding backlog completed 368 successful native
RPCs while all 40 frozen searches overlapped actual indexing execution; all
started native calls acknowledged completion. All 400 citation readbacks were
valid, with no scope leakage. This was controlled embedding load, not a complete
MoHESR source scan or a successful publication qualification. The backlog stopped
at an acknowledged, lease-cleared checkpoint; the separate small publication was
not attempted because readiness failed.

The last complete serving candidate, before the new keyword/INT8 work below,
failed acceptance: loaded API p95 was 15.24 seconds against ten, only
39/40 responses were hybrid-ready, and independent answer presence was 25/40
(62.5%) against 90%. Repeated rounds are not independent questions. That
quiescent run also failed API/dense p95 at 12.21/3.14 seconds; it answered 13/20
questions. Trained reranking times out throughout. These are post-hoc frozen
workload observations, not held-out or exhaustive relevance validation.

One fresh bounded lexical plan for the timed-out question examined 735,172
non-Full-Text bodies and performed 2,802,817 logical body reads to find no literal
substring matches. Its serial substring statement took 2.088 seconds alone.
Disabling the row goal preserved all 100 ordered keys/ranks but took 2.091
seconds; the hint was rejected. This explains a concrete cost, but does not
attribute all 15.677 seconds of the loaded lexical wait to that statement.
The user subsequently approved indexed keyword defaults with explicit literal
mode, and the bounded offline INT8 trial. Keyword selection now scopes Full-Text
keys before its 100-candidate budget; exhaustive body scanning remains only in
literal mode. Literal preserves raw code units, bypasses models/generations and
retains final citation validation, including the model-free service paths. A
controlled pending-change fixture also exposed an idle-but-incomplete Full-Text
state; readiness now checks pending changes as well as population status.

Full-Text materialisation initially chose twelve parallel workers and recorded
substantial latch waits. A bounded serial variant retained all 100 ordered
candidate keys/ranks and completed in 2.13 seconds. Its 8.36-second baseline
overlapped other disposable SQL tests, so this is not an isolated speedup estimate
or API SLO proof. The normal query now bounds that materialisation to one worker.

The offline INT8 derivative was created and verified under `J:\Models` with zero
downloads and the original weights preserved. Tool-verification defects stopped
the first two attempts before conversion; their stages and failure markers remain.
A separately bounded, successful import-only preflight preceded the successful
conversion. Both bounded native trials produced 50 finite scores and acknowledged
every native call, but failed the declared top-ten rank-overlap guard: 0.8 with one
shard and 0.7 with four, against 0.9 required. Each stopped before warm timing or
the second case. The derivative remains disabled and is rejected for this change;
the original FP32 files, failed trials and private prototype remain preserved.
The unused experimental public profile and its tests were excluded from the
shipping diff. The original FP32 model specification/opener is unchanged against
HEAD; its subsequent build had zero warnings and all 24 original BGE inference
checks passed. The measured staging package retains the disabled prototype;
it has never selected that profile and was not republished after this exclusion.
The 20/25/3-second search budgets and acceptance targets are unchanged.

Fresh checks pass: 1,466 Domain tests, 105 scoped SQL/CLI/profile tests and 34
focused model-profile/inference tests (overlapping the profile cases in the 105).
The configured full Web run returned 684 passes, eight failures and 28 existing
skips. Corrections established actual Full-Text readiness, real prepared scoped
ANN in the older HTTP fixtures, and isolated fake-endpoint background services;
all ten affected HTTP/contract checks then passed. A fresh complete configured Web
run on 31 October passed 692 tests with zero failures and the same 28 skips,
including the corrections and exclusion of the rejected INT8 API. These
implementation checks do not replace serving acceptance or constitute a passing
full branch check.

The new keyword/ANN/FP32 quiescent cohort returned 40/40 hybrid-ready responses,
with API p95 8.708 seconds and 400/400 exact citation readbacks, without scope
leakage. All rerank attempts timed out. Independent comparison of every returned
binding and body with the previously adjudicated capture confirms unchanged
answer presence: 13/20 unique questions in both rounds, or 26/40 observations
(65%), below 90%. Concept smoke was 70% and is not answer accuracy. Dense
selection p95 was 1.578 seconds against the two-second target. The original
two queries also returned 12 and ten validated lexical passages during semantic
warmup, in 8.636 and 1.841 seconds respectively.

The single subsequently reviewed loaded measurement also passes API/dense p95,
at 7.918/1.745 seconds. All 40 searches were hybrid-ready, all 400 citation reads
valid, and no scope leakage occurred. Actual native indexing execution overlapped
all 40 API and search-native intervals; 192/192 embedding RPCs succeeded and every
started native call acknowledged settlement. The owned 2,048-passage backlog
stopped after 192 persisted vectors at a lease-cleared checkpoint, leaving 1,856
unpublished. No publication mode was invoked. These are controlled embedding-load
observations, not raw-source indexing or production GPU throughput. Independent
loaded adjudication found the same 13/20 answers in both rounds (26/40, 65%);
every returned binding/body matched the quiescent capture. Accuracy therefore
still prevents qualification.

A distinct independent review approved a final bounded, read-only audit of the
seven misses' already captured candidates and known answers. Its 844 distinct
keyed reads completed in 11.14 seconds, using 38 SELECT commands with no vector
payloads, model work, leases or persistent writes; publication/isolation checks
matched before and after. Four complete currently citable answers appear in the
actual shortlists at ranks 24, 32, 43 and 32. Three other questions omit an eligible
complete known answer from both candidate channels; this does not prove absence
of alternative answers. Every known answer passes the normal disclosure guards.
More ranking capacity alone therefore cannot yet be proposed as the complete
remedy. No further performance cohort, CPU tuning or quantisation trial is planned
for this measurement cycle.

Completed trained FP32 scoring of the four exact current shortlists (50, 42, 49
and 50 inputs, without padding) promoted the verified answers to raw score ranks
2, 10, 1 and 2. All 191 scores were finite and aligned, with 53/53 native calls
acknowledged. Each diagnostic took 20.33–22.79 seconds under a separate 60-second
diagnostic budget. This proves useful trained ordering on those inputs, not normal
three-second completion, final citation/diversity selection or API answer accuracy.

All three missing known answers also match the exact indexed Full-Text expression.
A bounded raw-reader reproduction returned 100 candidates per query; all 300
passed existing length, hash, metadata and lexical-anchor checks. The suspected
2,048/1,024 length mismatch did not cause these misses and was not changed.
Normal disclosure reads of the 295 distinct captured bindings and actual
`CitableAsync` reproduced the final 38/16/12 candidates. Missing disclosure windows
removed 57/82/87 rows; the secret guard withheld another 5/2/1. Those protections
remain required. The candidate budget is consumed before returnability filtering.

Independent review approved one fixed 1,000-key SQL lookahead prototype: materialise
scoped Full-Text keys once, probe existing bounded window availability, then select
at most 100 structurally available candidates. Detect unexamined keys and report
explicit exhaustion when the cap prevents filling the budget, including empty
results. Preserve rank/tie order, cancellation, model-free/literal behaviour and
all later disclosure/publication checks. Availability grants no disclosure
permission. Do not enlarge the cap or scan windows for every Full-Text match if
the three known answers still lie outside this bounded result.

That fixed prototype completed all three queries in 5.91 seconds, with commands
taking 1.80/2.06/1.60 seconds. It probed exactly 1,000 keys per query and found
514/457/624 available windows, filling all 100 selection slots. It recovered none
of the three identified answers: authenticity was available inside the probe but
below the selected 100; CRM and full registration were outside the probe. The cap
was not raised. Independent review rejected shipping this as an accuracy fix.
No product lookahead code or configuration change was made.

Independent review then froze one term-coverage alternative within the existing
keyword/ANN match domain, with controls for duplicate concentration and separate
citation bindings. Known answer IDs remained evaluation labels, and work limits
were fixed before execution. This experiment remained separate from GPU
authorisation.

The subsequently frozen two-lane prototype retained the original Full-Text match
domain and used distinct indexed-input term-family coverage, with exact source
path tokens as a subordinate signal. After native SQL controls established parser,
binary grouping, window and cross-command temporary-table behaviour, the validated
seven-case run completed in 38.95 seconds. Each case selected 100 candidates;
no work cap was reached. None of the three previously missing known answer inputs
was selected. The policy is rejected without changing its weights or caps. Other
selected bodies were not adjudicated, so this does not establish that alternative
answers are absent. No product selection policy or configuration changed.

An existing four-sample corpus embedding comparison was initially missed during
evidence lookup. A fresh three-key metadata check then confirmed current f82
membership and the same complete source/input/vector/payload identities for the
QR and CRM samples. Their existing fresh-versus-stored CPU comparisons remain
usable: cosines exceed 0.99999999999 and maximum component errors are below
4e-7. No model call or search was repeated. These two examples do not support an
input/provider mismatch as their retrieval failure; they establish neither global
embedding correctness nor why the learned similarity misses the answers. An exact
fresh comparison remains unavailable for the full-registration sample.

Independent inspection of the actual 46/49/26 citable shortlist representatives
found no complete alternative answer to those three questions. The review did not
inspect further bindings or claim library-wide absence. Completing trained ranking
of the four other known misses, while retaining the thirteen previous answers,
would demonstrate 17/20 answers, below the acceptance target. This is conditional
on the inspected representatives, not a ceiling on alternative binding provenance
or answers elsewhere in the library.

A subsequent offline audit inspected all 207 captured alternative-binding
occurrences for those same groups (159 distinct chunks). Exact header/body/input
identity and saved current eligibility matched throughout. Their source provenance
also supplied no complete answer to the three missed questions under the unchanged
rubric. This closes the saved-alternative question for these frozen shortlists;
it does not establish library-wide absence, a successful remedy or future API
accuracy.

The next frozen experiment was designed to use source-topic metadata for one supplemental
ANN search inside the original workspace. It reuses the exact saved query vectors
and prepared segment bindings, without re-embedding or changing the index. Only
new distinct citable inputs may fill the actual 4/1/24 empty model-input slots;
the original shortlist and independent citation alternatives remain intact. Source
tags are whole root-relative metadata tokens containing letters and digits, or
source-uppercase acronyms, matched without case sensitivity and combined by union.
There are no service-specific identifiers or query rewrites. One search returns
at most 100 candidates under a separate one-second supplementation budget. Cold
preparation is separately bounded at 180 seconds and eight GiB. No use of partial
metadata, per-request full-corpus window preparation, cap growth or model calls is
permitted. This experiment must establish actual candidate admission before any
product change; it cannot establish answer accuracy or serving latency by itself.

The single reviewed source-scoped prototype launch exceeded its 180-second
startup limit before writing a preparation receipt or executing any of the three
searches. The controller observed child exit one and an empty owned job, with a
205,479,936-byte peak. A subsequent restricted staging metadata read found one
query lease created during the trial and no remaining segment-owner rows from
that interval. The lease row is preserved. Process exit does not establish managed
lease cleanup, and absent segment owners do not distinguish preparation that
never began from preparation followed by cleanup. The last outstanding startup
phase and segment were not recorded, so SQL binding/window capture, physical
manifest/native preparation and exception disposal remain hypotheses. This is an
inconclusive experiment, not a failed relevance test or evidence of absent answers.
Do not repeat the trial unchanged or enlarge its work limits. The next useful
observation is a phase/segment checkpoint at cutoff, before considering another
bounded run; no source-scoped supplement has been adopted into the product.

Existing GPU admission and native-worker ownership are catalogue-local; the BGE
adapter opens DirectML device 0 without machine-global exclusion. An independent
staging database cannot reserve shared hardware from production. GPU validation
therefore requires an explicitly reserved window with competing producers fenced;
an idle utilisation snapshot alone is insufficient. The subsequently approved,
single standard `nvidia-smi` summary completed on 31 October in 0.208 seconds:
2,748 of 12,282 MiB was used and utilisation was zero at that instant. Per-process
memory was unavailable. This neither maps DirectML device zero to the observed
adapter nor reserves capacity. No further production inspection, GPU reservation,
pool stop/resume or GPU model execution is authorised by that approval.

The separately approved one-off adapter/producer inventory subsequently completed
in 2.815 seconds, with actual query and controller exit zero, no SQL access, model
loading or production mutation. It mapped DirectML adapter zero to the hardware
GPU and confirmed the production pool remains started with automatic startup.
This is a point-in-time inventory, not continuing compute reservation. A private
two-callback FP32 feasibility packet is being prepared with unchanged three-second
caller and twenty-second native-owner limits. Its fresh disposable SQL controls
prove capacity remains reserved after caller cancellation until actual callback
settlement. Completed scoring and cancelled execution have distinct, coupled
durable outcome tuples; a released cancellation can retain delivery-outcome
uncertainty without retaining GPU capacity. A synthetic child-process control
also confirms cancellation reaches actual process exit and an empty bounded job.
These controls execute no real models and do not establish GPU feasibility,
answer accuracy or serving latency. Independent operational review approved the exact pinned packet after corrections
for pre-launch refusal and monitoring-error settlement. Both direct and fresh-process
failure-path checks pass. The user then explicitly approved the bounded production
pause and staging GPU window. Its first execution aborted during preflight:
liveness returned 200, readiness returned 503, and index health reported
`OperatorActionRequired / ConfigurationInvalid`. No admission reservation,
pool-stop intent or model request was created; the original pool remained started.
The exact underlying exception is not established by that broad failure category,
and a null generation in this status projection does not prove SQL index absence.
The separately user-authorised deleted-file-fix chat now owns the production
deployment window. This search task has confirmed no overlap and holds all
production actions and GPU execution. The next observation is release of that
window with restored readiness or the precise recorded readiness exception;
do not repeat the unchanged trial or perform production repair under this test.

The recovery owner subsequently confirmed a distinct held-startup cycle: the
active segmented generation lagged the canonical corpus by one version in the
same epoch, with no rebuild operation. The deployment hold suppressed the derived
refresh needed for healthy readiness. Increasing the deadline cannot break that
cycle. That owner is qualifying a sealed/no-draft refresh correction under the
existing fences. That owner subsequently reported the correction qualified and
pushed as `91f9690e`, with 4,927 passing tests, 37 existing opt-in skips, zero
failures/build warnings, native contracts passing and isolated held readiness
200/Healthy. The recovery owner now reports successful production recovery with liveness and
readiness 200, index Healthy, the hold removed and the companion task restored.
Its bounded deletion acceptance and coordinated shared-GPU window remain active;
explicit operational release has not been received.
This finding explains the later held deployment failure, not the earlier broad
`ConfigurationInvalid` classification or the original search timeout. This task
continues to hold production, GPU execution and main-branch writes. Integration
must include the completed recovery change and fresh affected checks before
claiming qualification of this search feature.

The measurement host stopped after zero active searches and a CPU native-cleanup
acknowledgement; its PTY exit was one, without a separately captured child exit
code, so a clean zero application exit is not claimed for that earlier process.
The later guarded host ran only the accuracy-decision diagnostics and stopped
after zero active searches and native CPU cleanup. A witness holding the original
process handle observed its actual exit code zero. No staging host is running.
Preserve the backup,
both published generations, failed trials and unfinished isolated drafts. No
production deployment, feature closeout or completion claim follows from these
results.

## Implementation history

The first full-size metadata checkpoint failed the latency gate: repeating current
publication checks for each vector took the semantic budget before ANN. Actual
plans showed keyed lookups rather than a segment scan; parameter typing and join
hints did not remove the cost. A separately approved amendment caches immutable
segment-to-canonical-artifact bindings with an explicit managed-memory budget,
then reads current publication/root/path binding keys once per query.
Only fresh eligible keys grant access before native top-k. Normal vector/chunk
writers insert immutable identities; both normal vector-tombstone paths revoke
their associated records atomically. Rebuild changes epoch and drains leases.
Cache loading verifies complete membership, ordered digest, profile and static
bindings; partial loads are discarded. Current hydration and final fenced citation
validation remain mandatory. A future selective vector withdrawal that leaves its
canonical binding eligible must change this contract before use.

The full-size cold checkpoint required a further approved amendment: preparation
runs independently after database startup readiness, under its own captured
SQL/native lease. Serving checks actual cache residency before embedding and uses
cached-only binding lookup/native borrow; a miss returns lexical results with
`semantic:warming` and signals one coalesced preparation. Retained entries have
no time-only expiry. Capacity pressure, native retirement and shutdown still
evict safely. Only a measured working-set byte excess is a permanent capacity
failure; transient eviction remains retryable warming. An incremental manifest
may need preparation for its new segments; an older readable captured request
continues using its original snapshot. A bounded 250 ms monotonic grace allows
small incremental preparation to finish on that original snapshot. Cold loading
remains outside the request; cancellation and a final readable check still apply.

Real HTTP timing also exposed a separate lexical bottleneck: the combined
Full-Text/ordinal predicate scanned historical chunk bodies before publication
filtering. The correction materialises current artifact/revision keys, covered
scoped chunk keys and their Full-Text intersection inside one request-local SQL
batch. Exact FT matches, exact substring-only matches and remaining FT matches
retain the original ordering, including nullable rank precedence. Only selected
keys receive full current-publication hydration. Exact substring inspection
remains linear in the necessary current bodies; it has not been removed or capped.
Dense hydration likewise drives bounded requested chunk/vector IDs. Full-size
latency acceptance remains an executable gate, including concurrent callers.

Two real CPU pool trials exposed optional reranking consuming the remaining
semantic deadline. An independently approved three-second rerank cancellation
budget now preserves validated fused semantic/lexical results when only reranking
is late. Actual native settlement remains mandatory before lane release; expired
scores are never accepted. This is an extension of existing rerank-failure RRF
degradation, with unchanged relevance and latency acceptance gates.

The captured full-size exact-vector reference exposed a per-query recall failure
at search expansion 256. Bounded replays selected 2048 as the smallest tested
passing width: mean recall@100 0.995, minimum 0.95 and mean recall@10 1.0.
This does not establish document relevance. The subsequent frozen 40-request,
two-caller HTTP run returned hybrid results throughout with valid citations, but
failed end-to-end latency and known-answer relevance. A staging-only one-thread
reranker experiment preserved the production four-thread default; it also failed
the fixed acceptance gates. Investigation of captured candidates and bounded
construction-quality experiments continues before any delivery claim.

Exact duplicate body/header inputs are now grouped before per-channel ranks,
fusion and the 50-input shortlist. Group members retain their own scope and
citation guards. The frozen two-caller run after grouping returned hybrid results
throughout, with no invalid citations or scope leakage, but p95 was 11.83 seconds.
Independent answer adjudication improved from 10/20 to 12/20 questions (60%),
below the unchanged 90% gate. The simpler literal-concept check was 70%; it is
not the adjudicated answer rate. Two complete 50-input CPU rerank diagnostics
took about 24.4 seconds each, confirming a remaining inference bottleneck.
Five subsequent keyed, model-free reads identified a separate answer-loss cause:
three complete answers had current bindings and exact canonical text, but no
complete surrounding line within the existing 16,384-unit UTF-16 disclosure window. Assembly
correctly excluded them. A shorter complete answer passed every read/disclosure
gate but lay outside the current 50-input shortlist. Faster reranking alone cannot
resolve either case. The safety limit remains unchanged; any shortlist correction
must measure actual returnable answers and its own latency cost.

A subsequent replay of two captured raw pools confirmed that filtering these
bindings before fusion moves a valid answer from outside the shortlist to ranks
43 and 15. The focused correction batches current binding/canonical-window and
existing proof reads before fusion, retaining all final read/disclosure/fence
checks and the original raw candidate count. It does not change the privacy
boundary, channel limits, models or schema. Every decision and resulting group
order matched the 335-candidate serial reference. Two measured preflight batches
took 0.98 and 0.39 seconds including disclosure checks, with four SQL queries
each; these are bounded diagnostic observations, not API p95 or relevance
acceptance. The subsequent frozen 40-request quiescent run returned hybrid-ready
results throughout, with all 400 citations verified. End-to-end p95 was 9.95
seconds, within its ten-second target, but dense-selection p95 was 2.55 seconds
and trained reranking timed out throughout. Independent answer presence was
13/20 (65%), below 90%. The additional supported answer appeared at result rank
ten. This remains a failed overall acceptance run; concurrent indexing and final
full-change qualification remain open.

An optional M32/efConstruction512 graph policy now has a distinct native-format
and segment identity, with explicit earlier/new reader compatibility. The existing
construction default and search expansion remain unchanged. Native fixture tests
prove separation, reuse, membership and both-format reads; they do not prove
full-library recall or performance. The separately reviewed four-segment
checkpoint sealed 32,768 vectors with unchanged active publication and observed
native settlement. The reviewed continuation completed all 136 segments and a
fresh exact oracle from the same verified 1,113,836-vector worklist in 60.3 minutes,
without activation. Width 512 passed the original coarse population and the
separately recomputed window-available population: mean recall@100 0.996, minimum
0.96 and mean recall@10 1.0. Widths 1024/2048 also passed. The new-population replay
freshly reconstructed all six coarse scope masks and required bitwise equality
before reusing every stored distance. It evaluated current window eligibility
over all 138 active segments in 250.1 seconds, separately from serving. Independent
review approved width 512 for staging selection. Canonical preparation captured
the worklist under the publication fence with the staging host stopped, so this
does not establish concurrent-serving performance. Candidate activation and
full-runtime qualification remain open.

Background binding preparation now excludes deterministic disclosure-window
failures before semantic top-100 selection. Every segment member still passes
membership/profile/revision verification before any exclusion. The initial
prototype's repeated scalar evaluation exceeded five seconds; materialising each
bounded window once resolved that diagnostic failure. All 219 synthetic cases
and 335 captured bindings matched the native control. One complete 8,192-member
segment took 1.36 seconds including capture and availability evaluation. The
repository adds a credential case to the 219 synthetic cases and verifies runtime
failure cleanup, candidate crowding and rejection of corrupt unavailable members;
54 focused SQL/integration checks passed. These are preparation observations,
not full-library latency or accuracy acceptance. Lexical selection and actual
disclosure guards remain unchanged. The diagnostic selector uses the same
versioned availability policy; its exact oracle population is explicitly the
original scoped population intersected with window availability. New exact
top-k must be recomputed from the entire distance population. Preserve original
coarse-population results and every frozen question and threshold.

The real CPU staging pool now permits one bounded waiting request in addition to
two assigned lanes. The original constructor and production admission retain
immediate busy refusal. A lane transfers only after actual callback settlement;
cancelled pending work cannot invoke models, and shutdown drains assigned work
before model disposal. Queue waiting consumes existing deadlines. The staging
embedding diagnostic likewise waits for actual acknowledgement and rejects late
values. Native-run trace events distinguish execution from queued RPC intervals.
The full-size loaded trial found a further admission problem: two search
callbacks held both model lanes across SQL and citation work for an entire
indexing request's ten-second wait. Embedding and reranking now borrow lanes
separately. An observed semantic owner and a callback-entry/settlement gate retain
corpus leases through late native completion and reject entry after admission
has ended. GPU rerank-only requests retain phase, ownership and cleanup checks;
unconfirmed cleanup propagates to semantic fallback even after early scheduler
cancellation. Focused ownership, race, GPU and captured-generation checks passed,
as did 1,452 Domain tests and 133 Web checks. The changed serving path still
requires fresh full-size and sustained-indexing runtime qualification.

A real staging publication retained both older captured query leases and all
twenty returned citations passed readback. The two searches took 8.50 and 16.37
seconds. This proves bounded publication safety, not the indexing latency gate:
the sentinel's intake and embedding preceded the measured overlap, and the first
new-generation freshness request fell back while preparation completed. The
250 ms correction subsequently passed a fresh publication check: the first
request exercised the grace and returned hybrid results on the new generation,
with exact sentinel readback and both older captured leases/citations preserved.
The grace phase took 255.7 ms including the readable recheck; the waiting budget
remained 250 ms. The two overlapping searches took 10.92 and 17.71 seconds, and
first freshness took 11.05 seconds, so latency still fails. During overlap, native
ANN took about 3.96 seconds and live binding selection 1.27–1.40 seconds; the
trace does not establish a SQL-wait or hardware-contention cause. Source-root
coverage, concurrent embedding and sustained indexing acceptance remain unproved.
No production system or model configuration has been changed.

Full-size preparation also exposed repeated whole-corpus joins/sorting in the
explicit rebuild operator. Its capture and final verification now page raw canonical
vector keys, apply unchanged eligibility only inside that bounded window, and
advance by the last raw key even when no eligible row survives. Canonical vectors
missing from the current projection remain discoverable. Early/middle/late page
digests matched the previous selector, with ordered primary-key seeks and no broad
sort; empty-window and missing-projection integration checks passed.

## Intended behaviour and acceptance

Workspace and root searches use the existing immutable USearch projection with eligibility applied during nearest-neighbour retrieval. SQL remains canonical and supplies eligible identifiers and final retained passages, without transferring vector payloads on the serving path. Keyword and semantic candidates retain existing fusion, disclosure, diversity and citation rules.

The first observable result is a real scoped native search that returns eligible neighbours despite closer out-of-scope neighbours. The next checkpoint is a complete search/read through staging HTTP with real embeddings. A model-free fixture or lexical-only success cannot close semantic acceptance.

Predeclared full-library targets: warm dense-selection p95 at most two seconds; warm end-to-end p95 at most ten seconds with two callers, both quiescent and during controlled indexing; every request bounded by the existing 25-second outer deadline. Record cold latency independently. Normal workload requests must be hybrid-ready; deliberate failure tests must return validated lexical fallback or an explicit timeout refusal. Measure filtered recall against an independently computed exact cosine oracle (mean recall@100 at least 0.95, no query below 0.90; overall recall@10 at least 0.95), and assess a frozen set of document-relevance queries separately. Ties need explicit treatment. Scope leakage and stale/invalid citations must be zero.

## Proposed interfaces and implementation slices

1. **Generation-bound filtered ANN**
   - Extend the application ANN lease with an explicit scoped-search overload; preserve the existing all-scope interface for callers.
   - Add a SQL-backed scoped-selection port returning eligible vector IDs for one captured manifest segment. Bound the set by the descriptor's vector count. Preserve full publication ownership/path predicates, profile/hash/revision rules and workspace containment.
   - Implement a small owned native USearch 2.19.3 read/view/filter adapter using its pinned C ABI. Do not extract the managed wrapper's private handle. Keep callbacks, key sets and buffers alive through native completion, and contain callback exceptions.
   - Reuse segment ownership, no-follow path guards, checksums and continuous top-100 merging. Skip segments with no eligible IDs. Hydrate only selected IDs through current SQL eligibility and disclosure checks.
   - Retain memory-mapped native views under a bounded 8 GiB file-length budget, with capacity-pressure, retirement and shutdown eviction. Measure mapped/private/resident memory and transient active borrows rather than equating mapped length with RAM use. No second verification cache is justified by the current corpus.
   - Explicitly cover earlier generation behaviour; no unnoticed fallback to an unbounded SQL vector scan.

2. **Search availability during publication and semantic failure**
   - Add distinct readable-generation acquisition/checks, preserving strict current-generation callers. Capture the active validated generation's own epoch/version even when its projection trails current publication. Retain original session/applock, durable lease/profile, same epoch, no rebuild and validated/unretired/unreclaimed generation checks. Use captured IndexGenerationSegments and IndexSegmentVectors, with current eligibility before ANN for every readable scope, including all. Revalidate returned evidence bindings under one short publication fence. Unrelated publication can advance; suppression/replacement removes ineligible results; rebuild/epoch changes refuse the request.
   - Give semantic work a separately cancellable earlier cutoff, retaining overall/caller cancellation for lexical work and fresh citation assembly. A detached callback retains native/SQL ownership until it actually stops.
   - Overall deadline exhaustion without validated results uses the existing retryable timeout refusal, including MCP/REST/CLI, instead of a successful empty lexical response.

3. **Real staging acceptance**
   - Add explicit isolated-staging registration for the existing offline BGE embedding/reranking implementation; current functional staging uses synthetic models. Keep all mutable bindings, ownership and endpoints separate from production and reuse only verified immutable J:\Models files.
   - Qualify a complete retained MoHESR staging snapshot and document its exact publication/vector coverage and exclusions. Existing repository-only 39,447-vector staging data is not sufficient.
   - Freeze real paraphrase/identifier/sparse-scope queries and relevance labels before measuring. Keep private text, embeddings, qrels and exports outside Git.
   - Compute an exact scoped vector oracle offline in staging. Exercise publication through the real indexing interfaces, validate current citations, and record generation transitions, SQL vector bytes, eligible-ID counts, cache opens/hash reads and latency/memory.
   - Forced semantic stalls, caller cancellation and source replacement must prove fallback and ownership safety independently of the normal ready-search workload.

## Focused verification and review

- Test first: adversarial scope crowding, nested/sibling paths and publication ownership; zero serving SQL vector payload reads; real native filter/view lifetime and file-tamper refusal.
- Test first: semantic stall preserves fresh lexical evidence, delayed lexical completion has its own budget, outer timeout refusal and caller cancellation differ, and late callback disposal occurs once.
- Disposable SQL integration: suppression, republishing, unrelated publication, rebuild/epoch changes and retirement interleavings. No tests bind production.
- Run focused checks after each coherent slice, then combined relevant suites and required canonical closeout checks. No new build warnings.
- Reassess after native/filter integration and the first real full-size measurement. Do not extend deadlines or lower quality gates to obtain a pass.
- Independent Astra review covers the high-risk design and complete final change. Required corrections stay within the demonstrated requirements.

## Recovery and authority

Derived indexes remain rebuildable from canonical staging SQL. Preserve existing production payloads, data, models and recovery evidence. Retain the task worktree and private staging evidence. A production snapshot/export, if needed, requires a concrete reviewed proposal and fresh specific user approval under AGENTS.md; stage implementation proceeds independently. No production activation is authorised.

## Integrated runtime checkpoint, 10 November 2026

The isolated staging database lacked only the additive publication-metadata
covering-index migration. Independent review approved the exact guarded staging
operation; it completed with all 79 migrations and native readiness true. No
production operation or configuration change occurred.

A fresh two-caller, forty-search run on the complete published snapshot returned
hybrid results for every request, with 400 valid citation readbacks and no scope
leakage. Its API p95 was 10.383 seconds and complete dense-selection p95 was 2.467
seconds, exceeding the unchanged ten/two-second targets. All forty three-second
ranking attempts timed out. The provider-only trace captured 17,634 events with
no loss, truncation or profiling providers. Independent answer-presence review
remains 13/20 in each round; the higher keyword-concept smoke result is not the
acceptance metric.

Ideal ranking of the actual fifty-input lists could reach at most 16/20 and
17/20. The known authenticity and registration answers are absent from the
captured validated channels. Another complete answer survives that gate but
falls from rank 42 without source candidates to 56 with them. Pure replay
reproduced all forty actual shortlists. The affected population has fourteen
source-only inputs and no global/source overlap: a proposed maximum-semantic-vote
change would leave every score unchanged and was rejected before implementation.
No voting correction or larger production candidate budget is justified yet.

A bounded metadata capture retained the actual validated channel population and
reproduced its fusion; it read no SQL vectors or source files. The next native-only
diagnostic distinguishes the initial raw result budget from pre-validation
removal, and tests at most four distinct hundred-result windows for three frozen
queries. It reuses saved real vectors, preserves current scope and native/SQL
ownership, and makes no serving or accuracy claim. Duplicate-heavy inputs are a
measured population property and an unresolved recall hypothesis.

Twenty offline CPU ranking references completed with finite aligned scores in
18.45--29.21 seconds each. Their representative-only projections omit alternative
citation selection and final fresh reads, and do not qualify serving accuracy.
Further GPU or indexing trials are deferred until the candidate population can
support the unchanged answer target. A source-constrained literal retrieval
returned a known answering passage in 1.306 seconds with valid readback; this is
a staging-only workaround. Native verification still reports invalidated,
incomplete original-source coverage, so the published snapshot is not proof that
every original file was indexed. The owned staging host was stopped and its PID
and listener were absent; its outer PTY exited one, with no separate child exit
code or captured native-cleanup acknowledgement. Production was not accessed.

## Expanded candidate quality checkpoint, 10 November 2026

The bounded native diagnostic completed against the unchanged published snapshot.
It located independently adjudicated answering passages beyond the first hundred
physical matches. Its successive filtered windows are an availability diagnostic,
not an exact top-400 oracle. Returning a hundred distinct inputs alone still left
two answers outside the fifty-input fusion shortlist. A separate CPU experiment
preserved that baseline and appended independently validated distinct inputs; all
three targeted questions then received an answering top-ten passage. That saved
input experiment does not establish full-workload or serving quality.

An independently reviewed, explicit CPU-only staging mode now tests this policy
through fresh retrieval interfaces. It makes the actual original ANN100 call and
a separate ANN400 call with the same captured scope and query. It retains every
baseline concrete binding within the single fresh 200-binding disclosure gate,
recomputes the baseline fifty inputs after validation, then appends at most fifty
approved distinct inputs. Model calls remain bounded at fifty pairs, and normal
serving limits and deadlines are unchanged. Final citation assembly still reads
and validates each concrete binding independently.

Focused checks passed 124 Domain, 73 Web and 95 native/SQL cases with no failures
or skips. The isolated payload published with no warnings. The full frozen
twenty-question, two-round, two-caller CPU test then completed forty searches and
400 valid citation readbacks with no scope failures. Independent answer presence
is 17/20 in each round, below the unchanged 18/20 requirement. All thirteen prior
passes remain, and four previously missed questions now pass. The three remaining
misses keep the accuracy gate open.

This private test used a 120-second diagnostic deadline and a 25-minute monotonic
work/admission budget, separate from the serving latency target. Its diagnostic
API p95 was 60.650 seconds; it does not qualify serving latency. The owned host
shut down with zero active searches and an explicit CPU native-cleanup
acknowledgement; its PID and listener were absent afterwards. A bounded native
availability check will distinguish omitted hydrated candidates from selection
and disclosure exclusions before a capacity correction is considered. No further
GPU, production or concurrent-indexing qualification follows from this failed
accuracy gate.

### 10 November: expanded quality and bounded latency evidence

The separate 250-binding CPU quality mode passed 19 of 20 questions in each frozen
round, preserving every previous pass; all 400 citation readbacks were independently
verified. Its 65.231-second API p95 does not qualify serving latency.

A bounded actual SQL plan identified an eligible-record lazy spool rescanned 384
times, examining about 5.27 million rows. A vector-driven join rewrite failed to
improve it and was rejected. Keyed record seeks preserved all candidate/proof data
and reduced median hydration from 743.1 to 36.3 milliseconds. Exactly three keyed
hydration, citation and disclosure joins now use the hint, preserving every
authority predicate. All 118 focused native/SQL regressions passed without warnings.

Fresh measurements on the unchanged full published snapshot passed the component
targets: six warm dense windows measured p95 at 1.509 seconds, and ten final
readbacks plus fenced validation measured p95 at 157 milliseconds. All candidate
identities matched and all 136 native owners were released. These measurements
exclude complete HTTP assembly/serialization and cannot establish API p95.

A separately owned FP16 GPU trial scored two logical 100-input payloads through
four calls of 50, using native batch 4 and one load/unload. The full top-ten overlap
with FP32 retained the known answering passage at rank one. Scoring took 6.274
seconds; complete owner delivery took 9.834. Loading, admission and settlement
remain part of the API clock. All native calls, model disposal, process/job exit
and exact disposable database cleanup were verified.

A separate, explicit staging API trial now reserves at most two whole clients
early, overlaps CPU embedding and retrieval with GPU loading, and publishes scores
only after actual native and durable settlement. It preserves indexed lexical
fallback and fresh disclosure/citation fences. Generic executor failure closes the
coordinator until owned host exit. Focused cancellation, disposal, observer and
dual-cleanup failures are covered; fresh checks passed 196 search and 73 staging
cases, with no warnings.

The cloned corpus contains copied production GPU queue state. It remains intact.
The GPU API trial therefore uses a fresh native disposable scheduler database,
separate from the full corpus snapshot. A bounded external controller requires
the production pool to remain already stopped, holds the existing admission fence,
checks exact process identities and cached model pins, runs one concurrent HTTP
pair, verifies every citation readback, then joins staging host/job exit before
releasing admission. Independent review and execution are pending. Full frozen
FP16 accuracy, complete two-caller API p95, real indexing/publication qualification
and feature closeout remain open. No production deployment or recovery is included.

### First complete API pair and next observation

The first actual pair failed the semantic gate. Its retained QR response contained
ten usable lexical passages with semantic timeout after 8.179 seconds inside the
application envelope; the independent HTTP timing and second response were not
retained because the observer asserted too early. Neither full API latency nor
accuracy is qualified. The captured generation's preparation completed, and the
only GPU task ended with a confirmed receipt and available capacity. Those facts
do not establish which retrieval phase missed the payload-readiness cutoff.

The staging host exited with code zero, an empty owned job, zero active CPU
searches and explicit native CPU cleanup. Its exact disposable scheduler was
removed using native ownership/file guards after exit. A separate reviewed
postflight confirms the production stopped baseline. The initial postflight
observer referenced a closed fence command; this is an observation defect, not
evidence of a production change. Automatic approval review blocked removal of
the disposable credential copy, so that file remains privately recorded.

The six-second payload cutoff, twenty-second semantic budget and twenty-five-second
outer budget remain unchanged. The next explicit diagnostic uses real CPU
embedding, parallel lexical/dense retrieval and fresh disclosure, then captures
and refuses ranking without creating GPU work or claiming trained results. It
compares the first pair in one new host with a second warm pair, without clearing
server or operating-system caches. Failure paths now retain partial phase and
generation evidence; both outcomes are persisted before assertions. Parallel
phase durations are not added together. The full serving and indexing gates
remain open.

The completed readiness observation retained all four responses. API-arrival-relative
payload preparation was 3,163/3,163 ms for the first pair and 2,653/2,158 ms
for the second pair, all within the unchanged 6,000 ms cutoff. Warm dense
selection was 1,543/1,509 ms. These are four diagnostic observations, not a
p95 sample or GPU causality proof. The host exited zero, its job emptied, the
staging listener closed and native CPU cleanup was explicitly acknowledged.
A single instrumented GPU pair is prepared to retain both outcomes and the
backend receipt before asserting qualification; it does not extend deadlines.
The instrumented GPU pair returned hybrid-ready results for both questions:
92 and 100 model inputs, twenty trained citations and twenty valid readbacks.
Both HTTP requests took 10.062 seconds, missing the ten-second target; dense
selection took 2.368/2.282 seconds. The actual shared GPU owner took 9.389
seconds including admission, 2.831 seconds of model loading, 5.549 seconds of
scoring, native disposal and positive durable settlement. All fifty native calls
settled. This first-use pair is not a warm p95 sample and does not reproduce the
earlier readiness timeout. Its host exited zero, the job emptied and the exact
disposable scheduler was removed. Production's stopped baseline was preserved.

The next independently approved approach retains models, batch size four and
request deadlines. One explicit staging qualification mode permits at most
21 atomically consumed host-lifetime cohort attempts: a fixed excluded warm-up
pair and twenty timed pairs covering the frozen twenty questions twice. A new
permit cannot reset the count. Each pair must settle native/durable ownership
before the next, and every timed response must be hybrid-ready with valid
citations. Nearest-rank p95 requires all forty timed observations. Three values
above either threshold make that target unattainable and stop the run after
settlement. Four narrower workspace scopes remain in the frozen workload.
The fixed quiescent qualification passed independently: all forty timed responses
were hybrid-ready, nearest-rank API p95 was 7,750.580 ms and dense p95 was
1,713.700 ms. All four narrower workspace scopes were preserved. Four hundred
citation readbacks passed. Exact body and nine-field source-binding comparison
retained all nineteen independently accepted answering passages in each round;
the prior missed question returned the identical evidence set. All twenty-one
cohorts and 898 native calls settled. Host exit, empty job, native CPU cleanup
and exact disposable scheduler removal were observed. The first-use warm-up
pair took 10.188 seconds and remains separately reported.

Concurrent indexing remains open. Its preparation uses a dedicated rooted
synthetic fixture so native deletion can retire its records, drafts and retained
files. Existing staging metadata shows two paused original roots and eleven
inactive deletion operations. A closed-fixture guard rejects any foreign pending
or running deletion (including an unexpired lease), changed foreign row version,
resumed original root or missing exclusive-writer witness. It permits only the
exact owned operation/root, and returns no work rather than a global fallback.
Six focused checks cover these boundaries; forty-two combined diagnostic and
permit checks pass. The indexing trial uses the existing staging CPU embedding
provider and GPU search; this does not qualify production GPU indexing scheduler
fairness.

The first rooted attempt stopped before search because the benchmark composition
correctly left source-work admission closed. The private harness now verifies both
generated revisions through native physical-source admission, plans only their
durable pending activities and resolves the native job claim store through DI.
A disposable SQL regression reproduced the failure and then prepared both files
through canonical indexing without starting models. Twenty-one focused native and
lifecycle checks passed. Native cleanup removed that failed attempt's exact root
and generated inputs.

Cleanup advanced the publication stamp without changing the ordered 136 immutable
segments or 1,113,836 published vectors. A bounded metadata comparison established
this before rebinding the next trial to that verified generation. The subsequent
actual indexing/publication run returned eight timed hybrid-ready responses,
all below ten seconds, with a maximum of 9,596.836 ms. Four dense phases exceeded
two seconds, with a maximum of 2,161.778 ms, so the predeclared stop rule fired.
All eight timed requests overlapped actual native CPU embedding; the publication
transition and old-generation query leases were observed. This partial run is
not a p95 or full accuracy qualification.

The producer and cleanup helper exited and their jobs emptied; the disposable GPU
scheduler catalogue was removed. Native deletion of the published synthetic root
is deferred at `cleanup-memberships` because a current recovery-reference audit is
required. The supported read-only audit refuses the existing historical
`Recovery/App-before-M32` packet. Preserved application payloads and two SQL
backups must receive truthful, bounded historical reference classification before
native audit and exact cleanup can resume. No audit bypass, SQL state edit,
backup removal or production mutation was performed. Further workloads remain
on hold until this cleanup dependency is resolved. Normal serving integration
also remains open.

On 11 November the recovery dependency was resolved through a separately reviewed,
staging-bound live proof. The proof hashes and holds the retained recovery files,
preserves existing references and admits only the synthetic fixture's exact
manifest and segment retirement. Ordinary recovery classification and retirement
remain unchanged. No recovery packet, backup or source file was hidden or removed
to obtain an audit seal.

The first native retirement reclaimed the segment but exposed a coordinator
reporting defect: its `source-delete-segment-retirement-progressing` disposition
returned false despite committed native work. The coordinator now reports that
specific disposition as progress only after deferral settles. Other blockers,
failed deferral and cancellation retain their behaviour. Ten focused coordinator
checks and twenty-six native SQL/index checks passed, including resumption between
segment and manifest reclamation.

The subsequent bounded cleanup exited normally with its owned job empty. An
independent read-only observation found no owned root, records, revisions, segment
members or generated inputs. Both native retirement paths are absent, the audit
seal is invalidated and no recovery writer remains. The original two roots and
twelve foreign deletion operations retain their complete row versions; the
1,113,836-vector publication retains the same 136 ordered immutable segments.
Automatic approval review rejected deletion of twenty-three other owned
temporary-directory and obsolete scheduler-credential targets. Their exact list
is retained privately; no alternative deletion mechanism was attempted.

Search acceptance remains incomplete. The concurrent dense phase needs a bounded
same-host phase comparison before another full workload. Permanent expanded
ranking must use the existing scheduler and interactive executor with joinable
native/durable settlement and consistent FP16 profile binding. The current
idle-only guard refuses queued or active GPU work; CPU-index overlap does not
prove semantic availability during sustained GPU-index backlog. No production
deployment, recovery or start was performed in this cleanup.
