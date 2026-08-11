## Task 2 report

- Capability: added SQL-authoritative private Outlook profile, folder, operation, export, browse and catch-up entities, a generated `AddNativeOutlookIngress` migration, and a closed `SqlOutlookCaptureStore` implementation. The model keeps canonical Outlook identifiers and relative spool evidence in private SQL fields; local projections expose neither store/folder entry identifiers nor the spool root.
- RED evidence: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlOutlookCaptureStoreTests|FullyQualifiedName~OutlookSchemaMappingTests"` exited 1 before implementation. The compiler reported `CS0246` for the intentionally absent `SqlOutlookCaptureStore` in both store tests.
- GREEN evidence: the same command exited 0 after implementation: 2 passed mapping tests, 4 SQL-backed store tests skipped because `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` was unavailable. `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build` exited 0 and reported no pending model changes. `git diff --check` exited 0.
- SQL fixture availability: unavailable in the process environment; no SQL Server, Outlook, mailbox or external service was contacted.
- Changed files: private Outlook entities and host-control entities; SQL DbContext/configuration/store; generated migration, designer and snapshot; Outlook store and schema-boundary tests.
- Commit: `4d8d3f1 feat: persist Outlook capture evidence`.
- Risks: the Task 1 save contract carries only a sanitised spool path fingerprint, so the store cannot persist the configured raw spool root without a future contract change. Task 1 also exposes no enable operation, therefore saved profiles stay disabled by design and cannot create catch-up work until the later control-plane contract supplies an enablement path. Task 3 must supply ready-export creation and cursor advancement; Task 2's commit path only accepts an existing, fenced ready export.
- Vertical-slice progress: durable control-plane persistence and safe local projection are present; no COM activation, real mailbox access, export writer, processor activation, UI or deployment work was added.

## Fix round 1/5

- Added generated `HardenNativeOutlookIngress` migration for profile-linked browse requests, relational constraints and immutable deferred-capability evidence.
- Catch-up mutations now require an unexpired injected-clock lease and terminal rows preserve their immutable lease claim. Coalescing is history-safe: terminal rows no longer prevent later requests with the same key.
- Browse completion requires private canonical folder identities, an active unexpired fence, and the request/current profile revision before upserting canonical folders; safe browse projections remain identifier-free.
- Export commit accepts a private ready observation, is idempotent for the same folder/EntryID/fingerprint, marks fingerprint conflicts as durable `Blocked`, and advances the folder cursor only with the committed export.
- Profile saves now retain a private spool root and explicit enable state. A `DeferredCapabilities` table retains artifact fingerprint, capability and immutable provenance without paths or raw source content.
- RED/GREEN: the Task 1 Outlook contract suite initially failed after strengthening browse result typing (`CS0029`); it passed after restoring the safe public projection and adding a separate private completion identity list: 10 passed. Focused integration compilation/model run passed 2 mapping tests; 4 SQL invariant tests remain explicitly skipped because the disposable SQL environment variable is absent, so no live SQL invariant proof is claimed.
- `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build` reported no pending changes; `git diff --check` passed.

## Fix round 2/5

- RED: with the disposable server-level SQL fixture, all four SQL store tests failed while applying the first Outlook migration: `The size (4096) given to the parameter 'EntryId' exceeds the maximum allowed (4000)`.
- Fixed private canonical StoreId, FolderEntryId and EntryId persistence to `nvarchar(max)` and removed invalid oversized key indexes. The upgrade migration conditionally removes the legacy indexes before altering columns, so fresh databases and prior migration histories can both apply it.
- GREEN: `$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION='Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'; dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlOutlookCaptureStoreTests|FullyQualifiedName~OutlookSchemaMappingTests"` passed: 6 passed, 0 failed, 0 skipped.

## Fix round 3/5

- RED: added a schema assertion for indexable canonical SHA-256 identity columns. It failed because neither the folder nor export private entity exposed a digest.
- Added generated `EnforceOutlookCaptureIdentityFences` migration with stored SHA-256 computed columns and unique indexes over `(ProfileId, canonical folder digest)` and `(FolderId, EntryID digest)`. The catch-up active uniqueness model is now a filtered unique index over states 0/1, preserving terminal history.
- GREEN: disposable SQL focused suite passed: 7 passed, 0 failed, 0 skipped. The schema assertion passes and a fresh generated SQL catalogue applies every migration.
- Remaining: this narrow batch has not yet supplied the requested real-SQL export receipt/fence, collision verification, catch-up concurrency, or operation concurrency behaviour tests; no completion claim is made for those items.

## Fix round 4/5

