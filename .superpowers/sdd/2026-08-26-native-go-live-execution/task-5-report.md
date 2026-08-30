# Task 5 report: safe native go-live storage and Codex marketplace adapter

## Outcome

Task 5 is implemented without composing the live host or introducing a process-backed Codex runner. The delivered surface is limited to:

- handle-relative Windows filesystem primitives for verified directories and literal children;
- a stable native go-live lease and journal compare-and-swap store;
- a go-live-only Codex marketplace adapter using Task 2's claimed provisioning capability;
- a shared no-follow manifest writer; and
- focused integration tests using temporary paths, deterministic interlocks and fake marketplace runners.

No real Codex CLI, network, source parsing, model, GPU, FFmpeg, deployment or production mutation was run.

## Implemented behaviour

### Handle-relative filesystem primitives

`HandleRelativeNativeFileSystem` opens every absolute path segment without following reparse points, then performs child operations relative to the verified parent handle. It supplies internal directory open/create, file replace, literal-child delete and literal-child move operations.

The mutation boundary revalidates the parent and expected child identity. Literal child names containing traversal, separators or wildcard syntax are rejected before mutation. Reparse-point children, identity changes, unknown temporary files and non-empty directory deletion are rejected. Deletion is a single-handle disposition operation: there is no wildcard or recursive production deletion. Move and replace use a destination directory handle rather than an absolute destination path.

Replace writes an exact sibling temporary file, flushes the stream and underlying file, revalidates the destination, replaces handle-relatively, reopens the destination and verifies both its identity and bytes. A matching flushed temporary payload can resume after a crash; an unknown or mismatching temporary payload is rejected without replacement.

### Stable lease and journal CAS

`NativeGoLiveLease` coordinates through the exact named mutex `Global\\FluxKnowledge.NativeGoLive.v1` and a stable, ignored `native-go-live.lock`. Mutex ownership remains on a dedicated thread because Windows mutex ownership is thread-affine across asynchronous continuations. The lease also holds a verified no-follow parent handle and an exclusive stable lock-file handle.

`NativeGoLiveJournalStore` reads and closes the replaceable journal before replacement. Under the stable lease it validates the current record, compares the complete expected value, writes and flushes the next record, replaces handle-relatively, reopens the journal and verifies the persisted record. It rejects a missing/existing mismatch, execution mismatch, stale same-execution compare, foreign temporary sibling, corrupt post-replace record and post-replace identity mismatch.

The exact stable lock name was added to `.gitignore`.

### Narrow Codex marketplace adapter

The go-live adapter is internal and requires the claimed `NativeGoLiveProvisioningCapability` from Task 2. The separately consumable marketplace authority and its binding surface were removed.

Normal registrar and CLI composition remains status-only. The go-live-only adapter exposes a typed runner that can perform only:

1. `codex plugin marketplace add`
2. bounded `codex plugin marketplace list --json`

An exact existing registration with the same canonical source is a no-op. Foreign configuration and expected-identity changes fail before manifest-writer or command-runner mutation. After add, the bounded JSON list must confirm the exact canonical local source and the unrelated-configuration structural hash must remain unchanged. The adapter does not retain unrelated configuration content.

## RED evidence

The tests were introduced before the corresponding production implementation.

1. Initial filesystem/journal filter:

   `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~HandleRelativeNativeFileSystemTests"`

   RED: compilation failed because `HandleRelativeNativeFileSystem`, `NativeGoLiveJournalStore`, `NativeGoLiveLease` and their result/identity contracts did not exist.

2. First filesystem/journal implementation run:

   RED: 7 passed and 6 failed. Each failing replace/move path reported Windows error 87, `The parameter is incorrect`, at the root-relative rename boundary. The cause was isolated to the Win32 rename information wrapper. Changing that boundary to `NtSetInformationFile(FileRenameInformation)` made the isolated test and then the full filter pass.

3. Initial marketplace filter:

   `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeCodexPluginRegistrarTests`

   RED: compilation failed because the narrow native marketplace runner, sanitised preflight and command-result contracts did not exist.

4. Exact crash-temporary resume test added during self-review:

   RED: the store returned `journal-temporary-file-unexpected` instead of resuming a byte-for-byte matching flushed temporary record. Support was added only for that exact payload; mismatching or foreign temporaries remain conflicts. The isolated test then passed.

