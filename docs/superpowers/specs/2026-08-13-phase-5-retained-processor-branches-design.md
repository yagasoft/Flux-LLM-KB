# Phase 5 retained-content processor branches design

## Status and decision

**Status:** approved for implementation planning on 2026-08-13.

This records the user-approved Phase 5 clarification. The first processor is archive-zip-expand. Its explicit registration/configuration makes the installed local handler runnable in this disposable, non-production environment. A runnable capability automatically replays eligible durable work in bounded batches. The disabled state remains available for tests and future operations, but is not Phase 5's normal operating posture.

The ZIP vertical slice delivers the smallest reusable retained-processor execution contract needed for actual ZIP work. It is not a separate, productless foundation milestone.

## Approved boundary

Phase 5 owns durable retained-content processing irrespective of whether the source revision originated from Outlook, Gmail/IMAP or a watched file. It builds on SourceRevision, SourceArtifact, SourceActivity, DeferredCapability, source activity idempotency, existing SQL pipeline leases/fencing, durable activity evidence and the immutable revision/root/private-storage binding.

The first observable vertical slice is:

~~~text
deferred retained ZIP activity
  -> enabled archive-zip-expand capability
  -> bounded automatic SQL claim
  -> verified retained private artifact
  -> safe ZIP member streaming to content-addressed storage
  -> child SourceRevision, SourceArtifact and activity records
  -> fenced branch receipt and sanitised operator evidence
~~~

No stage opens an Outlook, Gmail, IMAP or watched-file original. It reads only verified bytes from an app-owned retained artifact.

Phase 5 includes the remaining archive, document/container, deterministic code, and deterministic image/media-metadata branches. Phase 6 remains the exclusive home for model-dependent processing.

## Activation and automatic replay

The first source-neutral capability is exactly **archive-zip-expand**. The handler descriptor is InProcess, accepts ArchiveExpansion activities, has processor version phase-5-zip-v1, fingerprint phase-5-zip-retained-archive-v1, and output contract retained:archive-zip-expand.

RetainedProcessorOptions has these defaults:

| Option | Default | Meaning |
| --- | ---: | --- |
| ArchiveZipExpandEnabled | true | Explicit Phase 5 configuration enables the installed ZIP handler. |
| AutomaticReplayBatchSize | 16 | At most 16 claims are processed in one bounded batch. |
| MaximumCompressedInputBytes | 67,108,864 | 64 MiB retained ZIP input limit. |
| MaximumEntryCount | 256 | Maximum central-directory entries. |
| MaximumExpandedBytes | 134,217,728 | 128 MiB expanded member total. |
| MaximumMemberBytes | 16,777,216 | 16 MiB per member. |
| MaximumLogicalPathLength | 512 | Normalised entry path limit. |
| MaximumCompressionRatio | 100 | Expanded-to-compressed ratio ceiling. |

RetainedProcessorActivationService is a local hosted service. It registers the descriptor, persists a runnable registration only when the option is enabled and the installed descriptor exactly matches it, promotes signature-confirmed ZIP candidates, claims a batch, processes it, and wakes itself after a non-empty batch. A false option returns before promotion, claim and replay. It does not require a per-item operator action.

The service performs no model, GPU, OCR, vision, speech, embedding, reranking, network or source-adapter work. It does not activate Outlook, construct COM objects or validate a mailbox.

### Legacy deferred promotion

The generic local-source-capability is never bulk-mapped. The activation service reads only a candidate's retained artifact header through the verified reader. It promotes only a normal ZIP signature PK\x03\x04 or an empty ZIP signature PK\x05\x06 to a new ArchiveExpansion activity under archive-zip-expand.

The old activity becomes CancelledSuperseded with reason code superseded-by-archive-zip-expand and an activity relation pointing to the successor. Its original required capability, idempotency evidence and activity history remain durable. A non-ZIP generic activity is not changed.

This retained-only signature promotion is source-neutral: an Outlook-originated retained revision can become eligible without Outlook code, source rereads or mailbox access. No Outlook-specific identifier is placed in the new processor contract.

## ZIP policy

The first processor accepts only a signature-confirmed ZIP retained artifact. It does not accept self-extracting, multi-volume or nested archives and it does not interpret ZIP-based document containers.

