# Incremental index publication and bounded retention

Date: 20 October 2026.
Status: independently reviewed design; implementation resumed following the user's approval on 20 October 2026. Default-off isolated work is in progress; no new production behaviour is claimed.
Baseline: source-access feature `428484c4`, based on main `61b5e452`. This document does not describe deployed behaviour.

## Outcome and scope

A small source publication must stop saving a complete application-wide ANN index and duplicating its complete SQL membership. Publish bounded immutable USearch segments through a small, stamped manifest. Reuse unaffected segment files, membership and verified native handles. Updates and deletions must preserve current source eligibility, citation, ownership, crash recovery and physical deletion guarantees.

SQL Server remains canonical, SQL Full-Text remains the lexical projection, and USearch remains a rebuildable derived projection. Existing MCP, REST and CLI contracts remain. Internal segment and work budgets split processing; they do not reject large trusted source files.

This increment includes the publication write path, affected SQL membership, the supported global ANN query path, necessary deletion/recovery adaptations, bounded merging/reclamation and a versioned migration. It excludes new models, a different database/search product, scoped ANN, source ACL changes, parser changes and manual updates. The ongoing source-access correction and its recovery evidence must be preserved separately. Production capacity relief is an independently approved operation; this design does not classify existing directories as disposable.

## Current cause

The current code performs whole-corpus work at several points:

| Area | Current implementation | Required change |
|---|---|---|
| Publication | `PublishStageWorker` invokes a generation build for each draft. | Keep the durable stage; publish a manifest of shared segments. |
| SQL preview/activation | `SqlPipelineStore` and `SqlStageTransitionStore` read all eligible IDs/payloads and insert complete generation membership. | Calculate and recheck the affected eligibility change; insert new segment members only. |
| Native build | `UsearchGenerationBuilder` loads/validates the previous full index and saves a complete replacement even for append. | Build only new or affected bounded segments. |
| Global search | `UsearchCorpusAnnLeaseFactory` reads all SQL vectors, validates the whole native projection and opens it again per query. | Capture one manifest and borrow verified segment handles. |
| Query admission | `SqlCorpusGenerationLeaseStore` counts all generation members. | Verify sealed manifest/segment metadata and current stamp without a vector-wide count. |
| Retention | `SqlIndexRetentionOperations` supports individually reviewed historical generations, with same-volume recovery staging. | Add a bounded, reference-aware lifecycle for the new format; bound recovery payloads as well. |

Root/workspace dense search currently uses exact scoped SQL paging. Preserve that behaviour and its scope-before-limit checks.

## Choice and trade-offs

| Option | Benefit | Cost or reason not selected |
|---|---|---|
| **Bounded immutable segments and an atomic manifest** | Small changes reuse unrelated bytes and SQL members; retains immutable publication and recovery. | Requires segment-aware queries, SQL deltas and ownership-aware cleanup. Selected. |
| Batch complete snapshots | Fewer full saves if several sources are ready together. | Still scales every save/validation with the full corpus, requires full-snapshot free space and introduces publication delay. No separate batching subsystem in this increment. |
| Mutate the active index, or keep a large base plus tombstones | Less immediate writing. | Weakens immutable-reader/crash guarantees; existing code records that native removal retains payload bytes. Tombstones can also consume candidate limits. Rejected. |

A manifest initially copies a flat list of segment references. This is proportional to segment count, not vector payload size. Do not introduce a persistent manifest tree or parallel search before measurement shows a need.

## Invariants

1. Canonical eligibility always wins. A withdrawn source, deleting root, superseded branch or stale revision cannot be disclosed through an old manifest.
2. One query captures one corpus epoch/version, profile and manifest before embedding and retains existing final hydration/disclosure checks.
3. Publication atomically commits the selected source/branch, membership change, active manifest and stage/outbox completion. Partial files never become visible.
4. Segment bytes and membership are immutable after sealing. An ID collision or profile/checksum disagreement fails closed.
5. Ordinary publication never reads or rewrites unrelated vector payloads or duplicates unrelated vector membership. Metadata work over segment references is allowed.
6. Cleanup requires proved absence of live references and native owners. Lost SQL sessions, elapsed leases or process IDs alone do not prove native disposal.
7. Source deletion remains incomplete while obsolete payloads containing its deleted vectors remain pinned or unremoved.
8. Path, no-follow, containment and app-owned storage guards apply to segment, manifest, staging and recovery paths. No source-content or OneDrive configuration changes.

