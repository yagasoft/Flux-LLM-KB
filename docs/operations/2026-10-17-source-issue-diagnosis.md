# Source scan and current-issue diagnosis

## Scope and observed causes

The 17 October follow-up separates scan access problems, current processing
failures and historical activity. It does not relax source permissions, physical
identity, reparse-point policy, checksum validation, encryption or parser limits.
Production application requires fresh approval and a coordinated window.

The bounded metadata inventory recorded 25,362 reports in the latest affected
filesystem scan. Its older audit retained only the first 100 samples, all labelled
`SourcePathDenied`; those samples cannot establish all causes. Three exact sampled
files were pinned cloud placeholders. Guarded no-recall handles opened under the
interactive account, while their ACLs did not list the application-pool identity
or IIS group. This establishes a permission gap in those samples, not an effective
token test or a diagnosis of every scan report. No source ACL was changed.

Of 130 current failed pipeline records, 125 reported `document-ocr-runtime-failed`,
four exceeded the image decode bounds and one was encrypted. The existing OCR
virtual environment depends on a base interpreter in a user-profile installation;
that executable's ACL did not list the application-pool identity or IIS group.
Offline dependency imports succeeded under the interactive account. Initialisation,
inference and access under the application identity remain unverified, so this
dependency concern is not presented as the proven cause of all 125 failures.

Other retained candidates include unsupported Project/binary formats, Office
container/structure refusals, archive path/expansion bounds, one checksum refusal
and unconfirmed native ownership. Activity rows are not file counts: aliases,
derived inputs and provisional bindings must be reconciled through the existing
current logical-file projection. The private operational inventory groups exact
items and identifiers outside Git. No blanket retry or history deletion is used.

## Correction

- Filesystem hint watchers use the existing cloud-aware, anchored directory
  validation and admitted physical identity. They hold the directory lease until
  watcher disposal and release partial registrations on failure or cancellation.
  Git discovery and retained-storage reparse checks stay strict.
- A watcher still opens a separate native handle. Events carry root identifiers
  only; guarded reconciliation remains the content-authorisation boundary. The
  isolated partial-cloud fixture demonstrated notifications with unchanged
  unavailable-child metadata and materialised namespace. It is not a universal
  proof about every provider callback or registration race.
- Scan evidence distinguishes explicit path-security refusals from Windows access
  denial, unavailable content, changed paths and I/O failures, retaining native
  error metadata where available. Any incomplete enumeration still prevents
  absence-based suppression.
- Existing audit JSON gains bounded cause groups: up to 64 groups and ten complete
  path samples per group. Counts cover the full evidence set when all cause groups
  fit. older samples and truncated groups explicitly report incomplete coverage.
  A later finished scan replaces the displayed evidence; old scans remain history.
  Source list/detail and Overview retain that total while a newer scan is pending
  or running, so starting a scan is not presented as recovery.
- Source detail gains current issue groups from the same logical files as its
  table, including failures of derived OCR inputs attributed to their owner file.
  Verified recovery removes an item from current groups without deleting attempts.
  `CurrentProcessingIssues` is additive; the existing activity-reason projection
  remains compatible. Item paths and scan samples remain local-operator UI data,
  excluded from public JSON.
- OCR failures now return fixed stage codes, or a sanitised hexadecimal exit
  status when the child produced no valid failure result. Failure output is limited
  to a guarded 4 KiB read and an exact allowlist. Provider stdout/stderr are drained
  with bounded memory and are not exposed as document/error text.

## Verification and remaining work

Focused checks exercised cloud and ordinary roots, unsafe links, identity
refusals, cancellation/failure cleanup, source ACL denial, retained cloud
publication/readback, incomplete inventory protection, logical recovery, older
scan samples, public-JSON privacy and OCR failure/result handling. Model-free
Python protocol tests exercised the real worker with a substituted provider
boundary. Chromium checked expansion, item visibility and absence of horizontal
overflow at 1280 × 900 and 390 × 844 using the rendered component and application
CSS. That fixture does not prove the deployed full page or live refresh.

Independent review covered the complete implementation. Its presentation
corrections preserve uncertainty: an Office container refusal does not prove
corruption, and `office-document-part-unsupported` can mean a part-size bound,
relationship bound or rejected derived-output receipt. No parser bound was raised.
The separate directory-marker compatibility question is not attributed to current
production items and is not changed by this release.

Canonical integration and any incremental production application have distinct
receipts. Production follow-up must refresh the installed identity and workload
ownership, coordinate with Release 1, review the incremental updater plan and
obtain fresh approval. Preserve paired rollback payloads, configuration, scheduled
host identity, immutable models and unresolved native outcomes. No schema migration,
model download, source modification, full-source replay or dead-letter processing
is part of this correction.

The incremental updater does not repair source ACLs or adopt/repoint an external
Python base installation. Those are separate, precisely bounded operational
proposals requiring fresh approval. Do not grant broad profile access, relocate
existing models, reinterpret a safety refusal as resolved or clear current failures
until their own supported pipeline result proves recovery.
