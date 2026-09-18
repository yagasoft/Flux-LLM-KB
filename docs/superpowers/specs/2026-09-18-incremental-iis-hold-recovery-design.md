# Incremental IIS validation-hold recovery design

**Status:** Implemented, independently reviewed and incrementally deployed on
2026-09-18 from commit `9cd35fc07ca3da488957d96d9f347f3d73c53522`.

## Goal

Allow the existing incremental IIS payload swap to complete its rollback when
candidate validation fails, without changing deployment scope or weakening its
deployment-validation hold.

## Decision

The only production-code change is to make the existing validation hold
idempotent for the **same release**. The first stop still creates the hold
exclusively. A second stop during rollback may reuse it only when it is an
ordinary, non-reparse file whose UTF-8 contents exactly equal that release's
canonical JSON payload. A foreign, malformed, or reparse-point hold remains a
hard failure and is never altered.

The retained-state baseline is acquired only with the first successful stop.
The rollback stop reuses it, avoiding a second database read and preserving the
comparison used to prove the candidate did not change retained pipeline state.

## Hook adapter

No hook-installation path is added. Read-only verification established that the
installed adapter already equals the `a79e937` generated adapter: 1,477 bytes,
SHA-256 `DF01EB06D0455148721A24CDB12026BE639C572BBEF7F0B64210A6AF11BECD2E`.
The incremental updater continues to preserve `CodexPlugin`; this hash is
checked before and after Apply. A mismatch stops live validation and is not
repaired by the full-GoLive-only marketplace writer.

## Non-goals

No new deployment subsystem, hook updater, model operation, migration,
configuration, schema change, GoLive invocation, or change to the canonical
IIS/loopback paths.

## Acceptance criteria

- A failed candidate validation with two stops restores the original payload.
- The second stop accepts only the current release's existing ordinary hold.
- The original retained-state baseline is used for post-candidate comparison.
- Foreign, malformed, and reparse holds fail closed without alteration.
- PlanOnly and Apply retain fixed roots, no migrations/clean-slate behaviour,
  candidate probes, unchanged-state validation and final hold removal.
