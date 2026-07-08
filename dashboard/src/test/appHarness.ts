import { afterEach, beforeEach, vi } from "vitest";

export const health: any = {
  database: {
    ok: true,
    message: "database reachable",
    checks: {
      service: { ok: true, message: "database reachable", required: true, label: "API database" },
      host_published: { ok: true, message: "database reachable", required: true, label: "Host database" }
    }
  },
  runtime: {
    python: { ok: true },
    docker: { ok: true },
    git: { ok: true },
    postgresql: { ok: true }
  },
  watcher: { active_roots: 1, disabled_roots: 2, stale_count: 0 },
  jobs: { pending: 4, failed: 1, blocked: 2 },
  retrieval: { episodes: 9, asset_chunks: 12, search_index_records: 40 },
  acceleration: {
    capabilities: {
      nvidia: { ok: false, state: "missing", message: "nvidia-smi not found" },
      onnxruntime: { ok: true, providers: ["CPUExecutionProvider"] },
      local_model: { ok: false, state: "disabled", provider: "ollama" },
      watcher_backend: { ok: true, state: "available", provider: "watchdog", policy: "auto", selected_backend: "watchdog", native: true },
      cpu: { ok: true, count: 16 },
      memory: { ok: true, total_bytes: 34359738368 }
    },
    cache: {
      root: "D:/FluxLLMKB/private/cache",
      source: "install_root",
      directories: {
        models: "D:/FluxLLMKB/private/cache/models",
        ocr: "D:/FluxLLMKB/private/cache/ocr",
        asr: "D:/FluxLLMKB/private/cache/asr",
        vision: "D:/FluxLLMKB/private/cache/vision",
        thumbnails: "D:/FluxLLMKB/private/cache/thumbnails",
        parser: "D:/FluxLLMKB/private/cache/parser",
        embeddings: "D:/FluxLLMKB/private/cache/embeddings",
        temp: "D:/FluxLLMKB/private/cache/temp"
      }
    },
    worker_families: [
      { family: "media", resource_class: "gpu", configured_cap: 1, cap_available: 0, backpressure: "cap_reached", pending: 2, running: 1, blocked: 1, failed: 0, oldest_pending_age_seconds: 120, retrying_locked: 2, blocked_locked: 1, avg_duration_ms: 24, p95_duration_ms: 95, ocr_cache_hits: 6, ocr_cache_misses: 2, asr_cache_hits: 4, asr_cache_misses: 1, asr_segments: 9, vision_cache_hits: 5, vision_cache_misses: 2, vision_descriptions: 3, vision_blocked_dependency_count: 1, decorative_image_skips: 4, frame_sample_count: 6, thumbnail_cache_hits: 7, thumbnail_cache_misses: 8, parser_cache_hits: 3, parser_cache_misses: 1, manifest_skipped_unchanged: 5, embedding_vectors: 10, embedding_skipped_unchanged: 2, embedding_batches: 1, embedding_cache_hits: 3, embedding_cache_misses: 4 },
      { family: "office", resource_class: "cpu", configured_cap: 2, pending: 3, running: 0, blocked: 0, failed: 1, avg_duration_ms: 12, p95_duration_ms: 40 }
    ],
    benchmarks: {
      history: [
        {
          id: "run-2",
          fixture: "image-heavy",
          mode: "scan",
          label: "after-deploy",
          status: "completed",
          file_count: 10,
          elapsed_ms: 1000,
          throughput_files_per_second: 10,
          previous_elapsed_delta_ms: -250,
          previous_throughput_delta: 2,
          warm_state: "warm",
          pass_index: 2,
          hash_parallelism: 4,
          worker_count: 3,
          manifest_skipped_unchanged: 8,
          cache_hits: 7,
          cache_misses: 3,
          scope_type: "monitored_root",
          deployment_label: "desktop-after",
          model_telemetry: {
            local_model: { state: "disabled", provider: "ollama" },
            blocked_dependency_count: 2
          }
        }
      ]
    },
    docker: {
      ok: true,
      state: "available",
      totals: {
        memory_usage_bytes: 1280 * 1024 * 1024,
        memory_limit_bytes: 5 * 1024 * 1024 * 1024,
        size_rw_bytes: 192 * 1024 * 1024,
        block_io_read_bytes: 3 * 1024 * 1024,
        block_io_write_bytes: 72 * 1024 * 1024
      },
      containers: [
        {
          service: "api",
          container_name: "flux-llm-kb-api",
          status: "running",
          running: true,
          cpu_percent: 12.34,
          memory_usage_bytes: 512 * 1024 * 1024,
          memory_limit_bytes: 2 * 1024 * 1024 * 1024,
          memory_swap_limit_bytes: 2 * 1024 * 1024 * 1024,
          memory_percent: 25,
          block_io_read_bytes: 1024 * 1024,
          block_io_write_bytes: 64 * 1024 * 1024,
          size_rw_bytes: 128 * 1024 * 1024,
          pids: 42
        },
        {
          service: "postgres",
          container_name: "flux-llm-kb-postgres",
          status: "running",
          running: true,
          cpu_percent: 1.5,
          memory_usage_bytes: 768 * 1024 * 1024,
          memory_limit_bytes: 3 * 1024 * 1024 * 1024,
          memory_swap_limit_bytes: 3 * 1024 * 1024 * 1024,
          memory_percent: 25,
          block_io_read_bytes: 2 * 1024 * 1024,
          block_io_write_bytes: 8 * 1024 * 1024,
          size_rw_bytes: 64 * 1024 * 1024,
          pids: 19
        }
      ]
    }
  },
  workers: {
    active: 1,
    components: [
      { name: "corpus-worker:docker", status: "running", heartbeat_age_seconds: 2 }
    ]
  },
  recent_errors: ["ffprobe command not found"],
  host_agent: { status: "running", browse_supported: true },
  codex: {
    status: "configured_not_installed",
    configured: true,
    installed: false,
    hooks_available: true,
    discoverable: false,
    restart_required: true,
    hook_policy: {
      status: "active",
      enabled: true,
      preflight_enabled: true,
      capture_enabled: true,
      token_budget: 900,
      recent_events: [
        {
          event_type: "codex_hook.preflight_injected",
          created_at: "2026-06-23T10:00:00+00:00",
          details: { reason: "matched" }
        }
      ]
    },
    mcp: {
      configured: true,
      command: "python",
      cwd: "D:/FluxLLMKB/app",
      enabled: true,
      dependency_available: true,
      message: "ready"
    }
  }
};

