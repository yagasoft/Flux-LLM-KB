const now = "2026-06-26T12:00:00+00:00";

const health = {
  database: { ok: true, message: "database reachable" },
  runtime: {
    python: { ok: true, message: "3.12 runtime ready" },
    docker: { ok: true, message: "optional containers reachable" },
    git: { ok: true, message: "repository available" },
    postgresql: { ok: true, message: "pgvector extension ready" }
  },
  watcher: { active_roots: 2, disabled_roots: 1, stale_count: 1 },
  jobs: { pending: 9, failed: 1, blocked: 1 },
  retrieval: { episodes: 42, sources: 18, source_assets: 24, asset_chunks: 156, embeddings: 141 },
  acceleration: {
    capabilities: {
      cpu: { ok: true, state: "ready", count: 16, message: "16 logical CPUs available" },
      memory: { ok: true, state: "ready", total_bytes: 34359738368, message: "32 GB available" },
      watcher_backend: { ok: true, state: "available", provider: "watchdog", selected_backend: "watchdog", native: true, policy: "auto" },
      onnxruntime: { ok: true, state: "ready", providers: ["CPUExecutionProvider"] },
      local_model: { ok: false, state: "disabled", provider: "ollama", message: "local model probe disabled by setting" },
      nvidia: { ok: false, state: "missing", message: "GPU provider not configured" }
    },
    cache: {
      root: "E:/FluxManualFixtures/cache",
      source: "fixture",
      directories: {
        models: "E:/FluxManualFixtures/cache/models",
        ocr: "E:/FluxManualFixtures/cache/ocr",
        asr: "E:/FluxManualFixtures/cache/asr",
        parser: "E:/FluxManualFixtures/cache/parser",
        embeddings: "E:/FluxManualFixtures/cache/embeddings",
        temp: "E:/FluxManualFixtures/cache/temp"
      }
    },
    worker_families: [
      {
        family: "office",
        resource_class: "cpu",
        configured_cap: 2,
        cap_available: 1,
        backpressure: "healthy",
        pending: 1,
        running: 1,
        blocked: 1,
        failed: 0,
        avg_duration_ms: 1200,
        p95_duration_ms: 3600,
        parser_cache_hits: 14,
        parser_cache_misses: 3,
        manifest_skipped_unchanged: 21,
        slowest_recent_jobs: [{ id: "job-office-12", path: "Policies/quarterly-plan.docx", duration_ms: 3600 }]
      },
      {
        family: "embeddings",
        resource_class: "cpu",
        configured_cap: 4,
        cap_available: 2,
        backpressure: "normal_queue",
        pending: 8,
        running: 1,
        blocked: 0,
        failed: 0,
        embedding_vectors: 141,
        embedding_skipped_unchanged: 18,
        embedding_batches: 4,
        embedding_cache_hits: 64,
        embedding_cache_misses: 7
      },
      {
        family: "media",
        resource_class: "gpu_optional",
        configured_cap: 1,
        cap_available: 0,
        backpressure: "dependency_blocked",
        pending: 0,
        running: 0,
        blocked: 1,
        failed: 0,
        ocr_cache_hits: 5,
        ocr_cache_misses: 1,
        asr_cache_hits: 2,
        asr_cache_misses: 1,
        vision_blocked_dependency_count: 1
      }
    ],
    benchmarks: {
      history: [
        {
          id: "bench-all-roots-042",
          fixture: "all-roots",
          mode: "scan",
          scenario: "reliability",
          label: "manual-fixture",
          status: "completed",
          file_count: 40,
          elapsed_ms: 8600,
          throughput_files_per_second: 4.65,
          warm_state: "warm",
          pass_index: 2,
          hash_parallelism: 4,
          worker_count: 3,
          manifest_skipped_unchanged: 17,
          cache_hits: 63,
          cache_misses: 8,
          scope_type: "public_safe_fixture",
          deployment_label: "guide"
        }
      ]
    }
  },
  workers: {
    active: 3,
    components: [
      { name: "corpus-worker:office", status: "running", heartbeat_age_seconds: 4 },
      { name: "corpus-worker:embeddings", status: "running", heartbeat_age_seconds: 6 },
      { name: "mail-worker", status: "idle", heartbeat_age_seconds: 12 }
    ]
  },
  recent_errors: [
    "office.extractor_missing_dependency: example dependency is unavailable",
    "mail.scheduler_backoff: gmail-capture is waiting for retry backoff"
  ],
  recent_error_details: [
    {
      code: "office.extractor_missing_dependency",
      severity: "error",
      message: "Office extractor dependency is missing for one queued job.",
      user_action: "Open Diagnostics, confirm the target job, then run the safe retry after the dependency is installed.",
      technical_detail: "Public-safe fixture detail: job job-office-12 needs a local extractor dependency. No raw file content is included.",
      component: "corpus",
      stage: "extract",
      retryable: true,
      target: { type: "job", id: "job-office-12" },
      links: [{ label: "Jobs", tab: "jobs" }]
    },
    {
      code: "mail.scheduler_backoff",
      severity: "warning",
      message: "gmail-capture is waiting for retry backoff.",
      user_action: "Inspect the Mail tab before forcing a manual sync.",
      technical_detail: "Public-safe fixture detail: the IMAP request timed out during a scheduled run.",
      component: "mail",
      stage: "scheduler",
      retryable: true,
      target: { type: "mail_profile", id: "gmail-capture" },
      links: [{ label: "Mail", tab: "mail" }]
    }
  ],
  extractors: {
    office: { ok: true, message: "docx/xlsx/pptx parser ready" },
    pdf: { ok: true, message: "text extraction ready" },
    media: { ok: false, message: "optional media tools missing" }
  },
  host_agent: { status: "running", browse_supported: true, message: "folder browser available" },
  deployment: {
    install_root: "E:/FluxManualFixtures",
    app_root: "E:/FluxManualFixtures/app",
    private_dir: "E:/FluxManualFixtures/private",
    data_dir: "E:/FluxManualFixtures/data",
    logs_dir: "E:/FluxManualFixtures/logs",
    image_tag: "manual-guide",
    mode: "local",
    repo_coupled: false,
    running_from_repo: true
  },
  codex: {
    status: "ready",
    configured: true,
    installed: true,
    hooks_available: true,
    discoverable: true,
    restart_required: false,
    mcp: { configured: true, command: "python", cwd: "E:/FluxManualFixtures/app", enabled: true, dependency_available: true, message: "kb.brief ready" },
    hook_policy: {
      status: "active",
      enabled: true,
      preflight_enabled: true,
      capture_enabled: true,
      token_budget: 900,
      recent_events: [
        { event_type: "codex_hook.preflight_injected", created_at: now, details: { reason: "public_safe_fixture" } }
      ]
    }
  }
};

