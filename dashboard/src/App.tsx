import {
  AlertCircle,
  AlertTriangle,
  Archive,
  BriefcaseBusiness,
  CheckCircle2,
  ChevronDown,
  Copy,
  Clock3,
  Database,
  ExternalLink,
  FileText,
  Folder,
  FolderOpen,
  Gauge,
  HeartPulse,
  Inbox,
  KeyRound,
  ListFilter,
  Mail,
  MoreVertical,
  Paperclip,
  Play,
  Plus,
  RefreshCcw,
  RotateCcw,
  Search,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Square,
  Terminal,
  Trash2,
  Wrench,
  X
} from "lucide-react";
import type { FormEvent, ReactNode } from "react";
import { Fragment, useEffect, useMemo, useState } from "react";

type HealthPayload = {
  database?: { ok?: boolean; message?: string };
  runtime?: Record<string, { ok?: boolean; message?: string; required?: boolean }>;
  watcher?: { active_roots?: number; disabled_roots?: number; stale_count?: number; roots?: unknown[] };
  jobs?: { pending?: number; failed?: number; blocked?: number };
  retrieval?: { episodes?: number; sources?: number; source_assets?: number; asset_chunks?: number; embeddings?: number };
  acceleration?: AccelerationStatus;
  workers?: {
    active?: number;
    components?: Array<{ name?: string; status?: string; heartbeat_age_seconds?: number | null; metadata?: Record<string, unknown> }>;
  };
  recent_errors?: string[];
  recent_error_details?: ErrorDiagnostic[];
  extractors?: Record<string, { ok?: boolean; message?: string }>;
  host_agent?: { status?: string; browse_supported?: boolean; message?: string };
  deployment?: {
    install_root?: string | null;
    app_root?: string | null;
    private_dir?: string | null;
    data_dir?: string | null;
    logs_dir?: string | null;
    image_tag?: string | null;
    mode?: string;
    repo_coupled?: boolean;
    running_from_repo?: boolean;
  };
  codex?: {
    status?: string;
    configured?: boolean;
    installed?: boolean;
    hooks_available?: boolean;
    discoverable?: boolean;
    restart_required?: boolean;
    message?: string;
    mcp?: {
      configured?: boolean;
      command?: string | null;
      cwd?: string | null;
      enabled?: boolean;
      dependency_available?: boolean;
      message?: string;
    };
    hook_policy?: {
      status?: string;
      enabled?: boolean;
      preflight_enabled?: boolean;
      capture_enabled?: boolean;
      token_budget?: number;
      recent_events?: Array<{ event_type?: string; created_at?: string; details?: Record<string, unknown> }>;
    };
  };
};

type AccelerationStatus = {
  capabilities?: Record<string, AccelerationCapability>;
  cache?: {
    root?: string;
    source?: string;
    directories?: Record<string, string>;
  };
  worker_families?: AccelerationWorkerFamily[];
  benchmarks?: {
    history?: AccelerationBenchmarkRun[];
    fixtures?: Array<Record<string, unknown>>;
    totals?: Record<string, number>;
  };
};

type AccelerationCapability = {
  ok?: boolean;
  state?: string;
  message?: string;
  provider?: string;
  policy?: string;
  selected_backend?: string;
  fallback_reason?: string | null;
  native?: boolean;
  providers?: string[];
  count?: number;
  total_bytes?: number | null;
  gpus?: Array<{ name?: string; memory_total_mb?: number; driver_version?: string }>;
};

type AccelerationWorkerFamily = {
  family?: string;
  resource_class?: string;
  configured_cap?: number;
  cap_available?: number;
  backpressure?: string | null;
  pending?: number;
  running?: number;
  blocked?: number;
  failed?: number;
  avg_duration_ms?: number | null;
  p95_duration_ms?: number | null;
  max_duration_ms?: number | null;
  oldest_pending_age_seconds?: number | null;
  retrying_locked?: number;
  blocked_locked?: number;
  slowest_recent_jobs?: Array<{ id?: string; path?: string; duration_ms?: number | null }>;
  ocr_cache_hits?: number;
  ocr_cache_misses?: number;
  asr_cache_hits?: number;
  asr_cache_misses?: number;
  asr_segments?: number;
  vision_cache_hits?: number;
  vision_cache_misses?: number;
  vision_descriptions?: number;
  vision_blocked_dependency_count?: number;
  decorative_image_skips?: number;
  frame_sample_count?: number;
  thumbnail_cache_hits?: number;
  thumbnail_cache_misses?: number;
  parser_cache_hits?: number;
  parser_cache_misses?: number;
  manifest_skipped_unchanged?: number;
  embedding_vectors?: number;
  embedding_skipped_unchanged?: number;
  embedding_batches?: number;
  embedding_cache_hits?: number;
  embedding_cache_misses?: number;
};

type AccelerationBenchmarkRun = {
  id?: string;
  fixture?: string;
  mode?: string;
  label?: string;
  compare_label?: string;
  status?: string;
  file_count?: number;
  elapsed_ms?: number;
  throughput_files_per_second?: number;
  previous_elapsed_delta_ms?: number | null;
  previous_throughput_delta?: number | null;
  warm_state?: string;
  pass_index?: number;
  hash_parallelism?: number;
  worker_count?: number;
  manifest_skipped_unchanged?: number;
  cache_hits?: number;
  cache_misses?: number;
  scope_type?: string;
  deployment_label?: string | null;
  model_telemetry?: {
    local_model?: { state?: string; provider?: string };
    blocked_dependency_count?: number;
  };
  created_at?: string | null;
};

type ErrorDiagnostic = {
  code?: string;
  message?: string;
  severity?: "error" | "warning" | "info" | string;
  component?: string;
  stage?: string | null;
  retryable?: boolean;
  user_action?: string | null;
  technical_detail?: string | null;
  target?: { type?: string; id?: string };
  links?: Array<{ label?: string; tab?: string; profile?: string; root?: string }>;
  status_code?: number | null;
};

type MailProfile = {
  name: string;
  source_type: "imap" | "outlook_com" | string;
  account?: string | null;
  server?: string | null;
  folder_paths?: string[];
  spool_path?: string;
  post_process_policy?: string;
  enabled?: boolean;
  sync_enabled?: boolean;
  sync_interval_seconds?: number;
  sync_window_days?: number;
  max_messages_per_run?: number;
  last_sync_at?: string | null;
  next_sync_at?: string | null;
  metadata?: Record<string, unknown>;
};

type MailSchedulerCounts = {
  due?: number;
  queued?: number;
  claimed?: number;
  running?: number;
  failed?: number;
  blocked_auth?: number;
  backoff?: number;
};

type MailSyncRun = {
  id?: string;
  profile_name?: string;
  status?: string;
  trigger?: string;
  requested_by?: string;
  claimed_by?: string | null;
  worker_id?: string | null;
  attempt_count?: number;
  last_error?: string | null;
  next_attempt_at?: string | null;
  drift_seconds?: number;
  missed_runs?: number;
  started_at?: string | null;
  completed_at?: string | null;
  messages_seen?: number;
  messages_exported?: number;
  errors?: MailSyncError[];
};

type MailSchedulerStatus = {
  counts?: MailSchedulerCounts;
  recent_runs?: MailSyncRun[];
  diagnostics?: ErrorDiagnostic[];
};

type MailPostProcessEvent = {
  id?: string;
  profile_name?: string | null;
  provider?: string;
  policy?: string;
  action?: string;
  status?: string;
  dry_run?: boolean;
  error?: string | null;
  created_at?: string | null;
};

type MailStatus = {
  enabled_profiles?: number;
  exported_messages?: number;
  errored_messages?: number;
  profiles?: MailProfile[];
  scheduler?: MailSchedulerStatus;
  post_process?: {
    counts?: Record<string, number>;
    recent_events?: MailPostProcessEvent[];
  };
  oauth?: {
    profiles?: Array<{ profile_name?: string; status?: string; expires_at?: string | null; has_refresh_token?: boolean }>;
  };
};

type OutlookStatus = {
  host?: {
    host_id?: string;
    status?: string;
    command?: string;
    heartbeat_at?: string | null;
    last_error?: string | null;
  };
  profiles?: MailProfile[];
  pending_requests?: Array<{ id?: string; profile_name?: string; status?: string; requested_at?: string }>;
};

type SettingRow = {
  key: string;
  value: unknown;
  source: string;
  sensitive: boolean;
  category: string;
  apply_mode: string;
  read_only?: boolean;
  affected_components?: string[];
  description?: string;
};

type CrawlPayload = {
  roots?: MonitoredRoot[];
  root_summaries?: RootSummary[];
  status?: Record<string, unknown>;
  watchers?: Array<Record<string, unknown>>;
  recent_errors?: string[];
};

type JobsPayload = {
  jobs?: Array<Record<string, unknown>>;
  count?: number;
};

type RetrievalPayload = {
  retrieval?: HealthPayload["retrieval"];
  duplicate_assets?: number;
  duplicate_count?: number;
  stats?: Record<string, unknown>;
};

type SearchResult = {
  kind?: string;
  logical_kind?: "file" | "mail" | "episode" | string;
  title?: string;
  excerpt?: string;
  summary?: string;
  snippet?: {
    text?: string;
    matched_terms?: string[];
    highlights?: Array<{ term?: string; start?: number; end?: number }>;
    source?: string;
    source_path?: string;
  };
  retrieval_explanation?: {
    score?: number;
    streams?: string[];
    raw_scores?: Record<string, number>;
    scope?: Record<string, unknown>;
    lifecycle?: Record<string, unknown>;
    graph?: Record<string, unknown>;
    corpus?: Record<string, unknown>;
    adjustments?: Record<string, unknown>;
    filters?: Record<string, unknown>;
    suppression?: Record<string, unknown>;
  };
  score?: number;
  id?: string;
  asset_id?: string;
  chunk_id?: string;
  mail_message_id?: string;
  detail_ref?: { kind?: string; id?: string };
  related_evidence_count?: number;
  source_path?: string;
  streams?: string[];
  raw_scores?: Record<string, number>;
};

type RetrievalFilters = {
  logical_kinds: string[];
  current_only: boolean;
  lifecycle_states: string[];
  include_suppressed: boolean;
};

type RetrievalFilterExcluded = {
  id?: string;
  title?: string;
  kind?: string;
  reason?: string;
  score?: number;
  lifecycle_state?: string;
  source_path?: string;
};

type RetrievalFilterTrace = {
  excluded?: RetrievalFilterExcluded[];
};

type RetrievalSuppression = {
  exact_duplicates?: Array<Record<string, unknown>>;
  version_families?: Array<Record<string, unknown>>;
};

type ExplainPayload = {
  query?: string;
  results?: SearchResult[];
  brief?: Record<string, unknown>;
  filters?: RetrievalFilters;
  filter_trace?: RetrievalFilterTrace;
  suppression?: RetrievalSuppression;
};

type ResultAction = {
  available?: boolean;
  disabled_reason?: string;
  path?: string;
};

type EvidenceItem = {
  title?: string;
  path?: string;
  relationship?: string;
  kind?: string;
  status?: string;
  asset_id?: string;
  metadata?: Record<string, unknown>;
};

type ResultDetail = {
  kind?: string;
  id?: string;
  logical_kind?: "file" | "mail" | "episode" | string;
  title?: string;
  asset_id?: string;
  chunk_id?: string;
  mail_message_id?: string;
  metadata?: Record<string, unknown>;
  preview?: { available?: boolean; text?: string; chunks?: Array<Record<string, unknown>> };
  body?: { format?: string; html_sanitized?: string; text?: string };
  mail?: {
    subject?: string;
    sender?: string;
    recipients?: string[];
    cc?: string[];
    bcc?: string[];
    received_at?: string;
    sent_at?: string;
    profile_name?: string;
    source_folder?: string;
    export_id?: string;
    post_process_state?: string;
  };
  attachments?: EvidenceItem[];
  related_evidence?: EvidenceItem[];
  provenance?: EvidenceItem[];
  actions?: Record<string, ResultAction>;
};

type FileActionResponse = {
  state?: "opened" | "missing" | "deleted" | "locked" | "host_agent_offline" | "not_allowed";
  action?: string;
  asset_id?: string;
};

type MailSyncError = {
  folder?: string;
  stage?: string;
  error?: string;
  [key: string]: unknown;
};

type MailSyncProfileResult = {
  profile?: string;
  status?: string;
  run_id?: string;
  exported?: number;
  errors?: MailSyncError[];
  spool_sync?: { count?: number };
};

type LoadState = {
  health: HealthPayload;
  crawl: CrawlPayload;
  jobs: JobsPayload;
  retrieval: RetrievalPayload;
  mail: MailStatus;
  outlook: OutlookStatus;
  settings: SettingRow[];
};

type ClaimReviewFilter = "all" | "needs_review" | "current";

type ClaimReviewCounts = {
  total?: number;
  current?: number;
  needs_review?: number;
  stale?: number;
  contradicted?: number;
  superseded?: number;
  retired?: number;
  retention_action?: number;
};

type ClaimReviewClaim = {
  id: string;
  subject_entity_id?: string | null;
  subject?: { id?: string; type?: string; name?: string } | null;
  predicate?: string;
  object_text?: string;
  confidence?: number;
  lifecycle_state?: string;
  retention_action?: string;
  review_reasons?: string[];
  updated_at?: string | null;
  lifecycle?: { score?: number; current?: boolean; audit_visible?: boolean };
};

type ClaimReviewPayload = {
  claims?: ClaimReviewClaim[];
  counts?: ClaimReviewCounts;
};

type GraphEdge = {
  relation_id?: string;
  from_entity?: { type?: string; name?: string };
  to_entity?: { type?: string; name?: string };
  relation_type?: string;
  confidence?: number;
  depth?: number;
};

type GraphPayload = {
  start_entity_id?: string;
  edges?: GraphEdge[];
};

type CaptureReviewJob = {
  id?: string;
  job_type?: string;
  status?: string;
  payload?: Record<string, unknown>;
  attempts?: number;
  last_error?: string | null;
  updated_at?: string | null;
};

type CaptureReviewPayload = {
  jobs?: CaptureReviewJob[];
};

type RetentionPolicy = {
  memory_class: string;
  half_life_days?: number;
  min_confidence?: number;
  action?: string;
  updated_by?: string | null;
  updated_at?: string | null;
};

type RetentionPolicyPayload = {
  policies?: RetentionPolicy[];
};

type RetentionPolicyUpdate = {
  half_life_days: number;
  min_confidence: number;
  action: string;
  reason: string;
};

type RetentionQualityCandidate = {
  id?: string;
  memory_class?: string;
  label?: string;
  reason?: string;
  quality_bucket?: string;
  confidence?: number;
  lifecycle_state?: string;
  extraction_status?: string;
  retention_action?: string | null;
  updated_at?: string | null;
};

type RetentionQualityPayload = {
  summary?: {
    total?: number;
    needs_review?: number;
    by_class?: Record<string, number>;
    by_bucket?: Record<string, number>;
  };
  candidates?: RetentionQualityCandidate[];
};

type AuditEvent = {
  id?: string;
  event_type?: string;
  actor?: string | null;
  target_id?: string | null;
  details?: Record<string, unknown>;
  created_at?: string | null;
};

type AuditPayload = AuditEvent[] | { events?: AuditEvent[] };

type CaptureDecisionState = {
  job: CaptureReviewJob;
  decision: "approve" | "reject";
};

type TabId = "health" | "corpus" | "mail" | "settings" | "retrieval" | "review" | "jobs";

type ProfileForm = {
  name: string;
  source_type: "imap" | "outlook_com";
  account: string;
  server: string;
  folder_paths: string;
  spool_path: string;
  post_process_policy: string;
  processed_folder: string;
  trash_folder: string;
  destructive_post_process_confirmed: boolean;
  sync_enabled: boolean;
  sync_interval_seconds: number;
  sync_window_days: number;
  max_messages_per_run: number;
};

type MonitoredRoot = {
  id?: string;
  name: string;
  root_path: string;
  enabled?: boolean;
  recursive?: boolean;
  watch_enabled?: boolean;
  trust_rank?: number;
  include_globs?: string[];
  exclude_globs?: string[];
  glob_mode?: "inherit" | "extend" | "override" | string;
  effective_globs?: { include_globs?: string[]; exclude_globs?: string[]; mode?: string };
  max_inline_bytes?: number;
  heavy_threshold_bytes?: number;
  metadata?: Record<string, unknown>;
};

type RootSummary = MonitoredRoot & {
  state?: string;
  watcher?: { status?: string; heartbeat_at?: string | null; last_event_at?: string | null; last_error?: string | null; heartbeat_age_seconds?: number | null };
  asset_counts?: Record<string, number>;
  job_counts?: Record<string, number>;
  latest_crawl?: Record<string, unknown> | null;
  recent_assets?: Array<Record<string, unknown>>;
  recent_jobs?: Array<Record<string, unknown>>;
  recent_errors?: string[];
};

type CrawlRootForm = {
  name: string;
  root_path: string;
  recursive: boolean;
  watch_enabled: boolean;
  initial_crawl: boolean;
  trust_rank: number;
  include_globs: string;
  exclude_globs: string;
  glob_mode: "inherit" | "extend" | "override";
  max_inline_bytes: number;
  heavy_threshold_bytes: number;
};

const emptyState: LoadState = {
  health: {},
  crawl: { roots: [] },
  jobs: { jobs: [] },
  retrieval: {},
  mail: { profiles: [] },
  outlook: { profiles: [], pending_requests: [] },
  settings: []
};

const navItems: Array<{ id: TabId; label: string; icon: ReactNode }> = [
  { id: "health", label: "Health", icon: <HeartPulse size={20} /> },
  { id: "corpus", label: "Corpus", icon: <Folder size={20} /> },
  { id: "mail", label: "Mail", icon: <Mail size={20} /> },
  { id: "settings", label: "Settings", icon: <Settings size={20} /> },
  { id: "retrieval", label: "Retrieval", icon: <Search size={20} /> },
  { id: "review", label: "Review", icon: <ShieldCheck size={20} /> },
  { id: "jobs", label: "Jobs", icon: <ListFilter size={20} /> }
];

const DASHBOARD_STATE_KEY = "flux-dashboard-state";
const DEFAULT_POLL_SECONDS = 10;
type SavedDashboardState = {
  activeTab?: TabId;
  selectedName?: string;
  selectedRootName?: string;
};

