# Task 6 report: guarded Windows host lifecycle

## Outcome

Implemented the private native go-live host lifecycle without running it against the machine. The public deployment script is now a read-only plan/refusal façade: `-PlanOnly` returns the canonical `I:\FluxKnowledge`, IIS, VSS and live-validation identities, while direct `-GoLive` refuses because no claimed in-process authority can cross that script boundary. The previous unreachable legacy deployment body was removed, so the public script contains no IIS, SQL, VSS, publish, Codex or process mutation.

The new `native-go-live.psm1` exports only `Get-NativeGoLivePlan`. Its private `Invoke-NativeGoLive -Request <NativeGoLiveRequest>` path accepts a module-issued typed request bound to a real claimed `NativeGoLiveAuthority`, its execution ID, committed SHA, plan hash and journal. It rejects incomplete acknowledgements, malformed or foreign plans, and replay of a claimed authority before host observation.

The private lifecycle performs all observation and validation before the first reversible or irreversible host phase. Its order is preflight, stop the fixed pool, configure typed VSS, destroy only previously proved owned state, bootstrap the canonical catalogue, clear the bootstrap environment, apply exact ACLs, publish the journal-bound payload, start the pool, validate the exact loopback contract, and register the marketplace last.

## Files changed

- Created `scripts/deploy/native-go-live.psm1`.
- Replaced the unreachable execution body in `scripts/deploy/update-native-windows.ps1` with a read-only plan/refusal façade.
- Created `src/FluxKnowledge.Integrations/Windows/NativeGoLive/VssDiffAreaAdministration.cs`.
- Added the closed loopback/MCP contract to `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLivePorts.cs`.
- Created `tests/native/native-go-live-contract.ps1`.
- Created `tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveHostLifecycleTests.cs`.
- Updated `tests/native/native-deployment-plan.ps1` and `tests/native/phase-5-deployment-safety.ps1` for the new private-module/public-façade boundary.

## Authority and public-surface boundary

- `Invoke-NativeGoLive`, its request factory and every operational helper remain unexported. The only exported command is the pure plan builder.
- The request factory accepts only the exact CLR `NativeGoLiveAuthority` type in `Claimed` state and binds its execution ID, committed SHA and plan hash to the request journal and canonical PowerShell plan.
- A concurrent dictionary consumes the authority execution ID before preflight. A second invocation in the same module process returns `go-live-authority-consumed` without a host call.
- `update-native-windows.ps1 -GoLive` always refuses with the claimed-in-process-authority reason. It has no parameter capable of serialising, forwarding or reconstructing authority.
- `-PlanOnly` preserves the existing preparation schema and adds `executionAvailable = false`, `root`, the fixed site/port, the exact 10% `I:` VSS policy, and the nine-tool MCP contract.

## Preflight and least privilege

The preflight contract rejects before pool stop or VSS:

- a site, app pool, physical path or binding other than `FluxKnowledge`, `I:\FluxKnowledge\App` and one `http://127.0.0.1:5137` binding;
- disabled anonymous authentication or enabled Windows authentication;
- a bootstrap connection that is absent, malformed, over 2,048 characters, non-loopback, not integrated, not attached to `master`, contains SQL credentials/attach-file state, or contains an unused option such as a failover partner;
- a catalogue/file identity other than the canonical `FluxKnowledge` MDF/LDF pair;
- absent Full-Text or bootstrap evidence broader/narrower than create/drop `FluxKnowledge` and manage `IIS AppPool\FluxKnowledge`, including any server role;
- an unallowlisted SQL service identity, a SQL-service write-root set other than the two canonical SQL directories, or app-pool MDF/LDF access;
- an existing app-pool login with a foreign SID, sysadmin/server-role membership or DDL grant;
- a noncanonical, reparse, foreign or non-native root/payload, or a SHA/plan-hash mismatch;
- enabled Outlook, Phase 6, model runtime, GPU, FFmpeg or network parsing;
- VSS state other than `ExactExisting` or `SupportedAbsent`, a cross-volume association, an empty volume identity or capacity below the API minimum;
- a foreign Codex marketplace identity.

After bootstrap, the lifecycle accepts only these app-pool ACL facts:

- Read/execute on `App`;
- Read on `Config`;
- Modify on `Config\data-protection`, `Data\Index`, `Data\Retained`, `Runtime\Spool`, `Runtime\Temp` and `Runtime\Logs`;
- no write to App/general Config, no SQL-file access and no Recovery access.

The SQL bootstrap environment value is cleared in a `finally` block on every outcome and is also cleared immediately after bootstrap, before ACL, publish, start, loopback probe or Codex work. Results and exception reason codes contain bounded safe facts only; no connection string or credential is returned or logged.

## Typed VSS implementation

`VssAssociationState` contains exactly `ExactExisting`, `SupportedAbsent`, `ForeignAssociation`, `Unsupported`, `Failed` and `Interrupted`. `VssDiffAreaState` carries source/storage volume GUIDs and the optional maximum byte value.

The internal operational adapter:

- resolves `I:` to its volume GUID with `GetVolumeNameForVolumeMountPoint`;
- obtains total capacity with `GetDiskFreeSpaceEx`;
- uses the Microsoft software VSS provider through typed `IVssSnapshotMgmt`, `IVssDifferentialSoftwareSnapshotMgmt` and `IVssEnumMgmtObject` COM contracts;
- queries both source and storage associations by GUID and rejects any foreign association;
- queries supported diff-area storage when the association is absent;
- calculates exactly 10% with checked decimal arithmetic and rejects values below the VSS minimum;
- calls only `ChangeDiffAreaMaximumSize` for `ExactExisting` and only `AddDiffArea` for `SupportedAbsent`;
- re-queries and requires the exact source GUID, storage GUID and maximum before returning success.

