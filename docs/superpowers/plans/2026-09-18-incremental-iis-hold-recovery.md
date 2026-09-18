# Incremental IIS validation-hold recovery implementation plan

**Status:** Complete. The approved incremental deployment succeeded on
2026-09-18 from `9cd35fc07ca3da488957d96d9f347f3d73c53522`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve fail-closed deployment validation while allowing the existing incremental payload swap to roll back after candidate validation fails.

**Architecture:** The existing script owns a release-scoped JSON hold. It creates it once with `CreateNew`, reuses it only if the exact current release owns a safe existing file, and captures the SQL baseline only once. The existing payload-swap module continues to own rollback ordering.

**Tech Stack:** PowerShell 7, existing IIS incremental-update script and native PowerShell contract tests.

**Spec:** `docs/superpowers/specs/2026-09-18-incremental-iis-hold-recovery-design.md`

## Global constraints

- Use only `scripts/deploy/update-native-iis-incremental.ps1`; no full GoLive.
- Keep the canonical `I:\FluxKnowledge\App` root and `http://127.0.0.1:5137` loopback origin.
- Keep migrations and clean-slate operations false and preserve Config, Data, Runtime, Recovery and CodexPlugin.
- Do not add a hook adapter installer; compare the installed adapter checksum before and after Apply.
- A foreign, malformed or reparse-point validation hold must fail closed and must not be modified.

---

### Task 1: Make rollback hold reuse safe and prove it

**Files:**
- Modify: `scripts/deploy/update-native-iis-incremental.ps1:132-170,421-429`
- Modify: `tests/native/incremental-iis-payload-swap.ps1`
- Test: `tests/native/incremental-iis-update-contract.ps1`

**Interfaces:**
- Consumes: `New-DeploymentValidationHold -Path <string> -ReleaseId <string>` and `Invoke-IncrementalApplicationPayloadSwap`.
- Produces: a Boolean return from `New-DeploymentValidationHold`: `$true` when it created the hold and `$false` when the exact same release safely owns the existing hold.

- [x] **Step 1: Add failing composition coverage**

Extract the hold helpers from the deployment script using the existing native-test AST pattern. In the candidate-validation rollback case, invoke the real hold helper in both stop callbacks. Assert two stops, original payload restoration, the first baseline object is retained, and the hold exists until explicit cleanup. Add independent foreign-payload, malformed-payload and reparse-point hold cases that assert failure and unchanged files.

Run: `pwsh -NoProfile -File tests/native/incremental-iis-payload-swap.ps1`

Expected: FAIL because the second real hold creation rejects its own existing file.

- [x] **Step 2: Implement minimal same-release reuse**

Keep the existing `FileMode.CreateNew` creation and durable write for a missing hold. On the expected existing-file `IOException`, reject a reparse point; read its UTF-8 text; return `$false` only when it exactly equals `($ReleaseId | ConvertTo-Json -Compress)`; otherwise throw the existing-owner failure. Do not overwrite, delete, or repair the existing hold.

Change the `StopApplication` callback to set `HoldCreated` only after the first successful creation/reuse and to call `Get-RetainedPipelineStateBaseline` only when `Baseline` is `$null`.

- [x] **Step 3: Run focused verification**

Run:

```powershell
pwsh -NoProfile -File tests/native/incremental-iis-payload-swap.ps1
pwsh -NoProfile -File tests/native/incremental-iis-update-contract.ps1
git diff --check
```

Expected: both contracts pass and the diff has no whitespace errors.

- [x] **Step 4: Commit the focused correction**

```powershell
git add scripts/deploy/update-native-iis-incremental.ps1 tests/native/incremental-iis-payload-swap.ps1 tests/native/incremental-iis-update-contract.ps1 docs/superpowers/specs/2026-09-18-incremental-iis-hold-recovery-design.md docs/superpowers/plans/2026-09-18-incremental-iis-hold-recovery.md
git commit -m "Fix incremental IIS rollback validation hold"
```

## Deployment checklist

1. Run `pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -PlanOnly` and confirm fixed roots, no migrations, no clean slate and preserved state.
2. Confirm clean immutable source and that `I:\FluxKnowledge\CodexPlugin\plugins\fluxknowledge\hooks\invoke-native-hook.ps1` is 1,477 bytes with SHA-256 `DF01EB06D0455148721A24CDB12026BE639C572BBEF7F0B64210A6AF11BECD2E`.
3. Run `-Apply`; require successful JSON, released validation hold, automatic rollback metadata and successful final fixed-loopback probes.
4. Recheck the same adapter checksum, then remove only clean worktrees/branches whose content is already on `main`.