const crawl = {
  roots: [],
  root_summaries: [
    {
      id: "docs",
      name: "docs",
      root_path: "E:/FluxManualFixtures/Docs",
      enabled: true,
      recursive: true,
      watch_enabled: true,
      trust_rank: 720,
      include_globs: ["**/*.md", "**/*.docx", "**/*.pdf"],
      exclude_globs: ["private/**", "**/*.tmp"],
      glob_mode: "extend",
      effective_globs: { mode: "extend", include_globs: ["**/*.md", "**/*.docx", "**/*.pdf"], exclude_globs: ["private/**", "**/*.tmp"] },
      max_inline_bytes: 131072,
      heavy_threshold_bytes: 5242880,
      state: "watching",
      watcher: { status: "running", heartbeat_age_seconds: 3, last_event_at: now },
      asset_counts: { total: 42, indexed: 38, queued: 3, duplicate_suppressed: 4, deleted: 0, pending_stable: 1 },
      job_counts: { pending: 2, blocked: 1, failed: 0, running: 1, retrying_locked: 0, blocked_locked: 1 },
      latest_crawl: { status: "completed", files_seen: 42, files_changed: 4, jobs_queued: 5 },
      recent_assets: [
        { path: "OperatorGuide.md", file_kind: "markdown", status: "indexed", size_bytes: 1200 },
        { path: "Policies/quarterly-plan.docx", file_kind: "office", status: "queued", size_bytes: 820000 }
      ],
      recent_jobs: [
        { id: "job-office-12", job_type: "corpus_extract_office", status: "blocked_missing_dependency", path: "Policies/quarterly-plan.docx" },
        { id: "job-embed-08", job_type: "embedding_refresh", status: "queued", path: "OperatorGuide.md" }
      ],
      recent_errors: ["office extractor dependency missing"]
    },
    {
      id: "projects",
      name: "projects",
      root_path: "E:/FluxManualFixtures/Projects",
      enabled: true,
      recursive: true,
      watch_enabled: true,
      trust_rank: 680,
      include_globs: ["src/**/*.py", "docs/**/*.md"],
      exclude_globs: ["node_modules/**", ".venv/**"],
      glob_mode: "override",
      state: "polling_fallback",
      watcher: { status: "polling", heartbeat_age_seconds: 20 },
      asset_counts: { total: 24, indexed: 21, queued: 2, duplicate_suppressed: 2, deleted: 0 },
      job_counts: { pending: 1, blocked: 0, failed: 1, running: 0 },
      latest_crawl: { status: "completed", files_seen: 24, files_changed: 2, jobs_queued: 2 },
      recent_assets: [{ path: "src/search.py", file_kind: "python", status: "indexed", size_bytes: 6400 }],
      recent_jobs: [{ id: "job-code-03", job_type: "code_index", status: "completed", path: "src/search.py" }]
    },
    {
      id: "archive",
      name: "archive",
      root_path: "E:/FluxManualFixtures/Archive",
      enabled: false,
      recursive: true,
      watch_enabled: false,
      trust_rank: 450,
      include_globs: ["**/*"],
      exclude_globs: ["**/*.bak"],
      glob_mode: "inherit",
      state: "disabled",
      asset_counts: { total: 10, indexed: 9, queued: 0, duplicate_suppressed: 1, deleted: 0 },
      job_counts: { pending: 0, blocked: 0, failed: 0, running: 0 },
      latest_crawl: { status: "skipped_disabled", files_seen: 0, files_changed: 0, jobs_queued: 0 }
    }
  ],
  status: { active_watch_roots: 2, disabled_watch_roots: 1, recent_errors: ["office extractor dependency missing"] }
};

