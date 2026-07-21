# GPU runtime reconciliation and Vespa fallback implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make runtime inventory and measured request behaviour authoritative for GPU admission and eviction, give interactive retrieval safe-boundary priority over background work, expire stale brokered evictions, unload confirmed-idle models, and keep Vespa lexical retrieval available when Snowflake query embedding is temporarily unavailable.

**Architecture:** Add a service-local runtime tracker and priority operation queue to model-runner/Paddle/ASR; add a PostgreSQL-coordinated reconciliation layer which reads those endpoints, Ollama and one designated driver source under a session advisory lock; persist observations, request-shape calibration, waiting admissions and fenced evictions; then make the existing scheduler and GPU-eviction worker consume that runtime truth. The retrieval slice is independently shippable and changes a retryable embedding failure into a BM25-only Vespa request, while PostgreSQL remains only the Vespa-failure diagnostic fallback.

**Tech stack:** Python 3.11+, FastAPI, psycopg/PostgreSQL, RabbitMQ quorum/retry queues, PyTorch, PaddlePaddle, CTranslate2/faster-whisper, Ollama HTTP API, Vespa YQL/BM25, pytest.

## Global constraints

- Work only in `E:\LLM KB\.worktrees\gpu-runtime-reconciliation` on `codex/gpu-runtime-reconciliation`.
- Do not deploy, recreate/restart a service, alter the live GPU budget, run a live GPU probe, or run `scripts/dev/complete-feature.ps1` without a separate approval checkpoint.
- Never cancel a running CUDA call. Priority may revoke or detach a waiting admission, but a granted lease plus runtime `in_flight > 0` completes normally.
- Treat PostgreSQL as the production coordination authority. `InProcessGpuScheduler` may use an injected inventory provider for deterministic tests/development only.
- Hold the GPU-control PostgreSQL advisory lock on a dedicated session, not in a long-running transaction. Do not hold row locks while making HTTP calls.
- Keep the runtime residency endpoint independent of the scheduler and database. It reports only process-local truth.
- Keep telemetry bounded and content-free: numeric shape buckets, model/task identifiers, opaque request/observation IDs and sanitised error classes only; never persist query/document/audio/image content.
- Runtime enforcement, priority drain, retry coalescing, expiry and idle unload begin behind independent reloadable flags. Lexical Vespa degradation is an independent slice.
- A blocked **interactive** queue head establishes the drain barrier whether its task is exclusive or shareable. This is necessary because Snowflake query embeddings are normally shareable; restricting the barrier to exclusive requests would violate the approved external-query priority.
- Preserve every eviction row for audit. State transitions are compare-and-set; no late delivery may rewrite terminal history.
- Use the existing `gpu-eviction-worker`; do not add a Compose service.

## File and responsibility map

| File | Responsibility |
| --- | --- |
| `src/flux_llm_kb/gpu_runtime.py` (new) | Process generation, priority operation tickets, in-flight/activity tracking, allocator probes and service-local unload fencing. |
| `src/flux_llm_kb/gpu_reconciliation.py` (new) | Runtime/Ollama adapters, concurrent inventory reads, driver accounting, capacity-state calculation and observation models. |
| `src/flux_llm_kb/gpu_scheduler.py` | Stable admission lifecycle, priority/drain policy, request-shaped reservations, reconciliation orchestration and verified eviction. |
| `src/flux_llm_kb/database.py` | Observation/calibration persistence, admission reattachment, fenced eviction lifecycle, expiry and idle-candidate queries. |
| `src/flux_llm_kb/model_runner.py` | Model-runner/Paddle inventory and unload endpoints, operation tracking, request-class propagation and allocator samples. |
| `src/flux_llm_kb/asr_server.py` | ASR inventory/unload contract and a transcribe/load/unload operation gate. |
| `src/flux_llm_kb/model_activity.py`, `src/flux_llm_kb/cli.py` | Trusted caller context, interactive CLI scope and scheduler reconciliation summary. |
| `src/flux_llm_kb/embeddings.py`, `src/flux_llm_kb/extractors.py`, `src/flux_llm_kb/worker.py` | Propagate request identity/class; split explicitly unschedulable background embedding batches; surface deferred capacity. |
| `src/flux_llm_kb/search_index.py`, `src/flux_llm_kb/database.py`, `src/flux_llm_kb/service.py` | Optional-vector Vespa query, retryable embedding classification, lexical diagnostics and Vespa-only fallback boundary. |
| `src/flux_llm_kb/event_worker.py` | Leader-elected idle/stale maintenance task alongside GPU eviction consumption. |
| `src/flux_llm_kb/settings_registry.py`, `src/flux_llm_kb/sql/0045_gpu_runtime_reconciliation.sql` | Reloadable controls and additive persistence. |
| Focused tests under `tests/` | Contract, state-machine, race, degradation and acceptance evidence. |

---

### Task 1: Add the additive schema and reloadable rollout controls

**Files:**

- Create: `src/flux_llm_kb/sql/0045_gpu_runtime_reconciliation.sql`
- Modify: `src/flux_llm_kb/settings_registry.py`
- Modify: `tests/test_migrations.py`
- Modify: `tests/test_settings.py`

**Interfaces:**

- Consumes existing `gpu_leases`, `gpu_model_residency`, `gpu_evictions` and `SettingDefinition` contracts.
- Produces the columns/tables named in the approved specification and the exact settings listed below.
- Does not yet alter runtime behaviour.

- [ ] Add a failing migration contract test which asserts the migration is named `0045_gpu_runtime_reconciliation`, adds `expired` to the eviction status check, adds every fencing/admission column, and creates both new tables:

```python
def test_gpu_runtime_reconciliation_migration_is_additive():
    migration = next(item for item in load_migrations() if item.name == "0045_gpu_runtime_reconciliation")
    sql = migration.sql
    for fragment in (
        "CREATE TABLE IF NOT EXISTS gpu_runtime_inventory",
        "CREATE TABLE IF NOT EXISTS gpu_model_vram_calibration",
        "ADD COLUMN IF NOT EXISTS admission_key",
        "ADD COLUMN IF NOT EXISTS priority_class",
        "ADD COLUMN IF NOT EXISTS wait_reason",
        "ADD COLUMN IF NOT EXISTS runtime_generation",
        "ADD COLUMN IF NOT EXISTS runtime_activity_sequence",
        "ADD COLUMN IF NOT EXISTS claim_token",
        "ADD COLUMN IF NOT EXISTS row_version",
        "ADD COLUMN IF NOT EXISTS heartbeat_at",
        "ADD COLUMN IF NOT EXISTS retry_not_before",
        "ADD COLUMN IF NOT EXISTS expires_at",
        "'expired'",
    ):
        assert fragment in sql
```

- [ ] Run `python -m pytest tests/test_migrations.py -q`; expect the new test to fail because migration `0045` is absent.

- [ ] Add the migration. Use these columns and indexes; retain the existing `(model_id, task_type)` residency key for rollback compatibility and record duplicate-owner conflicts in the observation payload/state instead of changing that key:

```sql
ALTER TABLE gpu_model_residency
    ADD COLUMN IF NOT EXISTS runtime_state text NOT NULL DEFAULT 'unknown',
    ADD COLUMN IF NOT EXISTS owner_component text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_generation text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_fingerprint text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_activity_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS runtime_in_flight integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_operation_started_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_operation_completed_at timestamptz,
    ADD COLUMN IF NOT EXISTS runtime_observed_at timestamptz,
    ADD COLUMN IF NOT EXISTS runtime_failure_reason text NOT NULL DEFAULT '';

ALTER TABLE gpu_leases
    ADD COLUMN IF NOT EXISTS admission_key text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS priority_class text NOT NULL DEFAULT 'background',
    ADD COLUMN IF NOT EXISTS wait_reason text NOT NULL DEFAULT 'queue_wait',
    ADD COLUMN IF NOT EXISTS linked_eviction_id bigint REFERENCES gpu_evictions(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS shape_bucket text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reserved_peak_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS load_delta_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS working_set_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS reconciliation_observation_id text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS caller_attached boolean NOT NULL DEFAULT true;

CREATE UNIQUE INDEX IF NOT EXISTS idx_gpu_leases_active_admission_key
    ON gpu_leases (admission_key)
    WHERE admission_key <> '' AND status IN ('waiting', 'running');

ALTER TABLE gpu_evictions DROP CONSTRAINT IF EXISTS gpu_evictions_status_check;
ALTER TABLE gpu_evictions ADD CONSTRAINT gpu_evictions_status_check
    CHECK (status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'skipped', 'expired'));
ALTER TABLE gpu_evictions
    ADD COLUMN IF NOT EXISTS runtime_generation text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_activity_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS claim_token text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS heartbeat_at timestamptz,
    ADD COLUMN IF NOT EXISTS retry_not_before timestamptz,
    ADD COLUMN IF NOT EXISTS expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS request_reason text NOT NULL DEFAULT 'demand',
    ADD COLUMN IF NOT EXISTS terminal_reason text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reconciliation_observation_id text NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS gpu_runtime_inventory (
    id bigserial PRIMARY KEY,
    observation_id text NOT NULL,
    component text NOT NULL,
    owner_component text NOT NULL DEFAULT '',
    process_generation text NOT NULL DEFAULT '',
    runtime_fingerprint text NOT NULL DEFAULT '',
    state text NOT NULL,
    allocator_capability text NOT NULL DEFAULT 'unknown',
    driver_used_mb integer,
    driver_free_mb integer,
    known_measured_mb integer NOT NULL DEFAULT 0,
    known_reported_mb integer NOT NULL DEFAULT 0,
    context_allowance_mb integer NOT NULL DEFAULT 0,
    unresolved_known_owner_mb integer NOT NULL DEFAULT 0,
    unattributed_mb integer NOT NULL DEFAULT 0,
    models jsonb NOT NULL DEFAULT '[]'::jsonb,
    allocators jsonb NOT NULL DEFAULT '[]'::jsonb,
    error_code text NOT NULL DEFAULT '',
    error_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    observed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (observation_id, component)
);

CREATE INDEX IF NOT EXISTS idx_gpu_runtime_inventory_component_observed
    ON gpu_runtime_inventory (component, observed_at DESC);

CREATE TABLE IF NOT EXISTS gpu_model_vram_calibration (
    id bigserial PRIMARY KEY,
    task_type text NOT NULL,
    model_id text NOT NULL,
    owner_component text NOT NULL,
    device text NOT NULL,
    shape_bucket text NOT NULL,
    resident_floor_mb integer NOT NULL DEFAULT 0,
    load_delta_mb integer NOT NULL DEFAULT 0,
    working_set_mb integer NOT NULL DEFAULT 0,
    guard_margin_mb integer NOT NULL DEFAULT 0,
    sample_count integer NOT NULL DEFAULT 0,
    recent_samples jsonb NOT NULL DEFAULT '[]'::jsonb,
    calibration_source text NOT NULL DEFAULT 'configured_seed',
    observed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (task_type, model_id, owner_component, device, shape_bucket)
);
```

- [ ] Add settings tests for these exact defaults and validators: `gpu.scheduler.runtime_reconciliation_mode=observation`, `gpu.scheduler.inventory_timeout_seconds=2`, `gpu.scheduler.control_lock_timeout_seconds=2`, `gpu.scheduler.context_allowance_mb=256`, `gpu.scheduler.unattributed_threshold_mb=512`, `gpu.scheduler.unattributed_threshold_percent=5`, `gpu.scheduler.reconciliation_retry_seconds=15`, `gpu.scheduler.calibration_min_samples=5`, `gpu.scheduler.calibration_guard_margin_mb=512`, `gpu.scheduler.priority_drain_enabled=false`, `gpu.scheduler.retry_coalescing_enabled=false`, `gpu.scheduler.eviction_expiry_enabled=false`, `gpu.scheduler.idle_unload_enabled=false`, `gpu.scheduler.idle_unload_seconds=120`, `gpu.scheduler.idle_sweep_interval_seconds=30`, and `retrieval.vespa_lexical_fallback_enabled=true`.