## Storage and SQL model

Retain `IndexGenerations` and `ActiveIndexGenerationId` as the public/internal publication identity. Add a format discriminator: format-1 monolith is version 1; segmented manifest is version 2. Existing embedding draft generations and `Vectors.IndexGenerationId` retain their ownership meaning; do not repoint every canonical vector on publication.

Add the following focused persistence:

| Record | Responsibility and key constraints |
|---|---|
| `IndexSegments` | Content identity, model/dimensions/metric/native-format identity, ordered membership digest, vector count, file checksum/length, guarded relative location and lifecycle state. |
| `IndexSegmentVectors` | Immutable `(SegmentId, VectorId)` membership, indexed in both directions. Persist each new segment's membership once. |
| `IndexGenerationSegments` | Ordered unique references from a version-2 generation to sealed segments. Store manifest digest and aggregate count on the generation. |
| `CurrentIndexMembers` | Indexed `(CorpusEpoch, VectorId)` to active segment and publication-owner identity. Describes the active projection, not a substitute for canonical eligibility. Update affected entries only. |
| `IndexProjectionChanges` | Transactional change scopes, keyed by epoch/version/ordinal, sufficient to find both former and replacement publication owners. Root-wide changes may use a root scope. |
| `IndexSegmentOwners` | Durable process-incarnation/native-handle registration and retirement request/disposal state, extending the existing query-owner proof pattern. |

Foreign keys/uniqueness must prevent duplicate active vector membership, missing referenced segments and premature canonical-vector removal. Validation at seal proves the ordered members, finite payloads, profile and native contents. Store a digest of ordered segment identities/counts/digests on the manifest; activation checks that proof rather than recomputing a whole-vector digest.

A segment's identity covers ordered vector IDs/payload checksums and its complete profile/format. Store the native file checksum separately: two concurrent builders may produce semantically identical native files with different bytes. A placement collision may reuse the first sealed object only after proving its full semantic identity and recorded file integrity. Never overwrite an existing immutable object.

Locations are `segments/<segment-id>/index.usearch` and `manifests/<generation-id>/manifest.json`, beneath the existing configured index root. Atomic staging/placement and SQL recovery receipts must distinguish building, sealed, referenced, retirement-requested and reclaimed objects. Existing version-1 `generations/<generation-id>` paths and evidence remain valid.

Initial **staging** sizing is 8,192 vectors or 64 MiB of raw float payload per segment, whichever is reached first. Native graph overhead is measured separately. One source can span arbitrarily many segments; split deterministically by vector ID. Production values are frozen only after the scale gate, with no admission limit added to sources.

## Incremental selection and publication

### Record the complete affected scope

Use the existing `SqlPublishedPassageSelection` rule, parameterised by an affected owner/root set. Do not create an independent approximation of publication eligibility.

Every eligibility writer records its change scope in the same transaction as the mutation and corpus-version advance. Inventory at least:

- `SqlStageTransitionStore`: new publication, superseded unrooted revision and document/processor branch winner changes.
- `SqlSourceScanStore`: suppression, restoration and physical replacement of current revisions.
- `SqlNativeOperationStore`: the initial deleting-root transition.
- `SqlSourceDeletionStore`: survivor activation and removal of historical memberships/payloads.
- `SqlCorpusRebuildStore` and `SqlDerivedIndexRecoveryStore`: epoch replacement, explicit rebuild and pending projection refresh.
- `SqlStageTransitionStore.PublishDocumentIfApplicableAsync` and `SqlRetainedPublicationBinding`: winner selection/binding invoked by preview and commit, including derived children and their physical owners.

Record former and new identities before old links are removed. Expand closure through indexed source, revision, publication and owner bindings. A scope is not necessarily one file: a root deletion legitimately affects the root; a branch replacement may affect several children. A corpus-wide epoch/profile rebuild is explicit full work.