export default function App() {
  const initialDashboardState = readDashboardState();
  const [state, setState] = useState<LoadState>(emptyState);
  const [activeTab, setActiveTab] = useState<TabId>(initialDashboardState.activeTab ?? "health");
  const [selectedName, setSelectedName] = useState<string>(initialDashboardState.selectedName ?? "");
  const [selectedRootName, setSelectedRootName] = useState<string>(initialDashboardState.selectedRootName ?? "");
  const [loading, setLoading] = useState(true);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [toast, setToast] = useState<string>("");
  const [debugOpen, setDebugOpen] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [profileDialog, setProfileDialog] = useState<MailProfile | "new" | null>(null);
  const [rootDialog, setRootDialog] = useState<RootSummary | "new" | null>(null);
  const [deleteRoot, setDeleteRoot] = useState<RootSummary | null>(null);
  const [settingEditor, setSettingEditor] = useState<SettingRow | null>(null);
  const [settingValue, setSettingValue] = useState("");
  const [confirmSetting, setConfirmSetting] = useState<SettingRow | null>(null);
  const [errorDetail, setErrorDetail] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState<SearchResult[]>([]);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchKind, setSearchKind] = useState("all");
  const [searchCurrentOnly, setSearchCurrentOnly] = useState(false);
  const [searchIncludeSuppressed, setSearchIncludeSuppressed] = useState(false);
  const [searchFilterTrace, setSearchFilterTrace] = useState<RetrievalFilterTrace>({});
  const [searchSuppression, setSearchSuppression] = useState<RetrievalSuppression>({});
  const [resultDetail, setResultDetail] = useState<ResultDetail | null>(null);
  const [resultDetailLoading, setResultDetailLoading] = useState(false);
  const [reviewFilter, setReviewFilter] = useState<ClaimReviewFilter>("needs_review");
  const [reviewStateFilter, setReviewStateFilter] = useState("");
  const [claimReview, setClaimReview] = useState<ClaimReviewPayload>({ claims: [], counts: {} });
  const [captureReview, setCaptureReview] = useState<CaptureReviewPayload>({ jobs: [] });
  const [captureReviewAudit, setCaptureReviewAudit] = useState<AuditEvent[]>([]);
  const [retentionPolicies, setRetentionPolicies] = useState<RetentionPolicyPayload>({ policies: [] });
  const [retentionQuality, setRetentionQuality] = useState<RetentionQualityPayload>({ summary: {}, candidates: [] });
  const [captureDecision, setCaptureDecision] = useState<CaptureDecisionState | null>(null);
  const [captureDecisionReason, setCaptureDecisionReason] = useState("");
  const [selectedClaimId, setSelectedClaimId] = useState("");
  const [claimGraph, setClaimGraph] = useState<GraphPayload>({ edges: [] });
  const [reviewLoading, setReviewLoading] = useState(false);
  const [theme, setTheme] = useState(() => localStorage.getItem("flux-dashboard-theme") ?? "light");

  async function load(options: { showLoading?: boolean } = {}) {
    if (options.showLoading ?? false) {
      setLoading(true);
    }
    const [health, crawl, jobs, retrieval, mail, outlook, settings] = await Promise.all([
      getJson<HealthPayload>("/api/dashboard/health", {}),
      getJson<CrawlPayload>("/api/dashboard/crawl", { roots: [] }),
      getJson<JobsPayload>("/api/dashboard/jobs", { jobs: [] }),
      getJson<RetrievalPayload>("/api/dashboard/retrieval-stats", {}),
      getJson<MailStatus>("/api/mail/status", { profiles: [] }),
      getJson<OutlookStatus>("/api/outlook-host/status", { profiles: [], pending_requests: [] }),
      getJson<SettingRow[]>("/api/settings", [])
    ]);
    setState({ health, crawl, jobs, retrieval, mail, outlook, settings });
    setLastUpdated(new Date());
    setLoading(false);
  }

  useEffect(() => {
    void load({ showLoading: true });
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("flux-dashboard-theme", theme);
  }, [theme]);

  const profiles = state.mail.profiles ?? [];
  const rootSummaries = state.crawl.root_summaries ?? (state.crawl.roots ?? []);
  const selectedProfile = useMemo(() => {
    return profiles.find((profile) => profile.name === selectedName) ?? profiles.find((profile) => profile.source_type === "outlook_com") ?? profiles[0];
  }, [profiles, selectedName]);
  const selectedRoot = useMemo(() => {
    return rootSummaries.find((root) => root.name === selectedRootName) ?? rootSummaries[0];
  }, [rootSummaries, selectedRootName]);
  const reviewClaims = claimReview.claims ?? [];
  const selectedClaim = useMemo(() => {
    return reviewClaims.find((claim) => claim.id === selectedClaimId) ?? reviewClaims[0];
  }, [reviewClaims, selectedClaimId]);

  useEffect(() => {
    if (!selectedName && selectedProfile?.name) {
      setSelectedName(selectedProfile.name);
    }
  }, [selectedName, selectedProfile?.name]);

  useEffect(() => {
    if (!selectedRootName && selectedRoot?.name) {
      setSelectedRootName(selectedRoot.name);
    }
  }, [selectedRootName, selectedRoot?.name]);

  useEffect(() => {
    if (settingEditor) {
      setSettingValue(String(settingEditor.value ?? ""));
    }
  }, [settingEditor]);

  useEffect(() => {
    if (activeTab === "review") {
      void loadReview();
    }
  }, [activeTab, reviewFilter, reviewStateFilter]);

  useEffect(() => {
    if (activeTab !== "review" || !selectedClaim?.subject_entity_id) {
      setClaimGraph({ edges: [] });
      return;
    }
    void loadClaimGraph(selectedClaim.subject_entity_id);
  }, [activeTab, selectedClaim?.subject_entity_id]);

  const pollSeconds = dashboardPollSeconds(state.settings);

  useEffect(() => {
    writeDashboardState({ activeTab, selectedName, selectedRootName });
  }, [activeTab, selectedName, selectedRootName]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      void load({ showLoading: false });
    }, pollSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [pollSeconds]);

  async function requestProfileSync(profile = selectedProfile) {
    if (!profile) {
      setToast("Select a mail profile first.");
      return;
    }
    try {
      setToast(`Sync request queued for ${profile.name}...`);
      if (profile.source_type === "outlook_com") {
        const payload = await sendJson<{ status?: string }>("/api/outlook-host/request-sync", "POST", { profile_name: profile.name });
        setToast(payload?.status ? `Outlook sync request ${payload.status}.` : "Outlook sync request queued.");
      } else {
        const payload = await sendJson<{ count?: number; profiles?: MailSyncProfileResult[] }>("/api/mail/sync", "POST", { profile_name: profile.name });
        const result = payload.profiles?.[0];
        const status = result?.status ?? "completed";
        const exported = result?.exported ?? 0;
        const details = mailSyncErrorDetail(result);
        if (mailSyncStatusFailed(status)) {
          setToast(`Sync failed for ${profile.name}: ${status}${details ? ` - ${details}` : ""}`);
        } else {
          const runText = result?.run_id ? ` (run ${result.run_id})` : "";
          const exportedText = status === "queued" || status === "claimed" || status === "running" ? "" : `; exported ${exported} message${exported === 1 ? "" : "s"}`;
          setToast(`IMAP sync ${status} for ${profile.name}${runText}${exportedText}.`);
        }
      }
      await load();
    } catch (error) {
      setToast(`Sync failed for ${profile.name}: ${errorMessage(error)}`);
    }
  }

  async function saveProfile(form: ProfileForm) {
    const payload = {
      name: form.name.trim(),
      source_type: form.source_type,
      account: form.account.trim() || null,
      server: form.server.trim() || null,
      folder_paths: splitLines(form.folder_paths),
      spool_path: form.spool_path.trim(),
      post_process_policy: form.post_process_policy,
      processed_folder: form.processed_folder.trim(),
      trash_folder: form.trash_folder.trim(),
      destructive_post_process_confirmed: form.destructive_post_process_confirmed,
      sync_enabled: form.sync_enabled,
      sync_interval_seconds: Number(form.sync_interval_seconds),
      sync_window_days: Number(form.sync_window_days),
      max_messages_per_run: Number(form.max_messages_per_run)
    };
    await sendJson("/api/mail/profiles", "POST", payload);
    setProfileDialog(null);
    setSelectedName(payload.name);
    setToast("Mail profile saved.");
    await load();
  }

  async function runPostProcessDryRun(profile = selectedProfile) {
    if (!profile) {
      setToast("Select a mail profile first.");
      return;
    }
    try {
      const payload = await sendJson<{ events?: MailPostProcessEvent[] }>(
        `/api/mail/profiles/${encodeURIComponent(profile.name)}/post-process/dry-run`,
        "POST",
        { limit: 5 }
      );
      const planned = payload.events?.length ?? 0;
      setToast(`Post-process dry-run planned ${planned} action${planned === 1 ? "" : "s"}.`);
      await load();
    } catch (error) {
      setToast(`Post-process dry-run failed for ${profile.name}: ${errorMessage(error)}`);
    }
  }

  async function startGmailOAuth(profile: MailProfile, clientConfigPath: string) {
    const popup = openOAuthPopup();
    try {
      const payload = await sendJson<{ auth_url?: string; authorization_url?: string; status?: string; message?: string }>("/api/mail/oauth/gmail/start", "POST", {
        profile_name: profile.name,
        client_config_path: clientConfigPath
      });
      const authUrl = payload.auth_url ?? payload.authorization_url;
      if (authUrl) {
        if (popup && !popup.closed) {
          popup.location.assign(authUrl);
        } else {
          setToast(`Gmail OAuth URL ready for ${profile.name}, but the browser blocked the popup: ${authUrl}`);
          await load();
          return;
        }
        setToast(`Gmail OAuth opened for ${profile.name}.`);
      } else if (payload.message) {
        popup?.close();
        setToast(`Gmail OAuth ${payload.status ?? "blocked"} for ${profile.name}: ${payload.message}`);
      } else {
        popup?.close();
        setToast(payload.status ?? `Gmail OAuth setup started for ${profile.name}.`);
      }
      await load();
    } catch (error) {
      popup?.close();
      setToast(`Gmail OAuth failed for ${profile.name}: ${errorMessage(error)}`);
    }
  }

  async function saveGmailOAuthClientPath(profile: MailProfile, clientConfigPath: string) {
    try {
      await sendJson(`/api/mail/profiles/${encodeURIComponent(profile.name)}/oauth-client-config`, "PUT", {
        client_config_path: clientConfigPath.trim()
      });
      setToast(`OAuth client JSON path saved for ${profile.name}.`);
      await load();
    } catch (error) {
      setToast(`Could not save OAuth client JSON path for ${profile.name}: ${errorMessage(error)}`);
    }
  }

  async function saveCrawlRoot(form: CrawlRootForm) {
    try {
      const editingRoot = rootDialog && rootDialog !== "new" ? rootDialog : null;
      const payload = {
        name: form.name.trim(),
        root_path: form.root_path.trim(),
        recursive: form.recursive,
        watch_enabled: form.watch_enabled,
        initial_crawl: form.initial_crawl,
        glob_mode: form.glob_mode,
        trust_rank: Number(form.trust_rank),
        include_globs: splitLines(form.include_globs),
        exclude_globs: splitLines(form.exclude_globs),
        max_inline_bytes: Number(form.max_inline_bytes),
        heavy_threshold_bytes: Number(form.heavy_threshold_bytes)
      };
      const result = editingRoot
        ? await sendJson<MonitoredRoot>(`/api/crawl/roots/${encodeURIComponent(editingRoot.id ?? editingRoot.name)}`, "PATCH", payload)
        : await sendJson<{ root?: MonitoredRoot; sync?: Record<string, unknown> }>("/api/crawl/roots", "POST", payload);
      setRootDialog(null);
      const savedName = editingRoot ? (result as MonitoredRoot).name : ((result as { root?: MonitoredRoot }).root?.name ?? payload.name);
      setSelectedRootName(savedName);
      if (editingRoot) {
        setToast(`Watched path ${payload.name} updated.`);
      } else {
        const addResult = result as { sync?: Record<string, unknown> };
        setToast(addResult.sync ? `Watched path ${payload.name} added and initial crawl started.` : `Watched path ${payload.name} added.`);
      }
      await load();
    } catch (error) {
      setToast(`Could not save watched path: ${errorMessage(error)}`);
    }
  }

  async function runCorpusSync() {
    try {
      await sendJson("/api/crawl/sync", "POST", { dry_run: false });
      setToast("Corpus sync completed.");
      await load();
    } catch (error) {
      setToast(`Corpus sync failed: ${errorMessage(error)}`);
    }
  }

  async function setCorpusWatch(enabled: boolean) {
    try {
      await sendJson("/api/crawl/watch", "POST", { enabled });
      setToast(enabled ? "Watch enabled." : "Watch disabled.");
      await load();
    } catch (error) {
      setToast(`Watch update failed: ${errorMessage(error)}`);
    }
  }

  async function runRootSync(rootName: string, dryRun = false) {
    try {
      await sendJson("/api/crawl/sync", "POST", { root_name: rootName, dry_run: dryRun });
      setToast(dryRun ? `Dry run completed for ${rootName}.` : `Sync completed for ${rootName}.`);
      await load();
    } catch (error) {
      setToast(`Root sync failed for ${rootName}: ${errorMessage(error)}`);
    }
  }

  async function setRootWatch(rootName: string, enabled: boolean) {
    try {
      await sendJson("/api/crawl/watch", "POST", { root_name: rootName, enabled });
      setToast(enabled ? `Watch enabled for ${rootName}.` : `Watch disabled for ${rootName}.`);
      await load();
    } catch (error) {
      setToast(`Watch update failed for ${rootName}: ${errorMessage(error)}`);
    }
  }

  async function runRootBackfill(rootName: string) {
    try {
      await sendJson("/api/crawl/backfill", "POST", { kind: "all", limit: 10, workers: 1, root_name: rootName });
      setToast(`Backfill run completed for ${rootName}.`);
      await load();
    } catch (error) {
      setToast(`Backfill failed for ${rootName}: ${errorMessage(error)}`);
    }
  }

  async function deleteSelectedRoot(root: RootSummary) {
    try {
      await sendJson(`/api/crawl/roots/${encodeURIComponent(root.id ?? root.name)}?purge_index=true`, "DELETE", {});
      setDeleteRoot(null);
      setSelectedRootName("");
      setToast(`Watched path ${root.name} deleted and index rows purged. Files on disk were not deleted.`);
      await load();
    } catch (error) {
      setToast(`Could not delete watched path ${root.name}: ${errorMessage(error)}`);
    }
  }

  async function saveSetting(confirmed = false) {
    if (!settingEditor) return;
    if (requiresConfirmation(settingEditor) && !confirmed) {
      setConfirmSetting(settingEditor);
      return;
    }
    const value = parseSettingValue(settingValue, settingEditor.value);
    await sendJson(`/api/settings/${encodeURIComponent(settingEditor.key)}`, "PUT", {
      value,
      confirmed,
      reason: "dashboard update"
    });
    setConfirmSetting(null);
    setSettingEditor(null);
    setToast(`Setting ${settingEditor.key} saved.`);
    await load();
  }

  async function resetSetting(setting: SettingRow) {
    await sendJson(`/api/settings/${encodeURIComponent(setting.key)}/reset`, "POST", {});
    setToast(`Setting ${setting.key} reset.`);
    await load();
  }

  async function applySettings(component?: string) {
    await sendJson("/api/settings/apply", "POST", { component: component ?? null });
    setToast(component ? `Apply request acknowledged for ${component}.` : "Apply requests acknowledged.");
    await load();
  }

  async function loadReview() {
    setReviewLoading(true);
    try {
      const params = new URLSearchParams();
      params.set("review", reviewFilter);
      if (reviewStateFilter) params.set("state", reviewStateFilter);
      params.set("limit", "50");
      const [claims, capture, audit, policies, quality] = await Promise.all([
        fetchRequiredJson<ClaimReviewPayload>(`/api/claims?${params.toString()}`),
        getJson<CaptureReviewPayload>("/api/capture/review?limit=50", { jobs: [] }),
        getJson<AuditPayload>("/api/audit?limit=50", []),
        getJson<RetentionPolicyPayload>("/api/retention/policies", { policies: [] }),
        getJson<RetentionQualityPayload>("/api/retention/quality?limit=25", { summary: {}, candidates: [] })
      ]);
      const nextClaims = claims.claims ?? [];
      setClaimReview({ claims: nextClaims, counts: claims.counts ?? {} });
      setCaptureReview(capture);
      setCaptureReviewAudit(captureReviewAuditEvents(audit));
      setRetentionPolicies(policies);
      setRetentionQuality(quality);
      setSelectedClaimId((current) => nextClaims.some((claim) => claim.id === current) ? current : nextClaims[0]?.id ?? "");
    } catch (error) {
      setToast(`Could not load claim review queue: ${errorMessage(error)}`);
    } finally {
      setReviewLoading(false);
    }
  }

  async function loadClaimGraph(entityId: string) {
    try {
      const params = new URLSearchParams();
      params.set("entity_id", entityId);
      params.set("direction", "both");
      params.set("max_depth", "2");
      params.set("limit", "100");
      setClaimGraph(await fetchRequiredJson<GraphPayload>(`/api/graph/traverse?${params.toString()}`));
    } catch (error) {
      setClaimGraph({ edges: [] });
      setToast(`Could not load entity graph: ${errorMessage(error)}`);
    }
  }

  async function transitionReviewClaim(claim: ClaimReviewClaim, transition: string) {
    try {
      await sendJson(`/api/claims/${encodeURIComponent(claim.id)}/transitions`, "POST", {
        transition,
        reason: "dashboard review"
      });
      setToast(`Claim ${transition} recorded for ${claim.id}.`);
      await loadReview();
    } catch (error) {
      setToast(`Claim transition failed for ${claim.id}: ${errorMessage(error)}`);
    }
  }

  async function saveRetentionPolicy(policy: RetentionPolicy, update: RetentionPolicyUpdate) {
    try {
      await sendJson(`/api/retention/policies/${encodeURIComponent(policy.memory_class)}`, "PUT", update);
      setToast(`Retention policy saved for ${policy.memory_class}.`);
      await loadReview();
    } catch (error) {
      setToast(`Retention policy update failed for ${policy.memory_class}: ${errorMessage(error)}`);
    }
  }

  function openCaptureDecision(job: CaptureReviewJob, decision: "approve" | "reject") {
    setCaptureDecision({ job, decision });
    setCaptureDecisionReason("");
  }

  async function decideCaptureReviewJob() {
    if (!captureDecision?.job.id) return;
    const rationale = captureDecisionReason.trim();
    if (!rationale) return;
    try {
      await sendJson(`/api/capture/review/${encodeURIComponent(captureDecision.job.id)}/decision`, "POST", {
        decision: captureDecision.decision,
        rationale
      });
      setToast(`Capture job ${captureDecision.decision} recorded for ${captureDecision.job.id}.`);
      setCaptureDecision(null);
      setCaptureDecisionReason("");
      await loadReview();
    } catch (error) {
      setToast(`Capture review decision failed for ${captureDecision.job.id}: ${errorMessage(error)}`);
    }
  }

  async function runSearch(event: FormEvent) {
    event.preventDefault();
    const query = searchQuery.trim();
    if (!query) {
      setToast("Enter a search query first.");
      return;
    }
    const filters = buildDashboardRetrievalFilters(searchKind, searchCurrentOnly, searchIncludeSuppressed);
    const payload = await sendJson<ExplainPayload>("/api/explain", "POST", { query, limit: 8, filters });
    setSearchResults(Array.isArray(payload.results) ? payload.results : []);
    setSearchFilterTrace(payload.filter_trace ?? {});
    setSearchSuppression(payload.suppression ?? {});
    setSearchOpen(true);
    setActiveTab("retrieval");
  }

  async function openSearchResult(result: SearchResult) {
    const refKind = result.detail_ref?.kind ?? result.kind;
    const refId = result.detail_ref?.id ?? result.id ?? result.chunk_id ?? result.asset_id ?? result.mail_message_id;
    if (!refKind || !refId) {
      setToast("This result does not expose a detail reference.");
      return;
    }
    setResultDetail(null);
    setResultDetailLoading(true);
    try {
      const detail = await fetchRequiredJson<ResultDetail>(`/api/results/${encodeURIComponent(refKind)}/${encodeURIComponent(refId)}`);
      setResultDetail(detail);
    } catch (error) {
      setToast(`Could not load result detail: ${errorMessage(error)}`);
    } finally {
      setResultDetailLoading(false);
    }
  }

  async function copyResultPath(detail: ResultDetail) {
    const path = resultActionPath(detail, "copy_path") ?? stringFromUnknown(detail.metadata?.canonical_path) ?? stringFromUnknown(detail.metadata?.path);
    if (!path) {
      setToast("No canonical path is available for this result.");
      return;
    }
    const clipboard = window.navigator.clipboard;
    if (!clipboard?.writeText) {
      setToast("Clipboard access is unavailable in this browser context.");
      return;
    }
    try {
      await clipboard.writeText(path);
      setToast("Path copied.");
    } catch (error) {
      setToast(`Could not copy path: ${errorMessage(error)}`);
    }
  }

  async function copyDiagnostic(diagnostic: ErrorDiagnostic) {
    const clipboard = window.navigator.clipboard ?? globalThis.navigator?.clipboard;
    if (!clipboard?.writeText) {
      setToast("Clipboard access is unavailable in this browser context.");
      return;
    }
    try {
      await clipboard.writeText(JSON.stringify(diagnostic, null, 2));
      setToast("Diagnostic copied.");
    } catch (error) {
      setToast(`Could not copy diagnostic: ${errorMessage(error)}`);
    }
  }

  function navigateDiagnostic(diagnostic: ErrorDiagnostic) {
    const link = diagnostic.links?.find((item) => item.tab && navItems.some((nav) => nav.id === item.tab));
    if (!link?.tab) return;
    if (link.profile) setSelectedName(link.profile);
    if (link.root) setSelectedRootName(link.root);
    setActiveTab(link.tab as TabId);
  }

  async function runResultFileAction(detail: ResultDetail, action: "open" | "reveal") {
    if (!detail.asset_id) {
      setToast("No indexed asset id is available for this result.");
      return;
    }
    try {
      const payload = await sendJson<FileActionResponse>(`/api/corpus/assets/${encodeURIComponent(detail.asset_id)}/actions`, "POST", { action });
      setToast(fileActionToast(action, payload.state));
    } catch (error) {
      setToast(`File action failed: ${errorMessage(error)}`);
    }
  }

  const health = state.health;
  const runtime = health.runtime ?? {};
  const host = state.outlook.host ?? {};
  const hostStatus = host.status ?? "host_offline";
  const mailErrors = state.mail.errored_messages ?? 0;
  const blockedJobs = health.jobs?.blocked ?? 0;
  const oauthProfiles = state.mail.oauth?.profiles ?? [];
  const restartRows = restartSettings(state.settings);
  const currentToastTone = toast ? toastTone(toast) : "success";

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">
            <Archive size={28} />
          </div>
          <div>
            <div className="brand-title">FLUX</div>
            <div className="brand-subtitle">LLM-KB</div>
          </div>
        </div>
        <nav className="nav-list" aria-label="Primary">
          {navItems.map((item) => (
            <button
              className={item.id === activeTab ? "nav-item active" : "nav-item"}
              key={item.id}
              type="button"
              onClick={() => setActiveTab(item.id)}
            >
              {item.icon}
              <span>{item.label}</span>
            </button>
          ))}
        </nav>
        <div className="sidebar-card">
          <span>Version 0.5.0</span>
          <strong>Build: 2026-06-21</strong>
        </div>
        <div className="theme-toggle" aria-label="Theme">
          <button className={theme === "light" ? "active" : ""} type="button" onClick={() => setTheme("light")}>Light</button>
          <button className={theme === "dark" ? "active" : ""} type="button" onClick={() => setTheme("dark")}>Dark</button>
        </div>
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div>
            <h1>Operations</h1>
            <p>Dashboard control plane for capture, indexing, retrieval, and Windows Outlook host coordination.</p>
            <div className="refresh-meta">
              <span>Last updated {lastUpdated ? formatDate(lastUpdated.toISOString()) : "-"}</span>
              <span>Auto-refresh every {pollSeconds}s</span>
            </div>
          </div>
          <div className="top-actions">
            <StatusChip label="Database" ok={health.database?.ok} />
            <StatusChip label="API" ok />
            <StatusChip label="PG" ok={runtime.postgresql?.ok} />
            <form className="search-box interactive" onSubmit={(event) => void runSearch(event)}>
              <Search size={17} />
              <label className="sr-only" htmlFor="dashboard-search">Dashboard search</label>
              <input
                id="dashboard-search"
                aria-label="Dashboard search"
                placeholder="Search memories, mail, corpus..."
                value={searchQuery}
                onChange={(event) => setSearchQuery(event.target.value)}
              />
              <kbd>Ctrl K</kbd>
            </form>
            <button className="primary-action" type="button" title="Run the selected mail profile sync now" onClick={() => void requestProfileSync()}>
              <RefreshCcw size={17} />
              Sync Now
            </button>
            <div className="menu-wrap">
              <button className="ghost-action" aria-label="More actions" title="Open dashboard actions and diagnostics" type="button" onClick={() => setMoreOpen((open) => !open)}>
                More <ChevronDown size={16} />
              </button>
              {moreOpen && (
                <div className="action-menu" role="menu" aria-label="More actions">
                  <button role="menuitem" type="button" onClick={() => { setMoreOpen(false); void load(); }}>Refresh data</button>
                  <button role="menuitem" type="button" onClick={() => { setMoreOpen(false); setDebugOpen(true); }}>Open debug drawer</button>
                  <button role="menuitem" type="button" onClick={() => { setMoreOpen(false); setActiveTab("settings"); }}>Review settings</button>
                  <button role="menuitem" type="button" onClick={() => { setMoreOpen(false); setToast(host.command ?? "flux-kb outlook-host run"); }}>Show Outlook host command</button>
                </div>
              )}
            </div>
          </div>
        </header>

        {toast && (
          <div className={`toast ${currentToastTone}`} role={currentToastTone === "success" ? "status" : "alert"}>
            {currentToastTone === "error" ? <AlertCircle size={18} /> : <CheckCircle2 size={18} />}
            <span>{toast}</span>
            <button type="button" aria-label="Dismiss notification" onClick={() => setToast("")}><X size={15} /></button>
          </div>
        )}

        <HealthStrip health={health} mail={state.mail} blockedJobs={blockedJobs} />

        {activeTab === "mail" && (
          <MailTab
            state={state}
            loading={loading}
            profiles={profiles}
            selectedProfile={selectedProfile}
            hostStatus={hostStatus}
            host={host}
            oauthProfiles={oauthProfiles}
            mailErrors={mailErrors}
            onAddProfile={() => setProfileDialog("new")}
            onEditProfile={setProfileDialog}
            onSelectProfile={(profile) => setSelectedName(profile.name)}
            onSyncProfile={(profile) => void requestProfileSync(profile)}
            onPostProcessDryRun={(profile) => void runPostProcessDryRun(profile)}
            onOAuthStart={(profile, clientPath) => void startGmailOAuth(profile, clientPath)}
            onOAuthPathSave={(profile, clientPath) => void saveGmailOAuthClientPath(profile, clientPath)}
            onErrorDetail={setErrorDetail}
          />
        )}

        {activeTab === "health" && (
          <HealthTab
            state={state}
            hostStatus={hostStatus}
            restartRows={restartRows}
            onErrorDetail={setErrorDetail}
            onCopyDiagnostic={(diagnostic) => void copyDiagnostic(diagnostic)}
            onNavigateDiagnostic={navigateDiagnostic}
            onApplySettings={() => void applySettings()}
          />
        )}

        {activeTab === "corpus" && (
          <CorpusTab
            state={state}
            selectedRoot={selectedRoot}
            onAddRoot={() => setRootDialog("new")}
            onEditRoot={(root) => setRootDialog(root)}
            onDeleteRoot={(root) => setDeleteRoot(root)}
            onSelectRoot={(root) => setSelectedRootName(root.name)}
            onRefresh={() => void load()}
            onSync={() => void runCorpusSync()}
            onWatch={(enabled) => void setCorpusWatch(enabled)}
            onRootSync={(root, dryRun) => void runRootSync(root.name, dryRun)}
            onRootWatch={(root, enabled) => void setRootWatch(root.name, enabled)}
            onRootBackfill={(root) => void runRootBackfill(root.name)}
          />
        )}

        {activeTab === "settings" && (
          <SettingsTab
            settings={state.settings}
            restartRows={restartRows}
            onEdit={setSettingEditor}
            onReset={(setting) => void resetSetting(setting)}
            onApply={(component) => void applySettings(component)}
          />
        )}

        {activeTab === "retrieval" && (
          <RetrievalTab
            state={state}
            searchOpen={searchOpen}
            searchResults={searchResults}
            searchKind={searchKind}
            searchCurrentOnly={searchCurrentOnly}
            searchIncludeSuppressed={searchIncludeSuppressed}
            filterTrace={searchFilterTrace}
            suppression={searchSuppression}
            query={searchQuery}
            onClear={() => { setSearchOpen(false); setSearchResults([]); setSearchFilterTrace({}); setSearchSuppression({}); }}
            onSearchKind={setSearchKind}
            onSearchCurrentOnly={setSearchCurrentOnly}
            onSearchIncludeSuppressed={setSearchIncludeSuppressed}
            onErrorDetail={setErrorDetail}
            onOpenResult={(result) => void openSearchResult(result)}
          />
        )}

        {activeTab === "review" && (
          <ReviewTab
            payload={claimReview}
            capture={captureReview}
            captureAudit={captureReviewAudit}
            retentionPolicies={retentionPolicies}
            retentionQuality={retentionQuality}
            graph={claimGraph}
            selectedClaim={selectedClaim}
            loading={reviewLoading}
            reviewFilter={reviewFilter}
            stateFilter={reviewStateFilter}
            onReviewFilter={(value) => setReviewFilter(value)}
            onStateFilter={setReviewStateFilter}
            onSelectClaim={(claim) => setSelectedClaimId(claim.id)}
            onTransition={(claim, transition) => void transitionReviewClaim(claim, transition)}
            onCaptureDecision={openCaptureDecision}
            onRetentionPolicySave={(policy, update) => void saveRetentionPolicy(policy, update)}
            onRefresh={() => void loadReview()}
          />
        )}

        {activeTab === "jobs" && <JobsTab state={state} onRefresh={() => void load()} />}
      </main>

      {profileDialog && (
        <ProfileDialog
          profile={profileDialog === "new" ? undefined : profileDialog}
          onClose={() => setProfileDialog(null)}
          onSave={(form) => void saveProfile(form)}
        />
      )}

      {rootDialog && (
        <CrawlRootDialog
          root={rootDialog === "new" ? undefined : rootDialog}
          onClose={() => setRootDialog(null)}
          onSave={(form) => void saveCrawlRoot(form)}
        />
      )}

      {deleteRoot && (
        <ConfirmDialog
          title="Delete watched path"
          body={`Delete watched path ${deleteRoot.name} and purge its indexed files, chunks, embeddings, jobs, crawl runs, and watcher state. This does not delete files from disk.`}
          confirmLabel="Delete watched path and purge index"
          onCancel={() => setDeleteRoot(null)}
          onConfirm={() => void deleteSelectedRoot(deleteRoot)}
        />
      )}

      {settingEditor && (
        <SettingDialog
          setting={settingEditor}
          value={settingValue}
          onValue={setSettingValue}
          onClose={() => setSettingEditor(null)}
          onSave={() => void saveSetting(false)}
          onReset={() => void resetSetting(settingEditor)}
        />
      )}

      {confirmSetting && (
        <ConfirmDialog
          title="Confirm setting change"
          body={`Changing ${confirmSetting.key} uses apply mode ${confirmSetting.apply_mode}. Flux will queue the required runtime action instead of silently changing behavior.`}
          confirmLabel="Confirm and save"
          onCancel={() => setConfirmSetting(null)}
          onConfirm={() => void saveSetting(true)}
        />
      )}

      {captureDecision && (
        <CaptureDecisionDialog
          decision={captureDecision}
          reason={captureDecisionReason}
          onReason={setCaptureDecisionReason}
          onClose={() => setCaptureDecision(null)}
          onSave={() => void decideCaptureReviewJob()}
        />
      )}

      {errorDetail && (
        <InfoDialog title="Error detail" onClose={() => setErrorDetail(null)}>
          <p>{errorDetail}</p>
          <p className="muted">This error is reported by the shared health service and may represent an optional local extractor/tool dependency.</p>
        </InfoDialog>
      )}

      {(resultDetailLoading || resultDetail) && (
        <ResultDetailDialog
          detail={resultDetail}
          loading={resultDetailLoading}
          onClose={() => { setResultDetail(null); setResultDetailLoading(false); }}
          onCopyPath={(detail) => void copyResultPath(detail)}
          onFileAction={(detail, action) => void runResultFileAction(detail, action)}
        />
      )}

      {debugOpen && (
        <InfoDialog title="Developer Debug Drawer" onClose={() => setDebugOpen(false)}>
          <p className="muted">Raw payloads are shown only for diagnostics.</p>
          <pre className="debug-drawer">{JSON.stringify(state, null, 2)}</pre>
        </InfoDialog>
      )}

    </div>
  );
}

