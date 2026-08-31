# Native go-live without certificate signing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the failing certificate-signed SQL bootstrap with the approved trusted-local direct-admin bootstrap.

**Architecture:** Retain the existing one-shot closeout, stored-procedure bridge and application-pool identity. Remove certificate authority from SQL, generated manifest and C# evidence; the fixed IIS app-pool Windows login becomes the database owner and a local SQL sysadmin.

**Tech Stack:** .NET 10, Microsoft.Data.SqlClient, PowerShell, SQL Server, xUnit and native PowerShell contracts.

**Spec:** `docs/superpowers/specs/2026-08-31-native-go-live-no-signing-design.md`

## Global Constraints

- Use a dedicated `codex/...` worktree and preserve unrelated changes.
- Do not use Flux tools, deployment commands, push, merge, model/GPU/FFmpeg/network parsing or `dotnet format`.
- Do not add SQL certificates, certificate logins, SQL master keys, credential bridges, recovery machinery or database migrations.
- Retain the one-shot confirmed clean-slate admission and existing fixed paths/procedure names.
- Protect real secrets, credentials, connection strings and private keys.

---

### Task 1: Direct-admin SQL bootstrap and evidence contract

**Files:**

- Modify: `scripts/deploy/native-go-live-bootstrap.sql`
- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `scripts/dev/generate-native-go-live-bootstrap-manifest.ps1`
- Modify: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveSqlBootstrapAuthorityManifest.g.cs`
- Modify: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/GuardedNativeGoLiveHost.cs`
- Modify: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveWindowsHostPorts.cs`
- Modify: `tests/native/complete-feature-bootstrap-nondryrun.ps1`
- Modify: `tests/native/native-go-live-bootstrap-manifest.ps1`
- Modify: the existing native/domain/integration tests that construct or validate bootstrap evidence.

**Interfaces:**

- Consumes: the four existing fixed procedure names and the supplied trusted-local Windows bootstrap connection.
- Produces: a manifest and `NativeGoLiveSqlBootstrapEvidence` that contain only fixed procedure identity/hash facts plus direct app-pool login, SID, sysadmin membership and catalogue-owner evidence.

- [ ] **Step 1: Write failing contracts**

Add assertions that the bootstrap source and generated manifest contain none of
`CERTIFICATE`, `ADD SIGNATURE`, certificate-login constants or signing evidence;
that the source grants `sysadmin` to `IIS AppPool\\FluxKnowledge`; and that
the C# validator accepts app-pool sysadmin/owner evidence while rejecting
missing sysadmin membership or a non-app-pool database owner.

- [ ] **Step 2: Run the focused contracts to verify RED**

Run:

```powershell
pwsh -NoProfile -File tests/native/native-go-live-bootstrap-manifest.ps1 -SourceRoot .
pwsh -NoProfile -File tests/native/complete-feature-bootstrap-nondryrun.ps1 -SourceRoot .
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~NativeGoLive" --logger "console;verbosity=minimal"
```

Expected: certificate/signature assertions fail against the current signed
bootstrap or C# evidence validator.

- [ ] **Step 3: Implement the smallest direct-admin replacement**

Delete certificate SQL, certificate grants, signature loop and certificate
reset cleanup. Add the fixed app-pool login to `sysadmin`, make it the target
catalogue owner, and retain fixed procedure/path/SID checks. Regenerate the
manifest as procedure-only; remove certificate fields and checks from C#
evidence/observation/validation and update existing test builders accordingly.

- [ ] **Step 4: Run focused GREEN verification**

Run the three commands in Step 2 plus:

```powershell
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure --startup-project src/FluxKnowledge.Web --configuration Release --no-build
```

Expected: all pass with zero warnings; EF reports no pending model changes.

- [ ] **Step 5: Commit**

```powershell
git add scripts src tests docs
git commit -m "feat: simplify native SQL bootstrap authority"
```