The version-2 publisher reads change scopes since the base manifest's stamp, plus the proposed draft's closure. Compare prior projected members with canonical eligible members for that closure. Load payloads only for added vectors and surviving members of segments containing removed vectors. Unknown scope, a missing change interval or an unrecognised writer fails closed into an actionable repair-required state; do not silently fall back to a full scan on every publication.

### Build and activate

1. Under the existing publication fence, capture base manifest, expected stamp, affected lineage and bounded metadata; preview the same winner selection as commit in a rolled-back transaction. In a new short transaction, acquire shared recovery admission before the publication fence, recheck that captured base/stamp/lineage and segment state, then persist a candidate receipt bound to the existing job/operation ownership. A disagreement retries before building. The receipt pins the base manifest, reused segments and required change-log interval while work is outstanding; a rolled-back preview alone is not a durable pin.
2. Outside the fence, build new segments and replace segments touched by removals. Reuse untouched references. Stream large affected sets in bounded pages; seal reusable candidate membership without one enormous JSON/array.
3. Thoroughly validate new segments against their canonical inputs and place them atomically. Build and seal the manifest with its expected/resulting stamps and affected-lineage digest.
4. Under the publication fence, recheck expected stamp, base manifest, candidate ownership, non-retiring segment state and affected lineage/membership. Commit the source/branch transition, segment references, affected current-membership updates, stamp/pointer and existing stage/outbox receipt together, releasing candidate pins only once durable references replace them. Do not reread unaffected payloads.
5. A changed stamp rejects the candidate before visibility. Preserve the existing three attempts followed by durable five-second retry. Reuse matching sealed segments across attempts; retain no unbounded retry/build backlog.

Ordinary publication can include outstanding committed revocations from the change log. The normal derived-recovery path uses the same delta engine when no draft is ready; it must not continue making whole-corpus snapshots. Only bootstrap, explicit profile/epoch rebuild or specifically admitted repair may use a full canonical scan.

A no-op must not produce another full segment set or duplicate membership. Replay after acknowledgement loss returns the durable stage receipt even if a later publication has advanced the pointer. Abandoned candidates release pins only through the existing job/operation ownership and recovery proof, never a wall-clock timeout alone. Preserve existing zero-eligible/empty-catalogue distinctions; absent projection is not proof of an empty catalogue.

## Queries and native lifetime

Extend the existing generation descriptor/lease to expose a versioned projection. Version 1 continues through its current reader. Version 2 global ANN reads the captured manifest, searches each referenced segment for up to the existing 100 candidates, and merges by distance then vector ID. Deduplicate and reject non-finite/invalid output. Retain current SQL eligibility, scope, hydration and citation checks; no exact ANN recall guarantee is introduced.

Root/workspace dense selection uses exact SQL paging over the captured manifest's segment membership with predicates before limits. Preserve keyset ordering and indexes; prove that the new join does not introduce whole-corpus sort/materialisation. The shared retrieval engine and REST/MCP/CLI outcomes stay aligned.

A single process cache owns immutable native handles, keyed by full segment identity. First open validates file identity/checksum and sealed SQL membership under the existing physical read guards. Warm queries reuse that handle and do not reread all SQL payloads. Search and disposal are serialised for each native handle unless the pinned library's actual concurrent contract is independently proved.

Register a durable segment owner **before** opening native memory, while holding existing shared recovery admission and the publication fence in the established order. Queries retain their ordinary shared recovery/query lease through disclosure. Idle cache entries release query/session locks but retain the durable owner record. They cannot prevent maintenance by holding a perpetual shared lock.

Retirement has three distinct phases. First, take exclusive recovery ownership then the publication fence, recheck reference closure, durably mark an unreferenced segment retirement-requested, and commit. Release both locks before waiting for cache disposal or an acknowledgement; new opens/references refuse the retired state. Second, the bounded cache sweep observes the request and disposes the handle after borrowers drain. Only successful native disposal permits removal of its owner record. Third, reacquire exclusive recovery ownership then the publication fence, recheck the exact intent/segment identity, complete reference closure and absence of native owners, and execute/reconcile the existing guarded physical deletion protocol. Never keep the publication fence while waiting for an owner, and never rely on the first phase's stale closure.

Lost connection/disposal failure retains an uncertain owner; only the existing process-incarnation probe's verified `Exited` observation can recover it. An idle or unknown process must not be cleared by age. Acquisition, retirement and re-acquisition races require integration tests. The minimal exact retirement path belongs in the early lifecycle slice; later scheduled policy adds batching and limits to that same path.

