# GPU scheduler priority and capacity recovery

**Status:** approved for implementation and deployment on 2026-07-21.

## Purpose

Repair the live scheduler failure in which an indexing worker is treated as an
interactive CLI request, repeatedly retries against idle retained Snowflake
allocator memory, and obscures the actual priority of work.

The priority order is strict at safe scheduling boundaries:

1. MCP retrieval.
2. Document indexing and embedding.
3. Image OCR.
4. Image LLM enrichment (Ollama).
5. Video extraction.

Only an MCP retrieval may ever ask a runtime to cancel running GPU work. It may
do so only after that exact runtime has declared a fenced cooperative
cancellation capability and confirms the cancellation. At this rollout no GPU
path has that capability, so every request uses safe-boundary priority: a
higher-priority waiter prevents lower-priority work from receiving its *next*
lease or batch, the active operation completes, and the queue is re-evaluated.

## Evidence

- The search-index worker enters `caller_surface("cli")`, which made internal
  indexing interactive and gave its whole process one request ID.
- A current indexing job has repeatedly returned `vram_budget_exceeded` without
  embedding a batch, while Snowflake is resident with no in-flight work and the
  GPU has substantial retained allocator memory.
- Runtime inventory reported Snowflake present while the database cache reported
  it absent. The cache update timestamp can be newer than the observation it is
  about to persist, so a present update is discarded.
- A rejected PostgreSQL admission decision reduced its eviction-candidate
  decision to a string before broker delivery, so a valid idle-eviction request
  could be silently lost rather than queued.
- A target-only unload fence could call the process-wide allocator release while
  another model in the same runtime process was active. Unload and trim must
  both require the whole owner process to be idle.
- A generic `interactive` mapping elevated direct OCR, vision and ASR calls to
  the MCP lane. Retrieval may use that lane; every other task retains its
  declared work-family lane.
- Measured Snowflake allocation is materially larger than the old generic 2.5
  GiB estimate. Admission must use runtime measurements and conservative shape
  calibration, not a resident/zero-cost assumption.

## Decisions

| Concern | Decision |
| --- | --- |
| Caller classification | External MCP retrieval stays interactive. Worker executions receive a fresh, job-scoped `worker` context and are background by default. Direct interactive CLI retrieval remains interactive. |
| Priority lanes | Use explicit numerical lane priorities in the order above, with FIFO within a lane. A missing or untrusted task defaults to the lowest safe background lane, not MCP. |
| Pre-emption | MCP retrieval alone may request cancellation of lower-priority work, but only through a runtime that declares and acknowledges a safe cooperative cancellation capability. The practical audit below shows every current path is unsupported, so this release sends no cancellation request. Every lane therefore uses cooperative safe-boundary priority. |
| Allocator fence | Unload and allocator trim require the target fence *and* zero in-flight operations across the owner process, because framework cache release is process-wide. |
| Unfenced owners | Ollama `/api/ps` lists residency but cannot prove a generation is idle or acknowledge a fenced unload. Its VRAM remains real occupied capacity, but it is excluded from demand and idle automatic eviction. Existing broker rows are terminally skipped with `unload_capability_unavailable`; no `/api/generate keep_alive=0` request is sent. |
| Same-model pressure | Before rejecting an idle Snowflake request, ask its confirmed owner to trim releasable allocator cache while no operation is in flight. Reconcile afterwards; never unload a model that the requesting work requires merely to reload it. |
| Idle residency | Enable the existing verified idle-unload sweep with a 120-second default. It selects only runtime-confirmed, lease-free, in-flight-free owners and records the result. |
| Runtime cache | Preserve runtime observation time when marking omitted models absent, so a present record from that observation cannot be rejected as stale. Database residency remains a cache, not authority. |
| Retry behaviour | Retry logical jobs with a stable job/stage identity, bounded slow cooldown and structured capacity reason. Do not repeatedly create interactive leases or hot-retry an unchanged capacity state. |
| Calibration | Record runtime allocator samples for successful work and use request-shape-aware conservative high-water values. The old estimate remains a fallback only until enough samples exist. |