## GREEN evidence

Fresh final commands were run from `E:\LLM KB\.worktrees\native-mcp-live-contract`.

### Required Task 5 matrix

Command:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~HandleRelativeNativeFileSystemTests|FullyQualifiedName~NativeCodexPluginRegistrarTests"`

Result: passed 30, failed 0, skipped 0.

### Adjacent native go-live compatibility

Command:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FreshStartExecutorTests|FullyQualifiedName~NativeGoLiveExecutorTests|FullyQualifiedName~NativeCodexPluginMarketplaceTests"`

Result: passed 42, failed 0, skipped 0.

Command:

`dotnet test tests\FluxKnowledge.Domain.Tests\FluxKnowledge.Domain.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~NativeGoLiveAuthorityTests|FullyQualifiedName~NativeGoLiveRootMarkerTests|FullyQualifiedName~NativeGoLivePlanTests"`

Result: passed 38, failed 0, skipped 0.

### Build and diff hygiene

Command:

`dotnet build FluxKnowledge.slnx -c Release --no-restore`

Result: build succeeded with 0 warnings and 0 errors.

Command:

`git diff --check`

Result: exit code 0 with no whitespace errors.

## Self-review

- Scope: the live host is not composed, and no concrete process implementation was introduced. Process composition remains a later task.
- Filesystem safety: mutation targets are literal children beneath held, verified handles; production code contains no recursive or wildcard delete/move.
- Reparse safety: all path segments and final children are opened no-follow and reparse points are rejected.
- Race safety: deterministic validation-to-mutation identity swaps are rejected for delete, move and replace; the destination is reopened and verified after replacement.
- Journal durability: the replaceable journal handle is closed before replacement; the temporary record is flushed; the final record is reopened and compared under the stable mutex and lock-file lease.
- CAS semantics: execution mismatch, stale complete-record mismatch, missing/existing mismatch and lock contention are distinct non-mutating conflicts.
- Marketplace safety: foreign preflight state and identity mismatch are rejected before writer/runner calls; successful registration is exactly add then bounded list verification; exact same-source state is mutation-free.
- Authority: only Task 2's provisioning capability gates the adapter; the former marketplace-specific authority is absent.
- Compatibility: Task 1-4 authority, root-marker, plan, executor and marketplace projection suites remain GREEN.

## Concerns and deferred work

- The adapter intentionally has no real Codex process runner and is not composed into the live host. Task 6 must provide the constrained process boundary and orchestration without weakening the typed add/list contract.
- The native implementation is Windows-specific by design and its integration tests require Windows handle and reparse semantics.
- Fresh-start no longer returns or binds the removed marketplace-specific authority. This is an intentional security-contract change required by Task 5; disposable reset behaviour remains covered by the adjacent compatibility suite.

No blocking concern remains for Task 5.

## Fix round 1: race and timeout review findings

### Corrections

- Expected-destination identity is now guarded by a no-delete-sharing handle across the mutation boundary. After the post-validation race interlock, the implementation reopens and verifies the expected identity, moves that exact handle to a literal reserved backup name, installs the already-flushed temporary file without replacement, reopens and verifies the installed identity, then deletes only the held prior handle. If installation fails, it restores the held prior handle before propagating the failure. A raced foreign destination is rejected without being replaced or deleted.
- The final temporary handle now requests read plus rename access while denying write and delete sharing. Its identity and complete payload are re-read from that same held handle immediately before any destination mutation. A durable corruption injected after flush returns `temporary-payload-changed`, retains the corrupt temp for diagnosis and leaves the prior journal byte-for-byte unchanged.
- Foreign-temporary inspection no longer calls `Directory.EnumerateFileSystemEntries` or re-resolves `_root`. `NtQueryDirectoryFile(FileNamesInformation)` enumerates names through the already-held verified parent handle. A root-binding race test proves that changing the path binding after the handle is acquired cannot redirect inspection.
- Marketplace command bounding now awaits the runner task with the linked timeout token. Fakes that ignore cancellation and never complete are returned as the existing add or verification timeout result instead of holding the adapter forever.

### RED evidence

Initial focused compilation after adding the four behavioural tests failed with:

- `CS0117`: `NativeGoLiveJournalStoreStage` had no `BeforeTemporaryInspection` boundary.
- `CS0117`: `NativeFileOperation` had no `ReplaceFileAfterDestinationValidation` boundary.

After adding only the deterministic race boundaries, the exact focused command was:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Replace_guards_the_expected_destination_identity|FullyQualifiedName~Foreign_temporary_is_inspected_through_the_held_parent|FullyQualifiedName~Corrupted_flushed_temporary_is_rejected|FullyQualifiedName~Marketplace_enforces_timeout_when_runner_never_completes"`

Result: failed 5, passed 0. The failures were behavioural and matched the review findings:

- destination race returned `Changed = true` and replaced the foreign destination;
- flushed-temp corruption reached post-replace verification and threw `journal-replace-verification-failed`, after the prior record had already been replaced;
- the first physical ancestor-swap fixture was correctly blocked by Windows sharing, so it was replaced with a deterministic root-binding injection rather than weakening the assertion; and
- both non-completing marketplace cases exceeded the independent one-second test guard with `TimeoutException`.

With the final root-binding injection and the original path-based implementation restored, the isolated test failed because the mutation returned `Changed = true`: the re-resolved clean path hid the foreign temp beneath the already-held parent. This provided the behavioural RED for the handle-relative inspection correction.

A final late-writer test was then added at the post-payload-validation boundary. Its first isolated run failed compilation with `CS0117` because `ReplaceFileAfterTemporaryValidation` did not yet exist. After adding only that boundary, the same test proved the held temporary blocks a second writer until installation and preserves the flushed `after` payload.

The first guarded-replacement implementation used `FileRenameInformationEx` with POSIX replacement. The focused Task 5 matrix exposed that Windows still returned sharing violation while the destination identity guard denied delete sharing: 34 passed, 1 failed. The replacement boundary was corrected to mutate and delete only the held expected-destination handle.

### GREEN evidence

Focused review-finding command:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Journal_replace_flushes_then_reopens_and_verifies_under_the_stable_lock|FullyQualifiedName~Replace_guards_the_expected_destination_identity|FullyQualifiedName~Corrupted_flushed_temporary_is_rejected|FullyQualifiedName~Foreign_temporary_is_inspected_through_the_held_parent|FullyQualifiedName~Marketplace_enforces_timeout_when_runner_never_completes"`

Result: passed 6, failed 0, skipped 0.

Complete Task 5 command:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~HandleRelativeNativeFileSystemTests|FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~NativeCodexPluginRegistrarTests"`

Result: passed 36, failed 0, skipped 0.

### Fix-round self-review

- Destination race: the final verified destination handle is never closed between identity verification and moving that exact identity; the later delete is issued against that same held handle, not a re-resolved name.
- Temporary race: the held final temp handle blocks writers/deleters, and its exact bytes are checked before the first destination mutation.
- Journal binding: production foreign-temp inspection contains no path enumeration and consumes only names returned through the verified directory handle.
- Timeout: `CancelAfter` now bounds the await itself; caller cancellation continues to propagate, while internal timeout retains the existing sanitised result reasons.
- Scope: no real process runner, Codex command, live-host composition, network action or broad test suite was added or run.

No blocking concern remains after fix round 1.

## Fix round 2: interrupted guarded-replacement recovery

### Corrections

- The reserved `native-go-live.json.replace-backup.tmp` is now an explicit journal recovery artefact rather than an unknown temporary that permanently blocks CAS.
- Compare-and-swap inspects the backup, canonical and next-temp through the held verified parent before ordinary comparison. Recovery accepts only the exact serialised expected backup and exact serialised next temp supplied by the retrying CAS.
- If the canonical is absent and the exact next temp remains, recovery installs that temp through the same no-follow, exact-content replacement primitive, reopens/verifies it and deletes only the expected backup identity.
- If the canonical already contains the exact next record and the next temp has been consumed, recovery treats the transition as committed and deletes only the expected backup identity.
- If the canonical is occupied while the exact backup and next temp remain, recovery returns `foreign-destination-occupied` and preserves all three. Once the operator/test moves that foreign file aside, the same expected-to-next retry completes automatically.
- Installation failure no longer attempts a non-replacing rollback into a possibly occupied canonical name. With no occupant it leaves the exact backup/temp tuple for retry (`file-install-interrupted`); with an occupant it returns the explicit foreign conflict. This avoids overwriting/deleting foreign data and avoids masking the install cause with a failed rollback.
- Unknown backup bytes, unknown next-temp bytes and any additional native go-live temporary sibling remain non-mutating conflicts.