- [ ] Add the matching `SettingDefinition` entries with `APPLY_RELOAD`, bounded numeric validators and affected components. Do not change `gpu.scheduler.embedding_vram_mb=2500`; it remains a legacy/configured seed, not measured truth.

- [ ] Run `python -m pytest tests/test_migrations.py tests/test_settings.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/sql/0045_gpu_runtime_reconciliation.sql src/flux_llm_kb/settings_registry.py tests/test_migrations.py tests/test_settings.py && git commit -m "feat: add GPU reconciliation persistence controls"`.

### Task 2: Build the service-local priority operation tracker

**Files:**

- Create: `src/flux_llm_kb/gpu_runtime.py`
- Create: `tests/test_gpu_runtime.py`

**Interfaces:**

- Produces `RuntimeModelKey`, `RuntimeOperationTicket`, `RuntimeResidencyTracker`, `AllocatorSnapshot`, `runtime_request_priority()` and `normalise_priority_class()`.
- `RuntimeResidencyTracker` owns one opaque `process_generation`, timestamps and per-model `activity_sequence`/`in_flight`; it never imports the scheduler or database.
- The queue orders interactive (`100`) before background (`0`), FIFO within a class. A ticket may be yielded before activation, but an active operation is never pre-empted.

- [ ] Write failing unit tests for priority/FIFO ordering, activity-sequence increments, active-operation non-pre-emption, generation mismatch, activity mismatch, in-flight unload refusal and allocator capability fallbacks. The core race test must be deterministic:

```python
def test_interactive_ticket_moves_ahead_of_waiting_background_at_boundary():
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background")
    with tracker.operation(active):
        background = tracker.enqueue(key, priority_class="background")
        interactive = tracker.enqueue(key, priority_class="interactive")
        assert active.is_active
        assert tracker.next_waiting_ticket_id(key) == interactive.id

    with tracker.operation(interactive):
        assert interactive.is_active
    assert not background.is_head
```

- [ ] Run `python -m pytest tests/test_gpu_runtime.py -q`; expect import failure because `gpu_runtime.py` does not exist.

- [ ] Implement these public signatures and make the context manager update timestamps/refcounts in `finally`:

```python
@dataclass(frozen=True)
class RuntimeModelKey:
    task_type: str
    model_id: str

@dataclass(frozen=True)
class AllocatorSnapshot:
    framework: str
    device: str
    capability: str
    allocated_mb: int | None
    reserved_mb: int | None
    peak_reserved_mb: int | None
    reason: str = ""

class RuntimeResidencyTracker:
    def enqueue(self, key: RuntimeModelKey, *, priority_class: str, request_id: str = "") -> RuntimeOperationTicket:
        raise NotImplementedError

    @contextmanager
    def operation(self, ticket: RuntimeOperationTicket) -> Iterator[RuntimeOperationMeasurement]:
        raise NotImplementedError

    def inventory(self, loaded_models: Iterable[RuntimeModelKey]) -> dict[str, Any]:
        raise NotImplementedError

    def next_waiting_ticket_id(self, key: RuntimeModelKey) -> str | None:
        raise NotImplementedError

    def unload(
        self,
        key: RuntimeModelKey,
        *,
        expected_generation: str,
        expected_activity_sequence: int,
        remove: Callable[[], bool],
    ) -> dict[str, Any]:
        raise NotImplementedError
```

- [ ] Implement Torch and Paddle allocator probes with feature detection. Unsupported/import-failed frameworks return `capability="known_unmeasured"` and null numeric fields; never substitute a configured estimate. Keep allocator rows process-global and do not divide them between models.

- [ ] Run `python -m pytest tests/test_gpu_runtime.py -q`; expect all tests to pass without requiring CUDA libraries.

- [ ] Commit: `git add src/flux_llm_kb/gpu_runtime.py tests/test_gpu_runtime.py && git commit -m "feat: track runtime GPU residency safely"`.

### Task 3: Add authoritative model-runner, Paddle and ASR contracts

**Files:**

- Modify: `src/flux_llm_kb/model_runner.py`
- Modify: `src/flux_llm_kb/asr_server.py`
- Modify: `tests/test_model_runner.py`
- Modify: `tests/test_asr_server.py`

**Interfaces:**

- Produces `GET /v1/gpu/residency` on model-runner, Paddle role and ASR.
- Changes `POST /v1/gpu/unload` to require `expected_generation` and `expected_activity_sequence` and return `unload_confirmed`, target presence and allocator before/after.
- Consumes `request_class`/`request_id` only through enum validation; missing or invalid values become background.
- Preserves `/v1/gpu/status` for compatibility.

- [ ] Add endpoint tests which populate the real module caches/fake ASR runtime and assert actual owner, stable generation, PID/start time, `worker_count=1`, canonical model keys, in-flight/timestamps, allocator capability and fenced unload. A second tracker instance must expose a different generation. Include an ASR test where transcribe is blocked and unload returns `409` without calling `unload_model`.

- [ ] Run `python -m pytest tests/test_model_runner.py tests/test_asr_server.py -q`; expect failures for the missing residency endpoint and old unload contract.

- [ ] Instantiate one tracker per service process, with `owner_component` taken from `FLUX_KB_MODEL_RUNNER_ROLE` for model-runner/Paddle and fixed to `asr` for ASR. Build loaded model keys directly from `_EMBEDDING_MODELS`, `_RERANKER_MODELS`, `_PADDLE_OCR_MODELS`, `_PADDLE_OCR_VL_MODELS` and `AsrRuntime._model`; do not read scheduler rows.

- [ ] Replace the separate encode/predict/inference/unload locking boundary with the tracker ticket/operation boundary. Retain model-cache creation locks only for duplicate construction. At this checkpoint, wrap the existing scheduler acquisition inside the active tracker operation so unload cannot race live work; Task 6 replaces that temporary ordering with the yieldable ticket/admission handshake required for priority and admission reattachment.

