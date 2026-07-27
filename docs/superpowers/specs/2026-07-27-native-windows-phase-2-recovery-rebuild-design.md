# Native Windows Phase 2 derived-index recovery design

Status: approved; local implementation and verification complete on 2026-07-27.

## Goal

Keep the local-only application unavailable for readiness while its active
USearch generation is missing or invalid, then recover it from immutable SQL
membership without an IIS restart. SQL remains the authoritative source of
truth throughout recovery.

## Scope and boundaries

This Phase 2 slice adds derived-index recovery, recovery evidence and a
read-only local status projection. It does not add scheduler lanes, model or
GPU work, external access, legacy actions, a deployment change, or the full
Jobs/timeline user interface.

The active SQL generation and its immutable `IndexGenerationVectors` membership
are authoritative. A USearch directory, metadata file, staging directory and
quarantine directory are derived state only. Recovery never creates a new SQL
generation, changes `IndexState.ActiveIndexGenerationId`, or treats a directory
as a source for rebuilding SQL state.

## Continuous recovery contract

`DerivedIndexRecoveryService` is an in-process hosted service. It validates the
active SQL generation on startup and continues to react after startup when:

- the active index cannot be opened or validated by the ANN reader; or
- a bounded periodic probe detects a missing or invalid active derived index.

The service owns one process-wide `DerivedIndexRecoveryState` with these public
states:

| State | Meaning | `/health/ready` |
| --- | --- | --- |
| `Starting` | Initial validation has not finished. | 503 |
| `Healthy` | The active SQL generation has a validated derived index. | Eligible for 200 after SQL readiness also passes. |
| `Recovering` | A detected derived-index fault is being validated or rebuilt. | 503 |
| `RetryScheduled` | A recoverable derived-index attempt failed and has a bounded retry due time. | 503 |
| `OperatorActionRequired` | SQL membership, schema, configuration, permissions, or bounded retry policy prevents automatic recovery. | 503 |

The service serialises recovery across overlapping IIS processes with a
session-scoped SQL application lock named `FluxKnowledge.DerivedIndexRecovery`.
After acquiring the lock it rereads the active SQL generation and validates the
current state again. If another process has already restored a valid generation,
it records that outcome and returns to `Healthy` without rebuilding.

## Recovery flow

1. Mark recovery as `Recovering`, publish a local status invalidation, and write
   a sanitised durable audit event.
2. Read the active generation descriptor and immutable vector membership from
   SQL. Verify dimensions, model fingerprint, vector count and membership
   checksum before any filesystem mutation.
3. Classify the failure.
   - A missing directory, invalid derived metadata/index, transient file lock,
     or transient local I/O failure is recoverable.
   - Invalid SQL membership/checksum, missing or invalid schema, invalid USearch
     configuration, non-writable app-owned directories, and access-denied
     failures are operator-actionable. They do not enter automatic retry.
4. For a recoverable fault, build a replacement only in an app-owned staging
   directory, reopen and validate it against the immutable SQL membership, then
   atomically place it at a new immutable recovery path within the same app-owned
   root.
5. Only after placement and validation succeed, update the existing generation's
   SQL metadata to that new derived path. `IndexState.ActiveIndexGenerationId`
   is not changed. If metadata update fails, the old SQL-referenced path remains
   untouched and the unreferenced replacement becomes a quarantine candidate.
6. After the metadata update succeeds, take a fresh SQL path-reference snapshot
   while still holding the recovery lock. Canonicalise every SQL
   `IndexGenerations.IndexPath`; only when the previous invalid path is absent
   from that snapshot may it be moved into the app-owned quarantine area. It is
   not deleted as part of recovery. An invalid or out-of-root SQL path is an
   operator-actionable configuration fault and receives no filesystem mutation.
   A failed rebuild leaves the SQL active pointer unchanged.
7. On success, write a sanitised audit outcome, set `Healthy`, and publish a
   status invalidation. Only then can readiness return 200.