function MailTab({
  state,
  loading,
  profiles,
  selectedProfile,
  hostStatus,
  host,
  oauthProfiles,
  mailErrors,
  onAddProfile,
  onEditProfile,
  onSelectProfile,
  onSyncProfile,
  onPostProcessDryRun,
  onOAuthStart,
  onOAuthPathSave,
  onErrorDetail
}: {
  state: LoadState;
  loading: boolean;
  profiles: MailProfile[];
  selectedProfile?: MailProfile;
  hostStatus: string;
  host: OutlookStatus["host"];
  oauthProfiles: Array<{ profile_name?: string; status?: string }>;
  mailErrors: number;
  onAddProfile: () => void;
  onEditProfile: (profile: MailProfile) => void;
  onSelectProfile: (profile: MailProfile) => void;
  onSyncProfile: (profile: MailProfile) => void;
  onPostProcessDryRun: (profile: MailProfile) => void;
  onOAuthStart: (profile: MailProfile, clientPath: string) => void;
  onOAuthPathSave: (profile: MailProfile, clientPath: string) => void;
  onErrorDetail: (error: string) => void;
}) {
  const hasOutlookProfiles = profiles.some((profile) => profile.source_type === "outlook_com") || (state.outlook.pending_requests?.length ?? 0) > 0;
  return (
    <>
      <section className="main-grid">
        <Panel className="profiles-panel" title="Mail Profiles" action={<button className="small-primary" type="button" onClick={onAddProfile}><Plus size={15} /> Add Profile</button>}>
          <div className="table-toolbar">
            <span>{loading ? "Refreshing..." : `Showing ${profiles.length} profile${profiles.length === 1 ? "" : "s"}`}</span>
            <div>
              <button className="icon-button" type="button" aria-label="Filter profiles" title="Profile filters are not needed for the current profile count" disabled><SlidersHorizontal size={18} /></button>
              <button className="icon-button" type="button" aria-label="Profile table options" title="Profile table options will appear here when bulk actions are added" disabled><MoreVertical size={18} /></button>
            </div>
          </div>
          <ProfileTable
            profiles={profiles}
            selectedProfile={selectedProfile}
            oauthProfiles={oauthProfiles}
            hostStatus={hostStatus}
            mailErrors={mailErrors}
            onSelect={onSelectProfile}
            onSync={onSyncProfile}
            onEdit={onEditProfile}
          />
        </Panel>

        <Panel className="inspector-panel" title="Profile Details" action={<span className={selectedProfile?.sync_enabled ? "state-pill enabled" : "state-pill"}>{selectedProfile?.sync_enabled ? "Enabled" : "Manual"}</span>}>
          <Inspector
            profile={selectedProfile}
            hostStatus={hostStatus}
            hostCommand={host?.command}
            oauthProfile={oauthProfiles.find((row) => row.profile_name === selectedProfile?.name)}
            runs={(state.mail.scheduler?.recent_runs ?? []).filter((run) => run.profile_name === selectedProfile?.name)}
            onSync={() => selectedProfile && onSyncProfile(selectedProfile)}
            onEdit={() => selectedProfile && onEditProfile(selectedProfile)}
            onOAuthStart={(clientPath) => selectedProfile && onOAuthStart(selectedProfile, clientPath)}
            onOAuthPathSave={(clientPath) => selectedProfile && onOAuthPathSave(selectedProfile, clientPath)}
          />
        </Panel>
      </section>

      <section className="lower-grid">
        <MailSchedulerPanel scheduler={state.mail.scheduler} />
        <MailPostProcessPanel mail={state.mail} selectedProfile={selectedProfile} onDryRun={onPostProcessDryRun} />
        <MailStatusPanel mail={state.mail} hostStatus={hostStatus} showOutlook={hasOutlookProfiles} />
        <MailErrorsPanel mail={state.mail} errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
        {hasOutlookProfiles && <OutlookHostPanel host={host} hostStatus={hostStatus} pending={state.outlook.pending_requests?.length ?? 0} />}
      </section>
    </>
  );
}

function MailSchedulerPanel({ scheduler }: { scheduler?: MailSchedulerStatus }) {
  const counts = scheduler?.counts ?? {};
  const rows: Array<[string, string, string]> = [
    ["Due", "profiles", `${counts.due ?? 0} due`],
    ["Queued", "runs", `${counts.queued ?? 0} queued`],
    ["Claimed", "runs", `${counts.claimed ?? 0} claimed`],
    ["Running", "runs", `${counts.running ?? 0} running`],
    ["Blocked Auth", "profiles", `${counts.blocked_auth ?? 0} blocked`],
    ["Backoff", "runs", `${counts.backoff ?? 0} retrying`],
    ["Failed", "runs", `${counts.failed ?? 0} failed`]
  ];
  const nextRun = nextMailRun(scheduler?.recent_runs ?? []);
  return (
    <Panel title="IMAP Scheduler">
      <MiniTable rows={rows} />
      <div className="scheduler-note">
        <span>Next run</span>
        <strong>{nextRun}</strong>
      </div>
      {(scheduler?.diagnostics?.length ?? 0) > 0 && (
        <div className="scheduler-warning">
          <AlertTriangle size={16} />
          <span>{scheduler?.diagnostics?.[0]?.message ?? "Mail scheduler requires attention."}</span>
        </div>
      )}
    </Panel>
  );
}

function MailStatusPanel({ mail, hostStatus, showOutlook }: { mail: MailStatus; hostStatus: string; showOutlook: boolean }) {
  const postProcessCounts = mail.post_process?.counts ?? {};
  const rows = [
    ["Profiles", "enabled", String(mail.enabled_profiles ?? 0)],
    ["Exports", "messages", String(mail.exported_messages ?? 0)],
    ["Errors", "messages", String(mail.errored_messages ?? 0)],
    ["Post-process failed", "events", String(postProcessCounts.failed ?? 0)],
    ["Post-process blocked", "events", String(postProcessCounts.blocked_config ?? 0)]
  ];
  if (showOutlook) {
    rows.push(["Outlook host", "state", hostStatusLabel(hostStatus)]);
  }
  return (
    <Panel title="Mail Runtime">
      <MiniTable rows={rows} />
    </Panel>
  );
}

function MailPostProcessPanel({ mail, selectedProfile, onDryRun }: { mail: MailStatus; selectedProfile?: MailProfile; onDryRun: (profile: MailProfile) => void }) {
  const events = mail.post_process?.recent_events ?? [];
  return (
    <Panel
      title="Post Process"
      action={selectedProfile ? (
        <button
          className="ghost-action compact"
          type="button"
          aria-label={`Dry run post process for ${selectedProfile.name}`}
          title={`Preview post-process commands for ${selectedProfile.name}`}
          onClick={() => onDryRun(selectedProfile)}
        >
          <Play size={15} /> Dry run
        </button>
      ) : undefined}
    >
      <div className="run-history">
        {events.slice(0, 5).map((event) => (
          <div className="run-row" key={event.id ?? `${event.profile_name}-${event.created_at}-${event.action}`}>
            <div>
              <span className={event.status === "failed" || event.status === "blocked_config" ? "state-pill warning" : "state-pill enabled"}>{mailPostProcessStatusLabel(event.status)}</span>
              <strong>{mailPostProcessPolicyLabel(event.policy)}</strong>
              <span>{event.profile_name ?? "profile"} - {mailPostProcessActionLabel(event.action)}{event.dry_run ? " - dry-run" : ""}</span>
            </div>
            <div>
              <span>{event.provider ?? "provider"}</span>
              <span>{formatDate(event.created_at)}</span>
            </div>
            {event.error && <p>{event.error}</p>}
          </div>
        ))}
        {events.length === 0 && <p className="muted">No recent post-process events.</p>}
      </div>
    </Panel>
  );
}

function MailErrorsPanel({ mail, errors, onErrorDetail }: { mail: MailStatus; errors: string[]; onErrorDetail: (error: string) => void }) {
  const mailErrors = errors.filter((error) => /mail|imap|oauth|gmail|outlook/i.test(error));
  return (
    <Panel title="Mail Errors">
      <div className="error-list">
        {mailErrors.slice(0, 4).map((error, index) => (
          <div className="error-item" key={`${error}-${index}`}>
            <span className="error-dot" />
            <div>
              <strong>{error}</strong>
              <span>Captured from mail ingestion or profile auth state</span>
            </div>
            <button type="button" title="Show mail error detail" aria-label={`View mail error ${error}`} onClick={() => onErrorDetail(error)}>View</button>
          </div>
        ))}
        {mailErrors.length === 0 && <p className="muted">{mail.errored_messages ? `${mail.errored_messages} errored message records. Select a profile or open Jobs for details.` : "No mail errors."}</p>}
      </div>
    </Panel>
  );
}

function HealthTab({
  state,
  hostStatus,
  restartRows,
  onErrorDetail,
  onCopyDiagnostic,
  onNavigateDiagnostic,
  onApplySettings
}: {
  state: LoadState;
  hostStatus: string;
  restartRows: SettingRow[];
  onErrorDetail: (error: string) => void;
  onCopyDiagnostic: (diagnostic: ErrorDiagnostic) => void;
  onNavigateDiagnostic: (diagnostic: ErrorDiagnostic) => void;
  onApplySettings: () => void;
}) {
  const runtimeRows = Object.entries(state.health.runtime ?? {});
  const hostAgent = state.health.host_agent;
  const codex = state.health.codex;
  const workers = state.health.workers;
  const deployment = state.health.deployment;
  return (
    <section className="tab-grid">
      <Panel title="System Health">
        <div className="status-grid">
          <StatusTile label="Database" ok={state.health.database?.ok} message={state.health.database?.message} />
          {runtimeRows.map(([key, value]) => <StatusTile key={key} label={key} ok={value.ok} message={value.message} />)}
          <StatusTile label="Outlook Host" ok={hostStatus === "running"} message={hostStatusLabel(hostStatus)} />
          <StatusTile label="Host Agent" ok={hostAgent?.status === "running"} message={hostAgent?.status ?? "host_agent_offline"} />
          <StatusTile label="Corpus Worker" ok={(workers?.active ?? 0) > 0} message={`${workers?.active ?? 0} active worker${workers?.active === 1 ? "" : "s"}`} />
          <StatusTile
            label="Codex Integration"
            ok={codex?.status === "ready"}
            message={codex?.restart_required ? "Codex restart required" : (codex?.status ?? "unknown")}
          />
        </div>
      </Panel>
      <CodexHooksPanel codex={codex} />
      <AccelerationPanel acceleration={state.health.acceleration} />
      <Panel title="Deployment">
        <div className="status-grid">
          <StatusTile
            label="Runtime Mode"
            ok={deployment?.mode === "production" && !deployment?.repo_coupled}
            message={deployment?.mode ?? "development"}
          />
          <StatusTile
            label="Repo Coupled"
            ok={!deployment?.repo_coupled}
            message={deployment?.repo_coupled ? "runtime is repo-coupled" : "runtime is separated"}
          />
          <StatusTile label="Install Root" ok={Boolean(deployment?.install_root)} message={deployment?.install_root ?? "not deployed"} />
          <StatusTile label="Image Tag" ok={Boolean(deployment?.image_tag)} message={deployment?.image_tag ?? "local/dev"} />
          <StatusTile label="Private Dir" ok={Boolean(deployment?.private_dir)} message={deployment?.private_dir ?? "not configured"} />
          <StatusTile label="Data Dir" ok={Boolean(deployment?.data_dir)} message={deployment?.data_dir ?? "not configured"} />
        </div>
      </Panel>
      <ActionableDiagnostics diagnostics={state.health.recent_error_details ?? []} onCopy={onCopyDiagnostic} onNavigate={onNavigateDiagnostic} />
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
      <Panel title={`Runtime Actions (${restartRows.length})`} action={<button className="small-primary" type="button" onClick={onApplySettings}>Apply acknowledged</button>}>
        <SettingsPreview rows={restartRows} />
      </Panel>
    </section>
  );
}

