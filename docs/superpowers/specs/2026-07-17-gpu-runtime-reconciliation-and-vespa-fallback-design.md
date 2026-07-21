# GPU runtime reconciliation, fair eviction and lexical Vespa fallback

**Status:** approved for implementation on 2026-07-17; the implementation plan is prepared, but execution is deliberately paused for a model switch. Deployment, live probing and service restart remain unapproved.

## Purpose

Fix GPU starvation and misleading capacity decisions without cancelling live CUDA work:

1. A Snowflake query-embedding failure must still use Vespa's lexical BM25 index instead of abandoning Vespa for PostgreSQL.
2. Admission and eviction must use fresh runtime inventory and measured request behaviour. Database rows are a cache of runtime truth, not the authority.
3. Interactive MCP/API queries must move ahead of background jobs at a safe lease boundary.
4. Stale eviction requests and repeated GPU-busy retries must not circulate indefinitely.
5. Confirmed-idle models must unload automatically after a bounded timeout.

This is one focused scheduler/retrieval change. It does not authorise production deployment, alter the live GPU budget, or add automatic service recovery.

## Evidence and invariants

- Snowflake is configured at 2.5 GiB, but sustained 16-item embedding batches reached about 11.9 GiB. A verified unload later released 10.37 GiB.
- During the design review, Snowflake was the only database-marked resident, no GPU lease was running, and the driver reported 10,839 MiB used with 1,159 MiB free. Only 138 MiB remained after the configured safety margin. This snapshot is diagnostic evidence, not an attribution claim until allocator inventory exists.
- Current admission treats a resident model as requiring zero incremental VRAM. Residency avoids a model-load cost; it does not eliminate execution working memory, allocator growth or retained framework workspace.
- Current PostgreSQL admission queues an eviction and immediately times out its waiting lease. Subsequent job attempts create an apparent loop.
- Current candidate selection protects every waiting model. A lower-priority Snowflake waiter can therefore pin an idle Snowflake model against a higher-priority exclusive request.
- Caller surfaces are already tracked, but GPU task profiles currently receive priority zero.
- Existing gpu_evictions rows in queued, running or retrying can remain active indefinitely and suppress a fresh request for the same target.
- The broker retry delay is 30 seconds and the delivery limit is eight. Expiry must be derived from those settings and refreshed by valid progress, not guessed independently.
- Unload is safe only after the active operation and lease finish. No part of this design cancels CUDA kernels, terminates a request, pops a model used by another thread, or restarts a service automatically.

## Decisions

| Area | Decision |
| --- | --- |
| Runtime authority | Model-runner, Paddle and ASR expose authoritative residency. Ollama /api/ps is normalised through an adapter. Database state is a time-stamped cache. |
| Capacity model | Separate resident/load footprint from request-shaped execution working set. A resident hit never implies zero required headroom. |
| Scheduling | Interactive query work has priority over background work, with FIFO inside each class and a drain barrier at safe lease boundaries. |
| Active work | Running leases and runtime in-flight operations are never evicted or cancelled. |
| Eviction | Route unload to the runtime-reported owner and fence it by runtime generation, activity sequence and claim token. |
| Unknown memory | Distinguish incomplete inventory, known-but-unmeasured owners and material unattributed VRAM. Never guess an owner; only a confirmed idle unmeasured owner may be unloaded to reconcile its footprint. |
| Retry behaviour | Reuse one admission and one fresh eviction request per job/stage and runtime generation. Slow unresolved reconciliation; reject only genuinely unschedulable shapes. |
| Retrieval degradation | A retryable Snowflake failure within the interactive embedding budget produces a lexical-only Vespa query. PostgreSQL is used only if Vespa fails. |
| Idle residency | The existing GPU-eviction worker runs a leader-elected maintenance tick and requests verified unload after 120 seconds idle by default. |
| Recovery | Unattributed capacity never triggers an automatic container or service restart. Recovery remains approval-gated. |

## Runtime residency and unload contract