export const crawl: any = {
  roots: [
    {
      name: "docs",
      root_path: "E:/Flux Docs",
      enabled: true,
      recursive: true,
      watch_enabled: true,
      trust_rank: 720,
      include_globs: ["**/*.md"],
      exclude_globs: ["private/**"],
      max_inline_bytes: 131072,
      heavy_threshold_bytes: 5242880
    }
  ],
  root_summaries: [
    {
      name: "docs",
      root_path: "E:/Flux Docs",
      enabled: true,
      recursive: true,
      watch_enabled: true,
      trust_rank: 720,
      include_globs: ["**/*.md"],
      exclude_globs: ["private/**"],
      max_inline_bytes: 131072,
      heavy_threshold_bytes: 5242880,
      state: "watching",
      watcher: { status: "running", heartbeat_age_seconds: 3 },
      asset_counts: { total: 4, indexed: 3, queued: 1, duplicate_suppressed: 1, deleted: 0 },
      job_counts: { pending: 1, blocked: 0, failed: 0, running: 0 },
      latest_crawl: { status: "completed", files_seen: 4, files_changed: 1, jobs_queued: 1 },
      recent_assets: [
        { path: "README.md", file_kind: "text", status: "indexed", size_bytes: 1200 },
        { path: "clip.mp4", file_kind: "video", status: "queued", size_bytes: 8200000 }
      ],
      recent_jobs: [{ id: "job-1", job_type: "corpus_extract_video", status: "blocked_missing_dependency", path: "clip.mp4" }],
      recent_errors: ["ffprobe command not found"]
    }
  ],
  status: { active_watch_roots: 1, disabled_watch_roots: 0, recent_errors: ["ffprobe command not found"] }
};

export const mail: any = {
  enabled_profiles: 2,
  exported_messages: 10,
  errored_messages: 1,
  scheduler: {
    counts: {
      due: 1,
      queued: 1,
      claimed: 0,
      running: 1,
      failed: 1,
      blocked_auth: 1,
      backoff: 1
    },
    recent_runs: [
      {
        id: "run-backoff",
        profile_name: "gmail-capture",
        status: "backoff",
        trigger: "schedule",
        attempt_count: 2,
        messages_seen: 0,
        messages_exported: 0,
        last_error: "IMAP search timed out",
        next_attempt_at: "2026-06-21T13:20:00+00:00",
        drift_seconds: 300,
        missed_runs: 1,
        started_at: "2026-06-21T13:16:00+00:00",
        finished_at: "2026-06-21T13:16:10+00:00"
      },
      {
        id: "run-auth",
        profile_name: "gmail-capture",
        status: "blocked_auth_required",
        trigger: "schedule",
        attempt_count: 1,
        messages_seen: 0,
        messages_exported: 0,
        last_error: "Gmail OAuth is not configured for this mail profile",
        next_attempt_at: "2026-06-22T13:16:00+00:00",
        drift_seconds: 0,
        missed_runs: 0,
        started_at: "2026-06-21T13:16:00+00:00",
        finished_at: "2026-06-21T13:16:01+00:00"
      }
    ],
    diagnostics: [
      {
        code: "mail.scheduler_backoff",
        message: "gmail-capture is waiting for retry backoff",
        severity: "warning",
        component: "mail",
        stage: "imap_scheduler",
        target: { type: "mail_profile", id: "gmail-capture" }
      }
    ]
  },
  oauth: {
    profiles: [
      { profile_name: "gmail-capture", status: "blocked_auth_required", has_refresh_token: false }
    ]
  },
  post_process: {
    recent_events: [
      {
        id: "post-process-1",
        profile_name: "gmail-capture",
        policy: "remove_label",
        action: "gmail_remove_label",
        status: "planned",
        dry_run: true,
        created_at: "2026-06-23T10:20:00+00:00"
      }
    ]
  },
  profiles: [
    {
      name: "gmail-capture",
      source_type: "imap",
      account: "me@gmail.com",
      folder_paths: ["FluxCapture"],
      sync_enabled: true,
      sync_interval_seconds: 900,
      last_sync_at: "2026-06-21T13:00:00+00:00",
      next_sync_at: "2026-06-21T13:15:00+00:00",
      metadata: {}
    },
    {
      name: "outlook-catchup",
      source_type: "outlook_com",
      account: null,
      folder_paths: ["Mailbox - Me/Inbox/Flux"],
      sync_enabled: false,
      sync_interval_seconds: 1800,
      last_sync_at: null,
      next_sync_at: null,
      metadata: {}
    }
  ]
};

export const outlook: any = {
  host: {
    host_id: "default",
    status: "host_offline",
    command: "flux-kb outlook-host run",
    heartbeat_at: null,
    last_error: null
  },
  profiles: [mail.profiles[1]],
  pending_requests: []
};

export const settings: any = [
  {
    key: "retrieval.token_budget",
    value: 1200,
    source: "default",
    sensitive: false,
    category: "retrieval",
    apply_mode: "live",
    read_only: false,
    affected_components: ["retrieval"],
    description: "Default context brief token budget."
  },
  {
    key: "retrieval.embedding_model",
    value: "Snowflake/snowflake-arctic-embed-l-v2.0",
    source: "default",
    sensitive: false,
    category: "retrieval",
    apply_mode: "reindex_required",
    read_only: false,
    affected_components: ["retrieval", "worker"],
    description: "Snowflake embedding model used for Vespa search-index sync."
  },
  {
    key: "dashboard.poll_interval_seconds",
    value: 1,
    source: "default",
    sensitive: false,
    category: "dashboard",
    apply_mode: "live",
    read_only: false,
    affected_components: ["dashboard"],
    description: "Dashboard polling interval."
  },
  {
    key: "codex.hooks.enabled",
    value: true,
    source: "default",
    sensitive: false,
    category: "codex",
    apply_mode: "reload",
    read_only: false,
    affected_components: ["hooks", "dashboard"],
    description: "Enable Flux Codex hook policy evaluation."
  }
];

export type DashboardTestState = {
  mailSyncPayload: any;
  searchPayload: any;
  explainPayload: any;
  explainRequestPayload: any;
  resultDetailPayload: any;
  fileActionPayload: any;
  healthPayload: any;
  crawlPayload: any;
  mailPayload: any;
  jobsPayload: any;
  modelActivityPayload: any;
  crawlSyncErrorPayload: any;
  reviewPayload: any;
  captureReviewPayload: any;
  captureReviewRequestUrl: string | undefined;
  captureReviewDecisionPayload: any;
  captureReviewIngestPayload: any;
  auditPayload: any;
  graphPayload: any;
  claimTransitionPayload: any;
  postProcessDryRunPayload: any;
  mailProfileDeleteResponse: any;
  retentionPoliciesPayload: any;
  retentionQualityPayload: any;
  retentionPolicyUpdatePayload: any;
  benchmarkRunPayload: any;
  reliabilityRunPayload: any;
  codeFeedbackPayload: any;
  codeSearchRequestUrl: string | undefined;
  codeSymbolRequestUrl: string | undefined;
  retrievalBenchmarkRunPayload: any;
  retrievalBenchmarkHistoryPayload: any;
  diagnosticsActionPayload: any;
  automationStatusPayload: any;
  automationRunPayload: any;
  automationActionsPayload: any;
  governanceActionsPayload: any;
  governanceDigestPayload: any;
  governancePolicyPayload: any;
  governanceRunPayload: any;
  governanceApplyPayload: any;
  governanceRecoverPayload: any;
  outlookCancelRequests: string[];
  corpusCancelRequests: string[];
  corpusRetryRequests: string[];
  corpusDeleteRequests: string[];
  corpusRestoreRequests: string[];
  corpusJobFileActionRequests: Array<{ url: string; body: unknown }>;
  corpusJobFileActionPayload: Record<string, any>;
  snapshotRequestUrls: string[];
  webSockets: MockDashboardWebSocket[];
  jobsRequestUrls: string[];
  modelActivityRequestUrls: string[];
  jobToolInvocationPayload: any;
  jobToolInvocationRequestUrls: string[];
  pendingFetchResponses: Record<string, DeferredResponse>;
};