- [ ] Route unload through `tracker.unload` and the exact cache-removal callback. Return `409` for generation/activity/in-flight conflicts, `404` only when the exact model is not served, and `200` for a matching already-absent target with `unload_confirmed=true` and `target_present=false`.

- [ ] Keep `GET /v1/gpu/residency` fast: no scheduler status call, database call, health fan-out or HTTP self-call. Assert this with monkeypatched failure sentinels.

- [ ] Run `python -m pytest tests/test_gpu_runtime.py tests/test_model_runner.py tests/test_asr_server.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/model_runner.py src/flux_llm_kb/asr_server.py tests/test_model_runner.py tests/test_asr_server.py && git commit -m "feat: expose authoritative GPU residency"`.

### Task 4: Reconcile runtime inventory under one PostgreSQL control lock

**Files:**

- Create: `src/flux_llm_kb/gpu_reconciliation.py`
- Create: `tests/test_gpu_reconciliation.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `tests/test_database.py`
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `tests/test_gpu_scheduler.py`

**Interfaces:**

- Produces `RuntimeInventorySnapshot`, `GpuReconciliationObservation`, `RuntimeInventoryAdapter`, `OllamaInventoryAdapter`, `reconcile_runtime_inventory()` and database persistence/read methods.
- Produces `database.gpu_control_lock(timeout_seconds, url=None)` as a session-scoped context manager and `database.persist_gpu_runtime_observation(observation, url=None)`.
- Consumes the three residency endpoints, Ollama `/api/ps`, `live_gpu_memory()` and the settings from Task 1.

- [ ] Write failing pure accounting tests for `healthy`, `inventory_incomplete`, `reconciliation_required` and `unschedulable`, including the distinction between a known CTranslate2 owner and unattributed memory:

```python
def test_known_unmeasured_owner_is_not_labelled_unattributed():
    observation = calculate_gpu_capacity(
        driver_used_mb=10_839,
        driver_free_mb=1_159,
        inventories=[inventory(owner="asr", capability="known_unmeasured")],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )
    assert observation.state == "inventory_incomplete"
    assert observation.unresolved_known_owner_mb > 0
    assert observation.unattributed_mb == 0
```

- [ ] Add database tests for the session advisory lock, successful-generation replacement, successful omission -> absent, endpoint failure -> unknown, owner conflict -> `inventory_incomplete`, and bounded error metadata.

- [ ] Run `python -m pytest tests/test_gpu_reconciliation.py tests/test_database.py -q`; expect failures for missing interfaces.

- [ ] Implement inventory models with exact state values `healthy`, `inventory_incomplete`, `reconciliation_required`, `unschedulable`. Calculate:

```python
raw_residual_mb = max(
    0,
    driver_used_mb - known_measured_mb - known_reported_mb - context_allowance_total_mb,
)
```

If any confirmed GPU owner is `known_unmeasured`, place residual in `unresolved_known_owner_mb`; otherwise place it in `unattributed_mb`. Exact Ollama `size_vram` contributes to `known_reported_mb` only when no allocator reservation covers that process.

- [ ] Implement concurrent endpoint reads with `ThreadPoolExecutor(max_workers=4)` and one absolute timeout. After the deadline, cancel pending futures and call `shutdown(wait=False, cancel_futures=True)` so context-manager shutdown cannot silently extend the two-second bound. A failed component snapshot marks its previous residency rows `unknown`; it never marks them absent. A successful new generation supersedes old generation rows atomically. A successful same-generation omission marks the missing key absent. Duplicate owners for the same canonical key remain in `gpu_runtime_inventory.models` and make the cache row conflicted/incomplete rather than choosing an owner.

- [ ] Implement the control lock with `pg_try_advisory_lock(hashtextextended('flux.gpu.control', 0))`, bounded polling and `pg_advisory_unlock` in `finally`. Fetch HTTP/driver observations while holding that session lock but outside a transaction; use a short transaction only to persist the completed observation and decision link.

- [ ] Replace Ollama-only `_reconcile_runtime_residency()` with this reconciler. In `observation` mode, record and expose the state but retain legacy admission decisions. Inject a provider into `InProcessGpuScheduler` tests; do not let it call live services.

- [ ] Run `python -m pytest tests/test_gpu_reconciliation.py tests/test_database.py tests/test_gpu_scheduler.py -q`; expect all focused tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/gpu_reconciliation.py src/flux_llm_kb/database.py src/flux_llm_kb/gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py tests/test_gpu_scheduler.py && git commit -m "feat: reconcile GPU runtime inventory"`.

### Task 5: Admit request-shaped VRAM and calibrate measured behaviour

**Files:**

- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/gpu_reconciliation.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `src/flux_llm_kb/model_runner.py`
- Modify: `src/flux_llm_kb/asr_server.py`
- Modify: `tests/test_gpu_scheduler.py`
- Modify: `tests/test_gpu_reconciliation.py`
- Modify: `tests/test_database.py`
- Modify: `tests/test_model_runner.py`
- Modify: `tests/test_asr_server.py`

**Interfaces:**

- Adds `GpuRequestShape`, `GpuVramCalibration`, `shape_bucket_for_profile()`, `resolve_vram_reservation()` and `BaseGpuScheduler.record_vram_sample`.
- Adds database methods `record_gpu_vram_sample` and `resolve_gpu_vram_calibration`.
- Extends `GpuAdmissionDecision` with capacity state, observation ID, calibration source, load delta, working set and reserved peak.

- [ ] Replace the existing resident-zero regression with failing tests proving: a resident Snowflake hit reserves working-set headroom; batch-one and batch-16 buckets differ; cold reserves load + working set; active reservations remain until release; and a shape above physical/configured capacity is explicitly `unschedulable`.

- [ ] Add a failing database calibration test which keeps only the latest 32 numeric samples, uses the conservative high-water/percentile after five fresh samples, and ignores stale/contended samples.