Add GET /v1/gpu/residency to model-runner, Paddle runner and ASR. It is local/control-plane only, fast, independent of the scheduler and database, and safe to call while work is running.

~~~json
{
  "ok": true,
  "service": "model-runner",
  "owner_component": "model-runner",
  "process_generation": "opaque-id-created-at-service-start",
  "process": {
    "pid": 1234,
    "started_at": "...",
    "worker_count": 1
  },
  "observed_at": "...",
  "models": [
    {
      "task_type": "embedding",
      "model_id": "Snowflake/snowflake-arctic-embed-l-v2.0",
      "device": "cuda:0",
      "gpu_resident": true,
      "in_flight": 0,
      "activity_sequence": 42,
      "last_operation_started_at": "...",
      "last_operation_completed_at": "..."
    }
  ],
  "allocators": [
    {
      "framework": "torch",
      "device": "cuda:0",
      "capability": "measured",
      "allocated_mb": 0,
      "reserved_mb": 0,
      "peak_reserved_mb": 0
    }
  ]
}
~~~

Contract rules:

- process_generation changes on every service start.
- owner_component is the endpoint that actually owns the object. The scheduler must not infer ownership from task type.
- Models use canonical task_type/model_id keys matching scheduler profiles.
- activity_sequence increments when a model operation starts. It prevents a stale idle or demand eviction from unloading a model used since selection.
- in_flight and the timestamps are maintained by the same operation gate used for load, inference and unload.
- Allocator values are process-global per framework/device. They must not be falsely divided between models.
- A framework that cannot expose allocator data, including CTranslate2 where applicable, reports null numeric fields, capability known_unmeasured and a bounded reason. Static scheduler estimates are not runtime measurements.
- The controlled services must remain single-process unless the endpoint aggregates every serving worker. Otherwise the inventory is not authoritative and enforcement remains disabled for that component.

POST /v1/gpu/unload accepts task_type, model_id, expected_generation and expected_activity_sequence. Under the service-local operation gate it:

1. rejects a generation or activity-sequence mismatch;
2. rejects a non-zero in-flight count;
3. removes only the requested cache object;
4. runs the framework's supported release/empty-cache path;
5. returns generation, target presence after unload, allocator before/after values and unload_confirmed.

Embedding encode, rerank predict, Paddle inference and ASR transcribe must use that same gate/refcount. Their current load and execution locks are not sufficient because unload can otherwise race live work.

Ollama's adapter uses /api/ps model name, digest, size_vram and expiry fields as an observation fingerprint. Because /api/ps does not expose a process generation, the worker must reread and match the fingerprint immediately before unload and confirm absence afterwards while holding the global scheduler control lock. All app-originated Ollama work must continue to use GPU leases; an out-of-band state change produces reconciliation_required rather than guessed ownership.

## Reconciliation and capacity accounting

Production enforcement is a PostgreSQL-scheduler contract. The in-process scheduler uses injected inventory in tests/development and must not claim authoritative multi-service reconciliation.

Each admission cycle and each eviction attempt performs one reconciliation. A cycle is not repeated on every 250 ms queue poll:

1. Acquire the PostgreSQL GPU-control advisory lock with a bounded wait.
2. Fetch the four inventories concurrently within the configured timeout. A runtime may use an in-process provider for its own inventory to avoid an HTTP self-call.
3. Take timestamped driver readings from the same designated source used by the decision.
4. Atomically persist successful component snapshots:
   - a new process generation supersedes every cached row from the old generation;
   - omission by a successful snapshot marks that generation's missing key absent;
   - endpoint failure marks prior rows unknown and never absent;
   - the runtime-reported owner is retained.
5. Calculate capacity, persist an observation identifier, and record the exact observation on the admission or eviction decision.
6. Release the control lock after the decision or verified unload transition is durable.

Per GPU:

- known_measured_mb is the sum of unique process allocator reservations.
- known_reported_mb includes exact runtime-reported VRAM, such as Ollama size_vram, only for processes without allocator reservation data.
- Configured model estimates never count as known runtime allocation.
- context_allowance_total_mb is the per-process allowance multiplied by confirmed GPU processes; it accounts for driver/context overhead outside framework allocators.
- raw_residual_mb = max(0, driver_used_mb - known_measured_mb - known_reported_mb - context_allowance_total_mb).
- If a confirmed owner lacks allocator or exact runtime size, report raw_residual_mb as unresolved_known_owner_mb and use inventory_incomplete. Otherwise, unattributed_mb equals raw_residual_mb. This prevents known-but-unmeasured ASR/CTranslate2 memory from being mislabeled as a leak.

Capacity states are mutually exclusive and stable:

- healthy: all required inventories are fresh and residual memory is below the material threshold;
- inventory_incomplete: a required runtime is unreachable, multi-process without aggregation, or known-but-unmeasured beyond the safe allowance;
- reconciliation_required: material unattributed VRAM remains;
- unschedulable: the calibrated request shape exceeds physical or configured capacity even with all evictable models unloaded.

inventory_incomplete and reconciliation_required return one structured retryable state with a default 15-second slow retry and never restart a service. Neither queues an eviction for unowned residual memory. inventory_incomplete may queue one verified unload of the runtime-confirmed idle unmeasured owner; endpoint failure or non-aggregated multi-process state cannot. unschedulable is non-retryable unless the caller can split the batch.

Initial reloadable telemetry defaults are a 2-second inventory timeout, 256 MiB context allowance per confirmed GPU process, and a material threshold of max(512 MiB, 5% of configured budget). These defaults begin in observation mode. Enforcement is enabled only after an approval-gated probe confirms that the allowance and threshold fit this host; the system must not automatically learn an ever-rising baseline that could hide a leak.

## Request-shaped VRAM admission

Record three separate calibration values for each task/model/owner/device and bounded request-shape bucket:

- resident_floor_mb: quiescent retained allocation after load/use;
- load_delta_mb: additional peak needed when the target is absent;
- working_set_mb: peak execution increase over the pre-operation resident state.

Embedding shape uses input count, total bounded input characters or token estimate, maximum item size and dimensions. Store only numeric buckets and hashes/identifiers already permitted by telemetry; never store query or document text. Other task types use bounded workload features already available, such as rerank passage count/token bucket or OCR/ASR input class.

Admission reserves:

- confirmed resident target: calibrated working_set_mb plus guard margin;
- absent target: calibrated load_delta_mb plus working_set_mb plus guard margin;
- insufficient fresh samples: the conservative configured value or a larger observed seed, never zero.

The headroom check honours both physical driver-free memory and the configured Flux budget, subtracts the safety margin, and retains outstanding active-lease peak reservations until release. Live driver memory anchors the calculation; database resident estimates are not added again.

Calibration uses bounded recent samples, a conservative percentile/high-water value, minimum sample count, freshness and guard margin. The observed Snowflake batch-16 peak is a seed for that shape, not a universal requirement for a one-text MCP query.

If a background embedding shape is unschedulable, reduce its batch size through the existing batch boundary and recalculate once per smaller shape. Batch-one unschedulable work returns the explicit state. Do not repeatedly admit a resident Snowflake request with zero headroom.

## Interactive priority, fair waiting and retry coalescing

Map trusted caller surfaces to two scheduler classes:

- interactive priority 100: MCP, REST query endpoints and interactive CLI retrieval;
- background priority 0: workers, indexing, scheduled processing and maintenance.

The API-side ModelRunnerClient propagates the allowlisted request class to model-runner/Paddle/ASR calls. Missing or untrusted values default to background. FIFO applies within each class. Service-local execution gates must also be priority-aware: an interactive call already waiting at an embedding/model lock must be selected before queued background calls at the next safe boundary. The current embedding order, which takes its encode lock before entering the scheduler, must not hide an interactive waiter from both queues or allow several granted leases to wait behind one serial execution slot.

Admission behaviour:

1. Create or reattach to a waiting lease using a stable admission_key: interactive request ID, or background job ID plus stage/model/shape.
2. When a blocked interactive request is the queue head, establish a drain barrier: no new lower-priority lease is granted. This applies to shareable query embeddings as well as exclusive requests.
3. Let running leases and runtime in-flight work finish naturally.
4. Reconcile again at that safe boundary.
5. Protect the head request's target and every running/in-flight owner absolutely.
6. Treat later/lower-priority waiting targets as an eviction preference, not an ownership lock. They cannot pin an idle model against the queue head.
7. If capacity needs eviction, keep the same lease waiting, set wait_reason waiting_eviction, link the fresh eviction request, and re-evaluate after its terminal transition.

Separate the caller's synchronous wait budget from the admission lifetime:

- An interactive query cancels its unused waiting lease when its short budget expires and follows its defined degradation path.
- A background worker may return deferred_gpu_capacity while the database lease remains waiting for a bounded lifetime. Its next attempt reattaches by admission_key rather than creating another lease or eviction.
- A waiting admission is never granted asynchronously without an attached caller; a retry reattaches and performs the next grant decision.

“Cap the GPU-busy retry loop” means coalescing and rate-limiting, not giving up on valid background work:

- one active admission per job/stage/shape;
- one fresh active eviction per owner/model/generation;
- active_work and eviction_pending are wait reasons, not immediate failures;
- unchanged incomplete/unattributed capacity observes the 15-second slow cooldown;
- retries record gpu_capacity_state and linked identifiers instead of issuing another eviction;
- unschedulable work is split where supported or becomes an explicit non-retryable blocker.

No fixed retry count terminalises work that merely needs the current lease to finish.

## Generation-fenced broker eviction

Add additive lifecycle fields to gpu_evictions:

- runtime_generation and runtime_activity_sequence;
- claim_token and row_version;
- status_changed_at, heartbeat_at, retry_not_before and expires_at;
- request_reason, terminal_reason and reconciliation_observation_id.

Active statuses remain queued, running and retrying. Terminal statuses are succeeded, failed, skipped and expired.

Rules:

1. Before deduplication and on the maintenance tick, terminalise expired active rows under lock and emit an audit event.
2. Deduplicate only a fresh row for the same owner, task/model and runtime generation. A new generation cannot be blocked by an old request.
3. A demand eviction may supersede or upgrade a queued idle request for the same target; it must not create two unloads.
4. Each claim creates a claim_token and increments row_version. A live worker heartbeats and extends expires_at only while it owns that token.
5. Queue expiry is max(120 seconds, two broker retry intervals). Running expiry is max(60 seconds, twice the unload-plus-verification deadline). Retrying expiry is retry_not_before plus the remaining delivery count multiplied by broker retry delay, plus one running-expiry guard. Each valid claim/retry refreshes the applicable deadline; changing the broker settings changes new deadlines.
6. Completion and retry use compare-and-set on running status, claim_token and row_version. A terminalised or replaced attempt cannot resurrect itself.
7. A late broker delivery for a terminal row is acknowledged without another unload. A late runtime result triggers reconciliation/audit but does not change the request's terminal history.

This preserves every old row for audit while ensuring stale queued/running/retrying rows cannot permanently deduplicate new work.

## Runtime-confirmed eviction and verification

The eviction worker holds the GPU-control lock and reconciles immediately before unload:

1. The target must still be reported by the selected owner with matching generation/fingerprint and activity sequence.
2. No running lease or runtime in-flight operation may protect it.
3. The claim token must still be current.
4. The worker calls only the reported owner endpoint.

An eviction succeeds only when:

- the response confirms the expected runtime identity;
- unload_confirmed is true;
- the target is absent in the post-unload inventory; and
- memory release is attributable.

For a measured allocator, reserved memory must fall beyond the configured measurement tolerance. The global driver reading is recorded as corroboration but is not required to match exactly because unrelated allocations can change concurrently. For a runtime without allocator telemetry, defer verification until every other Flux GPU lease is quiescent, then take quiet-window driver readings under the control lock and require a meaningful drop. If that quiet window cannot be obtained, retry as verification_deferred rather than guessing.

Outcomes:

- target still present, generation changed, busy or endpoint failure: runtime state unload_failed; retain the confirmed owner;
- target absent but allocator/driver release cannot be verified: runtime state memory_release_unverified and capacity reconciliation_required; retain the last confirmed owner and evidence;
- verified target absence plus attributable release: succeeded, then refresh the runtime cache from the post-unload inventory.

“Request now fits” alone is not unload proof. No outcome silently clears ownership based only on a database update.

## Automatic idle unload

Run a CPU-only maintenance task inside the existing gpu-eviction-worker process when it consumes the GPU eviction queue. Do not add a new Compose service. The task:

- runs every 30 seconds by default through asyncio.to_thread for blocking database/runtime work;
- uses a PostgreSQL advisory leader lock so only one instance sweeps;
- reconciles stale eviction rows first;
- selects only runtime-confirmed residents whose last_operation_completed_at is older than gpu.scheduler.idle_unload_seconds, default 120 seconds;
- excludes any running lease, in-flight operation, fresh load, waiting lease for the same target, or active eviction claim;
- queues the same generation-fenced broker request with request_reason idle.

The worker repeats the activity-sequence and in-flight checks under the service operation gate. A new lease or operation causes skipped/became_active, not an unload. Setting idle_unload_seconds to zero disables the sweep.

## Lexical-only Vespa degradation

Vespa is not dependent on Snowflake for lexical retrieval.

Make VespaSearchAdapter.query accept an optional embedding. Without one it emits filtered userQuery YQL using the existing bm25 rank profile, with no nearestNeighbor clause or query tensor.

search_corpus_chunks_vespa and search_evidence_vespa:

1. retain the existing bounded interactive embedding wait, currently five seconds;
2. catch only classified retryable Snowflake/model-runner/lease timeout, busy, reconciliation and capacity failures around query embedding;
3. issue the lexical-only Vespa query immediately;
4. record query_mode vespa_lexical_fallback, embedding latency, failure class and capacity state;
5. apply the existing reranker degradation: a busy Qwen reranker returns Vespa-ranked results;
6. use PostgreSQL lexical diagnostic fallback only when Vespa transport/query execution itself fails.

Programming errors, invalid embeddings and non-retryable model failures remain visible rather than being mislabeled as capacity degradation.

## Persistence and observability

Add additive migrations for:

- gpu_runtime_inventory: component, owner, generation/fingerprint, freshness/state, model inventory, allocator capability, driver/known/unattributed totals, observed time and bounded error metadata;
- gpu_model_residency: runtime_state, owner, generation/fingerprint, activity sequence, in-flight count, operation timestamps, observed time and bounded failure reason;
- gpu_model_vram_calibration: load/resident/working-set samples by shape bucket, percentile/high-water values, sample count, freshness and guard;
- gpu_leases: admission_key, priority_class, wait_reason, linked eviction, shape bucket, reserved/load/working-set values and reconciliation observation;
- gpu_evictions lifecycle/fencing fields defined above.

Keep bounded numeric telemetry and identifiers only. Apply the project's existing retention and metadata sanitisation rules.

Scheduler status, dashboard payloads and structured model errors expose one runtime_reconciliation object containing:

- state and retry time;
- observation ID and measurement timestamps;
- per-component owner/generation/freshness/capability;
- driver used/free, known measured, known reported, known unmeasured, allowance and unattributed VRAM;
- queue head priority, drain-barrier state and wait reasons;
- admission load/working-set reservation and calibration source;
- eviction request/claim/verification outcome.

Required counters include resident-hit non-zero reservations, wait duration by reason/priority, coalesced retries, stale expiries, late CAS rejections, unload_failed, memory_release_unverified, idle unloads and lexical Vespa degradations.

## Regression and acceptance evidence

Add focused tests to the scheduler, database, event-worker, model-runner, ASR, retrieval and search-index suites.

Capacity and calibration:

1. A resident Snowflake hit still reserves request-shaped working-set VRAM.
2. Batch-one and batch-16 Snowflake shapes calibrate separately.
3. A cold request reserves load plus working set; a resident request does not double-count its floor.
4. An unschedulable background batch splits; batch-one does not hot-retry.
5. Known-unmeasured ownership is distinct from unattributed VRAM and permits only a targeted, confirmed-idle reconciliation unload.

