# Task 3 report: native worker supervision

## Implemented boundary

- Default-disabled native worker options and conditional scheduler composition.
- App-owned worker launch with pre-launch SHA-256 fingerprint, explicit process arguments, private current-user asynchronous pipe, PID/start-time attestation and transient nonce.
- Scoped store/lifecycle operations from the singleton hosted supervisor.
- Exact-handle uncertainty for exit, heartbeat timeout and controlled forced termination; no completion, retry, requeue or capacity-release call is made.
- Private recovery-candidate read fence: potentially live rows block adoption/replacement; active attested rows receive deterministic exact uncertainty and inactive rows become lost.
- Deterministic worker reconnects after controlled pipe EOF retaining its opaque handle; supervisor recreates the private server sequentially and uses stable connection/ack/bind IDs.

## RED evidence

- Native worker option, supervisor registration, stale identity, launch failure, scope validation, exit and heartbeat tests were added before their respective implementation changes and initially failed through absent members, scoped-capture validation or unmet assertions.
- Candidate contract test failed before `NativeWorkerRecoveryCandidate` existed.
- Restart active-candidate test failed before startup recovery fencing existed.

## GREEN evidence

- `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~NativeWorkerContractTests.Recovery_candidate"`: 1 passed.
- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests|FullyQualifiedName~DeterministicWorkerProcessTests"`: 25 passed.
- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests.Restart_candidate_without"`: 3 passed.
- `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror`: 0 warnings, 0 errors.
- `rg` proof for public mutation/network in worker/supervisor sources exited 1; `git diff --check` passed.

## Risks and limits

- The disposable SQL fixture is unavailable in this workspace, so the new SQL recovery-query test is skipped here; it must run in the SQL-enabled CI/disposable catalogue.
- Explicit fresh-nonce sequential reconnect fixture coverage remains required for a later focused test update; existing deterministic-worker and supervisor suites remain green after the sequential reconnect implementation.

## Fix round 1

- Delivery now requires an attested `Ready` frame and durably binds the exact handle before writing a dispatch; a rejected or uncommitted binding fails closed without retaining the handle in memory.
- Acknowledgements and receipts must carry the exact active handle. Receipts map their closed protocol disposition through the lifecycle sink. Callback frames carry no protocol handle and therefore are accepted only while an already-bound active handle exists; they cannot name an unrelated dispatch.
- EOF now checks the launched process: an exited child records exit and makes the bound handle uncertain, while a live child alone may use the sequential reconnect path. Controlled termination requires accepted and committed exact uncertainty first.
- Initial connection is bounded with `ConnectTimeout` using `TimeProvider`; supervisor observation has a cancellable lifetime.

### Fix-round evidence

- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests"`: 13 passed.
- `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror`: 0 warnings, 0 errors.
- The SQL recovery-candidate fixture remains externally blocked: `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is unset. The repository fixture skips without that explicit disposable server-level connection; no substitute was configured.

## Fix round 2: real sequential reconnect proof

- Added a real deterministic-worker two-session fixture. It creates the second named-pipe instance before controlled EOF of the first, then proves the same child PID and start time reconnect with a newly issued nonce before accepting graceful stop.
- Added a supervisor-level real-child fixture which closes the first attested session, verifies the second attestation is for the same process identity, and proves one launch, one durable `Ready` record, no dispatch bind artefact, and one stable deterministic connection operation ID across both sessions.
- The worker-test temporary-directory cleanup now retries a short Windows file-handle race after child exit; the retry was required by a reproducible combined-matrix RED and does not alter product behaviour.

### RED/GREEN evidence

- RED: the first execution of the newly added controlled-EOF fixture failed with `ObjectDisposedException: Cannot access a closed pipe` because the test disposed its writer after the pipe. The corrected fixture disposes reader/writer before inducing EOF.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~DeterministicWorkerProcessTests.Worker_reconnects_after_controlled_eof_with_same_identity_and_a_fresh_nonce" --no-restore`: 1 passed.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests.Real_child_reconnects_after_controlled_eof_without_relaunch_or_duplicate_ready_artifacts" --no-restore`: 1 passed.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests|FullyQualifiedName~DeterministicWorkerProcessTests" --no-restore`: 30 passed.
- GREEN: `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror --no-restore`: 0 warnings, 0 errors.

## Fix round 3: loss-path durable uncertainty fence

- Exact uncertainty no longer clears the supervisor's in-memory active handle until the store reports both `Accepted` and `Committed`. A rejected or uncommitted mutation leaves the handle fenced, so an idle stop cannot become graceful.
- Reconnect accept-timeout and exited pipe-loss paths now attempt exact uncertainty before durable loss/exit evidence. They fail closed without appending replacement-enabling loss evidence when an active-handle mutation is rejected.
- Controlled EOF may surface as either EOF or local pipe `IOException`/`ObjectDisposedException`; live children treat all three as a sequential reconnect condition. The deterministic worker also retries a local connection attempted just before the successor server is created.

### RED/GREEN evidence

- RED: `NativeWorkerSupervisorServiceTests.Rejected_exact_uncertainty_after_active_child_loss_retains_the_handle_and_prevents_idle_stop` failed with active handle `null` against the prior eager-clear implementation.
- GREEN: the same focused test passed after the durable-mutation fence.
- GREEN: focused real reconnect plus rejected-uncertainty tests: 2 passed.
- GREEN: combined supervisor/worker matrix: 31 passed.
- GREEN: Release build with warnings as errors: 0 warnings, 0 errors.

## Fix round 4: controlled EOF read-order reconnect fence

### Root cause

The real reconnect fixture passed in isolation but timed out in the combined
native-worker matrix. Bounded diagnostics established that the original child
was still alive, the supervisor had cleared its first session, and its
observation task was waiting for a successor connection. No second `Hello`
arrived. The controlled close could occur after the parent observed `Ready` but
before the child began its first post-`Ready` read; that left the test without a
deterministic proof that pipe EOF had an active child-side read to release.

Separately, once a local session close was observed, the supervisor's
`continue` targeted the nested frame-read loop. It repeatedly re-entered the
disposed session instead of disposing that scope and creating the successor
pipe server.

### Change

- The controlled-test-only `PostReadyReadSignalName` is passed as a child
  environment value, never a configuration binding, public surface or protocol
  frame. The deterministic child signals a named event only after starting its
  next `ReadAsync`; the real reconnect test waits for that event before it
  disposes the parent session. No timing sleep is used.
- `NativeWorkerPipeSession` makes controlled disposal idempotent, cancels an
  in-flight read and translates its local termination to the existing I/O loss
  path. The supervisor leaves the nested session loop before allocating the
  successor private server, preserves the original process identity and retains
  the fresh-nonce handshake.
- Successor acceptance now also observes child exit, so an exit after a pipe
  close records exact uncertainty and exit evidence without waiting for the
  connect timeout.

### RED/GREEN evidence

- RED: the full supervisor/process matrix failed the real reconnect fixture;
  diagnostics reported `launching, connected, Ready`, cleared parent session,
  live child and no successor connection. The focused fixture alone passed,
  proving an ordering race rather than an identity or nonce failure.
- GREEN: the explicit post-`Ready` read fence and corrected outer-loop
  reconnect transition passed the supervisor class: 19 passed.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~DeterministicWorkerProcessTests|FullyQualifiedName~NativeWorkerSupervisorServiceTests" --no-restore`: 35 passed.
- GREEN: `git diff --check` passed.

### Receipt fixtures

- Invalid parent-owned completion scripts are covered by
  `Invalid_completion_script_rejects_before_binding_or_lifecycle_mutation`.
- Rejected parent-owned receipt persistence is covered by
  `Rejected_parent_owned_completion_receipt_marks_only_its_exact_handle_uncertain_without_callback_or_clear`: it proves the exact handle becomes uncertain and neither callback nor clear occurs.
- The separate disposable-SQL rejected-result-receipt fixture remains present
  but cannot run here because `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is unset;
  no substitute connection was configured.

### Release verification

- `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror --no-restore`: passed with 0 warnings and 0 errors.
- Domain native-worker contracts: 18 passed.
- Integration non-SQL recovery service: 10 passed.
- Integration deterministic-worker and supervisor matrix: 36 passed.
- Web composition safety: 8 passed.

## Fix round 5: generic observer failure preserves rejected uncertainty fence

### Root cause

The generic `ObserveWorkerAsync` exception handler appended terminal `Exited`
evidence before it attempted the exact active-handle uncertainty mutation. If
that mutation was rejected, a recreated host could observe terminal evidence
despite the still-fenced active handle and risk treating the prior worker as
replaceable.

### RED/GREEN evidence

- RED: `Generic_observer_exception_with_rejected_uncertainty_retains_a_nonterminal_recovery_fence_and_blocks_replacement` failed because `Exited` was appended before the injected rejected uncertainty result.
- GREEN: the generic handler now attempts exact uncertainty first. On a rejected
  or uncommitted result it returns without terminal evidence; the active handle
  remains in memory and the durable nonterminal recovery candidate blocks the
  recreated host from launching a replacement.
- GREEN: the focused regression passed.
- GREEN: `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror --no-restore`: 0 warnings, 0 errors.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests|FullyQualifiedName~DeterministicWorkerProcessTests"`: 37 passed.

The disposable SQL fixture remains externally blocked because
`FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is unset.