const jobs = {
  jobs: [
    {
      id: "job-office-12",
      job_type: "corpus_extract_office",
      status: "blocked_missing_dependency",
      family: "office",
      root_name: "docs",
      path: "Policies/quarterly-plan.docx",
      attempts: 1,
      last_error: "office extractor dependency missing",
      created_at: now,
      updated_at: now,
      payload: { root_name: "docs", path: "Policies/quarterly-plan.docx" }
    },
    {
      id: "job-embed-08",
      job_type: "embedding_refresh",
      status: "queued",
      family: "embeddings",
      root_name: "docs",
      path: "OperatorGuide.md",
      attempts: 0,
      created_at: now,
      updated_at: now,
      payload: { root_name: "docs", path: "OperatorGuide.md" }
    },
    {
      id: "job-sync-21",
      job_type: "crawl_sync",
      status: "completed",
      family: "crawler",
      root_name: "projects",
      path: "src/search.py",
      attempts: 1,
      created_at: now,
      updated_at: now,
      payload: { root_name: "projects" }
    }
  ],
  worker_families: health.acceleration.worker_families
};

const retrievalStats = {
  retrieval: health.retrieval,
  duplicate_assets: 4,
  duplicate_count: 4
};

const mail = {
  enabled_profiles: 2,
  exported_messages: 18,
  errored_messages: 1,
  scheduler: {
    counts: { due: 1, queued: 1, claimed: 0, running: 0, failed: 1, blocked_auth: 1, backoff: 1 },
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
        next_attempt_at: "2026-06-26T13:15:00+00:00",
        drift_seconds: 180,
        missed_runs: 1,
        started_at: now,
        finished_at: now
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
      { profile_name: "gmail-capture", status: "blocked_auth_required", has_refresh_token: false },
      { profile_name: "outlook-catchup", status: "not_required", has_refresh_token: false }
    ]
  },
  post_process: {
    recent_events: [
      { id: "post-process-1", profile_name: "gmail-capture", policy: "remove_label", action: "gmail_remove_label", status: "planned", dry_run: true, created_at: now }
    ]
  },
  profiles: [
    {
      name: "gmail-capture",
      source_type: "imap",
      account: "operator@example.test",
      server: "imap.example.test",
      folder_paths: ["FluxCapture", "FluxReview"],
      spool_path: "E:/FluxManualFixtures/private/mail/gmail-capture",
      sync_enabled: true,
      sync_interval_seconds: 900,
      sync_window_days: 7,
      max_messages_per_run: 25,
      post_process_policy: "remove_label",
      processed_folder: "FluxProcessed",
      trash_folder: "",
      destructive_post_process_confirmed: false,
      last_sync_at: now,
      next_sync_at: "2026-06-26T12:15:00+00:00",
      metadata: {}
    },
    {
      name: "outlook-catchup",
      source_type: "outlook_com",
      account: "operator@example.test",
      folder_paths: ["Mailbox/Inbox/Flux"],
      spool_path: "E:/FluxManualFixtures/private/mail/outlook-catchup",
      sync_enabled: false,
      sync_interval_seconds: 1800,
      sync_window_days: 14,
      max_messages_per_run: 10,
      post_process_policy: "none",
      last_sync_at: null,
      next_sync_at: null,
      metadata: {}
    }
  ]
};

const outlook = {
  host: { host_id: "default", status: "running", pid: 4242, heartbeat_age_seconds: 8, command: "flux-kb outlook-host run" },
  profiles: mail.profiles.filter((profile) => profile.source_type === "outlook_com"),
  pending_requests: [
    { id: "outlook-sync-1", profile_name: "outlook-catchup", status: "queued", requested_at: now }
  ]
};

const settings = [
  { key: "operator.automation.enabled", value: false, default: false, source: "default", category: "automation", description: "Enable recurring guarded automation passes.", apply_mode: "live", read_only: false, sensitive: false },
  { key: "operator.automation.mode", value: "guarded", default: "guarded", source: "default", category: "automation", description: "Automation mode. Guarded is the only dashboard-supported automatic posture.", apply_mode: "live", read_only: false, sensitive: false },
  { key: "retrieval.token_budget", value: 1200, default: 900, source: "database", category: "retrieval", description: "Default compact brief token budget.", apply_mode: "live", read_only: false, sensitive: false },
  { key: "embedding.dimensions", value: 768, default: 768, source: "default", category: "retrieval", description: "Embedding vector dimensions. Changing this requires reindex.", apply_mode: "reindex_required", read_only: false, sensitive: false },
  { key: "server.dashboard_poll_seconds", value: 10, default: 10, source: "default", category: "system", description: "Dashboard refresh interval in seconds.", apply_mode: "restart_required", read_only: false, sensitive: false }
];

