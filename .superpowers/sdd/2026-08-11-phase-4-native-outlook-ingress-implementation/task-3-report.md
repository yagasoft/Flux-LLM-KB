# Task 3 report — private ready-export ingestion and deferred work

## Delivered capability

Commits `24bdb42..e0b3a47` implement one SQL-authoritative transition from a
promoted private `ready/<export-id>` directory to an idempotent export receipt,
immutable parent/child source revisions, content-addressed retained artifacts and
supported or deferred activities. The folder cursor advances only after the
transaction commits. SQL rollback leaves the ready export retryable.

Recovery reconstructs work from the verified ready manifest rather than
in-memory state. Bounded no-follow reads validate retained bytes, length, checksum
and containment. Missing, corrupt, invalid-identity and unsupported artifacts
become bounded durable blocked/deferred evidence; replay does not reopen Outlook
or an original watched file. Identity or fingerprint conflicts cannot mutate a
previously accepted export.

## Evidence

The detailed implementation/remediation record is retained in
`docs/superpowers/reports/2026-08-11-phase-4-native-outlook-task-3-report.md`.
Whole-branch review found that supported Outlook text was retained under the
profile's private spool while the registered retained-source reader resolved only
the shared artifact root. RED: an accepted Outlook body could not be reopened by
that reader even though its content-addressed bytes existed in the private spool.
The reader now selects the unique immutable Outlook source-root binding, opens a
private directory lease and applies the same containment, no-follow, byte-length,
SHA-256 and strict UTF-8 verification used for shared artifacts. It does not
expose the private root through a projection or audit surface.

Fresh Task 7 disposable-SQL verification passed 29/29 Outlook ingestion and
deferred-replay tests with no skips, including the private-spool reader E2E. The
solution-wide run also exercised the remaining retained-text and schema paths.

No COM, Outlook, mailbox, UI, deployment or Gmail action occurred.