| Condition | Required outcome |
| --- | --- |
| Input larger than 64 MiB | archive-input-too-large |
| More than 256 entries | archive-entry-count-limit |
| Expanded total above 128 MiB | archive-expanded-total-limit |
| Member above 16 MiB | archive-member-size-limit |
| Ratio above 100:1, or positive expanded size with zero compressed size | archive-compression-ratio-limit |
| Empty, rooted, drive-qualified, traversal, alternate-data-stream, NUL-containing or overlong path | archive-entry-path-invalid |
| Link, reparse target, encrypted entry, unsupported compression or multi-volume input | archive-entry-unsupported |
| Duplicate or conflicting normalised member identity | archive-member-identity-conflict |
| ZIP member that is itself a ZIP | nested-archive-depth-limit, recorded as a deferred child outcome; no recursive claim |
| Caller cancellation | processor-cancelled retryable attempt; no completion receipt |
| Missing retained artifact | retained-artifact-missing |
| Rebound/private root, containment, no-follow, size or checksum failure | retained-artifact-path-invalid or retained-artifact-checksum-invalid |

The processor validates central-directory metadata before it streams a member. It accepts only ordinary files and directory markers. It rejects Unix symbolic links and Windows reparse-point encodings. It treats ZipArchive parser exceptions as bounded, sanitised failure outcomes, not as source corruption.

Safe member streams go directly to ContentAddressedSourceArtifactStore. They are not extracted to a source root, a temporary operator-visible location or a filename created from the archive entry.

## Child provenance and branch completion

A safe member has a normalised identity fingerprint calculated from its parent stable identity and SHA-256 of the normalised entry name. The child SourceRevision:

- has ParentRevisionId set to the immutable parent revision;
- uses the same source root;
- uses a synthetic canonical locator made from fixed text and the member fingerprint only;
- has OriginKind ArchiveMember, so ordinary root scans do not suppress it;
- receives a stable identity based on parent stable identity and member fingerprint, allowing a later parent revision to create a linked child revision rather than a duplicate source;
- owns one content-addressed SourceArtifact and a classifier-planned child SourceActivity.

The raw member name is transient processing input and must not appear in branch tables, public projections, events or audit details. Existing private SourceRevision fields remain private implementation data; public views use safe aggregate state and existing root-relative display conventions only.

Each in-scope entry has exactly one SourceProcessorBranchMember row keyed by branch and member fingerprint. A member is either represented by a child revision/artifact/activity or has a durable blocked/deferred outcome. A parent completion receipt is committed only when all in-scope members are accounted for.

A newer parent revision reconciles generated member identities for that parent's stable source identity and suppresses obsolete generated children without deleting historical revisions. Root enumeration suppression excludes OriginKind ArchiveMember, preventing a later filesystem scan from destroying archive-derived provenance.

## Execution, fencing and migration

The ZIP slice adds an additive migration with five elements:

1. **SourceProcessorBranches**: exactly one per processor SourceActivity. It stores immutable input revision/hash/version/fingerprint, state, current lease owner, expiry, monotonic lease generation, bounded attempt count and completion receipt fingerprint/counts.
2. **SourceProcessorAttempts**: a per-branch ordered durable receipt with lease generation, start/finish timestamps, outcome code and bounded sanitised evidence.
3. **SourceProcessorBranchMembers**: unique branch/member-fingerprint state, child identifiers where created, safe disposition/reason code and bounded size metadata.
4. **SourceActivityRelations**: unique predecessor/successor relation with relationship kind superseded-by-retained-processor and a safe reason code.
5. **SourceRevisions.OriginKind**: defaults to RootDiscovered; ArchiveMember enables branch reconciliation and excludes normal scan suppression.

A claim returns RetainedProcessorClaim containing opaque identifiers, source revision id, immutable hash, descriptor version/fingerprint, lease owner and lease generation. Every completion, child commit, attempt update and release predicates on the same branch id, owner, generation and an unexpired lease. A stale claim commits nothing.