### RED evidence

The backup-move, install-occupancy and backup-delete recovery tests were added first. Their initial focused build failed with missing `AfterReplacementBackupMove`, `BeforeReplacementInstall`, `BeforeReplacementBackupDelete` and `ReplacementBackupPath` seams.

After adding only those failure-injection seams, the exact command was:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Crash_after_backup_move|FullyQualifiedName~Foreign_canonical_at_install|FullyQualifiedName~Crash_before_backup_delete"`

Result: failed 3, passed 0. Both restart cases returned `journal-temporary-file-unexpected`, proving the reserved backup wedged CAS. The foreign-install case threw from the non-replacing rollback with Windows `Cannot create a file when that file already exists`, masking the foreign occupancy and stranding the transaction.

### GREEN evidence

Failure-injection slice:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Crash_after_backup_move|FullyQualifiedName~Foreign_canonical_at_install|FullyQualifiedName~Crash_before_backup_delete"`

Result: passed 3, failed 0, skipped 0.

Complete Task 5 matrix:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~HandleRelativeNativeFileSystemTests|FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~NativeCodexPluginRegistrarTests"`

Result: passed 39, failed 0, skipped 0.

### Fix-round self-review

- Backup-move interruption: prior bytes remain at the exact reserved backup and next bytes remain at the exact flushed temp; retry installs and verifies next, then deletes the expected backup identity.
- Install occupancy: no replace flag or rollback can touch the foreign canonical; backup and next temp remain recoverable, and retry succeeds after foreign occupancy is moved aside without altering the foreign bytes.
- Backup-delete interruption: canonical next remains readable; retry recognises exact committed next, removes only the exact expected backup and returns idempotent success.
- All recovery inspection and mutations remain literal-child, handle-relative, no-follow and under the stable mutex/lock lease.
- No live action, process runner, Codex CLI, host composition or broad suite was run.

No blocking concern remains after fix round 2.

## Fix round 3: atomic canonical guard during recovery cleanup

### Correction

Recovery no longer reads and closes the canonical next record before deleting the last expected-record backup. Immediately before cleanup it now opens the canonical literal child with read access while denying write and delete sharing, revalidates both the expected installed identity and exact next payload from that same handle, and holds the handle until `DeleteLiteralChild` has revalidated and deleted the exact backup identity.

The deterministic recovery interlock runs before this final guard is acquired. A swapped, deleted, reparse or otherwise unavailable canonical therefore returns `journal-recovered-canonical-changed`; the backup is not opened for deletion and remains recoverable.

### RED evidence

The recovered-install swap and already-committed delete tests were added first. Their initial focused build failed with `CS0117` because `BeforeRecoveredBackupDelete` did not yet exist.

After adding only that interlock before the old unguarded delete, the focused command was:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Recovered_install_canonical_swap|FullyQualifiedName~Already_committed_canonical_delete"`

Result: failed 2, passed 0. Both mutations incorrectly returned `Changed = true`, confirming that a swapped or deleted canonical could cause false completion and deletion of the last expected-record backup.

### GREEN evidence

Focused race command:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~Recovered_install_canonical_swap|FullyQualifiedName~Already_committed_canonical_delete"`

Result: passed 2, failed 0, skipped 0.

Complete Task 5 matrix:

`dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~HandleRelativeNativeFileSystemTests|FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~NativeCodexPluginRegistrarTests"`

Result: passed 41, failed 0, skipped 0.

### Fix-round self-review

- Recovered-install path: a foreign replacement of the newly installed canonical is detected before backup mutation; foreign, parked-next and expected-backup bytes are all retained.
- Already-committed path: deletion of canonical next is detected before backup mutation; a later retry restores expected from the retained backup and completes expected-to-next normally.
- Atomic guard: after the final canonical handle opens, its identity and content cannot be changed or unlinked by another opener before the exact backup deletion completes.
- Backup race: backup deletion still performs its own literal-child expected-identity revalidation while the canonical guard remains held.
- No live action, Codex CLI, process runner, host composition or broad suite was run.

No blocking concern remains after fix round 3.
