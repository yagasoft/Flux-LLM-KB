# Resume staged Phase 5 closeout implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` for the implementation and an independent review before use.

**Goal:** Safely resume a Phase 5 closeout that stopped after a verified squash merge was staged on `main`, without accepting arbitrary staged content or skipping any standard gate.

**Architecture:** Keep the normal `complete-feature.ps1` route fail-closed on a dirty `main` worktree. Add an explicit, fully authenticated resume mode that validates both worktrees and commits, refreshes only the binary delta from the verified staged feature tree to the reviewed newer feature tree, then reruns every feature and main verification gate before the existing commit/push/deployment tail. The helper must not use reset, restore, checkout, three-way apply, reject files, or `git add -A`.

**Tech stack:** PowerShell 5.1-compatible scripts, Git tree/index checks, existing native PowerShell contract tests.

**Spec:** [native closeout and loopback deployment specification](../specs/2026-08-03-native-closeout-and-loopback-deployment.md)

## Global constraints

- The normal closeout route continues to reject any dirty `main` worktree.
- Resume is opt-in only through `-ResumeStagedSquash` and all expected SHA/branch inputs are mandatory canonical full SHA-1 values.
- Outlook remains permanently disabled; explicit false must fail in both normal and resume paths.
- No resume route may deploy, push, migrate, start Outlook, or change a production database before all existing verification gates have passed.
- Only a feature-tree delta authenticated by exact Git tree equality may update the staged `main` index.
- Do not expose or interpolate user-supplied Git/Site values into executable command strings.
- Resume-path Git must not inherit ambient `GIT_*`, external-diff, replacement, graft, alternate-object, namespace, shallow, or configuration state.
- The pre-redesign helper commits (`af975ad`, `ea73b0e`, `586be88`) are not approved for use; they remain historical implementation evidence only.

---

### Task 1: Implement authenticated staged-squash refresh

**Files:**

- Create: `scripts/dev/refresh-staged-squash.ps1`
- Modify: `scripts/dev/complete-feature.ps1`
- Test: `tests/native/complete-feature-dryrun.ps1`

**Interfaces:**

- Consumes the main worktree, feature worktree, expected main head, expected old staged feature head, expected current feature head, and expected feature branch.
- Produces an unchanged main `HEAD`, no unstaged/untracked main files, and an index/worktree whose tree is exactly the current feature head.

- [ ] **Step 1: Write failing native contracts**

  Add disposable temporary-repository tests which prove that ordinary closeout still rejects dirty `main`, while resume rejects missing/partial parameters, malformed or non-canonical SHAs, mismatched feature branch, dirty/untracked/unstaged main content, dirty feature content, divergent/rebased history, remote advancement, unmerged state, arbitrary staged content, and a staged tree that does not exactly equal the expected old feature head. Add the happy-path test with additions, modifications, deletions, and a binary change.

- [ ] **Step 2: Run the contracts and capture RED**

  Run `pwsh -NoProfile -ExecutionPolicy Bypass -File .\tests\native\complete-feature-dryrun.ps1` and confirm the new resume tests fail because the helper/mode does not exist or does not enforce the required checks.

- [ ] **Step 3: Implement the minimal helper**

  Implement `refresh-staged-squash.ps1` to validate same Git common directory, registered worktrees, `main` tracking `origin/main`, exact feature branch/head, clean worktrees, absence of in-progress Git operations and special index flags, ancestry, fetch-verified remote main, and exact old index tree. Create a binary patch with Git `--output`, run `git apply --check --index`, then `git apply --index`; delete the temporary patch in `finally`; recheck all expected tree/working-tree conditions.

- [ ] **Step 4: Integrate explicit resume sequencing**

  Add `-ResumeStagedSquash`, `-ExpectedMainHead`, `-ExpectedStagedFeatureHead`, `-ExpectedFeatureHead`, and `-ExpectedFeatureBranch` to `complete-feature.ps1`. In resume mode, run every feature-side gate, authenticated refresh, every main restore/build/test/Gmail gate, a new pre-commit tree/parent authentication, then the unchanged commit/push/deployment/validation sequence. Do not silently select resume or bypass a normal closeout step.

- [ ] **Step 5: Run GREEN verification and commit**

  Run the native dry-run contract, adjacent deployment/Outlook safety contracts, PowerShell parser checks, and diff check. Commit only the helper, closeout script, and focused native contracts.

### Task 2: Independently review recovery safety

**Files:**

- Review: Task 1 exact diff package

