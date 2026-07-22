# GPU eviction live repair implementation plan

> **For Codex:** use `superpowers:executing-plans` to carry out this plan task by task.

**Goal:** repair the scheduler persistence and runtime-identity faults found in the live audit, stop metadata-only capture jobs from being reactivated without a verified content change, and deploy with measured live validation.

**Architecture:** keep PostgreSQL as the scheduler's durable coordination cache, but ensure its eviction and residency records carry the runtime identity used by the service-side unload fence. Make all SQL parameter types explicit where PostgreSQL cannot infer them. Treat invalid Paddle allocator readings as unavailable telemetry. Replace the legacy metadata-only requeue loop with a once-per-content-identity migration marker that is only reset by verified content change.

**Tech stack:** Python 3, psycopg/PostgreSQL, pytest, Docker Compose, PowerShell closeout script.

---

### Task 1: Make scheduler persistence type-safe

**Files:**
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/database.py`
- Test: relevant scheduler/database pytest modules

1. Add focused regression tests for the bigint lease linkage, nullable runtime activity update, and non-UUID audit target.
2. Run the focused tests and confirm they fail against the baseline behaviour.
3. Add explicit PostgreSQL-safe casts and store bigint eviction identifiers only in audit details.
4. Rerun the focused tests.

### Task 2: Preserve runtime identity and honest allocator telemetry

**Files:**
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/gpu_runtime.py`
- Test: relevant scheduler/runtime pytest modules

1. Add tests proving eviction candidates inherit the latest persisted runtime generation, activity sequence, and observation ID.
2. Add tests proving negative Paddle readings are reported as unavailable rather than measured VRAM.
3. Run the focused tests and confirm they fail.
4. Implement the smallest changes that preserve the runtime unload fence while allowing a genuinely idle model to be selected.
5. Rerun the focused tests.

### Task 3: Stop metadata-only no-change job reactivation

**Files:**
- Modify: `src/flux_llm_kb/database.py`
- Test: relevant ingestion/database pytest modules

1. Add tests covering a one-time legacy metadata recovery requeue, an unchanged follow-up scan, timestamp-only changes, and a later verified content-hash change.
2. Run the focused tests and confirm the unchanged/timestamp cases fail.
3. Record a content-identity-aware migration marker atomically with the source-asset upsert; clear it only on verified content-hash change.
4. Rerun the focused tests.

### Task 4: Verify, review, document, and deploy

**Files:**
- Modify: `docs/roadmap.md`
- Add or update: focused test modules only as needed

1. Run focused tests, then the relevant broader suite/lint/type checks defined by the repository.
2. Inspect the full diff for scope, SQL safety, concurrency, and rollback risks.
3. Update affected roadmap progress and remaining work.
4. Use `scripts/dev/complete-feature.ps1` to commit, merge, push, deploy, and probe; do not substitute manual closeout steps.
5. Perform immediate production checks and a 30-minute post-deployment health/scheduler/repeat-work monitor. If any new regression appears, repeat this plan with a focused repair and fresh deployment.
