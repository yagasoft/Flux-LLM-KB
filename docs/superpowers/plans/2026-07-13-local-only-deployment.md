# Local-only deployment implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent installation, update, and closeout from downloading packages or images unless an operator explicitly opts in.

**Architecture:** The two production PowerShell scripts share the same policy: image lookup is local-first and package modes remain offline. The closeout wrapper exposes opt-in switches but leaves them off by default. The existing `flux-ollama:local` derivative is the reusable local runtime when its upstream base tag is absent.

**Tech Stack:** PowerShell deployment scripts, Docker, npm, pip, pytest source-contract tests.

## Global Constraints

- Default deployment must not pull an image or fetch a package.
- `flux-ollama:local` and its revision tag/labels remain the local runtime identity.
- Refreshes require explicit `-AllowImagePull` or `-AllowPackageRefresh` flags.
- No live requeue occurs until a local-only deployment and health probes succeed.

---

### Task 1: Specify and test the local-only contract

**Files:**
- Modify: `tests/test_production_deployment.py`
- Modify: `tests/test_complete_feature_script.py`
- Modify: `docs/setup.md`

**Interfaces:**
- Consumes: current deployment scripts and their `NpmOffline`/`PipOffline` controls.
- Produces: failing assertions for explicit image and package-refresh opt-in.

- [x] **Step 1: Write failing production-script assertions**

Add tests that require both installation scripts to expose `AllowImagePull` and
`AllowPackageRefresh`, reject a missing local Docker image without the image
flag, and reuse `flux-ollama:local` before attempting an upstream Ollama base
lookup.

- [x] **Step 2: Write failing closeout assertions**

Require `complete-feature.ps1` to use `npm ci --offline` by default and to
forward explicit refresh switches to `update-flux.ps1` only when selected.

- [x] **Step 3: Run the focused tests and verify red**

Run: `J:\FluxLLMKB\python\python.exe -m pytest tests/test_complete_feature_script.py tests/test_production_deployment.py -q`

Expected: failures because the current scripts still use `--prefer-offline`
and pull a missing Docker image automatically.

### Task 2: Implement strict local-only deployment and discovery

**Files:**
- Modify: `scripts/deploy/install-flux.ps1`
- Modify: `scripts/deploy/update-flux.ps1`
- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `docs/setup.md`

**Interfaces:**
- Consumes: `-AllowImagePull` and `-AllowPackageRefresh` from an operator or
  closeout invocation.
- Produces: local-only default deployment, an explicit refresh path, and local
  Ollama runtime reuse.

- [x] **Step 1: Add explicit switches and guards**

Add `[switch]$AllowImagePull` and `[switch]$AllowPackageRefresh` to both
production script parameter blocks. Reject `NpmOffline:$false` or
`PipOffline:$false` unless package refresh was explicitly allowed.

- [x] **Step 2: Make Docker lookup local-only by default**

Extend `Invoke-FluxDockerImageAvailable` with an `AllowImagePull` argument.
It returns for existing local tags, pulls only with explicit permission, and
otherwise throws an error naming the missing tag and the opt-in switch. Pass
the permission to PostgreSQL, RabbitMQ, and Ollama image resolution.

- [x] **Step 3: Reuse the discovered Ollama runtime**

In `Invoke-FluxOllamaImageBuild`, return early with a clear message when
`flux-ollama:local` exists but `ollama/ollama:latest` is absent and image
pulling is disabled. Preserve the derived image's tags and labels. Only compute
the upstream-base fingerprint or rebuild after a local base exists or explicit
image pulling is allowed.

- [x] **Step 4: Prevent implicit Docker and npm downloads**

Require a local Docker build base when image pulls are disallowed; do not fall
back to a missing `python:3.12-slim`. Change closeout's normal npm command to
`npm ci --offline`. When explicit package refresh is allowed, retain the
existing cache-aware `--prefer-offline` command and forward the package flag to
the deployment script.

- [x] **Step 5: Document the opt-in commands**

Update `docs/setup.md` so local-only is the default and the two refresh flags
are the only documented network-enabled paths.

### Task 3: Verify and resume recovery

**Files:**
- Verify: `tests/test_complete_feature_script.py`
- Verify: `tests/test_production_deployment.py`
- Verify: full test suite and deployment closeout logs

**Interfaces:**
- Consumes: completed local-only script changes.
- Produces: a deployed Office extraction fix, scoped requeue results, and live
  validation evidence.

- [x] **Step 1: Run focused tests and verify green**

Run: `J:\FluxLLMKB\python\python.exe -m pytest tests/test_complete_feature_script.py tests/test_production_deployment.py -q`

Expected: all local-only contract tests pass.

- [ ] **Step 2: Run the full suite through required closeout**

Run `scripts/dev/complete-feature.ps1` from the feature worktree with the
live `J:\FluxLLMKB` install-root environment. Do not pass either refresh flag.

Expected: the deployment reuses `flux-ollama:local`, does not run `docker
pull`, and completes its dashboard and readiness probes.

- [ ] **Step 3: Execute the approved scoped requeue and validate**

After the local-only deployment succeeds, pilot one standalone table-only
DOCX, then requeue standalone DOCX metadata-only assets in controlled batches
and exact parent archive paths. Validate chunk creation and the absence of
`WinError 32` for affected XLSX children.