Use a bounded resident cache (initial staging budget 256 MiB of segment file lengths), 30-second idle eviction and a one-second cooperative retirement sweep. Reserve resident capacity on opening; a scan miss does not evict a fitting resident. Overflow entries release at their last borrower, including when a resident releases before an overlapping overflow borrower. The file-byte budget is not an RSS or concurrent-borrower memory ceiling. Query fan-out streams one borrowed segment per query; oversized working sets still reopen uncached segments. The isolated 16 KiB cases retain warm residents and reopen four of six or 46 of 49 segments, rather than every segment; fitting warm cases reopen none. These small synthetic measurements establish reuse, not production latency or general memory bounds. Production resource settings remain a release decision. Retain file/ancestor read guards for the lifetime of a cached handle so a path replacement cannot silently change the proof.

## Bounded merging, retention and capacity

An ordinary publication or catch-up refresh may repack exactly four underfilled active segments, each at most one quarter of both the vector and raw-byte build budgets. Persist their IDs in the candidate and reuse the same survivor build, seal and atomic activation. Other segments remain shared. Fewer than four small segments remain until a later publication, avoiding repeated rewriting and a separate compactor. No periodic full-corpus compaction.

For version 2, distinguish a historical publication **proof** from a physical-retention pin. A completed Publish artefact may retain its immutable generation ID/digest without keeping all segment payloads forever. This is a new-format contract, requiring an audit of all consumers and explicit tests. Active/query/cache owners, unfinished operations, rebuild/recovery/source-deletion references and explicit recovery pins remain physical pins. Unknown reference kinds block reclamation. Do not reinterpret format-1 references or delete their files under this rule.

Proposed steady-state policy: retain at most two unpinned inactive manifests, for at most 24 hours; preserve all currently required pins even if the budget is exceeded and report their reason. Once superseded manifest references are retired, reclaim segments with no remaining physical references. An orphan build has a 30-minute grace and must also have no active candidate/operation. Source-deletion completion overrides optional historical retention for its affected payloads after all mandatory pins drain.

The scheduled pass reserves at most 256 MiB of verification reads per invocation with a 15-second cooperative budget. This includes both verification passes, segment metadata and a conservative manifest bound. It separately limits physical segment attempts to eight, orphan metadata intents to eight, manifest retirement to two and manifest unlink attempts to two. Marking a manifest examines its segment references; those metadata checks are not covered by the eight-segment limit. Rotating selection prevents a pinned prefix from starving later targets; each attempted object still passes fresh eligibility and ownership checks. An object larger than the read budget requires a separately admitted operation. A partial clean page does not acknowledge recovery: the internal log reports an observed drained queue only after a complete, fenced pending/eligible metadata observation. The shared lock order, path guards, durable intent/result receipts and unknown-outcome reconciliation apply. A lost acknowledgement is reconciled, never blindly deleted again.

Routine reclamation of version-2 derived payloads uses canonical-SQL rebuild capability and compact receipts; it does **not** copy every obsolete segment into an indefinitely retained same-volume recovery tree. Keep specifically pinned recovery payloads until their approved expiry/disposition. If a copy is explicitly required, include its destination, space and eventual retirement in the operation scope. The new policy and its activation require specific production approval.

Disk admission accounts for the largest bounded build, merge overlap, staging, recovery pins and the application's reserve. Insufficient capacity defers publication with a current actionable reason; it must not repeatedly build partial files, lose prior published data or misclassify the source as unsafe. Resolved capacity/segment errors disappear from current views when the corresponding state succeeds; retain only useful unresolved tracing and required compact operation receipts.

The implemented admission checks the actual process's available storage before ordinary candidate preparation and before each new native segment. Each new unit requires 1 GiB reserve, 16 MiB overhead headroom and four times that unit's raw payload bytes; existing verified placements can still be reused. This is a conservative observation, not a filesystem reservation or protection from another application's concurrent writes. Low space schedules a one-minute publication retry with `index-storage-capacity-deferred`; successful publication clears the current job's reason and error details. A model-free explicit rebuild retains its same-operation resume path.