- [ ] Run `python -m pytest tests/test_gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py -q`; expect the resident-hit assertion to fail under the current zero-increment logic.

- [ ] Implement bounded numeric shape buckets. Embeddings use count bucket, total-character bucket, maximum-item bucket and dimensions; rerank uses passage/token buckets; OCR/ASR use existing numeric workload classes. Do not put raw text/path/media metadata in the bucket.

```python
@dataclass(frozen=True)
class GpuVramReservation:
    shape_bucket: str
    resident_floor_mb: int
    load_delta_mb: int
    working_set_mb: int
    guard_margin_mb: int
    reserved_peak_mb: int
    calibration_source: str
```

- [ ] Seed Snowflake's observed 11.9 GiB only for the batch-count `9-16`, dimension `1024` family with wildcard character sub-buckets until measured samples replace it. Store it conservatively as the shape's working-set requirement with `calibration_source='observed_seed'`; do not invent a resident/load split from the aggregate observation. Batch-one continues to use its own configured/measured fallback. Never apply 11.9 GiB as a universal one-query reservation.

- [ ] Instrument operation measurements at three points: pre-load allocator reservation, post-load reservation, and execution peak/post-operation reservation. Record a sample only when allocator capability is measured and the tracker confirms no overlapping process-local GPU operation; otherwise record a bounded `sample_skipped_reason` counter without fabricating values.

- [ ] Calculate physical headroom and configured-budget headroom independently, subtract safety and active peak reservations, and use the minimum. Live driver memory anchors the physical calculation; do not add database resident estimates to it again.

- [ ] In enforcement mode, return the single reconciliation state with `retry_after_seconds=15` for incomplete/unattributed capacity. Do not enqueue an eviction for unattributed memory and do not restart a service. Return non-retryable `unschedulable` only when the smallest supported shape cannot fit.

- [ ] Run `python -m pytest tests/test_gpu_runtime.py tests/test_gpu_reconciliation.py tests/test_gpu_scheduler.py tests/test_database.py tests/test_model_runner.py tests/test_asr_server.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/gpu_scheduler.py src/flux_llm_kb/gpu_reconciliation.py src/flux_llm_kb/database.py src/flux_llm_kb/model_runner.py src/flux_llm_kb/asr_server.py tests/test_gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py tests/test_model_runner.py tests/test_asr_server.py && git commit -m "feat: admit measured GPU working sets"`.

### Task 6: Propagate interactive priority and coalesce waiting admissions

**Files:**

- Modify: `src/flux_llm_kb/model_activity.py`
- Modify: `src/flux_llm_kb/cli.py`
- Modify: `src/flux_llm_kb/model_runner.py`
- Modify: `src/flux_llm_kb/embeddings.py`
- Modify: `src/flux_llm_kb/extractors.py`
- Modify: `src/flux_llm_kb/worker.py`
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `tests/test_model_activity.py`
- Modify: `tests/test_cli.py`
- Modify: `tests/test_model_runner.py`
- Modify: `tests/test_worker.py`
- Modify: `tests/test_gpu_scheduler.py`
- Modify: `tests/test_database.py`

**Interfaces:**

- Extends `caller_surface(surface, request_id=None)` and adds `current_model_request_context()`.
- Extends `GpuTaskProfile` with `priority_class`, `admission_key` and `shape_bucket`, and extends `BaseGpuScheduler.acquire(profile, *, yield_wait=None)` so a higher-priority local ticket may detach a not-yet-granted background admission.
- Adds `GpuLeaseDeferred(GpuSchedulerError)` with `capacity_state`, `admission_id`, `eviction_id` and `retry_after_seconds`.
- Changes PostgreSQL admission to create-or-reattach by stable `admission_key`, detach background callers on short-budget expiry, and cancel unused interactive waiters.

- [ ] Add failing tests for caller mapping (`mcp`/`api`/interactive CLI -> interactive; worker/scheduler/missing/invalid -> background), request identity propagation, admission reattachment, no asynchronous grant for detached rows, drain-barrier behaviour and lower-priority waiter non-protection.

- [ ] Add the acceptance race test: start a background Snowflake lease; enqueue an interactive Qwen/exclusive request; enqueue another background Snowflake request; release the active lease; assert the interactive request becomes head, the later background waiter does not protect idle Snowflake, one linked eviction is reused, and the interactive request grants after verified eviction.

- [ ] Run `python -m pytest tests/test_model_activity.py tests/test_cli.py tests/test_model_runner.py tests/test_worker.py tests/test_gpu_scheduler.py tests/test_database.py -q`; expect priority/reattachment tests to fail.

- [ ] Store one request context for the whole MCP/REST/CLI call. `ModelRunnerClient` sends only `request_class` (`interactive`/`background`) and opaque `request_id`; the server maps those classes to `100`/`0` and ignores numeric client priority. Worker job execution sets `request_id=job['id']` so the server derives `job-id:stage:task:model:shape` admission keys.

- [ ] Implement the service-gate/scheduler handshake without hiding waiters: enqueue the local ticket first; create/reattach the database admission; while waiting, allow the local ticket to signal `yield_wait` when a higher-priority ticket arrives; yielding detaches the background caller but keeps its bounded waiting admission. Only the current local ticket may receive a running lease, so several granted leases cannot pile up behind one encode lock. Once a lease is granted and the ticket is active, ignore yield signals until operation completion.

- [ ] In `_try_grant`, order attached waiters by priority then creation time. If the attached interactive head cannot yet run, set a drain barrier that blocks new lower-priority grants, including shareable embeddings. Protect the head target and running/runtime-in-flight targets; do not protect every lower-priority waiting target.

- [ ] On capacity eviction, keep the same lease `waiting`, set `wait_reason='waiting_eviction'`, and link the fresh/deduplicated eviction. Interactive timeout terminalises its unused waiter and allows lexical/rerank degradation. Background timeout sets `caller_attached=false`, raises `GpuLeaseDeferred`, and leaves the admission reusable until its bounded expiry.