- [ ] **Step 1: Inspect the exact refresh and resume diff**

  Verify that no unknown staged main content can reach `main`, that correction delta application is binary-safe and authenticated, that all original gates rerun, and that Outlook-disabled enforcement applies to resume.

- [ ] **Step 2: Exercise focused adversarial contracts as useful**

  Verify happy-path tree equality and mutation-free rejection tests. Confirm the normal route remains dirty-main fail-closed.

- [ ] **Step 3: Record approval before resuming closeout**

  Do not use resume mode until the independent reviewer returns an approve verdict.

## Architecture revision: authenticated Git boundary

The first three implementations were rejected because individual Git commands continued to inherit ambient Git worktree, index, object, replacement, and configuration state. This revision supersedes the `git apply` implementation in Task 1. Normal closeout remains unchanged and dirty-main fail-closed; only the explicit resume route adopts the boundary below.

### Task 3: Build the authenticated resume Git boundary

**Files:**

- Create: `scripts/dev/ResumeGitBoundary.psm1` or a focused equivalent private module
- Modify: `scripts/dev/refresh-staged-squash.ps1`
- Modify: `scripts/dev/complete-feature.ps1`
- Test: `tests/native/complete-feature-dryrun.ps1`

**Interfaces:**

- Consumes authenticated main/feature repository identities, expected origin URL, exact expected refs, and an explicit command argument array.
- Produces a `ProcessStartInfo`-launched Git result with exit code/stdout/stderr, using the pinned worktree/index/common/object directories only.