Production Recovery is deliberately inaccessible to the IIS identity. Its reference closure must be audited by the privileged updater and sealed into SQL with compact external pins; segment retirement consumes that state without opening Recovery. The bounded classifier must bind the database incarnation, physical Recovery identity, classifier version and complete packet inventory. Recognised self-contained app/config and v1 retention packets do not pin live segments; supported rebuild manifests supply exact external references. Unknown or unfinished packets remain actionable blockers. Recovery writers must invalidate the sealed audit or register pins before filesystem mutation, using a common SQL application lock acquired before recovery/publication ownership. The updater mutex alone does not exclude the format-1 retention snapshot writer.

The isolated Recovery audit implementation binds both canonical roots, physical Recovery identity, database incarnation and complete bounded packet metadata. Successful writers remove their temporary SQL owner record. Interrupted writers default to requiring explicit reconciliation: parent exit does not prove that deployment children stopped. Only explicitly registered in-process writers may be cleared after exact process exit. Audit contexts refuse connection reopening, and physical retirement rechecks the original transaction and both application locks immediately before deletion. A combined run passed 103 checks with one optional format-1 scale test skipped, including actual session loss, retained snapshots, non-empty application recovery packets and overlapping source deletion. The subsequent CLI/parser/writer suite passed 53 checks, including a real child surviving its parent, idempotent completion and clearing resolved audit failures. The SQL-free `index-recovery report` classified all existing production packet variants in a bounded read-only report; `audit` separately seals the guarded SQL reference inventory. Privileged updater integration and initial writer quiescence remain required before production activation; IIS receives no Recovery access.

The updater now registers conservative Recovery ownership before ordinary and continuation writes; Outlook receipt replacement uses explicit in-process ownership. Early maintenance receipts must stay outside live Index and Recovery storage. The schema bootstrap core applies exactly seven reviewed migrations from the 14 October baseline in one original outer transaction, with database/history bindings and a complete metadata fingerprint. Isolated tests prove rollback after owner drift and actual SQL-session termination, committed replay and refusal of changed columns, defaults and unknown history. The combined core/writer suite passes 55 checks. Production schema invocation and coordinated conversion are still required; these checks are not a deployment claim.

Existing accumulated version-1 generations require a separate exact-reference report and reviewed reclamation/recovery plan. Neither age nor this design proves deletion eligibility. The user has approved production index investigation, deployment and migration, including read-only reports. The observed point report established catalogue presence/absence only; it did not establish deletion eligibility. Preserve the independent operational gate and concrete target/rollback evidence before applying cleanup or migration. Completed historical Publish artefacts require their SQL identity proof; an exact physical-only migration cleanup may preserve that provenance without keeping every old native snapshot. This is distinct from ordinary format-1 retention, whose backup and SQL-row removal contract remains unchanged.

## Migration, rollback and deployment

Use additive schema and a version-aware reader/publisher behind `IndexPublication:Mode`, initially using the format-1 publisher. Persist the required projection format in SQL. Unsupported format must fail closed; changing configuration alone must not activate another format. New tables are empty until an explicit bootstrap; do not run a whole-corpus conversion in startup or ordinary migrations.

Bootstrap version 2 once from current canonical SQL using bounded pages, sealed segments and a resumable, pinned operation. Compare exact membership, profile, source/branch eligibility and retrieval in isolated staging, then activate under the existing fences after verifying the expected stamp. Concurrent changes invalidate the final activation; retain/reuse sealed work and capture a fresh bounded delta. Model/profile changes or epoch reset remain explicit full rebuilds.

Explicit conversion and repair candidates have a separate purpose from ordinary delta publication. Bind the operation ID to its original source/target format, stamp, base and model profile; a committed retry returns the saved result without reactivation. A same-operation resume may reuse validated placement, while a new repair uses a distinct physical incarnation so corrupt bytes are never overwritten. Catalogue maintenance preserves unfinished explicit work for resume or explicit abandonment. A sealed worklist requires proved owner exit before takeover or abandonment. format-1 rollback streams current SQL membership and validates native payloads without requesting a whole-corpus ANN result array; an empty format-1 rollback remains an explicit refusal.