There is no `vssadmin`, command-output parsing, snapshot creation, encryption or restore path.

## Live validation contract

The fake host contract proves that marketplace registration cannot run until all of these succeed:

- HTTP 200 from `/health/live`, `/health/ready` and `/api/index-health`;
- SQL readiness;
- zero ready, active, deferred and uncertain GPU work with no active batch;
- HTTP 200 from bounded synthetic `POST /api/v1/knowledge/search`, limit one, successful with an empty result;
- HTTP MCP initialise and tools/list status 200;
- exactly `knowledge.search`, `knowledge.write`, `knowledge.graph`, `code.query`, `code.write`, `corpus.query`, `corpus.write`, `operations.status` and `operations.audit`;
- HTTP 403 for the Forwarded case and the direct non-loopback-peer case;
- no proxy and no redirect;
- a published SHA equal to the journal-bound merged-main SHA.

## RED evidence

The first required PowerShell run failed with `The private native go-live module is missing.`

The first required .NET run failed compilation with the expected missing production contracts, including `CS0246` for `VssAssociationState`, `IVssDiffAreaComApi` and `VssVolumeDiffAreaState`.

During tightening, the failover-option test returned a successful lifecycle instead of rejecting the connection. After that RED witness, the parser received its explicit option allowlist. The extra app-pool Modify-root test likewise reached publish before the exact ACL-set checks were added.

## GREEN evidence

The required commands were run without any live host operation:

```text
pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.
```

```text
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLiveHostLifecycleTests
Passed: 9, Failed: 0, Skipped: 0
```

The broader native contract filters passed 50 Integration tests and 38 Domain tests. Both adjacent PowerShell contracts also passed:

