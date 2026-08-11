# Phase 4 native Outlook Task 3 report

## Delivered capability

Task 3 now has one SQL-authoritative ingestion transition for a promoted private `ready/<export-id>` directory. The transition validates the ready export, creates or replays its durable receipt, catalogues immutable source revisions and retained artifacts, records supported or deferred activities, writes bounded blocked evidence, and advances the folder cursor last. No Task 2/3 capture-store port retains an independent cursor-mutating export commit method.

The filesystem promotion and retained-file placement remain deliberately outside the SQL transaction. SQL rollback leaves the cursor unchanged and the ready export retryable; this work does not claim filesystem and SQL atomicity.

## Review remediation

- The promoted manifest contains the complete recovery envelope: operation and request fingerprints, catch-up/fencing identity, profile and folder identifiers, EntryID, source fingerprint, and cursor observation. A restarted process reconstructs ingestion from the ready directory without an in-memory request.
- Manifest and sidecar bytes are read through bounded no-follow handles with leased parent/root identity checks. The exact retained copy is hashed and length-verified again before its content-addressed row is committed.
- Deferred retained-text replay validates the retained file, checksum, length, containment and strict UTF-8 encoding before it can create pipeline work or claim deferred evidence. Missing or corrupt artifacts remain unclaimed and move to a durable blocked state.
- MIME-aware routing keeps validated `text/*` content on the retained-text route. Binary content is classified as `DeferredCapability` and mapped to an explicit activity family; for example, `application/pdf` becomes `DocumentParsing` and cannot be claimed by the UTF-8 replay store.
- Blocked exports persist a bounded reason code and emit a sanitised operator event containing only the allow-listed `reasonCode`. No exception text, paths or content are retained in operator evidence.

## Schema

Migration `20260811143122_RecordOutlookExportBlockedReason` adds nullable `OutlookCaptureExports.BlockedReasonCode` as a binary-collated `varchar(64)`. Existing successful and historical rows remain compatible.

## Verification

Disposable SQL connection: `Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;`.

- Outlook domain contract tests: 10 passed.
- Outlook ingestion, Outlook schema mapping, deferred replay, and retained-text pipeline integration tests: 30 passed.
- Focused recovery/blocking batch: 4 passed.
- Focused MIME/replay batch: 4 passed.
- Solution build after restore: passed with 0 warnings and 0 errors.

The whole solution run passed all 304 domain tests and 412 of 413 integration tests. The one failure reproduces alone in the unchanged `PipelineOperatorEventIntegrationTests.Native_worker_persistence_audits_only_a_sanitised_lifecycle_summary`: its fixed 09:01 observation sorts before the store's current-clock connection event, so the assertion selects `connected` rather than `unresponsive`. This is outside the Task 3 diff; the Task 3-focused suites above are green.

No Gmail, COM, UI, deployment, live mailbox, or production migration action was performed.

## Review remediation round 2

- Directory leases now open the configured root without following the final reparse point, reject a reparse-point root, and reject any final handle path that differs from the configured root. This closes both final-junction and ancestor-junction swap windows before contained bytes are read.
- A JSON-valid but contract-invalid recovery envelope now creates one idempotent blocked receipt with `ready-manifest-recovery-invalid`, a sanitised reason-only operator event, no source work, and no cursor advance. Retry replays that receipt instead of throwing the same validation exception.
- Retained replay no longer calls `File.Exists` after a no-follow open rejects a path. Missing files are surfaced directly as `FileNotFoundException`; other no-follow I/O failures are durably classified as `retained-artifact-path-invalid`, while byte/hash failures remain `retained-artifact-checksum-invalid`.

Round 2 disposable-SQL verification passed 34 Outlook ingestion, schema, deferred replay, and retained-text integration tests.

## Review remediation round 3

- Recovery envelopes with an empty, unknown, or profile/folder-mismatched private identity now commit one idempotent blocked receipt instead of failing before durable evidence exists. The blocked receipt deliberately has no profile, folder, catch-up, source revision or source-root binding, and replay is keyed by the verified manifest digest.
- Operator evidence remains bounded to the allow-listed `ready-manifest-recovery-invalid` or `ready-manifest-identity-mismatch` reason code. Its correlation uses the manifest digest; malformed private profile and folder identifiers are not copied into outward evidence.
- Migration `20260811152249_AllowIdentitylessBlockedOutlookExports` permits profile and folder keys to be absent together only for a blocked export. Successful and identity-resolved blocked exports retain both canonical foreign keys.
- Real-SQL coverage exercises empty profile, empty folder, unknown profile, unknown folder, and mismatched profile/folder recovery independently, including durable replay, unchanged cursor, no source work and reason-only evidence.

Round 3 disposable-SQL verification passed 40 Outlook ingestion, schema, deferred replay, and retained-text integration tests. The focused five-case recovery theory and schema invariant passed 6 tests.
