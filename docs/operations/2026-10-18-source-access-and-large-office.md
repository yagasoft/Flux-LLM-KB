# Source access and large Office follow-up

The 18 October production release `20261018T095852Z-a32a97fd2b1f`
was applied through the approved incremental updater. A bounded read-only Preview
accepted the authorised cloud-managed root; six sampled permission refusals
remained. It created no durable root or scan and requested no cloud hydration.

A subsequent approved zero-content-byte probe used the bound IIS application's
actual token. All three exact sampled files returned Windows error 5,
`SourceAccessDenied`, with unchanged file identity and ACL metadata. This proves
application-account denial for those samples. It does not locate the denying
parent component or explain every historical scan report. No permissions changed.
The requested architecture document was already indexed and readable through
the supported Corpus view; no retry was needed.

## Focused Office correction

A finite retained-only experiment found two Word XML parts above the old 64 MiB
limit. Raising only that limit to 128 MiB let one current document extract
1,235,471 UTF-8 bytes in about one second. The other document then hit the
unchanged two-million-element limit. A third input had a compound-binary
container; that signature alone proves neither corruption, pre-OOXML Word format
nor encryption. Private paths, hashes and receipts remain outside Git.

The prepared OOXML v3 descriptor uses a new immutable capability identity.
New DOCX/XLSX/PPTX work may stream XML parts up to 128 MiB. Total XML expansion
remains 512 MiB, with two million elements, depth 128, 2,048 entries, 8,192
relationships and a 169:1 XML compression ratio. Compressed input stays at
128 MiB and extracted text at 200 MiB. Visio's shared parser keeps its 64 MiB
part limit. Package paths, relationships, encryption, checksums and source
authorisation are unchanged.

Exact v1/v2 descriptor pairs remain eligible for historical registration,
publication and source-status projection. Old runnable registrations retire;
the deployment readiness fence also recognises v2 pending/running work and
unsettled force requests. Activation must drain or refuse that work.

An exact v2 predecessor refused with `office-document-part-unsupported` can be
submitted through the existing retained `policy_reprocess` preview/commit route.
The route still verifies the root configuration, revision, retained hash,
completed attempt, leases, row versions and binding uncertainty. It creates a
successor without resetting prior attempts or replacing earlier lineage.
Startup and ordinary promotion do not replay terminal branches.

Current issue projections also recognise exact v2 successors. A later successful
successor keeps its resolved predecessor out of current failures across the v3
upgrade; a different retained hash does not hide an unresolved failure.

## Verification and production boundary

The disposable cap experiment built without warnings and used no production
database, models or original-source bytes. Test-first checks reproduced the old
part refusal and absent v2 retirement/recovery fences. Focused verification then
passed 122 OOXML domain checks, 70 SQL integration checks and 19 Web projection
and presentation checks, with no skips or new warnings. A new fixture initially
reused an idempotency key across v1/v2 cases and correctly received a confirmation
refusal; distinct test keys resolved it without changing product guards or
assertions. Canonical closeout subsequently passed all required native gates and
3,590 tests on both the feature and Main checkouts, with 30 opt-in skips and
zero-warning builds. The reviewed squash is `c5d5f84f`.

Production deployment and any exact-item recovery require fresh user approval,
current independent operational review and the incremental updater's native
drain/validation-hold safeguards. The approved ordinary incremental release
`20261019T060941Z-c5d5f84f96da` installed matching application/companion payloads
with unchanged configuration and released the validation hold. One exact native
policy recovery was admitted; retained extraction and the first three pipeline
stages passed. Embedding is progressing through normally scheduled requests;
publication and supported readback remain open. No broader replay, ACL change,
new scan or model acquisition was performed. A verified add-only cached Python
runtime was staged separately; application runtime activation remains pending.
Paired rollback is valid only before any attempt to release the deployment hold.
After a release attempt, including a successful release before explicit item
admission, workers may have written durable state. Preserve the installed
payload, immutable metadata and recovery receipts and use reviewed forward
recovery rather than replaying predecessor rollback or relabelling v3 work to
fit an older payload.