const automationStatus = {
  settings_mutated: false,
  policy: { enabled: false, mode: "guarded", cooldown_seconds: 600, allowlist: ["refresh_retrieval_evidence", "ingest_approved_captures", "enqueue_embedding_refresh", "run_governance_shadow"] },
  recurring: { enabled: false, interval_seconds: 1800, last_run_at: now, next_run_at: null, remaining_seconds: null, due: false },
  last_run: { id: "automation-run-1", status: "completed", mode: "guarded", trigger: "manual", started_at: now, completed_at: now, summary: { applied: 3, manual_required: 2, settings_mutated: false } },
  eligible_actions: [
    { id: "eligible-1", action: "refresh_retrieval_evidence", label: "Refresh retrieval evidence", status: "eligible", risk: "low", source: "retrieval", reason: "benchmark evidence is older than the freshness target" },
    { id: "eligible-2", action: "ingest_approved_captures", label: "Ingest approved captures", status: "eligible", risk: "low", source: "capture_review", reason: "four approved captures are waiting" },
    { id: "eligible-3", action: "enqueue_embedding_refresh", label: "Enqueue embedding refresh", status: "eligible", risk: "low", source: "embeddings", reason: "eight stable chunks do not have current vectors" },
    { id: "eligible-4", action: "run_governance_shadow", label: "Run governance shadow proposals", status: "eligible", risk: "low", source: "governance", reason: "shadow proposals do not mutate memory" }
  ],
  manual_required: [
    { id: "manual-1", action: "restart_required_setting", label: "Apply restart-required setting", status: "manual_required", risk: "medium", source: "settings", reason: "restart decisions stay with the operator" },
    { id: "manual-2", action: "delete_root", label: "Delete or purge a watched root", status: "manual_required", risk: "high", source: "corpus", reason: "delete and purge actions are destructive" },
    { id: "manual-3", action: "oauth_setup", label: "Start Gmail OAuth", status: "manual_required", risk: "medium", source: "mail", reason: "OAuth opens a browser and changes credentials" }
  ],
  recent_actions: [
    { id: "automation-action-1", action: "refresh_retrieval_evidence", label: "Refresh retrieval evidence", status: "applied", risk: "low", source: "retrieval", evidence: { benchmark_id: "bench-all-roots-042" }, result: { settings_mutated: false }, created_at: now },
    { id: "automation-action-2", action: "enqueue_embedding_refresh", label: "Enqueue embedding refresh", status: "applied", risk: "low", source: "embeddings", evidence: { chunks: 8 }, result: { settings_mutated: false }, created_at: now },
    { id: "automation-action-3", action: "restart_required_setting", label: "Restart setting change", status: "manual_required", risk: "medium", source: "settings", reason: "requires acknowledgement", created_at: now }
  ],
  runs: [
    { id: "automation-run-1", status: "completed", mode: "guarded", trigger: "manual", started_at: now, completed_at: now, summary: { applied: 3, manual_required: 2, settings_mutated: false } }
  ]
};

const diagnosticsAll = {
  diagnostics: [
    ...health.recent_error_details,
    {
      code: "embedding.refresh_backlog",
      severity: "info",
      message: "Embedding refresh backlog is eligible for guarded enqueue.",
      user_action: "Run a guarded automation pass or enqueue manually from Corpus.",
      technical_detail: "Public-safe fixture detail: eight chunks are waiting for vector refresh.",
      component: "retrieval",
      stage: "embedding_refresh",
      retryable: true,
      target: { type: "worker_family", id: "embeddings" },
      actions: [{ action: "enqueue_embedding_refresh", label: "Enqueue embedding refresh" }]
    }
  ],
  filters: { root_name: "docs", status: "blocked_missing_dependency", family: "office", include_details: true },
  summary: { total: 3, blocked_missing_dependency: 1, retryable: 2 }
};

const retrievalBenchmarkHistory = {
  suite: "standard",
  runs: [
    {
      id: "retrieval-bench-11",
      suite: "standard",
      label: "dashboard-public-fixture",
      status: "completed",
      query_count: 6,
      passed_count: 5,
      metrics: { top1_accuracy: 0.83, brief_dilution: 0.08 },
      metric_deltas: { top1_accuracy: 0.06, brief_dilution: -0.02 },
      settings_mutated: false,
      calibration_summary: {
        confidence_bands: { high: 4, medium: 2, low: 0 },
        semantic_thresholds: [{ threshold: 0.72, evaluated_count: 6, pass_count: 5, false_positive_count: 0, false_negative_count: 1 }]
      },
      recommendation_candidates: [{ kind: "threshold_review", rationale: "One medium confidence result missed top-1.", threshold: 0.72 }],
      case_results: [
        { case_id: "guide-search", status: "passed", observed_ids: ["asset-file"], reasons: ["expected result found"] }
      ]
    }
  ]
};