- RED: the disposable-SQL focused suite ran 17 tests and failed 11 for the intended behaviour gaps: stale export fences and profile/folder mismatches were accepted, cursor state regressed, accepted export evidence was mutated instead of retaining blocked conflict evidence, delimiter-concatenated folder identities collided, and operation, catch-up, canonical-folder and canonical-export races leaked `DbUpdateException`.
- Export commits now require a real profile/folder binding and an active unexpired catch-up fence before accepting new evidence. Durable observations retain profile, folder, EntryID, source fingerprint, manifest hash, private relative spool path and fencing token. Exact canonical replays return the prior durable export ID without mutating it; divergent observations create separate blocked evidence while the accepted ingested row remains unchanged. Folder cursors advance only monotonically after an accepted commit.
- The shared SQL mutation path now retries a unique-key loser once in a fresh transaction, reloads the database winner, replays matching operation fingerprints, and fails divergent operation fingerprints closed. Concurrent catch-up requests coalesce to one active catch-up resource; concurrent browse completions reuse one canonical folder; concurrent export observations deterministically reuse or record conflict evidence without exposing `DbUpdateException`.
- Generated migration `HardenOutlookCaptureReplay` changes the canonical folder digest to unambiguous byte-length-prefixed hashing and filters the canonical export uniqueness fence to non-blocked rows so conflict evidence can coexist with one accepted canonical export. Its upgrade path drops and recreates the dependent folder index around the computed-column change.
- GREEN: the focused disposable-SQL suite passed 17/17 with 0 failed and 0 skipped, then passed three further fresh GUID-catalogue repetitions at 17/17 each. `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build` reported no pending model changes. No Outlook, COM, UI, host, processor, deployment or live mailbox code was changed.
- Remaining concern: this store proves SQL identity, fencing and replay behaviour only; it does not validate a live Outlook mailbox or COM host, which remain outside Task 2 scope.
- Commit: `d053faf fix: harden Outlook capture replay`.

## Fix round 5/5

- RED: the focused disposable-SQL suite ran 22 tests and failed 4 for the intended gaps. An expired catch-up row was incorrectly authorised by another active row that reused fencing token `17`; concurrent claim and cursor mutations leaked `DbUpdateConcurrencyException`; and a divergent observation using the already accepted `ExportId` returned that accepted ID without retaining separate blocked evidence. A further targeted disposable-SQL test forced an existing-profile rowversion change immediately before `SaveChangesAsync` and failed with `DbUpdateConcurrencyException`.
- Export commits now carry and persist the immutable catch-up row ID together with its generation fencing token. Acceptance queries that exact row and requires it to remain claimed and unexpired; another catch-up row with the same numeric token cannot authorise ingestion or cursor movement. The nullable SQL foreign key preserves honest legacy upgrade semantics, while the application contract requires a non-empty catch-up ID for every new commit and EF prevents the stored claim ID/token from being changed after insert.
- The fresh-context mutation loop now reconciles `DbUpdateConcurrencyException` as well as unique-key races. Tests force and verify existing-profile update retry, one-winner duplicate catch-up claim coalescing, and two accepted exports monotonically advancing one folder cursor after a rowversion conflict.
- A divergent observation using an accepted `ExportId` now receives a distinct blocked evidence row and durable operation receipt. The accepted export row and its rowversion remain unchanged.
- GREEN: the exact focused command with `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` set to the supplied local integrated-security connection passed 23/23, with 0 failed and 0 skipped. Three further fresh disposable-catalogue repetitions each passed 23/23. The Outlook contract suite passed 10/10. `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build` reported no pending model changes.
- Generated migration: `BindOutlookExportClaimIdentity`. No Gmail, COM, UI, host, processor, deployment or live mailbox work was added.

## Task 7 closeout correction

The later Task 5 save contract requires an existing enabled profile update to
carry both the current durable configuration revision and a completed browse
correlation for that same revision. Two Task 2 concurrency tests still used the
new-profile request helper, so the disposable-SQL whole-suite run failed in
request validation before reaching their intended replay and row-version paths.

RED: the focused two-test command failed 2/2 with `ExpectedConfigurationRevision`
missing. The correction reads the current revision from the disposable database,
creates a completed browse result bound to that revision, and supplies both values
to the update request. No production code or assertion was changed.

GREEN: the same two tests passed 2/2.

The whole-branch review then found that resolving retained Outlook artifacts by
their profile's unique source-root binding would be unsafe if an existing profile
could rebind that source root to a different private spool. RED: a new disposable-
SQL assertion proved that such a save was accepted. The store now treats the
source-root-to-private-root association as immutable: existing profiles accept
same-root edits only and reject root rebinding without incrementing the revision.

GREEN: the full current Task 2 SQL store/schema filter passed 15/15 with no
skips. The existing synthetic-source-root test retains its original assertions
and now performs the permitted same-root profile edit.