The ordinary updater now records the release attempt before removing the hold.
Both recovery and final cleanup refuse predecessor restoration or a second
release after that boundary. Disposable fault injection covers failures before
and after deletion, a later probe failure, successful release and the existing
paired rollback before the boundary, in both PowerShell runtimes.

Source permissions and OCR startup under the application identity remain separate
unresolved causes. Oversized XML, unsupported binary formats, encrypted content,
image bounds, checksum failures and uncertain native ownership remain current
issues. The user's revised direction trusts all added sources and requires
continued work across format, encoding, count, nesting, image and size restrictions.
Unnecessary processor refusals should give way to suitable streaming, batching or
resumable work; the runtime must still report actual access, integrity and
ownership failures truthfully. This delivery does not claim those follow-ups are
already implemented.

## Active projection probe follow-up

Bounded SQL metadata found the full retained-checkpoint audit holding the global
publication fence while source operations waited. A focused correction gives
routine Healthy probes an active-projection read, preserving active integrity,
stamp, path and empty-catalogue checks. Full startup, fault/exclusive recovery and
cleanup validation remain unchanged. Five new checks first failed against the
old behaviour; the corrected combined recovery suite passed 114 tests with one
opt-in scale skip. Independent implementation review approved the correction;
deployment is pending.
The observed IIS shutdown crash remains unexplained and is not claimed fixed.

## Office package and text fidelity follow-up

Sixteen sealed retained samples exposed three package compatibility problems:
ignored extension-default content types, rejected package-rooted internal
relationships and rejected empty directory markers. Further inspection found
omitted worksheet inline strings and slide text read from the wrong namespace.
The slide correction uses DrawingML text; PresentationML text decoys are excluded.

OOXML v4 corrects these cases and removes its arbitrary XML element-count ceiling.
Existing byte, depth, checksum, package-path and external-resource controls remain.
The shared Visio extractor retains its separately versioned semantics and limits.
Versions 1–3 stay valid historical bindings, with old runnable descriptors retired
and exact recovery available for the v1/v2/v3 refusals the correction addresses.
Neither startup nor ordinary promotion replays terminal work.

Test-first verification reproduced 13 failures against v3. The corrected batch
passed 168 domain checks, 69 SQL integration checks and 15 Web projection checks,
without skips or build warnings. Four checks explicitly preserve Visio behaviour.
The actual v4 assembly then produced a nonempty retained text child for all 16
sealed samples in an isolated runner, without production access, models or
original-source reads. These are implementation checks; production publication
and supported readback for the v4 cohort still require the reviewed deployment
and finite exact-item recovery.

## Exact document recovery and publication throughput

The 19 October OOXML v4 release recovered one exact retained workbook into
23,136 UTF-8 bytes. All five stages and its exact selected publication completed;
supported Corpus readback returned nonempty text at 10:20 UTC. The larger Word
recovery remains in embedding, with durable checkpoint progress observed. Neither
admission is to be repeated.

A common Image/PDF recovery path uses the existing trusted-local
`documents reprocess` command with the exact failed Extract job and expected root
configuration revision. It creates a distinct child and durable recovery proof;
it never resets the failed predecessor. Admission, claim, commit, registration
and Publish recheck source scope, configuration, hashes, old attempts/delivery,
selected publication and native settlement. Only the specific historical runtime
failure or image-dimension refusal is eligible with completed native receipts,
terminal dispatch and no retained capacity/worker/uncertainty binding. The
historical encrypted-PDF case instead requires zero native execution history.
Unrelated historical GPU uncertainty and dead letters remain untouched.