export const dashboardTestState = {} as DashboardTestState;
const state = dashboardTestState;

export class MockDashboardWebSocket {
  static instances: MockDashboardWebSocket[] = [];
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  url: string;
  readyState = 0;
  sent: string[] = [];
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    MockDashboardWebSocket.instances.push(this);
    queueMicrotask(() => {
      this.readyState = 1;
      this.onopen?.(new Event("open"));
    });
  }

  send(payload: string) {
    this.sent.push(payload);
  }

  close() {
    this.readyState = 3;
    this.onclose?.(new CloseEvent("close"));
  }

  emit(payload: unknown) {
    this.onmessage?.({ data: JSON.stringify(payload) } as MessageEvent);
  }
}

export function setupDashboardTest(): void {
  beforeEach(() => {
    MockDashboardWebSocket.instances = [];
    outlook.pending_requests = [];
    state.healthPayload = health;
    state.crawlPayload = JSON.parse(JSON.stringify(crawl));
    state.mailPayload = JSON.parse(JSON.stringify(mail));
    state.jobsPayload = {
      jobs: [
        {
          id: "job-pdf",
          job_type: "corpus_extract_pdf",
          status: "retrying_locked",
          payload: {
            root_name: "docs",
            path: "docs/open.pdf",
            asset_id: "asset-1",
            source_id: "source-1"
          },
          attempts: 2,
          last_error: "file is locked by another process",
          created_at: "2026-06-23T06:00:00+00:00",
          updated_at: "2026-06-23T06:04:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["retrying_locked", "failed", "blocked_missing_dependency", "cancelled_operator"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf"]
      }
    };
    state.modelActivityPayload = {
      window_minutes: 60,
      limit: 50,
      offset: 0,
      total_count: 3,
      has_next: false,
      page_count: 1,
      active_count: 1,
      recent_count: 3,
      last_event_at: "2026-07-03T01:26:06+00:00",
      service_breakdown: [
        { service: "model-runner", count: 2, active: 1, failures: 0 },
        { service: "ollama", count: 1, active: 0, failures: 1 }
      ],
      class_breakdown: [
        { activity_class: "retrieval", count: 2 },
        { activity_class: "vision_ocr", count: 1 }
      ],
      events: [
        {
          id: "event-rerank",
          service: "model-runner",
          endpoint: "/v1/rerank",
          action: "rerank",
          activity_class: "retrieval",
          caller_surface: "mcp",
          model: "Qwen/Qwen3-Reranker-4B",
          status: "completed",
          started_at: "2026-07-03T01:25:58+00:00",
          completed_at: "2026-07-03T01:26:00+00:00",
          duration_ms: 1842,
          error_class: null,
          error_message: null
        },
        {
          id: "event-ollama",
          service: "ollama",
          endpoint: "/api/generate",
          action: "vision_generate",
          activity_class: "vision_ocr",
          caller_surface: "worker",
          model: "qwen3-vl:8b",
          status: "failed",
          started_at: "2026-07-03T01:24:58+00:00",
          completed_at: "2026-07-03T01:25:00+00:00",
          duration_ms: 2042,
          error_class: "RuntimeError",
          error_message: "redacted failure"
        }
      ],
      scheduler: {
        mode: "postgres",
        running_count: 1,
        waiting_count: 1,
        recent_count: 6,
        rejections: 2,
        timeouts: 1,
        evictions_recent_count: 1,
        last_eviction_at: "2026-07-03T01:20:00+00:00",
        oldest_wait_age_ms: 20000,
        last_activity_at: "2026-07-03T01:26:00+00:00",
        resident_models: [
          {
            service: "model-runner",
            model: "Snowflake/snowflake-arctic-embed-l-v2.0",
            task_type: "embedding",
            last_used_at: "2026-07-03T01:26:00+00:00"
          }
        ],
        live_gpu_memory: { available: true, used_mb: 8120, total_mb: 16380 }
      }
    };
    state.crawlSyncErrorPayload = undefined;
    state.mailSyncPayload = { profiles: [{ profile: "gmail-capture", status: "completed", exported: 0 }], count: 1 };
    state.searchPayload = [
      {
        kind: "corpus_chunk",
        title: "Dashboard Operations",
        excerpt: "dashboard search result",
        score: 0.91,
        snippet: {
          text: "Dashboard search result with highlighted operations.",
          matched_terms: ["dashboard", "operations"],
          highlights: [
            { term: "dashboard", start: 0, end: 9 },
            { term: "operations", start: 41, end: 51 }
          ],
          source: "summary",
          source_path: "docs/operations.md"
        },
        retrieval_explanation: {
          score: 0.91,
          streams: ["corpus_lexical", "vespa_hybrid"],
          raw_scores: { corpus_lexical: 0.7, vespa_hybrid: 0.3 },
          scope: { label: "local", root_name: "docs" },
          corpus: { source_path: "docs/operations.md", root_name: "docs", trust_rank: 450, duplicate_count: 2, related_evidence_count: 0 },
          lifecycle: { state: "active", score: 0.88, explanation: { penalties: { state: 1, retention: 0.6 } } },
          suppression: {
            exact_duplicates: { suppressed_count: 2, reason: "exact_content_duplicate", canonical_source_path: "docs/operations.md" },
            version_family: { suppressed_count: 1, reason: "same_document_version_family", canonical_source_path: "docs/operations.md" },
            semantic_duplicates: { suppressed_count: 2, reason: "semantic_near_duplicate", threshold: 0.86 }
          }
        }
      }
    ];
    state.explainPayload = undefined;
    state.explainRequestPayload = undefined;
    state.reviewPayload = {
      counts: {
        total: 2,
        current: 1,
        needs_review: 1,
        stale: 1,
        contradicted: 0,
        superseded: 0,
        retired: 0,
        retention_action: 1
      },
      claims: [
        {
          id: "claim-stale",
          subject_entity_id: "entity-1",
          subject: { id: "entity-1", type: "project", name: "Flux" },
          predicate: "uses",
          object_text: "PostgreSQL",
          confidence: 0.8,
          lifecycle_state: "stale",
          retention_action: "deprioritize",
          review_reasons: ["stale", "retention:deprioritize"],
          updated_at: "2026-06-23T10:00:00+00:00",
          lifecycle: { score: 0.42, current: false, audit_visible: true, audit_events: [], related_claims: [] }
        }
      ]
    };
    state.captureReviewPayload = {
      jobs: [
        {
          id: "job-review",
          job_type: "codex_backfill",
          status: "pending",
          payload: { status: "pending_review", path: "sessions/session.json" },
          updated_at: "2026-06-23T10:05:00+00:00"
        }
      ]
    };
    state.captureReviewDecisionPayload = undefined;
    state.captureReviewIngestPayload = undefined;
    state.captureReviewRequestUrl = undefined;
    state.auditPayload = {
      events: [
        {
          id: "audit-old",
          event_type: "capture.review_rejected",
          actor: "dashboard",
          target_id: "job-old",
          details: { decision: "reject", rationale: "duplicate capture", status: "rejected" },
          created_at: "2026-06-23T09:05:00+00:00"
        }
      ]
    };
    state.graphPayload = {
      start_entity_id: "entity-1",
      edges: [
        {
          relation_id: "rel-1",
          from_entity_id: "entity-1",
          from_entity: { type: "project", name: "Flux" },
          to_entity_id: "entity-2",
          to_entity: { type: "system", name: "PostgreSQL" },
          relation_type: "depends_on",
          confidence: 0.7,
          depth: 1,
          path: ["entity-1", "entity-2"]
        }
      ]
    };
    state.claimTransitionPayload = { id: "claim-stale", lifecycle_state: "confirmed" };
    state.postProcessDryRunPayload = undefined;
    state.retentionPoliciesPayload = {
      policies: [
        { memory_class: "claim", half_life_days: 120, min_confidence: 0.35, action: "review", updated_by: "system" },
        { memory_class: "episode", half_life_days: 180, min_confidence: 0.25, action: "deprioritize", updated_by: "system" },
        { memory_class: "corpus", half_life_days: 365, min_confidence: 0.2, action: "review", updated_by: "system" }
      ]
    };
    state.retentionQualityPayload = {
      summary: {
        total: 3,
        needs_review: 2,
        by_class: { claim: 1, episode: 1, corpus: 1 },
        by_bucket: { healthy: 1, review: 1, deprioritize: 1, retire: 0 }
      },
      candidates: [
        {
          id: "claim-stale",
          memory_class: "claim",
          label: "Flux uses PostgreSQL",
          reason: "retention:deprioritize",
          quality_bucket: "deprioritize",
          confidence: 0.8,
          lifecycle_state: "stale",
          retention_action: "deprioritize",
          updated_at: "2026-06-23T10:00:00+00:00"
        },
        {
          id: "asset-blocked",
          memory_class: "corpus",
          label: "blocked.pdf",
          reason: "blocked_missing_dependency",
          quality_bucket: "review",
          confidence: 0.2,
          extraction_status: "blocked_missing_dependency",
          updated_at: "2026-06-23T09:00:00+00:00"
        }
      ]
    };
    state.governanceActionsPayload = {
      telemetry: {
        total: 3,
        by_status: { proposed: 1, blocked: 1, applied: 1 },
        by_risk: { low: 1, high: 1, medium: 1 },
        by_mutation: { mutated: 1, not_mutated: 2 }
      },
      actions: [
        {
          id: "gov-action-1",
          action: "stale_tag",
          target_type: "claim",
          target_id: "claim-stale",
          memory_class: "claim",
          risk: "low",
          status: "proposed",
          source: "retention_quality",
          rationale: { summary: "stale evidence", guardrails: { gate_status: "ready", protected: false } },
          evidence: { lifecycle_state: "stale" }
        },
        {
          id: "gov-blocked-1",
          action: "retire",
          target_type: "claim",
          target_id: "claim-current",
          risk: "high",
          status: "blocked",
          source: "retention_quality",
          rationale: { summary: "protected memory", guardrails: { protected: true } }
        },
        {
          id: "gov-applied-1",
          action: "deprioritize",
          target_type: "claim",
          target_id: "claim-old",
          risk: "low",
          status: "applied",
          source: "retention_quality",
          rationale: { summary: "low confidence", guardrails: { gate_status: "ready" } }
        }
      ]
    };
    state.governanceDigestPayload = {
      digest: {
        summary: { new_proposals: 1, blocked_proposals: 1, recoverable_actions: 1, gate_status: "ready" },
        recommendations: [{ action: "inspect_blocked_governance", count: 1 }]
      },
      settings_mutated: false
    };
    state.governancePolicyPayload = {
      policy: { min_shadow_precision: 0.8, auto_apply_enabled: false, auto_apply_risk_ceiling: "low" },
      settings_mutated: false
    };
    state.governanceRunPayload = undefined;
    state.governanceApplyPayload = undefined;
    state.governanceRecoverPayload = undefined;
    state.retentionPolicyUpdatePayload = undefined;
    state.benchmarkRunPayload = undefined;
    state.reliabilityRunPayload = undefined;
    state.codeFeedbackPayload = undefined;
    state.codeSearchRequestUrl = undefined;
    state.codeSymbolRequestUrl = undefined;
    state.retrievalBenchmarkRunPayload = undefined;
    state.mailProfileDeleteResponse = {
      profile_name: "gmail-capture",
      root_name: "mail-gmail-capture",
      deleted: true,
      profile: { deleted: true },
      corpus_root: { deleted: true },
      search_index: { deleted: 2, records_deleted: 2 },
      sidecars: { deleted: 1, missing: 0, blocked: 0, failed: 0, errors: [] },
      spool: { status: "deleted", deleted: true, blocked_reason: null }
    };
    state.diagnosticsActionPayload = undefined;
    state.automationRunPayload = undefined;
    state.automationStatusPayload = {
      settings_mutated: false,
      policy: {
        enabled: false,
        mode: "guarded",
        interval_seconds: 1800,
        next_run_at: "2026-06-26T11:00:00+00:00"
      },
      last_run: {
        id: "automation-run-1",
        status: "completed",
        started_at: "2026-06-26T10:00:00+00:00",
        completed_at: "2026-06-26T10:01:00+00:00",
        summary: { eligible: 5, applied: 3, blocked: 2, settings_mutated: false }
      },
      eligible_actions: [
        {
          action: "refresh_retrieval_evidence",
          label: "Refresh retrieval evidence",
          status: "eligible",
          risk: "low",
          reason: "Benchmark evidence is stale."
        },
        {
          action: "ingest_approved_capture",
          label: "Ingest approved captures",
          status: "eligible",
          risk: "low",
          reason: "Approved captures are waiting."
        }
      ],
      manual_required: [
        { action: "delete", label: "Delete or purge data", reason: "Destructive actions require a person." },
        { action: "oauth", label: "OAuth setup", reason: "OAuth consent must stay manual." }
      ],
      recent_actions: [
        {
          id: "automation-action-1",
          action: "refresh_retrieval_evidence",
          status: "applied",
          risk: "low",
          source: "retrieval",
          evidence: { suite: "standard" },
          result: { settings_mutated: false }
        }
      ]
    };
    state.automationActionsPayload = {
      settings_mutated: false,
      status: "all",
      actions: [
        {
          id: "automation-action-1",
          action: "refresh_retrieval_evidence",
          status: "applied",
          risk: "low",
          source: "retrieval",
          evidence: { suite: "standard" },
          result: { settings_mutated: false }
        }
      ]
    };
    state.retrievalBenchmarkHistoryPayload = {
      suite: "standard",
      runs: [
        {
          id: "retrieval-run-1",
          suite: "standard",
          label: "baseline",
          status: "completed",
          query_count: 5,
          passed_count: 4,
          failed_count: 1,
          metrics: {
            top1_accuracy: 0.8,
            precision_at_3: 0.9,
            recall_at_5: 0.85,
            brief_dilution: 0.2,
            scope_pass_count: 5,
            suppression_pass_count: 4
          },
          metric_deltas: {
            top1_accuracy: 0.1,
            brief_dilution: -0.05
          },
          calibration_summary: {
            confidence_bands: { high: 3, medium: 1, low: 1 },
            semantic_thresholds: [
              { threshold: 0.86, evaluated_count: 4, false_positive_count: 0, false_negative_count: 1, pass_count: 3 }
            ]
          },
          case_results: [
            {
              case_id: "scope-filter",
              category: "current_only",
              status: "failed",
              expected_ids: ["chunk-scope"],
              observed_ids: ["chunk-other"],
              reasons: ["top1_miss", "scope_miss"],
              confidence_band: "low",
              failure_details: [
                { reason: "top1_miss", message: "Expected evidence was not ranked first." },
                { reason: "scope_miss", message: "The top result came from an unexpected retrieval scope." }
              ]
            }
          ],
          recommendations: {
            settings_mutated: false,
            candidates: [
              {
                kind: "semantic_duplicate_threshold",
                threshold: 0.86,
                evidence_count: 4,
                false_positive_count: 0,
                false_negative_count: 1,
                rationale: "Synthetic semantic duplicate calibration passed for 3/4 cases at threshold 0.86."
              }
            ]
          }
        }
      ]
    };
    state.outlookCancelRequests = [];
    state.corpusCancelRequests = [];
    state.corpusRetryRequests = [];
    state.corpusDeleteRequests = [];
    state.corpusRestoreRequests = [];
    state.corpusJobFileActionRequests = [];
    state.corpusJobFileActionPayload = { state: "opened", path: "E:/Flux Docs/docs/failed.pdf" };
    state.snapshotRequestUrls = [];
    state.webSockets = MockDashboardWebSocket.instances;
    state.jobsRequestUrls = [];
    state.modelActivityRequestUrls = [];
    state.jobToolInvocationRequestUrls = [];
    state.jobToolInvocationPayload = { job_id: "job-pdf", invocations: [] };
    state.pendingFetchResponses = {};
    state.resultDetailPayload = {
      logical_kind: "file",
      title: "Dashboard Operations",
      asset_id: "asset-1",
      metadata: { path: "docs/dashboard.md", canonical_path: "E:/Flux Docs/docs/dashboard.md", status: "indexed" },
      preview: { available: true, text: "dashboard search result", chunks: [] },
      actions: {
        copy_path: { available: true, path: "E:/Flux Docs/docs/dashboard.md" },
        open: { available: true },
        reveal: { available: true }
      },
      related_evidence: [],
      provenance: []
    };
    state.fileActionPayload = { state: "opened", asset_id: "asset-1", action: "open" };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith("/api/dashboard/snapshot")) {
        state.snapshotRequestUrls.push(url);
        const pending = state.pendingFetchResponses[url] ?? state.pendingFetchResponses["/api/dashboard/snapshot"];
        if (pending) return pending.promise;
        return json({
          generated_at: "2026-07-08T10:00:00+00:00",
          health: state.healthPayload,
          crawl: state.crawlPayload,
          jobs: state.jobsPayload,
          retrieval: { retrieval: health.retrieval, duplicate_assets: 0 },
          modelActivity: state.modelActivityPayload,
          mail: state.mailPayload,
          outlook,
          settings
        });
      }
      if (url === "/api/dashboard/health") {
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        return json(state.healthPayload);
      }
      if (url === "/api/dashboard/crawl") {
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        return json(state.crawlPayload);
      }
      if (url.startsWith("/api/dashboard/model-activity")) {
        state.modelActivityRequestUrls.push(url);
        return json(state.modelActivityPayload);
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.includes("/tool-invocations")) {
        state.jobToolInvocationRequestUrls.push(url);
        return json(state.jobToolInvocationPayload);
      }
      if (url.startsWith("/api/dashboard/jobs") && url !== "/api/dashboard/jobs/retry-blocked-asr" && !url.endsWith("/cancel") && !url.endsWith("/retry") && !url.endsWith("/delete-request") && !url.endsWith("/file-actions")) {
        state.jobsRequestUrls.push(url);
        return json(state.jobsPayload);
      }
      if (url === "/api/dashboard/retrieval-stats") return json({ retrieval: health.retrieval, duplicate_assets: 0 });
      if (url === "/api/mail/status") return json(state.mailPayload);
      if (url === "/api/outlook-host/status") return json(outlook);
      if (url === "/api/host/status") return json({ status: "running", browse_supported: true, platform: "Windows" });
      if (url === "/api/host/browse-folder") return json({ status: "selected", path: "E:\\Temp\\watch-test" });
      if (url === "/api/settings") return json(settings);
      if (url.startsWith("/api/settings/") && init?.method === "PUT") {
        const key = decodeURIComponent(url.replace("/api/settings/", ""));
        return json({ ...settings.find((row: { key: string }) => row.key === key), source: "db", value: JSON.parse(String(init.body)).value });
      }
      if (url.startsWith("/api/settings/") && url.endsWith("/reset")) return json({ status: "reset" });
      if (url === "/api/settings/apply") return json({ acknowledged: 1 });
      if (url === "/api/acceleration/reliability") {
        return json({
          readiness: "partial",
          settings_mutated: false,
          evidence_age_hours: 3,
          checks: [
            { check: "synthetic_reliability", status: "ok", summary: "Synthetic reliability evidence is current." },
            { check: "scoped_host_cloud", status: "missing", summary: "Run scoped host/cloud calibration for the selected root." },
            { check: "worker_tuning", status: "ok", summary: "Tuning evidence is available." }
          ],
          watcher: { backend: "watchdog", event_count: 2 },
          workers: { families: [{ family: "media", backpressure: "cap_reached", pending: 2 }] },
          candidates: [
            {
              setting: "crawler.hash_parallelism",
              current: 1,
              candidate: 2,
              evidence_state: "needs_comparison",
              follow_up_command: "flux-kb acceleration benchmark run --scenario tuning"
            }
          ]
        });
      }
      if (url === "/api/acceleration/reliability/roots") {
        return json({
          settings_mutated: false,
          totals: { ready: 1, partial: 1, blocked: 0, not_run: 1, total: 3 },
          roots: [
            {
              root_name: "docs",
              readiness: "ready",
              latest_benchmark: { id: "bench-docs", scenario: "host_cloud" },
              required_action: "No action required."
            },
            {
              root_name: "code",
              readiness: "partial",
              latest_benchmark: null,
              required_action: "Run scoped host/cloud reliability evidence and clear blocked or pending work."
            }
          ]
        });
      }
      if (url === "/api/acceleration/evidence") {
        return json({
          settings_mutated: false,
          readiness: "partial",
          root_readiness: { ready: 1, partial: 1, blocked: 0, not_run: 1, total: 3 },
          top_blockers: [{ section: "reliability", severity: "warning", root_name: "code", summary: "Run scoped host/cloud reliability evidence." }],
          manual_follow_ups: [{ setting: "crawler.hash_parallelism", command: "flux-kb acceleration benchmark run --scenario tuning" }],
          code_gaps: [{ category: "missing_symbol", count: 2, summary: "Code feedback reported misses." }],
          gates: {
            vss_snapshot: { state: "hold", reason: "VSS remains design-only." },
            provider_acceleration: { state: "hold", reason: "Provider acceleration remains blocked." }
          }
        });
      }
      if (url === "/api/acceleration/reliability/run" && init?.method === "POST") {
        state.reliabilityRunPayload = JSON.parse(String(init.body));
        if ((state.reliabilityRunPayload as { scope?: string }).scope === "all_roots") {
          return json({
            settings_mutated: false,
            totals: { ready: 2, partial: 0, blocked: 0, not_run: 0, total: 2 },
            roots: [
              { root_name: "docs", readiness: "ready", latest_benchmark: { id: "bench-docs" }, required_action: "No action required." }
            ]
          });
        }
        return json({ readiness: "ready", settings_mutated: false, checks: [] });
      }
      if (url === "/api/acceleration/reliability/root/docs") {
        return json({
          root_name: "docs",
          readiness: "partial",
          blockers: { blocked_assets: 1, pending_jobs: 2 },
          latest_benchmark: { id: "root-run", scenario: "host_cloud" }
        });
      }
      if (url === "/api/acceleration/benchmarks/run" && init?.method === "POST") {
        state.benchmarkRunPayload = JSON.parse(String(init.body));
        return json({
          fixture: "all",
          mode: "scan",
          scenario: (state.benchmarkRunPayload as { scenario?: string }).scenario ?? "standard",
          runs: [],
          diagnostics: [{ check: "tuning", status: "ok", evidence: { warm_runs: 1 } }],
          recommendations: {
            settings_mutated: false,
            candidates: [
              {
                setting: "crawler.hash_parallelism",
                current: 1,
                candidate: 4,
                reason: "Warm scan improved with bounded parallel hashing.",
                requires_manual_apply: true
              }
            ]
          }
        });
      }
      if (url === "/api/code/status") {
        return json({
          totals: { asset_count: 4, symbol_count: 7, reference_count: 9, fallback_count: 1 },
          feedback_summary: { totals: { event_count: 2 }, rows: [{ miss_category: "missing_symbol", root_name: "app", event_count: 2 }] },
          gaps: [{ category: "missing_symbol", count: 2, summary: "Code feedback reported missing symbol misses." }],
          roots: [
            {
              root_name: "app",
              health: "partial",
              symbol_count: 7,
              reference_count: 9,
              fallback_count: 1,
              languages: { python: 4, typescript: 3 },
              parser_statuses: { parsed: 6, fallback: 1 }
            }
          ]
        });
      }
      if (url === "/api/code/feedback" && init?.method === "POST") {
        state.codeFeedbackPayload = JSON.parse(String(init.body));
        return json({ id: "feedback-1", settings_mutated: false });
      }
      if (url.startsWith("/api/code/search")) {
        state.codeSearchRequestUrl = url;
        return json({
          settings_mutated: false,
          results: [
            {
              symbol: "OrderService.build_invoice",
              relationship: "call",
              language: "python",
              path: "tests/test_orders.py",
              line_start: 7,
              line_end: 9,
              is_generated: false
            }
          ]
        });
      }
      if (url.startsWith("/api/code/symbols")) {
        state.codeSymbolRequestUrl = url;
        return json({
          settings_mutated: false,
          query: "OrderService.build_invoice",
          matches: [
            {
              symbol: "OrderService.build_invoice",
              symbol_kind: "method",
              language: "python",
              path: "src/orders.py",
              line_start: 5,
              line_end: 7
            }
          ],
          references: [
            {
              target: "OrderService.build_invoice",
              relationship: "test",
              language: "python",
              path: "tests/test_orders.py",
              source_symbol: "test_build_invoice_returns_ready_status"
            }
          ]
        });
      }
      if (url === "/api/code/feedback/summary") {
        return json({ settings_mutated: false, totals: { event_count: 2 }, rows: [{ miss_category: "missing_symbol", root_name: "app", event_count: 2 }] });
      }
      if (url.startsWith("/api/diagnostics/all")) {
        return json({
          section: "all",
          settings_mutated: false,
          counts: { watcher_events: 2, worker_families: 1, blocked_jobs: 1, mail_sync_runs: 3 },
          sections: { workers: { families: [{ family: "office", pending: 2, blocked_locked: 1 }] } },
          items: [
            {
              section: "jobs",
              severity: "warning",
              status: "blocked_missing_dependency",
              root_name: "docs",
              family: "office",
              summary: "Job job-1 is blocked.",
              follow_up_command: "flux-kb crawl worker status --family office",
              evidence: { status: "blocked_missing_dependency" },
              remediation_actions: [
                {
                  id: "retry_corpus_job",
                  label: "Retry corpus job",
                  target: { type: "job", id: "job-1" },
                  method: "POST",
                  endpoint: "/api/diagnostics/actions",
                  payload: {
                    action: "retry_corpus_job",
                    target_type: "job",
                    target_id: "job-1",
                    root_name: "docs",
                    family: "office",
                    reason: "operator diagnostic remediation"
                  },
                  requires_confirmation: true,
                  destructive: false,
                  settings_mutated: false
                }
              ]
            }
          ]
        });
      }
      if (url === "/api/diagnostics/actions" && init?.method === "POST") {
        state.diagnosticsActionPayload = JSON.parse(String(init.body));
        return json({ settings_mutated: false, action: "retry_corpus_job", result: { status: "pending" } });
      }
      if (url === "/api/automation/status") return json(state.automationStatusPayload);
      if (url === "/api/automation/actions") return json(state.automationActionsPayload);
      if (url === "/api/automation/run" && init?.method === "POST") {
        state.automationRunPayload = JSON.parse(String(init.body));
        return json({
          settings_mutated: false,
          run: { id: "automation-run-2", status: "completed" },
          summary: { eligible: 5, applied: 4, blocked: 1, settings_mutated: false },
          actions: [
            { id: "automation-action-2", action: "run_governance_shadow", status: "applied", risk: "low", source: "governance" }
          ]
        });
      }
      if (url === "/api/retrieval/benchmarks") return json(state.retrievalBenchmarkHistoryPayload);
      if (url === "/api/retrieval/benchmarks/run" && init?.method === "POST") {
        state.retrievalBenchmarkRunPayload = JSON.parse(String(init.body));
        const suite = (state.retrievalBenchmarkRunPayload as { suite?: string }).suite ?? "standard";
        return json({
          suite,
          label: "nightly",
          status: "completed",
          query_count: 5,
          passed_count: 4,
          failed_count: 1,
          metrics: {
            top1_accuracy: 0.8,
            precision_at_3: 0.9,
            recall_at_5: 0.85,
            brief_dilution: 0.2
          },
          metric_deltas: {
            top1_accuracy: 0.1,
            brief_dilution: -0.05
          },
          calibration_summary: {
            confidence_bands: { high: 3, medium: 1, low: 1 },
            semantic_thresholds: [
              { threshold: 0.86, evaluated_count: 4, false_positive_count: 0, false_negative_count: 1, pass_count: 3 }
            ]
          },
          case_results: [
            {
              case_id: "scope-filter",
              category: "current_only",
              status: "failed",
              expected_ids: ["chunk-scope"],
              observed_ids: ["chunk-other"],
              reasons: ["top1_miss", "scope_miss"],
              confidence_band: "low",
              failure_details: [
                { reason: "top1_miss", message: "Expected evidence was not ranked first." },
                { reason: "scope_miss", message: "The top result came from an unexpected retrieval scope." }
              ]
            }
          ],
          recommendations: {
            settings_mutated: false,
            purpose: suite === "governance-shadow" ? "governance_shadow_evaluation" : "retrieval_evaluation",
            governance_shadow: suite === "governance-shadow"
              ? {
                  proposal_case_count: 4,
                  proposal_pass_count: 3,
                  proposal_precision: 0.75,
                  guardrail_case_count: 1,
                  guardrail_pass_count: 1,
                  guardrail_fail_count: 0,
                  proposal_categories: { governance_stale: 1, governance_duplicate: 1, governance_low_confidence: 1, governance_contradiction: 1 }
                }
              : undefined,
            candidates: [
              {
                kind: "semantic_duplicate_threshold",
                threshold: 0.86,
                evidence_count: 4,
                false_positive_count: 0,
                false_negative_count: 1,
                rationale: "Synthetic semantic duplicate calibration passed for 3/4 cases at threshold 0.86."
              }
            ]
          }
        });
      }
      if (url === "/api/mail/profiles" && init?.method === "POST") return json({ ...JSON.parse(String(init.body)), enabled: true });
      if (url.startsWith("/api/mail/profiles/") && init?.method === "DELETE") {
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        return json({
          ...(state.mailProfileDeleteResponse as Record<string, unknown>),
          profile_name: decodeURIComponent(url.split("/").pop() ?? "")
        });
      }
      if (url.startsWith("/api/mail/profiles/") && url.endsWith("/oauth-client-config") && init?.method === "PUT") {
        return json({
          name: decodeURIComponent(url.split("/").at(-2) ?? ""),
          metadata: { gmail_oauth_client_config_path: JSON.parse(String(init.body)).client_config_path }
        });
      }
      if (url.startsWith("/api/mail/profiles/") && url.endsWith("/post-process/dry-run") && init?.method === "POST") {
        state.postProcessDryRunPayload = JSON.parse(String(init.body));
        return json({
          profile_name: decodeURIComponent(url.split("/").at(-3) ?? ""),
          dry_run: true,
          events: [{ status: "planned", policy: "remove_label", action: "gmail_remove_label" }]
        });
      }
      if (url === "/api/mail/sync") return json(state.mailSyncPayload);
      if (url === "/api/mail/oauth/gmail/start") return json({ status: "pending_user_authorization", authorization_url: "https://accounts.google.com/o/oauth2/v2/auth?state=test" });
      if (url === "/api/search") return json(state.searchPayload);
      if (url === "/api/explain") {
        state.explainRequestPayload = JSON.parse(String(init?.body ?? "{}"));
        return json(
          state.explainPayload ?? {
            query: state.explainRequestPayload.query,
            results: state.searchPayload,
            brief: { text: "", token_budget: 0, packed: [], excluded: [] },
            filter_trace: { excluded: [] },
            suppression: {}
          }
        );
      }
      if (url.startsWith("/api/results/")) return json(state.resultDetailPayload);
      if (url.startsWith("/api/corpus/assets/") && url.endsWith("/actions")) return json(state.fileActionPayload);
      if (url === "/api/governance/run" && init?.method === "POST") {
        state.governanceRunPayload = JSON.parse(String(init.body));
        return json({ run: { id: "gov-run-1" }, actions: [], settings_mutated: false, memory_mutated: false });
      }
      if (url === "/api/governance/actions/gov-action-1/apply" && init?.method === "POST") {
        state.governanceApplyPayload = JSON.parse(String(init.body));
        state.governanceActionsPayload = {
          ...(state.governanceActionsPayload as Record<string, unknown>),
          actions: [
            {
              id: "gov-action-1",
              action: "stale_tag",
              target_type: "claim",
              target_id: "claim-stale",
              risk: "low",
              status: "applied",
              source: "retention_quality",
              rationale: { summary: "stale evidence" }
            },
            {
              id: "gov-applied-1",
              action: "deprioritize",
              target_type: "claim",
              target_id: "claim-old",
              risk: "low",
              status: "applied",
              source: "retention_quality",
              rationale: { summary: "low confidence" }
            }
          ],
          telemetry: { total: 2, by_status: { applied: 2 }, by_risk: { low: 2 }, by_mutation: { mutated: 2 } }
        };
        return json({ action: { id: "gov-action-1", status: "applied" }, memory_mutated: true, settings_mutated: false });
      }
      if (url === "/api/governance/actions/gov-applied-1/recover" && init?.method === "POST") {
        state.governanceRecoverPayload = JSON.parse(String(init.body));
        return json({ action: { id: "gov-applied-1", status: "recovered" }, memory_mutated: true, settings_mutated: false });
      }
      if (url.startsWith("/api/governance/actions")) return json(state.governanceActionsPayload);
      if (url === "/api/governance/digest") return json(state.governanceDigestPayload);
      if (url === "/api/governance/policy") return json(state.governancePolicyPayload);
      if (url.startsWith("/api/claims/") && url.endsWith("/transitions")) return json(state.claimTransitionPayload);
      if (url.startsWith("/api/claims")) return json(state.reviewPayload);
      if (url === "/api/retention/policies" && init?.method !== "PUT") return json(state.retentionPoliciesPayload);
      if (url.startsWith("/api/retention/policies/") && init?.method === "PUT") {
        state.retentionPolicyUpdatePayload = JSON.parse(String(init.body));
        return json({
          policy: {
            memory_class: decodeURIComponent(url.split("/").pop() ?? ""),
            ...state.retentionPolicyUpdatePayload,
            updated_by: "api"
          },
          audit_event: { id: "audit-retention", event_type: "retention.policy_updated" }
        });
      }
      if (url.startsWith("/api/retention/quality")) return json(state.retentionQualityPayload);
      if (url === "/api/capture/review/job-review/decision" && init?.method === "POST") {
        state.captureReviewDecisionPayload = JSON.parse(String(init.body));
        state.captureReviewPayload = { jobs: [] };
        state.auditPayload = {
          events: [
            {
              id: "audit-approved",
              event_type: "capture.review_approved",
              actor: "api",
              target_id: "job-review",
              details: { decision: "approve", rationale: "Verified source summary", status: "approved" },
              created_at: "2026-06-23T10:06:00+00:00"
            }
          ]
        };
        return json({
          job: { id: "job-review", status: "approved" },
          review: {
            decision: "approve",
            rationale: "Verified source summary",
            actor: "api",
            reviewed_at: "2026-06-23T10:06:00+00:00",
            audit_event_id: "audit-approved"
          },
          audit_event_id: "audit-approved",
          audit_event: { id: "audit-approved", event_type: "capture.review_approved" }
        });
      }
      if (url === "/api/capture/review/ingest" && init?.method === "POST") {
        state.captureReviewIngestPayload = JSON.parse(String(init.body));
        state.captureReviewPayload = {
          jobs: [
            {
              id: "job-review",
              job_type: "codex_backfill",
              status: "completed",
              payload: {
                status: "completed",
                path: "session.json",
                ingestion: { status: "ingested", episode_ids: ["episode-1"], source_leaf: "session.json" }
              },
              updated_at: "2026-06-23T10:10:00+00:00"
            }
          ]
        };
        state.auditPayload = {
          events: [
            {
              id: "audit-ingested",
              event_type: "capture.ingested",
              actor: "api",
              target_id: "job-review",
              details: { status: "ingested", source_leaf: "session.json", episode_count: 1 },
              created_at: "2026-06-23T10:11:00+00:00"
            }
          ]
        };
        return json({ processed: 1, ingested: 1, skipped: 0, failed: 0, blocked: 0, settings_mutated: false, jobs: state.captureReviewPayload.jobs });
      }
      if (url.startsWith("/api/capture/review")) {
        state.captureReviewRequestUrl = url;
        return json(state.captureReviewPayload);
      }
      if (url.startsWith("/api/audit")) return json(state.auditPayload);
      if (url.startsWith("/api/graph/traverse")) return json(state.graphPayload);
      if (url === "/api/outlook-host/request-sync") {
        return json({ id: "req-1", status: "pending", profile_name: JSON.parse(String(init?.body)).profile_name });
      }
      if (url.startsWith("/api/outlook-host/requests/") && url.endsWith("/cancel")) {
        state.outlookCancelRequests.push(url);
        const requestId = decodeURIComponent(url.split("/").at(-2) ?? "");
        if (requestId === "req-claimed") {
          return errorJson(
            { error: { message: "Outlook sync request is already claimed and cannot be cancelled mid-execution." } },
            409,
            "Conflict"
          );
        }
        return json({ id: requestId, status: "cancelled", cancelled: true });
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/cancel")) {
        state.corpusCancelRequests.push(url);
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        if (jobId === "job-sync-running") {
          return errorJson(
            { error: { message: "Corpus job is running and cannot be cancelled mid-execution." } },
            409,
            "Conflict"
          );
        }
        return json({ job_id: jobId, status: "cancelled_operator", cancelled: true });
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/retry")) {
        state.corpusRetryRequests.push(url);
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        if (jobId === "job-completed") {
          return errorJson(
            { error: { message: "retryable corpus job not found: job-completed" } },
            409,
            "Conflict"
          );
        }
        return json({ settings_mutated: false, action: "retry_corpus_job", result: { job_id: jobId, status: "pending" } });
      }
      if (url === "/api/dashboard/jobs/retry-blocked-asr") {
        state.corpusRetryRequests.push(url);
        return json({ retried: 1, eligible: 1, skipped: 0, errors: [], jobs: ["job-asr"], settings_mutated: false });
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/delete-request")) {
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        if (init?.method === "DELETE") {
          state.corpusRestoreRequests.push(url);
          const pending = state.pendingFetchResponses[url];
          if (pending) return pending.promise;
          return json({
            job_id: jobId,
            status: "blocked_missing_dependency",
            delete_requested: false,
            delete_requested_at: null,
            delete_requested_by: null,
            delete_reason: null
          });
        }
        state.corpusDeleteRequests.push(url);
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        if (jobId === "job-running") {
          return errorJson(
            { error: { message: "Corpus job status running cannot be marked for deletion." } },
            409,
            "Conflict"
          );
        }
        return json({
          job_id: jobId,
          status: "obsolete",
          delete_requested: true,
          delete_requested_at: "2026-07-01T09:00:00+00:00",
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        });
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/file-actions")) {
        state.corpusJobFileActionRequests.push({ url, body: JSON.parse(String(init?.body ?? "{}")) });
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        return json({ job_id: jobId, action: JSON.parse(String(init?.body ?? "{}")).action, ...state.corpusJobFileActionPayload });
      }
      if (url === "/api/crawl/roots") return json({ root: JSON.parse(String(init?.body)), sync: { files_seen: 0 } });
      if (url.startsWith("/api/crawl/roots/") && init?.method === "PATCH") {
        return json({ id: url.split("/").pop(), ...JSON.parse(String(init.body)) });
      }
      if (url.startsWith("/api/crawl/roots/") && init?.method === "DELETE") {
        const pending = state.pendingFetchResponses[url];
        if (pending) return pending.promise;
        return json({ id: url.split("/").pop()?.split("?")[0], deleted: true, purged_index: true });
      }
      if (url === "/api/crawl/backfill") return json({ completed: 1, blocked: 0, retried: 0 });
      if (url === "/api/crawl/sync") {
        if (state.crawlSyncErrorPayload) return errorJson(state.crawlSyncErrorPayload, 400, "Bad Request");
        return json({ root_name: JSON.parse(String(init?.body)).root_name ?? null, dry_run: JSON.parse(String(init?.body)).dry_run });
      }
      if (url === "/api/crawl/watch") return json({ updated: 1, watch_enabled: JSON.parse(String(init?.body)).enabled });
      if (url.endsWith("/enable") || url.endsWith("/disable")) return json({ status: "updated" });
      return json({});
    }));
    vi.stubGlobal("WebSocket", MockDashboardWebSocket);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    window.history.replaceState(null, "", "/dashboard");
    vi.useRealTimers();
  });
}

export function json(payload: unknown): Response {
  return {
    ok: true,
    json: async () => payload
  } as Response;
}

export function errorJson(payload: unknown, status: number, statusText: string): Response {
  return {
    ok: false,
    status,
    statusText,
    text: async () => JSON.stringify(payload),
    json: async () => payload
  } as Response;
}

export type DeferredResponse = {
  promise: Promise<Response>;
  resolve: (response: Response) => void;
  reject: (error: unknown) => void;
};

export function deferredResponse(): DeferredResponse {
  let resolve!: (response: Response) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<Response>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });
  return { promise, resolve, reject };
}