```text
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

`dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror` succeeded with 0 warnings and 0 errors. `git diff --check` returned exit code 0 with no output.

## Self-review

- Public surface: the public script has no production host command and cannot accept authority; the module exports no executor.
- Ordering: preflight precedes pool stop; VSS precedes deletion; bootstrap state is cleared before all child/process-like phases; marketplace is last.
- VSS: mutations receive only the resolved `I:` volume GUID and exact byte limit; foreign, unsupported, failed and interrupted states do not reach deletion.
- SQL and ACLs: the contract checks exact identities and sets, rather than trusting broad “allowed” flags alone.
- Secret handling: bootstrap values remain process-local, are never placed in results, and are cleared on success and failure.
- Scope: no IIS, VSS, SQL, Codex, `I:` root, network, model, GPU, FFmpeg, deployment, merge or push action was run.

## Remaining Task 7 boundary

Task 7 must enter the module in-process from `complete-feature.ps1`, claim the real authority after merged-main verification, construct the private typed request with the concrete host-operation bundle, and call `Invoke-NativeGoLive -Request`. It must complete/fail the authority and preserve the existing durable lease/journal executor semantics. No direct script or child-process route is available.

---

## Review round 1 corrective revision

This section supersedes the initial authority, host-callback, SQL-parser, journal and payload claims above. Review found those parts load-bearing and insufficient: the PowerShell request trusted caller-provided objects and callbacks, parsed SQL with the generic builder, sequenced a second non-durable lifecycle, and accepted a caller-reported publish SHA. Those paths have been removed rather than patched.

### Enforced closeout authority

- `NativeGoLiveAuthorityIssuer` is now internal to the trusted assemblies. Normal Web, CLI and other public composition cannot issue or claim authority.
- The new internal `NativeGoLiveCloseoutCapability` has no public constructor or public properties. JSON serialisation produces `{}`; it cannot be reconstructed from its visible values.
- `NativeGoLiveCloseoutCapabilityIssuer` retains the exact capability object and the exact authority-issuer/authority pair. A capability is bound to the canonical plan object, execution ID, normalised merged-main root, SHA-256 payload manifest and the earlier of five minutes or the authority expiry.
- Consumption is atomic and one-use. Expiry fails the underlying authority; execution success completes it; refusal/exception fails it. Replay stops before host lease acquisition.
- `GuardedNativeGoLiveHost.AcquireLeaseAsync` independently requires the capability to have been consumed by the closeout issuer and rechecks the request plan, execution ID, merged-root path and payload hash. Calling the public Task 2 executor directly cannot enter this host.
- The PowerShell module no longer validates a string `Claimed` state or accepts a value-shaped authority. Its private function calls one internal CLR bridge. The bridge uses exact sealed CLR pattern checks for the issuer, opaque capability, typed request and concrete `GuardedNativeGoLiveHost`; it does not accept type-name strings or caller-reported state.

### Concrete typed host and Task 2 executor

- All caller-supplied `HostOperations`, hashtables and script blocks were deleted.
- `GuardedNativeGoLiveHost` implements the Task 2 `INativeGoLiveHost` contract and accepts only internal typed ports with immutable typed observations for preflight, IIS, SQL, ACLs, owned-state destruction, publishing, loopback validation, marketplace registration and VSS.
- The module no longer sequences a parallel lifecycle. Its CLR bridge calls `NativeGoLiveCloseoutExecutor`, which consumes the closeout capability and then calls the existing `NativeGoLiveExecutor`.
- `NativeGoLiveJournalStore` now exposes a stable session that holds the Task 5 mutex and no-follow lock file for the whole Task 2 run. Reads and every compare-and-swap occur through that one session instead of reacquiring between phases.
- `DestroyOwnedStateAsync` now returns the exact updated journal. The host records `AdoptionRecorded` and `RecoveryCreated`, then persists each destructive pending state before its exact action and each durable completed state afterwards: adopted marker, catalogue drop, Outlook spool deletion, SQL-file deletion and canonical marker.
- The executor consumes that returned journal before the next outer transition and rejects a phase, execution, plan or payload mismatch. Retry therefore resumes only a matching durable prefix.
- Pre-VSS cancellation/failure still restores only a pool that was originally running. A restore failure is now converted to the explicit safe result `go-live-pool-recovery-failed`; it no longer escapes as an unclassified exception.

### Canonical SQL bootstrap and proof

- Parsing moved to `Microsoft.Data.SqlClient.SqlConnectionStringBuilder`; the Integrations project now has an explicit centrally pinned `Microsoft.Data.SqlClient` dependency and updated lock files.
- A raw-key scanner rejects aliases, duplicate keys and conflicts before the canonical builder is consulted. The complete key set is required: `Data Source`, `Initial Catalog`, `Integrated Security`, `Encrypt`, `Trust Server Certificate`, `Connect Timeout`, `Connect Retry Count`, `Pooling` and `Application Name`.
- Accepted values are exactly loopback `127.0.0.1`, `master`, integrated authentication, mandatory encryption with loopback certificate trust, five-second connect timeout, zero connect retries, pooling disabled and application name `FluxKnowledge.NativeGoLive`. Credentials, attach files, user instances, failover, multi-subnet, aliases, omitted limits and every extra option are refused.
- The parsed bootstrap object has no public serialisable properties. The process environment is cleared by the SQL phase before publish and again by the closeout executor and module on every exit path.
- Preflight typed evidence proves Full-Text, exact create/drop catalogue scope, exact manageable login scope, no bootstrap server roles, the SQL service identity and its exact two write roots, the current app-pool SID, no sysadmin/server roles/DDL grants and no MDF/LDF access.
- Post-bootstrap evidence re-proves Full-Text, the same bootstrap grant sets and roles, migrations, durable empty marker, empty readiness, app-pool connection, current SID, no roles/DDL/file access, and zero catalogue/index work counts.
- Effective ACL evidence is checked after application as well as after publish/start: SQL service write only on the two SQL roots; app pool read/execute only on `App`, read only on general `Config`, modify only on the six approved writable roots, no SQL/App/general-Config/Recovery write, and data-protection modify only.

### VSS, payload and live contract

- The guarded host independently queries VSS during preflight and requires that observation to equal the typed preflight evidence.
- The COM adapter now returns a typed mutation observation containing the exact pre-mutation GUID/state, action and verified result. `ExactExisting` requires `ChangeDiffAreaMaximumSize`; `SupportedAbsent` requires `AddDiffArea`. A state/action mismatch, GUID drift, cross-volume result or absent maximum stops before owned-state destruction.
- `NativeGoLivePayloadHasher` walks the actual merged-main root without following reparse entries, excludes only `.git`, enforces file-count and total-byte limits, streams every file with write/delete sharing denied, and produces a deterministic SHA-256 manifest.
- The capability and durable journal both bind that manifest. The host re-hashes immediately before publish, re-hashes the source afterwards, hashes the actual destination, and refuses any source change or destination mismatch before pool start.
- Typed loopback evidence checks exact methods, absolute `http://127.0.0.1:5137` URIs and status codes for live, ready, index, GPU, search and MCP. It also checks zero GPU values, exact synthetic query/limit/empty result, the exact nine-tool set, Forwarded and non-loopback HTTP 403, no proxy/redirect, and effective Outlook/Phase 6/model/GPU/FFmpeg/network-parsing exclusions. Marketplace remains the final mutation.

### Corrective RED/GREEN evidence

The new focused test file was first run before implementation and failed compilation with missing capability, guarded-host, typed-port, SQL-bootstrap, payload-manifest and observation types, plus the changed journal-return contract. After the first implementation slice, 19 tests passed. Additional RED/GREEN tightening added exact VSS action binding, direct-executor refusal and pool-recovery failure; the final guarded-host slice contains 21 passing tests.

The pool-recovery test first failed with an unhandled `InvalidOperationException: pool-restore-failed`. After the executor correction it returned the required `go-live-pool-recovery-failed` result and passed.

Fresh corrective verification, with no live host action, included:

```text
dotnet restore FluxKnowledge.slnx --locked-mode
Succeeded

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-restore
Passed: 72, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-restore
Passed: 38, Failed: 0, Skipped: 0

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.

pwsh -NoProfile -File tests/native/phase-5-deployment-safety.ps1 -SourceRoot .
Phase 5 deployment safety contract passed.
```

No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg operation was run. No merge or push was performed.

### Task 7 hand-off after correction

Task 7 is limited to closeout composition: after its reviewed merged-main checks it must construct the internal authority/capability and concrete typed host in-process, then call the module-private CLR bridge. It must not recreate host sequencing, accept serialised authority, forward the bootstrap value to a child process, or substitute public deployment execution. The Task 6 bridge will reject anything other than the exact issuer-owned capability, bound request and `GuardedNativeGoLiveHost`.