The model-free `index-projection` CLI exposes `plan --operation-id <guid> --target-format <1|2>` and `run|abandon --plan <private.json> --sha256 <sha256>`. It requires explicit `ConnectionStrings__FluxKnowledge` and `Usearch__RootPath` settings. Planning reads SQL without creating a candidate and refuses an index root unrelated to the base generation. Execution checks the saved database, physical root, build budgets, plan hash and original SQL binding before preparation. Reuse the same plan after an interrupted process or lost response; a recorded result is not reactivated. The isolated process test kills the operator after segment placement and before SQL seal, then verifies identical files and timestamps on resume. Response-delivery failure is tested separately with an injected writer failure.

Standalone production `run` and `abandon` refuse before path or SQL access. The incremental updater's explicit `-ApplySegmentedIndexProjection` mode consumes a reviewed plan and a separately reviewed stopped-owner receipt. It preserves the existing deny-all hold and disabled companion policy. A lost SQL session after an immediate native action is an unknown external outcome, not proof that the action was refused.

The internal in-process bridge holds the real updater mutex and pins the exact operator, configuration and deny-all hold. Its opaque capability is single-use and bound to the SQL database incarnation, saved plan and physical index root. It waits synchronously on the owning thread for all rebuild work to finish, then verifies the recorded result remains current. The actual PowerShell process owns the native conversion. Its private dependency context rejects missing declared packages rather than substituting PowerShell's copies; exact installed shared-framework dependencies remain supported. Windows native loading uses an extended path after payload containment validation.

The updater plan binds the committed implementation, staged Web/OutlookHost/CLI payloads, schema history and database incarnation, prior payloads, configurations, canonical hold bytes and companion task identity. Stage code-only payloads outside the constrained production drive before planning. The updater reuses and rechecks those E: candidates directly, including the CLI operator; only the original Web/OutlookHost pair is newly copied into I: Recovery. The fixed Apply path upgrades the seven additive migrations atomically, registers in-process Recovery writes, preserves prior payloads, places verified candidates, captures a model-free projection plan, converts, changes only publication mode, then admits the exact converted generation/configuration. It saves start/release intent outside Recovery before each immediate action. Failed or unknown admission requires a newer stop-and-exit proof before further mutation; automatic predecessor restart is forbidden. Completed replay performs only read checks and never reactivates a saved generation.

Disposable tests execute those SQL, filesystem and native callbacks together, with IIS/task boundaries substituted. They verify a fresh Recovery audit, retrieval membership and a normal subsequent publication. Separate fault checks cover each mutation boundary, repeated uncertain starts, configuration drift, interrupted payload placement and path escapes. These checks do not establish production IIS acceptance or sufficient production disk headroom; both remain release gates. `-PlanOnly` requires `-ProjectionPayloadRoot` and `-ProjectionOperationRoot`; Apply consumes `-SegmentedIndexPlanPath`/`-ExpectedSegmentedIndexPlanSha256` and `-ProjectionStoppedOwnersReceipt`/`-ExpectedProjectionStoppedOwnersReceiptSha256`. It preserves format-1 native payloads; their physical retirement requires its own reviewed scope.

For offline maintenance, include `-OfflineProjectionMaintenance` in PlanOnly to bind that mode into the reviewed plan. Apply upgrades, places, converts and verifies the projection, changes publication mode, then returns `OfflineReady=true` and `Completed=false` before any start intent. The pool stays stopped, the companion disabled and the hold retained. Verify current SQL membership, native files and representative retrieval, then apply separately reviewed physical-only cleanup while preserving canonical SQL/history, the working projection, the pre-conversion monolith and recovery references. Resume the same saved plan with `-ResumeOfflineProjectionMaintenance`; it rechecks bindings and resumes the existing fenced start, held probes and hold release without repeating placement or conversion. An uncertain start still requires newer stopped-owner proof. Offline delivery does not require live indexing or uninterrupted-service measurements.

Initial cutover retains the tested internal defaults: at most 8,192 vectors and 64 MiB raw payload per Float32 build unit, 256 MiB of retained native-file capacity with a 30-second idle lifetime, and maintenance attempts of eight segments, 256 MiB and 15 seconds. Retain two inactive manifests for up to 24 hours; terminal orphans require the existing ownership proof and 30-minute grace. These budgets divide work, without limiting source size or vector count. The cache budget is not an RSS guarantee. A larger corpus can reopen non-resident segments; this release does not claim full-cache residency or a production query-latency improvement. The capacity packet must include native graph/metadata, SQL growth, retained payloads, current-SQL rollback and free-space reserve, using measured Float32 sizes.

