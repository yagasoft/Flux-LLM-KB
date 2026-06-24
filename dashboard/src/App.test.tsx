import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import App from "./App";

const health = {
  database: { ok: true, message: "database reachable" },
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
      watcher_backend: { ok: true, state: "available", provider: "watchdog" },
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
      { family: "media", resource_class: "gpu", configured_cap: 1, pending: 2, running: 1, blocked: 1, failed: 0, avg_duration_ms: 24, p95_duration_ms: 95, ocr_cache_hits: 6, ocr_cache_misses: 2, asr_cache_hits: 4, asr_cache_misses: 1, asr_segments: 9 },
      { family: "office", resource_class: "cpu", configured_cap: 2, pending: 3, running: 0, blocked: 0, failed: 1, avg_duration_ms: 12, p95_duration_ms: 40 }
    ]
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
let jobsPayload: unknown;
let crawlSyncErrorPayload: unknown;
let reviewPayload: unknown;
let captureReviewPayload: unknown;
let captureReviewDecisionPayload: unknown;
let auditPayload: unknown;
let graphPayload: unknown;
let claimTransitionPayload: unknown;
let postProcessDryRunPayload: unknown;
let retentionPoliciesPayload: unknown;
let retentionQualityPayload: unknown;
let retentionPolicyUpdatePayload: unknown;

describe("Flux dashboard", () => {
  beforeEach(() => {
    healthPayload = health;
    crawlPayload = JSON.parse(JSON.stringify(crawl));
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
      ]
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
            version_family: { suppressed_count: 1, reason: "same_document_version_family", canonical_source_path: "docs/operations.md" }
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
    retentionPolicyUpdatePayload = undefined;
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
      if (url === "/api/dashboard/jobs") return json(jobsPayload);
      if (url === "/api/dashboard/retrieval-stats") return json({ retrieval: health.retrieval, duplicate_assets: 0 });
      if (url === "/api/mail/status") return json(mail);
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
      if (url.startsWith("/api/capture/review")) return json(captureReviewPayload);
      if (url.startsWith("/api/audit")) return json(auditPayload);
      if (url.startsWith("/api/graph/traverse")) return json(graphPayload);
      if (url === "/api/outlook-host/request-sync") {
        return json({ id: "req-1", status: "pending", profile_name: JSON.parse(String(init?.body)).profile_name });
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

  test("defaults to health and renders the operations console without primary raw JSON panels", async () => {
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "System Health" })).toBeInTheDocument();
    expect(screen.getByText("PostgreSQL")).toBeInTheDocument();
    expect(screen.getByText("Outlook Host")).toBeInTheDocument();
    expect(screen.getByText("Host Agent")).toBeInTheDocument();
    expect(screen.getByText("Codex Integration")).toBeInTheDocument();
    expect(screen.getByText("Codex restart required")).toBeInTheDocument();
    expect(screen.getByText(/Auto-refresh every 1s/i)).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Mail profiles" })).not.toBeInTheDocument();
    expect(screen.queryByText(/"database"/)).not.toBeInTheDocument();
  });

  test("health shows Codex hook policy status and settings expose hook controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Codex Hooks" })).toBeInTheDocument();
    expect(screen.getByText("Preflight brief")).toBeInTheDocument();
    expect(screen.getByText("Turn capture")).toBeInTheDocument();
    expect(screen.getByText("codex_hook.preflight_injected")).toBeInTheDocument();
    expect(screen.getByText("MCP tools")).toBeInTheDocument();
    expect(screen.getByText("kb.brief ready")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Settings" }));
    await waitFor(() => {
      expect(screen.getAllByText("codex.hooks.enabled").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("Enable Flux Codex hook policy evaluation.")).toBeInTheDocument();
  });

  test("health shows acceleration capabilities, cache layout, and family telemetry", async () => {
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Acceleration" })).toBeInTheDocument();
    expect(screen.getByText("NVIDIA")).toBeInTheDocument();
    expect(screen.getByText("nvidia-smi not found")).toBeInTheDocument();
    expect(screen.getByText("Local Model")).toBeInTheDocument();
    expect(screen.getByText("disabled")).toBeInTheDocument();
    expect(screen.getByText("D:/FluxLLMKB/private/cache")).toBeInTheDocument();
    expect(screen.getByText("media")).toBeInTheDocument();
    expect(screen.getByText("p95 95ms; OCR 6 hit / 2 miss; ASR 4 hit / 1 miss; 9 segments")).toBeInTheDocument();
    expect(screen.getByText("office")).toBeInTheDocument();
    expect(screen.getByText("3 pending")).toBeInTheDocument();
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
      expect(fetch).toHaveBeenCalledTimes(14);
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
    expect(screen.getByText("2 suppressed")).toBeInTheDocument();
    expect(screen.getByText("Same document versions")).toBeInTheDocument();
    expect(screen.getByText("1 suppressed")).toBeInTheDocument();

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
        version_families: [{ title: "Proposal", suppressed_count: 1, reason: "same_document_version_family" }]
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(screen.getByLabelText("Evidence kind"), "mail");
    await user.click(screen.getByLabelText("Current evidence only"));
    await user.click(screen.getByLabelText("Show suppressed diagnostics"));
    await user.type(screen.getByLabelText("Dashboard search"), "customer rfp{enter}");

    await screen.findByText("Filtered out 2 candidates");
    expect(screen.getByText("File result - logical kind")).toBeInTheDocument();
    expect(screen.getByText("Old decision - current only")).toBeInTheDocument();
    expect(screen.getByText("Suppressed evidence")).toBeInTheDocument();
    expect(screen.getByText("Exact duplicates: 3")).toBeInTheDocument();
    expect(screen.getByText("Version families: 1")).toBeInTheDocument();
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

  test("health renders structured diagnostics with details, copy, and target navigation", async () => {
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

    await user.click(screen.getByRole("button", { name: "Health" }));
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
