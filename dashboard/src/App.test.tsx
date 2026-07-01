import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import App from "./App";

const health = {
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
  retrieval: { episodes: 9, asset_chunks: 12, embeddings: 40 },
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

const crawl = {
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

const mail = {
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

const outlook = {
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

const settings = [
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
    key: "embedding.dimensions",
    value: 384,
    source: "default",
    sensitive: false,
    category: "retrieval",
    apply_mode: "reindex_required",
    read_only: false,
    affected_components: ["retrieval", "worker"],
    description: "Embedding vector dimensions."
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

let mailSyncPayload: unknown;
let searchPayload: unknown;
let explainPayload: unknown;
let explainRequestPayload: unknown;
let resultDetailPayload: unknown;
let fileActionPayload: unknown;
let healthPayload: unknown;
let crawlPayload: unknown;
let mailPayload: unknown;
let jobsPayload: unknown;
let crawlSyncErrorPayload: unknown;
let reviewPayload: unknown;
let captureReviewPayload: unknown;
let captureReviewRequestUrl: string | undefined;
let captureReviewDecisionPayload: unknown;
let captureReviewIngestPayload: unknown;
let auditPayload: unknown;
let graphPayload: unknown;
let claimTransitionPayload: unknown;
let postProcessDryRunPayload: unknown;
let retentionPoliciesPayload: unknown;
let retentionQualityPayload: unknown;
let retentionPolicyUpdatePayload: unknown;
let benchmarkRunPayload: unknown;
let reliabilityRunPayload: unknown;
let codeFeedbackPayload: unknown;
let codeSearchRequestUrl: string | undefined;
let codeSymbolRequestUrl: string | undefined;
let retrievalBenchmarkRunPayload: unknown;
let retrievalBenchmarkHistoryPayload: unknown;
let diagnosticsActionPayload: unknown;
let automationStatusPayload: unknown;
let automationRunPayload: unknown;
let automationActionsPayload: unknown;
let governanceActionsPayload: unknown;
let governanceDigestPayload: unknown;
let governancePolicyPayload: unknown;
let governanceRunPayload: unknown;
let governanceApplyPayload: unknown;
let governanceRecoverPayload: unknown;
let outlookCancelRequests: string[];
let corpusCancelRequests: string[];
let corpusRetryRequests: string[];
let corpusDeleteRequests: string[];
let corpusJobFileActionRequests: Array<{ url: string; body: unknown }>;
let jobsRequestUrls: string[];
let jobToolInvocationPayload: unknown;
let jobToolInvocationRequestUrls: string[];

describe("Flux dashboard", () => {
  beforeEach(() => {
    outlook.pending_requests = [];
    healthPayload = health;
    crawlPayload = JSON.parse(JSON.stringify(crawl));
    mailPayload = JSON.parse(JSON.stringify(mail));
    jobsPayload = {
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
    crawlSyncErrorPayload = undefined;
    mailSyncPayload = { profiles: [{ profile: "gmail-capture", status: "completed", exported: 0 }], count: 1 };
    searchPayload = [
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
          streams: ["corpus_lexical", "corpus_vector"],
          raw_scores: { corpus_lexical: 0.7, corpus_vector: 0.3 },
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
    explainPayload = undefined;
    explainRequestPayload = undefined;
    reviewPayload = {
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
    captureReviewPayload = {
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
    captureReviewDecisionPayload = undefined;
    captureReviewIngestPayload = undefined;
    captureReviewRequestUrl = undefined;
    auditPayload = {
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
    graphPayload = {
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
    claimTransitionPayload = { id: "claim-stale", lifecycle_state: "confirmed" };
    postProcessDryRunPayload = undefined;
    retentionPoliciesPayload = {
      policies: [
        { memory_class: "claim", half_life_days: 120, min_confidence: 0.35, action: "review", updated_by: "system" },
        { memory_class: "episode", half_life_days: 180, min_confidence: 0.25, action: "deprioritize", updated_by: "system" },
        { memory_class: "corpus", half_life_days: 365, min_confidence: 0.2, action: "review", updated_by: "system" }
      ]
    };
    retentionQualityPayload = {
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
    governanceActionsPayload = {
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
    governanceDigestPayload = {
      digest: {
        summary: { new_proposals: 1, blocked_proposals: 1, recoverable_actions: 1, gate_status: "ready" },
        recommendations: [{ action: "inspect_blocked_governance", count: 1 }]
      },
      settings_mutated: false
    };
    governancePolicyPayload = {
      policy: { min_shadow_precision: 0.8, auto_apply_enabled: false, auto_apply_risk_ceiling: "low" },
      settings_mutated: false
    };
    governanceRunPayload = undefined;
    governanceApplyPayload = undefined;
    governanceRecoverPayload = undefined;
    retentionPolicyUpdatePayload = undefined;
    benchmarkRunPayload = undefined;
    reliabilityRunPayload = undefined;
    codeFeedbackPayload = undefined;
    codeSearchRequestUrl = undefined;
    codeSymbolRequestUrl = undefined;
    retrievalBenchmarkRunPayload = undefined;
    diagnosticsActionPayload = undefined;
    automationRunPayload = undefined;
    automationStatusPayload = {
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
    automationActionsPayload = {
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
    retrievalBenchmarkHistoryPayload = {
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
    outlookCancelRequests = [];
    corpusCancelRequests = [];
    corpusRetryRequests = [];
    corpusDeleteRequests = [];
    corpusJobFileActionRequests = [];
    jobsRequestUrls = [];
    jobToolInvocationRequestUrls = [];
    jobToolInvocationPayload = { job_id: "job-pdf", invocations: [] };
    resultDetailPayload = {
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
    fileActionPayload = { state: "opened", asset_id: "asset-1", action: "open" };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/dashboard/health") return json(healthPayload);
      if (url === "/api/dashboard/crawl") return json(crawlPayload);
      if (url.startsWith("/api/dashboard/jobs/") && url.includes("/tool-invocations")) {
        jobToolInvocationRequestUrls.push(url);
        return json(jobToolInvocationPayload);
      }
      if (url.startsWith("/api/dashboard/jobs") && !url.endsWith("/cancel") && !url.endsWith("/retry") && !url.endsWith("/delete-request") && !url.endsWith("/file-actions")) {
        jobsRequestUrls.push(url);
        return json(jobsPayload);
      }
      if (url === "/api/dashboard/retrieval-stats") return json({ retrieval: health.retrieval, duplicate_assets: 0 });
      if (url === "/api/mail/status") return json(mailPayload);
      if (url === "/api/outlook-host/status") return json(outlook);
      if (url === "/api/host/status") return json({ status: "running", browse_supported: true, platform: "Windows" });
      if (url === "/api/host/browse-folder") return json({ status: "selected", path: "E:\\Temp\\watch-test" });
      if (url === "/api/settings") return json(settings);
      if (url.startsWith("/api/settings/") && init?.method === "PUT") {
        const key = decodeURIComponent(url.replace("/api/settings/", ""));
        return json({ ...settings.find((row) => row.key === key), source: "db", value: JSON.parse(String(init.body)).value });
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
        reliabilityRunPayload = JSON.parse(String(init.body));
        if ((reliabilityRunPayload as { scope?: string }).scope === "all_roots") {
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
        benchmarkRunPayload = JSON.parse(String(init.body));
        return json({
          fixture: "all",
          mode: "scan",
          scenario: (benchmarkRunPayload as { scenario?: string }).scenario ?? "standard",
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
        codeFeedbackPayload = JSON.parse(String(init.body));
        return json({ id: "feedback-1", settings_mutated: false });
      }
      if (url.startsWith("/api/code/search")) {
        codeSearchRequestUrl = url;
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
        codeSymbolRequestUrl = url;
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
        diagnosticsActionPayload = JSON.parse(String(init.body));
        return json({ settings_mutated: false, action: "retry_corpus_job", result: { status: "pending" } });
      }
      if (url === "/api/automation/status") return json(automationStatusPayload);
      if (url === "/api/automation/actions") return json(automationActionsPayload);
      if (url === "/api/automation/run" && init?.method === "POST") {
        automationRunPayload = JSON.parse(String(init.body));
        return json({
          settings_mutated: false,
          run: { id: "automation-run-2", status: "completed" },
          summary: { eligible: 5, applied: 4, blocked: 1, settings_mutated: false },
          actions: [
            { id: "automation-action-2", action: "run_governance_shadow", status: "applied", risk: "low", source: "governance" }
          ]
        });
      }
      if (url === "/api/retrieval/benchmarks") return json(retrievalBenchmarkHistoryPayload);
      if (url === "/api/retrieval/benchmarks/run" && init?.method === "POST") {
        retrievalBenchmarkRunPayload = JSON.parse(String(init.body));
        const suite = (retrievalBenchmarkRunPayload as { suite?: string }).suite ?? "standard";
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
      if (url.startsWith("/api/mail/profiles/") && url.endsWith("/oauth-client-config") && init?.method === "PUT") {
        return json({
          name: decodeURIComponent(url.split("/").at(-2) ?? ""),
          metadata: { gmail_oauth_client_config_path: JSON.parse(String(init.body)).client_config_path }
        });
      }
      if (url.startsWith("/api/mail/profiles/") && url.endsWith("/post-process/dry-run") && init?.method === "POST") {
        postProcessDryRunPayload = JSON.parse(String(init.body));
        return json({
          profile_name: decodeURIComponent(url.split("/").at(-3) ?? ""),
          dry_run: true,
          events: [{ status: "planned", policy: "remove_label", action: "gmail_remove_label" }]
        });
      }
      if (url === "/api/mail/sync") return json(mailSyncPayload);
      if (url === "/api/mail/oauth/gmail/start") return json({ status: "pending_user_authorization", authorization_url: "https://accounts.google.com/o/oauth2/v2/auth?state=test" });
      if (url === "/api/search") return json(searchPayload);
      if (url === "/api/explain") {
        explainRequestPayload = JSON.parse(String(init?.body ?? "{}"));
        return json(
          explainPayload ?? {
            query: explainRequestPayload.query,
            results: searchPayload,
            brief: { text: "", token_budget: 0, packed: [], excluded: [] },
            filter_trace: { excluded: [] },
            suppression: {}
          }
        );
      }
      if (url.startsWith("/api/results/")) return json(resultDetailPayload);
      if (url.startsWith("/api/corpus/assets/") && url.endsWith("/actions")) return json(fileActionPayload);
      if (url === "/api/governance/run" && init?.method === "POST") {
        governanceRunPayload = JSON.parse(String(init.body));
        return json({ run: { id: "gov-run-1" }, actions: [], settings_mutated: false, memory_mutated: false });
      }
      if (url === "/api/governance/actions/gov-action-1/apply" && init?.method === "POST") {
        governanceApplyPayload = JSON.parse(String(init.body));
        governanceActionsPayload = {
          ...(governanceActionsPayload as Record<string, unknown>),
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
        governanceRecoverPayload = JSON.parse(String(init.body));
        return json({ action: { id: "gov-applied-1", status: "recovered" }, memory_mutated: true, settings_mutated: false });
      }
      if (url.startsWith("/api/governance/actions")) return json(governanceActionsPayload);
      if (url === "/api/governance/digest") return json(governanceDigestPayload);
      if (url === "/api/governance/policy") return json(governancePolicyPayload);
      if (url.startsWith("/api/claims/") && url.endsWith("/transitions")) return json(claimTransitionPayload);
      if (url.startsWith("/api/claims")) return json(reviewPayload);
      if (url === "/api/retention/policies" && init?.method !== "PUT") return json(retentionPoliciesPayload);
      if (url.startsWith("/api/retention/policies/") && init?.method === "PUT") {
        retentionPolicyUpdatePayload = JSON.parse(String(init.body));
        return json({
          policy: {
            memory_class: decodeURIComponent(url.split("/").pop() ?? ""),
            ...retentionPolicyUpdatePayload,
            updated_by: "api"
          },
          audit_event: { id: "audit-retention", event_type: "retention.policy_updated" }
        });
      }
      if (url.startsWith("/api/retention/quality")) return json(retentionQualityPayload);
      if (url === "/api/capture/review/job-review/decision" && init?.method === "POST") {
        captureReviewDecisionPayload = JSON.parse(String(init.body));
        captureReviewPayload = { jobs: [] };
        auditPayload = {
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
        captureReviewIngestPayload = JSON.parse(String(init.body));
        captureReviewPayload = {
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
        auditPayload = {
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
        return json({ processed: 1, ingested: 1, skipped: 0, failed: 0, blocked: 0, settings_mutated: false, jobs: captureReviewPayload.jobs });
      }
      if (url.startsWith("/api/capture/review")) {
        captureReviewRequestUrl = url;
        return json(captureReviewPayload);
      }
      if (url.startsWith("/api/audit")) return json(auditPayload);
      if (url.startsWith("/api/graph/traverse")) return json(graphPayload);
      if (url === "/api/outlook-host/request-sync") {
        return json({ id: "req-1", status: "pending", profile_name: JSON.parse(String(init?.body)).profile_name });
      }
      if (url.startsWith("/api/outlook-host/requests/") && url.endsWith("/cancel")) {
        outlookCancelRequests.push(url);
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
        corpusCancelRequests.push(url);
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
        corpusRetryRequests.push(url);
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
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/delete-request")) {
        corpusDeleteRequests.push(url);
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        if (jobId === "job-running") {
          return errorJson(
            { error: { message: "Corpus job status running cannot be marked for deletion." } },
            409,
            "Conflict"
          );
        }
        return json({
          job_id: jobId,
          status: "failed",
          delete_requested: true,
          delete_requested_at: "2026-07-01T09:00:00+00:00",
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        });
      }
      if (url.startsWith("/api/dashboard/jobs/") && url.endsWith("/file-actions")) {
        corpusJobFileActionRequests.push({ url, body: JSON.parse(String(init?.body ?? "{}")) });
        const jobId = decodeURIComponent(url.split("/").at(-2) ?? "");
        return json({ job_id: jobId, action: JSON.parse(String(init?.body ?? "{}")).action, state: "opened", path: "E:/Flux Docs/docs/failed.pdf" });
      }
      if (url === "/api/crawl/roots") return json({ root: JSON.parse(String(init?.body)), sync: { files_seen: 0 } });
      if (url.startsWith("/api/crawl/roots/") && init?.method === "PATCH") {
        return json({ id: url.split("/").pop(), ...JSON.parse(String(init.body)) });
      }
      if (url.startsWith("/api/crawl/roots/") && init?.method === "DELETE") {
        return json({ id: url.split("/").pop()?.split("?")[0], deleted: true, purged_index: true });
      }
      if (url === "/api/crawl/backfill") return json({ completed: 1, blocked: 0, retried: 0 });
      if (url === "/api/crawl/sync") {
        if (crawlSyncErrorPayload) return errorJson(crawlSyncErrorPayload, 400, "Bad Request");
        return json({ root_name: JSON.parse(String(init?.body)).root_name ?? null, dry_run: JSON.parse(String(init?.body)).dry_run });
      }
      if (url === "/api/crawl/watch") return json({ updated: 1, watch_enabled: JSON.parse(String(init?.body)).enabled });
      if (url.endsWith("/enable") || url.endsWith("/disable")) return json({ status: "updated" });
      return json({});
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    window.history.replaceState(null, "", "/dashboard");
    vi.useRealTimers();
  });

  test("defaults to overview and renders a friendly read-only status console", async () => {
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Overview" })).toHaveClass("active");
    expect(screen.getByRole("heading", { name: "System Overview" })).toBeInTheDocument();
    expect(screen.getByText("What needs attention")).toBeInTheDocument();
    expect(screen.getByText("Flux handled automatically")).toBeInTheDocument();
    expect(screen.getByText("Next safe action")).toBeInTheDocument();
    expect(screen.getByText("Database paths")).toBeInTheDocument();
    expect(screen.getByText("Outlook Host")).toBeInTheDocument();
    expect(screen.getByText("Host Agent")).toBeInTheDocument();
    expect(screen.getByText("Codex Integration")).toBeInTheDocument();
    expect(screen.getByText("Codex restart required")).toBeInTheDocument();
    expect(screen.getByText(/Auto-refresh every 1s/i)).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Mail profiles" })).not.toBeInTheDocument();
    expect(screen.queryByText(/"database"/)).not.toBeInTheDocument();
  });

  test("top health chips distinguish API and host database paths", async () => {
    healthPayload = {
      ...health,
      database: {
        ok: false,
        message: "host-published database blocked",
        checks: {
          service: { ok: true, message: "database reachable", required: true, label: "API database" },
          host_published: { ok: false, message: "connection failed", required: true, label: "Host database" }
        }
      }
    };

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    const apiDbChip = screen.getAllByText("API DB").map((item) => item.closest(".status-chip")).find(Boolean);
    const hostDbChip = screen.getAllByText("Host DB").map((item) => item.closest(".status-chip")).find(Boolean);
    expect(apiDbChip).toHaveTextContent("Healthy");
    expect(hostDbChip).toHaveTextContent("Blocked");
    expect(screen.queryByText("PG")).not.toBeInTheDocument();
  });

  test("settings system section exposes Codex hooks deployment and runtime controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Settings" }));
    expect(screen.getByRole("heading", { name: "Codex Hooks" })).toBeInTheDocument();
    expect(screen.getByText("Preflight brief")).toBeInTheDocument();
    expect(screen.getByText("Turn capture")).toBeInTheDocument();
    expect(screen.getByText("codex_hook.preflight_injected")).toBeInTheDocument();
    expect(screen.getByText("MCP tools")).toBeInTheDocument();
    expect(screen.getByText("kb.brief ready")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Deployment" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /^Runtime Actions/ })).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getAllByText("codex.hooks.enabled").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("Enable Flux Codex hook policy evaluation.")).toBeInTheDocument();
  });

  test("performance shows acceleration capabilities, cache layout, and family telemetry", async () => {
    const user = userEvent.setup();
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Performance" }));
    expect(screen.getByRole("heading", { name: "Acceleration" })).toBeInTheDocument();
    expect(screen.getByText("NVIDIA")).toBeInTheDocument();
    expect(screen.getByText("nvidia-smi not found")).toBeInTheDocument();
    expect(screen.getByText("Local Model")).toBeInTheDocument();
    expect(screen.getByText("disabled")).toBeInTheDocument();
    expect(screen.getByText("Watcher Policy")).toBeInTheDocument();
    expect(screen.getAllByText("watchdog").length).toBeGreaterThan(0);
    expect(screen.getByText("auto")).toBeInTheDocument();
    expect(screen.getByText("D:/FluxLLMKB/private/cache")).toBeInTheDocument();
    expect(screen.getAllByText("media").length).toBeGreaterThan(0);
    expect(screen.getByText("p95 95ms; OCR 6 hit / 2 miss; ASR 4 hit / 1 miss; 9 segments; Vision 5 hit / 2 miss; 3 descriptions; 1 blocked; 4 decorative skips; Frames 6 sampled; thumbnails 7 hit / 8 miss; Embeddings 10 vectors; 2 skipped; 1 batches; cache 3 hit / 4 miss")).toBeInTheDocument();
    expect(screen.getByText("Family Backpressure")).toBeInTheDocument();
    expect(screen.getByText("cap 1/1")).toBeInTheDocument();
    expect(screen.getByText("Cap Reached; oldest 120s; retry 2; blocked locks 1; parser 3 hit / 1 miss; 5 manifest skips")).toBeInTheDocument();
    expect(screen.getByText("Benchmark History")).toBeInTheDocument();
    expect(screen.getByText("image-heavy")).toBeInTheDocument();
    expect(screen.getByText("Scan / warm / pass 2")).toBeInTheDocument();
    expect(screen.getByText("10 files/s; -250ms; +2 files/s")).toBeInTheDocument();
    expect(screen.getByText("after-deploy; desktop-after; Monitored Root; hash 4; workers 3; 8 manifest skips; model disabled; 2 blocked")).toBeInTheDocument();
    expect(screen.getByText("Reliability Gate")).toBeInTheDocument();
    expect(screen.getByText("Reliability Matrix")).toBeInTheDocument();
    expect(screen.getByText("1 ready / 1 partial / 1 not run")).toBeInTheDocument();
    expect(screen.getByText("benchmark bench-docs")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud reliability evidence and clear blocked or pending work.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Operator Evidence" })).toBeInTheDocument();
    expect(screen.getByText("VSS Snapshot")).toBeInTheDocument();
    expect(screen.getAllByText("Hold").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Provider Acceleration")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud reliability evidence.")).toBeInTheDocument();
    expect(screen.getByText("Code feedback reported misses.")).toBeInTheDocument();
    expect(screen.getAllByText("Partial").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Synthetic reliability evidence is current.")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud calibration for the selected root.")).toBeInTheDocument();
    expect(await screen.findByText("docs / partial")).toBeInTheDocument();
    expect(screen.getByText("crawler.hash_parallelism")).toBeInTheDocument();
    expect(screen.getByText("needs comparison")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run reliability gate" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run all roots" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run reliability diagnostics" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run host/cloud calibration" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run cache readiness" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run tuning diagnostics" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Run reliability gate" }));
    expect(reliabilityRunPayload).toEqual({ scope: "root", root_name: "docs", max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true });
    await user.click(screen.getByRole("button", { name: "Run all roots" }));
    expect(reliabilityRunPayload).toEqual({ scope: "all_roots", max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true, evidence_level: "full" });
    await user.click(screen.getByRole("button", { name: "Run scan benchmark" }));
    expect(benchmarkRunPayload).toEqual({ fixture: "all", files: 10, mode: "scan", passes: 2, workers: 1, family: "all", scope: "synthetic", scenario: "standard" });
    await user.click(screen.getByRole("button", { name: "Run tuning diagnostics" }));
    expect(benchmarkRunPayload).toEqual({ fixture: "all", files: 10, mode: "scan", passes: 2, workers: 1, family: "all", scope: "synthetic", scenario: "tuning" });
    expect(await screen.findByText("Manual candidates")).toBeInTheDocument();
    expect(screen.getByText("crawler.hash_parallelism")).toBeInTheDocument();
    expect(screen.getByText("current 1 -> candidate 4")).toBeInTheDocument();
    expect(screen.getAllByText("office").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("3 pending")).toBeInTheDocument();
  });

  test("retrieval tab owns code diagnostics and code-search quality controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));

    expect(screen.getByRole("heading", { name: "Code Diagnostics" })).toBeInTheDocument();
    expect(screen.getByText("Code Assets")).toBeInTheDocument();
    expect(screen.getByText("7 symbols / 9 refs")).toBeInTheDocument();
    expect(screen.getByText("python 4; typescript 3; Parsed 6; Fallback 1")).toBeInTheDocument();
    expect(screen.getByText("Code Feedback")).toBeInTheDocument();
    expect(screen.getByText("2 feedback events")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Submit code feedback" }));
    expect(codeFeedbackPayload).toMatchObject({ surface: "dashboard", miss_category: "missing_symbol" });
    await user.clear(screen.getByLabelText("Code search query"));
    await user.type(screen.getByLabelText("Code search query"), "build_invoice");
    await user.clear(screen.getByLabelText("Code path glob"));
    await user.type(screen.getByLabelText("Code path glob"), "src/*.py");
    await user.click(screen.getByRole("button", { name: "Run code search" }));
    expect(codeSearchRequestUrl).toContain("/api/code/search?");
    expect(codeSearchRequestUrl).toContain("query=build_invoice");
    expect(codeSearchRequestUrl).toContain("relationship=call");
    expect(codeSearchRequestUrl).toContain("path_glob=src%2F*.py");
    expect(codeSearchRequestUrl).toContain("include_generated=false");
    expect(await screen.findByText("OrderService.build_invoice")).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Symbol lookup query"));
    await user.type(screen.getByLabelText("Symbol lookup query"), "OrderService.build_invoice");
    await user.click(screen.getByRole("button", { name: "Lookup code symbol" }));
    expect(codeSymbolRequestUrl).toContain("/api/code/symbols?");
    expect(codeSymbolRequestUrl).toContain("symbol=OrderService.build_invoice");
    expect(await screen.findByText("test_build_invoice_returns_ready_status")).toBeInTheDocument();
  });

  test("diagnostics tab owns operational diagnostics and safe remediation controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Diagnostics" }));

    expect(screen.getByRole("heading", { name: "Operational Diagnostics" })).toBeInTheDocument();
    expect(screen.getByText("Blocked jobs")).toBeInTheDocument();
    expect(screen.getByText("1 blocked locks")).toBeInTheDocument();
    expect(screen.getByText("Job job-1 is blocked.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry corpus job" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Apply diagnostic filters" }));
    expect(fetch).toHaveBeenCalledWith("/api/diagnostics/all?root_name=docs&status=blocked_missing_dependency&family=office&include_details=true");
    vi.spyOn(window, "confirm").mockReturnValueOnce(true);
    await user.click(screen.getByRole("button", { name: "Retry corpus job" }));
    expect(diagnosticsActionPayload).toEqual({
      action: "retry_corpus_job",
      target_type: "job",
      target_id: "job-1",
      root_name: "docs",
      family: "office",
      reason: "operator diagnostic remediation"
    });
  });

  test("automation tab lists guarded actions manual blocks audit trail and can run a guarded pass", async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: "Automation" }));

    expect(await screen.findByRole("heading", { name: "Guarded Automation" })).toBeInTheDocument();
    expect(screen.getByText("Guarded Auto")).toBeInTheDocument();
    expect(screen.getByText("Refresh retrieval evidence")).toBeInTheDocument();
    expect(screen.getByText("Ingest approved captures")).toBeInTheDocument();
    expect(screen.getByText("Delete or purge data")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Automation Audit Trail" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Run guarded pass now" }));

    await waitFor(() => {
      expect(automationRunPayload).toEqual({ mode: "guarded", dry_run: false, limit: 25 });
    });
    expect(screen.getByText(/Guarded automation completed/i)).toBeInTheDocument();
  });

  test("restores the last tab and selected root after refresh", async () => {
    localStorage.setItem("flux-dashboard-state", JSON.stringify({ activeTab: "corpus", selectedRootName: "docs" }));
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Corpus Monitor" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Root Details" })).toBeInTheDocument();
    expect(screen.getAllByText("docs").length).toBeGreaterThan(0);
  });

  test("auto-refreshes from backend polling without a manual page refresh", async () => {
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    expect(fetch).toHaveBeenCalledWith("/api/dashboard/health");

    await waitFor(() => {
      const healthCalls = vi.mocked(fetch).mock.calls.filter(([url]) => String(url) === "/api/dashboard/health");
      expect(healthCalls.length).toBeGreaterThanOrEqual(2);
    }, { timeout: 2500 });
    expect(screen.getByText(/Last updated/i)).toBeInTheDocument();
  });

  test("manual Outlook sync creates a host request", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await screen.findByText("outlook-catchup");
    await user.click(screen.getByRole("button", { name: "Sync selected profile" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/outlook-host/request-sync",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ profile_name: "outlook-catchup" })
        })
      );
    });
  });

  test("navigation changes dashboard sections instead of using dead anchors", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Settings" }));

    expect(await screen.findByRole("heading", { name: "Runtime Settings" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Jobs" }));
    expect(await screen.findByRole("heading", { name: "Job Queue" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Corpus" }));
    expect(await screen.findByRole("heading", { name: "Corpus Monitor" })).toBeInTheDocument();
  });

  test("review tab lists claim review work, graph edges, capture queue, and lifecycle actions", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(await screen.findByRole("heading", { name: "Claim Review" })).toBeInTheDocument();
    expect(screen.getByText("1 needs review")).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Claim review queue" });
    expect(within(table).getByText("Flux")).toBeInTheDocument();
    expect(within(table).getByText("uses")).toBeInTheDocument();
    expect(within(table).getByText("PostgreSQL")).toBeInTheDocument();
    expect(within(table).getByText("stale")).toBeInTheDocument();
    expect(table).toHaveTextContent("retention:deprioritize");
    expect(screen.getByRole("heading", { name: "Entity Graph" })).toBeInTheDocument();
    expect(screen.getByText("depends_on")).toBeInTheDocument();
    expect(screen.getByText("Capture Review Queue")).toBeInTheDocument();
    expect(screen.getByText("job-review")).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("Review filter"), "all");
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith("/api/claims?review=all&limit=50");
    });

    await user.click(screen.getByRole("button", { name: "Confirm claim claim-stale" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/claims/claim-stale/transitions",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ transition: "confirm", reason: "dashboard review" })
        })
      );
    });
  });

  test("review tab shows retention tuning and memory quality reporting", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(await screen.findByRole("heading", { name: "Retention Tuning" })).toBeInTheDocument();
    const policyTable = await screen.findByRole("table", { name: "Retention policies" });
    expect(within(policyTable).getByText("claim")).toBeInTheDocument();
    expect(within(policyTable).getByDisplayValue("120")).toBeInTheDocument();
    expect(within(policyTable).getByDisplayValue("0.35")).toBeInTheDocument();

    expect(screen.getByRole("heading", { name: "Memory Quality" })).toBeInTheDocument();
    expect(screen.getByText("2 need attention")).toBeInTheDocument();
    const qualityTable = await screen.findByRole("table", { name: "Memory quality candidates" });
    expect(within(qualityTable).getByText("Flux uses PostgreSQL")).toBeInTheDocument();
    expect(within(qualityTable).getAllByText("blocked_missing_dependency").length).toBeGreaterThan(0);

    await user.clear(screen.getByLabelText("Claim half-life days"));
    await user.type(screen.getByLabelText("Claim half-life days"), "90");
    await user.selectOptions(screen.getByLabelText("Claim retention action"), "deprioritize");
    await user.clear(screen.getByLabelText("Claim retention reason"));
    await user.type(screen.getByLabelText("Claim retention reason"), "live review");
    await user.click(screen.getByRole("button", { name: "Save claim retention policy" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/retention/policies/claim",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({
            half_life_days: 90,
            min_confidence: 0.35,
            action: "deprioritize",
            reason: "live review"
          })
        })
      );
    });
    expect(retentionPolicyUpdatePayload).toEqual({
      half_life_days: 90,
      min_confidence: 0.35,
      action: "deprioritize",
      reason: "live review"
    });
  });

  test("review tab shows governance automation digest guardrails and recovery actions", async () => {
    const user = userEvent.setup();
    vi.spyOn(window, "prompt").mockReturnValue("Reviewed governance evidence");
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(await screen.findByRole("heading", { name: "Governance Automation" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Governance Digest" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Guardrails" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Recovery" })).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Governance actions" });
    expect(within(table).getByText("Stale Tag")).toBeInTheDocument();
    expect(within(table).getByText("protected memory")).toBeInTheDocument();
    expect(screen.getByText("Inspect Blocked Governance")).toBeInTheDocument();
    expect(screen.getAllByText("claim:claim-old").length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: "Run shadow" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/run",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ mode: "shadow", limit: 25 })
        })
      );
    });
    expect(governanceRunPayload).toEqual({ mode: "shadow", limit: 25 });

    await user.click(within(table).getByRole("button", { name: "Apply governance action gov-action-1" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/actions/gov-action-1/apply",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ rationale: "Reviewed governance evidence", confirm: true })
        })
      );
    });
    expect(governanceApplyPayload).toEqual({ rationale: "Reviewed governance evidence", confirm: true });

    await user.click(screen.getByRole("button", { name: "Recover governance action gov-applied-1" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/actions/gov-applied-1/recover",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ rationale: "Reviewed governance evidence", confirm: true })
        })
      );
    });
    expect(governanceRecoverPayload).toEqual({ rationale: "Reviewed governance evidence", confirm: true });
  });

  test("capture review queue requires rationale, posts decisions, refreshes, and shows audit decisions", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    const table = await screen.findByRole("table", { name: "Capture review queue" });
    expect(within(table).getByRole("button", { name: "Approve capture job job-review" })).toBeInTheDocument();
    expect(within(table).getByRole("button", { name: "Reject capture job job-review" })).toBeInTheDocument();
    expect(await screen.findByText("capture.review_rejected")).toBeInTheDocument();
    expect(screen.getByText("duplicate capture")).toBeInTheDocument();

    await user.click(within(table).getByRole("button", { name: "Approve capture job job-review" }));
    const dialog = await screen.findByRole("dialog", { name: "Approve capture review" });
    expect(within(dialog).getByRole("button", { name: "Approve" })).toBeDisabled();

    await user.type(within(dialog).getByLabelText("Rationale"), "Verified source summary");
    await user.click(within(dialog).getByRole("button", { name: "Approve" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/capture/review/job-review/decision",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ decision: "approve", rationale: "Verified source summary" })
        })
      );
    });
    expect(captureReviewDecisionPayload).toEqual({ decision: "approve", rationale: "Verified source summary" });
    expect(await screen.findByText("No pending capture review jobs.")).toBeInTheDocument();
    expect(await screen.findByText("capture.review_approved")).toBeInTheDocument();
    expect(screen.getByText("Verified source summary")).toBeInTheDocument();
  });

  test("capture review status filters and ingests approved jobs", async () => {
    const user = userEvent.setup();
    captureReviewPayload = {
      jobs: [
        {
          id: "job-review",
          job_type: "codex_backfill",
          status: "approved",
          payload: { status: "approved", path: "session.json", ingestion: { status: "approved" } },
          updated_at: "2026-06-23T10:05:00+00:00"
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    await user.selectOptions(await screen.findByLabelText("Capture status"), "approved");
    await waitFor(() => {
      expect(captureReviewRequestUrl).toContain("status=approved");
    });

    await user.click(screen.getByRole("button", { name: "Ingest approved" }));

    await waitFor(() => {
      expect(captureReviewIngestPayload).toEqual({ limit: 25, dry_run: false });
    });
    expect(await screen.findByText("capture.ingested")).toBeInTheDocument();
    expect(screen.getByText("session.json")).toBeInTheDocument();
    expect(screen.getByText("episode-1")).toBeInTheDocument();
  });

  test("job queue renders readable rows and expandable details instead of primary raw JSON", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Extraction jobs" });
    expect(within(table).getByText("Retrying Locked")).toBeInTheDocument();
    expect(within(table).getByText("Extract PDF")).toBeInTheDocument();
    expect(within(table).getByText("docs/open.pdf")).toBeInTheDocument();
    expect(within(table).getByText("docs")).toBeInTheDocument();
    expect(within(table).getByText("2")).toBeInTheDocument();
    expect(within(table).getByText("file is locked by another process")).toBeInTheDocument();
    expect(screen.queryByText(/"asset_id"/)).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-pdf" }));

    expect(screen.getByText("job-pdf")).toBeInTheDocument();
    expect(screen.getByText("asset-1")).toBeInTheDocument();
    expect(screen.getByText("source-1")).toBeInTheDocument();
    expect(screen.getByText("Raw payload")).toBeInTheDocument();
  });

  test("job queue filters and pages corpus history through the jobs API", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 75,
      limit: 50,
      offset: 0,
      has_next: true,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };
    const updatedFromIso = new Date("2026-06-25T00:00").toISOString();
    const updatedToIso = new Date("2026-06-26T23:59").toISOString();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await screen.findByRole("table", { name: "Extraction jobs" });

    await user.click(screen.getByRole("button", { name: "Job status filter" }));
    const statusOptions = screen.getByRole("group", { name: "Job status options" });
    await user.click(within(statusOptions).getByRole("checkbox", { name: "Failed" }));
    await user.click(within(statusOptions).getByRole("checkbox", { name: "Retrying Locked" }));
    await user.click(screen.getByRole("button", { name: "Job root filter" }));
    const rootOptions = screen.getByRole("group", { name: "Job root options" });
    await user.click(within(rootOptions).getByRole("checkbox", { name: "docs" }));
    await user.click(within(rootOptions).getByRole("checkbox", { name: "mail" }));
    await user.click(screen.getByRole("button", { name: "Job type filter" }));
    const typeOptions = screen.getByRole("group", { name: "Job type options" });
    await user.click(within(typeOptions).getByRole("checkbox", { name: "Extract PDF" }));
    await user.click(within(typeOptions).getByRole("checkbox", { name: "Sync Root" }));
    await user.type(screen.getByLabelText("Updated from filter"), "2026-06-25T00:00");
    await user.type(screen.getByLabelText("Updated to filter"), "2026-06-26T23:59");
    await user.click(screen.getByRole("button", { name: "Apply job filters" }));

    await waitFor(() => {
      const queryUrl = jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.get("updated_from")).toBe(updatedFromIso);
      expect(params.get("updated_to")).toBe(updatedToIso);
      expect(params.get("limit")).toBe("50");
      expect(params.get("offset")).toBe("0");
    });
    await waitFor(() => {
      const savedState = JSON.parse(localStorage.getItem("flux-dashboard-state") ?? "{}") as { jobFilters?: Record<string, unknown> };
      expect(savedState.jobFilters).toMatchObject({
        status: ["failed", "retrying_locked"],
        root_name: ["docs", "mail"],
        job_type: ["corpus_extract_pdf", "corpus_sync_root"],
        updated_from: "2026-06-25T00:00",
        updated_to: "2026-06-26T23:59"
      });
    });

    await user.click(screen.getByRole("button", { name: "Next jobs page" }));

    await waitFor(() => {
      const latestUrl = jobsRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("offset")).toBe("50");
      expect(params.get("limit")).toBe("50");
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
    });
  });

  test("job queue opens corpus job target files and containing folders", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const openFile = await screen.findByRole("button", { name: "Open job target file docs/failed.pdf" });
    const openFolder = screen.getByRole("button", { name: "Open containing folder for job target docs/failed.pdf" });
    expect(openFile).toHaveAttribute("title", "Open file");
    expect(openFolder).toHaveAttribute("title", "Open containing folder");

    await user.click(openFile);
    expect(await screen.findByText("Open request opened.")).toBeInTheDocument();
    await user.click(openFolder);

    await waitFor(() => {
      expect(corpusJobFileActionRequests).toEqual([
        { url: "/api/dashboard/jobs/job-failed/file-actions", body: { action: "open" } },
        { url: "/api/dashboard/jobs/job-failed/file-actions", body: { action: "reveal" } }
      ]);
    });
    expect(await screen.findByText("Open containing folder request opened.")).toBeInTheDocument();
  });

  test("job queue restores persisted history filters on load", async () => {
    localStorage.setItem("flux-dashboard-state", JSON.stringify({
      activeTab: "jobs",
      jobFilters: {
        status: ["failed", "retrying_locked"],
        root_name: ["docs", "mail"],
        job_type: ["corpus_extract_pdf", "corpus_sync_root"],
        updated_from: "2026-06-25T00:00",
        updated_to: "2026-06-26T23:59"
      }
    }));
    jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };
    const updatedFromIso = new Date("2026-06-25T00:00").toISOString();
    const updatedToIso = new Date("2026-06-26T23:59").toISOString();

    render(<App />);

    await screen.findByRole("table", { name: "Extraction jobs" });
    expect(screen.getByRole("button", { name: "Job status filter" })).toHaveTextContent("2 statuses");
    expect(screen.getByRole("button", { name: "Job root filter" })).toHaveTextContent("2 roots");
    expect(screen.getByRole("button", { name: "Job type filter" })).toHaveTextContent("2 types");
    expect(screen.getByLabelText("Updated from filter")).toHaveValue("2026-06-25T00:00");
    expect(screen.getByLabelText("Updated to filter")).toHaveValue("2026-06-26T23:59");
    await waitFor(() => {
      const queryUrl = jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.get("updated_from")).toBe(updatedFromIso);
      expect(params.get("updated_to")).toBe(updatedToIso);
      expect(params.get("limit")).toBe("50");
      expect(params.get("offset")).toBe("0");
    });
  });

  test("job queue restores legacy scalar history filters as single selections", async () => {
    localStorage.setItem("flux-dashboard-state", JSON.stringify({
      activeTab: "jobs",
      jobFilters: {
        status: "failed",
        root_name: "docs",
        job_type: "corpus_extract_pdf"
      }
    }));
    jobsPayload = {
      jobs: [],
      count: 0,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };

    render(<App />);

    await screen.findByRole("button", { name: "Job status filter" });
    expect(screen.getByRole("button", { name: "Job status filter" })).toHaveTextContent("Failed");
    expect(screen.getByRole("button", { name: "Job root filter" })).toHaveTextContent("docs");
    expect(screen.getByRole("button", { name: "Job type filter" })).toHaveTextContent("Extract PDF");
    await waitFor(() => {
      const queryUrl = jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed"]);
      expect(params.getAll("root_name")).toEqual(["docs"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf"]);
    });
  });

  test("job queue exposes force retry only for eligible corpus jobs", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        },
        {
          id: "job-cancelled",
          job_type: "corpus_sync_root",
          status: "cancelled_operator",
          payload: { root_name: "mail", profile_name: "outlook-catchup" },
          attempts: 1,
          updated_at: "2026-06-26T09:20:00+00:00"
        },
        {
          id: "job-completed",
          job_type: "corpus_extract_pdf",
          status: "completed",
          payload: { root_name: "docs", path: "docs/done.pdf" },
          attempts: 1,
          updated_at: "2026-06-26T09:10:00+00:00"
        }
      ],
      count: 3,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "cancelled_operator", "completed"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByRole("button", { name: "Force retry corpus job job-failed" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Force retry corpus job job-cancelled" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job job-completed" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Force retry corpus job job-cancelled" }));

    await waitFor(() => {
      expect(corpusRetryRequests).toContain("/api/dashboard/jobs/job-cancelled/retry");
    });
    expect(await screen.findByText("Corpus job queued for retry.")).toBeInTheDocument();
  });

  test("job queue marks terminal corpus jobs for delayed deletion", async () => {
    const user = userEvent.setup();
    const deleteRequestedAt = "2026-07-01T09:00:00+00:00";
    const formattedDeleteRequestedAt = new Date(deleteRequestedAt).toLocaleString([], { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
    jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/open.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        },
        {
          id: "job-marked",
          job_type: "corpus_extract_pdf",
          status: "blocked_missing_dependency",
          payload: { root_name: "docs", path: "docs/missing.pdf" },
          attempts: 2,
          last_error: "missing dependency",
          updated_at: "2026-06-26T09:20:00+00:00",
          delete_requested_at: deleteRequestedAt,
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        },
        {
          id: "job-policy",
          job_type: "corpus_extract_code",
          status: "blocked_by_policy",
          payload: { root_name: "docs", path: "src/large.py" },
          attempts: 1,
          last_error: "text file exceeds inline extraction limit",
          updated_at: "2026-06-26T09:15:00+00:00"
        },
        {
          id: "job-invalid",
          job_type: "corpus_extract_document",
          status: "blocked_invalid_source",
          payload: { root_name: "docs", path: "docs/broken.docx" },
          attempts: 1,
          last_error: "Package not found",
          updated_at: "2026-06-26T09:12:00+00:00"
        },
        {
          id: "job-running",
          job_type: "corpus_extract_pdf",
          status: "running",
          payload: { root_name: "docs", path: "docs/running.pdf" },
          attempts: 1,
          updated_at: "2026-06-26T09:10:00+00:00"
        }
      ],
      count: 5,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "blocked_missing_dependency", "blocked_by_policy", "blocked_invalid_source", "running"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf", "corpus_extract_code", "corpus_extract_document"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByRole("button", { name: "Mark corpus job job-failed for deletion" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mark corpus job job-policy for deletion" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mark corpus job job-invalid for deletion" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mark corpus job job-running for deletion" })).not.toBeInTheDocument();
    expect(screen.getByText("Blocked by policy")).toBeInTheDocument();
    expect(screen.getByText("Invalid source")).toBeInTheDocument();
    expect(screen.getByText("Marked for deletion")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Mark corpus job job-failed for deletion" }));

    await waitFor(() => {
      expect(corpusDeleteRequests).toContain("/api/dashboard/jobs/job-failed/delete-request");
    });
    expect(await screen.findByText("Corpus job marked for deletion after retention.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-marked" }));

    expect(screen.getByText("Delete requested")).toBeInTheDocument();
    expect(screen.getByText(formattedDeleteRequestedAt)).toBeInTheDocument();
    expect(screen.getByText("Delete requested by")).toBeInTheDocument();
    expect(screen.getByText("dashboard")).toBeInTheDocument();
    expect(screen.getByText("Delete reason")).toBeInTheDocument();
    expect(screen.getByText("operator_cleanup")).toBeInTheDocument();
  });

  test("job queue distinguishes completed metadata-only corpus jobs", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-metadata",
          job_type: "corpus_extract_video",
          status: "completed",
          payload: { root_name: "docs", path: "meetings/long.mp4" },
          attempts: 1,
          updated_at: "2026-07-01T10:04:00+00:00",
          telemetry: {
            result_status: "metadata_only",
            asr_duration_seconds: 5856,
            asr_segments: 0
          }
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["completed"],
        roots: ["docs"],
        job_types: ["corpus_extract_video"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByText("Completed Metadata Only")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-metadata" }));

    expect(screen.getByText("Result")).toBeInTheDocument();
    expect(screen.getByText("Metadata Only")).toBeInTheDocument();
  });

  test("job queue shows Outlook COM requests and cancel feedback", async () => {
    const user = userEvent.setup();
    outlook.pending_requests = [
      {
        id: "req-pending",
        profile_name: "outlook-catchup",
        status: "pending",
        requested_by: "dashboard",
        created_at: "2026-06-27T16:29:01+00:00",
        updated_at: "2026-06-27T16:29:01+00:00"
      },
      {
        id: "req-claimed",
        profile_name: "outlook-catchup",
        status: "claimed",
        requested_by: "dashboard",
        claimed_by: "host-1",
        created_at: "2026-06-27T16:27:46+00:00",
        updated_at: "2026-06-27T16:28:00+00:00"
      }
    ];
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Operational jobs" });
    expect(within(table).getAllByText("Outlook Sync Request")).toHaveLength(2);
    expect(within(table).getAllByText("outlook-catchup")).toHaveLength(2);
    expect(within(table).getByText("Pending")).toBeInTheDocument();
    expect(within(table).getByText("Claimed")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel Outlook request req-pending" }));
    await waitFor(() => {
      expect(outlookCancelRequests).toContain("/api/outlook-host/requests/req-pending/cancel");
    });
    expect(await screen.findByText("Outlook sync request cancelled.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel Outlook request req-claimed" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("cannot be cancelled mid-execution");
  });

  test("job queue shows corpus sync progress and cancel feedback", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-sync-pending",
          job_type: "corpus_sync_root",
          status: "pending",
          payload: { root_name: "mail-outlook-mohesr", profile_name: "outlook-mohesr", reason: "outlook_spool_sync" },
          attempts: 0,
          telemetry: { stage: "queued" },
          updated_at: "2026-06-27T16:29:01+00:00"
        },
        {
          id: "job-sync-running",
          job_type: "corpus_sync_root",
          status: "running",
          payload: { root_name: "mail-outlook-mohesr", profile_name: "outlook-mohesr", reason: "outlook_spool_sync" },
          attempts: 1,
          telemetry: {
            stage: "hashing",
            stage_index: 4,
            stage_total: 6,
            paths_done: 42,
            paths_total: 3292,
            files_done: 3,
            files_total: 8,
            files_seen: 35655,
            files_changed: 35371,
            jobs_queued: 120,
            current_path: "/app/private/mail-spool/outlook-mohesr/ready/export-42",
            progress_percent: 13
          },
          updated_at: "2026-06-27T16:30:01+00:00"
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Extraction jobs" });
    expect(within(table).getByText("Progress")).toBeInTheDocument();
    expect(within(table).getAllByText("Sync Root")).toHaveLength(2);
    expect(within(table).getAllByText("outlook-mohesr")).toHaveLength(2);
    expect(within(table).getAllByText("mail-outlook-mohesr")).toHaveLength(2);
    expect(within(table).getByText("Paths 42/3292, stage 4/6 hashing, files 3/8")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-sync-running" }));
    expect(screen.getByText("Stage")).toBeInTheDocument();
    expect(screen.getAllByText("Hashing").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Progress").length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText("Paths 42/3292, stage 4/6 hashing, files 3/8").length).toBeGreaterThan(0);
    expect(screen.getByText("Current path")).toBeInTheDocument();
    expect(screen.getByText("/app/private/mail-spool/outlook-mohesr/ready/export-42")).toBeInTheDocument();
    expect(screen.getByText("Files seen")).toBeInTheDocument();
    expect(screen.getByText("35655")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel corpus job job-sync-pending" }));
    await waitFor(() => {
      expect(corpusCancelRequests).toContain("/api/dashboard/jobs/job-sync-pending/cancel");
    });
    expect(await screen.findByText("Corpus job cancelled.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel corpus job job-sync-running" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("cannot be cancelled mid-execution");
  });

  test("expanded job details show live console output refreshed by polling", async () => {
    const user = userEvent.setup();
    jobsPayload = {
      jobs: [
        {
          id: "job-video",
          job_type: "corpus_extract_video",
          status: "running",
          payload: { root_name: "media", path: "clips/demo.mp4" },
          attempts: 1,
          telemetry: { stage: "extracting" },
          updated_at: "2026-06-27T16:31:01+00:00"
        }
      ]
    };
    jobToolInvocationPayload = {
      job_id: "job-video",
      invocations: [
        {
          id: "inv-1",
          job_id: "job-video",
          command: ["python", "-m", "demo_tool"],
          cwd: "E:/LLM KB",
          status: "running",
          return_code: null,
          stdout: "first line\n",
          stderr: "warning line\n",
          started_at: "2026-06-27T16:31:02+00:00",
          completed_at: null,
          duration_ms: null
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await user.click(await screen.findByRole("button", { name: "Show details for job job-video" }));

    expect(await screen.findByText("Console output")).toBeInTheDocument();
    expect(screen.getByText("python -m demo_tool")).toBeInTheDocument();
    expect(screen.getAllByText("Running").length).toBeGreaterThan(0);
    expect(screen.getByText("first line")).toBeInTheDocument();
    expect(screen.getByText("warning line")).toBeInTheDocument();
    expect(jobToolInvocationRequestUrls).toContain("/api/dashboard/jobs/job-video/tool-invocations?limit=100");

    jobToolInvocationPayload = {
      job_id: "job-video",
      invocations: [
        {
          id: "inv-1",
          job_id: "job-video",
          command: ["python", "-m", "demo_tool"],
          cwd: "E:/LLM KB",
          status: "running",
          return_code: null,
          stdout: "first line\nsecond line\n",
          stderr: "warning line\n",
          started_at: "2026-06-27T16:31:02+00:00",
          completed_at: null,
          duration_ms: null
        }
      ]
    };

    await waitFor(() => {
      expect(jobToolInvocationRequestUrls.filter((url) => url.includes("job-video/tool-invocations")).length).toBeGreaterThanOrEqual(2);
      expect(screen.getByText(/second line/)).toBeInTheDocument();
    }, { timeout: 2500 });
  });

  test("job queue keeps the readable empty state when no jobs are queued", async () => {
    jobsPayload = { jobs: [] };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByText("No queued extraction jobs.")).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Extraction jobs" })).not.toBeInTheDocument();
  });

  test("corpus dashboard surfaces unstable and locked indexing states", async () => {
    const root = (crawl.root_summaries[0]);
    crawlPayload = {
      ...crawl,
      root_summaries: [
        {
          ...root,
          asset_counts: {
            ...root.asset_counts,
            pending_stable: 2,
            retrying_locked: 1,
            blocked_locked: 1
          },
          job_counts: {
            ...root.job_counts,
            retrying_locked: 1,
            blocked_locked: 1
          },
          recent_assets: [
            { path: "draft.md", file_kind: "text", status: "pending_stable", size_bytes: 2500 },
            { path: "open.docx", file_kind: "document", status: "retrying_locked", size_bytes: 64000 },
            { path: "stuck.xlsx", file_kind: "document", status: "blocked_locked", size_bytes: 32000 }
          ],
          recent_jobs: [
            { id: "job-lock", job_type: "corpus_extract_document", status: "retrying_locked", path: "open.docx" },
            { id: "job-blocked", job_type: "corpus_extract_document", status: "blocked_locked", path: "stuck.xlsx" }
          ]
        }
      ]
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));

    expect(await screen.findByRole("heading", { name: "Corpus Monitor" })).toBeInTheDocument();
    expect(await screen.findByText("2 pending stable - 2 locked")).toBeInTheDocument();
    expect(screen.getByText("1 retrying locked - 1 blocked locked")).toBeInTheDocument();
    expect(screen.getByText("draft.md")).toBeInTheDocument();
    expect(screen.getAllByText("Pending Stable").length).toBeGreaterThan(0);
    expect(screen.getAllByText("open.docx").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Retrying Locked").length).toBeGreaterThan(0);
    expect(screen.getAllByText("stuck.xlsx").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Blocked Locked").length).toBeGreaterThan(0);
  });

  test("mail tab shows only mail-focused panels and profile-scoped OAuth actions", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));

    expect(screen.getByRole("heading", { name: "Mail Profiles" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Extraction Backlog/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Settings Changes Requiring Restart/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Developer Debug Drawer" })).not.toBeInTheDocument();
    expect(document.querySelector(".floating-oauth")).toBeNull();

    const profileDetails = screen.getByRole("heading", { name: "Profile Details" }).closest(".panel");
    expect(profileDetails).not.toBeNull();
    expect(within(profileDetails as HTMLElement).getByRole("button", { name: /Gmail OAuth/i })).toBeInTheDocument();
    expect(within(profileDetails as HTMLElement).getByText("blocked_auth_required")).toBeInTheDocument();
  });

  test("mail dashboard renders IMAP scheduler counts and selected profile run history", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));

    const schedulerPanel = await screen.findByRole("heading", { name: "IMAP Scheduler" }).then((heading) => heading.closest(".panel"));
    expect(schedulerPanel).not.toBeNull();
    expect(within(schedulerPanel as HTMLElement).getByText("Due")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("1 due")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("Running")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("1 running")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("Blocked Auth")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("1 blocked")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("Backoff")).toBeInTheDocument();
    expect(within(schedulerPanel as HTMLElement).getByText("1 retrying")).toBeInTheDocument();

    const details = screen.getByRole("heading", { name: "Profile Details" }).closest(".panel");
    expect(details).not.toBeNull();
    expect(within(details as HTMLElement).getByText("Run History")).toBeInTheDocument();
    expect(within(details as HTMLElement).getByText("Backoff")).toBeInTheDocument();
    expect(within(details as HTMLElement).getByText("Blocked Auth Required")).toBeInTheDocument();
    expect(within(details as HTMLElement).getByText("IMAP search timed out")).toBeInTheDocument();
    expect(within(details as HTMLElement).getByText("1 missed")).toBeInTheDocument();
  });

  test("mail errors panel ignores corpus extraction errors from mail spool paths", async () => {
    const corpusError =
      "Command '['/usr/bin/pdftoppm'] timed out for /app/private/mail-spool/outlook-mohesr/ready/message/attachments/scan.pdf";
    healthPayload = {
      ...health,
      recent_errors: [corpusError],
      status: { ...health.status, recent_errors: [corpusError] }
    };
    mailPayload = { ...(mail as Record<string, unknown>), errored_messages: 0 };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));

    const panel = screen.getByRole("heading", { name: "Mail Errors" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("No mail errors.")).toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText(corpusError)).not.toBeInTheDocument();
  });

  test("mail tab shows post-process outcomes and runs dry-run for selected profile", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));

    const panel = screen.getByRole("heading", { name: "Post Process" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("Remove Label")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Planned")).toBeInTheDocument();

    await user.click(within(panel as HTMLElement).getByRole("button", { name: "Dry run post process for gmail-capture" }));

    await waitFor(() => {
      expect(postProcessDryRunPayload).toEqual({ limit: 5 });
    });
    expect(await screen.findByText("Post-process dry-run planned 1 action.")).toBeInTheDocument();
  });

  test("manual IMAP sync surfaces the created run state", async () => {
    mailSyncPayload = {
      profiles: [{ profile: "gmail-capture", status: "queued", run_id: "run-manual", exported: 0 }],
      count: 1
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));
    await user.click(screen.getByRole("button", { name: "Sync selected profile" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("IMAP sync queued for gmail-capture (run run-manual)");
  });

  test("mail profile inspector saves the Gmail OAuth client JSON path", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));

    await user.clear(screen.getByLabelText("Private client JSON"));
    await user.type(screen.getByLabelText("Private client JSON"), "private/client_secret_custom.json");
    await user.click(screen.getByRole("button", { name: "Save OAuth client JSON path" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/mail/profiles/gmail-capture/oauth-client-config",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({ client_config_path: "private/client_secret_custom.json" })
        })
      );
    });
    expect(await screen.findByText("OAuth client JSON path saved for gmail-capture.")).toBeInTheDocument();
  });

  test("gmail oauth opens a user-initiated consent window from authorization_url", async () => {
    const popup = {
      closed: false,
      location: { assign: vi.fn() },
      close: vi.fn(),
      document: { title: "", body: { innerHTML: "" } }
    };
    const open = vi.fn(() => popup);
    vi.stubGlobal("open", open);

    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));
    await user.click(screen.getByRole("button", { name: "Gmail OAuth for gmail-capture" }));

    expect(open).toHaveBeenCalledWith("about:blank", "_blank");
    await waitFor(() => {
      expect(popup.location.assign).toHaveBeenCalledWith("https://accounts.google.com/o/oauth2/v2/auth?state=test");
    });
    expect(await screen.findByText("Gmail OAuth opened for gmail-capture.")).toBeInTheDocument();
  });

  test("mail sync failures show detailed red errors instead of success banners", async () => {
    mailSyncPayload = {
      profiles: [
        {
          profile: "gmail-capture",
          status: "auth_failed",
          exported: 0,
          errors: [
            {
              folder: "FluxCapture",
              stage: "authenticate_xoauth2",
              error: "AUTHENTICATE command error: BAD Invalid SASL argument"
            }
          ]
        }
      ],
      count: 1
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Select gmail-capture" }));
    await user.click(screen.getByRole("button", { name: "Sync selected profile" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveClass("error");
    expect(alert).toHaveTextContent("Sync failed for gmail-capture: auth_failed");
    expect(alert).toHaveTextContent("FluxCapture");
    expect(alert).toHaveTextContent("authenticate_xoauth2");
    expect(alert).toHaveTextContent("Invalid SASL argument");
  });

  test("retrieval tab documents REST MCP and CLI consumer access", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));

    expect(await screen.findByRole("heading", { name: "Consumer Access" })).toBeInTheDocument();
    expect(screen.getByText(/GET \/api\/search\?query=/)).toBeInTheDocument();
    expect(screen.getByText(/^kb\.search/)).toBeInTheDocument();
    expect(screen.getByText(/flux-kb search/)).toBeInTheDocument();
  });

  test("retrieval results render backend summaries and do not present RRF as a confidence percent", async () => {
    searchPayload = [
      {
        kind: "corpus_chunk",
        title: "Mail: YsTrader alert",
        summary: "From YsTrader; folder FluxCapture; 0 attachments.",
        score: 0.032,
        streams: ["corpus_lexical", "corpus_trust"],
        source_path: "export-1/manifest.json"
      }
    ];
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "ystrader{enter}");

    expect(await screen.findByText("Mail: YsTrader alert")).toBeInTheDocument();
    expect(screen.getByText("From YsTrader; folder FluxCapture; 0 attachments.")).toBeInTheDocument();
    expect(screen.queryByText(/3%/)).not.toBeInTheDocument();
    expect(screen.getByText("export-1/manifest.json")).toBeInTheDocument();
  });

  test("add profile opens a real form and persists an IMAP profile", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Add Profile" }));

    expect(screen.getByRole("dialog", { name: "Add Mail Profile" })).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Profile name"));
    await user.type(screen.getByLabelText("Profile name"), "team-gmail");
    await user.clear(screen.getByLabelText("Account"));
    await user.type(screen.getByLabelText("Account"), "team@example.com");
    await user.clear(screen.getByLabelText("Server"));
    await user.type(screen.getByLabelText("Server"), "imap.gmail.com");
    await user.clear(screen.getByLabelText("Folders or labels"));
    await user.type(screen.getByLabelText("Folders or labels"), "FluxCapture\nArchive/Flux");
    await user.clear(screen.getByLabelText("Private spool path"));
    await user.type(screen.getByLabelText("Private spool path"), "private/mail-spool/team-gmail");
    await user.click(screen.getByRole("button", { name: "Save profile" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/mail/profiles",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({
            name: "team-gmail",
            source_type: "imap",
            account: "team@example.com",
            server: "imap.gmail.com",
            folder_paths: ["FluxCapture", "Archive/Flux"],
            spool_path: "private/mail-spool/team-gmail",
            post_process_policy: "move_to_processed",
            processed_folder: "FluxProcessed",
            trash_folder: "",
            destructive_post_process_confirmed: false,
            sync_enabled: false,
            sync_interval_seconds: 900,
            sync_window_days: 30,
            max_messages_per_run: 200
          })
        })
      );
    });
    expect(await screen.findByText("Mail profile saved.")).toBeInTheDocument();
  });

  test("Outlook COM profile form hides IMAP fields and saves manual host profile defaults", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Add Profile" }));
    await user.selectOptions(screen.getByLabelText("Source"), "outlook_com");

    expect(screen.queryByLabelText("Account")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Server")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Interval seconds")).not.toBeInTheDocument();
    expect(screen.getByText(/uses the local Windows Outlook host/i)).toBeInTheDocument();
    expect(screen.getByLabelText("Post process")).toHaveValue("none");
    expect(screen.getByLabelText("Include subfolders")).toBeChecked();
    expect(screen.getByLabelText("Outlook incremental mode")).toHaveValue("received_time");

    await user.click(screen.getByLabelText("Scheduled sync enabled"));
    expect(screen.getByLabelText("Interval seconds")).toHaveValue(900);
    await user.click(screen.getByLabelText("Scheduled sync enabled"));
    expect(screen.queryByLabelText("Interval seconds")).not.toBeInTheDocument();

    await user.clear(screen.getByLabelText("Profile name"));
    await user.type(screen.getByLabelText("Profile name"), "outlook-catchup");
    await user.clear(screen.getByLabelText("Folders or labels"));
    await user.type(screen.getByLabelText("Folders or labels"), "Mailbox - Me\\Inbox\\Flux Capture");
    await user.clear(screen.getByLabelText("Private spool path"));
    await user.type(screen.getByLabelText("Private spool path"), "private/mail-spool/outlook-catchup");
    await user.selectOptions(screen.getByLabelText("Outlook incremental mode"), "last_modification_time");
    await user.click(screen.getByRole("button", { name: "Save profile" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/mail/profiles",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({
            name: "outlook-catchup",
            source_type: "outlook_com",
            account: null,
            server: null,
            folder_paths: ["Mailbox - Me\\Inbox\\Flux Capture"],
            spool_path: "private/mail-spool/outlook-catchup",
            post_process_policy: "none",
            processed_folder: "",
            trash_folder: "",
            destructive_post_process_confirmed: false,
            sync_enabled: false,
            sync_interval_seconds: 900,
            sync_window_days: 30,
            max_messages_per_run: 200,
            include_subfolders: true,
            outlook_incremental_basis: "last_modification_time"
          })
        })
      );
    });
  });

  test("mail profile form persists post-process policy metadata", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "Add Profile" }));

    await user.clear(screen.getByLabelText("Profile name"));
    await user.type(screen.getByLabelText("Profile name"), "team-gmail");
    await user.clear(screen.getByLabelText("Private spool path"));
    await user.type(screen.getByLabelText("Private spool path"), "private/mail-spool/team-gmail");
    await user.selectOptions(screen.getByLabelText("Post process"), "remove_label");
    await user.clear(screen.getByLabelText("Processed folder or label"));
    await user.type(screen.getByLabelText("Processed folder or label"), "FluxProcessed");
    await user.clear(screen.getByLabelText("Trash folder"));
    await user.click(screen.getByLabelText("Trash folder"));
    await user.paste("[Gmail]/Trash");
    await user.click(screen.getByLabelText("Confirm destructive post-process action"));
    await user.click(screen.getByRole("button", { name: "Save profile" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/mail/profiles",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({
            name: "team-gmail",
            source_type: "imap",
            account: "me@gmail.com",
            server: "imap.gmail.com",
            folder_paths: ["FluxCapture"],
            spool_path: "private/mail-spool/team-gmail",
            post_process_policy: "remove_label",
            processed_folder: "FluxProcessed",
            trash_folder: "[Gmail]/Trash",
            destructive_post_process_confirmed: true,
            sync_enabled: false,
            sync_interval_seconds: 900,
            sync_window_days: 30,
            max_messages_per_run: 200
          })
        })
      );
    });
  });

  test("corpus tab can add a watched path with policy fields", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    const addButton = screen.getByRole("button", { name: "Add Watched Path" });
    expect(addButton).toHaveAttribute("title", expect.stringContaining("monitored root"));
    await user.click(addButton);

    expect(screen.getByRole("dialog", { name: "Add Watched Path" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Browse" }));
    expect(screen.getByLabelText("Root path")).toHaveValue("E:\\Temp\\watch-test");
    await user.clear(screen.getByLabelText("Root path"));
    await user.type(screen.getByLabelText("Root path"), "E:/Client RFPs");
    await user.clear(screen.getByLabelText("Root name"));
    await user.type(screen.getByLabelText("Root name"), "client-rfps");
    await user.clear(screen.getByLabelText("Include globs"));
    await user.type(screen.getByLabelText("Include globs"), "**/*.pdf\n**/*.docx");
    await user.clear(screen.getByLabelText("Exclude globs"));
    await user.type(screen.getByLabelText("Exclude globs"), "private/**");
    await user.clear(screen.getByLabelText("Inline size bytes"));
    await user.type(screen.getByLabelText("Inline size bytes"), "131072");
    await user.clear(screen.getByLabelText("Heavy file threshold bytes"));
    await user.type(screen.getByLabelText("Heavy file threshold bytes"), "5242880");
    await user.click(screen.getByRole("button", { name: "Save watched path" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/roots",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({
            name: "client-rfps",
            root_path: "E:/Client RFPs",
            recursive: true,
            watch_enabled: true,
            initial_crawl: true,
            glob_mode: "extend",
            trust_rank: 500,
            include_globs: ["**/*.pdf", "**/*.docx"],
            exclude_globs: ["private/**"],
            max_inline_bytes: 131072,
            heavy_threshold_bytes: 5242880
          })
        })
      );
    });
  });

  test("corpus root actions call scoped sync, dry-run, and watch APIs", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    expect((await screen.findAllByText("E:/Flux Docs")).length).toBeGreaterThan(0);
    expect(screen.getAllByText("watching").length).toBeGreaterThan(0);
    expect(screen.getAllByText("clip.mp4").length).toBeGreaterThan(0);
    expect(screen.getByText("Effective include globs")).toBeInTheDocument();
    expect(screen.queryByText(/Start .*flux-kb crawl worker run/)).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Sync docs" }));
    await user.click(screen.getByRole("button", { name: "Dry run docs" }));
    await user.click(screen.getByRole("button", { name: "Disable watch docs" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/sync",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ root_name: "docs", dry_run: false }) })
      );
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/sync",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ root_name: "docs", dry_run: true }) })
      );
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/watch",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ root_name: "docs", enabled: false }) })
      );
    });
  });

  test("corpus root can be edited, backfilled, and deleted with purge confirmation", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    expect((await screen.findAllByText("docs")).length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: "Edit docs" }));
    expect(screen.getByRole("dialog", { name: "Edit Watched Path" })).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Root name"));
    await user.type(screen.getByLabelText("Root name"), "docs-edited");
    await user.click(screen.getByRole("button", { name: "Save watched path" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/roots/docs",
        expect.objectContaining({
          method: "PATCH",
          body: expect.stringContaining("docs-edited")
        })
      );
    });

    await user.click(screen.getByRole("button", { name: "Run backfill for docs" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/crawl/backfill",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ kind: "all", limit: 10, workers: 1, root_name: "docs" }) })
      );
    });

    await user.click(screen.getByRole("button", { name: "Delete docs" }));
    expect(screen.getByRole("dialog", { name: "Delete watched path" })).toHaveTextContent("does not delete files from disk");
    await user.click(screen.getByRole("button", { name: "Delete watched path and purge index" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith("/api/crawl/roots/docs?purge_index=true", expect.objectContaining({ method: "DELETE" }));
    });
  });

  test("settings editor saves live settings and confirms reindex-class changes", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Settings" }));
    await user.click(await screen.findByRole("button", { name: "Edit retrieval.token_budget" }));
    await user.clear(screen.getByLabelText("Setting value"));
    await user.type(screen.getByLabelText("Setting value"), "1600");
    await user.click(screen.getByRole("button", { name: "Save setting" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/settings/retrieval.token_budget",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({ value: 1600, confirmed: false, reason: "dashboard update" })
        })
      );
    });

    await user.click(screen.getByRole("button", { name: "Edit embedding.dimensions" }));
    await user.clear(screen.getByLabelText("Setting value"));
    await user.type(screen.getByLabelText("Setting value"), "768");
    await user.click(screen.getByRole("button", { name: "Save setting" }));
    expect(screen.getByRole("dialog", { name: "Confirm setting change" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Confirm and save" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/settings/embedding.dimensions",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({ value: 768, confirmed: true, reason: "dashboard update" })
        })
      );
    });
  });

  test("search, error details, theme, and menus expose visible state changes", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "More actions" }));
    expect(screen.getByRole("menu", { name: "More actions" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Dark" }));
    expect(document.documentElement.dataset.theme).toBe("dark");

    await user.type(screen.getByLabelText("Dashboard search"), "dashboard{enter}");
    expect(await screen.findByText("Dashboard Operations")).toBeInTheDocument();
    expect(screen.getByText("Dashboard search result with highlighted operations.")).toBeInTheDocument();
    await user.click(screen.getByText("Why this result"));
    expect(screen.getByText("Corpus Lexical, Corpus Vector")).toBeInTheDocument();
    expect(screen.getByText("local")).toBeInTheDocument();
    expect(screen.getByText("Lifecycle penalties")).toBeInTheDocument();
    expect(screen.getByText("state 1.000, retention 0.600")).toBeInTheDocument();
    expect(screen.getByText("Exact duplicates")).toBeInTheDocument();
    expect(screen.getByText("Same document versions")).toBeInTheDocument();
    expect(screen.getByText("1 suppressed")).toBeInTheDocument();
    expect(screen.getByText("Semantic duplicates")).toBeInTheDocument();
    expect(screen.getAllByText("2 suppressed").length).toBeGreaterThanOrEqual(2);

    await user.click(screen.getByRole("button", { name: "View error ffprobe command not found" }));
    expect(screen.getByRole("dialog", { name: "Error detail" })).toHaveTextContent("ffprobe command not found");
  });

  test("retrieval filters use explain endpoint and render exclusion trace", async () => {
    explainPayload = {
      query: "customer rfp",
      results: [],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filters: {
        logical_kinds: ["mail"],
        current_only: true,
        lifecycle_states: [],
        include_suppressed: true
      },
      filter_trace: {
        excluded: [
          { id: "chunk-file", title: "File result", kind: "file", reason: "logical_kind", score: 0.8 },
          { id: "episode-old", title: "Old decision", kind: "episode", reason: "current_only", score: 0.7, lifecycle_state: "retired" }
        ]
      },
      suppression: {
        exact_duplicates: [{ title: "RFP", suppressed_count: 3, reason: "exact_content_duplicate" }],
        version_families: [{ title: "Proposal", suppressed_count: 1, reason: "same_document_version_family" }],
        semantic_duplicates: [{ title: "Proposal Copy", suppressed_count: 2, reason: "semantic_near_duplicate" }]
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(screen.getByLabelText("Search focus"), "mail");
    await user.click(screen.getByLabelText("Current evidence only"));
    await user.click(screen.getByLabelText("Show suppressed diagnostics"));
    await user.type(screen.getByLabelText("Dashboard search"), "customer rfp{enter}");

    await screen.findByText("Filtered out 2 candidates");
    expect(screen.getByText("File result - logical kind")).toBeInTheDocument();
    expect(screen.getByText("Old decision - current only")).toBeInTheDocument();
    expect(screen.getByText("Suppressed evidence")).toBeInTheDocument();
    expect(screen.getByText("Exact duplicates: 3")).toBeInTheDocument();
    expect(screen.getByText("Version families: 1")).toBeInTheDocument();
    expect(screen.getByText("Semantic duplicates: 2")).toBeInTheDocument();
    expect(explainRequestPayload).toEqual({
      query: "customer rfp",
      limit: 8,
      filters: {
        logical_kinds: ["mail"],
        current_only: true,
        lifecycle_states: [],
        include_suppressed: true
      }
    });
  });

  test("retrieval search focus maps docs and code filters to explain", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(screen.getByLabelText("Search focus"), "docs");
    await user.type(screen.getByLabelText("Dashboard search"), "agent guidance{enter}");

    await waitFor(() => expect(explainRequestPayload).toEqual({
      query: "agent guidance",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["text", "document", "image"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    }));

    searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        file_kind: "code",
        title: "OrderService.build_invoice",
        excerpt: "def build_invoice(order_id): return order_id",
        score: 0.99,
        streams: ["code_symbol_exact"],
        snippet: { text: "def build_invoice(order_id): return order_id", matched_terms: ["build_invoice"] },
        retrieval_explanation: {
          score: 0.99,
          streams: ["code_symbol_exact"],
          raw_scores: { code_symbol_exact: 2.5 },
          scope: { label: "local" }
        }
      }
    ];
    await user.clear(screen.getByLabelText("Dashboard search"));
    await user.selectOptions(screen.getByLabelText("Search focus"), "code");
    await user.type(screen.getByLabelText("Dashboard search"), "build_invoice{enter}");

    expect(await screen.findByText("OrderService.build_invoice")).toBeInTheDocument();
    expect(screen.getByText("Why this result")).toBeInTheDocument();
    expect(explainRequestPayload).toEqual({
      query: "build_invoice",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["code"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    });
  });

  test("balanced code-heavy results show diagnostic and rerun docs files", async () => {
    explainPayload = {
      query: "closeout failed_step log_path",
      results: [
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "code",
          title: "src/hooks.py::failed_step",
          excerpt: "failed_step = result.failed_step",
          score: 0.24,
          streams: ["code_symbol_exact"],
          snippet: { text: "failed_step = result.failed_step", matched_terms: ["failed_step"] },
          retrieval_explanation: {
            score: 0.24,
            streams: ["code_symbol_exact"],
            raw_scores: { code_symbol_exact: 0.24 },
            scope: { label: "local" }
          }
        },
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "code",
          title: "src/hooks.py::log_path",
          excerpt: "log_path = result.log_path",
          score: 0.2,
          streams: ["code_symbol_exact"],
          snippet: { text: "log_path = result.log_path", matched_terms: ["log_path"] }
        }
      ],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filter_trace: { excluded: [] },
      suppression: {}
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.type(screen.getByLabelText("Dashboard search"), "closeout failed_step log_path{enter}");

    expect(await screen.findByText("Balanced results are code-heavy.")).toBeInTheDocument();
    expect(screen.getAllByText(/code - 1 matched term/i).length).toBeGreaterThanOrEqual(2);

    explainPayload = {
      query: "closeout failed_step log_path",
      results: [
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "text",
          title: "AGENTS.md",
          excerpt: "If closeout fails, report failed_step and log_path.",
          score: 0.91,
          streams: ["corpus_lexical"],
          snippet: {
            text: "If closeout fails, report failed_step and log_path.",
            matched_terms: ["failed_step", "log_path"]
          }
        }
      ],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filter_trace: { excluded: [] },
      suppression: {}
    };
    await user.click(screen.getByRole("button", { name: "Rerun Docs/files" }));

    expect(await screen.findByText("AGENTS.md")).toBeInTheDocument();
    expect(explainRequestPayload).toEqual({
      query: "closeout failed_step log_path",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["text", "document", "image"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    });
  });

  test("retrieval tab runs and displays retrieval benchmark history", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));

    expect(await screen.findByRole("heading", { name: "Retrieval Benchmarks" })).toBeInTheDocument();
    expect(screen.getAllByText("baseline").length).toBeGreaterThan(0);
    expect(screen.getByText("top1 80.0%")).toBeInTheDocument();
    expect(screen.getByText("brief dilution 20.0%")).toBeInTheDocument();
    expect(screen.getByText("top1 +10.0%")).toBeInTheDocument();
    expect(screen.getByText("brief dilution -5.0%")).toBeInTheDocument();
    expect(screen.getByText("High confidence: 3")).toBeInTheDocument();
    expect(screen.getByText("Semantic threshold 0.86")).toBeInTheDocument();
    expect(screen.getByText("3/4 calibration cases passed")).toBeInTheDocument();
    expect(screen.getByText("scope-filter")).toBeInTheDocument();
    expect(screen.getByText("top1 miss, scope miss")).toBeInTheDocument();
    expect(screen.getByText("current only - low confidence")).toBeInTheDocument();
    expect(screen.getByText("Expected evidence was not ranked first.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Run retrieval benchmark" }));

    await waitFor(() => {
      expect(retrievalBenchmarkRunPayload).toEqual({
        suite: "standard",
        label: "dashboard",
        limit_per_query: 5,
        persist: true
      });
    });
    await waitFor(() => {
      expect(screen.getAllByText("nightly").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("settings_mutated false")).toBeInTheDocument();
    expect(screen.getByText("Synthetic semantic duplicate calibration passed for 3/4 cases at threshold 0.86.")).toBeInTheDocument();
  });

  test("retrieval tab runs governance-shadow benchmark and displays shadow evidence", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(await screen.findByLabelText("Benchmark suite"), "governance-shadow");
    await user.click(screen.getByRole("button", { name: "Run retrieval benchmark" }));

    await waitFor(() => {
      expect(retrievalBenchmarkRunPayload).toEqual({
        suite: "governance-shadow",
        label: "dashboard",
        limit_per_query: 5,
        persist: true
      });
    });
    expect(await screen.findByText("Governance shadow evaluation")).toBeInTheDocument();
    expect(screen.getByText("proposal precision 75.0%")).toBeInTheDocument();
    expect(screen.getByText("guardrails 1/1 passed")).toBeInTheDocument();
  });

  test("diagnostics renders structured errors with details, copy, and target navigation", async () => {
    const writeText = vi.fn(async () => undefined);
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    Object.defineProperty(globalThis.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    expect(window.navigator.clipboard?.writeText).toBe(writeText);
    healthPayload = {
      ...health,
      recent_error_details: [
        {
          code: "mail.oauth_unavailable",
          message: "OAuth database unavailable",
          severity: "error",
          component: "mail",
          stage: "oauth",
          retryable: true,
          user_action: "Open Mail and recheck OAuth configuration.",
          technical_detail: "mail OAuth lookup failed for gmail-capture",
          target: { type: "mail_profile", id: "gmail-capture" },
          links: [{ label: "Mail", tab: "mail", profile: "gmail-capture" }],
          status_code: null
        },
        {
          code: "corpus.job_failed",
          message: "PDF extraction failed",
          severity: "error",
          component: "worker",
          stage: "corpus_extract_pdf",
          retryable: true,
          user_action: "Open Jobs and inspect the failed task.",
          technical_detail: "job-1 failed while extracting docs/proposal.pdf",
          target: { type: "job", id: "job-1" },
          links: [{ label: "Jobs", tab: "jobs" }],
          status_code: null
      }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Diagnostics" }));
    const panel = screen.getByRole("heading", { name: "Actionable Diagnostics" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("OAuth database unavailable")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Open Mail and recheck OAuth configuration.")).toBeInTheDocument();

    await user.click(within(panel as HTMLElement).getByRole("button", { name: "Show diagnostic detail mail.oauth_unavailable" }));
    expect(within(panel as HTMLElement).getByText("mail OAuth lookup failed for gmail-capture")).toBeInTheDocument();

    const expandedPanel = screen.getByRole("heading", { name: "Actionable Diagnostics" }).closest(".panel");
    await user.click(within(expandedPanel as HTMLElement).getByRole("button", { name: "Copy diagnostic mail.oauth_unavailable" }));
    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"code": "mail.oauth_unavailable"'));
    });

    await user.click(within(expandedPanel as HTMLElement).getByRole("button", { name: "Open Mail for mail.oauth_unavailable" }));
    expect(await screen.findByRole("heading", { name: "Mail Profiles" })).toBeInTheDocument();
    expect(screen.getAllByText("gmail-capture").length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: "Diagnostics" }));
    await user.click(screen.getByRole("button", { name: "Open Jobs for corpus.job_failed" }));
    expect(await screen.findByRole("heading", { name: "Job Queue" })).toBeInTheDocument();
  });

  test("structured API error envelopes produce readable error toasts", async () => {
    crawlSyncErrorPayload = {
      error: {
        code: "crawl.root_invalid",
        message: "Watched path is missing",
        severity: "error",
        component: "crawler",
        stage: "validate_path",
        retryable: false,
        user_action: "Choose an existing directory.",
        technical_detail: "directory does not exist: E:/Missing",
        target: { type: "root", id: "docs" },
        links: [{ label: "Corpus", tab: "corpus" }],
        status_code: 400
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    await user.click(screen.getByRole("button", { name: "Sync docs" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Watched path is missing");
    expect(alert).not.toHaveTextContent("code=crawl.root_invalid");
  });

  test("clicking a mail search result opens a sanitized in-app mail detail viewer", async () => {
    searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "mail",
        id: "chunk-mail",
        title: "Mail: Customer RFP",
        summary: "From Sender; folder FluxCapture; 1 attachment.",
        source_path: "export-1/manifest.json",
        detail_ref: { kind: "corpus_chunk", id: "chunk-mail" },
        related_evidence_count: 2
      }
    ];
    resultDetailPayload = {
      logical_kind: "mail",
      title: "Mail: Customer RFP",
      mail: {
        subject: "Customer RFP",
        sender: "Sender <sender@example.com>",
        recipients: ["me@example.com"],
        received_at: "Tue, 23 Jun 2026 10:00:00 +0000",
        profile_name: "gmail-capture",
        source_folder: "FluxCapture",
        post_process_state: "exported"
      },
      body: {
        format: "html",
        html_sanitized: '<p>Please <strong>review</strong> the RFP.</p>',
        text: ""
      },
      attachments: [{ title: "rfp.pdf", path: "export-1/attachments/rfp.pdf", status: "metadata_only" }],
      related_evidence: [{ title: "body.html", path: "export-1/body.html", relationship: "body" }],
      provenance: [{ path: "export-1/manifest.json" }]
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "customer rfp{enter}");
    await user.click(await screen.findByRole("button", { name: /Mail: Customer RFP/ }));

    const dialog = await screen.findByRole("dialog", { name: "Mail: Customer RFP" });
    expect(dialog).toHaveTextContent("Sender <sender@example.com>");
    expect(dialog).toHaveTextContent("me@example.com");
    expect(dialog).toHaveTextContent("gmail-capture");
    expect(dialog).toHaveTextContent("FluxCapture");
    expect(dialog).toHaveTextContent("Please review the RFP.");
    expect(dialog).toHaveTextContent("rfp.pdf");
    expect(dialog.innerHTML).not.toContain("onclick");
    expect(dialog.innerHTML).not.toContain("<script");
    expect(screen.queryByText("export-1/body.txt")).not.toBeInTheDocument();
  });

  test("file result detail previews text, copies path, and routes open and reveal actions", async () => {
    const writeText = vi.fn(async () => undefined);
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    expect(window.navigator.clipboard?.writeText).toBe(writeText);
    searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        id: "chunk-file",
        asset_id: "asset-file",
        title: "Project Plan",
        excerpt: "Milestone details",
        source_path: "plans/project-plan.md",
        detail_ref: { kind: "corpus_chunk", id: "chunk-file" }
      }
    ];
    resultDetailPayload = {
      logical_kind: "file",
      title: "Project Plan",
      asset_id: "asset-file",
      metadata: { path: "plans/project-plan.md", canonical_path: "E:/Flux Docs/plans/project-plan.md", status: "indexed" },
      preview: { available: true, text: "Milestone details and owners.", chunks: [] },
      actions: {
        copy_path: { available: true, path: "E:/Flux Docs/plans/project-plan.md" },
        open: { available: true },
        reveal: { available: true }
      },
      related_evidence: [],
      provenance: []
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "project plan{enter}");
    await user.click(await screen.findByRole("button", { name: /Project Plan/ }));

    expect(await screen.findByRole("dialog", { name: "Project Plan" })).toHaveTextContent("Milestone details and owners.");
    const copyButton = screen.getByRole("button", { name: "Copy path" });
    expect(screen.getByText("E:/Flux Docs/plans/project-plan.md")).toBeInTheDocument();
    expect(copyButton).toBeEnabled();
    await user.click(copyButton);
    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith("E:/Flux Docs/plans/project-plan.md");
    });
    await user.click(screen.getByRole("button", { name: "Open with default app" }));
    await user.click(screen.getByRole("button", { name: "Reveal in folder" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/corpus/assets/asset-file/actions",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ action: "open" }) })
      );
      expect(fetch).toHaveBeenCalledWith(
        "/api/corpus/assets/asset-file/actions",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ action: "reveal" }) })
      );
    });
  });

  test("file detail disables unavailable actions with readable reasons", async () => {
    searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        id: "chunk-deleted",
        asset_id: "asset-deleted",
        title: "Deleted Proposal",
        excerpt: "deleted",
        source_path: "archive/deleted.docx",
        detail_ref: { kind: "corpus_chunk", id: "chunk-deleted" }
      }
    ];
    resultDetailPayload = {
      logical_kind: "file",
      title: "Deleted Proposal",
      asset_id: "asset-deleted",
      metadata: { path: "archive/deleted.docx", canonical_path: "E:/Flux Docs/archive/deleted.docx", status: "deleted" },
      preview: { available: false, text: "", chunks: [] },
      actions: {
        copy_path: { available: true, path: "E:/Flux Docs/archive/deleted.docx" },
        open: { available: false, disabled_reason: "Asset is deleted from the index." },
        reveal: { available: false, disabled_reason: "Asset is deleted from the index." }
      },
      related_evidence: [],
      provenance: []
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "deleted proposal{enter}");
    await user.click(await screen.findByRole("button", { name: /Deleted Proposal/ }));

    expect(await screen.findByRole("dialog", { name: "Deleted Proposal" })).toHaveTextContent("No extracted text is available.");
    expect(screen.getByRole("button", { name: "Open with default app" })).toBeDisabled();
    expect(screen.getByText("Asset is deleted from the index.")).toBeInTheDocument();
  });
});

function json(payload: unknown): Response {
  return {
    ok: true,
    json: async () => payload
  } as Response;
}

function errorJson(payload: unknown, status: number, statusText: string): Response {
  return {
    ok: false,
    status,
    statusText,
    text: async () => JSON.stringify(payload),
    json: async () => payload
  } as Response;
}