IRetainedSourceReader gains a binary operation returning only verified bytes, hash, byte length and SourceRevisionId. SqlRetainedSourceReader continues to select the artifact root through the source revision/root/private-store binding, lease the physical root, prohibit reparse traversal, open no-follow, verify exact length and SHA-256, then returns the bytes. Its UTF-8 method remains a stricter decode over this shared verified read.

Existing SourceActivity idempotency remains authoritative. Branch, member, activity-relation and artifact uniqueness make duplicate delivery, concurrent activation, restart and lease loss converge without duplicate child revisions or a false parent completion. ZIP branch processing does not overload an Extract Job or infer a pipeline result from a process heartbeat.

## Local visibility and secret boundary

The [private-PC local visibility policy](2026-08-16-private-pc-local-visibility-policy-design.md) supersedes the earlier broad public-projection restriction. Trusted local SQL views, audit details, REST, MCP, CLI, SignalR and UI may expose useful retained-derived ZIP/TAR/Office/code facts, member names, paths, hashes, locators and parser diagnostics. They do not reopen a source original and never expose credentials, tokens, headers, private keys, connection strings or detected secret literals. External/public/export/shared projections remain sanitised.

Tests use a retained-detail sentinel and a secret sentinel. Trusted-local detail tests prove the former is available through the intended local contract; public/export projection tests prove it is absent there; every projection rejects or redacts the secret sentinel. Each branch transition and completion receipt appends bounded local evidence in the same transaction that exposes its durable state. Failed projection reads do not alter branch claims, leases, source state or recovery.

## Processor sequence

| Slice | Capability and delivery | Dependencies and non-goals |
| --- | --- | --- |
| 1 | archive-zip-expand: retained-only automatic replay, safe ZIP streaming, child provenance, fenced receipts, hostile ZIP outcomes and privacy proof. | Delivers the shared contract only to the extent ZIP needs it. No nested processing or OOXML interpretation. |
| 2 | A separate deterministic archive format only when an already available local/platform API is proven safe. | Reuses the branch contract; assigns its own capability, signature and bounds. |
| 3 | [Office document amendment](2026-08-13-phase-5-office-document-amendment-design.md): automatic structural extraction for `.doc`, `.docx`, `.xls`, `.xlsx`, `.ppt` and `.pptx`, plus fenced actions for actually blocked documents. | Bounded retained-only parsing only. The legacy Compound File Binary parser inventory is a hard prerequisite; no Office automation, model, runtime download or network client. |
| 4 | [Deterministic retained C# code parsing](2026-08-16-phase-5-retained-csharp-code-processor-design.md) using Roslyn. | Other languages remain durable deferred evidence until a separately reviewed deterministic parser choice. |
| 5 | Deterministic image/media metadata where an already present parser is safe. | Metadata only; no OCR, frames, vision, ASR or transcription. |

A fresh RED/GREEN record, generated/disposable-SQL evidence, task review and independent slice-review approval are required before the next processor family begins. A missing safe fixture must be configured rather than treated as a reason to stop.

## Phase 6 exclusions

Phase 5 excludes OCR, AI vision, speech transcription/ASR, embeddings, reranking, model-dependent document processing, model download/conversion/cache activation, ONNX Runtime, GPU admission and GPU work.

It excludes Outlook profile enablement, Outlook startup, mailbox validation, COM-host changes, mailbox mutation, deployment, merge, push, non-disposable migration and live validation. It preserves legacy Gmail code, configuration, tests, APIs and documentation unless a later compatible source-agnostic change demonstrates a necessity.

## Acceptance criteria

The ZIP slice is acceptable only with fresh evidence of:

- enabled automatic replay and disabled non-replay;
- retained-only success with a missing source original;
- 16-item bounded automatic batches;
- legacy ZIP-only promotion and untouched non-ZIP generic work;
- idempotent and concurrent replay;
- missing/corrupt/rebound retained artifact blocking;
- stale-fence rejection, cancellation and restart reconciliation;
- every ZIP policy outcome;
- immutable parent/child provenance and complete branch accounting;
- no private sentinel in any public projection or audit detail.

Disposable SQL tests are mandatory when FLUXKNOWLEDGE_TEST_SQL_CONNECTION is configured. When it is absent, their exact skipped condition must be reported rather than described as a pass.
