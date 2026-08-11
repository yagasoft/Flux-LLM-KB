# Task 1 report: Outlook contracts and closed state model

## Delivered capability

Committed `b73af9b6d09d41ae8005ce615d2612906eaee547` (`feat: define Outlook capture contracts`).

The Outlook-only domain and application contract slice now provides:

- immutable, validated capture profile, folder and export identities/states;
- disabled-by-default profiles using `LastModificationTime`, with an explicit received-time manual-reconciliation signal;
- source-fingerprint conflict protection for a stable profile/folder/EntryID export identity;
- closed local configuration commands for create/edit, pause and remove; bounded cadence/overlap validation; and sanitised spool admission evidence covering local-path, ACL, capacity and writability checks;
- private host/catch-up/browse lease contracts with host/session identity, expiry, heartbeat, fencing, bounded failure/completion data, request fingerprints and operation IDs;
- an SQL-authoritative `IOutlookCaptureStore` contract for configuration, coalesced catch-up requests, claims, renewal, completion/failure/requeue, stale release, browse claims/results and export commit; and
- safe local projection records that contain only application-generated IDs and display/state data.

No SQL implementation, COM/Outlook host activation, process, processor, UI/REST/MCP/CLI surface, export flow or Gmail-owned file was added.

## Test-first evidence

### RED

Command:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"
```

Output (exit 1):

```text
...OutlookCaptureContractTests.cs(3,28): error CS0234: The type or namespace name 'Outlook' does not exist in the namespace 'FluxKnowledge.Domain' ...
```

This was the expected failure: the new Outlook domain/application contract types did not yet exist.

### GREEN

Command:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"
```

Output (exit 0):

```text
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 28 ms - FluxKnowledge.Domain.Tests.dll (net10.0)
```

Focused tests cover identity validation, disabled/default basis, conflicting export fingerprints, received-time reconciliation signalling, spool writability evidence, browse fencing, and rejection of non-stale catch-up lease release.

## Modified files

- `src/FluxKnowledge.Domain/Outlook/OutlookCaptureState.cs`
- `src/FluxKnowledge.Domain/Outlook/OutlookCaptureProfile.cs`
- `src/FluxKnowledge.Domain/Outlook/OutlookCaptureFolder.cs`
- `src/FluxKnowledge.Domain/Outlook/OutlookCaptureExport.cs`
- `src/FluxKnowledge.Application/Contracts/OutlookCaptureContracts.cs`
- `src/FluxKnowledge.Application/Ports/IOutlookCaptureStore.cs`
- `tests/FluxKnowledge.Domain.Tests/Outlook/OutlookCaptureContractTests.cs`

## Review and risks

`git diff --cached --check` passed before the commit. A targeted sensitive-field review confirmed that `OutlookProfileProjection` and `OutlookBrowseFolderProjection` expose no StoreId, FolderEntryId, EntryID, spool-root, message-content, attachment, credential, raw-exception or diagnostic fields. Raw Outlook identities exist only in explicitly private domain identities needed for later restricted persistence/reconciliation.

The contracts make duplicate hint coalescing, disabled-profile rejection, stale takeover protection, fenced browse completion and stale-result rejection implementable, but their durable SQL behaviour and integration tests belong to later tasks. The spool evidence contract deliberately carries only a fingerprint and boolean admission evidence; the local filesystem/ACL/capacity probe itself is deferred to the local operator policy work.

## Vertical-slice progress

This advances the control-plane foundation: the Web host and future local Outlook host can coordinate solely through durable contracts without exposing Outlook identifiers to projections. There is not yet an executable end-to-end Outlook capture vertical slice because SQL persistence, host-side lease execution and export processing remain intentionally out of scope for Task 1.

## Fix round 1: reviewer corrections

Committed `14d3d8541702b64e30347af12e89375754cde316` (`fix: harden Outlook capture contracts`).

### Spool fingerprint privacy boundary

`OutlookSpoolValidation.PathFingerprint` now requires a canonical lower-case SHA-256 value. This rejects a path-shaped value such as `C:\operator-spool`, so raw local spool paths cannot pass through the validation evidence contract.

RED command:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"
```

RED output (exit 1):

```text
Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.ArgumentException)
...Spool_validation_rejects_a_path_instead_of_a_non_sensitive_fingerprint
```

### Claim receipt evidence

Catch-up and browse claim store methods now return `OutlookCatchUpClaimReceipt` and `OutlookBrowseClaimReceipt`. Each receipt carries the optional claim with `Accepted`, `Committed`, and `IsReplay` evidence, leaving future SQL implementations able to report an empty eligible-work result without losing durable mutation evidence.

RED command:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"
```

RED output (exit 1):

```text
error CS0246: The type or namespace name 'OutlookCatchUpClaimReceipt' could not be found
error CS0246: The type or namespace name 'OutlookBrowseClaimReceipt' could not be found
```

GREEN command:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"
```

GREEN output (exit 0):

```text
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 34 ms - FluxKnowledge.Domain.Tests.dll (net10.0)
```

Changed files: `OutlookCaptureContracts.cs`, `IOutlookCaptureStore.cs`, and `OutlookCaptureContractTests.cs`. No Gmail, COM, SQL, host, processor or external-surface file changed.