- [ ] Change worker busy telemetry to report `deferred_gpu_capacity`, `gpu_capacity_state`, admission/eviction IDs and retry-after. Do not create a new admission or eviction on an unchanged retry.

- [ ] Run `python -m pytest tests/test_model_activity.py tests/test_cli.py tests/test_model_runner.py tests/test_worker.py tests/test_gpu_scheduler.py tests/test_database.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/model_activity.py src/flux_llm_kb/cli.py src/flux_llm_kb/model_runner.py src/flux_llm_kb/embeddings.py src/flux_llm_kb/extractors.py src/flux_llm_kb/worker.py src/flux_llm_kb/gpu_scheduler.py src/flux_llm_kb/database.py tests/test_model_activity.py tests/test_cli.py tests/test_model_runner.py tests/test_worker.py tests/test_gpu_scheduler.py tests/test_database.py && git commit -m "feat: prioritise interactive GPU admissions"`.

### Task 7: Split only explicitly unschedulable embedding batches

**Files:**

- Modify: `src/flux_llm_kb/database.py`
- Modify: `src/flux_llm_kb/worker.py`
- Modify: `tests/test_database.py`
- Modify: `tests/test_worker.py`

**Interfaces:**

- Consumes `GpuLeaseRejected.capacity_state == 'unschedulable'` (or the equivalent structured model-runner error).
- Produces bounded binary batch splitting inside `sync_search_index()` and an explicit batch-one blocker.
- Does not split ordinary `active_work`, `eviction_pending`, `inventory_incomplete` or `reconciliation_required` responses.

- [ ] Add a failing sync test with a provider which rejects batch size 16 and 8 as unschedulable but accepts 4; assert every row is indexed once, the effective batch sizes are recorded, and no row is reset/repeated.

- [ ] Add a failing batch-one test which asserts one provider call, a non-retryable blocker and no hot retry. Add a timestamp-only regression proving an already indexed, source-hash-identical row is skipped without embedding or status reset.

- [ ] Run `python -m pytest tests/test_database.py tests/test_worker.py -q`; expect the new split tests to fail.

- [ ] Replace the fixed batch loop with a deque of `(offset, rows)` slices. On explicit unschedulable and `len(rows) > 1`, split once into two smaller slices and append them in original order. On batch-one unschedulable, mark only that row failed with `failed_stage='embedding_capacity'` plus non-retryable blocker metadata, and return an explicit non-retryable result. All other retryable capacity states return deferred work without marking rows failed.

- [ ] Add telemetry: configured batch size, attempted size histogram, split count, smallest attempted size and capacity state. Keep current source-hash/indexed checks so timestamp-only changes do not re-embed completed rows.

- [ ] Run `python -m pytest tests/test_database.py tests/test_worker.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/database.py src/flux_llm_kb/worker.py tests/test_database.py tests/test_worker.py && git commit -m "feat: split unschedulable embedding batches"`.

### Task 8: Fence and expire brokered eviction requests

**Files:**

- Modify: `src/flux_llm_kb/database.py`
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `tests/test_database.py`
- Modify: `tests/test_gpu_scheduler.py`

**Interfaces:**

- Extends `enqueue_gpu_eviction_request()` with runtime generation/activity, request reason and observation ID.
- Makes `claim_gpu_eviction_request()` return a new `claim_token` and incremented `row_version`.
- Adds `heartbeat_gpu_eviction_request()`, `expire_stale_gpu_eviction_requests()` and compare-and-set arguments to completion/retry.

- [ ] Add failing database tests for: four stale retrying rows expiring without deletion; a fresh request for the same target/generation being created; old generation never deduplicating new; demand upgrading queued idle; queue/running/retry deadlines derived from current RabbitMQ settings; and late claim-token completion returning `cas_rejected=true` without mutation.

- [ ] Run `python -m pytest tests/test_database.py tests/test_gpu_scheduler.py -q`; expect lifecycle tests to fail.

- [ ] Implement deadlines from `RabbitMqConfig.retry_delay_ms` and `delivery_limit`: queued `max(120s, 2*retry)`, running `max(60s, 2*(unload+verification timeout))`, and retrying `retry_not_before + remaining_deliveries*retry + running_guard`. Refresh only on a valid claim, heartbeat or retry owned by the current token.

- [ ] Under row lock, expire active rows before deduplication. Deduplicate only fresh same owner/task/model/generation. If a demand request finds a queued idle request, update its reason/link rather than adding a second unload. Preserve expired rows and emit the existing event/audit envelope with `terminal_reason='stale_request_expired'`.

- [ ] Require `(status='running', claim_token, row_version)` for heartbeat, retry and completion. A terminal/expired/replaced row returns a structured late-CAS result; it never becomes active again. A late broker delivery for a terminal row is acknowledged without unload.

- [ ] Run `python -m pytest tests/test_database.py tests/test_gpu_scheduler.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/database.py src/flux_llm_kb/gpu_scheduler.py tests/test_database.py tests/test_gpu_scheduler.py && git commit -m "feat: fence brokered GPU evictions"`.

### Task 9: Evict only a runtime-confirmed owner and verify attributable release

**Files:**

- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/gpu_reconciliation.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `tests/test_gpu_scheduler.py`
- Modify: `tests/test_gpu_reconciliation.py`
- Modify: `tests/test_database.py`

**Interfaces:**

- `process_gpu_eviction_request()` consumes the claim token/version and a fresh reconciliation observation.
- Produces terminal reasons `verified_unload`, `became_active`, `generation_changed`, `unload_failed`, `verification_deferred`, `memory_release_unverified`, `target_already_absent` and `cas_rejected`.
- Routes unload only to `owner_component` from fresh runtime inventory.

- [ ] Add failing tests for owner routing, generation/activity mismatch, active lease/in-flight protection, measured allocator drop success, still-present failure, absent-without-drop reconciliation, quiet-window deferral for known-unmeasured owners and Ollama fingerprint reread.

- [ ] Run `python -m pytest tests/test_gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py -q`; expect verification tests to fail under global-free-VRAM proof.