- [ ] **Step 1: Write boundary RED contracts**

  In disposable repositories, set each inherited `GIT_*` redirection channel (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_COMMON_DIR`, object/alternate object, namespace, shallow, replacement, and configuration channels) to a conflicting tree/index/object store. Add malicious repository configuration cases for aliases, includes, external diff/textconv, submodule ignore, filters, hooks, fsmonitor, URL rewrite, upload-pack, signing, credential and maintenance settings. Assert every resume command rejects before the staged main tree, refs, object database or working tree changes.

- [ ] **Step 2: Run the boundary RED contracts**

  Run the Windows PowerShell 5.1 native contract and confirm the current raw Git resume route can be redirected or accepts forbidden configuration.

- [ ] **Step 3: Implement one exclusive Git process boundary**

  Resolve a full `git.exe` path; execute only with `ProcessStartInfo`, `UseShellExecute = false`, redirected streams, an argument-list encoder compatible with Windows PowerShell 5.1, and stdin for commit messages. Bootstrap by clearing every inherited `GIT_*` environment variable, then authenticate canonical `--show-toplevel`, `--absolute-git-dir`, `--git-common-dir`, `--git-path index`, non-bare/non-shallow state, SHA-1 object format and expected origin URL. For each later command, explicitly set the authenticated Git directory, worktree, index, common directory and object directory, disable replacement/lazy fetch/optional index refresh, and clear alternate-object/namespace/shallow/external-diff/config channels.

- [ ] **Step 4: Lock and validate repository configuration**

  Use private empty system/global config files. Lock the authenticated common configuration file with `FileShare.Read`; reject includes/worktree configuration and all non-allowlisted local config keys. The exact allowlist must include only the minimal immutable repository/worktree data required by the authenticated commands; reject aliases, diff/status/submodule/filter/hook/fsmonitor/signing/credential/remote-uploadpack/maintenance and URL-rewrite settings.

- [ ] **Step 5: Run boundary GREEN verification**

  Run all hostile-environment and hostile-configuration contracts under Windows PowerShell 5.1 and PowerShell 7. Confirm none executes a marker or changes any protected repository state.

### Task 4: Rebuild resume refresh and closeout sequencing on the boundary

**Progress note (2026-08-21):** The disposable lifecycle contract captured the
required RED state for both the raw patch refresh and the raw closeout
commit/push route. The completed boundary-only conversion covers binary
add/modify/delete/no-op refreshes, byte-for-byte dry-run preservation,
hostile Git environment/configuration, index/worktree substitution, unmerged
indexes, remote advancement, explicit no-op validation records, and Outlook
false rejection. It also exposed a boundary index-path canonicalisation defect;
resolving `--git-path index` relative to the authenticated worktree fixed it
without relaxing configuration, identity or cleanliness policy. Windows
PowerShell 5.1 and PowerShell 7 disposable contracts are GREEN; an independent
Task 5 review remains mandatory before any real resume invocation.

**Reviewer follow-up (2026-08-21):** The first Task 5 correction round closes
the dry-run main-root log write, executable branch interpolation, late-clean
feature cleanup, and resolved-operation marker gaps. The expanded disposable
contract re-verifies those cases under both supported PowerShell engines.

**Reviewer follow-up (2026-08-21, second correction):** Resume Gmail child
arguments use native argument arrays so the optional confirmation is either a
bare switch or absent; expected branch text remains process-environment data.
Every authenticated resume write lifecycle rechecks all operation markers on
both worktrees immediately before mutation, including commit/CAS,
validation-record writes/CAS, lease pushes and feature cleanup. A bounded
disposable exact-child contract exercises commit-tree, ref CAS, expected-old
lease pushes, validation-record writes and the no-op remote recheck.

**Reviewer follow-up (2026-08-23, fifth correction):** A marker check in the
`Invoke-ResumeGit` caller is still before process construction. Every resume
write passes its main boundary and, where applicable, feature peer into the
authenticated process launcher; that launcher checks the markers after it has
prepared `ProcessStartInfo` and immediately before `Process.Start`. The native
disposable contract sets a breakpoint on this internal launch-fence line,
injects `MERGE_HEAD`, and requires refresh rejection with an unchanged main
index.

**Files:**

- Modify: `scripts/dev/refresh-staged-squash.ps1`
- Modify: `scripts/dev/complete-feature.ps1`
- Test: `tests/native/complete-feature-dryrun.ps1`

**Interfaces:**

- Consumes the Task 3 authenticated Git boundary.
- Produces a verified new index/worktree tree, a compare-and-swap main commit, a lease-protected push, and no mutation in dry-run mode.

- [x] **Step 1: Write lifecycle RED contracts**

  Add disposable-repository tests for a binary add/modify/delete/no-op refresh; remote advancement between identity check and push; failure injection before and after preview refresh, real refresh, commit object creation, ref update and push; dry-run byte-for-byte repository preservation; special indexes, swapped/nested worktrees and alternate matching indexes. Require Outlook false rejection in normal and resume modes.

- [x] **Step 2: Run lifecycle RED verification**

  Run the focused native contract and confirm the old patch/ordinary-commit route cannot meet the new lifecycle/provenance assertions.

- [x] **Step 3: Implement authenticated refresh and commit**

  Replace generated patches with boundary-launched `read-tree -n -m -u <old-tree> <new-tree>` preview followed by one `read-tree -m -u <old-tree> <new-tree>` mutation. Recheck exact index equality with `diff-index --cached --quiet <new-tree>` and strict worktree cleanliness. Create the main commit with `commit-tree <new-tree> -p <expected-main>` using explicit fixed author/committer identity and stdin message, then compare-and-swap `update-ref refs/heads/main <new-commit> <expected-main>`. Use `ls-remote --refs <ExpectedOriginUrl> refs/heads/main` for both remote checks and a force-with-lease-equivalent expected-old ref push. Never use ordinary `git commit`, `git push`, `git merge`, `git add`, `git apply`, reset, restore or checkout on the resume path.

- [x] **Step 4: Integrate complete-feature resume gates**

  Ensure resume reruns every feature-side gate, boundary bootstrap/authentication, preview, single refresh, every main gate, authenticated commit/ref update, lease push and remote verification before the unchanged deployment/live-validation tail. Validation-record commits/pushes and cleanup must also use the boundary. Dry-run runs only authentication, remote read and preview; all mutators are explicit skipped steps.

- [x] **Step 5: Run GREEN verification and commit**

  Run all native resume/deployment/Outlook/Phase 5 contracts under Windows PowerShell 5.1 and PowerShell 7, parser checks, focused generated-disposable SQL where used by a contract, and diff checks. Commit only the plan, boundary, resume scripts and focused tests.

### Task 5: Independently review the redesigned recovery path

**Files:**

- Review: Task 3–4 exact diff package

- [ ] **Step 1: Security review**

  Verify every resume-path Git operation crosses the authenticated boundary and no ambient Git/config path remains.

- [ ] **Step 2: Mutation-order review**

  Verify dry-run preservation, exact tree/index transitions, ref CAS, lease push, remote race rejection, and no-Outlook enforcement.

- [ ] **Step 3: Approve before real invocation**

  Do not invoke `-ResumeStagedSquash` against the preserved main state before an independent approve verdict.

## Acceptance criteria

- A failed staged squash can be resumed only with explicit expected identities and exact Git content provenance.
- The resume path refreshes only the feature delta and reruns all gates before commit/push/deploy.
- Any ambiguous or unrelated main/feature state fails before mutation.
- Existing no-Outlook, no-proxy/no-redirect, migration, backup, Gmail, and live-validation safeguards remain enforced.