A PDF that opens without a password is now read even if its encryption flag is
set. A genuine password-open failure reports `pdf-document-password-required`;
other read failures cannot become partial success. Images retain full resolution
within a 256 MiB decoded-bitmap budget, accounting for the additional rotation
bitmap. Encoding uses the live pixmap and checks the existing 16 MiB PNG bound
before a managed copy. Tests cover the exact identity/rotation allocation
boundaries, one row above each boundary and valid high-entropy output above the
encoded bound. The new fixed Python launcher uses the separately verified,
add-only cached J: runtime. No download, weights change, source mutation or ACL
change is part of activation. Actual application-account OCR remains a live gate.

The old failed child leaves current processing views only after the exact
successor has all five completed pipeline stages and is the selected successful
publication. Wrong revisions, deletion, incomplete stages, unrelated source
failures and mismatched lineage cannot hide a current problem. This does not yet
compact general audit events or historical scan observations.

Production metadata also identified a serial publication bottleneck: adding
five or six vectors rebuilt roughly 252,000 vectors and a 1 GiB graph for about
five minutes. The prepared correction can reuse an exact validated active graph
only for append-only membership. SQL captures the base descriptor and membership
under the publication fence. Strict held ancestor/file guards protect native
loading; a mutable RAM copy receives only new vectors and saves to fresh staging.
Full checksum, native payload, key-count and cosine validation still precede
activation. Exact membership uses distinct SQL IDs, matching native cardinality
and bitwise retrieval of every payload. Approximate metric probes do not enumerate
membership: repeated or distant vectors need not all appear in a search result.
Removal or payload replacement uses a full rebuild, since native tombstones can
retain deleted vector bytes. Missing bases can rebuild; unsafe paths, access
refusal, cancellation, allocation and unknown native errors remain fail-closed.
Recovery/rebuild paths and activation/job fences are unchanged.

A disposable actual-product experiment on 16,384 synthetic 256-dimensional
vectors took 18,267 ms for full construction/validation and 564 ms to append four
vectors with base loading, validation, save and final validation. The original
19,207,888-byte file was unchanged. This used no model or production binding and
measured no SQL phases. A separate disposable SQL test verifies dispatch,
publication and lexical readback. Production-scale latency and bounded real-item
acceptance are still required. Full rebuilds for removals/replacements remain
costly; this increment does not change pump concurrency or lease duration.

Release `20261019T115128Z-3fabb2e571d0` deployed the common document recovery,
image/PDF handling, cached runtime binding and immutable append correction. The
incremental updater confirmed unchanged retained state and released its validation
hold. Live, readiness and index-health endpoints returned HTTP 200. The first
one-image command exited with a generic availability error and no returned
successor. Exact bounded reconciliation found only its historical branch, with
no new recovery branch at observation. Its attempt marker is preserved; live OCR
success and further cohort admission remain unproved.

The follow-up found a second publication cost: activation issued one SQL insert
per vector while holding the serializable publication fence. A disposable product
regression reproduced 1,025 membership commands for 1,025 vectors. Set-based
activation now sends the typed vector IDs in one parameterised `nvarchar(max)`
JSON value and inserts the missing set with `OPENJSON`. It retains the transaction,
publication fence, row/range locks, immutable-generation comparison and exact
membership checks. It changes no schema, isolation, retries or timeouts.

Four focused SQL checks pass for one-round-trip exact membership, repeated
activation/replay, incompatible membership refusal, duplicate snapshot refusal
and rollback after cancellation immediately following the insert. Live metadata
also recorded lock waits, but a later statement-classification sample did not
capture the earlier blocking command. The generic CLI exception is not yet
attributed conclusively to this loop. Independent review, canonical closeout and
incremental deployment of the SQL correction precede further bounded acceptance.
General resolved-event compaction, source permission failures and the native IIS
crash cause remain separate open work; no source ACL or model content changed.

Cold empty-root overview reads now materialise the existing current-file query
before constructing derived branch summaries. If that set is empty, no branch
query is needed. The two-second read budget, deletion transaction and both
consistency fences remain unchanged. SqlClient zero-number cancellation is
reported as budget unavailability only after the internal deadline expires and
the caller remains active; the original exception is retained. Genuine SQL
errors, including timeout and deadlock, remain unchanged.