function AccelerationPanel({ acceleration }: { acceleration?: AccelerationStatus }) {
  const [benchmarkStatus, setBenchmarkStatus] = useState("");
  const [benchmarkRunning, setBenchmarkRunning] = useState(false);
  const capabilities = acceleration?.capabilities ?? {};
  const cache = acceleration?.cache ?? {};
  const families = acceleration?.worker_families ?? [];
  const watcherBackend = capabilities.watcher_backend;
  const benchmarkHistory = acceleration?.benchmarks?.history ?? [];
  const familyRows = families.slice(0, 8).map((family) => {
    const name = family.family ?? "general";
    const pending = family.pending ?? 0;
    const p95 = family.p95_duration_ms;
    const ocrHits = family.ocr_cache_hits ?? 0;
    const ocrMisses = family.ocr_cache_misses ?? 0;
    const asrHits = family.asr_cache_hits ?? 0;
    const asrMisses = family.asr_cache_misses ?? 0;
    const asrSegments = family.asr_segments ?? 0;
    const visionHits = family.vision_cache_hits ?? 0;
    const visionMisses = family.vision_cache_misses ?? 0;
    const visionDescriptions = family.vision_descriptions ?? 0;
    const visionBlocked = family.vision_blocked_dependency_count ?? 0;
    const decorativeSkips = family.decorative_image_skips ?? 0;
    const frameSamples = family.frame_sample_count ?? 0;
    const thumbnailHits = family.thumbnail_cache_hits ?? 0;
    const thumbnailMisses = family.thumbnail_cache_misses ?? 0;
    const embeddingVectors = family.embedding_vectors ?? 0;
    const embeddingSkipped = family.embedding_skipped_unchanged ?? 0;
    const embeddingBatches = family.embedding_batches ?? 0;
    const embeddingHits = family.embedding_cache_hits ?? 0;
    const embeddingMisses = family.embedding_cache_misses ?? 0;
    const hasOcrTelemetry = ocrHits > 0 || ocrMisses > 0;
    const hasAsrTelemetry = asrHits > 0 || asrMisses > 0 || asrSegments > 0;
    const hasVisionTelemetry = visionHits > 0 || visionMisses > 0 || visionDescriptions > 0 || visionBlocked > 0;
    const hasDecorativeTelemetry = decorativeSkips > 0;
    const hasFrameTelemetry = frameSamples > 0 || thumbnailHits > 0 || thumbnailMisses > 0;
    const hasEmbeddingTelemetry = embeddingVectors > 0 || embeddingSkipped > 0 || embeddingBatches > 0 || embeddingHits > 0 || embeddingMisses > 0;
    const duration = p95 == null ? `${family.running ?? 0} running` : `p95 ${p95}ms`;
    const parts = [duration];
    if (hasOcrTelemetry) parts.push(`OCR ${ocrHits} hit / ${ocrMisses} miss`);
    if (hasAsrTelemetry) parts.push(`ASR ${asrHits} hit / ${asrMisses} miss; ${asrSegments} segments`);
    if (hasVisionTelemetry) parts.push(`Vision ${visionHits} hit / ${visionMisses} miss; ${visionDescriptions} descriptions; ${visionBlocked} blocked`);
    if (hasDecorativeTelemetry) parts.push(`${decorativeSkips} decorative skips`);
    if (hasFrameTelemetry) parts.push(`Frames ${frameSamples} sampled; thumbnails ${thumbnailHits} hit / ${thumbnailMisses} miss`);
    if (hasEmbeddingTelemetry) parts.push(`Embeddings ${embeddingVectors} vectors; ${embeddingSkipped} skipped; ${embeddingBatches} batches; cache ${embeddingHits} hit / ${embeddingMisses} miss`);
    const status = parts.join("; ");
    return [
      name,
      `${pending} pending`,
      status
    ] as [string, string, string];
  });
  const backpressureRows = families
    .filter((family) => family.backpressure || (family.retrying_locked ?? 0) > 0 || (family.blocked_locked ?? 0) > 0 || (family.manifest_skipped_unchanged ?? 0) > 0 || (family.parser_cache_hits ?? 0) > 0 || (family.parser_cache_misses ?? 0) > 0)
    .slice(0, 6)
    .map((family) => {
      const parts = [
        family.backpressure ? humanizeIdentifier(family.backpressure) : null,
        family.oldest_pending_age_seconds != null ? `oldest ${family.oldest_pending_age_seconds}s` : null,
        (family.retrying_locked ?? 0) > 0 ? `retry ${family.retrying_locked}` : null,
        (family.blocked_locked ?? 0) > 0 ? `blocked locks ${family.blocked_locked}` : null,
        (family.parser_cache_hits ?? 0) || (family.parser_cache_misses ?? 0) ? `parser ${family.parser_cache_hits ?? 0} hit / ${family.parser_cache_misses ?? 0} miss` : null,
        (family.manifest_skipped_unchanged ?? 0) > 0 ? `${family.manifest_skipped_unchanged} manifest skips` : null
      ].filter(Boolean);
      return [
        family.family ?? "general",
        `cap ${family.running ?? 0}/${family.configured_cap ?? 0}`,
        parts.join("; ") || "no backpressure"
      ] as [string, string, string];
    });
  const benchmarkRows = benchmarkHistory.slice(0, 5).map((run) => {
    const elapsedDelta = run.previous_elapsed_delta_ms;
    const throughputDelta = run.previous_throughput_delta;
    const elapsedText = elapsedDelta == null ? "no prior" : `${elapsedDelta >= 0 ? "+" : ""}${elapsedDelta}ms`;
    const throughputText = throughputDelta == null ? "" : `; ${throughputDelta >= 0 ? "+" : ""}${Math.round(throughputDelta)} files/s`;
    const modeText = `${humanizeIdentifier(run.mode ?? "scan")} / ${run.warm_state ?? "cold"} / pass ${run.pass_index ?? 1}`;
    const metadata = [
      run.label,
      run.deployment_label,
      run.scope_type ? humanizeIdentifier(run.scope_type) : null,
      run.hash_parallelism != null ? `hash ${run.hash_parallelism}` : null,
      run.worker_count != null ? `workers ${run.worker_count}` : null,
      (run.manifest_skipped_unchanged ?? 0) > 0 ? `${run.manifest_skipped_unchanged} manifest skips` : null,
      run.model_telemetry?.local_model?.state ? `model ${run.model_telemetry.local_model.state}` : null,
      (run.model_telemetry?.blocked_dependency_count ?? 0) > 0 ? `${run.model_telemetry?.blocked_dependency_count} blocked` : null
    ].filter(Boolean).join("; ") || "metadata only";
    return [
      run.fixture ?? "fixture",
      modeText,
      `${Math.round(run.throughput_files_per_second ?? 0)} files/s; ${elapsedText}${throughputText}`,
      metadata
    ] as [string, string, string, string];
  });
  async function runScanBenchmark() {
    try {
      setBenchmarkRunning(true);
      setBenchmarkStatus("Benchmark queued...");
      await sendJson("/api/acceleration/benchmarks/run", "POST", { fixture: "all", files: 10, mode: "scan", passes: 2, workers: 1, family: "all", scope: "synthetic" });
      setBenchmarkStatus("Benchmark run recorded.");
    } catch (error) {
      setBenchmarkStatus(`Benchmark failed: ${errorMessage(error)}`);
    } finally {
      setBenchmarkRunning(false);
    }
  }
  return (
    <Panel title="Acceleration">
      <div className="status-grid">
        <StatusTile label="NVIDIA" ok={capabilities.nvidia?.ok} message={accelerationCapabilityMessage(capabilities.nvidia)} />
        <StatusTile label="ONNX Runtime" ok={capabilities.onnxruntime?.ok} message={accelerationCapabilityMessage(capabilities.onnxruntime)} />
        <StatusTile label="Local Model" ok={capabilities.local_model?.ok} message={accelerationCapabilityMessage(capabilities.local_model)} />
        <StatusTile label="Watcher Backend" ok={capabilities.watcher_backend?.ok} message={accelerationCapabilityMessage(capabilities.watcher_backend)} />
      </div>
      <div className="settings-list">
        <div className="settings-row">
          <strong>Cache Root</strong>
          <span>{cache.root ?? "not configured"}</span>
          <em>{cache.source ?? "default"}</em>
        </div>
        <div className="settings-row">
          <strong>Watcher Policy</strong>
          <span>{watcherBackend?.selected_backend ?? watcherBackend?.provider ?? "unknown"}</span>
          <em>{[watcherBackend?.policy, watcherBackend?.fallback_reason].filter(Boolean).join(" / ") || (watcherBackend?.native ? "native" : "fallback")}</em>
        </div>
      </div>
      {familyRows.length > 0 ? <MiniTable rows={familyRows} /> : <p className="muted">No worker-family telemetry yet.</p>}
      {backpressureRows.length > 0 && (
        <div className="settings-list">
          <div className="settings-row"><strong>Family Backpressure</strong><span>{backpressureRows.length} families reporting pressure or parser/manifest telemetry</span><em>debug</em></div>
          <MiniTable rows={backpressureRows} />
        </div>
      )}
      <div className="settings-list">
        <div className="settings-row">
          <strong>Benchmark History</strong>
          <span>{benchmarkRows.length} recent synthetic runs</span>
          <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={runScanBenchmark}>
            <Play size={15} /> Run scan benchmark
          </button>
        </div>
        {benchmarkRows.length > 0 ? <MiniTable rows={benchmarkRows} /> : <p className="panel-note">No synthetic benchmark history yet.</p>}
        {benchmarkStatus && <p className="panel-note">{benchmarkStatus}</p>}
      </div>
    </Panel>
  );
}

function accelerationCapabilityMessage(capability?: AccelerationCapability): string {
  if (!capability) return "unknown";
  if (capability.message) return capability.message;
  if (capability.selected_backend) return `${capability.selected_backend}${capability.fallback_reason ? ` (${capability.fallback_reason})` : ""}`;
  if (capability.gpus?.length) return capability.gpus.map((gpu) => gpu.name).filter(Boolean).join(", ");
  if (capability.providers?.length) return capability.providers.join(", ");
  if (capability.state) return capability.state;
  if (capability.provider) return capability.provider;
  if (capability.count != null) return `${capability.count}`;
  return capability.ok ? "available" : "unavailable";
}

function ActionableDiagnostics({
  diagnostics,
  onCopy,
  onNavigate
}: {
  diagnostics: ErrorDiagnostic[];
  onCopy: (diagnostic: ErrorDiagnostic) => void;
  onNavigate: (diagnostic: ErrorDiagnostic) => void;
}) {
  const [openCodes, setOpenCodes] = useState<Record<string, boolean>>({});
  return (
    <Panel title="Actionable Diagnostics">
      <div className="diagnostics-list">
        {diagnostics.slice(0, 6).map((diagnostic, index) => {
          const code = diagnostic.code || `diagnostic-${index}`;
          const open = Boolean(openCodes[code]);
          const canNavigate = Boolean(diagnostic.links?.some((link) => link.tab));
          const navigateLabel = diagnostic.links?.find((link) => link.tab)?.label ?? "Open";
          return (
            <article className={`diagnostic-item ${diagnostic.severity === "warning" ? "warning" : diagnostic.severity === "info" ? "info" : "error"}`} role={diagnostic.severity === "warning" ? "status" : "alert"} key={`${code}-${index}`}>
              <div className="diagnostic-main">
                <span className="diagnostic-code">{code}</span>
                <strong>{diagnostic.message ?? "Unknown diagnostic"}</strong>
                <p>{diagnostic.user_action ?? "Review the related dashboard panel for details."}</p>
                <div className="diagnostic-meta">
                  <span>{diagnostic.component ?? "component"}</span>
                  {diagnostic.stage && <span>{diagnostic.stage}</span>}
                  <span>{diagnostic.retryable ? "retryable" : "not retryable"}</span>
                  {diagnostic.target?.id && <span>{diagnostic.target.type ?? "target"}: {diagnostic.target.id}</span>}
                </div>
              </div>
              <div className="diagnostic-actions">
                <button type="button" aria-label={`Show diagnostic detail ${code}`} onClick={() => setOpenCodes((current) => ({ ...current, [code]: !open }))}>
                  <ChevronDown size={15} /> Details
                </button>
                <button type="button" aria-label={`Copy diagnostic ${code}`} onClick={() => onCopy(diagnostic)}>
                  <Copy size={15} /> Copy
                </button>
                {canNavigate && (
                  <button type="button" aria-label={`Open ${navigateLabel} for ${code}`} onClick={() => onNavigate(diagnostic)}>
                    <ExternalLink size={15} /> {navigateLabel}
                  </button>
                )}
              </div>
              {open && (
                <pre className="diagnostic-detail">
                  {diagnostic.technical_detail ?? JSON.stringify(diagnostic, null, 2)}
                </pre>
              )}
            </article>
          );
        })}
        {diagnostics.length === 0 && <p className="muted">No structured diagnostics.</p>}
      </div>
    </Panel>
  );
}

function CodexHooksPanel({ codex }: { codex?: HealthPayload["codex"] }) {
  const policy = codex?.hook_policy ?? {};
  const mcp = codex?.mcp ?? {};
  const recent = policy.recent_events ?? [];
  const lastEvent = recent[0];
  return (
    <Panel title="Codex Hooks">
      <div className="status-grid">
        <StatusTile label="Hook policy" ok={policy.status === "active"} message={policy.status ?? "unknown"} />
        <StatusTile label="Preflight brief" ok={policy.preflight_enabled} message={policy.preflight_enabled ? `enabled - ${policy.token_budget ?? "-"} tokens` : "disabled"} />
        <StatusTile label="Turn capture" ok={policy.capture_enabled} message={policy.capture_enabled ? "enabled" : "disabled"} />
        <StatusTile label="MCP tools" ok={mcp.configured && mcp.enabled && mcp.dependency_available} message={mcp.configured && mcp.enabled && mcp.dependency_available ? "kb.brief ready" : mcp.message ?? "not configured"} />
      </div>
      {lastEvent ? (
        <div className="settings-list">
          <div className="settings-row">
            <strong>Last hook event</strong>
            <span>{lastEvent.event_type ?? "unknown"}</span>
            <em>{formatDate(lastEvent.created_at)}</em>
          </div>
        </div>
      ) : (
        <p className="muted">No Codex hook audit events recorded yet.</p>
      )}
    </Panel>
  );
}

function CorpusTab({
  state,
  selectedRoot,
  onAddRoot,
  onEditRoot,
  onDeleteRoot,
  onSelectRoot,
  onRefresh,
  onSync,
  onWatch,
  onRootSync,
  onRootWatch,
  onRootBackfill
}: {
  state: LoadState;
  selectedRoot?: RootSummary;
  onAddRoot: () => void;
  onEditRoot: (root: RootSummary) => void;
  onDeleteRoot: (root: RootSummary) => void;
  onSelectRoot: (root: RootSummary) => void;
  onRefresh: () => void;
  onSync: () => void;
  onWatch: (enabled: boolean) => void;
  onRootSync: (root: RootSummary, dryRun: boolean) => void;
  onRootWatch: (root: RootSummary, enabled: boolean) => void;
  onRootBackfill: (root: RootSummary) => void;
}) {
  const roots = state.crawl.root_summaries ?? (state.crawl.roots ?? []);
  const status = state.crawl.status ?? {};
  return (
    <section className="tab-grid corpus-tab">
      <Panel title="Corpus Monitor" action={<button className="small-primary" type="button" title="Add a monitored root path for recursive crawl and watch" onClick={onAddRoot}><Plus size={15} /> Add Watched Path</button>}>
        <div className="corpus-actions">
          <button className="small-primary" type="button" title="Run a crawl sync for all configured roots" onClick={onSync}><RefreshCcw size={15} /> Sync all</button>
          <button className="ghost-action compact" type="button" title="Reload dashboard crawl state" onClick={onRefresh}>Refresh</button>
          <button className="ghost-action compact" type="button" title="Enable watch mode for every monitored root" onClick={() => onWatch(true)}>Enable all watch</button>
          <button className="ghost-action compact" type="button" title="Disable watch mode without deleting roots" onClick={() => onWatch(false)}>Disable all watch</button>
        </div>
        <div className="summary-cards">
          <Stat label="Roots" value={String(roots.length)} />
          <Stat label="Active watch" value={String(status.active_watch_roots ?? state.health.watcher?.active_roots ?? 0)} />
          <Stat label="Disabled watch" value={String(status.disabled_watch_roots ?? state.health.watcher?.disabled_roots ?? 0)} />
          <Stat label="Stale" value={String(state.health.watcher?.stale_count ?? 0)} />
          <Stat label="Workers" value={String(state.health.workers?.active ?? 0)} />
        </div>
      </Panel>
      <section className="main-grid">
        <Panel className="profiles-panel" title="Monitored Roots">
          <RootTable
            roots={roots}
            selectedRoot={selectedRoot}
            onSelect={onSelectRoot}
            onSync={onRootSync}
            onWatch={onRootWatch}
            onEdit={onEditRoot}
            onDelete={onDeleteRoot}
            onBackfill={onRootBackfill}
          />
        </Panel>
        <Panel title="Root Details" action={selectedRoot ? <RootStateBadge state={selectedRoot.state} /> : undefined}>
          <RootInspector root={selectedRoot} onEdit={onEditRoot} onDelete={onDeleteRoot} onBackfill={onRootBackfill} />
        </Panel>
      </section>
      <Panel title="Extractor Availability">
        <p className="panel-note">Optional local tools expand media extraction. Missing ffmpeg, ffprobe, or faster_whisper blocks only the related deferred media jobs; core health can stay green.</p>
        <div className="status-grid">
          {Object.entries(state.health.extractors ?? {}).map(([key, value]) => <StatusTile key={key} label={key} ok={value.ok} message={value.message} />)}
          {Object.keys(state.health.extractors ?? {}).length === 0 && <p className="muted">Extractor status is not available yet.</p>}
        </div>
      </Panel>
    </section>
  );
}

function RootTable({
  roots,
  selectedRoot,
  onSelect,
  onSync,
  onWatch,
  onEdit,
  onDelete,
  onBackfill
}: {
  roots: RootSummary[];
  selectedRoot?: RootSummary;
  onSelect: (root: RootSummary) => void;
  onSync: (root: RootSummary, dryRun: boolean) => void;
  onWatch: (root: RootSummary, enabled: boolean) => void;
  onEdit: (root: RootSummary) => void;
  onDelete: (root: RootSummary) => void;
  onBackfill: (root: RootSummary) => void;
}) {
  return (
    <table className="profile-table root-table" aria-label="Monitored roots">
      <thead>
        <tr>
          <th>Name</th>
          <th>Path</th>
          <th>Status</th>
          <th>Assets</th>
          <th>Jobs</th>
          <th>Watch</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        {roots.map((root) => {
          const pendingStableAssets = root.asset_counts?.pending_stable ?? 0;
          const lockedAssets = (root.asset_counts?.retrying_locked ?? 0) + (root.asset_counts?.blocked_locked ?? 0);
          const retryingLockedJobs = root.job_counts?.retrying_locked ?? 0;
          const blockedLockedJobs = root.job_counts?.blocked_locked ?? 0;
          return (
          <tr
            key={root.name}
            className={selectedRoot?.name === root.name ? "selected" : ""}
            onClick={() => onSelect(root)}
          >
            <td>
              <button className="row-select" type="button" aria-label={`Select ${root.name}`}>
                {selectedRoot?.name === root.name ? <CheckCircle2 size={15} /> : <Square size={12} />}
              </button>
              <div>
                <strong>{root.name}</strong>
                <span>{root.recursive ? "Recursive" : "Single level"} - trust {root.trust_rank ?? 500}</span>
              </div>
            </td>
            <td className="path-cell" title={root.root_path}>{root.root_path}</td>
            <td><RootStateBadge state={root.state} /></td>
            <td>
              <strong>{root.asset_counts?.indexed ?? 0} indexed</strong>
              <span>{root.asset_counts?.queued ?? 0} queued - {root.asset_counts?.duplicate_suppressed ?? 0} duplicate</span>
              {(pendingStableAssets || lockedAssets) ? <span>{pendingStableAssets} pending stable - {lockedAssets} locked</span> : null}
            </td>
            <td>
              <strong>{root.job_counts?.pending ?? 0} pending</strong>
              <span>{root.job_counts?.blocked ?? 0} blocked - {root.job_counts?.failed ?? 0} failed</span>
              {(retryingLockedJobs || blockedLockedJobs) ? <span>{retryingLockedJobs} retrying locked - {blockedLockedJobs} blocked locked</span> : null}
            </td>
            <td>
              <strong>{root.watch_enabled ? "On" : "Off"}</strong>
              <span>{root.watcher?.status ?? "stopped"}</span>
            </td>
            <td>
              <div className="row-actions root-actions">
                <button type="button" aria-label={`Sync ${root.name}`} title={`Sync ${root.name} now`} onClick={(event) => { event.stopPropagation(); onSync(root, false); }}><RefreshCcw size={15} /></button>
                <button type="button" aria-label={`Dry run ${root.name}`} title={`Preview crawl changes for ${root.name}`} onClick={(event) => { event.stopPropagation(); onSync(root, true); }}><ListFilter size={15} /></button>
                <button type="button" aria-label={`Run backfill for ${root.name}`} title={`Process deferred jobs for ${root.name}`} onClick={(event) => { event.stopPropagation(); onBackfill(root); }}><Play size={15} /></button>
                <button type="button" aria-label={`${root.watch_enabled ? "Disable" : "Enable"} watch ${root.name}`} title={`${root.watch_enabled ? "Disable" : "Enable"} recursive watch for ${root.name}`} onClick={(event) => { event.stopPropagation(); onWatch(root, !root.watch_enabled); }}>
                  {root.watch_enabled ? <Square size={15} /> : <Play size={15} />}
                </button>
                <button type="button" aria-label={`Edit ${root.name}`} title={`Edit watched path ${root.name}`} onClick={(event) => { event.stopPropagation(); onEdit(root); }}><Wrench size={15} /></button>
                <button type="button" aria-label={`Delete ${root.name}`} title={`Delete watched path ${root.name} from the Flux index`} onClick={(event) => { event.stopPropagation(); onDelete(root); }}><Trash2 size={15} /></button>
              </div>
            </td>
          </tr>
        );
        })}
        {roots.length === 0 && (
          <tr>
            <td colSpan={7} className="empty-row">No monitored roots configured yet.</td>
          </tr>
        )}
      </tbody>
    </table>
  );
}