const codeStatus = {
  totals: { roots: 2, files: 35, symbols: 118, relationships: 72, generated_files: 2, feedback_events: 3 },
  roots: [
    {
      root_name: "projects",
      indexed_files: 22,
      symbol_count: 86,
      relationship_count: 44,
      generated_files: 1,
      languages: { python: 14, typescript: 8 },
      parser_statuses: { ast: 18, fallback: 3, generated_skipped: 1 },
      top_gaps: [{ category: "missing_symbol", count: 2 }, { category: "relationship_gap", count: 1 }],
      hotspots: [{ path: "src/search.py", symbols: 22, relationships: 12 }]
    }
  ],
  feedback_summary: { totals: { event_count: 3 }, by_category: { missing_symbol: 2, relationship_gap: 1 } }
};

const searchPayload = {
  query: "operator dashboard",
  results: [
    {
      id: "result-file-1",
      logical_kind: "file",
      title: "Operator dashboard runbook",
      snippet: { text: "Use Overview first, then move to Automation, Diagnostics, Performance, or Review depending on the safest next action." },
      source_path: "E:/FluxManualFixtures/Docs/OperatorGuide.md",
      asset_id: "asset-file",
      chunk_id: "chunk-guide-1",
      detail_ref: { kind: "file", id: "asset-file" },
      score: 0.91,
      related_evidence_count: 2,
      retrieval_explanation: {
        score: 0.91,
        streams: ["semantic", "keyword"],
        scope: { label: "current workspace" },
        raw_scores: { semantic: 0.88, keyword: 0.72 },
        corpus: { source_path: "E:/FluxManualFixtures/Docs/OperatorGuide.md" },
        suppression: { exact_duplicates: [], version_family: [], semantic_duplicates: [] }
      }
    },
    {
      id: "result-mail-1",
      logical_kind: "mail",
      title: "Mail capture review request",
      snippet: { text: "The fixture mail result shows how sender, folder, export state, and provenance appear without exposing real mail." },
      source_path: "mail:gmail-capture/FluxCapture",
      asset_id: "asset-mail",
      detail_ref: { kind: "mail", id: "asset-mail" },
      score: 0.76,
      related_evidence_count: 1
    }
  ],
  filter_trace: {
    excluded: [
      { id: "old-result", title: "Superseded dashboard note", reason: "superseded" }
    ]
  },
  suppression: {
    exact_duplicates: [{ source: "fixture", suppressed_count: 1 }],
    version_families: [],
    semantic_duplicates: []
  }
};

const resultDetail = {
  logical_kind: "file",
  title: "Operator dashboard runbook",
  asset_id: "asset-file",
  chunk_id: "chunk-guide-1",
  metadata: { canonical_path: "E:/FluxManualFixtures/Docs/OperatorGuide.md", path: "E:/FluxManualFixtures/Docs/OperatorGuide.md", status: "indexed" },
  preview: { available: true, text: "Public-safe preview: this runbook explains the dashboard tabs, guarded automation, diagnostics, and review workflow." },
  actions: {
    copy_path: { available: true, path: "E:/FluxManualFixtures/Docs/OperatorGuide.md" },
    open: { available: false, reason: "Opening local files is manual and disabled in the public-safe guide fixture." },
    reveal: { available: false, reason: "Reveal in folder is manual and disabled in the public-safe guide fixture." }
  },
  related_evidence: [
    { title: "Benchmark evidence", path: "bench-all-roots-042", detail: "latest reliability gate" },
    { title: "Automation audit", path: "automation-action-1", detail: "refresh evidence applied" }
  ],
  provenance: [
    { title: "Root", path: "docs", detail: "public-safe fixture root" },
    { title: "Chunk", path: "chunk-guide-1", detail: "indexed markdown excerpt" }
  ]
};

const governanceActions = {
  actions: [
    {
      id: "gov-action-1",
      action: "semantic_cluster_apply",
      target_type: "claim_cluster",
      target_id: "cluster-1",
      memory_class: "claim",
      risk: "medium",
      status: "proposed",
      source: "governance_shadow",
      rationale: { summary: "Two public-safe sample claims appear duplicative.", guardrails: { requires_manual_apply: true } },
      evidence: { duplicate_count: 2 },
      settings_mutated: false,
      memory_mutated: false,
      created_at: now
    },
    {
      id: "gov-applied-1",
      action: "canonical_cluster_promote",
      target_type: "claim_cluster",
      target_id: "cluster-2",
      risk: "low",
      status: "applied",
      source: "manual",
      rationale: { summary: "Applied sample action with recoverable before-state." },
      before_state: { sample: true },
      after_state: { sample: true },
      settings_mutated: false,
      memory_mutated: true,
      created_at: now
    }
  ],
  telemetry: { total: 2, by_source: { governance_shadow: 1, manual: 1 }, by_risk: { medium: 1, low: 1 }, by_status: { proposed: 1, applied: 1 } }
};