---

## Review round 2 corrective revision

This section supersedes the round 1 statement that the typed host ports were only contracts. Review correctly identified that a guarded coordinator with caller-supplied or absent effect implementations was not production composition. Task 6 now owns concrete Windows adapters; Task 7 is still restricted to capability issuance and closeout composition.

### Exact authority, plan and recovery binding

- Canonical plans are registered by their factories in a `ConditionalWeakTable`. Execution validates both that exact object registration and recomputed semantics. A value-equal record clone, including one with the same plan hash, is refused.
- The authority and closeout capability retain the exact plan object. The capability issuer accepts only the exact issuer-owned, claimed authority and exact registered plan; it no longer accepts type names, strings or value-shaped authority.
- `GuardedNativeGoLiveHost` derives its destination solely from `plan.Layout.ApplicationRoot`. It has no destination parameter, so callers cannot redirect publish effects.
- A failed initial execution may reuse only the same opaque capability, authority, plan, execution ID, merged payload path/hash and unexpired issuer. Recovery admission additionally reads the stable durable journal and requires the exact incomplete execution/commit/plan/payload/adoption prefix. Success makes the capability terminal; forged, cloned and successful replay attempts stop before the lease/effect boundary.

### Concrete Windows ports

`NativeGoLiveWindowsHostPorts.CreateProduction` now composes side-effect-free constructors for:

- direct typed `Microsoft.Web.Administration` IIS observation and pool stop/start/restore, with post-action state observation even when the administrative call throws;
- root and every ancestor observation with no-follow resolution plus `GetVolumePathName`/`GetVolumeNameForVolumeMountPoint` GUID evidence, including exact durable recovery-prefix inspection;
- typed SQL preflight, creation/drop, migration, empty-readiness and post-bootstrap evidence through `Microsoft.Data.SqlClient`;
- protected Windows directory ACL application and effective SID/rule observation;
- journal-bound owned-state adoption, marker recovery, exact spool/SQL deletion and atomic marker replacement;
- exact payload publication to the plan application root, preserving its pre-applied root ACL and rejecting reparse entries;
- direct socket-bound HTTP validation with numeric endpoints, proxy disabled, redirects disabled, actual local/remote peer capture and a one-MiB response limit;
- bounded Codex marketplace list/add/list execution plus the existing no-follow manifest writer and structural-hash guard; and
- the existing typed VSS COM port.

The production factory performs no IIS, SQL, ACL, VSS, network, filesystem, Codex or process operation. Effects begin only when the already-consumed guarded host calls a port during the Task 2 executor sequence.

### SQL, ACL and migration evidence

- Preflight requires four exact signed bootstrap procedures for create, drop, fixed app-pool management and fixed app-pool observation. It rejects bootstrap sysadmin/roles, any server permission beyond `CONNECT SQL`, and master database permissions beyond `CONNECT` and `EXECUTE` on those exact procedures.
- The app-pool login may be absent during a fresh bootstrap only when no SID, role, DDL or file-access residue exists. An existing login must have the currently resolved `IIS AppPool\FluxKnowledge` SID and no sysadmin/server-role/DDL/file access.
- Catalogue creation and deletion go only through the fixed signed procedure names and fixed catalogue/login identities. Paths and SID values are parameters, and the host independently binds them to the canonical plan.
- `MergedMainRoot` is the immutable, already-built publish payload produced by Task 7. EF migrations load the exact bounded `FluxKnowledge.Infrastructure.SqlServer.dll` in that payload, compare its SHA-256 with any already loaded assembly of the same identity, construct its `FluxKnowledgeDbContext` from published migration metadata and run migrations in-process. It never treats the payload as a source checkout or invokes `dotnet ef`.
- Post-bootstrap observation checks the exact database ID, owner SID, file IDs/types/paths, all 39 required migration IDs, durable empty marker/readiness, exact login/user SIDs, signed app-pool connect observation, no server roles/DDL grants/file access, and zero knowledge, relation, unconsumed-operation and active-index counts.
- ACL application protects each relevant directory from inherited drift and preserves only SYSTEM/Administrators plus the exact SQL-service or app-pool grant. Effective observations are rechecked after bootstrap, publish and start.

### Payload and live-value proof

- The payload manifest is versioned and unambiguous: fixed magic, big-endian file count/total bytes, then ordered UTF-8 path length/path/file length/content for every exact file. Source-before, source-after and destination must match SHA-256, file count, total bytes and the full ordered path/length list.
- Runtime configuration is read from the payload-root `appsettings.json`. It requires the single canonical retained-ingress root, no source roots, disabled Outlook/OutlookCapture/Worker, and disabled model, GPU, OCR, vision, ASR, FFmpeg and network parsing. OCR/vision/ASR are folded into the prohibited model/media observation rather than silently ignored.
- Loopback observation sends exactly nine bounded requests. Besides methods, URIs, statuses and peer addresses, it validates healthy index values, zero GPU work, the exact empty REST envelope, MCP JSON-RPC IDs/version, initialise protocol/server/capabilities and the exact tools list.
- The Web loopback gate now includes `/health`, so the required Forwarded request to `/health/live` is actually denied with 403. A focused RED test first observed HTTP 200, then passed after the gate correction. The non-loopback probe uses the exact numeric peer and verifies actual socket endpoints.
- Marketplace registration remains after all HTTP, SQL, runtime, ACL and payload validation and is bound to the consumed closeout capability and exact plan Codex identity.

