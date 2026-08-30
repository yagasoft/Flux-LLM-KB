# Native clean-slate go-live execution design

**Status:** approved design, pending implementation

**Decision date:** 2026-08-26

## Purpose and authority

This design turns the existing native go-live preparation into one explicit,
locally executed clean-slate operation for the private single-user PC. It is
the only execution path that may initialise the native application for first
use. Normal application startup, the normal CLI, REST, MCP, diagnostics and
plugin status commands remain non-mutating.

The operator has chosen all of these decisions:

- native application only; no legacy application or compatibility path;
- wipe the Knowledge Base application state with no backup or restore;
- own all ordinary application data beneath `I:\FluxKnowledge`;
- place the SQL MDF and LDF at the canonical paths beneath that root;
- use unencrypted, OS-managed VSS shadow storage on `I:` with a 10% cap;
- use no at-rest encryption, including for the app-owned data-protection key
  ring; restricted local ACLs protect that ring on this private single-user PC;
- advertise the app-owned local Codex marketplace and register it
  automatically after successful native validation;
- use no cloud/network parsing, model/runtime activation, GPU work, FFmpeg or
  Outlook activation.

This design authorises implementation and disposable verification only. A
subsequent explicit operator action authorises the irreversible live run.

## Outcomes and non-goals

On success, the dedicated native IIS host serves the v1 REST and HTTP MCP
contract only on `http://127.0.0.1:5137`, its new SQL catalogue is migrated and
healthy, every runtime data path is under `I:\FluxKnowledge`, VSS is configured
for its specified cap, and the local `fluxknowledge` Codex marketplace/plugin
is registered. The only retained data is the new, empty native application
state.

This is not an update installer, migration-recovery mechanism or a backup
tool. It does not preserve a prior payload, database, configuration, index,
retained material, spool, temporary file or plugin registration. It does not
create a VSS snapshot before the wipe, automatically restore a shadow copy,
operate any non-native/legacy service, or activate Phase 6 capability.

## Single go-live contract

`scripts/dev/complete-feature.ps1` remains the only feature-closeout entry
point. Its explicit native go-live mode invokes one dedicated PowerShell
execution module after the reviewed feature has been squash-merged locally and
the merged `main` source verification passes, but before the final `main` push.
The execution module is not a Git executor: it neither merges nor pushes. It
owns only the host lifecycle described here.

The mode requires all of the following in the same invocation:

1. an explicit `-GoLive` switch;
2. the exact acknowledgement `-ConfirmCleanSlate`;
3. separate acknowledgement switches for VSS configuration, SQL destruction
   and Codex registration; and
4. a short-lived, single-use in-process authority created only by
   `complete-feature.ps1` after its non-mutating verification succeeds.

`complete-feature.ps1` imports the executor module and invokes the go-live
function in its own process; it must not route that call through the existing
child-process `Invoke-FeatureStep` wrapper. The private capability is bound to
the merged-main SHA, a canonical plan hash and a 30-minute expiry. Its state
machine is `Issued -> Claimed -> Completed|Failed`; the executor atomically
claims it immediately before quiescing the app pool, and every terminal state
rejects replay. Direct module/script invocation has no capability and cannot
mutate the host.

Before authority issue, the executor acquires the fixed machine-local
`Global\FluxKnowledge.NativeGoLive.v1` exclusive lease and an exclusive handle
on the separate, stable ignored lock file `native-go-live.lock`. It holds both
through every terminal authority/journal transition and final app-pool
disposition. The replaceable journal is never held open across replacement:
under that lease the executor re-opens and validates its expected execution
ID, writes and flushes a sibling temporary journal, atomically replaces the
journal, then re-opens and verifies the new journal before continuing. That is
the journal compare-and-set; contention, a different in-progress execution,
an unexpected temporary file or a stale journal fails before mutation. A
process exit releases the operating-system lease but leaves its durable
incomplete journal, which a later fully acknowledged invocation may claim only
after a complete new preflight.

`-PlanOnly` remains available and returns the exact expected identities and
steps without opening a database, process, IIS, Codex or `I:` path. The
execution module rejects direct invocation, expired/replayed authority,
unrecognised parameters, any external site/path/catalogue identity and any
attempt to use a backup/restore option.