const governanceDigest = {
  digest: {
    summary: { new_proposals: 1, blocked: 0, recoverable: 1, settings_mutated: false },
    recommendations: [
      { label: "Review duplicate proposal", reason: "Manual apply is required before memory mutation." },
      { label: "Run retrieval benchmark", reason: "Keep evidence current before expanding automation." }
    ]
  },
  settings_mutated: false
};

const governancePolicy = {
  policy: {
    protected_classes: ["private_note", "credential"],
    high_risk_requires_manual_apply: true,
    destructive_actions_disabled: true
  },
  settings_mutated: false
};

const reviewPayload = {
  counts: { needs_review: 2, stale: 1, contradicted: 1, superseded: 0, retired: 0, retention_action: 1 },
  claims: [
    {
      id: "claim-stale",
      subject_entity_id: "entity-dashboard",
      subject: { id: "entity-dashboard", type: "project", name: "Flux dashboard" },
      predicate: "has_status",
      object_text: "manual needs expansion",
      confidence: 0.78,
      lifecycle_state: "needs_review",
      retention_action: "review",
      review_reasons: ["stale_evidence", "manual_update_requested"],
      updated_at: now,
      lifecycle: { score: 0.62, current: false, audit_visible: true }
    },
    {
      id: "claim-current",
      subject_entity_id: "entity-automation",
      subject: { id: "entity-automation", type: "feature", name: "Guarded automation" },
      predicate: "posture",
      object_text: "guarded auto",
      confidence: 0.91,
      lifecycle_state: "current",
      review_reasons: ["recent_evidence"],
      updated_at: now,
      lifecycle: { score: 0.95, current: true, audit_visible: true }
    }
  ]
};

const graphPayload = {
  start_entity_id: "entity-dashboard",
  edges: [
    { relation_id: "edge-1", from_entity: { type: "project", name: "Flux dashboard" }, to_entity: { type: "feature", name: "Guarded automation" }, relation_type: "documents", confidence: 0.88, depth: 1 },
    { relation_id: "edge-2", from_entity: { type: "feature", name: "Diagnostics" }, to_entity: { type: "worker", name: "office" }, relation_type: "remediates", confidence: 0.81, depth: 1 }
  ]
};

const retentionPolicies = {
  policies: [
    { memory_class: "claim", half_life_days: 180, min_confidence: 0.62, action: "review", updated_by: "fixture", updated_at: now },
    { memory_class: "episode", half_life_days: 90, min_confidence: 0.55, action: "decay", updated_by: "fixture", updated_at: now }
  ]
};

const retentionQuality = {
  summary: { total: 3, needs_review: 1, by_class: { claim: 2, episode: 1 }, by_bucket: { good: 2, needs_review: 1 } },
  candidates: [
    { id: "quality-1", memory_class: "claim", label: "Manual update evidence is stale", reason: "Source is older than latest dashboard UI.", quality_bucket: "needs_review", confidence: 0.61, lifecycle_state: "stale", retention_action: "review", updated_at: now },
    { id: "quality-2", memory_class: "episode", label: "Automation audit summary", reason: "Recent and internally consistent.", quality_bucket: "good", confidence: 0.86, lifecycle_state: "current", retention_action: "keep", updated_at: now }
  ]
};

const captureReview = {
  jobs: [
    {
      id: "job-review",
      job_type: "codex_capture",
      status: "pending_review",
      payload: { source: "codex", title: "Dashboard manual expansion", proposed_memory_class: "episode", sanitized_excerpt: "The operator asked for a detailed public-safe dashboard manual." },
      attempts: 0,
      last_error: null,
      updated_at: now
    },
    {
      id: "job-approved",
      job_type: "mail_capture",
      status: "approved",
      payload: { source: "mail", title: "Approved public-safe fixture capture", proposed_memory_class: "episode" },
      attempts: 1,
      last_error: null,
      updated_at: now
    }
  ]
};

const auditEvents = {
  events: [
    { id: "audit-1", event_type: "capture.approved", actor: "operator", target_id: "job-approved", details: { reason: "public-safe fixture approval" }, created_at: now },
    { id: "audit-2", event_type: "automation.action.applied", actor: "automation", target_id: "automation-action-1", details: { settings_mutated: false }, created_at: now }
  ]
};

