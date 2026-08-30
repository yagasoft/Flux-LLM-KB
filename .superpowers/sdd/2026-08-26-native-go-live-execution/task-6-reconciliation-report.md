# Task 6 reconciliation report: accepted completion-marker risk

## Decision and reviewed range

This reconciliation compares the simplified Task 6 state at `7054ef8` with the later remediation at `ed21ba6`. The user explicitly accepted the complete-marker crash risk and directed the implementation to retain the `7054ef8` completion path while restoring the non-crash safeguards identified by the later review.

This report supersedes only the complete-marker recovery claims in the later corrective sections of `task-6-report.md`. The remaining Task 6 authority, host, payload, ACL, VSS and non-crash SQL safeguards still apply.

The resulting contract therefore keeps one completion transition: after the journal is durably `CanonicalMarkerDurable` and the outer phase is `Incomplete`, publish writes the `Complete` root marker and starts the pool. It does not add `CompleteMarkerPending`, `CompleteMarkerDurable` or `CanonicalCompleteWithTemporaryMarker`. A crash across that marker replacement remains an accepted risk.

## Reconciled safeguards

| Safeguard | Result |
| --- | --- |
| Residual SQL absence | From `CatalogueDropped` onward, every recovery prefix requires both canonical and arbitrary SQL payload to be absent. The valid-prefix table and exhaustive invalid residual matrix cover catalogue, spool and SQL combinations. |
| Fresh signing provenance | The bootstrap refuses every pre-existing canonical procedure, certificate, certificate login or relevant signature. It then creates a fresh certificate and certificate-mapped login, proves their opaque binary SID/thumbprint equality, and never executes `DROP SIGNATURE`. |
| Target catalogue authority | Bootstrap finalisation transfers catalogue ownership to the certificate-mapped login, drops the bootstrap principal from the target database, revokes target `CONNECT` and `EXECUTE` from `public`, and proves the owner transfer. |
| Effective authority | Server, `master` and target-database token observations include inherited roles, DDL-like grants and broad database/schema `public EXECUTE`. Post-bootstrap validation requires the bootstrap login to have no target access, the app-pool effective findings to be empty, and the opaque catalogue-owner SID to equal the signing certificate-login SID. |
| Trust manifest | The generator retains the pre-existing-procedure refusal, hashes the fresh-security section and procedure definitions, pins the certificate/login names, rejects security-contract mutants and emits a byte-reproducible manifest. |

Certificate-mapped login and catalogue-owner SIDs are handled as lower-case opaque hexadecimal bytes. Only Windows account and service SIDs are parsed as `SecurityIdentifier` values.

## RED evidence

- `tests/native/native-go-live-bootstrap-manifest.ps1` failed because `ed21ba6` had removed the existing-procedure refusal.
- `NativeGoLiveRootMarkerTests` failed four assertions because `ed21ba6` admitted the two extra complete-marker journal states and temporary-complete shape while rejecting the accepted `CanonicalMarkerDurable` complete prefix.
- The focused Integration build failed because `ed21ba6` still required the expanded journal-returning `MarkNativeRootIncompleteAsync` and `PublishAndStartAsync` interface.

## GREEN evidence

The following non-live checks passed after reconciliation:

```text
pwsh -NoProfile -File tests/native/native-go-live-bootstrap-manifest.ps1 -SourceRoot .
Native go-live bootstrap manifest passed.

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~NativeGoLiveRootMarkerTests
Passed: 30, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NativeGoLiveGuardedHostTests|FullyQualifiedName~NativeGoLiveExecutorTests|FullyQualifiedName~NativeGoLiveHostLifecycleTests|FullyQualifiedName~NativeGoLiveWindowsAdapterTests"
Passed: 191, Failed: 0, Skipped: 0
```

Final broader verification also passed:

```text
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 40, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 206, Failed: 0, Skipped: 0

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.
```

No IIS, SQL Server, VSS, filesystem-root, publish, marketplace, network, deployment, restart or cutover operation was run.

## Remaining risk and Task 7 boundary

The accepted complete-marker crash window is not repaired by this reconciliation. Task 7 must not describe it as restart-safe or broaden authority around it. All other Task 6 closeout, capability, payload, ACL, bootstrap and effective-authority gates remain in force.

## Follow-up critical correction: creation-only procedure DDL

The first reconciliation retained the initial existing-procedure guard but still defined the four canonical procedures with `CREATE OR ALTER`. A procedure plus stale `public` grant created after the initial guard could therefore be overwritten and signed without losing that grant.

The four definition batches now use `CREATE PROCEDURE` only. If any canonical object appears after the initial guard, its corresponding creation batch fails instead of replacing and signing it. The manifest generator requires the creation-only prefix for every hashed definition and rejects a mutation that restores `CREATE OR ALTER` to even one canonical procedure.

RED was observed as the bootstrap manifest test rejecting the then-current DDL with `Every canonical procedure must use creation-only DDL`. After the correction, fresh non-live verification passed:

```text
pwsh -NoProfile -File tests/native/native-go-live-bootstrap-manifest.ps1 -SourceRoot .
Native go-live bootstrap manifest passed.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 40, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 206, Failed: 0, Skipped: 0

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.
```

No live SQL, IIS, VSS, deployment, restart, marketplace or cutover action was run.

## Follow-up critical correction: fail-fast signing and exact procedure grantees

Creation-only DDL did not by itself stop SQLCMD from advancing to later signing batches after a raced `CREATE PROCEDURE` failure. The canonical source now starts with `:On Error exit` and directs operators to invoke `sqlcmd -b`. The generator requires that first-line directive, and its mutation matrix proves that changing it to continue-on-error is rejected. Together with creation-only procedure batches, a failed or raced create cannot advance to signing.

Immediately before the signature batch, the source now rejects any object or column permission on any of the four canonical procedures. Only after the clean check and signature creation does it grant each procedure's `EXECUTE` permission to the bootstrap login.

The SQL observation contract now returns every direct object/column permission and grantee for each canonical procedure. Both preflight and post-bootstrap validation require exactly one row per procedure: `EXECUTE` with state `GRANT`, minor id `0`, to the current bootstrap `WINDOWS_USER`. Added tests reject direct `public EXECUTE`, foreign-user `EXECUTE` and any extra noncanonical permission in both gates.

RED was observed in two independent checks: the bootstrap manifest test rejected the missing first-line fail-fast directive, and the focused Integration build failed because the per-procedure permission evidence type did not yet exist. After the correction, fresh non-live verification passed:

```text
pwsh -NoProfile -File tests/native/native-go-live-bootstrap-manifest.ps1 -SourceRoot .
Native go-live bootstrap manifest passed.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 40, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 212, Failed: 0, Skipped: 0

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.
```

No live SQL, IIS, VSS, deployment, restart, marketplace or cutover action was run. The previously accepted complete-marker crash risk remains unchanged.