### Round 2 RED/GREEN evidence

The production-adapter test first failed compilation because `NativeGoLiveWindowsHostPorts`, preflight, owned-state, SQL, ACL and marketplace implementations did not exist. The HTTP value tests then failed because unhealthy index and MCP initialise-error bodies were accepted. The health-gate test returned HTTP 200 instead of the required 403 for Forwarded headers. The first published-migration metadata test failed first on the reflected SQL Server overload and then on the hidden generic `Options` property; both reflection contracts were corrected and the test now constructs the exact published context without opening a database.

Fresh round 2 verification, with no live host operation, produced:

```text
dotnet restore FluxKnowledge.slnx --locked-mode
Succeeded after the EF SQL dependency graph was refreshed once with --force-evaluate.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 91, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 38, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~HealthEndpointTests --no-build --no-restore
Passed: 9, Failed: 0, Skipped: 0

Native go-live contract passed.
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

All adapter tests used fakes, disposable filesystem roots or context construction without a database connection. No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg action was run. No merge or push was performed.

### Task 7 hand-off after round 2

Task 7 must stage and build the immutable publish payload first, then hash that exact directory and bind it to the exact plan/authority/capability/request. It may call only `NativeGoLiveWindowsHostPorts.CreateProduction` and the module-private closeout bridge in-process. It must not pass a checkout as `MergedMainRoot`, reconstruct effect ports, override `ApplicationRoot`, substitute a migration project path or duplicate the durable executor sequence.

---

## Review round 3 corrective revision

This revision closes the remaining restart, filesystem-identity and observed-evidence gaps. It supersedes round 2's five-minute, same-process recovery limitation and its path-validated recursive publish/destruction implementation.

### Restart-safe authority and durable failure admission

- Every failure after the first exact journal compare-and-swap, including preflight, pool-stop and VSS failures, now attempts an exact transition to `Incomplete`. Cancellation follows the same durable rule. Pool restoration remains mandatory when a running pool was observed before stop.
- `NativeGoLivePoolStopException` carries the last proved pre-stop running state when IIS changes state but the final IIS observation throws. The executor restores that pool before returning the durable incomplete result; restoration failure remains an explicit safe failure.
- An incomplete execution can be recovered after process restart. The internal recovery issuer acquires the stable journal session, reads the actual durable record, recomputes the complete immutable payload manifest, and issues a new opaque recovery capability only for the exact `Incomplete` execution/plan/commit/payload/adoption prefix. Recovery authority has no five-minute durable-recovery deadline; the exact journal and stable execution lease remain the replay fence.
- A preflight-only failure uses the explicit `PreflightObservedNative` zero-mutation root prefix. It requires the exact durable journal plus a no-reparse, owned native root, and it cannot be used for an adoption prefix with any destructive state.
- A completed, foreign, malformed or manifest-mismatched journal cannot reconstruct recovery authority. A successful recovered execution is terminal, and a later recovery attempt is rejected before mutation.

### Full payload and held-handle filesystem boundary

- The capability, request and durable journal now carry the complete manifest: SHA-256, file count, total bytes, and the exact ordered relative-path/length list. Journal validation rejects invalid hashes, counts, totals, ordering, duplicate/non-canonical paths and bounds violations.
- The guarded host compares the complete manifest at construction, lease admission, preflight, immediately before SQL migration loading, before publish, after publish and at the destination. The published migration runner independently recomputes the complete manifest before resolving or loading `FluxKnowledge.Infrastructure.SqlServer.dll`.
- Owned marker creation/replacement, spool deletion, SQL-file deletion, application-root clearing and payload publication now consume Task 5's held `VerifiedNativeDirectory` handles and expected `NativeFileIdentity` values. Recursive pathname delete/copy and validate-then-mutate sequences were removed.
- Recursive clearing enumerates literal children relative to a held directory handle, rejects every reparse point, recursively holds each child directory, and revalidates the expected identity at the single-child delete boundary. Publication streams each source file from a held no-follow handle into a literal destination child and revalidates the source identity after the mutation interlock.
- Behavioural race tests exchange an owned spool file and a publish source file between observation and mutation. Both adapters preserve the original and replacement identities and return the exact identity-change refusal.

### Observed VSS, SQL and ACL evidence

- VSS mutation is now bound to the complete independent preflight observation, including source/storage volume GUIDs, association state, volume capacity, policy-derived required maximum, exact add/change action and exact verified byte maximum.
- SQL preflight queries the actual four procedure object IDs, ordered parameter signatures, definition SHA-256 values, cryptographic signatures, fixed signing-certificate identity/thumbprints, direct server permissions and master database permissions. The host accepts only the four fixed names, exact bounded signatures, signed definitions, `CONNECT SQL`, master `CONNECT`, and `EXECUTE` on those four procedures.
- Post-bootstrap SQL observation reruns that evidence rather than returning constant procedure/grant claims. It continues to prove exact database ID/owner/files, the complete migration set, app-pool login/user SIDs, absence of server roles/DDL grants/file access, durable empty readiness and zero work counts.
- SQL-service write roots are derived from effective ACL observations across every relevant live root; they are no longer returned as the two expected constants.
- ACL evidence now contains each protected path and every explicit/inherited ACE with SID, rights, allow/deny and child applicability. Validation rejects inherited ACEs, broad or otherwise unrecognised allow identities, unprotected DACLs and summary claims not supported by the observed ACE masks. SYSTEM/Administrators full control, app-pool read/modify boundaries, SQL-service modify-only roots and Recovery denial are recomputed from those ACEs after ACL application, publish and live validation.

### Round 3 RED/GREEN evidence

Focused tests were observed failing before each corresponding implementation change for:

- preflight failure persistence, payload-identity drift during incomplete persistence and pool-stop side-effect restoration;
- new-process durable recovery-capability reconstruction and the preflight-only recovery prefix;
- exact VSS capacity-derived maximum binding;
- payload mutation after owned destruction but before migration loading;
- owned deletion and publication identity swaps at the held-handle mutation boundary;
- inherited/broad ACL rules and ACL summaries unsupported by raw ACEs;
- wrong signed-procedure parameter metadata; and
- payload-manifest drift immediately before migration context loading.

Fresh round 3 verification, with no live host operation, produced:

```text
dotnet restore FluxKnowledge.slnx --locked-mode
All projects are up-to-date for restore.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 104, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~HandleRelativeNativeFileSystemTests --no-build --no-restore
Passed: 10, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 38, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~HealthEndpointTests --no-build --no-restore
Passed: 9, Failed: 0, Skipped: 0

