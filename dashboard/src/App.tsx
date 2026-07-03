import {
  AlertCircle,
  AlertTriangle,
  Archive,
  ArrowDown,
  ArrowUp,
  ArrowUpDown,
  BriefcaseBusiness,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
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
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";

type HealthCheck = { ok?: boolean; message?: string; required?: boolean; label?: string; target?: string | null; probe_target?: string | null };

type HealthPayload = {
  database?: HealthCheck & { checks?: Record<string, HealthCheck> };
  runtime?: Record<string, HealthCheck>;
  watcher?: { active_roots?: number; disabled_roots?: number; stale_count?: number; roots?: unknown[] };
  jobs?: { pending?: number; failed?: number; blocked?: number };
  retrieval?: { episodes?: number; sources?: number; source_assets?: number; asset_chunks?: number; search_index_records?: number };
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
  docker?: DockerResourceStatus;
};

type DockerResourceStatus = {
  ok?: boolean;
  state?: string;
  message?: string;
  containers?: DockerContainerResource[];
  totals?: {
    reported?: number;
    running?: number;
    memory_limit_bytes?: number;
    memory_swap_limit_bytes?: number;
    memory_usage_bytes?: number;
    size_rw_bytes?: number;
    size_root_fs_bytes?: number;
    block_io_read_bytes?: number;
    block_io_write_bytes?: number;
  };
};

type DockerContainerResource = {
  service?: string;
  container_name?: string;
  image?: string;
  status?: string;
  running?: boolean;
  cpu_percent?: number | null;
  memory_usage_bytes?: number | null;
  memory_limit_bytes?: number | null;
  memory_swap_limit_bytes?: number | null;
  memory_stats_limit_bytes?: number | null;
  memory_percent?: number | null;
  block_io_read_bytes?: number | null;
  block_io_write_bytes?: number | null;
  network_rx_bytes?: number | null;
  network_tx_bytes?: number | null;
  pids?: number | null;
  size_rw_bytes?: number | null;
  size_root_fs_bytes?: number | null;
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

type JobToolInvocation = {
  id?: string;
  job_id?: string;
  command?: unknown;
  cwd?: string | null;
  status?: string;
  return_code?: number | null;
  stdout?: string;
  stderr?: string;
  exception_type?: string | null;
  exception_message?: string | null;
  started_at?: string | null;
  completed_at?: string | null;
  duration_ms?: number | null;
};

type JobToolInvocationState = {
  loading: boolean;
  error?: string;
  invocations: JobToolInvocation[];
};

type JobToolInvocationResponse = {
  job_id?: string;
  invocations?: JobToolInvocation[];
};

type AccelerationBenchmarkRun = {
  id?: string;
  fixture?: string;
  mode?: string;
  scenario?: string;
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
  recommendation_metadata?: Record<string, unknown>;
  created_at?: string | null;
};

type BenchmarkDiagnostic = {
  scenario?: string;
  check?: string;
  status?: string;
  summary?: string;
  evidence?: Record<string, unknown>;
};

type BenchmarkCandidate = {
  setting?: string;
  current?: number | string;
  candidate?: number | string;
  reason?: string;
  requires_manual_apply?: boolean;
  evidence_state?: string;
  follow_up_command?: string;
};

type BenchmarkRunResponse = {
  fixture?: string;
  mode?: string;
  scenario?: string;
  runs?: AccelerationBenchmarkRun[];
  diagnostics?: BenchmarkDiagnostic[];
  recommendations?: {
    settings_mutated?: boolean;
    scenario?: string;
    candidates?: BenchmarkCandidate[];
  };
};

type ReliabilityCheck = {
  check?: string;
  status?: string;
  summary?: string;
};

type ReliabilityStatus = {
  readiness?: string;
  settings_mutated?: boolean;
  evidence_age_hours?: number | null;
  checks?: ReliabilityCheck[];
  candidates?: BenchmarkCandidate[];
  watcher?: { backend?: string; event_count?: number; probe_event_count?: number };
  workers?: { families?: Array<{ family?: string; backpressure?: string; pending?: number }> };
};

type RootReliabilityCard = {
  root_name?: string;
  readiness?: string;
  blockers?: Record<string, number>;
  latest_benchmark?: { id?: string; scenario?: string };
  required_action?: string;
};

type ReliabilityRootsStatus = {
  settings_mutated?: boolean;
  totals?: Record<string, number>;
  roots?: RootReliabilityCard[];
  required_actions?: Array<{ root_name?: string; readiness?: string; required_action?: string }>;
};

type OperatorEvidence = {
  settings_mutated?: boolean;
  readiness?: string;
  root_readiness?: Record<string, number>;
  gates?: Record<string, { state?: string; reason?: string }>;
  top_blockers?: Array<{ section?: string; severity?: string; root_name?: string; summary?: string }>;
  manual_follow_ups?: Array<{ setting?: string; command?: string }>;
  code_gaps?: CodeGap[];
};

type CodeGap = {
  category?: string;
  priority?: string;
  count?: number;
  summary?: string;
  source?: string;
  case_category?: string;
  reasons?: string[];
};

type CodeSearchResult = {
  symbol?: string;
  target?: string;
  symbol_kind?: string;
  relationship?: string;
  relationship_kind?: string;
  language?: string;
  path?: string;
  line_start?: number;
  line_end?: number;
  is_generated?: boolean;
  source_symbol?: string;
  target_symbol?: string;
};

type CodeSearchResponse = {
  settings_mutated?: boolean;
  results?: CodeSearchResult[];
};

type CodeSymbolLookupResponse = {
  settings_mutated?: boolean;
  query?: string;
  matches?: CodeSearchResult[];
  references?: CodeSearchResult[];
};

type CodeStatus = {
  totals?: Record<string, number>;
  feedback_summary?: { totals?: Record<string, number>; rows?: Array<{ miss_category?: string; root_name?: string; event_count?: number }> };
  gaps?: CodeGap[];
  roots?: Array<{
    root_name?: string;
    health?: string;
    asset_count?: number;
    symbol_count?: number;
    reference_count?: number;
    fallback_count?: number;
    generated_count?: number;
    languages?: Record<string, number>;
    parser_statuses?: Record<string, number>;
    slow_files?: Array<{ path?: string; duration_ms?: number }>;
  }>;
};

type OperationalDiagnostics = {
  section?: string;
  settings_mutated?: boolean;
  counts?: Record<string, number>;
  items?: DiagnosticItem[];
  sections?: {
    retrieval?: { recent_explains?: Array<Record<string, unknown>> };
    watcher?: { events?: Array<Record<string, unknown>> };
    workers?: { families?: Array<Record<string, unknown>> };
    jobs?: { jobs?: Array<Record<string, unknown>> };
    mail?: { sync_runs?: Array<Record<string, unknown>>; post_process_events?: Array<Record<string, unknown>> };
  };
};

type DiagnosticAction = {
  id?: string;
  label?: string;
  target?: { type?: string; id?: string };
  method?: string;
  endpoint?: string;
  payload?: Record<string, unknown>;
  requires_confirmation?: boolean;
  destructive?: boolean;
  settings_mutated?: boolean;
};

type DiagnosticItem = {
  section?: string;
  severity?: string;
  status?: string;
  family?: string;
  root_name?: string;
  summary?: string;
  evidence?: Record<string, unknown>;
  follow_up_command?: string;
  remediation_actions?: DiagnosticAction[];
};

type RetrievalBenchmarkCaseResult = {
  case_id?: string;
  category?: string;
  status?: string;
  expected_ids?: string[];
  observed_ids?: string[];
  reasons?: string[];
  confidence_band?: string;
  failure_details?: Array<{ reason?: string; message?: string }>;
};

type RetrievalBenchmarkCalibrationSummary = {
  confidence_bands?: Record<string, number>;
  semantic_thresholds?: Array<{
    threshold?: number;
    evaluated_count?: number;
    false_positive_count?: number;
    false_negative_count?: number;
    pass_count?: number;
  }>;
};

type RetrievalBenchmarkCandidate = {
  kind?: string;
  threshold?: number;
  evidence_count?: number;
  false_positive_count?: number;
  false_negative_count?: number;
  rationale?: string;
};

type GovernanceShadowSummary = {
  proposal_case_count?: number;
  proposal_pass_count?: number;
  proposal_precision?: number;
  guardrail_case_count?: number;
  guardrail_pass_count?: number;
  guardrail_fail_count?: number;
  proposal_categories?: Record<string, number>;
};

type RetrievalBenchmarkRun = {
  id?: string;
  suite?: string;
  label?: string | null;
  compare_label?: string | null;
  status?: string;
  query_count?: number;
  passed_count?: number;
  failed_count?: number;
  metrics?: Record<string, number>;
  metric_deltas?: Record<string, number>;
  calibration_summary?: RetrievalBenchmarkCalibrationSummary;
  case_results?: RetrievalBenchmarkCaseResult[];
  recommendations?: {
    settings_mutated?: boolean;
    purpose?: string;
    governance_shadow?: GovernanceShadowSummary;
    candidates?: RetrievalBenchmarkCandidate[];
  };
  recommendation_metadata?: {
    settings_mutated?: boolean;
    purpose?: string;
    governance_shadow?: GovernanceShadowSummary;
    candidates?: RetrievalBenchmarkCandidate[];
  };
  created_at?: string | null;
};

type RetrievalBenchmarkHistory = {
  suite?: string;
  runs?: RetrievalBenchmarkRun[];
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

type MailProfileDeleteResponse = {
  profile_name?: string;
  root_name?: string;
  deleted?: boolean;
  profile?: { deleted?: boolean };
  corpus_root?: { deleted?: boolean };
  search_index?: { deleted?: number; records_deleted?: number; failed?: number; errors?: unknown[] };
  semantic_duplicate_clusters?: { deleted?: number };
  sidecars?: { deleted?: number; missing?: number; blocked?: number; failed?: number; errors?: unknown[] };
  spool?: {
    status?: string;
    deleted?: boolean;
    path?: string | null;
    blocked_reason?: string | null;
    error?: string | null;
  };
};

type OutlookSyncRequest = {
  id?: string;
  profile_name?: string;
  status?: string;
  requested_by?: string;
  claimed_by?: string | null;
  error?: string | null;
  result?: Record<string, unknown>;
  created_at?: string;
  updated_at?: string;
};

type OutlookStatus = {
  host?: {
    host_id?: string;
    status?: string;
    reported_status?: string;
    command?: string;
    heartbeat_at?: string | null;
    last_error?: string | null;
    heartbeat_age_seconds?: number | null;
  };
  profiles?: MailProfile[];
  pending_requests?: OutlookSyncRequest[];
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

type JobFilterOptions = {
  statuses?: string[];
  roots?: string[];
  job_types?: string[];
};

type JobsPayload = {
  jobs?: Array<Record<string, unknown>>;
  count?: number;
  limit?: number;
  offset?: number;
  has_next?: boolean;
  filter_options?: JobFilterOptions;
};

type RetrievalPayload = {
  retrieval?: HealthPayload["retrieval"];
  duplicate_assets?: number;
  duplicate_count?: number;
  stats?: Record<string, unknown>;
};

type ModelActivityEvent = {
  id?: string;
  service?: string;
  endpoint?: string;
  action?: string;
  activity_class?: string;
  caller_surface?: string | null;
  model?: string | null;
  status?: "running" | "completed" | "failed" | "busy" | "stale_running" | string;
  started_at?: string | null;
  completed_at?: string | null;
  duration_ms?: number | null;
  error_class?: string | null;
  error_message?: string | null;
  metadata?: Record<string, unknown>;
};

type ModelActivityBreakdown = {
  service?: string;
  activity_class?: string;
  count?: number;
  active?: number;
  failures?: number;
};

type ModelActivityScheduler = {
  mode?: string;
  running_count?: number;
  waiting_count?: number;
  recent_count?: number;
  rejections?: number;
  timeouts?: number;
  evictions_recent_count?: number;
  last_eviction_at?: string | null;
  oldest_wait_age_ms?: number | null;
  last_activity_at?: string | null;
  resident_models?: Array<{ service?: string; model?: string; task_type?: string | null; last_used_at?: string | null }>;
  live_gpu_memory?: { available?: boolean; used_mb?: number | null; total_mb?: number | null };
};

type ModelActivityPayload = {
  window_minutes?: number;
  limit?: number;
  offset?: number;
  total_count?: number;
  has_next?: boolean;
  page_count?: number;
  active_count?: number;
  recent_count?: number;
  last_event_at?: string | null;
  service_breakdown?: ModelActivityBreakdown[];
  class_breakdown?: ModelActivityBreakdown[];
  events?: ModelActivityEvent[];
  scheduler?: ModelActivityScheduler;
};

type SearchResult = {
  kind?: string;
  logical_kind?: "file" | "mail" | "episode" | string;
  file_kind?: string;
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
  file_kinds?: string[];
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
  semantic_duplicates?: Array<Record<string, unknown>>;
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
  path?: string | null;
  message?: string | null;
  reason?: string | null;
};

type ToastTone = "success" | "warning" | "error";
type ToastState = {
  message: string;
  tone: ToastTone;
};

type PendingActionState = Record<string, { label: string }>;

const MAIL_ROW_MENU_WIDTH = 180;
const MAIL_ROW_MENU_ESTIMATED_HEIGHT = 96;
const MENU_VIEWPORT_MARGIN = 8;

function mailProfileActionKey(action: string, profileName: string) {
  return `mail-profile:${action}:${profileName}`;
}

function corpusRootActionKey(action: string, rootName: string) {
  return `corpus-root:${action}:${rootName}`;
}

function corpusJobActionKey(action: string, jobId: string) {
  return `corpus-job:${action}:${jobId}`;
}

function settingActionKey(action: string, key: string) {
  return `setting:${action}:${key}`;
}

function globalActionKey(action: string) {
  return `dashboard:${action}`;
}

function pendingLabel(pendingActions: PendingActionState, key: string) {
  return pendingActions[key]?.label;
}

function rowMenuPosition(rect: DOMRect, width = MAIL_ROW_MENU_WIDTH, height = MAIL_ROW_MENU_ESTIMATED_HEIGHT) {
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || width + MENU_VIEWPORT_MARGIN * 2;
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || height + MENU_VIEWPORT_MARGIN * 2;
  const maxLeft = Math.max(MENU_VIEWPORT_MARGIN, viewportWidth - width - MENU_VIEWPORT_MARGIN);
  const left = Math.min(Math.max(MENU_VIEWPORT_MARGIN, rect.right - width), maxLeft);
  const below = rect.bottom + MENU_VIEWPORT_MARGIN;
  const top = below + height > viewportHeight - MENU_VIEWPORT_MARGIN
    ? Math.max(MENU_VIEWPORT_MARGIN, rect.top - height - MENU_VIEWPORT_MARGIN)
    : below;
  return { top, left };
}

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
  modelActivity: ModelActivityPayload;
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

type CaptureReviewStatus = "pending_review" | "approved" | "rejected" | "completed" | "failed" | "blocked_missing_dependency" | "all";

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

type GovernanceAction = {
  id?: string;
  action?: string;
  target_type?: string;
  target_id?: string;
  memory_class?: string | null;
  risk?: string;
  status?: string;
  source?: string;
  rationale?: { summary?: string; guardrails?: Record<string, unknown> };
  evidence?: Record<string, unknown>;
  before_state?: Record<string, unknown>;
  after_state?: Record<string, unknown>;
  settings_mutated?: boolean;
  memory_mutated?: boolean;
  created_at?: string | null;
  applied_at?: string | null;
  recovered_at?: string | null;
};

type GovernanceActionsPayload = {
  actions?: GovernanceAction[];
  telemetry?: {
    total?: number;
    by_source?: Record<string, number>;
    by_action?: Record<string, number>;
    by_risk?: Record<string, number>;
    by_status?: Record<string, number>;
    by_mutation?: Record<string, number>;
  };
};

type GovernanceDigestPayload = {
  digest?: {
    summary?: Record<string, unknown>;
    recommendations?: Array<Record<string, unknown>>;
  };
  settings_mutated?: boolean;
};

type GovernancePolicyPayload = {
  policy?: Record<string, unknown>;
  settings_mutated?: boolean;
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

type TabId = "overview" | "automation" | "diagnostics" | "performance" | "corpus" | "mail" | "settings" | "retrieval" | "review" | "jobs";

type AutomationAction = {
  id?: string;
  action?: string;
  label?: string;
  status?: string;
  risk?: string;
  source?: string;
  target_type?: string | null;
  target_id?: string | null;
  reason?: string;
  evidence?: Record<string, unknown>;
  result?: Record<string, unknown>;
  created_at?: string | null;
};

type AutomationRun = {
  id?: string;
  status?: string;
  mode?: string;
  trigger?: string;
  started_at?: string | null;
  completed_at?: string | null;
  summary?: Record<string, unknown>;
};

type AutomationRecurring = {
  enabled?: boolean;
  interval_seconds?: number;
  last_run_at?: string | null;
  next_run_at?: string | null;
  remaining_seconds?: number;
  due?: boolean;
};

type AutomationStatus = {
  settings_mutated?: boolean;
  policy?: Record<string, unknown>;
  recurring?: AutomationRecurring;
  last_run?: AutomationRun | null;
  eligible_actions?: AutomationAction[];
  manual_required?: AutomationAction[];
  recent_actions?: AutomationAction[];
  runs?: AutomationRun[];
};

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
  include_subfolders: boolean;
  outlook_incremental_basis: "received_time" | "last_modification_time";
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
  modelActivity: { events: [], service_breakdown: [], class_breakdown: [], scheduler: {} },
  mail: { profiles: [] },
  outlook: { profiles: [], pending_requests: [] },
  settings: []
};

const navItems: Array<{ id: TabId; label: string; icon: ReactNode }> = [
  { id: "overview", label: "Overview", icon: <HeartPulse size={20} /> },
  { id: "automation", label: "Automation", icon: <ShieldCheck size={20} /> },
  { id: "diagnostics", label: "Diagnostics", icon: <Wrench size={20} /> },
  { id: "performance", label: "Performance", icon: <Gauge size={20} /> },
  { id: "corpus", label: "Corpus", icon: <Folder size={20} /> },
  { id: "mail", label: "Mail", icon: <Mail size={20} /> },
  { id: "settings", label: "Settings", icon: <Settings size={20} /> },
  { id: "retrieval", label: "Retrieval", icon: <Search size={20} /> },
  { id: "review", label: "Review", icon: <ShieldCheck size={20} /> },
  { id: "jobs", label: "Jobs", icon: <ListFilter size={20} /> }
];

const DASHBOARD_STATE_KEY = "flux-dashboard-state";
const DEFAULT_POLL_SECONDS = 10;
const JOB_PAGE_LIMIT = 50;
const MODEL_ACTIVITY_PAGE_LIMIT = 50;
type JobHistoryFilters = {
  status: string[];
  root_name: string[];
  job_type: string[];
  updated_from: string;
  updated_to: string;
};
type JobSortKey = "status" | "job_type" | "target" | "root" | "attempts" | "updated" | "progress" | "last_error";
type JobSortDir = "asc" | "desc";
type JobSortState = {
  sort_by: JobSortKey;
  sort_dir: JobSortDir;
};
type JobPageItem = number | `ellipsis-${number}`;
const emptyJobHistoryFilters: JobHistoryFilters = {
  status: [],
  root_name: [],
  job_type: [],
  updated_from: "",
  updated_to: ""
};
const defaultJobSort: JobSortState = {
  sort_by: "updated",
  sort_dir: "desc"
};
const jobSortKeys = new Set<JobSortKey>(["status", "job_type", "target", "root", "attempts", "updated", "progress", "last_error"]);
type SavedDashboardState = {
  activeTab?: TabId;
  selectedName?: string;
  selectedRootName?: string;
  jobFilters?: JobHistoryFilters;
  jobSort?: JobSortState;
};

export default function App() {
  const initialDashboardState = readDashboardState();
  const [state, setState] = useState<LoadState>(emptyState);
  const [activeTab, setActiveTab] = useState<TabId>(initialDashboardState.activeTab ?? "overview");
  const [selectedName, setSelectedName] = useState<string>(initialDashboardState.selectedName ?? "");
  const [selectedRootName, setSelectedRootName] = useState<string>(initialDashboardState.selectedRootName ?? "");
  const [jobFilters, setJobFilters] = useState<JobHistoryFilters>(initialDashboardState.jobFilters ?? emptyJobHistoryFilters);
  const [jobSort, setJobSort] = useState<JobSortState>(initialDashboardState.jobSort ?? defaultJobSort);
  const [jobOffset, setJobOffset] = useState(0);
  const [modelActivityOffset, setModelActivityOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [toast, setToastState] = useState<ToastState | null>(null);
  const [pendingActions, setPendingActions] = useState<PendingActionState>({});
  const pendingActionKeysRef = useRef<Set<string>>(new Set());
  function setToast(next: string | ToastState, tone?: ToastTone) {
    if (typeof next !== "string") {
      setToastState(next);
      return;
    }
    if (!next) {
      setToastState(null);
      return;
    }
    setToastState({ message: next, tone: tone ?? toastTone(next) });
  }
  async function runPendingAction<T>(key: string, label: string, operation: () => Promise<T>): Promise<T | undefined> {
    if (pendingActionKeysRef.current.has(key)) {
      return undefined;
    }
    pendingActionKeysRef.current.add(key);
    setPendingActions((current) => ({ ...current, [key]: { label } }));
    try {
      return await operation();
    } finally {
      pendingActionKeysRef.current.delete(key);
      setPendingActions((current) => {
        const next = { ...current };
        delete next[key];
        return next;
      });
    }
  }
  const [debugOpen, setDebugOpen] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [profileDialog, setProfileDialog] = useState<MailProfile | "new" | null>(null);
  const [rootDialog, setRootDialog] = useState<RootSummary | "new" | null>(null);
  const [deleteProfile, setDeleteProfile] = useState<MailProfile | null>(null);
  const [deleteRoot, setDeleteRoot] = useState<RootSummary | null>(null);
  const [settingEditor, setSettingEditor] = useState<SettingRow | null>(null);
  const [settingValue, setSettingValue] = useState("");
  const [confirmSetting, setConfirmSetting] = useState<SettingRow | null>(null);
  const [errorDetail, setErrorDetail] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState<SearchResult[]>([]);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchKind, setSearchKind] = useState("balanced");
  const [searchCurrentOnly, setSearchCurrentOnly] = useState(false);
  const [searchIncludeSuppressed, setSearchIncludeSuppressed] = useState(false);
  const [searchFilterTrace, setSearchFilterTrace] = useState<RetrievalFilterTrace>({});
  const [searchSuppression, setSearchSuppression] = useState<RetrievalSuppression>({});
  const [resultDetail, setResultDetail] = useState<ResultDetail | null>(null);
  const [resultDetailLoading, setResultDetailLoading] = useState(false);
  const [reviewFilter, setReviewFilter] = useState<ClaimReviewFilter>("needs_review");
  const [reviewStateFilter, setReviewStateFilter] = useState("");
  const [captureReviewStatus, setCaptureReviewStatus] = useState<CaptureReviewStatus>("pending_review");
  const [claimReview, setClaimReview] = useState<ClaimReviewPayload>({ claims: [], counts: {} });
  const [captureReview, setCaptureReview] = useState<CaptureReviewPayload>({ jobs: [] });
  const [captureReviewAudit, setCaptureReviewAudit] = useState<AuditEvent[]>([]);
  const [retentionPolicies, setRetentionPolicies] = useState<RetentionPolicyPayload>({ policies: [] });
  const [retentionQuality, setRetentionQuality] = useState<RetentionQualityPayload>({ summary: {}, candidates: [] });
  const [governanceActions, setGovernanceActions] = useState<GovernanceActionsPayload>({ actions: [], telemetry: {} });
  const [governanceDigest, setGovernanceDigest] = useState<GovernanceDigestPayload>({ digest: { summary: {}, recommendations: [] } });
  const [governancePolicy, setGovernancePolicy] = useState<GovernancePolicyPayload>({ policy: {} });
  const [captureDecision, setCaptureDecision] = useState<CaptureDecisionState | null>(null);
  const [captureDecisionReason, setCaptureDecisionReason] = useState("");
  const [selectedClaimId, setSelectedClaimId] = useState("");
  const [claimGraph, setClaimGraph] = useState<GraphPayload>({ edges: [] });
  const [reviewLoading, setReviewLoading] = useState(false);
  const [showControlPlaneActivity, setShowControlPlaneActivity] = useState(false);
  const [theme, setTheme] = useState(() => localStorage.getItem("flux-dashboard-theme") ?? "light");

  async function load(options: { showLoading?: boolean; jobFilters?: JobHistoryFilters; jobOffset?: number; jobSort?: JobSortState; modelActivityOffset?: number; showControlPlaneActivity?: boolean } = {}) {
    if (options.showLoading ?? false) {
      setLoading(true);
    }
    const effectiveJobFilters = options.jobFilters ?? jobFilters;
    const effectiveJobOffset = options.jobOffset ?? jobOffset;
    const effectiveJobSort = options.jobSort ?? jobSort;
    const effectiveModelActivityOffset = options.modelActivityOffset ?? modelActivityOffset;
    const effectiveShowControlPlaneActivity = options.showControlPlaneActivity ?? showControlPlaneActivity;
    const modelActivityUrl = modelActivityHistoryUrl(effectiveShowControlPlaneActivity, effectiveModelActivityOffset);
    const [health, crawl, jobs, retrieval, modelActivity, mail, outlook, settings] = await Promise.all([
      getJson<HealthPayload>("/api/dashboard/health", {}),
      getJson<CrawlPayload>("/api/dashboard/crawl", { roots: [] }),
      getJson<JobsPayload>(jobHistoryUrl(effectiveJobFilters, effectiveJobOffset, effectiveJobSort), { jobs: [], limit: JOB_PAGE_LIMIT, offset: effectiveJobOffset }),
      getJson<RetrievalPayload>("/api/dashboard/retrieval-stats", {}),
      getJson<ModelActivityPayload>(modelActivityUrl, { events: [], service_breakdown: [], class_breakdown: [], scheduler: {} }),
      getJson<MailStatus>("/api/mail/status", { profiles: [] }),
      getJson<OutlookStatus>("/api/outlook-host/status", { profiles: [], pending_requests: [] }),
      getJson<SettingRow[]>("/api/settings", [])
    ]);
    setState({ health, crawl, jobs, retrieval, modelActivity, mail, outlook, settings });
    setLastUpdated(new Date());
    setLoading(false);
  }

  useEffect(() => {
    void load({ showLoading: true });
  }, []);

  function updateControlPlaneActivity(next: boolean) {
    setShowControlPlaneActivity(next);
    setModelActivityOffset(0);
    void load({ showControlPlaneActivity: next, modelActivityOffset: 0 });
  }

  async function updateModelActivityPage(offset: number) {
    const nextOffset = Math.max(0, offset);
    setModelActivityOffset(nextOffset);
    await load({ modelActivityOffset: nextOffset });
  }

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
  }, [activeTab, reviewFilter, reviewStateFilter, captureReviewStatus]);

  useEffect(() => {
    if (activeTab !== "review" || !selectedClaim?.subject_entity_id) {
      setClaimGraph({ edges: [] });
      return;
    }
    void loadClaimGraph(selectedClaim.subject_entity_id);
  }, [activeTab, selectedClaim?.subject_entity_id]);

  const pollSeconds = dashboardPollSeconds(state.settings);

  useEffect(() => {
    writeDashboardState({ activeTab, selectedName, selectedRootName, jobFilters, jobSort });
  }, [activeTab, selectedName, selectedRootName, jobFilters, jobSort]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      void load({ showLoading: false });
    }, pollSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [pollSeconds, jobFilters, jobOffset, jobSort, modelActivityOffset, showControlPlaneActivity]);

  async function requestProfileSync(profile = selectedProfile) {
    if (!profile) {
      setToast("Select a mail profile first.");
      return;
    }
    await runPendingAction(mailProfileActionKey("sync", profile.name), "Syncing...", async () => {
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
    });
  }

  async function cancelOutlookRequest(requestId: string) {
    await runPendingAction(`outlook-request:cancel:${requestId}`, "Cancelling...", async () => {
      try {
        await sendJson(`/api/outlook-host/requests/${encodeURIComponent(requestId)}/cancel`, "POST", {});
        setToast("Outlook sync request cancelled.");
        await load();
      } catch (error) {
        setToast(`Outlook sync request cancellation failed: ${errorMessage(error)}`);
      }
    });
  }

  async function cancelCorpusJob(jobId: string) {
    await runPendingAction(corpusJobActionKey("cancel", jobId), "Cancelling...", async () => {
      try {
        await sendJson(`/api/dashboard/jobs/${encodeURIComponent(jobId)}/cancel`, "POST", {});
        setToast("Corpus job cancelled.");
        await load();
      } catch (error) {
        setToast(`Corpus job cancellation failed: ${errorMessage(error)}`);
      }
    });
  }

  async function retryCorpusJob(jobId: string) {
    await runPendingAction(corpusJobActionKey("retry", jobId), "Retrying...", async () => {
      try {
        await sendJson(`/api/dashboard/jobs/${encodeURIComponent(jobId)}/retry`, "POST", {});
        setToast("Corpus job queued for retry.");
        await load();
      } catch (error) {
        setToast(`Corpus job retry failed: ${errorMessage(error)}`);
      }
    });
  }

  async function markCorpusJobForDeletion(jobId: string) {
    await runPendingAction(corpusJobActionKey("delete-request", jobId), "Marking...", async () => {
      try {
        await sendJson(`/api/dashboard/jobs/${encodeURIComponent(jobId)}/delete-request`, "POST", { reason: "operator_cleanup" });
        setToast("Corpus job marked obsolete for deletion.");
        await load();
      } catch (error) {
        setToast(`Corpus job deletion mark failed: ${errorMessage(error)}`);
      }
    });
  }

  async function restoreCorpusJobDeletionRequest(jobId: string) {
    await runPendingAction(corpusJobActionKey("restore-delete-request", jobId), "Restoring...", async () => {
      try {
        await sendJson(`/api/dashboard/jobs/${encodeURIComponent(jobId)}/delete-request`, "DELETE", {});
        setToast("Corpus job deletion mark restored.");
        await load();
      } catch (error) {
        setToast(`Corpus job deletion restore failed: ${errorMessage(error)}`);
      }
    });
  }

  async function applyJobFilters(filters: JobHistoryFilters) {
    setJobFilters(filters);
    setJobOffset(0);
    await load({ jobFilters: filters, jobOffset: 0, jobSort });
  }

  async function clearJobFilters() {
    setJobFilters(emptyJobHistoryFilters);
    setJobOffset(0);
    await load({ jobFilters: emptyJobHistoryFilters, jobOffset: 0, jobSort });
  }

  async function applyJobSort(sort: JobSortState) {
    setJobSort(sort);
    setJobOffset(0);
    await load({ jobSort: sort, jobOffset: 0 });
  }

  async function pageJobHistory(offset: number) {
    const nextOffset = Math.max(0, offset);
    setJobOffset(nextOffset);
    await load({ jobOffset: nextOffset, jobSort });
  }

  async function saveProfile(form: ProfileForm) {
    const outlook = form.source_type === "outlook_com";
    const payload = {
      name: form.name.trim(),
      source_type: form.source_type,
      account: outlook ? null : form.account.trim() || null,
      server: outlook ? null : form.server.trim() || null,
      folder_paths: splitLines(form.folder_paths),
      spool_path: form.spool_path.trim(),
      post_process_policy: outlook ? "none" : form.post_process_policy,
      processed_folder: outlook ? "" : form.processed_folder.trim(),
      trash_folder: outlook ? "" : form.trash_folder.trim(),
      destructive_post_process_confirmed: outlook ? false : form.destructive_post_process_confirmed,
      sync_enabled: form.sync_enabled,
      sync_interval_seconds: Number(form.sync_interval_seconds),
      sync_window_days: Number(form.sync_window_days),
      max_messages_per_run: Number(form.max_messages_per_run),
      ...(outlook
        ? {
            include_subfolders: form.include_subfolders,
            outlook_incremental_basis: form.outlook_incremental_basis
          }
        : {})
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
    await runPendingAction(mailProfileActionKey("post-process-dry-run", profile.name), "Checking...", async () => {
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
    });
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
    await runPendingAction(mailProfileActionKey("oauth-client-save", profile.name), "Saving...", async () => {
      try {
        await sendJson(`/api/mail/profiles/${encodeURIComponent(profile.name)}/oauth-client-config`, "PUT", {
          client_config_path: clientConfigPath.trim()
        });
        setToast(`OAuth client JSON path saved for ${profile.name}.`);
        await load();
      } catch (error) {
        setToast(`Could not save OAuth client JSON path for ${profile.name}: ${errorMessage(error)}`);
      }
    });
  }

  async function deleteSelectedProfile(profile: MailProfile) {
    await runPendingAction(mailProfileActionKey("delete", profile.name), "Deleting...", async () => {
      setDeleteProfile(null);
      try {
        const payload = await sendJson<MailProfileDeleteResponse>(`/api/mail/profiles/${encodeURIComponent(profile.name)}`, "DELETE", {});
        if (selectedName === profile.name) setSelectedName("");
        const spoolMessage = mailProfileSpoolCleanupMessage(payload);
        const tone: ToastTone = ["blocked", "failed", "missing"].includes(String(payload.spool?.status ?? "")) ? "warning" : "success";
        setToast(`Mail profile ${profile.name} deleted. Corpus ${payload.root_name ?? `mail-${profile.name}`} removed.${spoolMessage ? ` ${spoolMessage}` : ""}`, tone);
        await load();
      } catch (error) {
        setToast(`Could not delete mail profile ${profile.name}: ${errorMessage(error)}`);
      }
    });
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
    await runPendingAction(globalActionKey("corpus-sync"), "Syncing...", async () => {
      try {
        await sendJson("/api/crawl/sync", "POST", { dry_run: false });
        setToast("Corpus sync completed.");
        await load();
      } catch (error) {
        setToast(`Corpus sync failed: ${errorMessage(error)}`);
      }
    });
  }

  async function setCorpusWatch(enabled: boolean) {
    await runPendingAction(globalActionKey(enabled ? "corpus-watch-enable" : "corpus-watch-disable"), enabled ? "Enabling..." : "Disabling...", async () => {
      try {
        await sendJson("/api/crawl/watch", "POST", { enabled });
        setToast(enabled ? "Watch enabled." : "Watch disabled.");
        await load();
      } catch (error) {
        setToast(`Watch update failed: ${errorMessage(error)}`);
      }
    });
  }

  async function runRootSync(rootName: string, dryRun = false) {
    await runPendingAction(corpusRootActionKey(dryRun ? "dry-run" : "sync", rootName), dryRun ? "Checking..." : "Syncing...", async () => {
      try {
        await sendJson("/api/crawl/sync", "POST", { root_name: rootName, dry_run: dryRun });
        setToast(dryRun ? `Dry run completed for ${rootName}.` : `Sync completed for ${rootName}.`);
        await load();
      } catch (error) {
        setToast(`Root sync failed for ${rootName}: ${errorMessage(error)}`);
      }
    });
  }

  async function setRootWatch(rootName: string, enabled: boolean) {
    await runPendingAction(corpusRootActionKey(enabled ? "watch-enable" : "watch-disable", rootName), enabled ? "Enabling..." : "Disabling...", async () => {
      try {
        await sendJson("/api/crawl/watch", "POST", { root_name: rootName, enabled });
        setToast(enabled ? `Watch enabled for ${rootName}.` : `Watch disabled for ${rootName}.`);
        await load();
      } catch (error) {
        setToast(`Watch update failed for ${rootName}: ${errorMessage(error)}`);
      }
    });
  }

  async function runRootBackfill(rootName: string) {
    await runPendingAction(corpusRootActionKey("backfill", rootName), "Running...", async () => {
      try {
        await sendJson("/api/crawl/backfill", "POST", { kind: "all", limit: 10, workers: 1, root_name: rootName });
        setToast(`Backfill run completed for ${rootName}.`);
        await load();
      } catch (error) {
        setToast(`Backfill failed for ${rootName}: ${errorMessage(error)}`);
      }
    });
  }

  async function deleteSelectedRoot(root: RootSummary) {
    await runPendingAction(corpusRootActionKey("delete", root.name), "Deleting...", async () => {
      setDeleteRoot(null);
      try {
        await sendJson(`/api/crawl/roots/${encodeURIComponent(root.id ?? root.name)}?purge_index=true`, "DELETE", {});
        setSelectedRootName("");
        setToast(`Watched path ${root.name} deleted and index rows purged. Files on disk were not deleted.`);
        await load();
      } catch (error) {
        setToast(`Could not delete watched path ${root.name}: ${errorMessage(error)}`);
      }
    });
  }

  async function saveSetting(confirmed = false) {
    if (!settingEditor) return;
    if (requiresConfirmation(settingEditor) && !confirmed) {
      setConfirmSetting(settingEditor);
      return;
    }
    const setting = settingEditor;
    await runPendingAction(settingActionKey("save", setting.key), "Saving...", async () => {
      const value = parseSettingValue(settingValue, setting.value);
      await sendJson(`/api/settings/${encodeURIComponent(setting.key)}`, "PUT", {
        value,
        confirmed,
        reason: "dashboard update"
      });
      setConfirmSetting(null);
      setSettingEditor(null);
      setToast(`Setting ${setting.key} saved.`);
      await load();
    });
  }

  async function resetSetting(setting: SettingRow) {
    await runPendingAction(settingActionKey("reset", setting.key), "Resetting...", async () => {
      await sendJson(`/api/settings/${encodeURIComponent(setting.key)}/reset`, "POST", {});
      setToast(`Setting ${setting.key} reset.`);
      await load();
    });
  }

  async function applySettings(component?: string) {
    await runPendingAction(globalActionKey(component ? `settings-apply:${component}` : "settings-apply"), "Applying...", async () => {
      await sendJson("/api/settings/apply", "POST", { component: component ?? null });
      setToast(component ? `Apply request acknowledged for ${component}.` : "Apply requests acknowledged.");
      await load();
    });
  }

  async function loadReview() {
    setReviewLoading(true);
    try {
      const params = new URLSearchParams();
      params.set("review", reviewFilter);
      if (reviewStateFilter) params.set("state", reviewStateFilter);
      params.set("limit", "50");
      const captureParams = new URLSearchParams();
      captureParams.set("status", captureReviewStatus);
      captureParams.set("limit", "50");
      const [claims, capture, audit, policies, quality, govActions, govDigest, govPolicy] = await Promise.all([
        fetchRequiredJson<ClaimReviewPayload>(`/api/claims?${params.toString()}`),
        getJson<CaptureReviewPayload>(`/api/capture/review?${captureParams.toString()}`, { jobs: [] }),
        getJson<AuditPayload>("/api/audit?limit=50", []),
        getJson<RetentionPolicyPayload>("/api/retention/policies", { policies: [] }),
        getJson<RetentionQualityPayload>("/api/retention/quality?limit=25", { summary: {}, candidates: [] }),
        getJson<GovernanceActionsPayload>("/api/governance/actions?status=all&limit=50", { actions: [], telemetry: {} }),
        getJson<GovernanceDigestPayload>("/api/governance/digest", { digest: { summary: {}, recommendations: [] }, settings_mutated: false }),
        getJson<GovernancePolicyPayload>("/api/governance/policy", { policy: {}, settings_mutated: false })
      ]);
      const nextClaims = claims.claims ?? [];
      setClaimReview({ claims: nextClaims, counts: claims.counts ?? {} });
      setCaptureReview(capture);
      setCaptureReviewAudit(captureReviewAuditEvents(audit));
      setRetentionPolicies(policies);
      setRetentionQuality(quality);
      setGovernanceActions(govActions);
      setGovernanceDigest(govDigest);
      setGovernancePolicy(govPolicy);
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

  async function runGovernanceAutomation() {
    try {
      await sendJson("/api/governance/run", "POST", { mode: "shadow", limit: 25 });
      setToast("Governance shadow run completed.");
      await loadReview();
    } catch (error) {
      setToast(`Governance run failed: ${errorMessage(error)}`);
    }
  }

  async function applyGovernanceAction(action: GovernanceAction) {
    const actionId = action.id;
    if (!actionId) return;
    const rationale = window.prompt(`Rationale for applying ${action.action ?? "governance action"} ${actionId}`);
    if (!rationale?.trim()) return;
    if (!window.confirm(`Apply governance action ${actionId}?`)) return;
    try {
      await sendJson(`/api/governance/actions/${encodeURIComponent(actionId)}/apply`, "POST", { rationale: rationale.trim(), confirm: true });
      setToast(`Governance action ${actionId} applied.`);
      await loadReview();
    } catch (error) {
      setToast(`Governance apply failed: ${errorMessage(error)}`);
    }
  }

  async function recoverGovernanceAction(action: GovernanceAction) {
    const actionId = action.id;
    if (!actionId) return;
    const rationale = window.prompt(`Rationale for recovering ${actionId}`);
    if (!rationale?.trim()) return;
    if (!window.confirm(`Recover governance action ${actionId}?`)) return;
    try {
      await sendJson(`/api/governance/actions/${encodeURIComponent(actionId)}/recover`, "POST", { rationale: rationale.trim(), confirm: true });
      setToast(`Governance action ${actionId} recovered.`);
      await loadReview();
    } catch (error) {
      setToast(`Governance recovery failed: ${errorMessage(error)}`);
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

  async function ingestApprovedCaptureJobs() {
    try {
      await sendJson("/api/capture/review/ingest", "POST", {
        limit: 25,
        dry_run: false
      });
      setCaptureReviewStatus("completed");
      setToast("Approved capture review jobs ingested.");
      await loadReview();
    } catch (error) {
      setToast(`Capture ingestion failed: ${errorMessage(error)}`);
    }
  }

  async function runSearch(event: FormEvent) {
    event.preventDefault();
    const query = searchQuery.trim();
    if (!query) {
      setToast("Enter a search query first.");
      return;
    }
    await executeSearch(query, searchKind);
  }

  async function executeSearch(query: string, focus: string) {
    const filters = buildDashboardRetrievalFilters(focus, searchCurrentOnly, searchIncludeSuppressed);
    const payload = await sendJson<ExplainPayload>("/api/explain", "POST", { query, limit: 8, filters });
    setSearchResults(Array.isArray(payload.results) ? payload.results : []);
    setSearchFilterTrace(payload.filter_trace ?? {});
    setSearchSuppression(payload.suppression ?? {});
    setSearchOpen(true);
    setActiveTab("retrieval");
  }

  async function rerunSearchWithFocus(focus: string) {
    const query = searchQuery.trim();
    if (!query) {
      setToast("Enter a search query first.");
      return;
    }
    setSearchKind(focus);
    await executeSearch(query, focus);
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
    const link = diagnostic.links?.find((item) => normalizeTabId(item.tab));
    const tab = normalizeTabId(link?.tab);
    if (!tab) return;
    if (link.profile) setSelectedName(link.profile);
    if (link.root) setSelectedRootName(link.root);
    setActiveTab(tab);
  }

  async function runResultFileAction(detail: ResultDetail, action: "open" | "reveal") {
    if (!detail.asset_id) {
      setToast("No indexed asset id is available for this result.");
      return;
    }
    try {
      const payload = await sendJson<FileActionResponse>(`/api/corpus/assets/${encodeURIComponent(detail.asset_id)}/actions`, "POST", { action });
      const nextToast = fileActionToast(action, payload);
      if (nextToast) setToast(nextToast);
      else setToastState(null);
    } catch (error) {
      setToast(`File action failed: ${errorMessage(error)}`, "error");
    }
  }

  async function runJobFileAction(jobId: string, action: "open" | "reveal") {
    try {
      const payload = await sendJson<FileActionResponse>(`/api/dashboard/jobs/${encodeURIComponent(jobId)}/file-actions`, "POST", { action });
      const nextToast = fileActionToast(action, payload, action === "reveal" ? "Open containing folder" : undefined);
      if (nextToast) setToast(nextToast);
      else setToastState(null);
    } catch (error) {
      setToast(`Job file action failed: ${errorMessage(error)}`, "error");
    }
  }

  const health = state.health;
  const runtime = health.runtime ?? {};
  const databaseChecks = health.database?.checks ?? {};
  const apiDatabase = databaseChecks.service ?? runtime.postgresql ?? health.database;
  const hostDatabase = databaseChecks.host_published;
  const host = state.outlook.host ?? {};
  const hostStatus = host.status ?? "host_offline";
  const mailErrors = state.mail.errored_messages ?? 0;
  const blockedJobs = health.jobs?.blocked ?? 0;
  const oauthProfiles = state.mail.oauth?.profiles ?? [];
  const restartRows = restartSettings(state.settings);
  const currentToastTone = toast?.tone ?? "success";
  const selectedProfileSyncPending = selectedProfile ? pendingLabel(pendingActions, mailProfileActionKey("sync", selectedProfile.name)) : undefined;

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
            <StatusChip label="API DB" ok={apiDatabase?.ok} />
            <StatusChip label="API" ok />
            {hostDatabase && hostDatabase.required !== false && <StatusChip label="Host DB" ok={hostDatabase.ok} />}
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
            <button className="primary-action" type="button" title="Run the selected mail profile sync now" disabled={Boolean(selectedProfileSyncPending)} onClick={() => void requestProfileSync()}>
              {selectedProfileSyncPending ? <Clock3 size={17} /> : <RefreshCcw size={17} />}
              {selectedProfileSyncPending ?? "Sync Now"}
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
            {currentToastTone === "error" ? <AlertCircle size={18} /> : currentToastTone === "warning" ? <AlertTriangle size={18} /> : <CheckCircle2 size={18} />}
            <span>{toast.message}</span>
            <button type="button" aria-label="Dismiss notification" onClick={() => setToastState(null)}><X size={15} /></button>
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
            pendingActions={pendingActions}
            onAddProfile={() => setProfileDialog("new")}
            onEditProfile={setProfileDialog}
            onSelectProfile={(profile) => setSelectedName(profile.name)}
            onSyncProfile={(profile) => void requestProfileSync(profile)}
            onDeleteProfile={setDeleteProfile}
            onPostProcessDryRun={(profile) => void runPostProcessDryRun(profile)}
            onOAuthStart={(profile, clientPath) => void startGmailOAuth(profile, clientPath)}
            onOAuthPathSave={(profile, clientPath) => void saveGmailOAuthClientPath(profile, clientPath)}
            onErrorDetail={setErrorDetail}
          />
        )}

        {activeTab === "overview" && (
          <OverviewTab
            state={state}
            hostStatus={hostStatus}
            onErrorDetail={setErrorDetail}
          />
        )}

        {activeTab === "automation" && (
          <AutomationTab />
        )}

        {activeTab === "diagnostics" && (
          <DiagnosticsTab
            state={state}
            onErrorDetail={setErrorDetail}
            onCopyDiagnostic={(diagnostic) => void copyDiagnostic(diagnostic)}
            onNavigateDiagnostic={navigateDiagnostic}
          />
        )}

        {activeTab === "performance" && (
          <PerformanceTab
            state={state}
            selectedRoot={selectedRoot}
            modelActivityOffset={modelActivityOffset}
            includeControlPlaneActivity={showControlPlaneActivity}
            onIncludeControlPlaneActivityChange={updateControlPlaneActivity}
            onModelActivityPage={(offset) => void updateModelActivityPage(offset)}
          />
        )}

        {activeTab === "corpus" && (
          <CorpusTab
            state={state}
            selectedRoot={selectedRoot}
            pendingActions={pendingActions}
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
            health={state.health}
            hostStatus={hostStatus}
            restartRows={restartRows}
            pendingActions={pendingActions}
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
            onRerunDocsFiles={() => void rerunSearchWithFocus("docs")}
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
            governanceActions={governanceActions}
            governanceDigest={governanceDigest}
            governancePolicy={governancePolicy}
            graph={claimGraph}
            selectedClaim={selectedClaim}
            loading={reviewLoading}
            reviewFilter={reviewFilter}
            stateFilter={reviewStateFilter}
            captureStatus={captureReviewStatus}
            onReviewFilter={(value) => setReviewFilter(value)}
            onStateFilter={setReviewStateFilter}
            onCaptureStatus={(value) => setCaptureReviewStatus(value)}
            onSelectClaim={(claim) => setSelectedClaimId(claim.id)}
            onTransition={(claim, transition) => void transitionReviewClaim(claim, transition)}
            onCaptureDecision={openCaptureDecision}
            onCaptureIngest={() => void ingestApprovedCaptureJobs()}
            onRetentionPolicySave={(policy, update) => void saveRetentionPolicy(policy, update)}
            onGovernanceRun={() => void runGovernanceAutomation()}
            onGovernanceApply={(action) => void applyGovernanceAction(action)}
            onGovernanceRecover={(action) => void recoverGovernanceAction(action)}
            onRefresh={() => void loadReview()}
          />
        )}

        {activeTab === "jobs" && (
          <JobsTab
            state={state}
            jobFilters={jobFilters}
            jobSort={jobSort}
            onRefresh={() => void load()}
            onApplyJobFilters={(filters) => void applyJobFilters(filters)}
            onClearJobFilters={() => void clearJobFilters()}
            onApplyJobSort={(sort) => void applyJobSort(sort)}
            onPageJobHistory={(offset) => void pageJobHistory(offset)}
            onCancelOutlookRequest={(requestId) => void cancelOutlookRequest(requestId)}
            onCancelCorpusJob={(jobId) => void cancelCorpusJob(jobId)}
            onRetryCorpusJob={(jobId) => void retryCorpusJob(jobId)}
            onMarkCorpusJobForDeletion={(jobId) => void markCorpusJobForDeletion(jobId)}
            onRestoreCorpusJobDeletionRequest={(jobId) => void restoreCorpusJobDeletionRequest(jobId)}
            onJobFileAction={(jobId, action) => void runJobFileAction(jobId, action)}
            pendingActions={pendingActions}
          />
        )}
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
          body={`Delete watched path ${deleteRoot.name} and purge its indexed files, chunks, search-index records, jobs, crawl runs, and watcher state. This does not delete files from disk.`}
          confirmLabel="Delete watched path and purge index"
          pending={Boolean(pendingLabel(pendingActions, corpusRootActionKey("delete", deleteRoot.name)))}
          pendingLabel={pendingLabel(pendingActions, corpusRootActionKey("delete", deleteRoot.name))}
          onCancel={() => setDeleteRoot(null)}
          onConfirm={() => void deleteSelectedRoot(deleteRoot)}
        />
      )}

      {deleteProfile && (
        <ConfirmDialog
          title="Delete mail profile"
          body={`Delete mail profile ${deleteProfile.name}, mailbox corpus root mail-${deleteProfile.name}, search-index records, semantic duplicate metadata, managed mail sidecar files, and private spool files on disk when the configured path passes strict private mail-spool guards.`}
          confirmLabel="Delete profile and private spool"
          pending={Boolean(pendingLabel(pendingActions, mailProfileActionKey("delete", deleteProfile.name)))}
          pendingLabel={pendingLabel(pendingActions, mailProfileActionKey("delete", deleteProfile.name))}
          onCancel={() => setDeleteProfile(null)}
          onConfirm={() => void deleteSelectedProfile(deleteProfile)}
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
          pending={Boolean(pendingLabel(pendingActions, settingActionKey("save", confirmSetting.key)))}
          pendingLabel={pendingLabel(pendingActions, settingActionKey("save", confirmSetting.key))}
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
  pendingActions,
  onAddProfile,
  onEditProfile,
  onSelectProfile,
  onSyncProfile,
  onDeleteProfile,
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
  pendingActions: PendingActionState;
  onAddProfile: () => void;
  onEditProfile: (profile: MailProfile) => void;
  onSelectProfile: (profile: MailProfile) => void;
  onSyncProfile: (profile: MailProfile) => void;
  onDeleteProfile: (profile: MailProfile) => void;
  onPostProcessDryRun: (profile: MailProfile) => void;
  onOAuthStart: (profile: MailProfile, clientPath: string) => void;
  onOAuthPathSave: (profile: MailProfile, clientPath: string) => void;
  onErrorDetail: (error: string) => void;
}) {
  const activeOutlookCount = activeOutlookRequests(state.outlook.pending_requests).length;
  const hasOutlookProfiles = profiles.some((profile) => profile.source_type === "outlook_com") || activeOutlookCount > 0;
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
            pendingActions={pendingActions}
            onSelect={onSelectProfile}
            onSync={onSyncProfile}
            onEdit={onEditProfile}
            onDelete={onDeleteProfile}
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
            pendingActions={pendingActions}
          />
        </Panel>
      </section>

      <section className="lower-grid">
        <MailSchedulerPanel scheduler={state.mail.scheduler} />
        <MailPostProcessPanel mail={state.mail} selectedProfile={selectedProfile} pendingActions={pendingActions} onDryRun={onPostProcessDryRun} />
        <MailStatusPanel mail={state.mail} hostStatus={hostStatus} showOutlook={hasOutlookProfiles} />
        <MailErrorsPanel mail={state.mail} errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
        {hasOutlookProfiles && <OutlookHostPanel host={host} hostStatus={hostStatus} pending={activeOutlookCount} />}
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

function MailPostProcessPanel({ mail, selectedProfile, pendingActions, onDryRun }: { mail: MailStatus; selectedProfile?: MailProfile; pendingActions: PendingActionState; onDryRun: (profile: MailProfile) => void }) {
  const events = mail.post_process?.recent_events ?? [];
  const dryRunPending = selectedProfile ? pendingLabel(pendingActions, mailProfileActionKey("post-process-dry-run", selectedProfile.name)) : undefined;
  return (
    <Panel
      title="Post Process"
      action={selectedProfile ? (
        <button
          className="ghost-action compact"
          type="button"
          aria-label={`Dry run post process for ${selectedProfile.name}`}
          title={`Preview post-process commands for ${selectedProfile.name}`}
          disabled={Boolean(dryRunPending)}
          onClick={() => onDryRun(selectedProfile)}
        >
          {dryRunPending ? <Clock3 size={15} /> : <Play size={15} />} {dryRunPending ?? "Dry run"}
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

function isMailOperationalError(error: string): boolean {
  if (/mail-spool|corpus_|pdftoppm|paddleocr|ffprobe|ffmpeg|strict indexing|metadata-only/i.test(error)) {
    return false;
  }
  return /imap|oauth|gmail|outlook sync|outlook host|mail ingestion|mail profile|mail scheduler|blocked_auth/i.test(error);
}

function MailErrorsPanel({ mail, errors, onErrorDetail }: { mail: MailStatus; errors: string[]; onErrorDetail: (error: string) => void }) {
  const mailErrors = errors.filter(isMailOperationalError);
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

function OverviewTab({
  state,
  hostStatus,
  onErrorDetail
}: {
  state: LoadState;
  hostStatus: string;
  onErrorDetail: (error: string) => void;
}) {
  const databaseChecks = state.health.database?.checks ?? {};
  const apiDatabase = databaseChecks.service ?? state.health.runtime?.postgresql;
  const hostDatabase = databaseChecks.host_published;
  const runtimeRows = Object.entries(state.health.runtime ?? {}).filter(([key]) => key !== "postgresql");
  const hostAgent = state.health.host_agent;
  const codex = state.health.codex;
  const workers = state.health.workers;
  const attentionItems = overviewAttentionItems(state, hostStatus);
  const handledItems = overviewAutomationItems(state);
  const nextAction = overviewNextAction(attentionItems);
  return (
    <section className="tab-grid">
      <Panel title="System Overview">
        <div className="status-grid">
          <StatusTile label="Database" ok={state.health.database?.ok} message={state.health.database?.message} />
          {apiDatabase && <StatusTile label="API DB" ok={apiDatabase.ok} message={apiDatabase.message} />}
          {hostDatabase && hostDatabase.required !== false && <StatusTile label="Host DB" ok={hostDatabase.ok} message={hostDatabase.message} />}
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
      <Panel title="What needs attention">
        <div className="friendly-list">
          {attentionItems.map((item) => (
            <div className="friendly-item" key={`${item.label}-${item.detail}`}>
              <strong>{item.label}</strong>
              <span>{item.detail}</span>
            </div>
          ))}
        </div>
      </Panel>
      <Panel title="Flux handled automatically">
        <div className="friendly-list">
          {handledItems.map((item) => (
            <div className="friendly-item" key={`${item.label}-${item.detail}`}>
              <strong>{item.label}</strong>
              <span>{item.detail}</span>
            </div>
          ))}
        </div>
      </Panel>
      <Panel title="Next safe action">
        <p className="panel-note">{nextAction}</p>
      </Panel>
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
    </section>
  );
}

function AutomationTab() {
  const [status, setStatus] = useState<AutomationStatus>({ eligible_actions: [], manual_required: [], recent_actions: [] });
  const [runStatus, setRunStatus] = useState("");
  const [running, setRunning] = useState(false);
  const loadAutomation = useCallback(() => {
    return getJson<AutomationStatus>("/api/automation/status", { eligible_actions: [], manual_required: [], recent_actions: [] }).then((payload) => {
      setStatus(payload);
    });
  }, []);
  useEffect(() => {
    let cancelled = false;
    getJson<AutomationStatus>("/api/automation/status", { eligible_actions: [], manual_required: [], recent_actions: [] }).then((payload) => {
      if (!cancelled) setStatus(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  async function runGuardedPass() {
    setRunning(true);
    setRunStatus("Running guarded automation...");
    try {
      const result = await sendJson<{ summary?: Record<string, unknown>; actions?: AutomationAction[] }>("/api/automation/run", "POST", {
        mode: "guarded",
        dry_run: false,
        limit: 25
      });
      const applied = Number(result.summary?.applied ?? result.actions?.filter((action) => action.status === "applied").length ?? 0);
      setRunStatus(`Guarded automation completed: ${applied} action${applied === 1 ? "" : "s"} applied.`);
      await loadAutomation();
    } catch (error) {
      setRunStatus(`Guarded automation failed: ${errorMessage(error)}`);
    } finally {
      setRunning(false);
    }
  }
  const policy = status.policy ?? {};
  const recurring = status.recurring ?? {};
  const lastRun = status.last_run;
  const eligible = status.eligible_actions ?? [];
  const manual = status.manual_required ?? [];
  const recent = status.recent_actions ?? [];
  const recurringEnabled = recurring.enabled ?? Boolean(policy.enabled);
  return (
    <section className="tab-grid">
      <Panel
        title="Guarded Automation"
        action={<button className="small-primary" type="button" disabled={running} onClick={() => void runGuardedPass()}>Run guarded pass now</button>}
      >
        <div className="summary-cards">
          <Stat label="Mode" value="Guarded Auto" />
          <Stat label="Recurring" value={recurringEnabled ? "Enabled" : "Disabled"} />
          <Stat label="Last Run" value={lastRun?.status ? humanizeIdentifier(lastRun.status) : "Not run"} />
          <Stat label="Next Window" value={automationNextWindow(recurring, policy)} />
        </div>
        <p className="panel-note">Guarded automation runs only allowlisted, reversible or non-destructive actions. Settings changes, deletes, OAuth, host startup, and ambiguous work stay manual.</p>
        {runStatus && <p className="muted">{runStatus}</p>}
      </Panel>
      <Panel title="Eligible Actions">
        {eligible.length > 0 ? (
          <MiniTable rows={eligible.map((action) => [
            action.label ?? humanizeIdentifier(action.action ?? "action"),
            humanizeIdentifier(action.risk ?? "low"),
            action.reason ?? action.source ?? "Eligible guarded action"
          ])} />
        ) : (
          <p className="muted">No guarded action is currently eligible.</p>
        )}
      </Panel>
      <Panel title="Manual Required">
        {manual.length > 0 ? (
          <MiniTable rows={manual.slice(0, 10).map((action) => [
            action.label ?? humanizeIdentifier(action.action ?? "manual"),
            "manual",
            action.reason ?? "Operator decision required"
          ])} />
        ) : (
          <p className="muted">No manual-only items are queued.</p>
        )}
      </Panel>
      <Panel title="Automation Audit Trail">
        {recent.length > 0 ? (
          <div className="diagnostic-list">
            {recent.slice(0, 8).map((action) => (
              <div className="diagnostic-item" key={action.id ?? `${action.action}-${action.created_at}`}>
                <strong>{humanizeIdentifier(action.action ?? "automation action")}</strong>
                <span>{humanizeIdentifier(action.status ?? "observed")} / {humanizeIdentifier(action.risk ?? "low")}</span>
                <em>{action.source ?? "automation"}</em>
              </div>
            ))}
          </div>
        ) : (
          <p className="muted">No automation actions have been recorded yet.</p>
        )}
      </Panel>
    </section>
  );
}

function DiagnosticsTab({
  state,
  onErrorDetail,
  onCopyDiagnostic,
  onNavigateDiagnostic
}: {
  state: LoadState;
  onErrorDetail: (error: string) => void;
  onCopyDiagnostic: (diagnostic: ErrorDiagnostic) => void;
  onNavigateDiagnostic: (diagnostic: ErrorDiagnostic) => void;
}) {
  return (
    <section className="tab-grid">
      <ActionableDiagnostics diagnostics={state.health.recent_error_details ?? []} onCopy={onCopyDiagnostic} onNavigate={onNavigateDiagnostic} />
      <OperationalDiagnosticsPanel />
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
    </section>
  );
}

function PerformanceTab({
  state,
  selectedRoot,
  modelActivityOffset,
  includeControlPlaneActivity,
  onIncludeControlPlaneActivityChange,
  onModelActivityPage
}: {
  state: LoadState;
  selectedRoot?: RootSummary | MonitoredRoot;
  modelActivityOffset: number;
  includeControlPlaneActivity: boolean;
  onIncludeControlPlaneActivityChange: (include: boolean) => void;
  onModelActivityPage: (offset: number) => void;
}) {
  return (
    <section className="tab-grid">
      <OperatorEvidencePanel />
      <ModelActivityPanel
        activity={state.modelActivity}
        offset={modelActivityOffset}
        includeControlPlane={includeControlPlaneActivity}
        onIncludeControlPlaneChange={onIncludeControlPlaneActivityChange}
        onPage={onModelActivityPage}
      />
      <AccelerationPanel acceleration={state.health.acceleration} selectedRoot={selectedRoot} />
    </section>
  );
}

function CodeDiagnosticsPanel() {
  const [status, setStatus] = useState<CodeStatus | null>(null);
  const [feedbackCategory, setFeedbackCategory] = useState("missing_symbol");
  const [feedbackStatus, setFeedbackStatus] = useState("");
  const [codeSearchQuery, setCodeSearchQuery] = useState("build_invoice");
  const [codeRelationship, setCodeRelationship] = useState("call");
  const [codePathGlob, setCodePathGlob] = useState("");
  const [codeIncludeGenerated, setCodeIncludeGenerated] = useState(false);
  const [codeSearchStatus, setCodeSearchStatus] = useState("");
  const [codeSearchResults, setCodeSearchResults] = useState<CodeSearchResult[]>([]);
  const [symbolLookupQuery, setSymbolLookupQuery] = useState("OrderService.build_invoice");
  const [symbolLookupStatus, setSymbolLookupStatus] = useState("");
  const [symbolLookup, setSymbolLookup] = useState<CodeSymbolLookupResponse | null>(null);
  useEffect(() => {
    let cancelled = false;
    getJson<CodeStatus>("/api/code/status", { totals: {}, roots: [] }).then((payload) => {
      if (!cancelled) setStatus(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  const totals = status?.totals ?? {};
  const roots = status?.roots ?? [];
  const feedbackTotal = status?.feedback_summary?.totals?.event_count ?? 0;
  async function submitFeedback() {
    try {
      const result = await sendJson<{ id?: string }>("/api/code/feedback", "POST", {
        query: "dashboard-code-feedback",
        root_name: roots[0]?.root_name ?? null,
        result_count: 0,
        surface: "dashboard",
        miss_category: feedbackCategory,
        metadata: {}
      });
      setFeedbackStatus(result.id ? `Feedback ${result.id} recorded.` : "Feedback recorded.");
    } catch (error) {
      setFeedbackStatus(`Feedback failed: ${errorMessage(error)}`);
    }
  }
  async function runCodeSearch(event?: FormEvent<HTMLFormElement>) {
    event?.preventDefault();
    const query = codeSearchQuery.trim();
    if (!query) {
      setCodeSearchStatus("Enter a code search query.");
      return;
    }
    const params = new URLSearchParams({ query, limit: "5", include_generated: String(codeIncludeGenerated) });
    if (roots[0]?.root_name) params.set("root_name", roots[0].root_name);
    if (codeRelationship) params.set("relationship", codeRelationship);
    if (codePathGlob.trim()) params.set("path_glob", codePathGlob.trim());
    setCodeSearchStatus("Searching code...");
    try {
      const result = await fetchRequiredJson<CodeSearchResponse>(`/api/code/search?${params.toString()}`);
      const results = result.results ?? [];
      setCodeSearchResults(results);
      setCodeSearchStatus(`${results.length} code result${results.length === 1 ? "" : "s"}.`);
    } catch (error) {
      setCodeSearchStatus(`Code search failed: ${errorMessage(error)}`);
    }
  }
  async function runSymbolLookup(event?: FormEvent<HTMLFormElement>) {
    event?.preventDefault();
    const symbol = symbolLookupQuery.trim();
    if (!symbol) {
      setSymbolLookupStatus("Enter a symbol name.");
      return;
    }
    const params = new URLSearchParams({ symbol, include_references: "true", limit: "5" });
    if (roots[0]?.root_name) params.set("root_name", roots[0].root_name);
    setSymbolLookupStatus("Looking up symbol...");
    try {
      const result = await fetchRequiredJson<CodeSymbolLookupResponse>(`/api/code/symbols?${params.toString()}`);
      setSymbolLookup(result);
      const count = (result.matches?.length ?? 0) + (result.references?.length ?? 0);
      setSymbolLookupStatus(`${count} symbol row${count === 1 ? "" : "s"}.`);
    } catch (error) {
      setSymbolLookupStatus(`Symbol lookup failed: ${errorMessage(error)}`);
    }
  }
  const rows = roots.slice(0, 5).map((root) => {
    const languages = Object.entries(root.languages ?? {})
      .slice(0, 3)
      .map(([language, count]) => `${language} ${count}`)
      .join("; ") || "no language evidence";
    const parser = Object.entries(root.parser_statuses ?? {})
      .slice(0, 3)
      .map(([state, count]) => `${humanizeIdentifier(state)} ${count}`)
      .join("; ") || `${root.fallback_count ?? 0} fallback`;
    return [
      root.root_name ?? "root",
      humanizeIdentifier(root.health ?? "not_run"),
      `${root.symbol_count ?? 0} symbols / ${root.reference_count ?? 0} refs`,
      `${languages}; ${parser}`
    ] as [string, string, string, string];
  });
  const gapRows = (status?.gaps ?? []).slice(0, 5).map((gap) => [
    humanizeIdentifier(gap.category ?? "gap"),
    humanizeIdentifier(gap.priority ?? "medium"),
    String(gap.count ?? 0),
    gap.summary ?? "Review code retrieval gap"
  ] as [string, string, string, string]);
  const hotspotRows = roots
    .flatMap((root) => (root.slow_files ?? []).map((file) => ({ root: root.root_name ?? "root", ...file })))
    .slice(0, 4)
    .map((file) => [file.root, file.path ?? "file", `${file.duration_ms ?? 0}ms`] as [string, string, string]);
  const codeResultRows = codeSearchResults.slice(0, 5).map((result) => [
    codeResultLabel(result),
    humanizeIdentifier(result.relationship ?? result.relationship_kind ?? result.symbol_kind ?? "match"),
    `${result.language ?? "-"} ${result.path ?? ""}`.trim(),
    `${lineRangeLabel(result)}${result.is_generated ? " generated" : ""}`.trim() || "-"
  ] as [string, string, string, string]);
  const lookupRows = [
    ...(symbolLookup?.matches ?? []).map((result) => [
      codeResultLabel(result),
      humanizeIdentifier(result.symbol_kind ?? "definition"),
      `${result.language ?? "-"} ${result.path ?? ""}`.trim(),
      lineRangeLabel(result) || "-"
    ] as [string, string, string, string]),
    ...(symbolLookup?.references ?? []).map((result) => [
      result.source_symbol ?? codeResultLabel(result),
      humanizeIdentifier(result.relationship ?? result.relationship_kind ?? "reference"),
      `${result.language ?? "-"} ${result.path ?? ""}`.trim(),
      codeResultLabel(result)
    ] as [string, string, string, string])
  ];
  return (
    <Panel title="Code Diagnostics">
      <div className="summary-cards">
        <Stat label="Code Assets" value={String(totals.asset_count ?? 0)} />
        <Stat label="Symbols" value={String(totals.symbol_count ?? 0)} />
        <Stat label="References" value={String(totals.reference_count ?? 0)} />
        <Stat label="Fallbacks" value={String(totals.fallback_count ?? 0)} />
        <Stat label="Generated" value={String(totals.generated_count ?? 0)} />
      </div>
      {rows.length > 0 ? <MiniTable rows={rows} /> : <p className="muted">No code index diagnostics yet.</p>}
      {gapRows.length > 0 && <MiniTable rows={gapRows} />}
      {hotspotRows.length > 0 && <MiniTable rows={hotspotRows} />}
      <div className="settings-list">
        <form className="settings-row" onSubmit={(event) => void runCodeSearch(event)}>
          <strong>Code Search</strong>
          <input aria-label="Code search query" value={codeSearchQuery} onChange={(event) => setCodeSearchQuery(event.target.value)} />
          <select aria-label="Code relationship filter" value={codeRelationship} onChange={(event) => setCodeRelationship(event.target.value)}>
            <option value="call">Call</option>
            <option value="definition">Definition</option>
            <option value="import">Import</option>
            <option value="route">Route</option>
            <option value="test">Test</option>
            <option value="fixture">Fixture</option>
            <option value="config">Config</option>
            <option value="migration">Migration</option>
            <option value="notebook_cell">Notebook cell</option>
          </select>
          <input aria-label="Code path glob" value={codePathGlob} onChange={(event) => setCodePathGlob(event.target.value)} placeholder="src/*.py" />
          <label className="inline-check">
            <input type="checkbox" checked={codeIncludeGenerated} onChange={(event) => setCodeIncludeGenerated(event.target.checked)} />
            Generated
          </label>
          <button className="ghost-action compact" type="submit" aria-label="Run code search"><Search size={15} /> Search</button>
        </form>
        {codeResultRows.length > 0 && <MiniTable rows={codeResultRows} />}
        {codeSearchStatus && <p className="panel-note">{codeSearchStatus}</p>}
        <form className="settings-row" onSubmit={(event) => void runSymbolLookup(event)}>
          <strong>Symbol Lookup</strong>
          <input aria-label="Symbol lookup query" value={symbolLookupQuery} onChange={(event) => setSymbolLookupQuery(event.target.value)} />
          <button className="ghost-action compact" type="submit" aria-label="Lookup code symbol"><Search size={15} /> Lookup</button>
        </form>
        {lookupRows.length > 0 && <MiniTable rows={lookupRows} />}
        {symbolLookupStatus && <p className="panel-note">{symbolLookupStatus}</p>}
        <div className="settings-row">
          <strong>Code Feedback</strong>
          <span>{feedbackTotal} feedback events</span>
          <select aria-label="Code feedback category" value={feedbackCategory} onChange={(event) => setFeedbackCategory(event.target.value)}>
            <option value="missing_symbol">Missing symbol</option>
            <option value="wrong_root">Wrong root</option>
            <option value="wrong_relationship">Wrong relationship</option>
            <option value="parser_fallback">Parser fallback</option>
            <option value="ranking_order">Ranking order</option>
            <option value="stale_generated">Stale generated</option>
            <option value="other">Other</option>
          </select>
          <button className="ghost-action compact" type="button" onClick={() => void submitFeedback()}>Submit code feedback</button>
        </div>
        {feedbackStatus && <p className="panel-note">{feedbackStatus}</p>}
      </div>
    </Panel>
  );
}

function OperatorEvidencePanel() {
  const [evidence, setEvidence] = useState<OperatorEvidence | null>(null);
  useEffect(() => {
    let cancelled = false;
    getJson<OperatorEvidence>("/api/acceleration/evidence", { settings_mutated: false, gates: {}, top_blockers: [], code_gaps: [] }).then((payload) => {
      if (!cancelled) setEvidence(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  const gates = evidence?.gates ?? {};
  const gateRows = Object.entries(gates).map(([name, gate]) => [
    humanizeIdentifier(name),
    humanizeIdentifier(gate.state ?? "hold"),
    gate.reason ?? "Evidence required"
  ] as [string, string, string]);
  const readiness = evidence?.root_readiness ?? {};
  const blockerRows = (evidence?.top_blockers ?? []).slice(0, 4).map((item) => [
    item.root_name ?? item.section ?? "evidence",
    humanizeIdentifier(item.severity ?? "info"),
    item.summary ?? "Review operator evidence"
  ] as [string, string, string]);
  const codeRows = (evidence?.code_gaps ?? []).slice(0, 4).map((gap) => [
    humanizeIdentifier(gap.category ?? "gap"),
    String(gap.count ?? 0),
    gap.summary ?? "Review code diagnostic evidence"
  ] as [string, string, string]);
  return (
    <Panel title="Operator Evidence">
      <div className="summary-cards">
        <Stat label="Readiness" value={humanizeIdentifier(evidence?.readiness ?? "not_run")} />
        <Stat label="Ready Roots" value={String(readiness.ready ?? 0)} />
        <Stat label="Partial Roots" value={String(readiness.partial ?? 0)} />
        <Stat label="Settings Mutated" value={evidence?.settings_mutated ? "true" : "false"} />
      </div>
      {gateRows.length > 0 ? <MiniTable rows={gateRows} /> : <p className="muted">No operator gate evidence yet.</p>}
      {blockerRows.length > 0 && <MiniTable rows={blockerRows} />}
      {codeRows.length > 0 && <MiniTable rows={codeRows} />}
    </Panel>
  );
}

function ModelActivityPanel({
  activity,
  offset,
  includeControlPlane,
  onIncludeControlPlaneChange,
  onPage
}: {
  activity: ModelActivityPayload;
  offset: number;
  includeControlPlane: boolean;
  onIncludeControlPlaneChange: (include: boolean) => void;
  onPage: (offset: number) => void;
}) {
  const scheduler = activity.scheduler ?? {};
  const events = [...(activity.events ?? [])].sort(modelActivityNewestFirst);
  const visibleEvents = includeControlPlane ? events : events.filter((event) => !isControlPlaneActivity(event.activity_class));
  const clientFiltered = visibleEvents.length !== events.length;
  const safeLimit = Math.max(1, numberFromUnknown(activity.limit) ?? MODEL_ACTIVITY_PAGE_LIMIT);
  const safeOffset = Math.max(0, offset);
  const totalCount = clientFiltered ? visibleEvents.length : Math.max(numberFromUnknown(activity.total_count) ?? visibleEvents.length, visibleEvents.length);
  const pageCount = numberFromUnknown(activity.page_count) ?? (totalCount > 0 ? Math.max(1, Math.ceil(totalCount / safeLimit)) : 0);
  const currentPage = pageCount > 0 ? Math.min(pageCount, Math.floor(safeOffset / safeLimit) + 1) : 0;
  const pageItems = jobPageItems(currentPage, pageCount);
  const pageStart = totalCount > 0 ? safeOffset + 1 : 0;
  const pageEnd = totalCount > 0 ? Math.min(safeOffset + visibleEvents.length, totalCount) : 0;
  const hasNext = Boolean(activity.has_next ?? (safeOffset + visibleEvents.length < totalCount));
  const visibleActivity = {
    ...activity,
    events: visibleEvents,
    recent_count: visibleEvents.length,
    active_count: visibleEvents.filter((event) => event.status === "running").length
  };
  const serviceRows = modelActivityServiceRows(visibleEvents).slice(0, 6);
  const classRows = modelActivityClassRows(visibleEvents).slice(0, 6).map((row) => [
    modelActivityClassLabel(row.activity_class),
    `${row.count} event${row.count === 1 ? "" : "s"}`,
    "activity class"
  ] as [string, string, string]);
  const eventRows = visibleEvents.slice(0, 8).map((event) => [
    event.endpoint ?? event.action ?? "model activity",
    `${event.service ?? "service"} / ${humanizeIdentifier(event.status ?? "observed")}`,
    modelActivityEventDetail(event),
    modelActivityEventTiming(event)
  ] as [string, string, string, string]);
  const residentRows = (scheduler.resident_models ?? []).slice(0, 5).map((model) => [
    `${model.service ?? "service"} resident`,
    model.model ?? "resident model",
    [model.task_type ? humanizeIdentifier(model.task_type) : null, model.last_used_at ? `last ${formatDate(model.last_used_at)}` : null].filter(Boolean).join("; ") || "resident"
  ] as [string, string, string]);
  const failures = visibleEvents.filter(isModelActivityIssue).slice(0, 4);
  return (
    <Panel
      title="Model activity"
      action={(
        <label className="inline-check">
          <input
            aria-label="Show control-plane diagnostics"
            type="checkbox"
            checked={includeControlPlane}
            onChange={(event) => onIncludeControlPlaneChange(event.target.checked)}
          />
          Control plane
        </label>
      )}
    >
      <div className="summary-cards">
        <Stat label="Activity" value={modelActivityCountText(visibleActivity)} />
        <Stat label="Last Event" value={formatDate(activity.last_event_at)} />
        <Stat label="Scheduler Mode" value={scheduler.mode ? humanizeIdentifier(scheduler.mode) : "Unknown"} />
        <Stat label="GPU Memory" value={formatGpuMemory(scheduler.live_gpu_memory)} />
      </div>
      <div className="settings-list">
        <div className="settings-row">
          <strong>Scheduler</strong>
          <span>{formatSchedulerLeaseCounts(scheduler)}</span>
          <em>{`${scheduler.recent_count ?? 0} recent`}</em>
        </div>
        <div className="settings-row">
          <strong>Scheduler pressure</strong>
          <span>{formatSchedulerRejections(scheduler)}</span>
          <em>{formatSchedulerEvictions(scheduler)}</em>
        </div>
      </div>
      {serviceRows.length > 0 ? <MiniTable label="Model service activity" rows={serviceRows} /> : <p className="muted">No recent model service activity.</p>}
      {classRows.length > 0 ? <MiniTable label="Model activity classes" rows={classRows} /> : null}
      {residentRows.length > 0 ? <MiniTable label="Resident models" rows={residentRows} /> : null}
      {eventRows.length > 0 ? <MiniTable label="Model activity events" rows={eventRows} /> : null}
      <div className="job-pager model-activity-pager" aria-label="Model activity paging">
        <span>{pageStart}-{pageEnd} of {totalCount} model activity event{totalCount === 1 ? "" : "s"}</span>
        <button className="ghost-action compact" type="button" aria-label="Previous model activity page" disabled={safeOffset <= 0} onClick={() => onPage(Math.max(0, safeOffset - safeLimit))}>
          <ChevronLeft size={15} /> Previous
        </button>
        {pageItems.length > 0 && (
          <div className="job-page-numbers" aria-label="Model activity pages">
            {pageItems.map((item) => {
              if (typeof item !== "number") {
                return <span className="job-page-ellipsis" aria-hidden="true" key={item}>...</span>;
              }
              const isCurrent = item === currentPage;
              return (
                <button
                  className="job-page-button"
                  type="button"
                  key={item}
                  aria-current={isCurrent ? "page" : undefined}
                  aria-label={isCurrent ? `Current model activity page ${item}` : `Go to model activity page ${item}`}
                  disabled={isCurrent}
                  onClick={() => onPage((item - 1) * safeLimit)}
                >
                  {item}
                </button>
              );
            })}
          </div>
        )}
        <button className="ghost-action compact" type="button" aria-label="Next model activity page" disabled={!hasNext} onClick={() => onPage(safeOffset + safeLimit)}>
          Next <ChevronRight size={15} />
        </button>
      </div>
      {failures.length > 0 ? (
        <div className="settings-list">
          {failures.map((event) => (
            <div className="settings-row" key={event.id ?? `${event.service}-${event.started_at}`}>
              <strong>{event.error_class ?? humanizeIdentifier(event.status ?? "failure")}</strong>
              <span>{event.error_message ?? "Failure recorded"}</span>
              <em>{modelActivityIssueLabel(event)}</em>
            </div>
          ))}
        </div>
      ) : null}
    </Panel>
  );
}

function OperationalDiagnosticsPanel() {
  const [diagnostics, setDiagnostics] = useState<OperationalDiagnostics | null>(null);
  const [rootFilter, setRootFilter] = useState("docs");
  const [statusFilter, setStatusFilter] = useState("blocked_missing_dependency");
  const [familyFilter, setFamilyFilter] = useState("office");
  const [includeDetails, setIncludeDetails] = useState(true);
  const [actionStatus, setActionStatus] = useState("");
  const loadDiagnostics = useCallback(() => {
    const params = new URLSearchParams();
    if (rootFilter) params.set("root_name", rootFilter);
    if (statusFilter) params.set("status", statusFilter);
    if (familyFilter) params.set("family", familyFilter);
    if (includeDetails) params.set("include_details", "true");
    const query = params.toString();
    return getJson<OperationalDiagnostics>(`/api/diagnostics/all${query ? `?${query}` : ""}`, { section: "all", counts: {}, sections: {}, items: [] }).then((payload) => {
      setDiagnostics(payload);
    });
  }, [familyFilter, includeDetails, rootFilter, statusFilter]);
  const runDiagnosticAction = useCallback(async (action: DiagnosticAction) => {
    if (action.requires_confirmation && !window.confirm(`Run ${action.label ?? action.id ?? "diagnostic action"}?`)) return;
    setActionStatus("Running diagnostic action...");
    try {
      await sendJson<Record<string, unknown>>(action.endpoint ?? "/api/diagnostics/actions", action.method ?? "POST", action.payload ?? {});
      setActionStatus(`${action.label ?? action.id ?? "Diagnostic action"} finished.`);
      await loadDiagnostics();
    } catch (error) {
      setActionStatus(`Diagnostic action failed: ${errorMessage(error)}`);
    }
  }, [loadDiagnostics]);
  useEffect(() => {
    let cancelled = false;
    getJson<OperationalDiagnostics>("/api/diagnostics/all", { section: "all", counts: {}, sections: {}, items: [] }).then((payload) => {
      if (!cancelled) setDiagnostics(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  const counts = diagnostics?.counts ?? {};
  const workerFamilies = diagnostics?.sections?.workers?.families ?? [];
  const diagnosticItems = (diagnostics?.items ?? []).slice(0, 8);
  const rows: MiniTableRow[] = [
    ["Watcher events", "recent", String(counts.watcher_events ?? 0)],
    ["Worker families", "status", String(counts.worker_families ?? 0)],
    ["Blocked jobs", "queue", String(counts.blocked_jobs ?? 0)],
    ["Mail sync runs", "recent", String(counts.mail_sync_runs ?? 0)]
  ];
  const workerRows = workerFamilies.slice(0, 4).map((family) => [
    String(family.family ?? "family"),
    `${family.pending ?? 0} pending`,
    `${family.blocked_locked ?? 0} blocked locks`
  ] as [string, string, string]);
  return (
    <Panel title="Operational Diagnostics">
      <div className="settings-row">
        <strong>Filters</strong>
        <input aria-label="Diagnostic root filter" value={rootFilter} onChange={(event) => setRootFilter(event.target.value)} />
        <input aria-label="Diagnostic status filter" value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)} />
        <input aria-label="Diagnostic family filter" value={familyFilter} onChange={(event) => setFamilyFilter(event.target.value)} />
        <label className="inline-check">
          <input type="checkbox" checked={includeDetails} onChange={(event) => setIncludeDetails(event.target.checked)} />
          Details
        </label>
        <button className="ghost-action compact" type="button" onClick={() => void loadDiagnostics()}>Apply diagnostic filters</button>
      </div>
      <MiniTable rows={rows} />
      {diagnosticItems.length > 0 && (
        <div className="diagnostics-list">
          {diagnosticItems.map((item, index) => (
            <div className="diagnostic-card" key={`${item.section ?? "item"}-${item.status ?? "status"}-${index}`}>
              <div className="diagnostic-head">
                <span className={`severity ${item.severity ?? "info"}`}>{humanizeIdentifier(item.severity ?? "info")}</span>
                <strong>{item.summary ?? item.follow_up_command ?? "Diagnostic item"}</strong>
              </div>
              <div className="diagnostic-meta">
                <span>{item.section ?? "section"}</span>
                <span>{item.status ?? "status"}</span>
                {item.root_name && <span>{item.root_name}</span>}
                {item.family && <span>{item.family}</span>}
              </div>
              {includeDetails && item.evidence && <code>{JSON.stringify(item.evidence)}</code>}
              {item.follow_up_command && <p className="muted">{item.follow_up_command}</p>}
              {(item.remediation_actions ?? []).length > 0 && (
                <div className="row-actions">
                  {(item.remediation_actions ?? []).map((action) => (
                    <button
                      className="ghost-action compact"
                      key={action.id ?? action.label}
                      type="button"
                      onClick={() => void runDiagnosticAction(action)}
                    >
                      {action.label ?? humanizeIdentifier(action.id ?? "action")}
                    </button>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
      {actionStatus && <p className="muted">{actionStatus}</p>}
      {workerRows.length > 0 ? <MiniTable rows={workerRows} /> : <p className="muted">No worker diagnostic rows yet.</p>}
    </Panel>
  );
}

function AccelerationPanel({ acceleration, selectedRoot }: { acceleration?: AccelerationStatus; selectedRoot?: RootSummary | MonitoredRoot }) {
  const [benchmarkStatus, setBenchmarkStatus] = useState("");
  const [benchmarkRunning, setBenchmarkRunning] = useState(false);
  const [benchmarkResult, setBenchmarkResult] = useState<BenchmarkRunResponse | null>(null);
  const [reliabilityStatus, setReliabilityStatus] = useState<ReliabilityStatus | null>(null);
  const [rootReliability, setRootReliability] = useState<RootReliabilityCard | null>(null);
  const [reliabilityRoots, setReliabilityRoots] = useState<ReliabilityRootsStatus | null>(null);
  const capabilities = acceleration?.capabilities ?? {};
  const cache = acceleration?.cache ?? {};
  const families = acceleration?.worker_families ?? [];
  const docker = acceleration?.docker;
  const watcherBackend = capabilities.watcher_backend;
  const benchmarkHistory = acceleration?.benchmarks?.history ?? [];
  const benchmarkDiagnostics = benchmarkResult?.diagnostics ?? [];
  const benchmarkCandidates = benchmarkResult?.recommendations?.candidates ?? [];
  const reliabilityChecks = reliabilityStatus?.checks ?? [];
  const reliabilityCandidates = reliabilityStatus?.candidates ?? [];
  const reliabilityRootRows = (reliabilityRoots?.roots ?? []).slice(0, 6).map((root) => [
    root.root_name ?? "root",
    humanizeIdentifier(root.readiness ?? "not_run"),
    root.latest_benchmark?.id ? `benchmark ${root.latest_benchmark.id}` : "no scoped benchmark",
    root.required_action ?? "Review reliability evidence"
  ] as [string, string, string, string]);
  useEffect(() => {
    let cancelled = false;
    getJson<ReliabilityStatus>("/api/acceleration/reliability", { readiness: "not_run", checks: [], candidates: [] }).then((payload) => {
      if (!cancelled) setReliabilityStatus(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  useEffect(() => {
    let cancelled = false;
    getJson<ReliabilityRootsStatus>("/api/acceleration/reliability/roots", { roots: [], totals: {}, settings_mutated: false }).then((payload) => {
      if (!cancelled) setReliabilityRoots(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);
  useEffect(() => {
    let cancelled = false;
    if (!selectedRoot?.name) {
      setRootReliability(null);
      return () => {
        cancelled = true;
      };
    }
    getJson<RootReliabilityCard>(`/api/acceleration/reliability/root/${encodeURIComponent(selectedRoot.name)}`, { root_name: selectedRoot.name, readiness: "not_run" }).then((payload) => {
      if (!cancelled) setRootReliability(payload);
    });
    return () => {
      cancelled = true;
    };
  }, [selectedRoot?.name]);
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
    if (hasEmbeddingTelemetry) parts.push(`Search index ${embeddingVectors} vectors; ${embeddingSkipped} skipped; ${embeddingBatches} batches; cache ${embeddingHits} hit / ${embeddingMisses} miss`);
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
  const dockerRows = (docker?.containers ?? []).slice(0, 8).map((container) => [
    container.service ?? container.container_name ?? "container",
    humanizeIdentifier(container.status ?? "unknown"),
    dockerContainerResourceSummary(container)
  ] as [string, string, string]);
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
  async function runBenchmarkScenario(scenario: "standard" | "reliability" | "host_cloud" | "cache_readiness" | "tuning") {
    const base = { fixture: "all", files: 10, passes: 2, workers: 1, family: "all", scenario };
    const request =
      scenario === "reliability"
        ? { ...base, mode: "all", scope: "synthetic" }
        : scenario === "host_cloud"
          ? { ...base, mode: "scan", scope: "root", root_name: selectedRoot?.name ?? null, max_files: 100 }
          : scenario === "cache_readiness"
            ? { ...base, mode: "model", scope: "synthetic", passes: 1 }
            : { ...base, mode: "scan", scope: "synthetic" };
    try {
      setBenchmarkRunning(true);
      setBenchmarkStatus(`${humanizeIdentifier(scenario)} benchmark queued...`);
      const result = await sendJson<BenchmarkRunResponse>("/api/acceleration/benchmarks/run", "POST", request);
      setBenchmarkResult(result);
      setBenchmarkStatus(`${humanizeIdentifier(result.scenario ?? scenario)} benchmark recorded.`);
    } catch (error) {
      setBenchmarkStatus(`Benchmark failed: ${errorMessage(error)}`);
    } finally {
      setBenchmarkRunning(false);
    }
  }
  async function runReliabilityGate() {
    const request = selectedRoot?.name
      ? { scope: "root", root_name: selectedRoot.name, max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true }
      : { scope: "synthetic", max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true };
    try {
      setBenchmarkRunning(true);
      setBenchmarkStatus("Reliability gate queued...");
      const result = await sendJson<ReliabilityStatus>("/api/acceleration/reliability/run", "POST", request);
      setReliabilityStatus(result);
      setBenchmarkStatus(`Reliability gate ${humanizeIdentifier(result.readiness ?? "observed").toLowerCase()}.`);
    } catch (error) {
      setBenchmarkStatus(`Reliability gate failed: ${errorMessage(error)}`);
    } finally {
      setBenchmarkRunning(false);
    }
  }
  async function runAllRootsReliabilityGate() {
    const request = {
      scope: "all_roots",
      max_files: 1000,
      passes: 2,
      include_cache_readiness: false,
      include_tuning: true,
      evidence_level: "full"
    };
    try {
      setBenchmarkRunning(true);
      setBenchmarkStatus("All-root reliability gate queued...");
      const result = await sendJson<ReliabilityRootsStatus>("/api/acceleration/reliability/run", "POST", request);
      setReliabilityRoots(result);
      setBenchmarkStatus(`All-root reliability checked ${result.roots?.length ?? 0} roots.`);
    } catch (error) {
      setBenchmarkStatus(`All-root reliability failed: ${errorMessage(error)}`);
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
        <div className="settings-row">
          <strong>Container resources</strong>
          <span>{dockerResourceOverview(docker)}</span>
          <em>{dockerResourceMemoryOverview(docker)}</em>
        </div>
      </div>
      {dockerRows.length > 0 ? <MiniTable rows={dockerRows} /> : <p className="muted">No Docker container resource telemetry yet.</p>}
      {familyRows.length > 0 ? <MiniTable rows={familyRows} /> : <p className="muted">No worker-family telemetry yet.</p>}
      {backpressureRows.length > 0 && (
        <div className="settings-list">
          <div className="settings-row"><strong>Family Backpressure</strong><span>{backpressureRows.length} families reporting pressure or parser/manifest telemetry</span><em>debug</em></div>
          <MiniTable rows={backpressureRows} />
        </div>
      )}
      <div className="settings-list">
        <div className="settings-row">
          <strong>Reliability Gate</strong>
          <span>{humanizeIdentifier(reliabilityStatus?.readiness ?? "not_run")}</span>
          <div className="button-row">
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runReliabilityGate()}>
              <Play size={15} /> Run reliability gate
            </button>
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runAllRootsReliabilityGate()}>
              <Play size={15} /> Run all roots
            </button>
          </div>
        </div>
        <div className="settings-row">
          <strong>Reliability Matrix</strong>
          <span>{reliabilityRoots?.totals?.ready ?? 0} ready / {reliabilityRoots?.totals?.partial ?? 0} partial / {reliabilityRoots?.totals?.not_run ?? 0} not run</span>
          <em>{reliabilityRoots?.settings_mutated ? "settings changed" : "settings_mutated false"}</em>
        </div>
        {reliabilityRootRows.length > 0 ? <MiniTable rows={reliabilityRootRows} /> : <p className="panel-note">No monitored-root reliability evidence yet.</p>}
        {rootReliability && (
          <div className="settings-row">
            <strong>Selected Root</strong>
            <span>{rootReliability.root_name ?? selectedRoot?.name} / {String(rootReliability.readiness ?? "not_run").toLowerCase()}</span>
            <em>{rootReliability.latest_benchmark?.id ? `benchmark ${rootReliability.latest_benchmark.id}` : "no scoped benchmark"}</em>
          </div>
        )}
        {reliabilityChecks.slice(0, 4).map((check) => (
          <div className="settings-row" key={`reliability-${check.check}`}>
            <strong>{humanizeIdentifier(check.check ?? "check")}</strong>
            <span>{check.summary ?? "reliability evidence recorded"}</span>
            <em>{humanizeIdentifier(check.status ?? "observed")}</em>
          </div>
        ))}
        {reliabilityCandidates.length > 0 && (
          <MiniTable rows={reliabilityCandidates.slice(0, 4).map((candidate) => [
            String(candidate.setting ?? "setting"),
            String(candidate.evidence_state ?? "needs_review").replace(/[_-]+/g, " "),
            candidate.follow_up_command ?? "manual follow-up"
          ] as [string, string, string])} />
        )}
        <div className="settings-row">
          <strong>Benchmark History</strong>
          <span>{benchmarkRows.length} recent synthetic runs</span>
          <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runBenchmarkScenario("standard")}>
            <Play size={15} /> Run scan benchmark
          </button>
        </div>
        <div className="settings-row">
          <strong>Scenario Runners</strong>
          <span>{selectedRoot?.name ? `host/cloud root ${selectedRoot.name}` : "host/cloud needs one monitored root"}</span>
          <div className="button-row">
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runBenchmarkScenario("reliability")}>
              <Play size={15} /> Run reliability diagnostics
            </button>
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning || !selectedRoot?.name} onClick={() => void runBenchmarkScenario("host_cloud")}>
              <Play size={15} /> Run host/cloud calibration
            </button>
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runBenchmarkScenario("cache_readiness")}>
              <Play size={15} /> Run cache readiness
            </button>
            <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runBenchmarkScenario("tuning")}>
              <Play size={15} /> Run tuning diagnostics
            </button>
          </div>
        </div>
        {benchmarkRows.length > 0 ? <MiniTable rows={benchmarkRows} /> : <p className="panel-note">No synthetic benchmark history yet.</p>}
        {benchmarkResult && (
          <div className="settings-list">
            <div className="settings-row">
              <strong>Scenario Status</strong>
              <span>{humanizeIdentifier(benchmarkResult.scenario ?? "standard")} / {humanizeIdentifier(benchmarkResult.mode ?? "scan")}</span>
              <em>{benchmarkResult.recommendations?.settings_mutated ? "settings changed" : "settings_mutated false"}</em>
            </div>
            {benchmarkDiagnostics.slice(0, 4).map((diagnostic) => (
              <div className="settings-row" key={`${diagnostic.scenario}-${diagnostic.check}`}>
                <strong>{humanizeIdentifier(diagnostic.check ?? "diagnostic")}</strong>
                <span>{diagnostic.summary ?? "diagnostic recorded"}</span>
                <em>{humanizeIdentifier(diagnostic.status ?? "observed")}</em>
              </div>
            ))}
            {benchmarkCandidates.length > 0 && (
              <>
                <div className="settings-row">
                  <strong>Manual candidates</strong>
                  <span>{benchmarkCandidates.length} setting candidate{benchmarkCandidates.length === 1 ? "" : "s"} from diagnostic evidence</span>
                  <em>no auto-apply</em>
                </div>
                <MiniTable rows={benchmarkCandidates.slice(0, 4).map((candidate) => [
                  String(candidate.setting ?? "setting"),
                  `current ${candidate.current ?? "-"} -> candidate ${candidate.candidate ?? "-"}`,
                  candidate.requires_manual_apply ? "manual apply" : "review"
                ] as [string, string, string])} />
              </>
            )}
          </div>
        )}
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

function dockerResourceOverview(docker?: DockerResourceStatus): string {
  const totals = docker?.totals ?? {};
  const running = totals.running ?? (docker?.containers ?? []).filter((container) => container.running).length;
  const reported = totals.reported ?? docker?.containers?.length ?? 0;
  return `${running} running / ${reported} reported`;
}

function dockerResourceMemoryOverview(docker?: DockerResourceStatus): string {
  const totals = docker?.totals ?? {};
  const used = formatBytes(totals.memory_usage_bytes);
  const limit = formatBytes(totals.memory_limit_bytes, "unbounded");
  return `memory ${used} / ${limit}`;
}

function dockerContainerResourceSummary(container: DockerContainerResource): string {
  const name = container.container_name ?? container.service ?? "container";
  const cpu = container.cpu_percent == null ? "CPU unknown" : `CPU ${formatCompactNumber(container.cpu_percent)}%`;
  const memoryLimit = container.memory_limit_bytes ?? container.memory_stats_limit_bytes;
  const memoryPercent = container.memory_percent == null ? "" : ` (${formatCompactNumber(container.memory_percent)}%)`;
  const memory = `memory ${formatBytes(container.memory_usage_bytes)} / ${formatBytes(memoryLimit, "unbounded")}${memoryPercent}`;
  const writable = container.size_rw_bytes == null ? null : `writable ${formatBytes(container.size_rw_bytes)}`;
  const blockIo =
    container.block_io_read_bytes == null && container.block_io_write_bytes == null
      ? null
      : `block I/O ${formatBytes(container.block_io_read_bytes)} / ${formatBytes(container.block_io_write_bytes)}`;
  return [name, cpu, memory, writable, blockIo].filter(Boolean).join("; ");
}

function formatBytes(value: number | null | undefined, zeroLabel = "0 B"): string {
  if (value == null || Number.isNaN(value)) return "unknown";
  if (value === 0) return zeroLabel;
  const units = ["B", "KB", "MB", "GB", "TB"];
  let amount = Math.abs(value);
  let unitIndex = 0;
  while (amount >= 1024 && unitIndex < units.length - 1) {
    amount /= 1024;
    unitIndex += 1;
  }
  const sign = value < 0 ? "-" : "";
  return `${sign}${formatCompactNumber(amount)} ${units[unitIndex]}`;
}

function formatCompactNumber(value: number): string {
  return value.toFixed(2).replace(/\.?0+$/, "");
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
  pendingActions,
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
  pendingActions: PendingActionState;
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
  const syncAllPending = pendingLabel(pendingActions, globalActionKey("corpus-sync"));
  const enableAllPending = pendingLabel(pendingActions, globalActionKey("corpus-watch-enable"));
  const disableAllPending = pendingLabel(pendingActions, globalActionKey("corpus-watch-disable"));
  return (
    <section className="tab-grid corpus-tab">
      <Panel title="Corpus Monitor" action={<button className="small-primary" type="button" title="Add a monitored root path for recursive crawl and watch" onClick={onAddRoot}><Plus size={15} /> Add Watched Path</button>}>
        <div className="corpus-actions">
          <button className="small-primary" type="button" title="Run a crawl sync for all configured roots" disabled={Boolean(syncAllPending)} onClick={onSync}>{syncAllPending ? <Clock3 size={15} /> : <RefreshCcw size={15} />} {syncAllPending ?? "Sync all"}</button>
          <button className="ghost-action compact" type="button" title="Reload dashboard crawl state" onClick={onRefresh}>Refresh</button>
          <button className="ghost-action compact" type="button" title="Enable watch mode for every monitored root" disabled={Boolean(enableAllPending)} onClick={() => onWatch(true)}>{enableAllPending ?? "Enable all watch"}</button>
          <button className="ghost-action compact" type="button" title="Disable watch mode without deleting roots" disabled={Boolean(disableAllPending)} onClick={() => onWatch(false)}>{disableAllPending ?? "Disable all watch"}</button>
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
            pendingActions={pendingActions}
            onSelect={onSelectRoot}
            onSync={onRootSync}
            onWatch={onRootWatch}
            onEdit={onEditRoot}
            onDelete={onDeleteRoot}
            onBackfill={onRootBackfill}
          />
        </Panel>
        <Panel title="Root Details" action={selectedRoot ? <RootStateBadge state={selectedRoot.state} /> : undefined}>
          <RootInspector root={selectedRoot} pendingActions={pendingActions} onEdit={onEditRoot} onDelete={onDeleteRoot} onBackfill={onRootBackfill} />
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
  pendingActions,
  onSelect,
  onSync,
  onWatch,
  onEdit,
  onDelete,
  onBackfill
}: {
  roots: RootSummary[];
  selectedRoot?: RootSummary;
  pendingActions: PendingActionState;
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
          const deleting = pendingLabel(pendingActions, corpusRootActionKey("delete", root.name));
          const syncing = pendingLabel(pendingActions, corpusRootActionKey("sync", root.name));
          const dryRun = pendingLabel(pendingActions, corpusRootActionKey("dry-run", root.name));
          const backfill = pendingLabel(pendingActions, corpusRootActionKey("backfill", root.name));
          const watch = pendingLabel(pendingActions, corpusRootActionKey(root.watch_enabled ? "watch-disable" : "watch-enable", root.name));
          const rowPending = Boolean(deleting);
          return (
          <tr
            key={root.name}
            className={`${selectedRoot?.name === root.name ? "selected" : ""}${rowPending ? " pending-row" : ""}`}
            aria-busy={rowPending ? "true" : undefined}
            onClick={() => { if (!rowPending) onSelect(root); }}
          >
            <td>
              <button className="row-select" type="button" aria-label={`Select ${root.name}`} disabled={rowPending}>
                {selectedRoot?.name === root.name ? <CheckCircle2 size={15} /> : <Square size={12} />}
              </button>
              <div>
                <strong>{root.name}</strong>
                <span>{root.recursive ? "Recursive" : "Single level"} - trust {root.trust_rank ?? 500}</span>
                {deleting ? <span className="row-pending-label"><Clock3 size={13} /> {deleting}</span> : null}
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
                <button type="button" aria-label={`Sync ${root.name}`} title={syncing ?? `Sync ${root.name} now`} disabled={rowPending || Boolean(syncing)} onClick={(event) => { event.stopPropagation(); onSync(root, false); }}>{syncing ? <Clock3 size={15} /> : <RefreshCcw size={15} />}</button>
                <button type="button" aria-label={`Dry run ${root.name}`} title={dryRun ?? `Preview crawl changes for ${root.name}`} disabled={rowPending || Boolean(dryRun)} onClick={(event) => { event.stopPropagation(); onSync(root, true); }}>{dryRun ? <Clock3 size={15} /> : <ListFilter size={15} />}</button>
                <button type="button" aria-label={`Run backfill for ${root.name}`} title={backfill ?? `Process deferred jobs for ${root.name}`} disabled={rowPending || Boolean(backfill)} onClick={(event) => { event.stopPropagation(); onBackfill(root); }}>{backfill ? <Clock3 size={15} /> : <Play size={15} />}</button>
                <button type="button" aria-label={`${root.watch_enabled ? "Disable" : "Enable"} watch ${root.name}`} title={watch ?? `${root.watch_enabled ? "Disable" : "Enable"} recursive watch for ${root.name}`} disabled={rowPending || Boolean(watch)} onClick={(event) => { event.stopPropagation(); onWatch(root, !root.watch_enabled); }}>
                  {watch ? <Clock3 size={15} /> : root.watch_enabled ? <Square size={15} /> : <Play size={15} />}
                </button>
                <button type="button" aria-label={`Edit ${root.name}`} title={`Edit watched path ${root.name}`} disabled={rowPending} onClick={(event) => { event.stopPropagation(); onEdit(root); }}><Wrench size={15} /></button>
                <button type="button" aria-label={`Delete ${root.name}`} title={deleting ?? `Delete watched path ${root.name} from the Flux index`} disabled={rowPending} onClick={(event) => { event.stopPropagation(); onDelete(root); }}>{deleting ? <Clock3 size={15} /> : <Trash2 size={15} />}</button>
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
  pendingActions,
  onEdit,
  onDelete,
  onBackfill
}: {
  root?: RootSummary;
  pendingActions: PendingActionState;
  onEdit: (root: RootSummary) => void;
  onDelete: (root: RootSummary) => void;
  onBackfill: (root: RootSummary) => void;
}) {
  if (!root) {
    return <div className="empty-inspector"><p className="muted">Add or select a watched path to see crawl, watch, and job status.</p></div>;
  }
  const latest = root.latest_crawl ?? {};
  const deleting = pendingLabel(pendingActions, corpusRootActionKey("delete", root.name));
  const backfill = pendingLabel(pendingActions, corpusRootActionKey("backfill", root.name));
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
        <button className="ghost-action compact" type="button" aria-label={`Edit selected watched path ${root.name}`} title="Edit this watched path" disabled={Boolean(deleting)} onClick={() => onEdit(root)}>
          <Wrench size={15} /> Edit
        </button>
        <button className="ghost-action compact" type="button" aria-label={`Run selected root backfill for ${root.name}`} title={backfill ?? "Process deferred jobs for this watched path"} disabled={Boolean(deleting || backfill)} onClick={() => onBackfill(root)}>
          {backfill ? <Clock3 size={15} /> : <Play size={15} />} {backfill ?? "Run backfill"}
        </button>
        <button className="ghost-action compact danger-action" type="button" aria-label={`Delete selected watched path ${root.name}`} title={deleting ?? "Delete this watched path from the Flux index"} disabled={Boolean(deleting)} onClick={() => onDelete(root)}>
          {deleting ? <Clock3 size={15} /> : <Trash2 size={15} />} {deleting ?? "Delete"}
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
  const label = LOCK_TOLERANT_STATE_LABELS[normalized] ?? STATUS_LABELS[normalized] ?? normalized;
  const tone = ["watching", "indexed", "completed"].includes(normalized)
    ? "enabled"
    : ["queued", "processing", "processing_staged", "crawling", "changed", "watch_enabled", "pending_stable", "retrying_locked"].includes(normalized)
      ? "info"
      : ["blocked", "failed", "stale", "deleted", "metadata_only", "blocked_missing_dependency", "blocked_by_policy", "blocked_invalid_source", "blocked_locked"].includes(normalized)
        ? "warning"
        : "";
  return <span className={`state-pill ${tone}`}>{label}</span>;
}

const LOCK_TOLERANT_STATE_LABELS: Record<string, string> = {
  pending_stable: "Pending Stable",
  retrying_locked: "Retrying Locked",
  blocked_locked: "Blocked Locked",
  processing_staged: "Processing Staged",
  metadata_only: "Metadata Only"
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

function DeploymentPanel({ deployment }: { deployment?: HealthPayload["deployment"] }) {
  return (
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
  );
}

function SettingsTab({
  settings,
  health,
  hostStatus,
  restartRows,
  pendingActions,
  onEdit,
  onReset,
  onApply
}: {
  settings: SettingRow[];
  health: HealthPayload;
  hostStatus: string;
  restartRows: SettingRow[];
  pendingActions: PendingActionState;
  onEdit: (setting: SettingRow) => void;
  onReset: (setting: SettingRow) => void;
  onApply: (component?: string) => void;
}) {
  const categories = [...new Set(settings.map((setting) => setting.category))].sort();
  const applyPending = pendingLabel(pendingActions, globalActionKey("settings-apply"));
  return (
    <section className="tab-grid">
      <CodexHooksPanel codex={health.codex} />
      <DeploymentPanel deployment={health.deployment} />
      <Panel title={`Runtime Actions (${restartRows.length})`} action={<button className="small-primary" type="button" disabled={Boolean(applyPending)} onClick={() => onApply()}>{applyPending ?? "Apply acknowledged"}</button>}>
        <SettingsPreview rows={restartRows} />
      </Panel>
      <Panel title="Runtime Settings" action={<button className="small-primary" type="button" disabled={Boolean(applyPending)} onClick={() => onApply()}>{applyPending ?? "Apply pending"}</button>}>
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
              {settings.map((setting) => {
                const resetPending = pendingLabel(pendingActions, settingActionKey("reset", setting.key));
                return (
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
                    <button className="row-button" type="button" aria-label={`Edit ${setting.key}`} disabled={setting.read_only || Boolean(resetPending)} onClick={() => onEdit(setting)}>
                      <Wrench size={15} /> Edit
                    </button>
                    <button className="row-button" type="button" aria-label={`Reset ${setting.key}`} disabled={setting.read_only || setting.source === "default" || Boolean(resetPending)} onClick={() => onReset(setting)}>
                      {resetPending ? <Clock3 size={15} /> : <RotateCcw size={15} />} {resetPending ?? "Reset"}
                    </button>
                  </td>
                </tr>
              );
              })}
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
          <span>system: {hostStatusLabel(hostStatus)}</span>
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
  onRerunDocsFiles,
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
  onRerunDocsFiles: () => void;
  onErrorDetail: (error: string) => void;
  onOpenResult: (result: SearchResult) => void;
}) {
  const retrieval = state.retrieval.retrieval ?? state.health.retrieval ?? {};
  const [benchmarkHistory, setBenchmarkHistory] = useState<RetrievalBenchmarkHistory>({ suite: "standard", runs: [] });
  const [benchmarkResult, setBenchmarkResult] = useState<RetrievalBenchmarkRun | null>(null);
  const [benchmarkRunning, setBenchmarkRunning] = useState(false);
  const [benchmarkStatus, setBenchmarkStatus] = useState("");
  const [benchmarkSuite, setBenchmarkSuite] = useState("standard");
  const benchmarkRuns = benchmarkResult ? [benchmarkResult, ...(benchmarkHistory.runs ?? [])] : benchmarkHistory.runs ?? [];
  const latestBenchmark = benchmarkRuns[0];
  const failedBenchmarkCases = (latestBenchmark?.case_results ?? []).filter((item) => item.status === "failed").slice(0, 3);
  const confidenceBandRows = retrievalConfidenceBandRows(latestBenchmark);
  const semanticThresholdRows = (latestBenchmark?.calibration_summary?.semantic_thresholds ?? []).slice(0, 2);
  const recommendationCandidates = retrievalRecommendationCandidates(latestBenchmark).slice(0, 3);

  useEffect(() => {
    let cancelled = false;
    getJson<RetrievalBenchmarkHistory>("/api/retrieval/benchmarks", { suite: "standard", runs: [] }).then((payload) => {
      if (!cancelled) setBenchmarkHistory(payload);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function runRetrievalBenchmark() {
    setBenchmarkRunning(true);
    setBenchmarkStatus("Retrieval benchmark queued...");
    try {
      const result = await sendJson<RetrievalBenchmarkRun>("/api/retrieval/benchmarks/run", "POST", {
        suite: benchmarkSuite,
        label: "dashboard",
        limit_per_query: 5,
        persist: true
      });
      setBenchmarkResult(result);
      setBenchmarkStatus("Retrieval benchmark recorded.");
    } catch (error) {
      setBenchmarkStatus(errorMessage(error));
    } finally {
      setBenchmarkRunning(false);
    }
  }

  return (
    <section className="tab-grid">
      <Panel title="Retrieval Console" action={searchOpen ? <button className="ghost-action compact" type="button" onClick={onClear}>Clear results</button> : undefined}>
        <MiniTable rows={[
          ["Episodes", "memory", String(retrieval.episodes ?? 0)],
          ["Sources", "memory", String(retrieval.sources ?? 0)],
          ["Corpus chunks", "assets", String(retrieval.asset_chunks ?? 0)],
          ["Search index", "Vespa", String(retrieval.search_index_records ?? 0)],
          ["Duplicates", "suppressed", String(state.retrieval.duplicate_assets ?? state.retrieval.duplicate_count ?? 0)]
        ]} />
        <div className="retrieval-filter-grid">
          <label>
            <span>Search focus</span>
            <select value={searchKind} onChange={(event) => onSearchKind(event.target.value)}>
              <option value="balanced">Balanced</option>
              <option value="docs">Docs/files</option>
              <option value="mail">Mail</option>
              <option value="episode">Episodes</option>
              <option value="code">Code</option>
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
        {isBalancedCodeHeavySearch(searchKind, searchResults) && (
          <div className="retrieval-diagnostic">
            <strong>Balanced results are code-heavy.</strong>
            <span>Docs/files searches text, documents, and images without code chunks crowding the list.</span>
            <button className="ghost-action compact" type="button" onClick={onRerunDocsFiles}>Rerun Docs/files</button>
          </div>
        )}
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
      <CodeDiagnosticsPanel />
      <Panel
        title="Retrieval Benchmarks"
        action={
          <button className="ghost-action compact" type="button" disabled={benchmarkRunning} onClick={() => void runRetrievalBenchmark()}>
            <Play size={15} /> Run retrieval benchmark
          </button>
        }
      >
        <div className="review-controls">
          <label>Benchmark suite
            <select value={benchmarkSuite} onChange={(event) => setBenchmarkSuite(event.target.value)}>
              <option value="standard">Standard</option>
              <option value="governance-shadow">Governance shadow</option>
            </select>
          </label>
        </div>
        {benchmarkRuns.length > 0 ? (
          <MiniTable
            rows={benchmarkRuns.slice(0, 5).map((run) => [
              run.label ?? run.id ?? "unlabeled",
              `${run.passed_count ?? 0}/${run.query_count ?? 0} passed`,
              `top1 ${formatPercentMetric(run.metrics?.top1_accuracy)}`,
              `brief dilution ${formatPercentMetric(run.metrics?.brief_dilution)}`,
              `top1 ${formatSignedPercentMetric(run.metric_deltas?.top1_accuracy)}`,
              `brief dilution ${formatSignedPercentMetric(run.metric_deltas?.brief_dilution)}`
            ])}
          />
        ) : (
          <p className="panel-note">No retrieval benchmark history yet.</p>
        )}
        {latestBenchmark && (
          <div className="diagnostic-list">
            <div className="diagnostic-item">
              <strong>{latestBenchmark.label ?? latestBenchmark.id ?? "latest run"}</strong>
              <span>{humanizeIdentifier(latestBenchmark.status ?? "observed")}</span>
              <em>{benchmarkSettingsMutated(latestBenchmark) ? "settings changed" : "settings_mutated false"}</em>
            </div>
            {confidenceBandRows.map(([band, count]) => (
              <div className="diagnostic-item" key={`confidence-${band}`}>
                <strong>{formatIdentifierWord(band)} confidence: {count}</strong>
                <span>score-confidence separation</span>
                <em>metadata only</em>
              </div>
            ))}
            {semanticThresholdRows.map((item) => (
              <div className="diagnostic-item" key={`semantic-threshold-${item.threshold}`}>
                <strong>Semantic threshold {formatDecimal(item.threshold, 2)}</strong>
                <span>{item.pass_count ?? 0}/{item.evaluated_count ?? 0} calibration cases passed</span>
                <em>{item.false_positive_count ?? 0} FP / {item.false_negative_count ?? 0} FN</em>
              </div>
            ))}
            {recommendationCandidates.map((candidate, index) => (
              <div className="diagnostic-item" key={`${candidate.kind ?? "candidate"}-${index}`}>
                <strong>{humanizeIdentifier(candidate.kind ?? "candidate")}</strong>
                <span>{candidate.rationale ?? "advisory candidate"}</span>
                <em>{candidate.threshold !== undefined ? `threshold ${formatDecimal(candidate.threshold, 2)}` : "advisory"}</em>
              </div>
            ))}
            <GovernanceShadowEvidence run={latestBenchmark} />
            {failedBenchmarkCases.map((item) => (
              <div className="diagnostic-item" key={item.case_id ?? item.observed_ids?.join(",") ?? "case"}>
                <strong>{item.case_id ?? "case"}</strong>
                <span>{formatReasonList(item.reasons)}</span>
                <em>{failedRetrievalCaseLabel(item)}</em>
                {(item.failure_details ?? []).slice(0, 2).map((detail, index) => (
                  <span key={`${item.case_id ?? "case"}-detail-${index}`}>{detail.message ?? formatRetrievalReason(detail.reason)}</span>
                ))}
              </div>
            ))}
          </div>
        )}
        {benchmarkStatus && <p className="panel-note">{benchmarkStatus}</p>}
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
  const semanticDuplicateCount = suppressionObjectCount(suppression, "semantic_duplicates");
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
        {semanticDuplicateCount > 0 && (
          <>
            <span>Semantic duplicates</span>
            <strong>{semanticDuplicateCount} suppressed</strong>
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
  const semanticDuplicateCount = suppressionCount(suppression.semantic_duplicates);
  if (excluded.length === 0 && exactDuplicateCount === 0 && versionFamilyCount === 0 && semanticDuplicateCount === 0) return null;
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
      {(exactDuplicateCount > 0 || versionFamilyCount > 0 || semanticDuplicateCount > 0) && (
        <section>
          <strong>Suppressed evidence</strong>
          {exactDuplicateCount > 0 && <span>Exact duplicates: {exactDuplicateCount}</span>}
          {versionFamilyCount > 0 && <span>Version families: {versionFamilyCount}</span>}
          {semanticDuplicateCount > 0 && <span>Semantic duplicates: {semanticDuplicateCount}</span>}
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
  governanceActions,
  governanceDigest,
  governancePolicy,
  graph,
  selectedClaim,
  loading,
  reviewFilter,
  stateFilter,
  captureStatus,
  onReviewFilter,
  onStateFilter,
  onCaptureStatus,
  onSelectClaim,
  onTransition,
  onCaptureDecision,
  onCaptureIngest,
  onRetentionPolicySave,
  onGovernanceRun,
  onGovernanceApply,
  onGovernanceRecover,
  onRefresh
}: {
  payload: ClaimReviewPayload;
  capture: CaptureReviewPayload;
  captureAudit: AuditEvent[];
  retentionPolicies: RetentionPolicyPayload;
  retentionQuality: RetentionQualityPayload;
  governanceActions: GovernanceActionsPayload;
  governanceDigest: GovernanceDigestPayload;
  governancePolicy: GovernancePolicyPayload;
  graph: GraphPayload;
  selectedClaim?: ClaimReviewClaim;
  loading: boolean;
  reviewFilter: ClaimReviewFilter;
  stateFilter: string;
  captureStatus: CaptureReviewStatus;
  onReviewFilter: (value: ClaimReviewFilter) => void;
  onStateFilter: (value: string) => void;
  onCaptureStatus: (value: CaptureReviewStatus) => void;
  onSelectClaim: (claim: ClaimReviewClaim) => void;
  onTransition: (claim: ClaimReviewClaim, transition: string) => void;
  onCaptureDecision: (job: CaptureReviewJob, decision: "approve" | "reject") => void;
  onCaptureIngest: () => void;
  onRetentionPolicySave: (policy: RetentionPolicy, update: RetentionPolicyUpdate) => void;
  onGovernanceRun: () => void;
  onGovernanceApply: (action: GovernanceAction) => void;
  onGovernanceRecover: (action: GovernanceAction) => void;
  onRefresh: () => void;
}) {
  const claims = payload.claims ?? [];
  const counts = payload.counts ?? {};
  const captureJobs = capture.jobs ?? [];
  const captureCounts = captureStatusCounts(captureJobs);
  const actions = governanceActions.actions ?? [];
  const recoverableActions = actions.filter((action) => action.status === "applied");
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
      <Panel
        title="Governance Automation"
        action={<button className="small-primary" type="button" onClick={onGovernanceRun}><Play size={15} /> Run shadow</button>}
      >
        <GovernanceActionSummary payload={governanceActions} />
        <GovernanceActionTable actions={actions} onApply={onGovernanceApply} onRecover={onGovernanceRecover} />
      </Panel>
      <Panel title="Governance Digest">
        <GovernanceDigestPanel payload={governanceDigest} />
      </Panel>
      <Panel title="Guardrails">
        <GovernanceGuardrailsPanel policy={governancePolicy} actions={actions} />
      </Panel>
      <Panel title="Recovery">
        <GovernanceRecoveryPanel actions={recoverableActions} onRecover={onGovernanceRecover} />
      </Panel>
      <Panel title="Capture Review Queue">
        <div className="review-summary">
          <Stat label="Pending" value={String(captureCounts.pending_review ?? 0)} />
          <Stat label="Approved" value={String(captureCounts.approved ?? 0)} />
          <Stat label="Ingested" value={String(captureCounts.completed ?? 0)} />
          <Stat label="Failed" value={String((captureCounts.failed ?? 0) + (captureCounts.blocked_missing_dependency ?? 0))} />
        </div>
        <div className="review-controls">
          <label>Capture status
            <select value={captureStatus} onChange={(event) => onCaptureStatus(event.target.value as CaptureReviewStatus)}>
              <option value="pending_review">Pending review</option>
              <option value="approved">Approved</option>
              <option value="completed">Completed</option>
              <option value="failed">Failed</option>
              <option value="blocked_missing_dependency">Blocked dependency</option>
              <option value="rejected">Rejected</option>
              <option value="all">All capture jobs</option>
            </select>
          </label>
          <button className="ghost-action compact" type="button" onClick={onCaptureIngest}>
            <Play size={15} /> Ingest approved
          </button>
        </div>
        <CaptureReviewTable jobs={captureJobs} onDecision={onCaptureDecision} />
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

function GovernanceActionSummary({ payload }: { payload: GovernanceActionsPayload }) {
  const telemetry = payload.telemetry ?? {};
  return (
    <>
      <div className="review-summary">
        <Stat label="Total" value={String(telemetry.total ?? payload.actions?.length ?? 0)} />
        <Stat label="Proposed" value={String(telemetry.by_status?.proposed ?? 0)} />
        <Stat label="Blocked" value={String(telemetry.by_status?.blocked ?? 0)} />
        <Stat label="Recoverable" value={String(telemetry.by_status?.applied ?? 0)} />
      </div>
      <div className="review-inline-status">
        {telemetry.by_risk?.high ?? 0} high risk - {telemetry.by_mutation?.mutated ?? 0} mutating actions
      </div>
    </>
  );
}

function GovernanceActionTable({
  actions,
  onApply,
  onRecover
}: {
  actions: GovernanceAction[];
  onApply: (action: GovernanceAction) => void;
  onRecover: (action: GovernanceAction) => void;
}) {
  const visible = actions.slice(0, 12);
  if (visible.length === 0) return <p className="muted padded">No governance proposals.</p>;
  return (
    <div className="review-table-wrap">
      <table className="profile-table review-table" aria-label="Governance actions">
        <thead>
          <tr>
            <th>Action</th>
            <th>Target</th>
            <th>Risk</th>
            <th>Status</th>
            <th>Source</th>
            <th>Rationale</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {visible.map((action) => (
            <tr key={action.id ?? `${action.action}-${action.target_id}`}>
              <td>{titleCase((action.action ?? "governance").replace(/_/g, " "))}</td>
              <td className="claim-object" title={action.target_id ?? ""}>{action.target_type ?? "-"}:{action.target_id ?? "-"}</td>
              <td><RootStateBadge state={action.risk ?? "medium"} /></td>
              <td><RootStateBadge state={action.status ?? "proposed"} /></td>
              <td>{titleCase((action.source ?? "governance").replace(/_/g, " "))}</td>
              <td className="claim-object" title={action.rationale?.summary ?? ""}>{action.rationale?.summary ?? "-"}</td>
              <td>
                <div className="row-actions">
                  <button
                    type="button"
                    aria-label={`Apply governance action ${action.id ?? action.target_id ?? "unknown"}`}
                    title="Apply governance action"
                    disabled={action.status !== "proposed"}
                    onClick={() => onApply(action)}
                  >
                    <CheckCircle2 size={15} />
                  </button>
                  <button
                    type="button"
                    aria-label={`Recover governance action ${action.id ?? action.target_id ?? "unknown"}`}
                    title="Recover governance action"
                    disabled={action.status !== "applied"}
                    onClick={() => onRecover(action)}
                  >
                    <RotateCcw size={15} />
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

function GovernanceDigestPanel({ payload }: { payload: GovernanceDigestPayload }) {
  const summary = payload.digest?.summary ?? {};
  const recommendations = payload.digest?.recommendations ?? [];
  return (
    <>
      <div className="review-summary">
        <Stat label="New" value={String(summary.new_proposals ?? 0)} />
        <Stat label="Blocked" value={String(summary.blocked_proposals ?? 0)} />
        <Stat label="Recoverable" value={String(summary.recoverable_actions ?? 0)} />
        <Stat label="Gate" value={String(summary.gate_status ?? "unknown")} />
      </div>
      {recommendations.length === 0 ? (
        <p className="muted padded">No governance recommendations.</p>
      ) : (
        <ul className="audit-list">
          {recommendations.slice(0, 6).map((item, index) => (
            <li key={`${item.action ?? "recommendation"}-${index}`}>
              <strong>{titleCase(String(item.action ?? "recommendation").replace(/_/g, " "))}</strong>
              <span>{String(item.reason ?? item.count ?? "")}</span>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function GovernanceGuardrailsPanel({ policy, actions }: { policy: GovernancePolicyPayload; actions: GovernanceAction[] }) {
  const blocked = actions.filter((action) => action.status === "blocked");
  const stale = actions.filter((action) => action.status === "skipped_conflict");
  const effectivePolicy = policy.policy ?? {};
  return (
    <>
      <div className="review-summary">
        <Stat label="Precision Gate" value={String(effectivePolicy.min_shadow_precision ?? "0.8")} />
        <Stat label="Auto Apply" value={String(effectivePolicy.auto_apply_enabled ?? false)} />
        <Stat label="Risk Ceiling" value={String(effectivePolicy.auto_apply_risk_ceiling ?? "low")} />
        <Stat label="Stale" value={String(stale.length)} />
      </div>
      {blocked.length === 0 ? (
        <p className="muted padded">No blocked governance guardrails.</p>
      ) : (
        <div className="diagnostic-list">
          {blocked.slice(0, 5).map((action) => (
            <div className="diagnostic-item" key={action.id ?? action.target_id}>
              <strong>{titleCase((action.action ?? "blocked").replace(/_/g, " "))}</strong>
              <span>{action.target_type ?? "target"}:{action.target_id ?? "-"}</span>
              <em>{action.rationale?.summary ?? "blocked by guardrail"}</em>
            </div>
          ))}
        </div>
      )}
    </>
  );
}

function GovernanceRecoveryPanel({ actions, onRecover }: { actions: GovernanceAction[]; onRecover: (action: GovernanceAction) => void }) {
  if (actions.length === 0) return <p className="muted padded">No recoverable governance actions.</p>;
  return (
    <div className="diagnostic-list">
      {actions.slice(0, 8).map((action) => (
        <div className="diagnostic-item" key={action.id ?? action.target_id}>
          <strong>{titleCase((action.action ?? "applied").replace(/_/g, " "))}</strong>
          <span>{action.target_type ?? "target"}:{action.target_id ?? "-"}</span>
          <button className="ghost-action compact" type="button" onClick={() => onRecover(action)}>
            <RotateCcw size={15} /> Recover
          </button>
        </div>
      ))}
    </div>
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
            <th>Ingestion</th>
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
              <td className="claim-object" title={captureReviewIngestionLabel(job)}>{captureReviewIngestionLabel(job)}</td>
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
    ?? stringFromUnknown(payload.source_leaf)
    ?? stringFromUnknown(payload.source)
    ?? stringFromUnknown(payload.source_dir)
    ?? stringFromUnknown(payload.file)
    ?? "-";
}

function captureReviewIngestionLabel(job: CaptureReviewJob) {
  const ingestion = job.payload?.ingestion;
  if (!isRecord(ingestion)) return "-";
  const episodeIds = Array.isArray(ingestion.episode_ids) ? ingestion.episode_ids.map(String).filter(Boolean) : [];
  return episodeIds.length > 0
    ? episodeIds.join(", ")
    : stringFromUnknown(ingestion.status) ?? stringFromUnknown(ingestion.error) ?? "-";
}

function captureStatusCounts(jobs: CaptureReviewJob[]) {
  const counts: Record<string, number> = {};
  for (const job of jobs) {
    const status = stringFromUnknown(job.payload?.status) ?? job.status ?? "pending_review";
    counts[status] = (counts[status] ?? 0) + 1;
  }
  return counts;
}

function captureReviewAuditEvents(payload: AuditPayload): AuditEvent[] {
  const events = Array.isArray(payload) ? payload : payload.events ?? [];
  return events.filter((event) => {
    const type = event.event_type ?? "";
    return type.startsWith("capture.review_") || type.startsWith("capture.ingestion") || type === "capture.ingested";
  }).slice(0, 8);
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
  const fileKind = result.file_kind ? formatRetrievalReason(result.file_kind) : "";
  const matchedTerms = result.snippet?.matched_terms?.length ?? 0;
  const matchedText = `${matchedTerms} matched term${matchedTerms === 1 ? "" : "s"}`;
  const parts = [kind];
  if (fileKind && fileKind.toLowerCase() !== String(kind).toLowerCase()) {
    parts.push(fileKind);
  }
  parts.push(matchedText);
  if (result.streams?.length) {
    parts.push(result.streams.map(prettyStreamName).join(", "));
    return parts.join(" - ");
  }
  if (typeof result.score === "number") {
    parts.push(`score ${result.score.toFixed(3)}`);
    return parts.join(" - ");
  }
  return parts.join(" - ");
}

function searchResultKey(result: SearchResult, index: number): string {
  return result.detail_ref?.id ?? result.id ?? result.asset_id ?? result.mail_message_id ?? `${result.title ?? "result"}-${index}`;
}

function buildDashboardRetrievalFilters(kind: string, currentOnly: boolean, includeSuppressed: boolean): RetrievalFilters {
  const filters: RetrievalFilters = {
    logical_kinds: [],
    current_only: currentOnly,
    lifecycle_states: [],
    include_suppressed: includeSuppressed
  };
  if (kind === "docs") {
    filters.logical_kinds = ["file"];
    filters.file_kinds = ["text", "document", "image"];
  } else if (kind === "mail") {
    filters.logical_kinds = ["mail"];
  } else if (kind === "episode") {
    filters.logical_kinds = ["episode"];
  } else if (kind === "code") {
    filters.logical_kinds = ["file"];
    filters.file_kinds = ["code"];
  }
  return filters;
}

function isBalancedCodeHeavySearch(searchKind: string, results: SearchResult[]): boolean {
  if (searchKind !== "balanced" || results.length === 0) return false;
  return results.every(isCodeSearchResult);
}

function isCodeSearchResult(result: SearchResult): boolean {
  const fileKind = String(result.file_kind ?? "").toLowerCase().replace(/[-_]+/g, "_");
  return fileKind === "code" || Boolean((result as Record<string, unknown>).code);
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

function JobsTab({
  state,
  jobFilters,
  jobSort,
  pendingActions,
  onRefresh,
  onApplyJobFilters,
  onClearJobFilters,
  onApplyJobSort,
  onPageJobHistory,
  onCancelOutlookRequest,
  onCancelCorpusJob,
  onRetryCorpusJob,
  onMarkCorpusJobForDeletion,
  onRestoreCorpusJobDeletionRequest,
  onJobFileAction
}: {
  state: LoadState;
  jobFilters: JobHistoryFilters;
  jobSort: JobSortState;
  pendingActions: PendingActionState;
  onRefresh: () => void;
  onApplyJobFilters: (filters: JobHistoryFilters) => void;
  onClearJobFilters: () => void;
  onApplyJobSort: (sort: JobSortState) => void;
  onPageJobHistory: (offset: number) => void;
  onCancelOutlookRequest: (requestId: string) => void;
  onCancelCorpusJob: (jobId: string) => void;
  onRetryCorpusJob: (jobId: string) => void;
  onMarkCorpusJobForDeletion: (jobId: string) => void;
  onRestoreCorpusJobDeletionRequest: (jobId: string) => void;
  onJobFileAction: (jobId: string, action: "open" | "reveal") => void;
}) {
  const outlookJobs = activeOutlookRequests(state.outlook.pending_requests).map(outlookRequestJob);
  const corpusJobs = state.jobs.jobs ?? [];
  const jobs = [...outlookJobs, ...corpusJobs];
  const hasOutlookRequests = outlookJobs.length > 0;
  const jobLimit = numberFromUnknown(state.jobs.limit) ?? JOB_PAGE_LIMIT;
  const jobOffsetValue = numberFromUnknown(state.jobs.offset) ?? 0;
  const jobCount = numberFromUnknown(state.jobs.count) ?? corpusJobs.length;
  const hasNext = Boolean(state.jobs.has_next ?? (jobOffsetValue + corpusJobs.length < jobCount));
  const jobsEmptyHint = jobs.length === 0 ? jobsModelActivityHint(state.modelActivity) : "";
  const familyRows = (state.health.acceleration?.worker_families ?? []).slice(0, 8).map((family) => [
    family.family ?? "general",
    `${family.pending ?? 0} pending / ${family.running ?? 0} running`,
    family.backpressure ? humanizeIdentifier(family.backpressure) : `${family.blocked ?? 0} blocked / ${family.failed ?? 0} failed`
  ] as [string, string, string]);
  return (
    <section className="tab-grid">
      <Panel title="Job Queue" action={<button className="small-primary" type="button" onClick={onRefresh}><RefreshCcw size={15} /> Refresh</button>}>
        <JobHistoryControls
          filters={jobFilters}
          options={state.jobs.filter_options ?? {}}
          count={jobCount}
          limit={jobLimit}
          offset={jobOffsetValue}
          visibleCount={corpusJobs.length}
          hasNext={hasNext}
          onApply={onApplyJobFilters}
          onClear={onClearJobFilters}
          onPage={onPageJobHistory}
        />
        <JobQueueTable
          jobs={jobs}
          label={hasOutlookRequests ? "Operational jobs" : "Extraction jobs"}
          empty={hasOutlookRequests ? "No operational jobs queued." : "No queued extraction jobs."}
          sort={jobSort}
          onSortChange={onApplyJobSort}
          onCancelOutlookRequest={onCancelOutlookRequest}
          onCancelCorpusJob={onCancelCorpusJob}
          onRetryCorpusJob={onRetryCorpusJob}
          onMarkCorpusJobForDeletion={onMarkCorpusJobForDeletion}
          onRestoreCorpusJobDeletionRequest={onRestoreCorpusJobDeletionRequest}
          onJobFileAction={onJobFileAction}
          pendingActions={pendingActions}
        />
        {jobsEmptyHint ? <p className="panel-note">{jobsEmptyHint}</p> : null}
      </Panel>
      <Panel title="Worker Family Status">
        {familyRows.length > 0 ? <MiniTable rows={familyRows} /> : <p className="muted">No worker-family status yet.</p>}
      </Panel>
      <BacklogPanel health={state.health} blockedJobs={state.health.jobs?.blocked ?? 0} />
    </section>
  );
}

function JobHistoryControls({
  filters,
  options,
  count,
  limit,
  offset,
  visibleCount,
  hasNext,
  onApply,
  onClear,
  onPage
}: {
  filters: JobHistoryFilters;
  options: JobFilterOptions;
  count: number;
  limit: number;
  offset: number;
  visibleCount: number;
  hasNext: boolean;
  onApply: (filters: JobHistoryFilters) => void;
  onClear: () => void;
  onPage: (offset: number) => void;
}) {
  const [draft, setDraft] = useState<JobHistoryFilters>(filters);
  const [openFilterMenu, setOpenFilterMenu] = useState<"status" | "root_name" | "job_type" | null>(null);
  useEffect(() => {
    setDraft(filters);
  }, [filters.status, filters.root_name, filters.job_type, filters.updated_from, filters.updated_to]);
  const updateDraftDate = (key: "updated_from" | "updated_to", value: string) => {
    setDraft((current) => ({ ...current, [key]: value }));
  };
  const toggleDraftValue = (key: "status" | "root_name" | "job_type", value: string) => {
    setDraft((current) => ({ ...current, [key]: toggleStringValue(current[key], value) }));
  };
  const pageStart = count > 0 ? offset + 1 : 0;
  const pageEnd = count > 0 ? Math.min(offset + visibleCount, count) : 0;
  const safeLimit = Math.max(1, limit || JOB_PAGE_LIMIT);
  const pageCount = count > 0 ? Math.max(1, Math.ceil(count / safeLimit)) : 0;
  const currentPage = pageCount > 0 ? Math.min(pageCount, Math.floor(offset / safeLimit) + 1) : 0;
  const pageItems = jobPageItems(currentPage, pageCount);
  const jobWord = count === 1 ? "job" : "jobs";
  return (
    <div className="job-history-controls">
      <div className="job-filter-grid">
        <JobFilterMultiSelect
          label="Status"
          buttonLabel="Job status filter"
          optionsLabel="Job status options"
          values={draft.status}
          options={options.statuses ?? []}
          allLabel="All statuses"
          pluralLabel="statuses"
          optionLabel={statusLabel}
          open={openFilterMenu === "status"}
          onOpenChange={(open) => setOpenFilterMenu(open ? "status" : null)}
          onToggle={(value) => toggleDraftValue("status", value)}
        />
        <JobFilterMultiSelect
          label="Root"
          buttonLabel="Job root filter"
          optionsLabel="Job root options"
          values={draft.root_name}
          options={options.roots ?? []}
          allLabel="All roots"
          pluralLabel="roots"
          optionLabel={(root) => root}
          open={openFilterMenu === "root_name"}
          onOpenChange={(open) => setOpenFilterMenu(open ? "root_name" : null)}
          onToggle={(value) => toggleDraftValue("root_name", value)}
        />
        <JobFilterMultiSelect
          label="Type"
          buttonLabel="Job type filter"
          optionsLabel="Job type options"
          values={draft.job_type}
          options={options.job_types ?? []}
          allLabel="All types"
          pluralLabel="types"
          optionLabel={jobTypeLabel}
          open={openFilterMenu === "job_type"}
          onOpenChange={(open) => setOpenFilterMenu(open ? "job_type" : null)}
          onToggle={(value) => toggleDraftValue("job_type", value)}
        />
        <label>Updated from
          <input aria-label="Updated from filter" type="datetime-local" value={draft.updated_from} onChange={(event) => updateDraftDate("updated_from", event.target.value)} />
        </label>
        <label>Updated to
          <input aria-label="Updated to filter" type="datetime-local" value={draft.updated_to} onChange={(event) => updateDraftDate("updated_to", event.target.value)} />
        </label>
        <div className="job-filter-actions">
          <button className="ghost-action compact" type="button" onClick={() => onApply(draft)}><ListFilter size={15} /> Apply job filters</button>
          <button className="ghost-action compact" type="button" onClick={() => { setDraft(emptyJobHistoryFilters); onClear(); }}><X size={15} /> Clear job filters</button>
        </div>
      </div>
      <div className="job-pager" aria-label="Job history paging">
        <span>{pageStart}-{pageEnd} of {count} corpus {jobWord}</span>
        <button className="ghost-action compact" type="button" aria-label="Previous jobs page" disabled={offset <= 0} onClick={() => onPage(Math.max(0, offset - safeLimit))}>
          <ChevronLeft size={15} /> Previous
        </button>
        {pageItems.length > 0 && (
          <div className="job-page-numbers" aria-label="Job pages">
            {pageItems.map((item) => {
              if (typeof item !== "number") {
                return <span className="job-page-ellipsis" aria-hidden="true" key={item}>...</span>;
              }
              const isCurrent = item === currentPage;
              return (
                <button
                  className="job-page-button"
                  type="button"
                  key={item}
                  aria-current={isCurrent ? "page" : undefined}
                  aria-label={isCurrent ? `Current jobs page ${item}` : `Go to jobs page ${item}`}
                  disabled={isCurrent}
                  onClick={() => onPage((item - 1) * safeLimit)}
                >
                  {item}
                </button>
              );
            })}
          </div>
        )}
        <button className="ghost-action compact" type="button" aria-label="Next jobs page" disabled={!hasNext} onClick={() => onPage(offset + safeLimit)}>
          Next <ChevronRight size={15} />
        </button>
      </div>
    </div>
  );
}

function jobPageItems(currentPage: number, pageCount: number): JobPageItem[] {
  if (pageCount <= 0 || currentPage <= 0) return [];
  if (pageCount <= 7) return Array.from({ length: pageCount }, (_, index) => index + 1);

  const pages = new Set([1, pageCount, currentPage - 1, currentPage, currentPage + 1].filter((page) => page >= 1 && page <= pageCount));
  const sorted = [...pages].sort((left, right) => left - right);
  const items: JobPageItem[] = [];
  for (const page of sorted) {
    const previous = typeof items.at(-1) === "number" ? items.at(-1) as number : undefined;
    if (previous !== undefined) {
      const gap = page - previous;
      if (gap === 2) {
        items.push(previous + 1);
      } else if (gap > 2) {
        items.push(`ellipsis-${previous}` as const);
      }
    }
    items.push(page);
  }
  return items;
}

function JobFilterMultiSelect({
  label,
  buttonLabel,
  optionsLabel,
  values,
  options,
  allLabel,
  pluralLabel,
  optionLabel,
  open,
  onOpenChange,
  onToggle
}: {
  label: string;
  buttonLabel: string;
  optionsLabel: string;
  values: string[];
  options: string[];
  allLabel: string;
  pluralLabel: string;
  optionLabel: (value: string) => string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onToggle: (value: string) => void;
}) {
  const menuRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (!open) return undefined;
    const handlePointerDown = (event: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        onOpenChange(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        onOpenChange(false);
      }
    };
    document.addEventListener("pointerdown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [open, onOpenChange]);
  const selected = new Set(values);
  const summary = multiFilterSummary(values, allLabel, pluralLabel, optionLabel);
  return (
    <div className="job-filter-menu" ref={menuRef}>
      <span>{label}</span>
      <button
        className="job-filter-menu-button"
        type="button"
        aria-label={buttonLabel}
        aria-expanded={open}
        onClick={() => onOpenChange(!open)}
      >
        <strong>{summary}</strong>
        <ChevronDown size={15} />
      </button>
      {open ? (
        <div className="job-filter-menu-panel" role="group" aria-label={optionsLabel}>
          {options.length === 0 ? <p className="muted">No options</p> : null}
          {options.map((option) => (
            <label className="checkbox-label" key={option}>
              <input type="checkbox" checked={selected.has(option)} onChange={() => onToggle(option)} />
              {optionLabel(option)}
            </label>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function JobQueueTable({
  jobs,
  label,
  empty,
  sort,
  onSortChange,
  onCancelOutlookRequest,
  onCancelCorpusJob,
  onRetryCorpusJob,
  onMarkCorpusJobForDeletion,
  onRestoreCorpusJobDeletionRequest,
  onJobFileAction,
  pendingActions
}: {
  jobs: Array<Record<string, unknown>>;
  label: string;
  empty: string;
  sort: JobSortState;
  onSortChange: (sort: JobSortState) => void;
  onCancelOutlookRequest?: (requestId: string) => void;
  onCancelCorpusJob?: (jobId: string) => void;
  onRetryCorpusJob?: (jobId: string) => void;
  onMarkCorpusJobForDeletion?: (jobId: string) => void;
  onRestoreCorpusJobDeletionRequest?: (jobId: string) => void;
  onJobFileAction?: (jobId: string, action: "open" | "reveal") => void;
  pendingActions: PendingActionState;
}) {
  const [expandedJobId, setExpandedJobId] = useState<string | null>(null);
  const [toolInvocationsByJob, setToolInvocationsByJob] = useState<Record<string, JobToolInvocationState>>({});
  const loadToolInvocations = useCallback(async (id: string) => {
    setToolInvocationsByJob((current) => ({
      ...current,
      [id]: { invocations: current[id]?.invocations ?? [], loading: true }
    }));
    try {
      const payload = await fetchRequiredJson<JobToolInvocationResponse>(`/api/dashboard/jobs/${encodeURIComponent(id)}/tool-invocations?limit=100`);
      setToolInvocationsByJob((current) => ({
        ...current,
        [id]: { invocations: payload.invocations ?? [], loading: false }
      }));
    } catch (error) {
      setToolInvocationsByJob((current) => ({
        ...current,
        [id]: { invocations: current[id]?.invocations ?? [], loading: false, error: errorMessage(error) }
      }));
    }
  }, []);
  useEffect(() => {
    if (expandedJobId) {
      void loadToolInvocations(expandedJobId);
    }
  }, [expandedJobId, jobs, loadToolInvocations]);
  if (jobs.length === 0) return <p className="muted">{empty}</p>;
  const sortHeader = (key: JobSortKey, headerLabel: string) => {
    const active = sort.sort_by === key;
    const Icon = active ? (sort.sort_dir === "asc" ? ArrowUp : ArrowDown) : ArrowUpDown;
    return (
      <th aria-sort={active ? (sort.sort_dir === "asc" ? "ascending" : "descending") : "none"}>
        <button
          className={`job-sort-button ${active ? "active" : ""}`}
          type="button"
          aria-label={`Sort jobs by ${headerLabel}`}
          onClick={() => onSortChange(nextJobSort(sort, key))}
        >
          <span>{headerLabel}</span>
          <Icon size={14} />
        </button>
      </th>
    );
  };
  return (
    <div className="job-table-wrap">
      <table className="profile-table job-table" aria-label={label}>
        <thead>
          <tr>
            {sortHeader("status", "Status")}
            {sortHeader("job_type", "Job type")}
            {sortHeader("target", "Target")}
            {sortHeader("root", "Root")}
            {sortHeader("attempts", "Attempts")}
            {sortHeader("updated", "Updated")}
            {sortHeader("progress", "Progress")}
            {sortHeader("last_error", "Last error")}
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((job, index) => {
            const id = jobId(job, index);
            const payload = jobPayload(job);
            const target = jobTarget(job, payload);
            const status = stringFromUnknown(job.status) ?? "unknown";
            const displayStatus = jobDisplayStatus(job, status);
            const expanded = expandedJobId === id;
            const outlookRequest = stringFromUnknown(job.job_type) === "outlook_sync_request";
            const cancellableCorpusJob = isCancelableCorpusJob(job, status);
            const retryableCorpusJob = isRetryableCorpusJob(job, status);
            const deletionRequested = Boolean(stringFromUnknown(job.delete_requested_at));
            const obsoleteCorpusJob = status === "obsolete";
            const deletionMarkableCorpusJob = isDeletionMarkableCorpusJob(job, status);
            const deletionRestorableCorpusJob = isDeletionRestorableCorpusJob(job, status);
            const nonRestorableObsoleteReason = obsoleteCorpusJob && !deletionRestorableCorpusJob ? obsoleteJobReasonLabel(job) : undefined;
            const showDeletionMarker = deletionRequested && !obsoleteCorpusJob;
            const targetActionable = isActionableJobTarget(job, payload, target);
            const progress = jobProgressSummary(job);
            const retryPending = pendingLabel(pendingActions, corpusJobActionKey("retry", id));
            const cancelPending = pendingLabel(pendingActions, corpusJobActionKey("cancel", id));
            const deleteRequestPending = pendingLabel(pendingActions, corpusJobActionKey("delete-request", id));
            const restoreDeletePending = pendingLabel(pendingActions, corpusJobActionKey("restore-delete-request", id));
            const outlookCancelPending = pendingLabel(pendingActions, `outlook-request:cancel:${id}`);
            return (
              <Fragment key={id}>
                <tr>
                  <td><JobStatusBadge status={displayStatus} /></td>
                  <td><strong>{jobTypeLabel(stringFromUnknown(job.job_type))}</strong></td>
                  <td className="job-target" title={target.path}>
                    <div className="job-target-cell">
                      <span className="job-target-path">{target.path}</span>
                      {targetActionable ? (
                        <span className="job-target-actions">
                          <button
                            className="icon-button compact"
                            type="button"
                            title="Open file"
                            aria-label={`Open job target file ${target.path}`}
                            onClick={() => onJobFileAction?.(id, "open")}
                          >
                            <ExternalLink size={15} />
                          </button>
                          <button
                            className="icon-button compact"
                            type="button"
                            title="Open containing folder"
                            aria-label={`Open containing folder for job target ${target.path}`}
                            onClick={() => onJobFileAction?.(id, "reveal")}
                          >
                            <FolderOpen size={15} />
                          </button>
                        </span>
                      ) : null}
                    </div>
                  </td>
                  <td>{target.root}</td>
                  <td>{numberFromUnknown(job.attempts) ?? 0}</td>
                  <td>{formatDate(stringFromUnknown(job.updated_at))}</td>
                  <td className="job-progress" title={progress}>{progress}</td>
                  <td className="job-error" title={stringFromUnknown(job.last_error) ?? ""}>{stringFromUnknown(job.last_error) ?? "-"}</td>
                  <td className="job-actions-cell">
                    {retryableCorpusJob ? (
                      <button
                        className="row-button"
                        type="button"
                        aria-label={`Force retry corpus job ${id}`}
                        disabled={Boolean(retryPending)}
                        onClick={() => onRetryCorpusJob?.(id)}
                      >
                        {retryPending ? <Clock3 size={15} /> : <RotateCcw size={15} />} {retryPending ?? "Retry"}
                      </button>
                    ) : null}
                    {outlookRequest ? (
                      <button
                        className="row-button warning"
                        type="button"
                        aria-label={`Cancel Outlook request ${id}`}
                        disabled={Boolean(outlookCancelPending)}
                        onClick={() => onCancelOutlookRequest?.(id)}
                      >
                        {outlookCancelPending ? <Clock3 size={15} /> : <Trash2 size={15} />} {outlookCancelPending ?? "Cancel"}
                      </button>
                    ) : null}
                    {cancellableCorpusJob ? (
                      <button
                        className="row-button warning"
                        type="button"
                        aria-label={`Cancel corpus job ${id}`}
                        disabled={Boolean(cancelPending)}
                        onClick={() => onCancelCorpusJob?.(id)}
                      >
                        {cancelPending ? <Clock3 size={15} /> : <Trash2 size={15} />} {cancelPending ?? "Cancel"}
                      </button>
                    ) : null}
                    {deletionMarkableCorpusJob ? (
                      <button
                        className="row-button warning"
                        type="button"
                        aria-label={`Mark corpus job ${id} for deletion`}
                        disabled={Boolean(deleteRequestPending)}
                        onClick={() => onMarkCorpusJobForDeletion?.(id)}
                      >
                        {deleteRequestPending ? <Clock3 size={15} /> : <Trash2 size={15} />} {deleteRequestPending ?? "Mark for deletion"}
                      </button>
                    ) : null}
                    {deletionRestorableCorpusJob ? (
                      <button
                        className="row-button"
                        type="button"
                        aria-label={`Restore deletion mark for corpus job ${id}`}
                        disabled={Boolean(restoreDeletePending)}
                        onClick={() => onRestoreCorpusJobDeletionRequest?.(id)}
                      >
                        {restoreDeletePending ? <Clock3 size={15} /> : <RotateCcw size={15} />} {restoreDeletePending ?? "Restore"}
                      </button>
                    ) : null}
                    {nonRestorableObsoleteReason ? <span className="state-pill warning">{nonRestorableObsoleteReason}</span> : null}
                    {showDeletionMarker ? <span className="state-pill warning">Marked for deletion</span> : null}
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
                {expanded ? <JobDetailRow job={job} payload={payload} target={target} id={id} status={displayStatus} toolInvocations={toolInvocationsByJob[id]} /> : null}
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
  status,
  toolInvocations
}: {
  job: Record<string, unknown>;
  payload: Record<string, unknown>;
  target: { path: string; root: string };
  id: string;
  status: string;
  toolInvocations?: JobToolInvocationState;
}) {
  const telemetry = jobTelemetry(job);
  const progress = jobProgressSummary(job);
  const details = [
    ["Job id", id],
    ["Status", statusLabel(status)],
    ["Result", statusLabel(stringFromUnknown(telemetry.result_status) ?? "")],
    ["Job type", jobTypeLabel(stringFromUnknown(job.job_type))],
    ["Root", target.root],
    ["Path", target.path],
    ["Profile", stringFromUnknown(payload.profile_name)],
    ["Requested by", stringFromUnknown(payload.requested_by)],
    ["Claimed by", stringFromUnknown(payload.claimed_by)],
    ["Asset id", stringFromUnknown(payload.asset_id)],
    ["Source id", stringFromUnknown(payload.source_id)],
    ["Progress", progress],
    ["Stage", humanizeIdentifier(stringFromUnknown(telemetry.stage) ?? "")],
    ["Stage progress", stageProgressSummary(telemetry)],
    ["Paths", countProgressSummary(telemetry.paths_done, telemetry.paths_total)],
    ["Files", countProgressSummary(telemetry.files_done ?? telemetry.files_seen, telemetry.files_total)],
    ["Current path", stringFromUnknown(telemetry.current_path ?? telemetry.path)],
    ["Files seen", stringFromUnknown(telemetry.files_seen) ?? numberFromUnknown(telemetry.files_seen)?.toString()],
    ["Files changed", stringFromUnknown(telemetry.files_changed) ?? numberFromUnknown(telemetry.files_changed)?.toString()],
    ["Jobs queued", stringFromUnknown(telemetry.jobs_queued) ?? numberFromUnknown(telemetry.jobs_queued)?.toString()],
    ["Previous status", statusLabel(stringFromUnknown(telemetry.obsolete_previous_status) ?? "")],
    ["Obsolete reason", obsoleteReasonDetailLabel(telemetry)],
    ["Delete requested", formatDate(stringFromUnknown(job.delete_requested_at))],
    ["Delete requested by", stringFromUnknown(job.delete_requested_by)],
    ["Delete reason", stringFromUnknown(job.delete_reason)],
    ["Attempts", String(numberFromUnknown(job.attempts) ?? 0)],
    ["Created", formatDate(stringFromUnknown(job.created_at))],
    ["Updated", formatDate(stringFromUnknown(job.updated_at))]
  ].filter(([, value]) => value && value !== "-");
  return (
    <tr className="job-detail-row">
      <td colSpan={9}>
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
          <JobConsoleOutput state={toolInvocations} />
        </div>
      </td>
    </tr>
  );
}

function JobConsoleOutput({ state }: { state?: JobToolInvocationState }) {
  const invocations = state?.invocations ?? [];
  return (
    <section className="job-console-panel">
      <div className="job-console-header">
        <h3><Terminal size={15} /> Console output</h3>
        {state?.loading ? <span className="state-pill info">Refreshing</span> : null}
      </div>
      {state?.error ? <p className="warning-note">{state.error}</p> : null}
      {!state || (state.loading && invocations.length === 0) ? <p className="muted">Loading console output.</p> : null}
      {state && !state.loading && invocations.length === 0 && !state.error ? <p className="muted">No console output captured for this job.</p> : null}
      <div className="job-console-list">
        {invocations.map((invocation, index) => (
          <details className="job-console-invocation" key={invocation.id ?? `invocation-${index}`} open={index === invocations.length - 1}>
            <summary>
              <span>{toolInvocationCommand(invocation.command)}</span>
              <strong>{humanizeIdentifier(invocation.status ?? "unknown")}</strong>
              {typeof invocation.return_code === "number" ? <em>exit {invocation.return_code}</em> : null}
            </summary>
            <dl className="job-console-meta">
              {invocation.cwd ? <div><dt>Cwd</dt><dd>{invocation.cwd}</dd></div> : null}
              {invocation.started_at ? <div><dt>Started</dt><dd>{formatDate(invocation.started_at)}</dd></div> : null}
              {invocation.completed_at ? <div><dt>Completed</dt><dd>{formatDate(invocation.completed_at)}</dd></div> : null}
              {typeof invocation.duration_ms === "number" ? <div><dt>Duration</dt><dd>{invocation.duration_ms} ms</dd></div> : null}
              {invocation.exception_type ? <div><dt>Exception</dt><dd>{invocation.exception_type}</dd></div> : null}
            </dl>
            {invocation.exception_message ? <p className="job-console-exception">{invocation.exception_message}</p> : null}
            <div className="job-console-streams">
              <div>
                <strong>stdout</strong>
                <pre>{invocation.stdout || ""}</pre>
              </div>
              <div>
                <strong>stderr</strong>
                <pre>{invocation.stderr || ""}</pre>
              </div>
            </div>
          </details>
        ))}
      </div>
    </section>
  );
}

function JobStatusBadge({ status }: { status: string }) {
  const tone = ["completed", "indexed"].includes(status)
    ? "enabled"
    : ["pending", "running", "processing", "processing_staged", "completed_staged", "retrying_locked", "staged"].includes(status)
      ? "info"
      : status === "failed"
        ? "error"
        : status.startsWith("blocked") || status.startsWith("cancelled") || status === "metadata_only" || status === "completed_metadata_only"
          ? "warning"
          : "";
  return <span className={`state-pill ${tone}`}>{statusLabel(status)}</span>;
}

function jobId(job: Record<string, unknown>, index: number) {
  return stringFromUnknown(job.id) ?? `job-${index + 1}`;
}

function jobPayload(job: Record<string, unknown>) {
  return job.payload && typeof job.payload === "object" && !Array.isArray(job.payload) ? job.payload as Record<string, unknown> : {};
}

function jobTelemetry(job: Record<string, unknown>) {
  return job.telemetry && typeof job.telemetry === "object" && !Array.isArray(job.telemetry) ? job.telemetry as Record<string, unknown> : {};
}

function jobDisplayStatus(job: Record<string, unknown>, status: string) {
  const telemetry = jobTelemetry(job);
  const resultStatus = stringFromUnknown(telemetry.result_status);
  if (status === "completed" && resultStatus === "metadata_only") return "completed_metadata_only";
  if (status === "completed" && resultStatus === "staged") return "completed_staged";
  return status;
}

function toolInvocationCommand(command: unknown): string {
  if (Array.isArray(command)) return command.map((part) => String(part)).join(" ");
  const text = stringFromUnknown(command);
  return text ?? "command";
}

function jobProgressSummary(job: Record<string, unknown>) {
  const telemetry = jobTelemetry(job);
  const explicit = stringFromUnknown(telemetry.progress_label);
  if (explicit) return explicit;
  const terminal = terminalJobProgressLabel(job, telemetry);
  if (terminal) return terminal;
  const parts = [
    countProgressSummary(telemetry.paths_done, telemetry.paths_total, "Paths"),
    stageProgressSummary(telemetry),
    countProgressSummary(telemetry.files_done ?? telemetry.files_seen, telemetry.files_total, "files")
  ].filter(Boolean);
  if (parts.length > 0) return parts.join(", ");
  const percent = numberFromUnknown(telemetry.progress_percent);
  if (percent !== undefined) return `${percent}%`;
  const stage = stringFromUnknown(telemetry.stage);
  return stage ? humanizeIdentifier(stage) : "-";
}

function terminalJobProgressLabel(job: Record<string, unknown>, telemetry: Record<string, unknown>) {
  const status = stringFromUnknown(job.status) ?? "";
  const resultStatus = stringFromUnknown(telemetry.result_status);
  if (status === "completed") {
    return resultStatus ? statusLabel(resultStatus) : "Completed";
  }
  if (status === "obsolete") {
    return obsoleteJobReasonLabel(job) ?? "Obsolete";
  }
  return undefined;
}

function obsoleteJobReasonLabel(job: Record<string, unknown>) {
  const telemetry = jobTelemetry(job);
  const reason = stringFromUnknown(telemetry.obsolete_reason);
  if (reason === "maintenance_reprocess_derived_state") return "Maintenance obsolete";
  return undefined;
}

function obsoleteReasonDetailLabel(telemetry: Record<string, unknown>) {
  const reason = stringFromUnknown(telemetry.obsolete_reason);
  if (!reason) return undefined;
  if (reason === "maintenance_reprocess_derived_state") return "Maintenance reprocess derived state";
  return sentenceLabel(reason);
}

function sentenceLabel(value: string) {
  const label = humanizeIdentifier(value);
  return label ? `${label.charAt(0).toUpperCase()}${label.slice(1).toLowerCase()}` : "";
}

function stageProgressSummary(telemetry: Record<string, unknown>) {
  const stage = stringFromUnknown(telemetry.stage);
  const stageIndex = numberFromUnknown(telemetry.stage_index);
  const stageTotal = numberFromUnknown(telemetry.stage_total);
  if (stageIndex !== undefined && stageTotal !== undefined && stage) return `stage ${stageIndex}/${stageTotal} ${stage}`;
  if (stage) return humanizeIdentifier(stage);
  return undefined;
}

function countProgressSummary(doneValue: unknown, totalValue: unknown, label?: string) {
  const done = numberFromUnknown(doneValue);
  const total = numberFromUnknown(totalValue);
  if (done === undefined || total === undefined) return undefined;
  const prefix = label ? `${label} ` : "";
  return `${prefix}${done}/${total}`;
}

function jobTarget(job: Record<string, unknown>, payload: Record<string, unknown>) {
  const syncRootJob = stringFromUnknown(job.job_type) === "corpus_sync_root";
  const path = stringFromUnknown(payload.path)
    ?? stringFromUnknown(payload.canonical_path)
    ?? stringFromUnknown(payload.file_path)
    ?? stringFromUnknown(payload.profile_name)
    ?? stringFromUnknown(job.path)
    ?? (syncRootJob ? "Root sync" : undefined)
    ?? "No path";
  const root = stringFromUnknown(payload.root_name) ?? stringFromUnknown(job.root_name) ?? "-";
  return { path, root };
}

function isActionableJobTarget(job: Record<string, unknown>, payload: Record<string, unknown>, target: { path: string; root: string }) {
  const type = stringFromUnknown(job.job_type) ?? "";
  return type.startsWith("corpus_") && Boolean(stringFromUnknown(payload.path)) && target.path !== "Root sync" && target.path !== "No path";
}

function jobTypeLabel(value?: string) {
  return humanizeIdentifier(value?.replace(/^corpus_/, "") ?? "job");
}

function isCancelableCorpusJob(job: Record<string, unknown>, status: string) {
  const type = stringFromUnknown(job.job_type) ?? "";
  return type.startsWith("corpus_") && ["pending", "retrying_locked", "running"].includes(status);
}

function isRetryableCorpusJob(job: Record<string, unknown>, status: string) {
  const type = stringFromUnknown(job.job_type) ?? "";
  const deletionRequested = Boolean(stringFromUnknown(job.delete_requested_at));
  if (deletionRequested || status === "obsolete") return false;
  return type.startsWith("corpus_") && (
    status === "failed"
    || status === "retrying_locked"
    || status.startsWith("blocked_")
    || status.startsWith("cancelled_")
  );
}

function isDeletionMarkableCorpusJob(job: Record<string, unknown>, status: string) {
  const type = stringFromUnknown(job.job_type) ?? "";
  const deletionRequested = Boolean(stringFromUnknown(job.delete_requested_at));
  if (deletionRequested || status === "obsolete") return false;
  return type.startsWith("corpus_") && (
    status === "failed"
    || status.startsWith("blocked_")
    || status.startsWith("cancelled_")
  );
}

function isDeletionRestorableCorpusJob(job: Record<string, unknown>, status: string) {
  const type = stringFromUnknown(job.job_type) ?? "";
  const deletionRequested = Boolean(stringFromUnknown(job.delete_requested_at));
  return type.startsWith("corpus_") && status === "obsolete" && deletionRequested;
}

function nextJobSort(current: JobSortState, key: JobSortKey): JobSortState {
  if (current.sort_by === key) {
    return { sort_by: key, sort_dir: current.sort_dir === "asc" ? "desc" : "asc" };
  }
  return { sort_by: key, sort_dir: "asc" };
}

function activeOutlookRequests(requests?: OutlookSyncRequest[]) {
  return (requests ?? []).filter((request) => ["pending", "claimed", "running"].includes(request.status ?? "pending"));
}

function outlookRequestJob(request: OutlookSyncRequest): Record<string, unknown> {
  const status = request.status ?? "pending";
  return {
    id: request.id,
    job_type: "outlook_sync_request",
    status,
    path: request.profile_name ?? "Outlook request",
    root_name: "Outlook COM",
    attempts: 0,
    last_error: request.error ?? null,
    created_at: request.created_at,
    updated_at: request.updated_at ?? request.created_at,
    payload: {
      profile_name: request.profile_name,
      requested_by: request.requested_by,
      claimed_by: request.claimed_by,
      result: request.result
    }
  };
}

const STATUS_LABELS: Record<string, string> = {
  blocked_by_policy: "Blocked by policy",
  blocked_invalid_source: "Invalid source",
  blocked_missing_dependency: "Missing dependency",
  obsolete: "Obsolete"
};

function statusLabel(value: string) {
  return STATUS_LABELS[value] ?? humanizeIdentifier(value);
}

function humanizeIdentifier(value: string) {
  return value
    .replace(/[_-]+/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => formatIdentifierWord(word))
    .join(" ");
}

function modelActivityCountText(activity: ModelActivityPayload) {
  return `${activity.recent_count ?? activity.events?.length ?? 0} recent / ${activity.active_count ?? 0} active`;
}

function isModelActivityIssue(event: ModelActivityEvent) {
  return event.status === "failed" || event.status === "busy" || event.status === "blocked_missing_dependency";
}

function modelActivityIssueLabel(event: ModelActivityEvent) {
  const service = event.service ?? "model activity";
  if (event.status === "blocked_missing_dependency") return `${service} missing dependency`;
  if (event.status === "busy") return `${service} busy`;
  return `${service} failure`;
}

function modelActivityServiceRows(events: ModelActivityEvent[]) {
  const rows = new Map<string, { count: number; active: number; failures: number }>();
  for (const event of events) {
    const service = event.service ?? "unknown";
    const row = rows.get(service) ?? { count: 0, active: 0, failures: 0 };
    row.count += 1;
    if (event.status === "running") row.active += 1;
    if (event.status === "failed") row.failures += 1;
    rows.set(service, row);
  }
  return Array.from(rows.entries())
    .sort((left, right) => right[1].count - left[1].count || left[0].localeCompare(right[0]))
    .map(([service, row]) => [
      service,
      `${row.count} event${row.count === 1 ? "" : "s"}`,
      `${row.active} active / ${row.failures} failed`
    ] as [string, string, string]);
}

function modelActivityClassRows(events: ModelActivityEvent[]) {
  const counts = new Map<string, number>();
  for (const event of events) {
    const activityClass = event.activity_class ?? "sidecar";
    counts.set(activityClass, (counts.get(activityClass) ?? 0) + 1);
  }
  return Array.from(counts.entries())
    .sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]))
    .map(([activity_class, count]) => ({ activity_class, count }));
}

function isControlPlaneActivity(value?: string) {
  const normalized = String(value ?? "").toLowerCase();
  return normalized === "health" || normalized === "control_plane";
}

function modelActivityNewestFirst(left: ModelActivityEvent, right: ModelActivityEvent) {
  const rightTime = modelActivitySortTime(right);
  const leftTime = modelActivitySortTime(left);
  if (rightTime !== leftTime) return rightTime - leftTime;
  const rightStarted = Date.parse(right.started_at ?? "") || 0;
  const leftStarted = Date.parse(left.started_at ?? "") || 0;
  if (rightStarted !== leftStarted) return rightStarted - leftStarted;
  return String(right.id ?? "").localeCompare(String(left.id ?? ""));
}

function modelActivitySortTime(event: ModelActivityEvent) {
  return Date.parse(event.completed_at ?? event.started_at ?? "") || 0;
}

function modelActivityEventDetail(event: ModelActivityEvent) {
  const parts = [
    event.model,
    event.caller_surface ? humanizeIdentifier(event.caller_surface) : null,
    event.activity_class ? modelActivityClassLabel(event.activity_class) : null
  ].filter(Boolean);
  return parts.join("; ") || humanizeIdentifier(event.action ?? "activity");
}

function modelActivityClassLabel(value: string) {
  return humanizeIdentifier(value);
}

function modelActivityEventTiming(event: ModelActivityEvent) {
  const duration = typeof event.duration_ms === "number" ? `${event.duration_ms}ms` : null;
  const when = event.completed_at ?? event.started_at ?? null;
  return [duration, when ? formatDate(when) : null].filter(Boolean).join("; ") || "-";
}

function formatSchedulerLeaseCounts(scheduler: ModelActivityScheduler) {
  return `${scheduler.running_count ?? 0} running / ${scheduler.waiting_count ?? 0} waiting`;
}

function formatSchedulerRejections(scheduler: ModelActivityScheduler) {
  return `${scheduler.rejections ?? 0} rejected / ${scheduler.timeouts ?? 0} timed out`;
}

function formatSchedulerEvictions(scheduler: ModelActivityScheduler) {
  const count = scheduler.evictions_recent_count ?? 0;
  return `${count} recent eviction${count === 1 ? "" : "s"}`;
}

function formatGpuMemory(memory?: ModelActivityScheduler["live_gpu_memory"]) {
  if (!memory?.available || memory.used_mb == null || memory.total_mb == null) return "Unavailable";
  return `${Math.round(memory.used_mb)}/${Math.round(memory.total_mb)} MB`;
}

function jobsModelActivityHint(activity: ModelActivityPayload) {
  const scheduler = activity.scheduler ?? {};
  const hasModelActivity = (activity.active_count ?? 0) > 0 || (activity.recent_count ?? 0) > 0 || (activity.events?.length ?? 0) > 0;
  const hasSchedulerActivity = schedulerHasActivity(scheduler);
  if (!hasModelActivity && !hasSchedulerActivity) return "";
  let source = "Recent model activity was detected.";
  if (hasModelActivity && hasSchedulerActivity) {
    source = "Recent model activity and GPU scheduler activity were detected.";
  } else if (hasSchedulerActivity) {
    source = "GPU scheduler activity was detected.";
  }
  const running = scheduler.running_count ?? 0;
  const waiting = scheduler.waiting_count ?? 0;
  const schedulerDetail = hasSchedulerActivity
    ? ` ${running} running ${running === 1 ? "lease" : "leases"}, ${waiting} waiting ${waiting === 1 ? "request" : "requests"}.`
    : "";
  return `No active crawl jobs. ${source}${schedulerDetail}`;
}

function schedulerHasActivity(scheduler: ModelActivityScheduler) {
  return [
    scheduler.running_count,
    scheduler.waiting_count,
    scheduler.recent_count,
    scheduler.rejections,
    scheduler.timeouts,
    scheduler.evictions_recent_count
  ].some((value) => (value ?? 0) > 0)
    || (scheduler.resident_models?.length ?? 0) > 0
    || Boolean(scheduler.live_gpu_memory?.available);
}

function formatPercentMetric(value: number | undefined) {
  const numeric = typeof value === "number" && Number.isFinite(value) ? value : 0;
  return `${(numeric * 100).toFixed(1)}%`;
}

function formatSignedPercentMetric(value: number | undefined) {
  if (typeof value !== "number" || !Number.isFinite(value)) return "+0.0%";
  const signed = value >= 0 ? "+" : "";
  return `${signed}${(value * 100).toFixed(1)}%`;
}

function formatDecimal(value: number | undefined, digits: number) {
  const numeric = typeof value === "number" && Number.isFinite(value) ? value : 0;
  return numeric.toFixed(digits);
}

function formatReasonList(values?: string[]) {
  const rows = values?.length ? values : ["observed"];
  return rows.map((value) => value.replace(/[_-]+/g, " ").toLowerCase()).join(", ");
}

function benchmarkSettingsMutated(run: RetrievalBenchmarkRun) {
  return Boolean(run.recommendations?.settings_mutated ?? run.recommendation_metadata?.settings_mutated);
}

function GovernanceShadowEvidence({ run }: { run?: RetrievalBenchmarkRun }) {
  const summary = run?.recommendations?.governance_shadow ?? run?.recommendation_metadata?.governance_shadow;
  if (!summary) return null;
  return (
    <div className="diagnostic-item">
      <strong>Governance shadow evaluation</strong>
      <span>proposal precision {formatPercentMetric(summary.proposal_precision)}</span>
      <em>guardrails {summary.guardrail_pass_count ?? 0}/{summary.guardrail_case_count ?? 0} passed</em>
    </div>
  );
}

function retrievalConfidenceBandRows(run?: RetrievalBenchmarkRun) {
  const bands = run?.calibration_summary?.confidence_bands ?? {};
  return Object.entries(bands).filter(([, count]) => Number(count) > 0);
}

function retrievalRecommendationCandidates(run?: RetrievalBenchmarkRun) {
  return run?.recommendations?.candidates ?? run?.recommendation_metadata?.candidates ?? [];
}

function failedRetrievalCaseLabel(item: RetrievalBenchmarkCaseResult) {
  const category = item.category ? humanizeIdentifier(item.category).toLowerCase() : `${(item.observed_ids ?? []).length} observed`;
  const confidence = item.confidence_band ? `${humanizeIdentifier(item.confidence_band)} confidence`.toLowerCase() : "confidence unknown";
  return `${category} - ${confidence}`;
}

function formatIdentifierWord(word: string) {
  const upper = word.toUpperCase();
  if (["API", "HTML", "ID", "IMAP", "JSON", "OCR", "PDF", "UID", "URL", "VSS"].includes(upper)) return upper;
  return `${word.charAt(0).toUpperCase()}${word.slice(1).toLowerCase()}`;
}

function HealthStrip({ health, mail, blockedJobs }: { health: HealthPayload; mail: MailStatus; blockedJobs: number }) {
  const runtime = health.runtime ?? {};
  const databaseChecks = health.database?.checks ?? {};
  const apiDatabase = databaseChecks.service ?? runtime.postgresql;
  const hostDatabase = databaseChecks.host_published;
  const databaseBlocked = [apiDatabase, hostDatabase].find((check) => check && check.required !== false && check.ok === false);
  const databaseOk = health.database?.ok ?? apiDatabase?.ok;
  const databaseHint = databaseBlocked?.message ?? health.database?.message ?? apiDatabase?.message ?? "database";
  return (
    <section className="health-strip" aria-label="Health summary">
      <MetricCard icon={<Database />} label="Database paths" value={databaseOk ? "Healthy" : "Blocked"} hint={databaseHint} tone={databaseOk ? "green" : "red"} />
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
  pendingActions,
  onSelect,
  onSync,
  onEdit,
  onDelete
}: {
  profiles: MailProfile[];
  selectedProfile?: MailProfile;
  oauthProfiles: Array<{ profile_name?: string; status?: string }>;
  hostStatus: string;
  mailErrors: number;
  pendingActions: PendingActionState;
  onSelect: (profile: MailProfile) => void;
  onSync: (profile: MailProfile) => void;
  onEdit: (profile: MailProfile) => void;
  onDelete: (profile: MailProfile) => void;
}) {
  const [openMenu, setOpenMenu] = useState<{ profileName: string; top: number; left: number } | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const menuButtonRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    if (!openMenu) return undefined;
    function closeOnPointer(event: MouseEvent) {
      const target = event.target as Node;
      if (menuRef.current?.contains(target) || menuButtonRef.current?.contains(target)) {
        return;
      }
      setOpenMenu(null);
    }
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setOpenMenu(null);
      }
    }
    function closeOnViewportChange() {
      setOpenMenu(null);
    }
    document.addEventListener("mousedown", closeOnPointer);
    document.addEventListener("keydown", closeOnEscape);
    window.addEventListener("resize", closeOnViewportChange);
    window.addEventListener("scroll", closeOnViewportChange, true);
    return () => {
      document.removeEventListener("mousedown", closeOnPointer);
      document.removeEventListener("keydown", closeOnEscape);
      window.removeEventListener("resize", closeOnViewportChange);
      window.removeEventListener("scroll", closeOnViewportChange, true);
    };
  }, [openMenu]);

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
        {profiles.map((profile) => {
          const deleting = pendingLabel(pendingActions, mailProfileActionKey("delete", profile.name));
          const syncing = pendingLabel(pendingActions, mailProfileActionKey("sync", profile.name));
          const rowPending = Boolean(deleting);
          return (
          <tr
            key={profile.name}
            className={`${profile.name === selectedProfile?.name ? "selected" : ""}${rowPending ? " pending-row" : ""}`}
            aria-busy={rowPending ? "true" : undefined}
            onClick={() => { if (!rowPending) onSelect(profile); }}
          >
            <td>
              <button className="row-select" type="button" aria-label={`Select ${profile.name}`} disabled={rowPending} onClick={(event) => { event.stopPropagation(); onSelect(profile); }}>
                {profile.name === selectedProfile?.name ? <Play size={14} fill="currentColor" /> : <Square size={12} />}
              </button>
              <div>
                <strong>{profile.name}</strong>
                <span>{profile.source_type === "outlook_com" ? "Catch-up" : "Primary Capture"}</span>
                {deleting ? <span className="row-pending-label"><Clock3 size={13} /> {deleting}</span> : null}
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
                <button type="button" aria-label={`Sync ${profile.name}`} title={syncing ?? `Sync ${profile.name} now`} disabled={rowPending || Boolean(syncing)} onClick={(event) => { event.stopPropagation(); onSync(profile); }}>{syncing ? <Clock3 size={15} /> : <RefreshCcw size={15} />}</button>
                <button type="button" aria-label={`Edit ${profile.name}`} title={`Edit ${profile.name}`} disabled={rowPending} onClick={(event) => { event.stopPropagation(); onEdit(profile); }}><Wrench size={15} /></button>
                <div className="menu-wrap row-menu-wrap">
                  <button
                    type="button"
                    aria-label={`More ${profile.name}`}
                    aria-haspopup="menu"
                    aria-expanded={openMenu?.profileName === profile.name}
                    title={`Open ${profile.name} actions`}
                    disabled={rowPending}
                    onClick={(event) => {
                      event.stopPropagation();
                      if (openMenu?.profileName === profile.name) {
                        setOpenMenu(null);
                        return;
                      }
                      menuButtonRef.current = event.currentTarget;
                      const nextPosition = rowMenuPosition(event.currentTarget.getBoundingClientRect());
                      setOpenMenu({ profileName: profile.name, ...nextPosition });
                    }}
                  >
                    <MoreVertical size={15} />
                  </button>
                  {openMenu?.profileName === profile.name && createPortal(
                    <div
                      ref={menuRef}
                      className="action-menu row-action-menu portal-action-menu"
                      role="menu"
                      aria-label={`${profile.name} profile actions`}
                      style={{ position: "fixed", top: `${openMenu.top}px`, left: `${openMenu.left}px`, width: MAIL_ROW_MENU_WIDTH }}
                      onClick={(event) => event.stopPropagation()}
                    >
                      <button role="menuitem" type="button" onClick={() => { setOpenMenu(null); onSelect(profile); }}>
                        <FileText size={15} /> View details
                      </button>
                      <button className="danger-menu-item" role="menuitem" type="button" onClick={() => { setOpenMenu(null); onDelete(profile); }}>
                        <Trash2 size={15} /> Delete profile
                      </button>
                    </div>,
                    document.body
                  )}
                </div>
              </div>
            </td>
          </tr>
        );
        })}
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
  onOAuthPathSave,
  pendingActions
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
  pendingActions: PendingActionState;
}) {
  const profileClientPath = oauthClientConfigPath(profile);
  const [clientPath, setClientPath] = useState(profileClientPath);
  useEffect(() => {
    setClientPath(profileClientPath);
  }, [profile?.name, profileClientPath]);
  if (!profile) return <div className="empty-inspector"><p className="muted">Select or create a mail profile.</p></div>;
  const oauthStatus = oauthProfile?.status ?? (profile.source_type === "imap" ? "blocked_auth_required" : "");
  const syncing = pendingLabel(pendingActions, mailProfileActionKey("sync", profile.name));
  const savingOAuthPath = pendingLabel(pendingActions, mailProfileActionKey("oauth-client-save", profile.name));
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
            disabled={Boolean(savingOAuthPath)}
            onClick={() => onOAuthPathSave(clientPath)}
          >
            {savingOAuthPath ? <Clock3 size={15} /> : <CheckCircle2 size={15} />} {savingOAuthPath ?? "Save OAuth client JSON path"}
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
        <button className="sync-selected" type="button" title={`Sync ${profile.name} now`} disabled={Boolean(syncing)} onClick={onSync}>
          {syncing ? <Clock3 size={17} /> : <RefreshCcw size={17} />}
          {syncing ?? "Sync selected profile"}
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
    ...(() => {
      const sourceType = profile?.source_type === "outlook_com" ? "outlook_com" : "imap";
      const outlook = sourceType === "outlook_com";
      return {
        name: profile?.name ?? "gmail-capture",
        source_type: sourceType,
        account: outlook ? "" : profile?.account ?? "me@gmail.com",
        server: outlook ? "" : profile?.server ?? "imap.gmail.com",
        folder_paths: (profile?.folder_paths ?? ["FluxCapture"]).join("\n"),
        spool_path: profile?.spool_path ?? "private/mail-spool/gmail-capture",
        post_process_policy: outlook ? "none" : profile?.post_process_policy ?? "move_to_processed",
        processed_folder: metadataString(metadata, "processed_folder", outlook ? "" : "FluxProcessed"),
        trash_folder: metadataString(metadata, "trash_folder", ""),
        destructive_post_process_confirmed: outlook ? false : Boolean(metadata.destructive_post_process_confirmed),
        sync_enabled: Boolean(profile?.sync_enabled),
        sync_interval_seconds: profile?.sync_interval_seconds ?? 900,
        sync_window_days: profile?.sync_window_days ?? 30,
        max_messages_per_run: profile?.max_messages_per_run ?? 200,
        include_subfolders: outlook ? metadataBoolean(metadata, "include_subfolders", true) : false,
        outlook_incremental_basis: outlookIncrementalBasis(metadata)
      };
    })()
  }));
  const outlook = form.source_type === "outlook_com";

  function update<K extends keyof ProfileForm>(key: K, value: ProfileForm[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  function updateSource(sourceType: ProfileForm["source_type"]) {
    setForm((current) => {
      if (sourceType === "outlook_com") {
        return {
          ...current,
          source_type: "outlook_com",
          name: current.name === "gmail-capture" ? "outlook-catchup" : current.name,
          account: "",
          server: "",
          folder_paths: current.folder_paths === "FluxCapture" ? "Mailbox - Me\\Inbox\\Flux Capture" : current.folder_paths,
          spool_path: current.spool_path === "private/mail-spool/gmail-capture" ? "private/mail-spool/outlook-catchup" : current.spool_path,
          post_process_policy: "none",
          processed_folder: "",
          trash_folder: "",
          destructive_post_process_confirmed: false,
          sync_enabled: false,
          include_subfolders: true,
          outlook_incremental_basis: "received_time"
        };
      }
      return {
        ...current,
        source_type: "imap",
        name: current.name === "outlook-catchup" ? "gmail-capture" : current.name,
        account: current.account || "me@gmail.com",
        server: current.server || "imap.gmail.com",
        folder_paths: current.folder_paths === "Mailbox - Me\\Inbox\\Flux Capture" ? "FluxCapture" : current.folder_paths,
        spool_path: current.spool_path === "private/mail-spool/outlook-catchup" ? "private/mail-spool/gmail-capture" : current.spool_path,
        post_process_policy: current.post_process_policy === "none" ? "move_to_processed" : current.post_process_policy,
        processed_folder: current.processed_folder || "FluxProcessed",
        include_subfolders: false,
        outlook_incremental_basis: "received_time"
      };
    });
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
          <label>Source<select value={form.source_type} onChange={(event) => updateSource(event.target.value as ProfileForm["source_type"])}>
            <option value="imap">IMAP</option>
            <option value="outlook_com">Outlook COM</option>
          </select></label>
          {outlook && <p className="muted span-2">Outlook COM uses the local Windows Outlook host. It reads the selected Outlook folder through the desktop profile, not an IMAP server.</p>}
          {!outlook && <label>Account<input value={form.account} onChange={(event) => update("account", event.target.value)} /></label>}
          {!outlook && <label>Server<input value={form.server} onChange={(event) => update("server", event.target.value)} /></label>}
          <label className="span-2">Folders or labels<textarea value={form.folder_paths} onChange={(event) => update("folder_paths", event.target.value)} required /></label>
          <label className="span-2">Private spool path<input value={form.spool_path} onChange={(event) => update("spool_path", event.target.value)} required /></label>
          <label>Post process<select value={form.post_process_policy} onChange={(event) => update("post_process_policy", event.target.value)}>
            <option value="move_to_processed" disabled={outlook}>Move to processed</option>
            <option value="remove_label" disabled={outlook}>Remove label</option>
            <option value="none">Leave in place</option>
            <option value="trash" disabled={outlook}>Trash/delete</option>
          </select></label>
          {!outlook && <label>Processed folder or label<input value={form.processed_folder} onChange={(event) => update("processed_folder", event.target.value)} /></label>}
          {!outlook && <label>Trash folder<input value={form.trash_folder} onChange={(event) => update("trash_folder", event.target.value)} /></label>}
          {outlook && <label className="checkbox-label span-2"><input type="checkbox" checked={form.include_subfolders} onChange={(event) => update("include_subfolders", event.target.checked)} /> Include subfolders</label>}
          {outlook && <label>Outlook incremental mode<select value={form.outlook_incremental_basis} onChange={(event) => update("outlook_incremental_basis", event.target.value as ProfileForm["outlook_incremental_basis"])}>
            <option value="received_time">New received mail</option>
            <option value="last_modification_time">Moved or changed mail</option>
          </select></label>}
          {form.sync_enabled && <label>Interval seconds<input type="number" min="60" value={form.sync_interval_seconds} onChange={(event) => update("sync_interval_seconds", Number(event.target.value))} /></label>}
          <label>Window days<input type="number" min="1" value={form.sync_window_days} onChange={(event) => update("sync_window_days", Number(event.target.value))} /></label>
          <label>Max messages/run<input type="number" min="1" value={form.max_messages_per_run} onChange={(event) => update("max_messages_per_run", Number(event.target.value))} /></label>
          <label className="checkbox-label"><input type="checkbox" checked={form.sync_enabled} onChange={(event) => update("sync_enabled", event.target.checked)} /> Scheduled sync enabled</label>
          {!outlook && <label className="checkbox-label"><input type="checkbox" checked={form.destructive_post_process_confirmed} onChange={(event) => update("destructive_post_process_confirmed", event.target.checked)} /> Confirm destructive post-process action</label>}
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

function ConfirmDialog({
  title,
  body,
  confirmLabel,
  pending = false,
  pendingLabel: pendingText,
  onCancel,
  onConfirm
}: {
  title: string;
  body: string;
  confirmLabel: string;
  pending?: boolean;
  pendingLabel?: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div className="modal-backdrop top-layer">
      <div className="modal confirm-modal" role="dialog" aria-modal="true" aria-labelledby="confirm-dialog-title">
        <header>
          <h2 id="confirm-dialog-title">{title}</h2>
          <button type="button" aria-label="Close confirmation" disabled={pending} onClick={onCancel}><X size={18} /></button>
        </header>
        <p>{body}</p>
        <footer>
          <button className="ghost-action compact" type="button" disabled={pending} onClick={onCancel}>Cancel</button>
          <button className="small-primary" type="button" disabled={pending} onClick={onConfirm}>
            {pending ? <Clock3 size={15} /> : null}
            {pendingText ?? confirmLabel}
          </button>
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
        ["Search index", "retrieval", `${health.retrieval?.search_index_records ?? 0} records`],
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

function overviewAttentionItems(state: LoadState, hostStatus: string) {
  const items: Array<{ label: string; detail: string }> = [];
  const blockedJobs = state.health.jobs?.blocked ?? 0;
  const failedJobs = state.health.jobs?.failed ?? 0;
  const mailErrors = state.mail.errored_messages ?? 0;
  if (blockedJobs > 0) items.push({ label: "Blocked jobs", detail: `${blockedJobs} job${blockedJobs === 1 ? "" : "s"} need Diagnostics review.` });
  if (failedJobs > 0) items.push({ label: "Failed jobs", detail: `${failedJobs} job${failedJobs === 1 ? "" : "s"} failed recently.` });
  if (mailErrors > 0) items.push({ label: "Mail errors", detail: `${mailErrors} message or profile issue${mailErrors === 1 ? "" : "s"} need Mail review.` });
  if (hostStatus !== "running") items.push({ label: "Outlook host", detail: hostStatusLabel(hostStatus) });
  if (state.health.codex?.restart_required) items.push({ label: "Codex integration", detail: "Restart Codex after reviewing Settings/System." });
  if (items.length === 0) items.push({ label: "No urgent attention", detail: "Core services look healthy from the latest dashboard refresh." });
  return items.slice(0, 5);
}

function overviewAutomationItems(state: LoadState) {
  const items: Array<{ label: string; detail: string }> = [];
  const pending = state.health.jobs?.pending ?? 0;
  const workers = state.health.workers?.active ?? 0;
  items.push({ label: "Worker processing", detail: `${workers} worker${workers === 1 ? "" : "s"} active; ${pending} pending job${pending === 1 ? "" : "s"}.` });
  items.push({ label: "Safe recoveries", detail: "Diagnostics can run only non-destructive recovery buttons." });
  items.push({ label: "Governance", detail: "Automation proposes shadow governance actions before any manual apply." });
  return items;
}

function overviewNextAction(items: Array<{ label: string; detail: string }>) {
  const first = items[0];
  if (!first || first.label === "No urgent attention") return "No immediate action is required. Review Automation for optional guarded evidence refreshes.";
  if (/blocked|failed/i.test(first.label)) return "Open Diagnostics and run only the suggested non-destructive remediation buttons.";
  if (/mail/i.test(first.label)) return "Open Mail and inspect the affected profile before changing any mailbox policy.";
  if (/codex/i.test(first.label)) return "Open Settings, review Codex hook status, then restart Codex manually if needed.";
  return first.detail;
}

function readDashboardState(): SavedDashboardState {
  const params = new URLSearchParams(window.location.search);
  const tab = normalizeTabId(params.get("tab"));
  const root = params.get("root");
  const profile = params.get("profile");
  let saved: SavedDashboardState = {};
  try {
    saved = normalizeDashboardState(JSON.parse(localStorage.getItem(DASHBOARD_STATE_KEY) ?? "{}"));
  } catch {
    saved = {};
  }
  if (tab) {
    return { ...saved, activeTab: tab, selectedRootName: root ?? "", selectedName: profile ?? "" };
  }
  return saved;
}

function normalizeDashboardState(value: unknown): SavedDashboardState {
  if (!isRecord(value)) return {};
  return {
    activeTab: normalizeTabId(value.activeTab) ?? "overview",
    selectedName: stringFromUnknown(value.selectedName) ?? "",
    selectedRootName: stringFromUnknown(value.selectedRootName) ?? "",
    jobFilters: normalizeJobHistoryFilters(value.jobFilters),
    jobSort: normalizeJobSort(value.jobSort)
  };
}

function writeDashboardState(value: SavedDashboardState) {
  localStorage.setItem(DASHBOARD_STATE_KEY, JSON.stringify(value));
  const params = new URLSearchParams();
  if (value.activeTab && value.activeTab !== "overview") params.set("tab", value.activeTab);
  if (value.selectedRootName) params.set("root", value.selectedRootName);
  if (value.selectedName) params.set("profile", value.selectedName);
  const query = params.toString();
  const nextUrl = query ? `/dashboard?${query}` : "/dashboard";
  if (`${window.location.pathname}${window.location.search}` !== nextUrl) {
    window.history.replaceState(null, "", nextUrl);
  }
}

function jobHistoryUrl(filters: JobHistoryFilters, offset: number, sort: JobSortState = defaultJobSort) {
  const safeOffset = Math.max(0, offset);
  const hasSort = hasJobSort(sort);
  if (!hasJobHistoryFilters(filters) && safeOffset === 0 && !hasSort) return "/api/dashboard/jobs";
  const params = new URLSearchParams();
  params.set("limit", String(JOB_PAGE_LIMIT));
  params.set("offset", String(safeOffset));
  if (hasSort) {
    params.set("sort_by", sort.sort_by);
    params.set("sort_dir", sort.sort_dir);
  }
  filters.status.forEach((status) => params.append("status", status));
  filters.root_name.forEach((root) => params.append("root_name", root));
  filters.job_type.forEach((type) => params.append("job_type", type));
  const updatedFrom = datetimeLocalToIso(filters.updated_from);
  const updatedTo = datetimeLocalToIso(filters.updated_to);
  if (updatedFrom) params.set("updated_from", updatedFrom);
  if (updatedTo) params.set("updated_to", updatedTo);
  return `/api/dashboard/jobs?${params.toString()}`;
}

function modelActivityHistoryUrl(includeControlPlane: boolean, offset: number) {
  const params = new URLSearchParams();
  params.set("limit", String(MODEL_ACTIVITY_PAGE_LIMIT));
  params.set("offset", String(Math.max(0, offset)));
  if (includeControlPlane) params.set("include_control_plane", "true");
  return `/api/dashboard/model-activity?${params.toString()}`;
}

function hasJobHistoryFilters(filters: JobHistoryFilters) {
  return Boolean(filters.status.length || filters.root_name.length || filters.job_type.length || filters.updated_from || filters.updated_to);
}

function hasJobSort(sort: JobSortState) {
  return sort.sort_by !== defaultJobSort.sort_by || sort.sort_dir !== defaultJobSort.sort_dir;
}

function normalizeJobHistoryFilters(value: unknown): JobHistoryFilters {
  if (!isRecord(value)) return emptyJobHistoryFilters;
  return {
    status: stringListFromUnknown(value.status),
    root_name: stringListFromUnknown(value.root_name),
    job_type: stringListFromUnknown(value.job_type),
    updated_from: stringFromUnknown(value.updated_from) ?? "",
    updated_to: stringFromUnknown(value.updated_to) ?? ""
  };
}

function normalizeJobSort(value: unknown): JobSortState {
  if (!isRecord(value)) return defaultJobSort;
  const sortBy = stringFromUnknown(value.sort_by);
  const sortDir = stringFromUnknown(value.sort_dir);
  return {
    sort_by: sortBy && jobSortKeys.has(sortBy as JobSortKey) ? sortBy as JobSortKey : defaultJobSort.sort_by,
    sort_dir: sortDir === "asc" || sortDir === "desc" ? sortDir : defaultJobSort.sort_dir
  };
}

function stringListFromUnknown(value: unknown): string[] {
  const rawValues = Array.isArray(value) ? value : [value];
  const values: string[] = [];
  const seen = new Set<string>();
  rawValues.forEach((item) => {
    const clean = stringFromUnknown(item);
    if (clean && !seen.has(clean)) {
      values.push(clean);
      seen.add(clean);
    }
  });
  return values;
}

function toggleStringValue(values: string[], value: string) {
  return values.includes(value) ? values.filter((item) => item !== value) : [...values, value];
}

function multiFilterSummary(values: string[], allLabel: string, pluralLabel: string, optionLabel: (value: string) => string) {
  if (values.length === 0) return allLabel;
  if (values.length === 1) return optionLabel(values[0]);
  return `${values.length} ${pluralLabel}`;
}

function datetimeLocalToIso(value: string) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toISOString();
}

function normalizeTabId(value: unknown): TabId | undefined {
  const raw = String(value ?? "").trim().toLowerCase();
  const normalized = raw === "health" ? "overview" : raw;
  return navItems.some((item) => item.id === normalized) ? normalized as TabId : undefined;
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

function mailProfileSpoolCleanupMessage(payload: MailProfileDeleteResponse) {
  const status = String(payload.spool?.status ?? "");
  if (status === "blocked") {
    return `Spool cleanup blocked: ${payload.spool?.blocked_reason ?? "strict private mail-spool guard refused the configured path"}.`;
  }
  if (status === "failed") {
    return `Spool cleanup failed: ${payload.spool?.error ?? "check the private spool path"}.`;
  }
  if (status === "missing") {
    return "Private spool was already missing.";
  }
  if (status === "deleted") {
    return "Private spool deleted.";
  }
  return "";
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

function metadataBoolean(metadata: Record<string, unknown>, key: string, fallback: boolean) {
  const value = metadata[key];
  if (value === undefined || value === null) return fallback;
  if (typeof value === "boolean") return value;
  if (typeof value === "string") return !["", "0", "false", "no", "off"].includes(value.trim().toLowerCase());
  return Boolean(value);
}

function outlookIncrementalBasis(metadata: Record<string, unknown>): ProfileForm["outlook_incremental_basis"] {
  const value = metadataString(metadata, "outlook_incremental_basis", "received_time").replace(/-/g, "_").toLowerCase();
  return value === "last_modification_time" ? "last_modification_time" : "received_time";
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

function toastTone(message: string): ToastTone {
  const text = message.toLowerCase();
  if (/(failed|failure|could not|error|invalid|denied|timed out|auth_failed|auth_expired)/.test(text)) {
    return "error";
  }
  if (/(queued|pending|started|blocked|rejected|unavailable|cannot run|locked|deleted|missing)/.test(text)) {
    return "warning";
  }
  return "success";
}

function fileActionToast(action: "open" | "reveal", payload: FileActionResponse, labelOverride?: string): ToastState | null {
  const state = payload.state;
  const label = labelOverride ?? (action === "open" ? "Open" : "Reveal");
  const detail = payload.message ? `: ${payload.message}` : "";
  if (state === "opened") return null;
  if (state === "missing") return { message: `${label} request could not find the file${detail}.`, tone: "warning" };
  if (state === "deleted") return { message: `${label} request is unavailable because the asset is deleted from the index${detail}.`, tone: "warning" };
  if (state === "locked") return { message: `${label} request could not access the file because it is locked${detail}.`, tone: "warning" };
  if (state === "host_agent_offline") return { message: `${label} request cannot run because the host agent is offline${detail}.`, tone: "warning" };
  if (state === "not_allowed") return { message: `${label} request was rejected${detail}.`, tone: "warning" };
  return { message: `${label} request finished with state ${state ?? "unknown"}${detail}.`, tone: "warning" };
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

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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

function MiniTable({ rows, label = "Dashboard summary table" }: { rows: MiniTableRow[]; label?: string }) {
  const wide = rows.some((row) => row.length > 3);
  return (
    <div className="mini-table-wrap">
      <table className="mini-table" data-columns={wide ? 4 : 3} aria-label={label}>
        <tbody>
          {rows.map((row) => (
            <tr key={row.join("-")}>
              {row.map((cell, index) => (
                <td key={`${cell}-${index}`} className={index === row.length - 1 ? "mini-table-emphasis" : undefined}>
                  {cell}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function codeResultLabel(result: CodeSearchResult) {
  return result.symbol ?? result.target_symbol ?? result.target ?? "code result";
}

function lineRangeLabel(result: CodeSearchResult) {
  if (result.line_start == null) return "";
  if (result.line_end != null && result.line_end !== result.line_start) {
    return `L${result.line_start}-L${result.line_end}`;
  }
  return `L${result.line_start}`;
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

function automationNextWindow(recurring: AutomationRecurring, policy: Record<string, unknown>) {
  if (recurring.due === true) return "Due now";
  if (typeof recurring.remaining_seconds === "number" && recurring.remaining_seconds > 0) {
    return `in ${intervalLabel(recurring.remaining_seconds)}`;
  }
  if (recurring.next_run_at) return formatDate(recurring.next_run_at);
  if (typeof policy.next_run_at === "string") return formatDate(policy.next_run_at);
  if (typeof policy.next_run_after_seconds === "number") return intervalLabel(policy.next_run_after_seconds);
  return "-";
}

function hostStatusLabel(status: string) {
  if (status === "host_offline") return "Host offline";
  if (status === "host_stale") return "Host stale";
  if (status === "host_error") return "Host error";
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