The invocation receives its bootstrap SQL connection only through the
dedicated, unlogged `FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP` process
environment value. It must use local integrated authentication and a loopback
SQL Server endpoint; SQL authentication and password fields are rejected.
The value is parsed only in memory, never passed as a command-line argument,
and is cleared before publish, probe and Codex child processes. Passwords,
credentials and connection strings are never emitted in plan, event or error
output. A separately validated integrated-security runtime value is written to
the newly created app-owned `Config` location, which the Web host deliberately
loads through a new no-follow JSON configuration provider.

## Preconditions and all-or-nothing safety boundary

Before the first mutation, the executor validates:

- fixed IIS site and application-pool identity `FluxKnowledge`, exact
  loopback-only binding on port 5137, anonymous authentication enabled and
  Windows authentication disabled;
- the exact native application identity and hashes being closed out;
- `I:\FluxKnowledge` and every existing ancestor/child segment with the
  existing no-follow/reparse-point guard;
- the `I:\` volume ancestor and every operation path through a Windows-native,
  handle-relative no-follow protocol: open each directory with reparse-point
  semantics, compare its stable volume/file identity under the verified parent,
  then perform create, rename, replace or deletion through that verified handle;
- if the root exists, that it contains only the exact app-owned layout. An
  unexpected entry, junction, foreign resolved path or non-canonical SQL file
  is ambiguity and fails before deletion;
- the only deletable SQL catalogue is `FluxKnowledge`, with exactly the
  canonical MDF and LDF paths; a different catalogue, file or server identity
  fails before mutation;
- the bootstrap database endpoint is loopback and integrated-authenticated;
- the local SQL service identity is resolved from the validated instance and
  is an allowlisted local service principal; and
- the instance has Full-Text, the bootstrap principal can drop/create exactly
  the canonical catalogue and manage only the fixed app-pool login, and any
  existing app-pool login has the exact current Windows SID with no server role,
  sysadmin membership or explicit DDL grant;
- VSS administration is elevated and the supported local VSS management API
  can query the existing `I:` shadow-storage state. An existing association
  may be absent or exactly `I:` to `I:`; any association that uses another
  volume fails;
- Codex lifecycle status identifies only the app-owned `fluxknowledge`
  marketplace/plugin; and
- Outlook remains disabled and the native runtime model/GPU/FFmpeg switches
  remain disabled.

One pre-marker layout is allowed solely for this first clean-slate launch:
`I:\FluxKnowledge` may contain exactly the historic preliminary native
children `Sql` and `OutlookSpool`, with literal `Sql\Data\FluxKnowledge.mdf`,
`Sql\Log\FluxKnowledge_log.ldf`, and the three `OutlookSpool` children
`_inflight`, `ready` and `sha256`. It must have no reparse segment or other
direct child, its SQL catalogue/files must match the preflighted canonical
identity, and its IIS payload/configuration must independently identify this
native product. The executor records this one-time adoption in the external
journal before VSS, but does not mutate the root until VSS has succeeded.
Every other markerless root fails; this is a clean-slate adoption guard, not a
legacy runtime compatibility path.

Before any host mutation, `complete-feature.ps1` atomically writes a sanitised
journal outside the wipe target at a dedicated ignored closeout location beneath
the merged-main repository root. It contains only execution ID, committed SHA,
plan hash, phase, timestamps and safe reason codes. The final result is
mirrored into `I:\FluxKnowledge\Recovery` only after that hierarchy exists. An
exact `Completed` journal skips host mutation on rerun and permits only the
pending validation-record/push/cleanup closeout steps. A `Failed` or incomplete
journal requires a new full acknowledgement and authority, and begins again at
preflight. A SHA or plan-hash mismatch fails closed.

There are two explicit action boundaries. Before quiesce, cancellation leaves
the host untouched. Quiesce is reversible: cancellation/failure before the VSS
invocation restores only the app pool's observed original state and leaves VSS,
the catalogue and the root unchanged. The irreversible point is immediately
before the VSS command, after the authority is claimed and the journal records
`VssPending`. A VSS failure or cancellation after invocation is an incomplete
post-irreversible result even if application state remains intact. Every later
failure stops the operation, leaves the dedicated app pool stopped unless the
new host has already validated, and reports `clean-slate-incomplete`. It never
silently restores or preserves old state.

## Ordered execution

The same pure plan drives fake-host tests and the production executor. Each
phase validates its exact owned targets immediately before use.

1. **Preflight.** Perform every predicate above; calculate, but do not create,
   the canonical hierarchy and deployment stage.
2. **Claim and quiesce.** Atomically claim the authority, record the observed
   app-pool state and `VssPending` journal phase, then stop only the fixed
   `FluxKnowledge` app pool, wait for its stopped state and verify the target
   is no longer serving. Do not stop other sites, services, tasks or Outlook.
   Before VSS, cancellation restores the pool only when this phase stopped a
   previously started pool.
3. **Cross the irreversible point and configure recovery.** Use the supported
   local VSS management API, not locale-dependent `vssadmin` output. Query
   `I:\` support and current diff-area associations by volume GUID, calculate
   exactly 10% of the verified total `I:` volume capacity in bytes, and apply
   that limit unencrypted. For an existing exact `I:` to `I:` association call
   `ChangeDiffAreaMaximumSize`; for an absent association call `AddDiffArea`
   with the same exact maximum. A foreign association fails. The executor then
   re-queries the association and requires the exact source, storage volume and
   maximum before deletion. This is the only volume-global mutation; an API
   failure leaves the root and catalogue untouched. It creates no snapshot and
   has no restore path. The VSS implementation must reject volumes/providers
   that do not support a diff area or cannot meet the API minimum capacity.
4. **Mark and destroy old owned state.** For an existing Complete root, first
   atomically replace `Recovery\native-go-live-owner.json` with the current
   journal-bound `Incomplete` marker before deleting any owned child. Retain
   that no-follow-validated Recovery directory and marker throughout deletion;
   delete its other owned content only after the marker is durable. The one
   adopted preliminary root has a distinct, bounded transition: after VSS,
   create only `Recovery`; write and flush a matching temporary marker; then
   atomically replace it with a journal-bound `AdoptedPreliminary` marker
   before any deletion. Its literal sequence is record a pending action, drop
   the prevalidated native catalogue and confirm its canonical files are
   absent, record a pending action and delete `OutlookSpool`, record a pending
   action and delete the now-empty `Sql`, then atomically replace its marker
   with normal `Incomplete` before creating canonical state. It never deletes
   the root or `Recovery`.

   `AdoptedPreliminary` is a journal adoption substate, independent of the
   outer execution phase. The executor records each completed physical action
   only after its identity revalidation; `*Pending` is recorded before the
   corresponding atomic marker replacement. Its only accepted crash-prefix
   pairs are: `AdoptionRecorded` with markerless `Sql` plus `OutlookSpool`, or
   the same plus an empty `Recovery` created before its journal update;
   `RecoveryCreated` with that empty `Recovery`; `AdoptedMarkerPending` with
   that root and either an empty Recovery, a matching temporary marker, or the
   durable `AdoptedPreliminary` marker; `AdoptedMarkerDurable` with that durable
   marker and the three direct children `Sql`, `OutlookSpool`, `Recovery`;
   `CatalogueDropPending` with those three children and either the exact
   prevalidated catalogue plus both exact canonical files, or an absent
   catalogue plus both files absent; `CatalogueDropped` with those three
   children, an absent catalogue and absent canonical catalogue files;
   `OutlookSpoolDeletePending` with either those three children or only `Sql`
   plus `Recovery`; `OutlookSpoolDeleted` with only `Sql` plus `Recovery`;
   `SqlDeletePending` with either `Sql` plus `Recovery` or `Recovery` alone;
   `SqlDeleted` with only `Recovery` and the durable adopted marker;
   `CanonicalMarkerPending` with only `Recovery` and either the adopted marker,
   a matching temporary normal marker, or the durable normal `Incomplete`
   marker; and `CanonicalMarkerDurable` with only `Recovery` and the durable
   normal `Incomplete` marker. The temporary and durable marker pairs must bind
   the same execution ID, SHA and plan hash as the journal. No other direct
   child, temporary file, journal/marker pair or deletion order is recoverable.
   A pending physical action resumes idempotently only by acting on its exact
   still-present literal target or, when its accepted post-action shape is
   present, recording its completion; all other partial or swapped states fail.
   The same pending-before-delete, exact-before-or-after-shape rule applies to
   every destructive literal-child operation in the normal complete-root path.
   Each accepted prefix requires fresh authority and exact revalidation before
   its next mutation.

   A root is otherwise deletable only when absent or when
   `Recovery\native-go-live-owner.json` is a validated native owner marker. A
   `Complete` marker requires exactly the six direct child classes `App`,
   `Config`, `Data`, `Runtime`, `CodexPlugin` and `Recovery`; normal
   `Incomplete` permits a subset of those six classes. The marker binds product
   identity, state, execution ID, SHA and plan hash; arbitrary
   data/index/retained/log content is accepted only below the owned child
   classes. An unknown root, missing/mismatched marker, foreign direct child,
   reparse segment or changed file identity is a pre-mutation failure. Delete
   only literal verified children, never the root or a wildcard; re-open and
   revalidate every identity immediately before deletion. A missing
   root/catalogue is an idempotent clean-slate condition.
5. **Create canonical state.** Recreate the exact owned hierarchy with
   no-follow revalidation. For an absent root, the only accepted crash prefixes
   are root absent, an empty canonical root, or an empty canonical `Recovery`
   child, each with an exact external `Incomplete` journal and fresh authority.
   The adoption transition above is the only exception to this canonical
   grammar. Create `Recovery` and write the `Incomplete` marker with a
   no-follow-validated temporary file plus atomic replacement before creating
   any other child. A pending temporary marker is accepted only when its
   execution ID/SHA/plan hash exactly match the journal; all other prefixes or
   content fail closed. Then create `Config`, `Data\Sql\Data`,
   `Data\Sql\Log`, `Data\Index`, `Data\Retained`, `Runtime\Spool`,
   `Runtime\Temp`, `Runtime\Logs`, `CodexPlugin` and `Recovery`.
6. **Create database and prove empty-catalogue readiness.** Grant the resolved
   local SQL service identity Modify only on the canonical Data/Log directories,
   then create `FluxKnowledge` only at the canonical MDF/LDF paths. Apply the
   full current EF migration graph once, including an additive
   `IndexState.EmptyCatalogueValidatedAtUtc` nullable timestamp and a shape
   constraint that prevents it coexisting with an active generation. In one
   transaction, the empty bootstrap proves zero canonical vectors, zero index
   generations and zero memberships before setting that timestamp and updating
   the singleton state. It creates no USearch artefact, synthetic vector or
   directory. Strict readiness accepts this explicit state only when the active
   generation is null and all three counts remain zero; null active generation
   without the marker, a marker with non-empty SQL state, or a marker plus an
   active generation remains invalid. Recovery marks the exact proved empty
   state healthy without disk access; the first real publish atomically clears
   the marker and activates a normal validated generation. Create the fixed
   `IIS AppPool\FluxKnowledge` SQL login/user with only Connect,
   `db_datareader` and `db_datawriter`. If the preflighted login is absent,
   create only the validated current-SID Windows login; if it is present, it
   must already match that SID and the preflighted non-privileged server state.
   Do not reuse, modify or remove a foreign/overprivileged principal. Prove the
   resulting app-pool token can connect and cannot perform DDL. The app pool
   receives no MDF/LDF filesystem access. A failed migration,
   bootstrap, permission proof or readiness check stops the run; it is not
   retried against a different catalogue or path.
7. **Publish and start.** Publish from the verified merged-main root at its
   journal-bound SHA to a same-volume validated `Runtime\Temp` stage, verify
   hashes and the expected assemblies, then move that stage into the initially
   absent literal `App` directory. Write only new canonical production
   configuration in `Config`, transition the new root owner marker atomically
   from `Incomplete` to `Complete`, grant the
   fixed app-pool identity read/execute on App and read on Config, and grant it
   only necessary app-owned Data/Runtime write access. It receives no write
   access to App, `appsettings.Production.json`, SQL Data/Log or Recovery. It
   receives Modify only on the no-follow-validated
   `Config\data-protection` subtree for private-PC data-protection key creation
   and rotation. Then start the fixed app pool.
8. **Validate live.** Use no-proxy/no-redirect loopback requests to require
   HTTP 200 from `/health/live`, `/health/ready` and `/api/index-health`; require
   `/api/gpu-status` to project zero ready, active, deferred and uncertain work
   with no active batch; and run the SQL readiness validator against the new
   catalogue. POST `/api/v1/knowledge/search` with a bounded synthetic query
   and limit one must return the standard successful empty native envelope.
   The HTTP MCP initialise/tools-list exchange must advertise exactly the nine
   closed native tool names. The same REST request with a `Forwarded` header,
   and a direct request from a non-loopback peer in the disposable host test,
   must receive HTTP 403. No browser validation is required because this
   introduces no UI change.
9. **Register marketplace source.** Materialise and validate the app-owned
   local marketplace beneath `CodexPlugin` with the same no-follow writer used
   by all production root writes. Register only the known `fluxknowledge`
   marketplace source and advertise its native plugin; this is not plugin
   installation or activation. The production adapter invokes exactly
   `codex plugin marketplace add I:\FluxKnowledge\CodexPlugin` and verifies
   the expected local source through `codex plugin marketplace list --json`.
   It must not invoke `codex plugin add`, any plugin activation command, a Git
   marketplace operation or a legacy Python installer. It changes only its
   explicitly identified marketplace-source record, proves a before/after
   structural hash equality for unrelated configuration without retaining that
   configuration's content, and treats an already matching source as a no-op.
   The same marketplace name at a foreign root fails before mutation.
10. **Finish.** Persist a sanitised completed result. Only a successful result
    permits `complete-feature.ps1` to perform its final `main` push. Live
    validation evidence remains in the ignored journal/result and is not a
    tracked documentation file or follow-up Git commit; the pushed `HEAD` is
    therefore exactly the deployed journal-bound SHA. A failed final push,
    cleanup or worktree operation after `Completed` reruns only the pending
    closeout action and never repeats IIS, VSS, SQL, root or Codex mutation.
    The closeout script itself never treats a partial clean slate as success.

## Architecture and implementation boundaries

The implementation replaces the currently unreachable body of
`scripts/deploy/update-native-windows.ps1` with a single named native
go-live plan/executor contract. It removes the obsolete `C:\inetpub` default,
the unsupported `BackupRoot` contract and deployment migration contradiction
from `complete-feature.ps1`. The PowerShell boundary owns IIS, VSS, local SQL
bootstrap and Codex lifecycle calls; application code retains only pure path,
plan, validation and single-use authority types. No generic command runner,
credential bridge, Git adapter or parallel lifecycle transport is introduced.

The production lifecycle adapters are private to the go-live composition and
cannot be constructed by Web/MCP/CLI normal composition. The existing fake
ports remain the test seam. Each adapter reports bounded safe facts only and
uses the existing disclosure policy before durable or local output. The public
`provision-sql` CLI command is removed from normal production dispatch; its
real provisioner is constructible only inside this claimed go-live composition.
Generated-database test provisioning remains an explicit non-production seam.

The new Config provider loads only the canonical no-follow-validated
`I:\FluxKnowledge\Config\appsettings.Production.json`; it does not fall back
to a former app deployment or legacy configuration. Fresh configuration sets
the source-root catalogue empty and binds `LocalIngress:AllowedRoots` only to
the inert app-owned retained root, so no source-original path is admitted at
go-live. It explicitly keeps Outlook recovery, native worker/model/runtime,
GPU admission, OCR/vision/ASR, FFmpeg and network parsing disabled. The
go-live preflight and live checks validate those effective options and service
types, not merely text configuration.

The data-protection ring at `Config\data-protection` is intentionally
unencrypted: the current-user DPAPI call is removed and no alternate encryption
provider is introduced. Its ACL grants Modify only to the fixed app-pool
identity and Read to the current interactive operator SID captured during
preflight; owner, local administrators and SYSTEM retain control. This allows
the IIS host and trusted local CLI to protect/unprotect the same cursor state
without giving either identity broad Config write access. The keys remain
secrets for logging and disclosure purposes and are never printed or journaled.

## Verification and acceptance criteria

Implementation begins with red tests. Domain and disposable-host integration
tests must prove all of the following:

- a plan-only request causes no filesystem, SQL, process, VSS or Codex I/O;
- every acknowledgement and single-use authority failure is fail-closed;
- two concurrent first runs, incomplete-run recoveries and phase-boundary
  contenders prove the machine lease/stable-lock-file/journal compare-and-set
  permits exactly one host executor and every contender loses without mutation;
- journal temporary-file flush, atomic replacement and post-replacement
  verification each have crash and contender tests; a recoverable journal is
  accepted only under the stable lock and with its expected execution identity;
- foreign/reparse/unknown root, foreign SQL files/catalogue, non-loopback SQL,
  non-loopback IIS binding, non-native site/pool, enabled Outlook or runtime
  activation each fail before the first mutation;
- the single markerless preliminary native layout is adopted only with its
  independent IIS, payload/configuration and SQL identity proof; every other
  markerless variation fails before VSS or deletion; tests stop and resume at
  adoption record, Recovery creation, temporary-marker flush/replacement,
  every pending destructive action before and after native-catalogue drop or a
  literal preliminary-child deletion, and the `AdoptedPreliminary` to
  normal-`Incomplete` transition;
- the exact destructive sequence, missing-state idempotency, per-step
  revalidation, cancellation and post-wipe incomplete state are deterministic,
  including restart at every owner-marker transition, bootstrap prefix and
  literal-child deletion boundary;
- identity swaps between validation and each child delete, marker replace,
  configuration write, stage move and marketplace write are rejected by the
  handle-relative no-follow implementation before the target mutation;
- VSS API query/create-or-change execution uses only `I:` and 10%, without
  encryption, snapshot or restore; it distinguishes exact existing,
  supported-absent, unsupported-absent, foreign, failed and interrupted states
  before application deletion;
- database creation/migration/readiness use the exact catalogue/MDF/LDF and no
  connection string is disclosed;
- Full-Text, bootstrap create/drop/login authority, local SQL service SID and
  fixed app-pool login SID/roles/grants are all preflighted before VSS; tests
  cover correct existing, foreign-SID, server-role/DDL and insufficient
  bootstrap-principal cases;
- the proved empty-catalogue marker reaches strict SQL and HTTP readiness with
  no USearch file access, while marker-absent/null-active, marker-plus-active
  and every non-zero SQL vector/generation/membership contradiction remain
  unavailable; the first normal index publish clears the marker atomically;
- only the canonical data hierarchy is written, every write is no-follow
  guarded and unexpected entries prevent a wipe;
- stage/hash/swap/pool lifecycle, direct-loopback probes and forwarded/proxy
  denial succeed only in the expected order;
- Codex registration is reached only after healthy host validation, is
  idempotent and does not alter unrelated configuration; and
- the production marketplace action invokes only `codex plugin marketplace
  add` plus bounded `list --json` verification, never `codex plugin add` or a
  plugin activation command; and
- unencrypted restricted-key-ring creation, restart, rotation and cursor
  protect/unprotect succeed in both IIS app-pool and interactive CLI directions
  under their Config/data-protection ACLs, without a DPAPI/encryption provider;
- normal CLI, Web, MCP and library composition expose no production SQL/root
  initialisation path; and a completed live journal followed by failed final
  push, cleanup or rerun performs no second host mutation;
- Phase 6 and Outlook remain inactive.

Required final evidence is locked restore, zero-warning Release build,
focused Domain/Integration/Web script checks, full native Release suite, EF
no-pending-model verification, independent implementation and whole-slice
reviews, then one explicitly authorised live run using the mandated
`complete-feature.ps1` sequence. The live report must show the actual
preflight, wipe, VSS, SQL, deployment, loopback and plugin results without
revealing secrets.