Native go-live contract passed.
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

All tests used typed fakes, disposable filesystem roots or context construction without opening a database. No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg action was run. No merge or push was performed.

### Task 7 hand-off after round 3

Task 7 must bind the complete manifest when issuing the initial capability/request. On restart it must invoke the internal durable recovery issuer rather than recreate an execution ID, authority or journal-shaped object. It then composes the concrete production ports and guarded host for that returned capability and calls only the module-private closeout bridge. `MergedMainRoot` remains the immutable, already-built publish payload; migration project metadata is independent and no checkout copying is permitted.

---

## Review round 4 corrective revision

This revision closes the remaining destructive-prefix, signed-bootstrap authority and privileged migration-load races, plus the carried pool, VSS and ACL evidence findings. It performed no live host action.

### Complete destructive-prefix recovery and pool restoration

- The concrete Windows recovery observer now maps every domain-approved destructive filesystem prefix, including both sides of each pending compare-and-swap. The filesystem-backed matrix covers 17 states/shapes: adopted-marker creation, catalogue drop, spool deletion, SQL-file deletion and canonical-marker replacement.
- `CatalogueDropPending` accepts both catalogue-present and catalogue-absent states, and `CatalogueDropped` accepts catalogue-absent state while the later spool still exists. Spool and SQL observations now distinguish contents inside their owned roots rather than treating the permanent root directories themselves as residual payload.
- When IIS stop does not finish in the exact `Stopped` observation, the guarded host carries the proved pre-stop `WasRunning` state in `NativeGoLivePoolStopException`. The executor therefore restores a pool that was running before the failed stop observation.

### Canonical signed-bootstrap authority

- The trusted host contains one immutable four-entry procedure-definition hash manifest and the fixed signing-certificate name. Preflight requires every exact procedure name and definition hash, one exact parameter signature, a cryptographic signature and one common lower-case 40-hex certificate thumbprint. Duplicate or substituted procedure entries are rejected.
- The clean-slate certificate thumbprint is deliberately not a repository constant. It is accepted only from the four exact manifest-bound signed procedures, then persisted by an exact journal compare-and-swap before pool stop, VSS or any destructive/privileged host call. A failed authority-binding compare-and-swap leaves every privileged mutation untouched.
- The durable store rejects every post-preflight phase or adoption/destructive state without that authority binding. Preflight after restart and the later post-bootstrap observation must reproduce the exact journal-bound manifest, certificate name and fresh thumbprint; an altered definition or re-signed certificate is refused.
- SQL evidence now includes exact permission scopes and grant states, direct server roles, direct master database roles, direct server permissions and direct master permissions. The accepted set is only server-scope `CONNECT SQL`, database-scope `CONNECT` in `master`, and object-scope `EXECUTE` on the four observed procedure object IDs. Database DDL, a database role such as `db_ddladmin`, wrong securable scope, denies, grants with grant option and any additional permission are rejected.

### Held migration image, VSS capacity and exact ACL masks

- The migration assembly is opened read-only without write/delete sharing before the full payload manifest is recomputed. That same held stream is loaded into a dedicated assembly-load context; the runner no longer validates one pathname and later loads or reuses a same-named pathname assembly. The behavioural race test proves replacement is refused for the whole verification-to-load interval and that the already loaded default assembly is not substituted.
- VSS mutation receives the complete preflight observation. It requeries capacity and association immediately before any COM mutation, derives the maximum from the preflight capacity, and returns `None` without mutation on any capacity, association or maximum drift. Public invalid-volume calls still refuse before any COM query.
- ACL validation requires the complete exact ACE set and exact numeric mask per protected path: SYSTEM and Administrators full control, app-pool read/execute on `App`, read on `Config`, modify on the six writable roots, SQL-service modify on Data/Log, and no other ACE. Extra rights on an otherwise allowed SID, duplicate, deny, inherited, non-child or foreign ACEs are rejected.

### Round 4 RED/GREEN evidence

Focused tests were observed failing before each corresponding implementation change for:

- catalogue pending/dropped recovery while the later spool remained, followed by a complete 17-case destructive-prefix filesystem matrix;
- non-`Stopped` final pool observation retaining the pre-stop running state;
- well-formed but untrusted procedure hashes and certificate thumbprints, post-bootstrap certificate re-signing, database-role authority, wrong permission scope and direct database DDL;
- SQL authority journal-binding failure before any privileged host mutation;
- pathname replacement between migration-image verification and load, including same-named default-assembly substitution;
- VSS capacity drift between preflight and mutation; and
- an allowed ACL SID carrying its required mask plus an extra delete right.

Fresh round 4 verification, with no live host operation, produced:

```text
dotnet restore FluxKnowledge.slnx --locked-mode
All projects are up-to-date for restore.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 137, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~HandleRelativeNativeFileSystemTests --no-build --no-restore
Passed: 10, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 38, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~HealthEndpointTests --no-build --no-restore
Passed: 9, Failed: 0, Skipped: 0

Native go-live contract passed.
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

All tests used typed fakes, disposable filesystem roots or context construction without opening a database. No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg action was run. No merge or push was performed.

### Task 7 hand-off after round 4

The separately administered clean-slate SQL bootstrap must provision definitions whose hashes exactly match the committed manifest and must create the fresh signing certificate whose observed thumbprint becomes the first journal binding. Task 7 must not supply, reconstruct or override that thumbprint. It must retain the authority-bound journal and immutable payload rules from round 3; recovery must reproduce the same SQL binding before any further mutation.

---

## Review round 5 corrective revision

This revision closes the final lifecycle-marker, cumulative recovery-prefix, effective SQL-authority, ACL inheritance and bootstrap-source trust findings. It performed no live host action.

### Durable lifecycle marker state

- After the publish manifest and effective ACLs are re-observed, the guarded host reads the exact durable journal and atomically replaces the native owner marker with the journal-bound `Complete` marker before the IIS pool start call.
- The marker replacement reuses the held-handle, temporary-file and atomic replacement path already used by adopted and incomplete markers. A filesystem-backed test proves `Incomplete -> Complete -> Incomplete` transitions and the absence of a residual temporary marker.
- Any caught pool-start, loopback-validation or marketplace-registration failure after the complete write compensates the durable root marker back to `Incomplete` before the journal is compared and swapped to `Incomplete`. Successful completion leaves the root `Complete`.

### Cumulative destructive-prefix absence

- Recovery-prefix inspection now applies cumulative absence rules before shape selection. Once catalogue drop is durable, the catalogue files may never reappear; once spool deletion is durable, spool contents may never reappear; once SQL deletion is durable, no SQL-root payload may reappear.
- The invalid matrix exhausts all 41 residual catalogue/spool/other-SQL combinations for the affected durable states, including combined reappearance at later SQL and canonical-marker prefixes. Every combination fails with `owned-state-journal-prefix-mismatch`; the existing 17 approved pending/durable crash-prefix shapes remain accepted.

### Actual and effective SQL authority

- SQL evidence now carries provenance-bearing findings with the subject, server/database scope, source principal, principal type, authority kind and concrete authority. Preflight and post-bootstrap both fail closed when the result set is absent or non-empty.
- Bootstrap evidence evaluates the actual `sys.login_token` and `sys.user_token`, rejecting effective non-public server/database roles plus group, public or nested-role grants. Risky public DDL/control/impersonation authority is rejected without treating SQL Server's baseline public connectivity as application authority.
- The fixed app-pool observer returns four required result sets and evaluates the app-pool token in server, `master` and `FluxKnowledge` contexts. Group grants, public DDL, nested server/database roles and inherited DDL in `FluxKnowledge` are therefore denied even when no direct grant or direct role row exists.

### Exact ACL semantics and DeleteChild boundaries

- Raw ACL evidence preserves the full inheritance flags, propagation flags, inherited state, access type, rights mask and applicability to the directory itself, child containers and child objects. Validation requires the exact canonical inheritance combination rather than the former collapsed child-applicability Boolean.
- `I:\FluxKnowledge`, `Data`, `Data\Sql`, `Runtime` and `CodexPlugin` are now explicitly created, protected, observed and required to contain only inheritable SYSTEM and Administrators full-control ACEs. The app-pool and SQL-service grants begin only at their intended child roots, so they cannot acquire `DeleteChild` on root or intermediate parents.
- Focused adversarial evidence rejects container-only inheritance, `NoPropagateInherit`, `InheritOnly`, omitted parent observations and an explicit app-pool `DeleteChild` ACE before any irreversible mutation.

### Reviewed bootstrap source and reproducible manifest

- `scripts/deploy/native-go-live-bootstrap.sql` is the canonical reviewed bootstrap DDL. It creates a fresh local signing certificate without exported key material, defines the four identity-bound procedures, signs them and grants the separately supplied bootstrap login only direct `CONNECT SQL`, master `CONNECT` and exact procedure `EXECUTE` authority.
- The source contains no credential, connection string, password or private-key material. Its app-pool procedures are fixed to the canonical catalogue, files, login and SID and expose the required effective-authority result set.
- `scripts/dev/generate-native-go-live-bootstrap-manifest.ps1` normalises line endings, hashes the whole SQL source as UTF-8, hashes each exact procedure definition as UTF-16LE to match SQL Server `nvarchar` definition hashing, and emits the C# trust manifest deterministically.
- `tests/native/native-go-live-bootstrap-manifest.ps1` generates twice, byte-compares both results with the committed generated file, pins the whole-source and four definition hashes to their reviewed values, and rejects secret or connection material. The existing native deployment contract invokes this verifier, so normal CI/closeout detects source, generator or committed-manifest drift.

### Round 5 RED/GREEN evidence

Focused tests were observed failing before each corresponding implementation change for:

- a successful lifecycle that left the durable root marker `Incomplete`, followed by pool-start, validation and marketplace failures that left it `Complete`;
- later catalogue, spool and SQL payload reappearance accepted at destructive recovery prefixes;
- missing provenance-bearing effective SQL authority observations for group, public, nested server role and nested database role paths, including `FluxKnowledge` DDL;
- collapsed ACL inheritance/propagation/applicability evidence and omitted root/intermediate DeleteChild boundaries; and
- the absent canonical bootstrap DDL, generator and committed manifest.

Fresh round 5 verification, with no live host operation, produced:

```text
dotnet restore FluxKnowledge.slnx --locked-mode
All projects restored successfully.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 194, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~HandleRelativeNativeFileSystemTests --no-build --no-restore
Passed: 10, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive --no-build --no-restore
Passed: 38, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~HealthEndpointTests --no-build --no-restore
Passed: 9, Failed: 0, Skipped: 0