Release `20261019T140228Z-bc82af83377f` deployed the set-based membership and
cold overview corrections. Canonical verification passed on the feature and Main,
and live, readiness and index-health checks returned HTTP 200 after deployment.
The next one-image command returned promptly but refused its exact binding.
Bounded metadata and historical code confirmed a compatibility defect: older
document inputs used `retained-document-input:{memberFingerprint}`, while the
recovery check required the newer parent-qualified locator.

Fenced predecessor validation now accepts that exact earlier literal only after
independently deriving the member identity and checking the existing parent,
child, artifact, failed-job, completion and native-settlement proofs. New children
still use the parent-qualified locator. No persisted locator is rewritten. Both
earlier image and PDF regressions failed before the correction; all 102 document
handoff/recovery checks now pass, including publication, unchanged predecessor
history and wrong-locator/hash, ambiguous-owner and uncertain-native refusals.
The next release and exact live acceptance are recorded below.

## Positive file bindings and physical revision history

Release `20261019T151854Z-94753620ba32` subsequently deployed the narrow earlier
locator compatibility. The exact settled image completed all five stages,
selected its recovered publication and returned nonempty text through the
supported Corpus page. Its matching native result, receipt and terminal ownership
checks passed; this does not assess OCR fidelity. The three separately authorised
access repairs also reached selected publications and supported readback through
normal processing. No source content or OneDrive configuration changed.

Of thirteen explicitly selected Office candidates, nine admitted new v4 work and
four refused ambiguous physical bindings without repeat admission. All nine
produced retained text; the bounded 19 October 16:17 UTC snapshot confirmed one
selected publication with five completed stages, while eight remained at
embedding. Supported readback for that selected document subsequently passed at
16:35 UTC; the other eight require their own fresh completion evidence.

Disposable integration tests now prove positive file capture resolves edits,
replacement content, renames and reversions despite an unavailable sibling.
Suppression requires the actual held hashing handle, current scan ownership,
unchanged root admission and scope, and a single physical link. Configuration or
ownership drift, case aliases, derived owners, pre-commit mutation and retried
stale captures refuse without changing bindings. Ordinary-file ingestion under
an existing `FollowLinks` setting remains compatible and does not gain suppression
authority. A same-content replacement with a new physical identity still refuses
instead of rewriting immutable history.

Later physical revisions now remain eligible for exact retained policy and
settled-document recovery when no unsuppressed competitor exists. Admission,
claim and commit recheck their respective current-binding proofs; derived owners
and the earlier document contract remain restricted. The focused binding/history
batch passes 28 checks. Independent review and canonical squash closeout passed
as `838ddb42`; each checkout passed 3,862 tests with 30 existing opt-in skips and
zero warnings. The combined `8491d35e` release deployed this correction on
19 October. A finite observation during ordinary reconciliation showed conflicting
physical revision rows fall from 57 to 40; these are rows, not a distinct file count.

The scan issue flood was independently grouped as access-denied reports on
distinct paths. Broader stale-inheritance repair still requires the requested
permission scope; only the original three leaves have been changed. Resolved
event compaction, other format limits, native uncertainty and measurable GPU
throughput remain separate follow-up work.

## Bounded background embedding requests

One reviewed 30-second observation of existing work on 19 October captured seven
complete embedding batches and one incomplete batch. Median model load was
2,174 ms, inference 434 ms and unload 186 ms. Canonical tests ran concurrently on
the same host; these are phase observations, not a controlled throughput benchmark
or queue-time attribution. Missing phases were excluded rather than counted as
zero. The observation added no workload and changed no source or model files.