- [ ] Hold the GPU-control session lock for the pre-unload reconciliation, exact unload request, post-unload inventory and durable state transition. Recheck current claim token/version before HTTP unload. Never infer the endpoint from task type when an owner is present; missing/ambiguous owner is `inventory_incomplete`.

- [ ] For measured allocators, require target absence, `unload_confirmed=true`, matching generation/activity and a reserved-memory drop above tolerance. Record the driver delta only as corroboration. For known-unmeasured owners, require all other Flux GPU leases quiescent and a stable pre/post quiet-window driver drop; otherwise retry as `verification_deferred`.

- [ ] If the target remains, the endpoint fails or identity changes, retain owner evidence and set `runtime_state='unload_failed'`. If absent but memory release cannot be attributed, retain last owner/evidence, set `runtime_state='memory_release_unverified'`, and make capacity `reconciliation_required`. Only verified absence plus attributable release marks eviction succeeded and refreshes the cache from post-unload inventory.

- [ ] Remove “request now fits” and database-residency clearing as success proofs. A no-op already-absent response is `skipped` only when a fresh authoritative snapshot proves absence; it does not claim memory was freed.

- [ ] Run `python -m pytest tests/test_gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py -q`; expect all tests to pass, including the interactive-after-embedding acceptance race from Task 6.

- [ ] Commit: `git add src/flux_llm_kb/gpu_scheduler.py src/flux_llm_kb/gpu_reconciliation.py src/flux_llm_kb/database.py tests/test_gpu_scheduler.py tests/test_gpu_reconciliation.py tests/test_database.py && git commit -m "feat: verify runtime-owned GPU evictions"`.

### Task 10: Add leader-elected automatic idle unload

**Files:**

- Modify: `src/flux_llm_kb/event_worker.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `tests/test_event_worker.py`
- Modify: `tests/test_database.py`
- Modify: `tests/test_gpu_scheduler.py`

**Interfaces:**

- Adds `run_gpu_eviction_maintenance_once()` and `run_gpu_eviction_maintenance_loop()`.
- Adds a distinct PostgreSQL advisory leader lock for the sweep and reuses the fenced eviction enqueue path with `request_reason='idle'`.
- Runs only when `queue_name == messaging.COMMAND_GPU_EVICTION_QUEUE` and `idle_unload_enabled=true`.

- [ ] Add failing tests which assert no task for other queues, one task for the GPU queue, `asyncio.to_thread` use, clean cancellation, leader-lock loser no-op, stale expiry before idle selection, and `idle_unload_seconds=0` disablement.

- [ ] Add race tests: a model older than 120 seconds queues one idle eviction; a waiting lease/fresh load/in-flight operation excludes it; a new activity sequence after selection makes the worker return `skipped/became_active`.

- [ ] Run `python -m pytest tests/test_event_worker.py tests/test_database.py tests/test_gpu_scheduler.py -q`; expect maintenance tests to fail.

- [ ] In the GPU queue worker, create the maintenance coroutine before `consume()`, call blocking work through `asyncio.to_thread`, and cancel/await it in `finally`. Each 30-second tick tries the leader lock, expires stale eviction rows, performs one fresh reconciliation and enqueues only runtime-confirmed idle targets with no protected lease/claim.

- [ ] Compute idle age from `last_operation_completed_at`, not cache insertion or database `last_used_at`. Exclude `in_flight > 0`, a fresh operation/load, any running lease, an attached waiting lease for the same target, and an active eviction claim.

- [ ] Reuse generation/activity fencing from Task 9. Do not call unload directly from the sweep and do not add a Compose service.

- [ ] Run `python -m pytest tests/test_event_worker.py tests/test_database.py tests/test_gpu_scheduler.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/event_worker.py src/flux_llm_kb/database.py src/flux_llm_kb/gpu_scheduler.py tests/test_event_worker.py tests/test_database.py tests/test_gpu_scheduler.py && git commit -m "feat: unload confirmed idle GPU models"`.

### Task 11: Keep Vespa lexical retrieval available without Snowflake

**Files:**

- Modify: `src/flux_llm_kb/search_index.py`
- Modify: `src/flux_llm_kb/database.py`
- Modify: `src/flux_llm_kb/service.py`
- Modify: `tests/test_retrieval_stack.py`
- Modify: `tests/test_corpus_search.py`
- Modify: `tests/test_database.py`

**Interfaces:**

- Changes `VespaSearchAdapter.query(query, *, embedding: list[float] | None, root_name=None, file_kinds=None, languages=None, limit=20, rank_profile=None)`.
- Adds one exact retryable embedding classifier returning failure class/capacity state or `None`.
- Produces diagnostics `query_mode='vespa_lexical_fallback'`, `rank_profile='bm25'`, embedding latency/failure class/capacity state.
- Restricts service-level PostgreSQL fallback to `SearchIndexError` from Vespa transport/query execution.

- [ ] Add a failing adapter test proving `embedding=None` emits filtered `userQuery()` YQL, `ranking.profile='bm25'`, and no `nearestNeighbor` or `input.query(query_embedding)`.

- [ ] Add parameterised database tests for retryable Snowflake/model-runner timeout, busy, lease timeout, `inventory_incomplete` and `reconciliation_required`: Vespa is called once without a vector, PostgreSQL is never called while Vespa succeeds, and Qwen busy returns Vespa order. Add an `unschedulable` case proving it is non-retryable and remains visible.

- [ ] Add tests proving Vespa `SearchIndexError` still invokes PostgreSQL diagnostic fallback and a `ValueError`/invalid embedding/non-retryable model error propagates visibly.

- [ ] Run `python -m pytest tests/test_retrieval_stack.py tests/test_corpus_search.py tests/test_database.py -q`; expect lexical-fallback tests to fail.

- [ ] Make the vector optional. Select `hybrid_rrf` plus nearest-neighbour/tensor only with a valid vector; select `bm25` plus filtered `userQuery()` when absent. Keep root/file-kind/language filters identical in both paths. Parameterise candidate signal merging by query mode: lexical fallback uses the Vespa relevance/BM25 score as `vespa_lexical` and must not fabricate `vespa_rrf` or `vespa_dense` streams.

- [ ] Wrap only `_embed_query_for_retrieval()` with the retryable classifier in both `search_corpus_chunks_vespa()` and `search_evidence_vespa()`. Retain the configured five-second wait. On a classified failure and enabled flag, call Vespa lexical mode immediately and fill bounded diagnostics. Do not catch the subsequent Vespa query in this layer.

- [ ] In `service._search_corpus_with_configured_engine()` and `_search_evidence_with_configured_engine()`, catch `SearchIndexError` only. Hydration/programming/non-retryable embedding errors must not be mislabeled as Vespa unavailability or silently sent to PostgreSQL.

- [ ] Run `python -m pytest tests/test_retrieval_stack.py tests/test_corpus_search.py tests/test_database.py -q`; expect all tests to pass.

- [ ] Commit: `git add src/flux_llm_kb/search_index.py src/flux_llm_kb/database.py src/flux_llm_kb/service.py tests/test_retrieval_stack.py tests/test_corpus_search.py tests/test_database.py && git commit -m "feat: fall back to lexical Vespa search"`.

### Task 12: Expose reconciliation evidence and complete local verification

**Files:**

- Modify: `src/flux_llm_kb/gpu_scheduler.py`
- Modify: `src/flux_llm_kb/model_activity.py`
- Modify: `src/flux_llm_kb/acceleration.py`
- Modify: `src/flux_llm_kb/operational_diagnostics.py` if counters are not already passed through
- Modify: `tests/test_gpu_scheduler.py`
- Modify: `tests/test_model_activity.py`
- Modify: `tests/test_acceleration.py`
- Modify: `tests/test_operational_diagnostics.py`
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md`