Native go-live bootstrap manifest passed.
Native go-live contract passed.
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

All tests used typed fakes, disposable filesystem roots or read-only source generation and comparison. No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg action was run. No merge or push was performed.

### Task 7 hand-off after round 5

Before Task 7 can consume a native go-live bootstrap connection, a separately authorised administrator must review and install the canonical SQLCMD source with the intended bootstrap login. Task 7 must not generate DDL, carry a certificate thumbprint, add SQL roles or broaden grants. It must retain the exact generated manifest binding, effective-authority observation, durable marker compensation and cumulative recovery-prefix rules described above.

---

## Review round 6 final remediation

This revision closes the four remaining go-live breakers without performing any live host action.

### Journal-fenced Complete marker

- The native root `Incomplete -> Complete` replacement now uses two durable adoption states: `CompleteMarkerPending` is compared and swapped before the physical marker write, and `CompleteMarkerDurable` is compared and swapped only after the atomic replacement succeeds.
- Recovery accepts the exact before-action, temporary-file-flushed and after-action physical shapes for the pending state, plus the durable completed state. Marker compensation resolves a pending write to its durable side before fencing and replacing it back to `Incomplete`.
- The filesystem-backed crash injection interrupts the marker replacement after temporary-file validation and proves that the resulting durable `Incomplete` plus temporary `Complete` pair is an accepted, journal-bound recovery prefix.

### Cumulative SQL residual absence

- From `CatalogueDropped` through `CanonicalMarkerDurable`, recovery now requires both SQL Data and Log payload to be absent. The valid matrix no longer constructs an arbitrary LDF as a stand-in for an empty SQL root.
- The cumulative invalid matrix covers catalogue, spool and arbitrary SQL payload reappearance across every affected state, with an explicit arbitrary-LDF regression at `CatalogueDropped`.

### No retained bootstrap target authority

- The signed app-pool management procedure transfers `FluxKnowledge` ownership to the certificate login, removes any target-database bootstrap user, revokes database-level `CONNECT` and `EXECUTE` from `public`, and proves the owner SID.
- Post-bootstrap observation independently reads the owner SID, requires it to equal the signing-certificate login SID, and requires the bootstrap login to have no target-database access.
- Effective bootstrap authority now observes the actual server, `master` and conditional `FluxKnowledge` tokens, including broad database/schema `EXECUTE` inherited through `public`. Preflight and post-bootstrap validation reject any finding.

### Clean-slate bootstrap security provenance

- The canonical SQLCMD source refuses any pre-existing canonical signing certificate, certificate login or signature on the four bootstrap procedures. It then creates and proves the fresh certificate/login before any procedure signature can be admitted.
- The deterministic generator hashes the marked security bootstrap into manifest v2 alongside the complete source and exact procedure definitions, enforces guard/create/proof ordering, and rejects any `DROP SIGNATURE` replacement path.
- Generator mutation tests reject certificate reuse, removal of the security-artifact refusal and replacement of an existing signature. The committed generated C# manifest byte-matches the reviewed DDL.

### Round 6 RED/GREEN evidence

Focused tests were observed failing before implementation for:

- missing `CompleteMarkerPending`/`CompleteMarkerDurable` transitions and both crash sides of marker replacement;
- accepted arbitrary SQL Log payload from `CatalogueDropped` onward;
- the bootstrap login remaining database owner/able to access `FluxKnowledge`, and unobserved public target-database `EXECUTE`; and
- canonical DDL/generator acceptance of reusable certificate, login and signature artefacts.

Fresh round 6 verification produced:

```text
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 warnings, 0 errors.

dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 40, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~NativeGoLive
Passed: 205, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~HandleRelativeNativeFileSystemTests
Passed: 10, Failed: 0, Skipped: 0

dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~HealthEndpointTests
Passed: 9, Failed: 0, Skipped: 0

Native go-live bootstrap manifest passed.
Native go-live contract passed.
Native deployment plan contract passed.
Phase 5 deployment safety contract passed.
```

All tests used typed fakes, disposable filesystem roots or read-only source generation and comparison. No `Invoke-NativeGoLive`, IIS, VSS, SQL, Codex, `I:` root, deployment, network probe, model, GPU or FFmpeg action was run. No merge or push was performed.