The correction amortises loading across at most sixteen passages in one owned
request, with sequential tensor runs still limited to four. It preserves model
and native settings identity, token and memory bounds, ownership, cancellation,
ordered outputs and atomic final checkpointing. Direct non-GPU work stays at four.
Unfinished four-input requests retain their exact bytes and digest; failed groups
retain the existing retry limit. Checkpoint and queue checks prevent regrouping
over conflicting or unsettled native work. Mixed four/sixteen-input history remains
readable by explicit recovery. Larger persisted requests require forward recovery
or explicit reconciliation before any older-binary downgrade.

An identical producer handoff can be acknowledged after the executor claims it
or saves its checkpoint. This validates the persisted ordered inputs and digest
and reuses the scheduler's exact parent-lease replay checks. It does not create
another dispatch, change results or release native ownership. Selecting a
different group continues to require the stricter unresolved-work checks.

The model-side regressions pass 45 checks, and the combined model/checkpoint/
request/recovery run passes 161 with no failures or skips. They cover lost-handoff
replay before and after executor claim/checkpoint saving, unchanged ownership,
mixed old/new completion, retry-budget preservation, conflicting/corrupt
groups, uncertain ownership and existing input-size constraints. Independent
final review and canonical closeout passed as `8491d35e`: both checkouts passed
3,883 tests, with 30 existing opt-in skips and zero build warnings. The paired
incremental release completed at 18:18 UTC with unchanged configuration, released
validation hold and healthy endpoints. A single unsaved read-only Preview accepted
the exact approved cloud root: 11 matched files, no blocked files and five permission
denials in an explicitly incomplete sample. No cloud-only content was recalled.

## Scheduler recovery after deployment validation

The subsequent finite reading found the larger Word document at 660 of 2,004
durable vectors, with an earlier four-input request still waiting for admission.
An advisory scheduler reading showed ready work, available capacity and no pending
wake. This does not establish improved throughput or native settlement. The code
and isolated regression reproduced a startup gap: an empty admission beneath the
deployment hold consumed the startup recovery opportunity, while release supplied
no new notification for work already queued.

The focused correction waits through the ordinary hold and remembers observed
holds, including permitted rebuild operation. It reconciles any pending admission
with its original identity before starting a distinct recovery round after full
release. The SQL admission fence, permitted rebuild restriction and all native
ownership/result checks remain in place. Thirty-three scheduler checks pass,
including seven new hold/release cases and a disposable SQL test that admits
pre-existing work without adding a task or notification after release. Independent
review and canonical integration passed as `e7cdded7`: each checkout passed
3,890 tests with 30 existing opt-in skips and zero build warnings. Incremental
release `20261019T200859Z-e7cdded710cb` preserved matching application/companion
payloads and configuration, released the hold after unchanged-state validation
and passed the approved health checks. Existing G6 work advanced from 936 to
952 of 2,004 durable vectors, including completion of a sixteen-input request.
Progress was already occurring before this release; it is not an exclusive
throughput attribution. The document still awaits full publication.

A fresh exact graph check confirmed seven of the nine admitted Office owners
with selected publications and five completed stages, while two remained in
embedding. One newly proved item passed supported nonempty Corpus readback.
The four earlier ambiguous bindings were not readmitted. An advisory reading
grouped 380 uncertain GPU tasks: 117 with completed parents, 103 with suppressed
source bindings, 60 other document tasks and 100 interactive tasks. All retained
recorded nonterminal dispatches; these metadata groups do not establish physical
settlement or authorise disposition.

The scheduler guarantee covers one updater validation
hold and full release for its newly started worker. Repeated in-place live holds,
or an entire unobserved hold cycle inside one call, are outside this correction.

## Resolved diagnostic cleanup

The next slice removes earlier matched activity, pipeline and retained-processor
diagnostics from current event pages before pagination. Its proof follows the
selected document publication, current unsuppressed owner and child bindings,
retained artifacts, recognised processor history and five completed stages.
Office v1–v4 policy chains may change derived text without changing their physical
owner. Explicit image/PDF recovery retains its separate child relation. Access
failures, later failures, unknown policy history and malformed correlations remain.