const accelerationEvidence = {
  readiness: "ready",
  settings_mutated: false,
  root_readiness: { ready: 2, partial: 1, blocked: 0 },
  latest_benchmark: { id: "bench-all-roots-042", scenario: "reliability", status: "completed" },
  gates: {
    reliability: { state: "pass", reason: "latest all-root gate completed" },
    cache: { state: "ready", reason: "cache roots reachable" },
    model: { state: "not_required", reason: "local model probe disabled" }
  },
  top_blockers: [
    { root_name: "docs", severity: "warning", summary: "One office job is blocked by a missing dependency." }
  ],
  code_gaps: [
    { category: "missing_symbol", count: 2, summary: "Two sanitized feedback rows mention missing code symbols." }
  ],
  manual_follow_ups: [
    { setting: "crawler.hash_parallelism", command: "flux-kb acceleration benchmark run --scenario tuning", reason: "Tune only after comparing benchmark history." }
  ]
};

const accelerationReliability = {
  status: "pass",
  settings_mutated: false,
  diagnostics: [{ scenario: "reliability", check: "all_roots", status: "ok", summary: "All public-safe fixture roots passed." }],
  recommendations: { settings_mutated: false, candidates: [] }
};

const accelerationRoots = {
  roots: [
    { name: "docs", readiness: "ready", latest_benchmark: { id: "bench-docs", scenario: "host_cloud" }, required_action: "No action required." },
    { name: "projects", readiness: "partial", latest_benchmark: null, required_action: "Run host/cloud calibration benchmark." },
    { name: "archive", readiness: "disabled", latest_benchmark: null, required_action: "Enable root before benchmarking." }
  ]
};

const defaultResponses = {
  "/api/dashboard/health": health,
  "/api/dashboard/crawl": crawl,
  "/api/dashboard/jobs": jobs,
  "/api/dashboard/retrieval-stats": retrievalStats,
  "/api/mail/status": mail,
  "/api/outlook-host/status": outlook,
  "/api/host/status": { status: "running", browse_supported: true, platform: "Windows" },
  "/api/settings": settings,
  "/api/automation/status": automationStatus,
  "/api/automation/actions": { actions: automationStatus.recent_actions },
  "/api/diagnostics/all": diagnosticsAll,
  "/api/acceleration/evidence": accelerationEvidence,
  "/api/acceleration/reliability": accelerationReliability,
  "/api/acceleration/reliability/roots": accelerationRoots,
  "/api/code/status": codeStatus,
  "/api/code/feedback/summary": codeStatus.feedback_summary,
  "/api/retrieval/benchmarks": retrievalBenchmarkHistory,
  "/api/search": searchPayload,
  "/api/explain": { query: "operator dashboard", results: searchPayload.results },
  "/api/governance/actions": governanceActions,
  "/api/governance/digest": governanceDigest,
  "/api/governance/policy": governancePolicy,
  "/api/claims": reviewPayload,
  "/api/retention/policies": retentionPolicies,
  "/api/retention/quality": retentionQuality,
  "/api/capture/review": captureReview,
  "/api/audit": auditEvents,
  "/api/graph/traverse": graphPayload
};

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function responseForPost(path, body) {
  if (path === "/api/automation/run") {
    return {
      run: { id: "automation-run-2", status: "completed", mode: "guarded", trigger: "manual", started_at: now, completed_at: now },
      summary: { applied: 4, manual_required: 3, settings_mutated: false },
      actions: [
        ...automationStatus.recent_actions,
        { id: "automation-action-4", action: "run_governance_shadow", label: "Run governance shadow proposals", status: "applied", risk: "low", source: "governance", result: { settings_mutated: false }, created_at: now }
      ]
    };
  }
  if (path === "/api/diagnostics/actions") {
    return { status: "completed", action: body?.action ?? "retry", settings_mutated: false, updated: 1 };
  }
  if (path === "/api/acceleration/reliability/run") {
    return { ...accelerationReliability, status: "completed", roots: accelerationRoots.roots };
  }
  if (path === "/api/acceleration/benchmarks/run") {
    return {
      fixture: body?.fixture ?? "all",
      mode: body?.mode ?? "scan",
      scenario: body?.scenario ?? "standard",
      runs: [{ ...health.acceleration.benchmarks.history[0], id: "bench-run-clicked", scenario: body?.scenario ?? "standard" }],
      diagnostics: [{ scenario: body?.scenario ?? "standard", check: "fixture", status: "ok", summary: "Public-safe benchmark fixture completed." }],
      recommendations: {
        settings_mutated: false,
        scenario: body?.scenario ?? "standard",
        candidates: [{ setting: "crawler.hash_parallelism", current: 2, candidate: 4, reason: "Fixture suggests a manual comparison candidate.", requires_manual_apply: true }]
      }
    };
  }
  if (path === "/api/retrieval/benchmarks/run") {
    return { ...retrievalBenchmarkHistory.runs[0], id: "retrieval-bench-clicked", label: body?.label ?? "dashboard", status: "completed" };
  }
  if (path === "/api/code/feedback") return { id: "code-feedback-fixture" };
  if (path === "/api/mail/sync") return { count: 1, profiles: [{ profile_name: body?.profile_name ?? "gmail-capture", status: "queued", run_id: "run-manual", exported: 0 }] };
  if (path === "/api/explain") return { ...searchPayload, query: body?.query ?? searchPayload.query };
  if (path === "/api/outlook-host/request-sync") return { status: "queued", request_id: "outlook-sync-fixture" };
  if (path === "/api/mail/oauth/gmail/start") return { status: "pending_user_authorization", authorization_url: "https://accounts.example.test/oauth/fixture" };
  if (path.endsWith("/post-process/dry-run")) return { events: mail.post_process.recent_events };
  if (path === "/api/mail/profiles") return { ...body, enabled: true };
  if (path === "/api/crawl/sync") return { status: "completed", files_seen: 4, files_changed: 1, jobs_queued: 1, dry_run: Boolean(body?.dry_run) };
  if (path === "/api/crawl/watch") return { updated: 1, watch_enabled: Boolean(body?.enabled) };
  if (path === "/api/crawl/backfill") return { completed: 1, blocked: 0, retried: 1 };
  if (path === "/api/crawl/roots") return { root: body, sync: { files_seen: 0, files_changed: 0, jobs_queued: 0 } };
  if (path === "/api/settings/apply") return { acknowledged: 2 };
  if (path === "/api/governance/run") return { ...governanceDigest, run: { id: "governance-run-fixture", status: "completed" } };
  if (path.includes("/governance/actions/") && path.endsWith("/apply")) return { status: "applied", settings_mutated: false, memory_mutated: true };
  if (path.includes("/governance/actions/") && path.endsWith("/recover")) return { status: "recovered", settings_mutated: false, memory_mutated: true };
  if (path.includes("/capture/review/") && path.endsWith("/decision")) return { status: "recorded", decision: body?.decision ?? "approve" };
  if (path === "/api/capture/review/ingest") return { status: "completed", ingested: 1, skipped: 0 };
  if (path.includes("/corpus/assets/") && path.endsWith("/actions")) return { status: "blocked_manual", action: body?.action ?? "open", message: "Manual file action blocked in public-safe fixture." };
  return { status: "ok", settings_mutated: false };
}