Production cutover requires the incremental updater's plan, exact schema/payload/configuration identities, sufficient measured headroom, drained format-1 writers/native owners and a tested recovery route. An older binary does not understand the new format. Enforce compatibility **outside the candidate binary**: the updater/operator launcher reads SQL's required format and the exact payload's declared capabilities before every candidate, predecessor or rollback start. Unknown/incompatible capability refuses before starting a process or releasing the deployment hold. The new application's startup check is additional protection, not a way to constrain an old executable. Every supported start/recovery callback must use the external admission check; an unreviewed manual start is not a rollback route. If the existing updater cannot represent the migration/hold sequence, explain the precise gap and obtain direction before using another mechanism.

Each Web, OutlookHost and CLI entry assembly declares its formats in build-produced assembly metadata. The updater reads PE metadata without executing the payload and binds it to the complete payload fingerprint. An exact reviewed `Role=SHA256` assertion may recognise an unmarked format-1 payload for format 1 only; it cannot override malformed metadata or permit format 2. New release/recovery receipts retain those assertions. `PlanOnly -InspectIndexAdmissionPayloads` inspects current payload identities for operational review; the default declarative plan does not read the current payloads. Admission holds shared recovery ownership followed by the existing publication row fence only through the immediate start, task-enable or hold mutation, then releases both before readiness waits. Web admission also requires the effective segmented setting; companion admission has no unused Web-setting requirement. The format-1 `corpus-rebuild` reset remains format-1-only, with refusal before model work and again in fenced mutation paths.

Rollback uses a version-aware payload to build and validate a format-1 projection from **current** canonical SQL, under an approved hold and capacity plan, then lowers the format requirement and permits an older payload. Do not reactivate the pre-cutover monolith after later updates/deletions. Preserve additive tables, canonical data and required receipts. A failed native disposal, unknown operation outcome or inadequate rollback capacity stops the cutover.

## Acceptance and effort checkpoint

Within **two substantial implementation batches**, require a model-free synthetic vertical slice in isolated SQL/runtime/storage/endpoints:

- Publish two authorised synthetic sources spanning several segments; retrieve them through the real REST or MCP pipeline.
- Update one source and delete the other; verify global and workspace results, current citations, and payload retirement after readers drain.
- Crash/restart after placement-before-commit and commit-before-acknowledgement; prove reusable orphan work, atomic visibility and duplicate-delivery idempotency.
- Hold a query/cache owner during publication/deletion; prove stale results are refused, cleanup waits and an unknown owner stays blocked.
- Demonstrate zero rewrites/membership duplication/native reopens for an unchanged warm segment.

The current staging host reports lexical-only search. Add a **staging-only deterministic query encoder** compatible with its deterministic document embeddings and route it through the real ANN/SQL query path; a lexical response cannot count as ANN acceptance. This proves mechanics, not real-model semantic quality.

If this slice is absent at the checkpoint, identify the concrete blocker and narrow the delivery path before adding batching, UI changes or abstractions. Do not call foundations or documents completion.

Scale the same one-source change against 1x and 10x unrelated synthetic corpus sizes. Hard gates: unchanged segments incur zero payload rewrites and zero member-row inserts; warm fitting-cache queries incur zero unchanged native opens/full-payload validations; ordinary publication has no full-vector scan/count; selection equals an independent full SQL oracle; pinned/unknown payloads are never reclaimed. Measure total bytes written, rows/payload bytes read, membership inserts, fence duration, native opens, query latency/recall, peak disk and memory, including merge and cleanup work.

Use repeated staging measurements to distinguish noise from meaningful regressions before fixing a production segment/cache policy. No speedup or latency target is claimed from this design. Global query fan-out/cold opens and genuinely corpus-wide changes still scale with corpus size. Safety, migration and performance evidence plus independent Astra review are required before canonical closeout and any separately approved production action.

Implementation sequence: [incremental index publication plan](incremental-index-publication-plan.md).