Physical removal runs separately after a successful scan, subject to fresh
completion authority and publication-first lock order. It inspects at most 1,000
root-scoped diagnostic candidates and deletes at most 100 proved-obsolete events
per attempt. It preserves pipeline/native history and never replays an ambiguous
cleanup commit. A small wrapping cursor prevents a persistent unresolved prefix
from starving later candidates; it changes no proof or deletion authority.
All 182 affected persistence checks pass with no skips, including pagination, proof refusal,
bounded deletion, changed or expired authority, concurrent publication/root/scan
changes, lost commit acknowledgement and an unrelated diagnostic backlog. A cold
cleanup regression also proves that an unresolved prefix cannot starve a resolved
tail. Query-plan diagnosis found repeated proof compilation taking 3.9 seconds
against 8 ms execution; one shared proof now passes the unchanged two-second SQL
budget. Temporary plan-capture instrumentation was removed. Independent review,
all 21 canonical steps and paired incremental deployment passed as `6371aada`;
both checkouts passed 3,928 tests with 30 existing opt-in skips and zero warnings.
The finite health checks and exact existing document proofs passed. Browser
verification did not establish that the Events filters applied, and no
post-release physical compaction was proved in the finite observation window;
these remain explicit acceptance limits.

## Captured input handoff and document publication recovery

The subsequent exact graph confirmed all nine admitted Office v4 owners published.
The separate large Word revision completed its 2,004-vector checkpoint and passed
supported Corpus readback. Its completion predates the latest diagnostic release.

An approved three-file access pilot restored normal inherited file permissions
and explicit IIS read access. All three passed actual app-pool-token guarded opens
and ordinary source capture. One passed publication and supported readback; the
other two completed extraction through Embed but failed Publish with SQL command
timeouts. No permitted stdout phase logs were available, so the timeout phase and
underlying contention remain unproved. The wider prepared permission cohort is
paused until the complete pilot succeeds. No full-root scan was launched.

The coordinator offered unlinked retained inputs only after its scan queue emptied.
It now offers them after each successfully persisted scan completion, including
failed scans, before claiming another scan. Registration stays outside scan
failure classification, preserves cancellation and deletion priority, and wakes
the outbox only when work is offered. This improves scheduling fairness; the
existing registration call is not a bounded work quantum.

Explicit publication recovery previously accepted only original retained text.
It now supports recognised derived document inputs using the same side-effect-free
lineage validation as ordinary publication. Recovery retains its separate stricter
checks for the current physical owner, one completed unleased branch, exact
retained artifacts, four completed unleased preceding stages and existing Embed
integrity. Confirmation covers relevant owner, member, processor, artifact and
native versions; a changed binding refuses commit. Active or uncertain native
ownership refuses recovery and remains untouched. Recovery requeues the same
Publish delivery, without repeating parsing, embedding or GPU execution.

These two application corrections require independent review, canonical closeout
and a fresh reviewed incremental deployment before production use. The pilot's
two terminal jobs require exact supported recovery and subsequent publication
and readback proof; permission success alone is not indexing success.

## 21 October index-storage recovery

The three access pilots subsequently passed publication and supported readback.
The specifically approved stale-permission cohort completed 25,385 existing files
including those pilots; source contents and OneDrive configuration were preserved.
This proves the scoped access repair, not publication of every newly readable file.
The generic source-access client/helper is implemented and remains default-off;
additional folder grants and privileged helper activation still need their exact
approved operational scope.

The independently reviewed offline segmented-index migration and verification
completed. Physical cleanup removed 3,010 proven obsolete files and increased free
space by 744,829,267,968 bytes. Canonical SQL, the working projection, preceding
monolith and necessary Recovery evidence were retained. A subsequent power loss
did not undo those completed operations. Current delivery is correcting the
small-removal refresh query and completing readiness and bounded readback; it does
not repeat migration, cleanup or full-source ingestion. Historical uncertain GPU
tasks and other unresolved item causes remain separate, actionable work.
