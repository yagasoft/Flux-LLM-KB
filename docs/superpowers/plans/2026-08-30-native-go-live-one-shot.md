# Native go-live one-shot clean-slate implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development`. Steps use checkbox syntax for tracking.

**Goal:** Replace resumable deployment with a confirmed one-shot clean-slate native go-live while preserving non-deployment native-app behaviour.

**Architecture:** The closeout entry point performs an explicit same-invocation wipe-or-absent admission and then a non-resumable sequence. No deployment state is persisted for a future invocation; uncertainty terminates the invocation and requires a newly confirmed wipe.

**Tech Stack:** .NET 10, PowerShell, SQL Server, xUnit, existing no-follow filesystem/configuration primitives.

**Spec:** `docs/superpowers/specs/2026-08-30-native-go-live-one-shot-design.md`

## Global constraints

- Use `scripts/dev/complete-feature.ps1` as the sole closeout/go-live entry point.
- Keep all actual deployment, merge, push, wipe, non-disposable database, SQL/IIS/VSS/Codex, Outlook and marketplace actions disabled until separately authorised.
- Preserve retained-data protections, Phase 5 behaviour, no-secret disclosure, exact SQL least privilege, fixed hierarchy, disabled Outlook and Phase 6 capabilities.
- Do not use Flux/Flux KB, `dotnet format`, FFmpeg, GPU work, network parsing, source-original rereads, or model activation/download.

---

### Task 1: One-shot admission and deletion contract

**Files:** guarded host/executor/ports and their native go-live domain/integration tests.

- [ ] Write RED tests proving only absent root/database or same-invocation confirmed wipe is admitted; all historic, marker, journal, partial and unknown state fails closed without mutation.
- [ ] Implement one-shot admission and confirmed root/database wipe; remove journal/CAS, root-admission/adoption, marker and replay dependencies from the execution path.
- [ ] Run focused tests GREEN and commit the coherent admission/deletion change.

### Task 2: Remove deployment recovery surface

**Files:** native go-live journal/marker/recovery production code, closeout/module/SQL contracts and deployment-only tests.

- [ ] Write RED source/contract tests asserting absent deployment journal, marker, recovery-prefix, historic-adoption, resume/recover and authority-reconstruction surfaces.
- [ ] Delete deployment-only recovery code and tests; retain ordinary application recovery systems unchanged.
- [ ] Run focused tests GREEN, `git diff --check`, and commit.

### Task 3: One-shot provisioning and pre-IIS validation

**Files:** SQL bootstrap, Windows ports/adapters, configuration serializer/writer, Web composition validation, tests.

- [ ] Write RED tests for exact app SQL authority, no bootstrap lifecycle residue, canonical no-follow configuration byte validation, secret-safe child environment, disabled Phase 6/Outlook composition, and ordering before IIS.
- [ ] Implement the one-shot bootstrap/provision/publish/config/composition sequence and remove re-authorisation/recovery paths.
- [ ] Run focused tests GREEN and commit.

### Task 4: Closeout contract, documentation and verification

**Files:** `complete-feature.ps1`, native contract/dry-run tests, deployment docs, `docs/roadmap.md`.

- [ ] Write RED dry-run tests for one-shot confirmed wipe admission and no recovery machinery.
- [ ] Update closeout contract and relevant deployment documentation to the one-shot model; update roadmap only after verified implementation.
- [ ] Run locked restore, zero-warning Release build, focused Domain/Integration/Web/native checks, full native Release suite, EF no-pending-model verification and `git diff --check`; commit.

### Task 5: Independent whole-slice review

- [ ] Package the full diff from the pre-one-shot merge base.
- [ ] Obtain an independent review focused on absence of resumability, fail-closed admission, retained-data/secret boundaries, exact SQL authority, disabled Phase 6/Outlook and closeout-only activation.
- [ ] Address material findings through the SDD review loop before any live-authorisation request.