## Behavioural contract

At every admission or batch boundary, the scheduler ranks waiting work by lane
priority then FIFO. If an MCP request arrives while lower work is active, it
first reads the lower runtime's cancellation capability. A later cancellation
implementation must use a fenced cooperative request and an acknowledgement;
client disconnects, thread cancellation and process termination are not proof.
If that capability is absent, the MCP request waits for the active operation.
No lower task receives another batch before the MCP request receives its
decision. The same non-cancelling rule applies in order for each lower lane.

The practical audit covers each current GPU path. Runtime residency and
scheduler status expose this result, so the live rollout can verify it rather
than infer it from a design assertion.

| GPU work | Runtime/path inspected | Running-work cancellation | Current behaviour |
| --- | --- | --- | --- |
| Snowflake embedding and reranking | model-runner synchronous inference | Unsupported: no cooperative acknowledgement | priority at the next operation boundary |
| Image/document OCR | Paddle OCR synchronous inference | Unsupported: no cooperative acknowledgement | priority at the next operation boundary |
| Image LLM enrichment | Ollama HTTP request path | Unsupported: no cooperative acknowledgement | priority at the next operation boundary |
| ASR | faster-whisper/CTranslate2 transcription | Unsupported: no cooperative acknowledgement | priority at the next operation boundary |
| Video extraction | worker FFmpeg/frame path | Unsupported: no checkpointed acknowledgement; raw extraction is CPU-bound in the present command path | priority at the next operation boundary for its GPU sub-stages |

The status contract reports every listed task as `unsupported`, with
`mcp_only: true`, `cancellation_request: unavailable`, and
`fallback: priority_at_safe_boundary`. It will not add a forced cancellation
mechanism for a runtime that cannot guarantee model/allocator integrity and
clean job recovery.

Ollama's configured two-minute native keep-alive can still release an idle
vision model on its own timer, but that is not treated as a scheduler eviction.
Until Ollama provides a runtime-owned activity fence and unload acknowledgement,
its retained VRAM is charged to admission and no automatic unload is attempted.

For capacity decisions, the scheduler first reconciles runtime inventory. It
may evict only an idle model confirmed by its owner, generation and activity
sequence. If the target is already Snowflake and the owner is idle, it may trim
the framework cache; it cannot count a resident model as needing no execution
headroom. If reconciliation still shows material unowned memory, the scheduler
returns one slow, explicit reconciliation state rather than spinning retries or
losing ownership.

## Acceptance criteria

1. A search-index worker gets a job-scoped background context rather than an
   interactive process-scoped CLI context.
2. Queue order is MCP retrieval, document embedding, OCR, Ollama enrichment,
   then video extraction; FIFO applies within each lane.
3. A higher-priority waiter prevents a lower-priority next batch without
   cancelling a running lease or CUDA operation.
4. Only an MCP retrieval can ever request cancellation. Practical status probes
   show each current GPU path is unsupported, so no running operation is
   interrupted; every task instead uses normal safe-boundary priority and emits
   its capability telemetry.
5. An idle model cache can be trimmed before a same-model admission rejection,
   and a confirmed idle model is automatically unloaded after the configured
   timeout.
6. A fresh runtime observation leaves its present models present in the database
   cache even when the reconciliation also marks omitted models absent.
7. Retry telemetry links to a stable logical job and avoids an unbounded
   GPU-busy loop.
8. Focused and full test suites pass; after deployment, live probes show healthy
   endpoints, the configured priority/idle settings, a successful MCP retrieval,
   and no new model-runner 500 or database-bind errors.

## Non-goals

- Killing a process, cancelling a CUDA kernel, or forcibly interrupting a live
  request.
- Automatic service restarts to reclaim unattributed VRAM.
- Changing the physical GPU budget without measured evidence.
- Claiming per-model allocator attribution where the runtime exposes only a
  process-global allocator figure.