function RootInspector({
  root,
  onEdit,
  onDelete,
  onBackfill
}: {
  root?: RootSummary;
  onEdit: (root: RootSummary) => void;
  onDelete: (root: RootSummary) => void;
  onBackfill: (root: RootSummary) => void;
}) {
  if (!root) {
    return <div className="empty-inspector"><p className="muted">Add or select a watched path to see crawl, watch, and job status.</p></div>;
  }
  const latest = root.latest_crawl ?? {};
  return (
    <div className="inspector root-inspector">
      <div className="profile-head">
        <div className="outlook-logo root-logo"><Folder size={24} /></div>
        <div>
          <h3>{root.name}</h3>
          <span title={root.root_path}>{root.root_path}</span>
        </div>
      </div>
      <div className="button-row">
        <button className="ghost-action compact" type="button" aria-label={`Edit selected watched path ${root.name}`} title="Edit this watched path" onClick={() => onEdit(root)}>
          <Wrench size={15} /> Edit
        </button>
        <button className="ghost-action compact" type="button" aria-label={`Run selected root backfill for ${root.name}`} title="Process deferred jobs for this watched path" onClick={() => onBackfill(root)}>
          <Play size={15} /> Run backfill
        </button>
        <button className="ghost-action compact danger-action" type="button" aria-label={`Delete selected watched path ${root.name}`} title="Delete this watched path from the Flux index" onClick={() => onDelete(root)}>
          <Trash2 size={15} /> Delete
        </button>
      </div>
      {root.job_counts?.pending ? (
        <p className="warning-note">
          {root.job_counts.pending} pending deferred job{root.job_counts.pending === 1 ? "" : "s"}.
          The background worker will process these automatically; use Run backfill only for an immediate one-shot retry.
        </p>
      ) : null}
      <div className="schedule-row">
        <Stat label="Last crawl" value={String(latest.status ?? "-")} />
        <Stat label="Files seen" value={String(latest.files_seen ?? 0)} />
        <Stat label="Changed" value={String(latest.files_changed ?? 0)} />
      </div>
      <div className="schedule-row">
        <Stat label="Watch heartbeat" value={root.watcher?.heartbeat_age_seconds == null ? "-" : `${root.watcher.heartbeat_age_seconds}s ago`} />
        <Stat label="Last event" value={formatDate(root.watcher?.last_event_at)} />
        <Stat label="Deleted" value={String(root.asset_counts?.deleted ?? 0)} />
      </div>
      <label>Include globs</label>
      <div className="folder-box">{(root.include_globs ?? []).join("\n") || "All files allowed by ignore policy."}</div>
      <label>Exclude globs</label>
      <div className="folder-box">{(root.exclude_globs ?? []).join("\n") || "No custom excludes."}</div>
      <label>Effective include globs</label>
      <div className="folder-box">{(root.effective_globs?.include_globs ?? root.include_globs ?? []).join("\n") || "All files allowed by effective policy."}</div>
      <label>Effective exclude globs</label>
      <div className="folder-box">{(root.effective_globs?.exclude_globs ?? root.exclude_globs ?? []).join("\n") || "No effective excludes."}</div>
      <div className="recent-grid">
        <div>
          <strong>Recent assets</strong>
          <AssetRows assets={root.recent_assets ?? []} />
        </div>
        <div>
          <strong>Recent jobs</strong>
          <JobRows jobs={root.recent_jobs ?? []} />
        </div>
      </div>
      {(root.recent_errors ?? []).length > 0 && (
        <div className="warning-note">
          {(root.recent_errors ?? []).slice(0, 3).join(" | ")}
        </div>
      )}
    </div>
  );
}

function AssetRows({ assets }: { assets: Array<Record<string, unknown>> }) {
  if (assets.length === 0) return <p className="muted">No assets indexed yet.</p>;
  return (
    <div className="compact-rows">
      {assets.slice(0, 6).map((asset) => (
        <div key={String(asset.path)}>
          <span title={String(asset.path ?? "-")}>{String(asset.path ?? "-")}</span>
          <RootStateBadge state={String(asset.status ?? "indexed")} />
        </div>
      ))}
    </div>
  );
}

function JobRows({ jobs }: { jobs: Array<Record<string, unknown>> }) {
  if (jobs.length === 0) return <p className="muted">No root jobs queued.</p>;
  return (
    <div className="compact-rows">
      {jobs.slice(0, 6).map((job) => (
        <div key={String(job.id)}>
          <span title={String(job.path ?? job.job_type ?? "-")}>{String(job.path ?? job.job_type ?? "-")}</span>
          <RootStateBadge state={String(job.status ?? "pending")} />
        </div>
      ))}
    </div>
  );
}

function RootStateBadge({ state }: { state?: string }) {
  const normalized = state ?? "unknown";
  const label = LOCK_TOLERANT_STATE_LABELS[normalized] ?? normalized;
  const tone = ["watching", "indexed", "completed"].includes(normalized)
    ? "enabled"
    : ["queued", "processing", "crawling", "changed", "watch_enabled", "pending_stable", "retrying_locked"].includes(normalized)
      ? "info"
      : ["blocked", "failed", "stale", "deleted", "blocked_missing_dependency", "blocked_locked"].includes(normalized)
        ? "warning"
        : "";
  return <span className={`state-pill ${tone}`}>{label}</span>;
}

const LOCK_TOLERANT_STATE_LABELS: Record<string, string> = {
  pending_stable: "Pending Stable",
  retrying_locked: "Retrying Locked",
  blocked_locked: "Blocked Locked"
};