Fairness and retry:

6. An interactive exclusive waiter establishes a drain barrier, waits for the active embedding lease, then evicts the idle resident and succeeds.
7. Running/in-flight models are never selected or unloaded.
8. A lower-priority waiting Snowflake request does not pin Snowflake against the queue head.
9. A background retry reattaches to its admission and linked eviction instead of creating new rows.
10. Interactive timeout cancels its unused waiter and follows lexical/rerank degradation.

Runtime and eviction fencing:

11. Residency endpoints report actual owner, stable generation, activity sequence, allocator capability and operation timestamps.
12. A new generation atomically supersedes old rows; partial endpoint failure changes rows to unknown, not absent.
13. Unload refuses a generation/activity mismatch or in-flight operation.
14. Stale queued/running/retrying rows become expired and audited; a fresh generation is not deduplicated.
15. Late completion with an old claim token cannot mutate a terminal row.
16. Verified allocator/driver release succeeds; still-loaded becomes unload_failed; absent-without-release becomes memory_release_unverified.
17. Idle eviction loses safely to a new lease/activity sequence.

Retrieval:

18. Retryable Snowflake failure within the interactive budget produces lexical-only Vespa YQL and never calls PostgreSQL while Vespa succeeds.
19. Vespa failure still invokes the existing PostgreSQL diagnostic fallback.
20. Non-retryable embedding errors remain visible.

Acceptance requires a clean focused test run and, only after separate deployment approval, a live probe showing:

- stale rows no longer block a new eviction;
- an interactive/exclusive request succeeds after active embedding completes and verified idle eviction runs;
- decisions record runtime observation, actual memory and request-shape reservation;
- no active CUDA work is cancelled;
- an idle model unloads after the configured timeout;
- a Snowflake outage still returns lexical Vespa results within the interactive budget;
- material unattributed VRAM produces one slow reconciliation state and no restart.

## Rollout and rollback

1. Ship additive schema, runtime endpoints and shared operation gates with reconciliation in observation mode.
2. Enable lexical Vespa degradation independently; it does not require GPU enforcement.
3. Collect allocator/driver/load/working-set samples and validate host-specific allowance, threshold and Snowflake shape calibration.
4. After an approval-gated probe, enable runtime enforcement, generation-fenced expiry, priority/drain behaviour and retry coalescing through reloadable flags.
5. Enable the idle sweep last, initially at 120 seconds, and observe reload/eviction churn.

Rollback disables enforcement, interactive drain, retry coalescing and idle unload independently while retaining endpoints, telemetry and audit history. Lexical Vespa fallback may remain enabled. No rollout or rollback step automatically recreates containers, restarts a service, changes the GPU budget or deploys production.

## Likely implementation surface

- src/flux_llm_kb/gpu_scheduler.py
- src/flux_llm_kb/database.py
- src/flux_llm_kb/model_runner.py
- src/flux_llm_kb/asr_server.py
- src/flux_llm_kb/embeddings.py
- src/flux_llm_kb/search_index.py
- src/flux_llm_kb/worker.py
- src/flux_llm_kb/event_worker.py
- src/flux_llm_kb/model_activity.py
- src/flux_llm_kb/settings_registry.py
- src/flux_llm_kb/sql/ with additive migrations
- targeted tests under tests/

The maintenance loop reuses the existing gpu-eviction-worker; docker-compose.yml does not need another service for this specification.

## Non-goals

- Killing or cancelling a running GPU task.
- Treating resident as zero execution headroom.
- Guessing owner or allocation from configured estimates.
- Treating a database row as runtime proof.
- Automatically learning a rising unattributed baseline.
- Automatically restarting model-runner, Paddle, ASR or Ollama.
- Changing the production GPU budget without separate evidence and approval.
- Replacing Vespa with PostgreSQL merely because Snowflake query embedding is unavailable.
