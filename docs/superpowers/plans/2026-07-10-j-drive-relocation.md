# I-drive Docker data and J-drive Flux runtime relocation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Safely relocate active Docker WSL data to Docker Desktop's managed \`I:\Docker\data\wsl\DockerDesktopWSL\` leaf (selected through the UI as \`I:\Docker\data\wsl\`) and the Flux runtime to \`J:\FluxLLMKB\`, without moving Docker Desktop binaries or deleting user data.

**Architecture:** Docker data remains a Docker Desktop-managed move, preceded by trim and detached VHDX compaction. A new PowerShell migration command performs a guarded, explicit Flux cutover: it validates the deployed revision, preserves named volumes, updates host path bindings and the active Codex MCP configuration, and leaves the source root intact until a separate removal action.

**Cache handling:** Copy Flux's local \`package-cache\wheelhouse\` to J: with the runtime, then materialise the required host-compatible wheels from pip's existing local HTTP cache into that copied wheelhouse. Rebuild \`flux-llm-kb-wheelhouse:local\` from the local cache with \`--pull=false\` before the target Compose project starts. Dockerfile dependency installation remains offline (\`--no-index\`) and reuses its BuildKit wheel cache.

**Recovery handling:** If a failed copy leaves a partial J: runtime, retry only with explicit \`-ResumePartialDestination\`; the command verifies that the partial runtime is the same Flux revision, disables source task triggers after stopping them, quiesces residual source-root Flux processes immediately before copying, and resumes Robocopy without deleting the partial target or source root.

**Tech Stack:** PowerShell, Docker Desktop/Compose, Windows Task Scheduler, robocopy, Python pytest.

## Global Constraints

- Docker Desktop binaries, \`com.docker.service\`, and the Docker PATH entry remain under \`D:\Docker\`.
- Never move the Docker VHDX manually; use Docker Desktop Settings to change the disk-image location.
- No Docker cache prune, no \`docker volume rm\`, no Compose \`--volumes\`, and no source-root deletion in the migration command.
- Flux defaults change from \`D:\FluxLLMKB\` to \`J:\FluxLLMKB\`; historical \`.agents\` logs and \`.worktrees\` snapshots remain unchanged.
- Live Docker/Flux shutdown and restart require a separate explicit user confirmation immediately before execution.

---

### Task 1: Add a guarded Flux install-root relocation command

**Files:**
- Create: \`scripts/deploy/migrate-flux-install-root.ps1\`
- Create: \`tests/test_flux_install_root_migration.py\`

**Interfaces:**
- Consumes: a stopped Flux runtime at \`-SourceRoot\`, matching \`app/VERSION\` and \`git -C -SourceCodeRoot rev-parse --short HEAD\`.
- Produces: an optional JSON preflight report by default; a J-drive Flux runtime only when \`-Apply\` is supplied.
- Parameters: \`-SourceRoot "D:\FluxLLMKB"\`, \`-DestinationRoot "J:\FluxLLMKB"\`, \`-SourceCodeRoot <repository root>\`, \`-Apply\`, and \`-Json\`.

- [x] **Step 1: Write failing tests**

Create tests that assert the command exists, defaults to D: source/J: destination, has an explicit \`-Apply\` gate, validates roots and matching revisions, updates user and machine PATH entries without touching Docker entries, recreates scheduled tasks and all Compose services, and prohibits source removal, volume deletion, cache pruning, and Compose volume removal.

- [x] **Step 2: Run the new tests**

Run: \`D:\FluxLLMKB\python\python.exe -m pytest tests/test_flux_install_root_migration.py -q\`

Expected: FAIL because the migration script does not exist.

- [x] **Step 3: Implement the minimal safe command**

Implement preflight-only behaviour unless \`-Apply\` is set. In apply mode: stop the named Flux tasks; run \`docker compose down\` from the source app without volume flags; copy to a new destination with robocopy; repair root-bearing text configuration and generated launchers; rebuild target Python launchers/venv from the verified source revision and local wheelhouse; replace only Flux segments in user/machine PATH; re-register the two existing tasks with J: actions; bring the complete target Compose project up with \`--no-build\`; and emit a redacted JSON report. Require source retention and never remove it.

- [x] **Step 4: Re-run the new tests**

Run: \`D:\FluxLLMKB\python\python.exe -m pytest tests/test_flux_install_root_migration.py -q\`

Expected: PASS.

### Task 2: Change maintained defaults and documentation to J:

**Files:**
- Modify: \`scripts/deploy/install-flux.ps1\`, \`scripts/deploy/update-flux.ps1\`, \`scripts/deploy/start-flux.ps1\`, \`scripts/deploy/stop-flux.ps1\`, \`scripts/deploy/status-flux.ps1\`, and \`scripts/deploy/migrate-postgres-to-docker-volume.ps1\`
- Modify: \`scripts/dev/complete-feature.ps1\`
- Modify: \`docs/setup.md\`, \`docs/architecture.md\`, \`docs/integrations.md\`, and \`docs/roadmap.md\`
- Modify: the affected deployment, health, CLI, mail, dashboard, and feature-closeout tests.

- [x] **Step 1: Write failing default-path assertions**

Change the existing test expectations so maintained runtime defaults and documentation require \`J:\FluxLLMKB\`, while no maintained source files retain \`D:\FluxLLMKB\`.

- [x] **Step 2: Run the affected tests**

Run: \`D:\FluxLLMKB\python\python.exe -m pytest tests/test_production_deployment.py tests/test_complete_feature_script.py tests/test_health_dashboard.py tests/test_rest_api_crawl.py -q\`

Expected: FAIL because code and documents still use D:.

- [x] **Step 3: Update the maintained references**

Replace only active defaults, cache paths, prose, and fixtures with J:. Preserve Docker's D: executable paths and historical logs/worktrees. Update the roadmap progress/remaining-work text for this migration work.

- [x] **Step 4: Re-run the affected tests**

Run: \`D:\FluxLLMKB\python\python.exe -m pytest tests/test_production_deployment.py tests/test_complete_feature_script.py tests/test_health_dashboard.py tests/test_rest_api_crawl.py -q\`

Expected: PASS.

### Task 3: Verify code and stage the live migration runbook

**Files:**
- Modify: none unless verification exposes a defect.

- [x] **Step 1: Run static safety scans**

Run the migration command without \`-Apply\` against the current paths and search maintained source for \`D:\FluxLLMKB\`. Confirm no source deletion or volume/cache-prune command is present.

- [x] **Step 2: Run focused regression tests**

Run: \`D:\FluxLLMKB\python\python.exe -m pytest tests/test_flux_install_root_migration.py tests/test_production_deployment.py tests/test_complete_feature_script.py tests/test_health_dashboard.py tests/test_mail_ingestion.py tests/test_rest_api_crawl.py -q\`

Expected: PASS with no warnings.

- [x] **Step 3: Execute the approved live sequence only after explicit confirmation**

Record Docker baseline; run \`fstrim -av\`; stop Docker Desktop and WSL; compact \`D:\Docker\data\wsl\disk\docker_data.vhdx\` with elevated DiskPart; restart Docker Desktop; select \`I:\Docker\data\wsl\` through Docker Desktop Settings so it moves the disk to its managed \`I:\Docker\data\wsl\DockerDesktopWSL\` leaf; verify Docker counts; then invoke the Flux migration command with \`-Apply\`. Verify all 21 Compose services, task actions, J: bind mounts, dashboard health, and MCP readiness before considering source cleanup.

Completed 2026-07-11: the D-drive VHDX was trimmed and compacted before Docker Desktop moved it through Settings to the I-drive managed leaf. Flux now runs from J with all 21 services, J bind mounts, J task actions and PATH entries, and J Codex MCP configuration; the D source root remains retained.