function CrawlRootDialog({ root, onClose, onSave }: { root?: RootSummary; onClose: () => void; onSave: (form: CrawlRootForm) => void }) {
  const [form, setForm] = useState<CrawlRootForm>(() => ({
    name: root?.name ?? "",
    root_path: root?.root_path ?? "",
    recursive: root?.recursive ?? true,
    watch_enabled: root?.watch_enabled ?? true,
    initial_crawl: root ? false : true,
    trust_rank: root?.trust_rank ?? 500,
    include_globs: (root?.include_globs ?? []).join("\n"),
    exclude_globs: (root?.exclude_globs ?? []).join("\n"),
    glob_mode: (root?.glob_mode as CrawlRootForm["glob_mode"]) ?? "extend",
    max_inline_bytes: root?.max_inline_bytes ?? 256 * 1024,
    heavy_threshold_bytes: root?.heavy_threshold_bytes ?? 10 * 1024 * 1024
  }));
  const [nameTouched, setNameTouched] = useState(Boolean(root));
  const [error, setError] = useState("");

  function update<K extends keyof CrawlRootForm>(key: K, value: CrawlRootForm[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  function updatePath(value: string) {
    setForm((current) => ({
      ...current,
      root_path: value,
      name: nameTouched ? current.name : slugFromPath(value)
    }));
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!form.root_path.trim()) {
      setError("Root path is required.");
      return;
    }
    if (!form.name.trim()) {
      setError("Root name is required.");
      return;
    }
    if (form.max_inline_bytes <= 0 || form.heavy_threshold_bytes <= 0) {
      setError("Size thresholds must be positive.");
      return;
    }
    setError("");
    onSave(form);
  }

  async function browse() {
    try {
      const result = await sendJson<{ status?: string; path?: string | null; message?: string }>("/api/host/browse-folder", "POST", {});
      if (result.path) {
        updatePath(result.path);
        setError("");
        return;
      }
      setError(result.message ?? "No folder was selected.");
    } catch (error) {
      setError(`Browse unavailable: ${errorMessage(error)}`);
    }
  }

  return (
    <div className="modal-backdrop">
      <form className="modal profile-modal" role="dialog" aria-modal="true" aria-labelledby="crawl-root-dialog-title" onSubmit={submit}>
        <header>
          <h2 id="crawl-root-dialog-title">{root ? "Edit Watched Path" : "Add Watched Path"}</h2>
          <button type="button" aria-label="Close watched path form" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="form-grid">
          <label className="span-2">Root path
            <div className="path-input-row">
              <input value={form.root_path} onChange={(event) => updatePath(event.target.value)} placeholder="E:/Projects/Client RFPs" required />
              <button className="ghost-action compact" type="button" title="Choose a local folder through the Flux host agent" onClick={() => void browse()}>Browse</button>
            </div>
          </label>
          <label>Root name<input value={form.name} onChange={(event) => { setNameTouched(true); update("name", event.target.value); }} required /></label>
          <label>Trust rank<input type="number" min="0" max="1000" value={form.trust_rank} onChange={(event) => update("trust_rank", Number(event.target.value))} /></label>
          <label>Glob inheritance<select value={form.glob_mode} onChange={(event) => update("glob_mode", event.target.value as CrawlRootForm["glob_mode"])}>
            <option value="extend">Extend global defaults</option>
            <option value="inherit">Inherit global defaults only</option>
            <option value="override">Override global defaults</option>
          </select></label>
          <label className="span-2">Include globs<textarea value={form.include_globs} onChange={(event) => update("include_globs", event.target.value)} placeholder="**/*.pdf&#10;**/*.docx" /></label>
          <label className="span-2">Exclude globs<textarea value={form.exclude_globs} onChange={(event) => update("exclude_globs", event.target.value)} placeholder="private/**&#10;node_modules/**" /></label>
          <label>Inline size bytes<input type="number" min="1" value={form.max_inline_bytes} onChange={(event) => update("max_inline_bytes", Number(event.target.value))} /></label>
          <label>Heavy file threshold bytes<input type="number" min="1" value={form.heavy_threshold_bytes} onChange={(event) => update("heavy_threshold_bytes", Number(event.target.value))} /></label>
          <label className="checkbox-label"><input type="checkbox" checked={form.recursive} onChange={(event) => update("recursive", event.target.checked)} /> Recursive</label>
          <label className="checkbox-label"><input type="checkbox" checked={form.watch_enabled} onChange={(event) => update("watch_enabled", event.target.checked)} /> Watch enabled</label>
          {!root && <label className="checkbox-label"><input type="checkbox" checked={form.initial_crawl} onChange={(event) => update("initial_crawl", event.target.checked)} /> Initial crawl now</label>}
          {error && <p className="warning-note span-2">{error}</p>}
        </div>
        <footer>
          <button className="ghost-action compact" type="button" onClick={onClose}>Cancel</button>
          <button className="small-primary" type="submit">Save watched path</button>
        </footer>
      </form>
    </div>
  );
}

function SettingsTab({ settings, restartRows, onEdit, onReset, onApply }: { settings: SettingRow[]; restartRows: SettingRow[]; onEdit: (setting: SettingRow) => void; onReset: (setting: SettingRow) => void; onApply: (component?: string) => void }) {
  const categories = [...new Set(settings.map((setting) => setting.category))].sort();
  return (
    <section className="tab-grid">
      <Panel title="Runtime Settings" action={<button className="small-primary" type="button" onClick={() => onApply()}>Apply pending</button>}>
        <p className="panel-note">Settings are catalog-backed and cross-platform. Environment values override database values, and sensitive values stay masked.</p>
        <div className="settings-table-wrap">
          <table className="settings-table" aria-label="Runtime settings">
            <thead>
              <tr>
                <th>Key</th>
                <th>Value</th>
                <th>Source</th>
                <th>Mode</th>
                <th>Components</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {settings.map((setting) => (
                <tr key={setting.key}>
                  <td>
                    <strong>{setting.key}</strong>
                    <span>{setting.description}</span>
                  </td>
                  <td>{String(setting.value ?? "")}</td>
                  <td>{setting.source}</td>
                  <td><em>{setting.apply_mode}</em></td>
                  <td>{(setting.affected_components ?? []).join(", ") || "-"}</td>
                  <td>
                    <button className="row-button" type="button" aria-label={`Edit ${setting.key}`} disabled={setting.read_only} onClick={() => onEdit(setting)}>
                      <Wrench size={15} /> Edit
                    </button>
                    <button className="row-button" type="button" aria-label={`Reset ${setting.key}`} disabled={setting.read_only || setting.source === "default"} onClick={() => onReset(setting)}>
                      <RotateCcw size={15} /> Reset
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>
      <Panel title={`Restart / Reindex Changes (${restartRows.length})`}>
        <SettingsPreview rows={restartRows} />
      </Panel>
      <Panel title="Categories">
        <div className="category-list">
          {categories.map((category) => <span key={category}>{category}</span>)}
        </div>
      </Panel>
    </section>
  );
}

function RetrievalTab({
  state,
  searchOpen,
  searchResults,
  searchKind,
  searchCurrentOnly,
  searchIncludeSuppressed,
  filterTrace,
  suppression,
  query,
  onClear,
  onSearchKind,
  onSearchCurrentOnly,
  onSearchIncludeSuppressed,
  onErrorDetail,
  onOpenResult
}: {
  state: LoadState;
  searchOpen: boolean;
  searchResults: SearchResult[];
  searchKind: string;
  searchCurrentOnly: boolean;
  searchIncludeSuppressed: boolean;
  filterTrace: RetrievalFilterTrace;
  suppression: RetrievalSuppression;
  query: string;
  onClear: () => void;
  onSearchKind: (value: string) => void;
  onSearchCurrentOnly: (value: boolean) => void;
  onSearchIncludeSuppressed: (value: boolean) => void;
  onErrorDetail: (error: string) => void;
  onOpenResult: (result: SearchResult) => void;
}) {
  const retrieval = state.retrieval.retrieval ?? state.health.retrieval ?? {};
  return (
    <section className="tab-grid">
      <Panel title="Retrieval Console" action={searchOpen ? <button className="ghost-action compact" type="button" onClick={onClear}>Clear results</button> : undefined}>
        <MiniTable rows={[
          ["Episodes", "memory", String(retrieval.episodes ?? 0)],
          ["Sources", "memory", String(retrieval.sources ?? 0)],
          ["Corpus chunks", "assets", String(retrieval.asset_chunks ?? 0)],
          ["Embeddings", "pgvector", String(retrieval.embeddings ?? 0)],
          ["Duplicates", "suppressed", String(state.retrieval.duplicate_assets ?? state.retrieval.duplicate_count ?? 0)]
        ]} />
        <div className="retrieval-filter-grid">
          <label>
            <span>Evidence kind</span>
            <select value={searchKind} onChange={(event) => onSearchKind(event.target.value)}>
              <option value="all">All</option>
              <option value="episode">Episodes</option>
              <option value="file">Files</option>
              <option value="mail">Mail</option>
            </select>
          </label>
          <label className="checkbox-field">
            <input type="checkbox" checked={searchCurrentOnly} onChange={(event) => onSearchCurrentOnly(event.target.checked)} />
            <span>Current evidence only</span>
          </label>
          <label className="checkbox-field">
            <input type="checkbox" checked={searchIncludeSuppressed} onChange={(event) => onSearchIncludeSuppressed(event.target.checked)} />
            <span>Show suppressed diagnostics</span>
          </label>
        </div>
      </Panel>
      <Panel title={searchOpen ? `Search Results: ${query}` : "Search Results"}>
        <RetrievalDebugTrace trace={filterTrace} suppression={suppression} />
        {searchResults.length > 0 ? (
          <div className="search-results">
            {searchResults.map((result, index) => (
              <div className="search-result-card" key={searchResultKey(result, index)}>
                <button className="search-result-open" type="button" onClick={() => onOpenResult(result)}>
                  <span>{searchResultMeta(result)}</span>
                  <strong>{result.title ?? result.id ?? "Untitled result"}</strong>
                  <p>{result.snippet?.text ?? result.excerpt ?? result.summary ?? "No excerpt available."}</p>
                  {result.source_path && <code className="result-path" title={result.source_path}>{result.source_path}</code>}
                  {Boolean(result.related_evidence_count) && (
                    <em>{result.related_evidence_count} related evidence item{result.related_evidence_count === 1 ? "" : "s"}</em>
                  )}
                </button>
                <SearchResultExplanation result={result} />
              </div>
            ))}
          </div>
        ) : (
          <p className="muted">Use the top search box to query episodes, corpus chunks, mail bodies, and attachments.</p>
        )}
      </Panel>
      <Panel title="Consumer Access">
        <p className="panel-note">External tools can query Flux through local REST, MCP tools, or the CLI. Keep the API bound to localhost unless you explicitly configure access controls.</p>
        <div className="consumer-grid">
          <code>GET /api/search?query=customer%20RFP&amp;limit=5</code>
          <code>GET /api/brief?query=customer%20RFP&amp;token_budget=1200</code>
          <code>GET /api/explain?query=customer%20RFP&amp;limit=5</code>
          <code>{'kb.search({"query":"customer RFP","limit":5})'}</code>
          <code>{'kb.explain({"query":"customer RFP","limit":5})'}</code>
          <code>{'kb.brief({"query":"customer RFP","token_budget":1200})'}</code>
          <code>flux-kb search "customer RFP" --limit 5</code>
          <code>flux-kb explain "customer RFP" --limit 5</code>
        </div>
      </Panel>
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
    </section>
  );
}

function SearchResultExplanation({ result }: { result: SearchResult }) {
  const explanation = result.retrieval_explanation;
  if (!explanation) return null;
  const streams = explanation.streams ?? result.streams ?? [];
  const rawScores = explanation.raw_scores ?? result.raw_scores ?? {};
  const scopeLabel = stringFromUnknown(explanation.scope?.label) ?? "unknown";
  const corpusPath = stringFromUnknown(explanation.corpus?.source_path) ?? result.source_path;
  const lifecyclePenalties = lifecyclePenaltyText(explanation.lifecycle);
  const suppression = explanation.suppression ?? {};
  const exactDuplicateCount = suppressionObjectCount(suppression, "exact_duplicates");
  const versionFamilyCount = suppressionObjectCount(suppression, "version_family");
  return (
    <details className="result-explanation">
      <summary>Why this result</summary>
      <div className="result-explanation-grid">
        <span>Streams</span>
        <strong>{streams.length ? streams.map(prettyStreamName).join(", ") : "-"}</strong>
        <span>Scope</span>
        <strong>{scopeLabel}</strong>
        <span>Score</span>
        <strong>{typeof explanation.score === "number" ? explanation.score.toFixed(3) : typeof result.score === "number" ? result.score.toFixed(3) : "-"}</strong>
        {corpusPath && (
          <>
            <span>Source</span>
            <code title={corpusPath}>{corpusPath}</code>
          </>
        )}
        {lifecyclePenalties && (
          <>
            <span>Lifecycle penalties</span>
            <strong>{lifecyclePenalties}</strong>
          </>
        )}
        {exactDuplicateCount > 0 && (
          <>
            <span>Exact duplicates</span>
            <strong>{exactDuplicateCount} suppressed</strong>
          </>
        )}
        {versionFamilyCount > 0 && (
          <>
            <span>Same document versions</span>
            <strong>{versionFamilyCount} suppressed</strong>
          </>
        )}
      </div>
      {Object.keys(rawScores).length > 0 && (
        <div className="raw-score-row">
          {Object.entries(rawScores).map(([stream, score]) => (
            <span key={stream}>{prettyStreamName(stream)} {Number(score).toFixed(3)}</span>
          ))}
        </div>
      )}
    </details>
  );
}

function RetrievalDebugTrace({ trace, suppression }: { trace: RetrievalFilterTrace; suppression: RetrievalSuppression }) {
  const excluded = trace.excluded ?? [];
  const exactDuplicateCount = suppressionCount(suppression.exact_duplicates);
  const versionFamilyCount = suppressionCount(suppression.version_families);
  if (excluded.length === 0 && exactDuplicateCount === 0 && versionFamilyCount === 0) return null;
  return (
    <div className="retrieval-debug-trace">
      {excluded.length > 0 && (
        <section>
          <strong>Filtered out {excluded.length} candidates</strong>
          <ul>
            {excluded.slice(0, 5).map((item, index) => (
              <li key={`${item.id ?? item.title ?? "excluded"}-${index}`}>
                {item.title ?? item.id ?? "Untitled"} - {formatRetrievalReason(item.reason)}
              </li>
            ))}
          </ul>
        </section>
      )}
      {(exactDuplicateCount > 0 || versionFamilyCount > 0) && (
        <section>
          <strong>Suppressed evidence</strong>
          {exactDuplicateCount > 0 && <span>Exact duplicates: {exactDuplicateCount}</span>}
          {versionFamilyCount > 0 && <span>Version families: {versionFamilyCount}</span>}
        </section>
      )}
    </div>
  );
}

function ReviewTab({
  payload,
  capture,
  captureAudit,
  retentionPolicies,
  retentionQuality,
  graph,
  selectedClaim,
  loading,
  reviewFilter,
  stateFilter,
  onReviewFilter,
  onStateFilter,
  onSelectClaim,
  onTransition,
  onCaptureDecision,
  onRetentionPolicySave,
  onRefresh
}: {
  payload: ClaimReviewPayload;
  capture: CaptureReviewPayload;
  captureAudit: AuditEvent[];
  retentionPolicies: RetentionPolicyPayload;
  retentionQuality: RetentionQualityPayload;
  graph: GraphPayload;
  selectedClaim?: ClaimReviewClaim;
  loading: boolean;
  reviewFilter: ClaimReviewFilter;
  stateFilter: string;
  onReviewFilter: (value: ClaimReviewFilter) => void;
  onStateFilter: (value: string) => void;
  onSelectClaim: (claim: ClaimReviewClaim) => void;
  onTransition: (claim: ClaimReviewClaim, transition: string) => void;
  onCaptureDecision: (job: CaptureReviewJob, decision: "approve" | "reject") => void;
  onRetentionPolicySave: (policy: RetentionPolicy, update: RetentionPolicyUpdate) => void;
  onRefresh: () => void;
}) {
  const claims = payload.claims ?? [];
  const counts = payload.counts ?? {};
  return (
    <section className="tab-grid review-grid">
      <Panel title="Claim Review" action={<button className="small-primary" type="button" onClick={onRefresh}><RefreshCcw size={15} /> Refresh</button>}>
        <div className="review-summary">
          <Stat label="Total" value={String(counts.total ?? 0)} />
          <Stat label="Current" value={String(counts.current ?? 0)} />
          <Stat label="Needs Review" value={String(counts.needs_review ?? 0)} />
          <Stat label="Retention" value={String(counts.retention_action ?? 0)} />
        </div>
        <div className="review-inline-status">{counts.needs_review ?? 0} needs review</div>
        <div className="review-controls">
          <label>Review filter
            <select value={reviewFilter} onChange={(event) => onReviewFilter(event.target.value as ClaimReviewFilter)}>
              <option value="needs_review">Needs review</option>
              <option value="current">Current</option>
              <option value="all">All claims</option>
            </select>
          </label>
          <label>Lifecycle state
            <select value={stateFilter} onChange={(event) => onStateFilter(event.target.value)}>
              <option value="">Any state</option>
              <option value="active">Active</option>
              <option value="confirmed">Confirmed</option>
              <option value="reinforced">Reinforced</option>
              <option value="stale">Stale</option>
              <option value="contradicted">Contradicted</option>
              <option value="superseded">Superseded</option>
              <option value="retired">Retired</option>
            </select>
          </label>
          {loading && <span className="muted">Loading review queue...</span>}
        </div>
        <ClaimReviewTable claims={claims} selectedClaim={selectedClaim} onSelectClaim={onSelectClaim} />
      </Panel>
      <Panel title="Selected Claim">
        <SelectedClaimPanel claim={selectedClaim} onTransition={onTransition} />
      </Panel>
      <Panel title="Entity Graph">
        <GraphEdgeTable graph={graph} />
      </Panel>
      <Panel title="Retention Tuning">
        <RetentionPolicyTable policies={retentionPolicies.policies ?? []} onSave={onRetentionPolicySave} />
      </Panel>
      <Panel title="Memory Quality">
        <RetentionQualityReport payload={retentionQuality} />
      </Panel>
      <Panel title="Capture Review Queue">
        <CaptureReviewTable jobs={capture.jobs ?? []} onDecision={onCaptureDecision} />
      </Panel>
      <Panel title="Capture Review Decisions">
        <CaptureReviewAuditList events={captureAudit} />
      </Panel>
    </section>
  );
}

function ClaimReviewTable({
  claims,
  selectedClaim,
  onSelectClaim
}: {
  claims: ClaimReviewClaim[];
  selectedClaim?: ClaimReviewClaim;
  onSelectClaim: (claim: ClaimReviewClaim) => void;
}) {
  if (claims.length === 0) return <p className="muted padded">No claims match this review filter.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table" aria-label="Claim review queue">
        <thead>
          <tr>
            <th>Subject</th>
            <th>Predicate</th>
            <th>Object</th>
            <th>State</th>
            <th>Reasons</th>
            <th>Score</th>
            <th>Updated</th>
          </tr>
        </thead>
        <tbody>
          {claims.map((claim) => (
            <tr key={claim.id} className={claim.id === selectedClaim?.id ? "selected" : ""} onClick={() => onSelectClaim(claim)}>
              <td>
                <button className="row-select" type="button" aria-label={`Select claim ${claim.id}`} onClick={(event) => { event.stopPropagation(); onSelectClaim(claim); }}>
                  {claim.id === selectedClaim?.id ? <Play size={14} fill="currentColor" /> : <Square size={12} />}
                </button>
                <div>
                  <strong>{claim.subject?.name ?? claim.subject_entity_id ?? "-"}</strong>
                  <span>{claim.subject?.type ?? "entity"}</span>
                </div>
              </td>
              <td>{claim.predicate ?? "-"}</td>
              <td className="claim-object" title={claim.object_text ?? ""}>{claim.object_text ?? "-"}</td>
              <td><RootStateBadge state={claim.lifecycle_state ?? "unknown"} /></td>
              <td>{(claim.review_reasons ?? []).join(", ") || "-"}</td>
              <td>{typeof claim.lifecycle?.score === "number" ? claim.lifecycle.score.toFixed(3) : "-"}</td>
              <td>{formatDate(claim.updated_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SelectedClaimPanel({ claim, onTransition }: { claim?: ClaimReviewClaim; onTransition: (claim: ClaimReviewClaim, transition: string) => void }) {
  if (!claim) return <p className="muted padded">Select a claim to inspect lifecycle details.</p>;
  const actions: Array<[string, string]> = [
    ["confirm", "Confirm"],
    ["reinforce", "Reinforce"],
    ["stale", "Mark stale"],
    ["deprioritize", "Deprioritize"],
    ["retire", "Retire"]
  ];
  return (
    <div className="claim-inspector">
      <div className="detail-grid">
        <DetailField label="Claim id" value={claim.id} />
        <DetailField label="Subject entity" value={claim.subject_entity_id} />
        <DetailField label="State" value={claim.lifecycle_state} />
        <DetailField label="Retention" value={claim.retention_action} />
      </div>
      <section className="result-section">
        <h3>Statement</h3>
        <p className="claim-statement">{claim.subject?.name ?? "Entity"} {claim.predicate ?? "relates to"} {claim.object_text ?? "-"}</p>
      </section>
      <section className="result-section">
        <h3>Review Reasons</h3>
        <div className="reason-row">
          {(claim.review_reasons ?? []).length > 0 ? claim.review_reasons?.map((reason) => <span key={reason}>{reason}</span>) : <span>current</span>}
        </div>
      </section>
      <div className="file-action-row">
        {actions.map(([transition, label]) => (
          <button
            className="ghost-action compact"
            key={transition}
            type="button"
            aria-label={`${label === "Confirm" ? "Confirm" : label} claim ${claim.id}`}
            onClick={() => onTransition(claim, transition)}
          >
            {label}
          </button>
        ))}
      </div>
    </div>
  );
}

function GraphEdgeTable({ graph }: { graph: GraphPayload }) {
  const edges = graph.edges ?? [];
  if (edges.length === 0) return <p className="muted padded">No graph edges are available for the selected claim entity.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table" aria-label="Entity graph edges">
        <thead>
          <tr>
            <th>From</th>
            <th>Relation</th>
            <th>To</th>
            <th>Depth</th>
            <th>Confidence</th>
          </tr>
        </thead>
        <tbody>
          {edges.map((edge, index) => (
            <tr key={edge.relation_id ?? `${edge.relation_type}-${index}`}>
              <td>{entityLabel(edge.from_entity)}</td>
              <td>{edge.relation_type ?? "-"}</td>
              <td>{entityLabel(edge.to_entity)}</td>
              <td>{edge.depth ?? "-"}</td>
              <td>{typeof edge.confidence === "number" ? edge.confidence.toFixed(2) : "-"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RetentionPolicyTable({ policies, onSave }: { policies: RetentionPolicy[]; onSave: (policy: RetentionPolicy, update: RetentionPolicyUpdate) => void }) {
  if (policies.length === 0) return <p className="muted padded">No retention policies are available.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table retention-table" aria-label="Retention policies">
        <thead>
          <tr>
            <th>Class</th>
            <th>Half-life</th>
            <th>Minimum Confidence</th>
            <th>Action</th>
            <th>Reason</th>
            <th>Updated By</th>
            <th>Save</th>
          </tr>
        </thead>
        <tbody>
          {policies.map((policy) => <RetentionPolicyRow key={policy.memory_class} policy={policy} onSave={onSave} />)}
        </tbody>
      </table>
    </div>
  );
}

function RetentionPolicyRow({ policy, onSave }: { policy: RetentionPolicy; onSave: (policy: RetentionPolicy, update: RetentionPolicyUpdate) => void }) {
  const classLabel = titleCase(policy.memory_class);
  const [halfLife, setHalfLife] = useState(String(policy.half_life_days ?? ""));
  const [minConfidence, setMinConfidence] = useState(String(policy.min_confidence ?? ""));
  const [action, setAction] = useState(policy.action ?? "review");
  const [reason, setReason] = useState("dashboard review");

  useEffect(() => {
    setHalfLife(String(policy.half_life_days ?? ""));
    setMinConfidence(String(policy.min_confidence ?? ""));
    setAction(policy.action ?? "review");
    setReason("dashboard review");
  }, [policy.half_life_days, policy.min_confidence, policy.action]);

  const update: RetentionPolicyUpdate = {
    half_life_days: Number.parseInt(halfLife, 10),
    min_confidence: Number.parseFloat(minConfidence),
    action,
    reason: reason.trim()
  };
  const saveDisabled = !Number.isFinite(update.half_life_days) || update.half_life_days <= 0 || !Number.isFinite(update.min_confidence) || update.min_confidence < 0 || update.min_confidence > 1 || !update.reason;

  return (
    <tr>
      <td><strong>{policy.memory_class}</strong></td>
      <td>
        <label className="compact-field">
          <span>{classLabel} half-life days</span>
          <input type="number" min="1" value={halfLife} onChange={(event) => setHalfLife(event.target.value)} />
        </label>
      </td>
      <td>
        <label className="compact-field">
          <span>{classLabel} minimum confidence</span>
          <input type="number" min="0" max="1" step="0.01" value={minConfidence} onChange={(event) => setMinConfidence(event.target.value)} />
        </label>
      </td>
      <td>
        <label className="compact-field">
          <span>{classLabel} retention action</span>
          <select value={action} onChange={(event) => setAction(event.target.value)}>
            <option value="review">review</option>
            <option value="deprioritize">deprioritize</option>
            <option value="retire">retire</option>
          </select>
        </label>
      </td>
      <td>
        <label className="compact-field">
          <span>{classLabel} retention reason</span>
          <input value={reason} onChange={(event) => setReason(event.target.value)} />
        </label>
      </td>
      <td>{policy.updated_by ?? "-"}</td>
      <td>
        <button
          className="ghost-action compact"
          type="button"
          aria-label={`Save ${policy.memory_class} retention policy`}
          disabled={saveDisabled}
          onClick={() => onSave(policy, update)}
        >
          Save
        </button>
      </td>
    </tr>
  );
}

function RetentionQualityReport({ payload }: { payload: RetentionQualityPayload }) {
  const summary = payload.summary ?? {};
  const candidates = payload.candidates ?? [];
  return (
    <>
      <div className="review-summary">
        <Stat label="Total" value={String(summary.total ?? 0)} />
        <Stat label="Needs Review" value={String(summary.needs_review ?? 0)} />
        <Stat label="Review" value={String(summary.by_bucket?.review ?? 0)} />
        <Stat label="Deprioritize" value={String(summary.by_bucket?.deprioritize ?? 0)} />
      </div>
      <div className="review-inline-status">{summary.needs_review ?? 0} need attention</div>
      {candidates.length === 0 ? (
        <p className="muted padded">No memory quality candidates.</p>
      ) : (
        <div className="review-table-wrap">
          <table className="profile-table review-table" aria-label="Memory quality candidates">
            <thead>
              <tr>
                <th>Class</th>
                <th>Label</th>
                <th>Reason</th>
                <th>Bucket</th>
                <th>Confidence</th>
                <th>State</th>
                <th>Updated</th>
              </tr>
            </thead>
            <tbody>
              {candidates.map((candidate) => (
                <tr key={`${candidate.memory_class}-${candidate.id}`}>
                  <td>{candidate.memory_class ?? "-"}</td>
                  <td className="claim-object" title={candidate.label ?? ""}>{candidate.label ?? "-"}</td>
                  <td>{candidate.reason ?? "-"}</td>
                  <td><RootStateBadge state={candidate.quality_bucket ?? "healthy"} /></td>
                  <td>{typeof candidate.confidence === "number" ? candidate.confidence.toFixed(2) : "-"}</td>
                  <td>{candidate.lifecycle_state ?? candidate.extraction_status ?? candidate.retention_action ?? "-"}</td>
                  <td>{formatDate(candidate.updated_at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function CaptureReviewTable({
  jobs,
  onDecision
}: {
  jobs: CaptureReviewJob[];
  onDecision: (job: CaptureReviewJob, decision: "approve" | "reject") => void;
}) {
  if (jobs.length === 0) return <p className="muted padded">No pending capture review jobs.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table" aria-label="Capture review queue">
        <thead>
          <tr>
            <th>Job</th>
            <th>Type</th>
            <th>Status</th>
            <th>Target</th>
            <th>Updated</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((job, index) => (
            <tr key={job.id ?? `capture-review-${index}`}>
              <td><strong>{job.id ?? "-"}</strong></td>
              <td>{jobTypeLabel(job.job_type)}</td>
              <td><JobStatusBadge status={job.status ?? "pending_review"} /></td>
              <td className="claim-object" title={captureReviewTarget(job)}>{captureReviewTarget(job)}</td>
              <td>{formatDate(job.updated_at)}</td>
              <td>
                <div className="row-actions">
                  <button
                    type="button"
                    aria-label={`Approve capture job ${job.id ?? index + 1}`}
                    title={`Approve ${job.id ?? "capture job"}`}
                    onClick={() => onDecision(job, "approve")}
                  >
                    <CheckCircle2 size={15} />
                  </button>
                  <button
                    type="button"
                    aria-label={`Reject capture job ${job.id ?? index + 1}`}
                    title={`Reject ${job.id ?? "capture job"}`}
                    onClick={() => onDecision(job, "reject")}
                  >
                    <X size={15} />
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CaptureReviewAuditList({ events }: { events: AuditEvent[] }) {
  if (events.length === 0) return <p className="muted padded">No capture review decisions recorded yet.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table" aria-label="Capture review decisions">
        <thead>
          <tr>
            <th>Event</th>
            <th>Job</th>
            <th>Rationale</th>
            <th>Actor</th>
            <th>When</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event, index) => (
            <tr key={event.id ?? `${event.event_type}-${index}`}>
              <td>{event.event_type ?? "-"}</td>
              <td>{event.target_id ?? "-"}</td>
              <td className="claim-object" title={captureReviewAuditReason(event)}>{captureReviewAuditReason(event)}</td>
              <td>{event.actor ?? "-"}</td>
              <td>{formatDate(event.created_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CaptureDecisionDialog({
  decision,
  reason,
  onReason,
  onClose,
  onSave
}: {
  decision: CaptureDecisionState;
  reason: string;
  onReason: (value: string) => void;
  onClose: () => void;
  onSave: () => void;
}) {
  const approve = decision.decision === "approve";
  const title = `${approve ? "Approve" : "Reject"} capture review`;
  return (
    <div className="modal-backdrop top-layer">
      <form
        className="modal capture-review-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="capture-review-dialog-title"
        onSubmit={(event) => {
          event.preventDefault();
          if (reason.trim()) onSave();
        }}
      >
        <header>
          <h2 id="capture-review-dialog-title">{title}</h2>
          <button type="button" aria-label="Close capture review decision" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="setting-editor">
          <strong>{decision.job.id ?? "Capture job"}</strong>
          <p className="muted">{captureReviewTarget(decision.job)}</p>
          <label>
            Rationale
            <textarea value={reason} maxLength={1000} onChange={(event) => onReason(event.target.value)} />
          </label>
        </div>
        <footer>
          <button className="ghost-action compact" type="button" onClick={onClose}>Cancel</button>
          <button className="small-primary" type="submit" disabled={!reason.trim()}>{approve ? "Approve" : "Reject"}</button>
        </footer>
      </form>
    </div>
  );
}

function entityLabel(entity?: { type?: string; name?: string }) {
  if (!entity) return "-";
  return [entity.type, entity.name].filter(Boolean).join(": ");
}

function captureReviewTarget(job: CaptureReviewJob) {
  const payload = job.payload ?? {};
  return stringFromUnknown(payload.path)
    ?? stringFromUnknown(payload.source)
    ?? stringFromUnknown(payload.source_dir)
    ?? stringFromUnknown(payload.file)
    ?? "-";
}

function captureReviewAuditEvents(payload: AuditPayload): AuditEvent[] {
  const events = Array.isArray(payload) ? payload : payload.events ?? [];
  return events.filter((event) => (event.event_type ?? "").startsWith("capture.review_")).slice(0, 8);
}

function captureReviewAuditReason(event: AuditEvent) {
  return stringFromUnknown(event.details?.rationale)
    ?? stringFromUnknown(event.details?.message)
    ?? "-";
}

function ResultDetailDialog({
  detail,
  loading,
  onClose,
  onCopyPath,
  onFileAction
}: {
  detail: ResultDetail | null;
  loading: boolean;
  onClose: () => void;
  onCopyPath: (detail: ResultDetail) => void;
  onFileAction: (detail: ResultDetail, action: "open" | "reveal") => void;
}) {
  const title = detail?.title ?? "Result detail";
  const logicalKind = detail?.logical_kind ?? detail?.kind ?? "result";
  return (
    <div className="modal-backdrop top-layer">
      <div className="modal result-detail-modal" role="dialog" aria-modal="true" aria-labelledby="result-detail-title">
        <header>
          <div className="result-detail-heading">
            <span>{logicalKind}</span>
            <h2 id="result-detail-title">{title}</h2>
          </div>
          <button type="button" aria-label="Close result detail" onClick={onClose}><X size={18} /></button>
        </header>
        {loading && !detail ? (
          <div className="result-detail-body">
            <p className="muted">Loading result detail...</p>
          </div>
        ) : detail?.logical_kind === "mail" ? (
          <MailResultDetail detail={detail} />
        ) : (
          <FileResultDetail detail={detail ?? {}} onCopyPath={onCopyPath} onFileAction={onFileAction} />
        )}
        <footer>
          <button className="small-primary" type="button" onClick={onClose}>Close</button>
        </footer>
      </div>
    </div>
  );
}

function MailResultDetail({ detail }: { detail: ResultDetail }) {
  const mail = detail.mail ?? {};
  return (
    <div className="result-detail-body">
      <div className="detail-grid">
        <DetailField label="From" value={mail.sender} />
        <DetailField label="To" value={mail.recipients?.join(", ")} />
        <DetailField label="Received" value={mail.received_at ?? mail.sent_at} />
        <DetailField label="Profile" value={mail.profile_name} />
        <DetailField label="Folder" value={mail.source_folder} />
        <DetailField label="Export state" value={mail.post_process_state} />
      </div>
      <section className="result-section">
        <h3>Body</h3>
        {detail.body?.html_sanitized ? (
          <div className="mail-body" dangerouslySetInnerHTML={{ __html: detail.body.html_sanitized }} />
        ) : detail.body?.text ? (
          <pre className="result-preview">{detail.body.text}</pre>
        ) : (
          <p className="muted">No readable body is available.</p>
        )}
      </section>
      <EvidenceList title="Attachments" empty="No attachments." items={detail.attachments ?? []} icon={<Paperclip size={15} />} />
      <EvidenceList title="Related Evidence" empty="No related implementation files." items={detail.related_evidence ?? []} icon={<Archive size={15} />} />
      <EvidenceList title="Provenance" empty="No provenance rows." items={detail.provenance ?? []} icon={<FileText size={15} />} />
    </div>
  );
}

function FileResultDetail({
  detail,
  onCopyPath,
  onFileAction
}: {
  detail: ResultDetail;
  onCopyPath: (detail: ResultDetail) => void;
  onFileAction: (detail: ResultDetail, action: "open" | "reveal") => void;
}) {
  const copyAvailable = actionAvailable(detail, "copy_path");
  const openAvailable = actionAvailable(detail, "open") && Boolean(detail.asset_id);
  const revealAvailable = actionAvailable(detail, "reveal") && Boolean(detail.asset_id);
  const disabledReasons = uniqueActionReasons(detail);
  const canonicalPath = resultActionPath(detail, "copy_path") ?? stringFromUnknown(detail.metadata?.canonical_path) ?? stringFromUnknown(detail.metadata?.path);
  return (
    <div className="result-detail-body">
      <div className="detail-grid">
        <DetailField label="Path" value={canonicalPath} />
        <DetailField label="Status" value={stringFromUnknown(detail.metadata?.status)} />
        <DetailField label="Asset id" value={detail.asset_id} />
        <DetailField label="Chunk id" value={detail.chunk_id} />
      </div>
      <div className="file-action-row">
        <button className="ghost-action compact" type="button" disabled={!copyAvailable} title={actionDisabledReason(detail, "copy_path")} onClick={() => onCopyPath(detail)}>
          <Copy size={15} /> Copy path
        </button>
        <button className="ghost-action compact" type="button" disabled={!openAvailable} title={actionDisabledReason(detail, "open")} onClick={() => onFileAction(detail, "open")}>
          <ExternalLink size={15} /> Open with default app
        </button>
        <button className="ghost-action compact" type="button" disabled={!revealAvailable} title={actionDisabledReason(detail, "reveal")} onClick={() => onFileAction(detail, "reveal")}>
          <FolderOpen size={15} /> Reveal in folder
        </button>
      </div>
      {disabledReasons.map((reason) => <p className="action-disabled-reason" key={reason}>{reason}</p>)}
      <section className="result-section">
        <h3>Preview</h3>
        {detail.preview?.available && detail.preview.text ? (
          <pre className="result-preview">{detail.preview.text}</pre>
        ) : (
          <p className="muted">No extracted text is available.</p>
        )}
      </section>
      <EvidenceList title="Related Evidence" empty="No related evidence." items={detail.related_evidence ?? []} icon={<Archive size={15} />} />
      <EvidenceList title="Provenance" empty="No provenance rows." items={detail.provenance ?? []} icon={<FileText size={15} />} />
    </div>
  );
}

function DetailField({ label, value }: { label: string; value?: string | null }) {
  return (
    <div>
      <span>{label}</span>
      <strong>{value || "-"}</strong>
    </div>
  );
}

function EvidenceList({ title, empty, items, icon }: { title: string; empty: string; items: EvidenceItem[]; icon: ReactNode }) {
  return (
    <section className="result-section">
      <h3>{title}</h3>
      {items.length > 0 ? (
        <div className="evidence-list">
          {items.map((item, index) => (
            <div key={`${item.path ?? item.title ?? "evidence"}-${index}`}>
              {icon}
              <div>
                <strong>{item.title ?? item.path ?? "Evidence"}</strong>
                {item.path && <code>{item.path}</code>}
                {(item.relationship || item.status || item.kind) && (
                  <span>{[item.relationship, item.status, item.kind].filter(Boolean).join(" - ")}</span>
                )}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="muted">{empty}</p>
      )}
    </section>
  );
}

function searchResultMeta(result: SearchResult): string {
  const kind = result.logical_kind ?? result.kind ?? "result";
  if (result.streams?.length) {
    return `${kind} - ${result.streams.map(prettyStreamName).join(", ")}`;
  }
  if (typeof result.score === "number") {
    return `${kind} - score ${result.score.toFixed(3)}`;
  }
  return kind;
}

function searchResultKey(result: SearchResult, index: number): string {
  return result.detail_ref?.id ?? result.id ?? result.asset_id ?? result.mail_message_id ?? `${result.title ?? "result"}-${index}`;
}

function buildDashboardRetrievalFilters(kind: string, currentOnly: boolean, includeSuppressed: boolean): RetrievalFilters {
  return {
    logical_kinds: kind === "all" ? [] : [kind],
    current_only: currentOnly,
    lifecycle_states: [],
    include_suppressed: includeSuppressed
  };
}

function lifecyclePenaltyText(value: unknown): string {
  if (!value || typeof value !== "object") return "";
  const explanation = (value as Record<string, unknown>).explanation;
  if (!explanation || typeof explanation !== "object") return "";
  const penalties = (explanation as Record<string, unknown>).penalties;
  if (!penalties || typeof penalties !== "object") return "";
  return Object.entries(penalties as Record<string, unknown>)
    .map(([key, penalty]) => {
      const number = numberFromUnknown(penalty);
      return number === undefined ? "" : `${key} ${number.toFixed(3)}`;
    })
    .filter(Boolean)
    .join(", ");
}

function suppressionObjectCount(suppression: Record<string, unknown>, key: string): number {
  const item = suppression[key];
  if (!item || typeof item !== "object" || Array.isArray(item)) return 0;
  return numberFromUnknown((item as Record<string, unknown>).suppressed_count) ?? 0;
}

function suppressionCount(items?: Array<Record<string, unknown>>): number {
  return (items ?? []).reduce((total, item) => total + (numberFromUnknown(item.suppressed_count) ?? 0), 0);
}

function formatRetrievalReason(value?: string): string {
  return String(value || "filtered").replace(/[_-]+/g, " ");
}

function prettyStreamName(value: string): string {
  return titleCase(value.replace(/^corpus_/, "corpus ").replace(/_/g, " "));
}

function JobsTab({ state, onRefresh }: { state: LoadState; onRefresh: () => void }) {
  const jobs = state.jobs.jobs ?? [];
  const familyRows = (state.health.acceleration?.worker_families ?? []).slice(0, 8).map((family) => [
    family.family ?? "general",
    `${family.pending ?? 0} pending / ${family.running ?? 0} running`,
    family.backpressure ? humanizeIdentifier(family.backpressure) : `${family.blocked ?? 0} blocked / ${family.failed ?? 0} failed`
  ] as [string, string, string]);
  return (
    <section className="tab-grid">
      <Panel title="Job Queue" action={<button className="small-primary" type="button" onClick={onRefresh}><RefreshCcw size={15} /> Refresh</button>}>
        <JobQueueTable jobs={jobs} />
      </Panel>
      <Panel title="Worker Family Status">
        {familyRows.length > 0 ? <MiniTable rows={familyRows} /> : <p className="muted">No worker-family status yet.</p>}
      </Panel>
      <BacklogPanel health={state.health} blockedJobs={state.health.jobs?.blocked ?? 0} />
    </section>
  );
}

function JobQueueTable({ jobs }: { jobs: Array<Record<string, unknown>> }) {
  const [expandedJobId, setExpandedJobId] = useState<string | null>(null);
  if (jobs.length === 0) return <p className="muted">No queued extraction jobs.</p>;
  return (
    <div className="job-table-wrap">
      <table className="profile-table job-table" aria-label="Extraction jobs">
        <thead>
          <tr>
            <th>Status</th>
            <th>Job type</th>
            <th>Target</th>
            <th>Root</th>
            <th>Attempts</th>
            <th>Updated</th>
            <th>Last error</th>
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((job, index) => {
            const id = jobId(job, index);
            const payload = jobPayload(job);
            const target = jobTarget(job, payload);
            const status = stringFromUnknown(job.status) ?? "unknown";
            const expanded = expandedJobId === id;
            return (
              <Fragment key={id}>
                <tr>
                  <td><JobStatusBadge status={status} /></td>
                  <td><strong>{jobTypeLabel(stringFromUnknown(job.job_type))}</strong></td>
                  <td className="job-target" title={target.path}>{target.path}</td>
                  <td>{target.root}</td>
                  <td>{numberFromUnknown(job.attempts) ?? 0}</td>
                  <td>{formatDate(stringFromUnknown(job.updated_at))}</td>
                  <td className="job-error" title={stringFromUnknown(job.last_error) ?? ""}>{stringFromUnknown(job.last_error) ?? "-"}</td>
                  <td>
                    <button
                      className="row-button"
                      type="button"
                      aria-expanded={expanded}
                      aria-label={`${expanded ? "Hide" : "Show"} details for job ${id}`}
                      onClick={() => setExpandedJobId(expanded ? null : id)}
                    >
                      <ChevronDown className={expanded ? "chevron-open" : ""} size={15} /> Details
                    </button>
                  </td>
                </tr>
                {expanded ? <JobDetailRow job={job} payload={payload} target={target} id={id} status={status} /> : null}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function JobDetailRow({
  job,
  payload,
  target,
  id,
  status
}: {
  job: Record<string, unknown>;
  payload: Record<string, unknown>;
  target: { path: string; root: string };
  id: string;
  status: string;
}) {
  const details = [
    ["Job id", id],
    ["Status", humanizeIdentifier(status)],
    ["Job type", jobTypeLabel(stringFromUnknown(job.job_type))],
    ["Root", target.root],
    ["Path", target.path],
    ["Asset id", stringFromUnknown(payload.asset_id)],
    ["Source id", stringFromUnknown(payload.source_id)],
    ["Attempts", String(numberFromUnknown(job.attempts) ?? 0)],
    ["Created", formatDate(stringFromUnknown(job.created_at))],
    ["Updated", formatDate(stringFromUnknown(job.updated_at))]
  ].filter(([, value]) => value && value !== "-");
  return (
    <tr className="job-detail-row">
      <td colSpan={8}>
        <div className="job-detail-panel">
          <dl className="job-detail-grid">
            {details.map(([label, value]) => (
              <div key={label}>
                <dt>{label}</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
          {stringFromUnknown(job.last_error) ? (
            <div className="job-detail-error">
              <strong>Last error</strong>
              <p>{stringFromUnknown(job.last_error)}</p>
            </div>
          ) : null}
          <details className="job-raw-payload">
            <summary>Raw payload</summary>
            <pre>{JSON.stringify(payload, null, 2)}</pre>
          </details>
        </div>
      </td>
    </tr>
  );
}

function JobStatusBadge({ status }: { status: string }) {
  const tone = ["completed", "metadata_only"].includes(status)
    ? "enabled"
    : ["pending", "running", "processing", "retrying_locked"].includes(status)
      ? "info"
      : status === "failed"
        ? "error"
        : status.startsWith("blocked") || status.startsWith("cancelled")
          ? "warning"
          : "";
  return <span className={`state-pill ${tone}`}>{humanizeIdentifier(status)}</span>;
}

function jobId(job: Record<string, unknown>, index: number) {
  return stringFromUnknown(job.id) ?? `job-${index + 1}`;
}

function jobPayload(job: Record<string, unknown>) {
  return job.payload && typeof job.payload === "object" && !Array.isArray(job.payload) ? job.payload as Record<string, unknown> : {};
}

function jobTarget(job: Record<string, unknown>, payload: Record<string, unknown>) {
  const path = stringFromUnknown(payload.path)
    ?? stringFromUnknown(payload.canonical_path)
    ?? stringFromUnknown(payload.file_path)
    ?? stringFromUnknown(job.path)
    ?? "No path";
  const root = stringFromUnknown(payload.root_name) ?? stringFromUnknown(job.root_name) ?? "-";
  return { path, root };
}

function jobTypeLabel(value?: string) {
  return humanizeIdentifier(value?.replace(/^corpus_/, "") ?? "job");
}

function humanizeIdentifier(value: string) {
  return value
    .replace(/[_-]+/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => formatIdentifierWord(word))
    .join(" ");
}

function formatIdentifierWord(word: string) {
  const upper = word.toUpperCase();
  if (["API", "HTML", "ID", "IMAP", "JSON", "OCR", "PDF", "UID", "URL", "VSS"].includes(upper)) return upper;
  return `${word.charAt(0).toUpperCase()}${word.slice(1).toLowerCase()}`;
}

function HealthStrip({ health, mail, blockedJobs }: { health: HealthPayload; mail: MailStatus; blockedJobs: number }) {
  const runtime = health.runtime ?? {};
  return (
    <section className="health-strip" aria-label="Health summary">
      <MetricCard icon={<Database />} label="PostgreSQL" value={runtime.postgresql?.ok ? "Healthy" : "Blocked"} hint={runtime.postgresql?.message ?? health.database?.message ?? "database"} tone={runtime.postgresql?.ok ? "green" : "red"} />
      <MetricCard icon={<Gauge />} label="Watcher" value={(health.watcher?.active_roots ?? 0) > 0 ? "Running" : "Idle"} hint={`${health.watcher?.active_roots ?? 0} active roots - ${health.watcher?.stale_count ?? 0} stale`} tone="blue" />
      <MetricCard icon={<Mail />} label="Mail Worker" value={(mail.enabled_profiles ?? 0) > 0 ? "Ready" : "Stopped"} hint={`${mail.enabled_profiles ?? 0} profiles enabled`} tone="purple" />
      <MetricCard icon={<BriefcaseBusiness />} label="Blocked Jobs" value={String(blockedJobs)} hint={`${health.jobs?.pending ?? 0} pending - ${health.jobs?.failed ?? 0} failed`} tone="orange" />
    </section>
  );
}

function ProfileTable({
  profiles,
  selectedProfile,
  oauthProfiles,
  hostStatus,
  mailErrors,
  onSelect,
  onSync,
  onEdit
}: {
  profiles: MailProfile[];
  selectedProfile?: MailProfile;
  oauthProfiles: Array<{ profile_name?: string; status?: string }>;
  hostStatus: string;
  mailErrors: number;
  onSelect: (profile: MailProfile) => void;
  onSync: (profile: MailProfile) => void;
  onEdit: (profile: MailProfile) => void;
}) {
  return (
    <table className="profile-table" aria-label="Mail profiles">
      <thead>
        <tr>
          <th>Name</th>
          <th>Source</th>
          <th>Folders / Labels</th>
          <th>Auth</th>
          <th>Schedule</th>
          <th>Last Sync</th>
          <th>Next Sync</th>
          <th>Errors</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        {profiles.map((profile) => (
          <tr key={profile.name} className={profile.name === selectedProfile?.name ? "selected" : ""} onClick={() => onSelect(profile)}>
            <td>
              <button className="row-select" type="button" aria-label={`Select ${profile.name}`} onClick={(event) => { event.stopPropagation(); onSelect(profile); }}>
                {profile.name === selectedProfile?.name ? <Play size={14} fill="currentColor" /> : <Square size={12} />}
              </button>
              <div>
                <strong>{profile.name}</strong>
                <span>{profile.source_type === "outlook_com" ? "Catch-up" : "Primary Capture"}</span>
              </div>
            </td>
            <td><SourceBadge source={profile.source_type} /></td>
            <td className="path-cell" title={(profile.folder_paths ?? []).join(" / ")}>{(profile.folder_paths ?? []).slice(0, 2).join(" / ") || "-"}</td>
            <td><AuthBadge profile={profile} oauthProfiles={oauthProfiles} hostStatus={hostStatus} /></td>
            <td>
              <strong>{profile.sync_enabled ? intervalLabel(profile.sync_interval_seconds) : "Manual"}</strong>
              <span>{profile.sync_enabled ? "Scheduled" : "Sync on demand"}</span>
            </td>
            <td>{formatDate(profile.last_sync_at)}</td>
            <td>{formatDate(profile.next_sync_at)}</td>
            <td className={mailErrors ? "error-text" : ""}>{mailErrors ? `${mailErrors} total` : "0"}</td>
            <td>
              <div className="row-actions">
                <button type="button" aria-label={`Sync ${profile.name}`} title={`Sync ${profile.name} now`} onClick={(event) => { event.stopPropagation(); onSync(profile); }}><RefreshCcw size={15} /></button>
                <button type="button" aria-label={`Edit ${profile.name}`} title={`Edit ${profile.name}`} onClick={(event) => { event.stopPropagation(); onEdit(profile); }}><Wrench size={15} /></button>
                <button type="button" aria-label={`More ${profile.name}`} title={`Select ${profile.name} for details`} onClick={(event) => { event.stopPropagation(); onSelect(profile); }}><MoreVertical size={15} /></button>
              </div>
            </td>
          </tr>
        ))}
        {profiles.length === 0 && (
          <tr>
            <td colSpan={9} className="empty-row">No mail profiles configured yet.</td>
          </tr>
        )}
      </tbody>
    </table>
  );
}

function Inspector({
  profile,
  hostStatus,
  hostCommand,
  oauthProfile,
  runs,
  onSync,
  onEdit,
  onOAuthStart,
  onOAuthPathSave
}: {
  profile?: MailProfile;
  hostStatus: string;
  hostCommand?: string;
  oauthProfile?: { profile_name?: string; status?: string; expires_at?: string | null; has_refresh_token?: boolean };
  runs?: MailSyncRun[];
  onSync: () => void;
  onEdit: () => void;
  onOAuthStart: (clientPath: string) => void;
  onOAuthPathSave: (clientPath: string) => void;
}) {
  const profileClientPath = oauthClientConfigPath(profile);
  const [clientPath, setClientPath] = useState(profileClientPath);
  useEffect(() => {
    setClientPath(profileClientPath);
  }, [profile?.name, profileClientPath]);
  if (!profile) return <div className="empty-inspector"><p className="muted">Select or create a mail profile.</p></div>;
  const oauthStatus = oauthProfile?.status ?? (profile.source_type === "imap" ? "blocked_auth_required" : "");
  return (
    <div className="inspector">
      <div className="profile-head">
        <div className="outlook-logo">{profile.source_type === "outlook_com" ? "O" : "M"}</div>
        <div>
          <h3>Selected Profile</h3>
          <span>{profile.name} - {profile.source_type === "outlook_com" ? "Outlook COM Catch-up" : "Gmail IMAP Capture"}</span>
        </div>
      </div>
      <label>Folder Paths</label>
      <div className="folder-box">{(profile.folder_paths ?? []).join("\n") || "-"}</div>
      <div className="availability">
        <span>Worker Availability</span>
        <strong className={hostStatus === "running" || profile.source_type !== "outlook_com" ? "good-text" : "warn-text"}>{profile.source_type === "outlook_com" ? hostStatusLabel(hostStatus) : "Docker mail worker"}</strong>
      </div>
      {profile.source_type === "outlook_com" && hostStatus !== "running" && <code>{hostCommand ?? "flux-kb outlook-host run"}</code>}
      {profile.source_type === "imap" && (
        <div className="oauth-inline">
          <div className="availability">
            <span>Gmail OAuth</span>
            <strong className={oauthStatus === "configured" ? "good-text" : "warn-text"}>{oauthStatus}</strong>
          </div>
          <label>Private client JSON
            <input value={clientPath} onChange={(event) => setClientPath(event.target.value)} />
          </label>
          <button
            className="ghost-action compact"
            type="button"
            title={`Save the private Google OAuth client JSON path for ${profile.name}`}
            onClick={() => onOAuthPathSave(clientPath)}
          >
            <CheckCircle2 size={15} /> Save OAuth client JSON path
          </button>
          <button
            className="ghost-action compact"
            type="button"
            title={`Start Gmail OAuth for ${profile.name}`}
            onClick={() => onOAuthStart(clientPath)}
          >
            <KeyRound size={15} /> Gmail OAuth for {profile.name}
          </button>
        </div>
      )}
      <div className="schedule-row">
        <Stat label="Schedule" value={profile.sync_enabled ? intervalLabel(profile.sync_interval_seconds) : "Manual"} />
        <Stat label="Window" value={`${profile.sync_window_days ?? 30} days`} />
        <Stat label="Max/run" value={String(profile.max_messages_per_run ?? 200)} />
      </div>
      <div className="schedule-row">
        <Stat label="Last Sync" value={formatDate(profile.last_sync_at)} />
        <Stat label="Next Sync" value={formatDate(profile.next_sync_at)} />
      </div>
      <RunHistory runs={runs ?? []} />
      <div className="button-row">
        <button className="sync-selected" type="button" title={`Sync ${profile.name} now`} onClick={onSync}>
          <RefreshCcw size={17} />
          Sync selected profile
        </button>
        <button className="ghost-action compact" type="button" title={`Edit ${profile.name}`} onClick={onEdit}>
          <Wrench size={15} />
          Edit profile
        </button>
      </div>
    </div>
  );
}

function RunHistory({ runs }: { runs: MailSyncRun[] }) {
  return (
    <div className="run-history">
      <div className="run-history-head">
        <strong>Run History</strong>
        <span>{runs.length ? `${runs.length} recent` : "No runs yet"}</span>
      </div>
      {runs.slice(0, 5).map((run) => {
        const status = runStatusLabel(run.status);
        const warning = ["Backoff", "Blocked Auth Required", "Auth Expired", "Auth Failed", "Failed"].includes(status);
        return (
          <div className="run-row" key={run.id ?? `${run.profile_name}-${run.started_at}`}>
            <div>
              <span className={warning ? "state-pill warning" : "state-pill enabled"}>{status}</span>
              <strong>{run.trigger === "manual" ? "Manual" : "Scheduled"} run</strong>
              <span>{formatDate(run.started_at)} - {formatDate(run.completed_at)}</span>
            </div>
            <div>
              <span>{run.messages_seen ?? 0} seen</span>
              <span>{run.messages_exported ?? 0} exported</span>
              <span>{run.missed_runs ?? 0} missed</span>
            </div>
            {(run.last_error || run.next_attempt_at) && (
              <p>
                {run.last_error && <span>{run.last_error}</span>}
                {run.next_attempt_at && <span>{run.last_error ? " " : ""}Next attempt {formatDate(run.next_attempt_at)}.</span>}
              </p>
            )}
          </div>
        );
      })}
    </div>
  );
}

function ProfileDialog({ profile, onClose, onSave }: { profile?: MailProfile; onClose: () => void; onSave: (form: ProfileForm) => void }) {
  const metadata = profile?.metadata ?? {};
  const [form, setForm] = useState<ProfileForm>(() => ({
    name: profile?.name ?? "gmail-capture",
    source_type: (profile?.source_type === "outlook_com" ? "outlook_com" : "imap"),
    account: profile?.account ?? "me@gmail.com",
    server: profile?.server ?? "imap.gmail.com",
    folder_paths: (profile?.folder_paths ?? ["FluxCapture"]).join("\n"),
    spool_path: profile?.spool_path ?? "private/mail-spool/gmail-capture",
    post_process_policy: profile?.post_process_policy ?? "move_to_processed",
    processed_folder: metadataString(metadata, "processed_folder", "FluxProcessed"),
    trash_folder: metadataString(metadata, "trash_folder", ""),
    destructive_post_process_confirmed: Boolean(metadata.destructive_post_process_confirmed),
    sync_enabled: Boolean(profile?.sync_enabled),
    sync_interval_seconds: profile?.sync_interval_seconds ?? 900,
    sync_window_days: profile?.sync_window_days ?? 30,
    max_messages_per_run: profile?.max_messages_per_run ?? 200
  }));

  function update<K extends keyof ProfileForm>(key: K, value: ProfileForm[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    onSave(form);
  }

  return (
    <div className="modal-backdrop">
      <form className="modal profile-modal" role="dialog" aria-modal="true" aria-labelledby="profile-dialog-title" onSubmit={submit}>
        <header>
          <h2 id="profile-dialog-title">{profile ? "Edit Mail Profile" : "Add Mail Profile"}</h2>
          <button type="button" aria-label="Close profile form" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="form-grid">
          <label>Profile name<input value={form.name} onChange={(event) => update("name", event.target.value)} required /></label>
          <label>Source<select value={form.source_type} onChange={(event) => update("source_type", event.target.value as ProfileForm["source_type"])}>
            <option value="imap">IMAP</option>
            <option value="outlook_com">Outlook COM</option>
          </select></label>
          <label>Account<input value={form.account} onChange={(event) => update("account", event.target.value)} /></label>
          <label>Server<input value={form.server} onChange={(event) => update("server", event.target.value)} /></label>
          <label className="span-2">Folders or labels<textarea value={form.folder_paths} onChange={(event) => update("folder_paths", event.target.value)} required /></label>
          <label className="span-2">Private spool path<input value={form.spool_path} onChange={(event) => update("spool_path", event.target.value)} required /></label>
          <label>Post process<select value={form.post_process_policy} onChange={(event) => update("post_process_policy", event.target.value)}>
            <option value="move_to_processed">Move to processed</option>
            <option value="remove_label">Remove label</option>
            <option value="none">Leave in place</option>
            <option value="trash">Trash/delete</option>
          </select></label>
          <label>Processed folder or label<input value={form.processed_folder} onChange={(event) => update("processed_folder", event.target.value)} /></label>
          <label>Trash folder<input value={form.trash_folder} onChange={(event) => update("trash_folder", event.target.value)} /></label>
          <label>Interval seconds<input type="number" min="60" value={form.sync_interval_seconds} onChange={(event) => update("sync_interval_seconds", Number(event.target.value))} /></label>
          <label>Window days<input type="number" min="1" value={form.sync_window_days} onChange={(event) => update("sync_window_days", Number(event.target.value))} /></label>
          <label>Max messages/run<input type="number" min="1" value={form.max_messages_per_run} onChange={(event) => update("max_messages_per_run", Number(event.target.value))} /></label>
          <label className="checkbox-label"><input type="checkbox" checked={form.sync_enabled} onChange={(event) => update("sync_enabled", event.target.checked)} /> Scheduled sync enabled</label>
          <label className="checkbox-label"><input type="checkbox" checked={form.destructive_post_process_confirmed} onChange={(event) => update("destructive_post_process_confirmed", event.target.checked)} /> Confirm destructive post-process action</label>
        </div>
        <footer>
          <button className="ghost-action compact" type="button" onClick={onClose}>Cancel</button>
          <button className="small-primary" type="submit">Save profile</button>
        </footer>
      </form>
    </div>
  );
}

function SettingDialog({ setting, value, onValue, onClose, onSave, onReset }: { setting: SettingRow; value: string; onValue: (value: string) => void; onClose: () => void; onSave: () => void; onReset: () => void }) {
  return (
    <div className="modal-backdrop">
      <form className="modal" role="dialog" aria-modal="true" aria-labelledby="setting-dialog-title" onSubmit={(event) => { event.preventDefault(); onSave(); }}>
        <header>
          <h2 id="setting-dialog-title">Edit Setting</h2>
          <button type="button" aria-label="Close setting editor" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="setting-editor">
          <strong>{setting.key}</strong>
          <p className="muted">{setting.description}</p>
          <div className="setting-meta">
            <span>{setting.source}</span>
            <span>{setting.apply_mode}</span>
            <span>{setting.sensitive ? "sensitive" : "public"}</span>
          </div>
          <label>Setting value<input value={value} onChange={(event) => onValue(event.target.value)} disabled={setting.read_only} /></label>
          {requiresConfirmation(setting) && <p className="warning-note">This change requires confirmation and queues a runtime action.</p>}
        </div>
        <footer>
          <button className="ghost-action compact" type="button" onClick={onReset} disabled={setting.read_only || setting.source === "default"}>Reset</button>
          <button className="ghost-action compact" type="button" onClick={onClose}>Cancel</button>
          <button className="small-primary" type="submit" disabled={setting.read_only}>Save setting</button>
        </footer>
      </form>
    </div>
  );
}

function ConfirmDialog({ title, body, confirmLabel, onCancel, onConfirm }: { title: string; body: string; confirmLabel: string; onCancel: () => void; onConfirm: () => void }) {
  return (
    <div className="modal-backdrop top-layer">
      <div className="modal confirm-modal" role="dialog" aria-modal="true" aria-labelledby="confirm-dialog-title">
        <header>
          <h2 id="confirm-dialog-title">{title}</h2>
          <button type="button" aria-label="Close confirmation" onClick={onCancel}><X size={18} /></button>
        </header>
        <p>{body}</p>
        <footer>
          <button className="ghost-action compact" type="button" onClick={onCancel}>Cancel</button>
          <button className="small-primary" type="button" onClick={onConfirm}>{confirmLabel}</button>
        </footer>
      </div>
    </div>
  );
}

function InfoDialog({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  return (
    <div className="modal-backdrop">
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby="info-dialog-title">
        <header>
          <h2 id="info-dialog-title">{title}</h2>
          <button type="button" aria-label="Close dialog" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="dialog-body">{children}</div>
        <footer>
          <button className="small-primary" type="button" onClick={onClose}>Close</button>
        </footer>
      </div>
    </div>
  );
}

function BacklogPanel({ health, blockedJobs }: { health: HealthPayload; blockedJobs: number }) {
  return (
    <Panel title={`Extraction Backlog (${health.jobs?.pending ?? 0})`}>
      <MiniTable rows={[
        ["OCR", "mail", "queued"],
        ["Video", "corpus", blockedJobs ? "blocked" : "idle"],
        ["Embeddings", "retrieval", `${health.retrieval?.embeddings ?? 0} vectors`],
        ["Assets", "corpus", `${health.retrieval?.asset_chunks ?? 0} chunks`]
      ]} />
    </Panel>
  );
}

function RecentErrors({ errors, onErrorDetail }: { errors: string[]; onErrorDetail: (error: string) => void }) {
  return (
    <Panel title="Recent Errors">
      <div className="error-list">
        {errors.slice(0, 4).map((error, index) => (
          <div className="error-item" key={`${error}-${index}`}>
            <span className="error-dot" />
            <div>
              <strong>{error}</strong>
              <span>Captured from shared health service</span>
            </div>
            <button type="button" aria-label={`View error ${error}`} onClick={() => onErrorDetail(error)}>View</button>
          </div>
        ))}
        {errors.length === 0 && <p className="muted">No recent errors.</p>}
      </div>
    </Panel>
  );
}

function SettingsPreview({ rows }: { rows: SettingRow[] }) {
  return (
    <div className="settings-list">
      {rows.slice(0, 8).map((setting) => (
        <div className="settings-row" key={setting.key}>
          <span>{setting.key}</span>
          <strong>{String(setting.value ?? "")}</strong>
          <em>{setting.apply_mode}</em>
        </div>
      ))}
      {rows.length === 0 && <p className="muted">No pending restart-class settings.</p>}
    </div>
  );
}

function OutlookHostPanel({ host, hostStatus, pending }: { host?: OutlookStatus["host"]; hostStatus: string; pending: number }) {
  return (
    <Panel title="Outlook COM host" action={<span className={`state-pill ${hostStatus === "running" ? "enabled" : "warning"}`}>{hostStatus}</span>}>
      <div className="host-layout">
        <div>
          <p className="muted">Outlook COM runs outside Docker in the logged-in Windows user session. Docker services coordinate through this API and the shared private spool.</p>
          <code>{host?.command ?? "flux-kb outlook-host run"}</code>
        </div>
        <div className="host-stats">
          <Stat label="Heartbeat" value={formatDate(host?.heartbeat_at)} />
          <Stat label="Pending requests" value={String(pending)} />
          <Stat label="Last error" value={host?.last_error ?? "-"} />
        </div>
      </div>
    </Panel>
  );
}

function DataRows({ rows, empty }: { rows: Array<Record<string, unknown>>; empty: string }) {
  if (rows.length === 0) return <p className="muted">{empty}</p>;
  return (
    <div className="data-rows">
      {rows.map((row, index) => (
        <pre key={index}>{JSON.stringify(row, null, 2)}</pre>
      ))}
    </div>
  );
}

function readDashboardState(): SavedDashboardState {
  const params = new URLSearchParams(window.location.search);
  const tab = params.get("tab") as TabId | null;
  const root = params.get("root");
  const profile = params.get("profile");
  if (tab && navItems.some((item) => item.id === tab)) {
    return { activeTab: tab, selectedRootName: root ?? "", selectedName: profile ?? "" };
  }
  try {
    const saved = JSON.parse(localStorage.getItem(DASHBOARD_STATE_KEY) ?? "{}") as SavedDashboardState;
    if (saved.activeTab && !navItems.some((item) => item.id === saved.activeTab)) {
      saved.activeTab = "health";
    }
    return saved;
  } catch {
    return {};
  }
}

function writeDashboardState(value: SavedDashboardState) {
  localStorage.setItem(DASHBOARD_STATE_KEY, JSON.stringify(value));
  const params = new URLSearchParams();
  if (value.activeTab && value.activeTab !== "health") params.set("tab", value.activeTab);
  if (value.selectedRootName) params.set("root", value.selectedRootName);
  if (value.selectedName) params.set("profile", value.selectedName);
  const query = params.toString();
  const nextUrl = query ? `/dashboard?${query}` : "/dashboard";
  if (`${window.location.pathname}${window.location.search}` !== nextUrl) {
    window.history.replaceState(null, "", nextUrl);
  }
}

function dashboardPollSeconds(settings: SettingRow[]) {
  const row = settings.find((setting) => setting.key === "dashboard.poll_interval_seconds");
  const value = Number(row?.value ?? DEFAULT_POLL_SECONDS);
  if (!Number.isFinite(value) || value <= 0) return DEFAULT_POLL_SECONDS;
  return Math.max(1, Math.round(value));
}

async function getJson<T>(url: string, fallback: T): Promise<T> {
  try {
    const response = await fetch(url);
    if (!response.ok) return fallback;
    return await response.json();
  } catch {
    return fallback;
  }
}

async function sendJson<T>(url: string, method: "POST" | "PUT" | "PATCH" | "DELETE", body: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    const details = await response.text().catch(() => "");
    throw new Error(formatApiError(method, url, response.status, response.statusText, details));
  }
  return await response.json();
}

async function fetchRequiredJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    const details = await response.text().catch(() => "");
    throw new Error(formatApiError("GET", url, response.status, response.statusText, details));
  }
  return await response.json();
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}

function formatApiError(method: string, url: string, status: number, statusText: string, body: string) {
  const detail = parseApiErrorDetail(body);
  const statusLabel = statusText ? `${status} ${statusText}` : String(status);
  return `${method} ${url} failed (${statusLabel})${detail ? `: ${detail}` : ""}`;
}

function parseApiErrorDetail(body: string) {
  if (!body.trim()) return "";
  try {
    const payload = JSON.parse(body) as Record<string, unknown>;
    const structured = payload.error;
    if (structured && typeof structured === "object") {
      const error = structured as Record<string, unknown>;
      const message = stringFromUnknown(error.message);
      const action = stringFromUnknown(error.user_action);
      if (message) return action ? `${message} ${action}` : message;
    }
    const detail = payload.detail ?? payload.message ?? payload.error;
    if (Array.isArray(detail)) {
      return detail.map((item) => objectSummary(item)).filter(Boolean).join("; ");
    }
    if (detail && typeof detail === "object") {
      return objectSummary(detail);
    }
    return detail ? String(detail) : body;
  } catch {
    return body;
  }
}

function mailSyncStatusFailed(status: string) {
  return ["auth_failed", "auth_expired", "blocked_auth_required", "blocked_missing_dependency", "failed"].includes(status) || status.endsWith("_failed");
}

function runStatusLabel(status?: string) {
  const value = status ?? "unknown";
  if (value === "blocked_auth_required") return "Blocked Auth Required";
  if (value === "auth_expired") return "Auth Expired";
  if (value === "auth_failed") return "Auth Failed";
  return value
    .split(/[_\s-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function mailPostProcessStatusLabel(status?: string) {
  return runStatusLabel(status);
}

function mailPostProcessPolicyLabel(policy?: string) {
  if (policy === "remove_label") return "Remove Label";
  if (policy === "move_to_processed") return "Move To Processed";
  if (policy === "trash") return "Trash";
  if (policy === "none") return "Leave In Place";
  return runStatusLabel(policy);
}

function mailPostProcessActionLabel(action?: string) {
  return runStatusLabel(action);
}

function metadataString(metadata: Record<string, unknown>, key: string, fallback: string) {
  const value = metadata[key];
  return typeof value === "string" ? value : fallback;
}

function nextMailRun(runs: MailSyncRun[]) {
  const candidates = runs
    .map((run) => run.next_attempt_at)
    .filter((value): value is string => Boolean(value))
    .sort();
  return candidates[0] ? formatDate(candidates[0]) : "-";
}

function mailSyncErrorDetail(result?: MailSyncProfileResult) {
  const errors = result?.errors ?? [];
  return errors
    .map((error) => {
      const parts = [error.folder, error.stage, error.error].filter(Boolean);
      return parts.join(" / ");
    })
    .filter(Boolean)
    .join("; ");
}

function objectSummary(value: unknown) {
  if (!value || typeof value !== "object") return String(value ?? "");
  return Object.entries(value as Record<string, unknown>)
    .map(([key, item]) => `${key}=${typeof item === "object" ? JSON.stringify(item) : String(item)}`)
    .join(", ");
}

function toastTone(message: string): "success" | "warning" | "error" {
  const text = message.toLowerCase();
  if (/(failed|failure|could not|error|invalid|denied|timed out|unavailable|auth_failed|auth_expired|blocked)/.test(text)) {
    return "error";
  }
  if (/(queued|pending|started|opened)/.test(text)) {
    return "warning";
  }
  return "success";
}

function fileActionToast(action: "open" | "reveal", state?: FileActionResponse["state"]) {
  const label = action === "open" ? "Open" : "Reveal";
  if (state === "opened") return `${label} request opened.`;
  if (state === "missing") return `${label} request could not find the file.`;
  if (state === "deleted") return `${label} request is unavailable because the asset is deleted from the index.`;
  if (state === "locked") return `${label} request could not access the file because it is locked.`;
  if (state === "host_agent_offline") return `${label} request cannot run because the host agent is offline.`;
  if (state === "not_allowed") return `${label} request was rejected for this indexed asset.`;
  return `${label} request finished with state ${state ?? "unknown"}.`;
}

function actionAvailable(detail: ResultDetail, action: string) {
  const availability = detail.actions?.[action];
  if (!availability) return false;
  return availability.available !== false;
}

function actionDisabledReason(detail: ResultDetail, action: string) {
  const availability = detail.actions?.[action];
  if (!availability) return "Action is not available for this result.";
  if (availability.available !== false) return "";
  return availability.disabled_reason ?? "Action is not available for this result.";
}

function resultActionPath(detail: ResultDetail, action: string) {
  return stringFromUnknown(detail.actions?.[action]?.path);
}

function uniqueActionReasons(detail: ResultDetail) {
  return Array.from(new Set(["copy_path", "open", "reveal"].map((action) => actionDisabledReason(detail, action)).filter(Boolean)));
}

function stringFromUnknown(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function numberFromUnknown(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function StatusChip({ label, ok }: { label: string; ok?: boolean }) {
  return (
    <div className="status-chip">
      <span>{label}</span>
      <i className={ok ? "ok" : "bad"} />
      <strong>{ok ? "Healthy" : "Blocked"}</strong>
    </div>
  );
}

function MetricCard({ icon, label, value, hint, tone }: { icon: ReactNode; label: string; value: string; hint: string; tone: string }) {
  return (
    <article className={`metric-card ${tone}`}>
      <div className="metric-icon">{icon}</div>
      <div>
        <span>{label}</span>
        <strong>{value}</strong>
        <em>{hint}</em>
      </div>
      <div className="sparkline" />
    </article>
  );
}

function Panel({ title, action, className = "", children }: { title: string; action?: ReactNode; className?: string; children: ReactNode }) {
  return (
    <section className={`panel ${className}`}>
      <header>
        <h2>{title}</h2>
        {action}
      </header>
      {children}
    </section>
  );
}

function SourceBadge({ source }: { source?: string }) {
  const outlook = source === "outlook_com";
  return (
    <span className="source-badge">
      {outlook ? <Inbox size={21} /> : <Mail size={21} />}
      {outlook ? "Outlook COM" : "Gmail IMAP"}
    </span>
  );
}

function AuthBadge({ profile, oauthProfiles, hostStatus }: { profile: MailProfile; oauthProfiles: Array<{ profile_name?: string; status?: string }>; hostStatus: string }) {
  if (profile.source_type === "outlook_com") {
    return <span className={hostStatus === "running" ? "auth good" : "auth warn"}><ShieldCheck size={16} /> {hostStatus === "running" ? "COM Ready" : "Host needed"}</span>;
  }
  const oauth = oauthProfiles.find((row) => row.profile_name === profile.name)?.status ?? "blocked_auth_required";
  const good = oauth === "configured";
  const pending = oauth === "pending_user_authorization";
  const Icon = good ? CheckCircle2 : pending ? Clock3 : AlertCircle;
  return <span className={good ? "auth good" : "auth warn"}><Icon size={16} /> {oauth}</span>;
}

function StatusTile({ label, ok, message }: { label: string; ok?: boolean; message?: string }) {
  return (
    <div className="status-tile">
      <span className={ok ? "status-dot ok" : "status-dot bad"} />
      <div>
        <strong>{label}</strong>
        <span>{message ?? (ok ? "healthy" : "blocked")}</span>
      </div>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return <div className="stat"><span>{label}</span><strong>{value}</strong></div>;
}

type MiniTableRow = [string, string, string] | [string, string, string, string];

function MiniTable({ rows }: { rows: MiniTableRow[] }) {
  const wide = rows.some((row) => row.length > 3);
  return (
    <div className={`mini-table${wide ? " wide" : ""}`}>
      {rows.map((row) => (
        <div key={row.join("-")}>
          {row.map((cell, index) => index === row.length - 1 ? <strong key={`${cell}-${index}`}>{cell}</strong> : <span key={`${cell}-${index}`}>{cell}</span>)}
        </div>
      ))}
    </div>
  );
}

function restartSettings(settings: SettingRow[]) {
  return settings.filter((setting) => ["reload", "restart_component", "reindex_required", "manual_process_restart"].includes(setting.apply_mode));
}

function requiresConfirmation(setting: SettingRow) {
  return ["restart_component", "reindex_required"].includes(setting.apply_mode);
}

function parseSettingValue(value: string, current: unknown) {
  if (typeof current === "number") return Number(value);
  if (typeof current === "boolean") return value === "true";
  if (/^-?\d+(\.\d+)?$/.test(value.trim())) return Number(value);
  if (value === "true") return true;
  if (value === "false") return false;
  return value;
}

function splitLines(value: string) {
  return value.split(/\r?\n|,/).map((item) => item.trim()).filter(Boolean);
}

function oauthClientConfigPath(profile?: MailProfile): string {
  const metadata = profile?.metadata ?? {};
  const profilePath = metadata.gmail_oauth_client_config_path ?? metadata.oauth_client_config_path;
  return typeof profilePath === "string" && profilePath.trim() ? profilePath : "private/google-oauth-client.json";
}

function openOAuthPopup(): Window | null {
  const popup = window.open("about:blank", "_blank");
  if (!popup) return null;
  try {
    popup.document.title = "Flux Gmail OAuth";
    popup.document.body.innerHTML = "<p>Preparing Gmail OAuth consent...</p>";
  } catch {
    // Some browsers restrict about:blank writes; the later navigation still works.
  }
  return popup;
}

function slugFromPath(value: string) {
  const leaf = value.trim().split(/[\\/]+/).filter(Boolean).pop() ?? "";
  return leaf
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

function intervalLabel(seconds?: number) {
  if (!seconds) return "Manual";
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  if (seconds % 86400 === 0) return `${seconds / 86400}d`;
  return `${Math.round(seconds / 3600)}h`;
}

function hostStatusLabel(status: string) {
  if (status === "host_offline") return "Host offline";
  if (status === "blocked_not_windows") return "Not Windows";
  if (status === "blocked_missing_dependency") return "Missing dependency";
  if (status === "blocked_outlook_unavailable") return "Outlook unavailable";
  return status;
}

function formatDate(value?: string | null) {
  if (!value) return "-";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString([], { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

function titleCase(value?: string | null) {
  const text = String(value ?? "").replace(/[_-]+/g, " ").trim();
  if (!text) return "";
  return text.replace(/\b\w/g, (letter) => letter.toUpperCase());
}