The current active or any SQL-referenced generation is never deleted. A SQL
generation reference is a canonical path reference, not only a generation ID:
the same generation ID can legitimately receive a new recovery path. Cleanup
may delete only direct children of the canonical USearch `staging` or
`quarantine` directories after both conditions hold: the candidate is older than
its configured retention and a fresh, lock-held SQL path-reference snapshot does
not contain it. Cleanup must not follow links outside the configured USearch
root.

## Bounded retry policy

One recovery episode has at most five automatic attempts: the initial attempt,
then retries after 2, 5, 15 and 30 seconds. A successful validation/rebuild ends
the episode. A fifth recoverable failure changes state to
`OperatorActionRequired`; it does not silently begin another episode or retry
indefinitely.

The policy applies only to classified recoverable derived-index failures.
Invalid SQL membership/checksum, schema/configuration failures and permission
failures transition directly to `OperatorActionRequired` after their first
classified attempt. The status projection exposes the safe failure category and
next retry time where applicable, never raw paths, source content, credentials,
or unredacted exception text.

Defaults are local configuration values: staging retention is 24 hours and
quarantine retention is seven days. They are not runtime-mutable through this
slice.

## Readiness, audit and local status

The existing SQL readiness validator remains responsible for SQL catalog,
migrations, Full-Text and active-pointer checks. `/health/ready` composes that
result with `DerivedIndexRecoveryState`; both must be healthy for HTTP 200.

The recovery service writes bounded, sanitised `AuditEvents` for detection,
lock contention, attempt start, retry scheduling, rebuild success, cleanup,
operator-actionable failure and retry exhaustion. Audit details include only
generation identifiers, safe categories, attempt counts, elapsed duration and
candidate counts.

A read-only local recovery projection is exposed through an index-health route
and the local Blazor overview. It reports current state, active generation ID,
last completed recovery time, retry due time when present, safe failure category
and bounded cleanup outcome. It includes no mutating recovery endpoint.

If the ANN reader discovers a fault after startup, it reports it to the shared
recovery state immediately. Readiness changes to 503 while the hosted service
recovers; no IIS restart is required for a successful recovery.

## Acceptance criteria

1. A valid active generation reaches `Healthy` at startup and readiness returns
   200 only when the existing SQL readiness checks also pass.
2. Deleting or corrupting the active derived index after startup causes
   readiness to become 503, triggers recovery without an IIS restart, rebuilds
   from immutable SQL membership, validates and safely places the result, then
   restores readiness to 200 and searchable ANN results.
3. The active SQL pointer is identical before and after successful derived-index
   recovery. A successful recovery may update only the active generation's
   derived path after placement and validation; a failed recovery never changes
   the active pointer or its SQL-referenced path.
4. A transient derived-index failure follows the exact bounded retry schedule
   and succeeds if a later attempt validates. Retry exhaustion ends in
   `OperatorActionRequired`, not an endless loop.
5. Invalid SQL membership/checksum, schema/configuration failures and
   permissions failures become `OperatorActionRequired` without automatic retry
   or unsafe filesystem mutation.
6. Cleanup removes only aged, unreferenced staging or quarantine candidates;
   active and SQL-referenced generations remain intact, including when old.
7. Concurrent recovery service instances perform at most one rebuild under the
   SQL application lock and converge on the same validated active generation.
8. Durable audit evidence and the read-only local status projection expose the
   recovery lifecycle without paths, private content, credentials or raw
   exception text.
9. Focused domain, SQL integration, web projection and browser checks prove the
   behaviours above. A later, separately approved loopback IIS checkpoint may
   exercise controlled fault-and-recovery validation; this design authorises no
   deployment action by itself.

## Non-goals

- No GPU mini-task scheduler, model runtime, model download or model activation.
- No external access, hostname/binding change, public endpoint or legacy cutover.
- No deletion of canonical SQL vectors, records, jobs, artefacts or audit data.
- No automatic repair of invalid SQL membership, schema, configuration or
  permissions; those require operator action.