**Interfaces:**

- Produces one `runtime_reconciliation` object through scheduler status, REST/CLI/MCP status consumers, acceleration status and the dashboard snapshot.
- Produces counters for non-zero resident reservations, wait duration by reason/class, coalesced retries, stale expiries, CAS rejections, unload failures, unverified release, idle unload and lexical Vespa degradation.
- Does not enable enforcement flags or perform a live probe.

- [ ] Add failing payload tests for the exact reconciliation object: state/retry, observation and measurement timestamps, component owner/generation/freshness/capability, driver/known/allowance/unresolved/unattributed totals, queue-head/drain/wait reasons, reservation/calibration source, and eviction claim/verification outcome.

- [ ] Run `python -m pytest tests/test_gpu_scheduler.py tests/test_model_activity.py tests/test_acceleration.py tests/test_operational_diagnostics.py -q`; expect missing payload fields.

- [ ] Add bounded payload serializers and counters. Keep `model_activity._scheduler_summary()` content-free and pass the reconciliation object through; do not make status call a second reconciliation more often than the configured observation cache/cooldown.

- [ ] Update `docs/architecture.md` with runtime authority, control-lock scope, capacity equations, priority boundary, fencing and independent rollout flags. Update the roadmap row to `in progress` only after code exists; record local verification and leave live probe/deployment as remaining work. Do not update the dashboard user manual.

- [ ] Run the focused suite:

```powershell
python -m pytest tests/test_migrations.py tests/test_settings.py tests/test_gpu_runtime.py tests/test_gpu_reconciliation.py tests/test_gpu_scheduler.py tests/test_database.py tests/test_model_runner.py tests/test_asr_server.py tests/test_model_activity.py tests/test_cli.py tests/test_acceleration.py tests/test_worker.py tests/test_event_worker.py tests/test_retrieval_stack.py tests/test_corpus_search.py tests/test_operational_diagnostics.py -q
```

Expected: zero failures and no warnings introduced by changed files.

- [ ] Run `python -m pytest -q`; expected: the full suite passes. If an environment-only integration test is skipped, record its exact name/reason; do not weaken it.

- [ ] Run `python -m compileall -q src tests`; expected: exit code 0.

- [ ] Review `git diff --check`, `git status --short`, and `git log --oneline --decorate -15`. Confirm no Compose/deployment/manual/generated-dashboard changes and no untracked artefacts.

- [ ] Commit final docs/observability changes: `git add src/flux_llm_kb/gpu_scheduler.py src/flux_llm_kb/model_activity.py src/flux_llm_kb/acceleration.py src/flux_llm_kb/operational_diagnostics.py tests/test_gpu_scheduler.py tests/test_model_activity.py tests/test_acceleration.py tests/test_operational_diagnostics.py docs/architecture.md docs/roadmap.md && git commit -m "docs: record GPU reconciliation rollout evidence"`.

- [ ] Stop and request a closeout/deployment decision. Production probes required by the specification remain approval-gated. If code-only closeout is approved, use `scripts/dev/complete-feature.ps1 -SkipDeploy`; never substitute a manual merge/push/purge sequence.

## Approval-gated live acceptance and rollback checklist

This section is not part of implementation-plan execution without fresh production approval.

1. Deploy endpoints/schema in observation mode; verify every controlled service is single-process or aggregates all workers.
2. Exercise one batch-one and one batch-16 Snowflake operation; compare allocator and driver readings and approve/adjust allowance/threshold settings from evidence.
3. Terminalise a synthetic expired eviction through the maintenance path and prove a fresh same-target generation queues without deleting audit history.
4. Hold an embedding lease, issue an interactive exclusive request, release the lease, and prove the idle runtime-confirmed owner unloads before the interactive grant. Record that no running/in-flight work was cancelled.
5. Disable Snowflake query embedding and prove MCP `kb.brief` returns `vespa_lexical_fallback` inside the interactive budget without PostgreSQL while Vespa is healthy.
6. Enable priority/retry/expiry enforcement flags separately, then idle unload last at 120 seconds. Observe churn and counters before widening rollout.
7. Roll back by disabling enforcement, priority drain, retry coalescing, expiry and idle unload independently. Retain endpoints, schema, telemetry and audit history; lexical Vespa fallback may remain enabled. Do not restart a service automatically for unattributed VRAM.