function responseForPut(path, body) {
  if (path.includes("/oauth-client-config")) return { status: "saved" };
  if (path.startsWith("/api/settings/")) return { key: decodeURIComponent(path.replace("/api/settings/", "")), value: body?.value, source: "database", apply_mode: "live" };
  if (path.startsWith("/api/retention/policies/")) return { status: "saved", policy: body };
  return { status: "saved" };
}

function responseForPatch(path, body) {
  if (path.startsWith("/api/crawl/roots/")) return { ...crawl.root_summaries[0], ...body };
  return { status: "updated" };
}

function responseForDelete(path) {
  if (path.startsWith("/api/crawl/roots/")) return { status: "deleted", purge_index: path.includes("purge_index=true") };
  return { status: "deleted" };
}

export function responseForApiRequest(url, method = "GET", body = undefined) {
  const parsed = new URL(url, "http://127.0.0.1");
  const path = parsed.pathname;
  if (method === "POST") return clone(responseForPost(path, body));
  if (method === "PUT") return clone(responseForPut(path, body));
  if (method === "PATCH") return clone(responseForPatch(path, body));
  if (method === "DELETE") return clone(responseForDelete(`${path}${parsed.search}`));
  if (path.startsWith("/api/results/")) return clone(resultDetail);
  if (path.startsWith("/api/code/search")) {
    return {
      results: [
        { id: "code-result-1", root_name: "projects", path: "src/search.py", language: "python", symbol: "SearchService.search", relationship: "call", snippet: "def search(query): return ranked_results" }
      ]
    };
  }
  if (path.startsWith("/api/code/symbols")) {
    return {
      matches: [{ symbol: "SearchService.search", path: "src/search.py", language: "python", kind: "function" }],
      references: [{ symbol: "SearchService.search", path: "tests/test_search.py", relationship: "call" }]
    };
  }
  if (path.startsWith("/api/acceleration/reliability/root/")) {
    return { root: { name: path.split("/").pop(), readiness: "ready", latest_benchmark: { id: "root-bench-fixture" } }, settings_mutated: false };
  }
  if (path.startsWith("/api/claims/") && path.endsWith("/transitions")) {
    return { transitions: [{ from: "needs_review", to: "current", label: "Confirm" }, { from: "needs_review", to: "retired", label: "Retire" }] };
  }
  if (path.startsWith("/api/capture/review")) return clone(captureReview);
  if (path.startsWith("/api/audit")) return clone(auditEvents);
  if (path.startsWith("/api/graph/traverse")) return clone(graphPayload);
  if (path.startsWith("/api/retention/quality")) return clone(retentionQuality);
  return clone(defaultResponses[path] ?? { status: "ok", settings_mutated: false });
}

export const fixtureResponses = defaultResponses;
