import {
  AlertCircle,
  Archive,
  BriefcaseBusiness,
  CheckCircle2,
  ChevronDown,
  Clock3,
  Database,
  Folder,
  Gauge,
  HeartPulse,
  Inbox,
  KeyRound,
  ListFilter,
  Mail,
  MoreVertical,
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
import { useEffect, useMemo, useState } from "react";

type HealthPayload = {
  database?: { ok?: boolean; message?: string };
  runtime?: Record<string, { ok?: boolean; message?: string; required?: boolean }>;
  watcher?: { active_roots?: number; disabled_roots?: number; stale_count?: number; roots?: unknown[] };
  jobs?: { pending?: number; failed?: number; blocked?: number };
  retrieval?: { episodes?: number; sources?: number; source_assets?: number; asset_chunks?: number; embeddings?: number };
  workers?: {
    active?: number;
    components?: Array<{ name?: string; status?: string; heartbeat_age_seconds?: number | null; metadata?: Record<string, unknown> }>;
  };
  recent_errors?: string[];
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
  };
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

type MailStatus = {
  enabled_profiles?: number;
  exported_messages?: number;
  errored_messages?: number;
  profiles?: MailProfile[];
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
  title?: string;
  excerpt?: string;
  score?: number;
  id?: string;
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

type TabId = "health" | "corpus" | "mail" | "settings" | "retrieval" | "jobs";

type ProfileForm = {
  name: string;
  source_type: "imap" | "outlook_com";
  account: string;
  server: string;
  folder_paths: string;
  spool_path: string;
  post_process_policy: string;
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
          setToast(`IMAP sync ${status} for ${profile.name}; exported ${exported} message${exported === 1 ? "" : "s"}.`);
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

  async function runSearch(event: FormEvent) {
    event.preventDefault();
    const query = searchQuery.trim();
    if (!query) {
      setToast("Enter a search query first.");
      return;
    }
    const results = await sendJson<SearchResult[]>("/api/search", "POST", { query, limit: 8 });
    setSearchResults(Array.isArray(results) ? results : []);
    setSearchOpen(true);
    setActiveTab("retrieval");
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
          <div className={`toast ${currentToastTone}`} role={currentToastTone === "error" ? "alert" : "status"}>
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
          <RetrievalTab state={state} searchOpen={searchOpen} searchResults={searchResults} query={searchQuery} onClear={() => { setSearchOpen(false); setSearchResults([]); }} onErrorDetail={setErrorDetail} />
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

      {errorDetail && (
        <InfoDialog title="Error detail" onClose={() => setErrorDetail(null)}>
          <p>{errorDetail}</p>
          <p className="muted">This error is reported by the shared health service and may represent an optional local extractor/tool dependency.</p>
        </InfoDialog>
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
            onSync={() => selectedProfile && onSyncProfile(selectedProfile)}
            onEdit={() => selectedProfile && onEditProfile(selectedProfile)}
            onOAuthStart={(clientPath) => selectedProfile && onOAuthStart(selectedProfile, clientPath)}
            onOAuthPathSave={(clientPath) => selectedProfile && onOAuthPathSave(selectedProfile, clientPath)}
          />
        </Panel>
      </section>

      <section className="lower-grid">
        <MailStatusPanel mail={state.mail} hostStatus={hostStatus} showOutlook={hasOutlookProfiles} />
        <MailErrorsPanel mail={state.mail} errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
        {hasOutlookProfiles && <OutlookHostPanel host={host} hostStatus={hostStatus} pending={state.outlook.pending_requests?.length ?? 0} />}
      </section>
    </>
  );
}

function MailStatusPanel({ mail, hostStatus, showOutlook }: { mail: MailStatus; hostStatus: string; showOutlook: boolean }) {
  const rows = [
    ["Profiles", "enabled", String(mail.enabled_profiles ?? 0)],
    ["Exports", "messages", String(mail.exported_messages ?? 0)],
    ["Errors", "messages", String(mail.errored_messages ?? 0)]
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

function HealthTab({ state, hostStatus, restartRows, onErrorDetail, onApplySettings }: { state: LoadState; hostStatus: string; restartRows: SettingRow[]; onErrorDetail: (error: string) => void; onApplySettings: () => void }) {
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
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
      <Panel title={`Runtime Actions (${restartRows.length})`} action={<button className="small-primary" type="button" onClick={onApplySettings}>Apply acknowledged</button>}>
        <SettingsPreview rows={restartRows} />
      </Panel>
    </section>
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
        {roots.map((root) => (
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
            </td>
            <td>
              <strong>{root.job_counts?.pending ?? 0} pending</strong>
              <span>{root.job_counts?.blocked ?? 0} blocked - {root.job_counts?.failed ?? 0} failed</span>
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
        ))}
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
  const tone = ["watching", "indexed", "completed"].includes(normalized)
    ? "enabled"
    : ["queued", "processing", "crawling", "changed", "watch_enabled"].includes(normalized)
      ? "info"
      : ["blocked", "failed", "stale", "deleted", "blocked_missing_dependency"].includes(normalized)
        ? "warning"
        : "";
  return <span className={`state-pill ${tone}`}>{normalized}</span>;
}

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

function RetrievalTab({ state, searchOpen, searchResults, query, onClear, onErrorDetail }: { state: LoadState; searchOpen: boolean; searchResults: SearchResult[]; query: string; onClear: () => void; onErrorDetail: (error: string) => void }) {
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
      </Panel>
      <Panel title={searchOpen ? `Search Results: ${query}` : "Search Results"}>
        {searchResults.length > 0 ? (
          <div className="search-results">
            {searchResults.map((result, index) => (
              <article key={`${result.title}-${index}`}>
                <span>{result.kind ?? "result"} {typeof result.score === "number" ? `- ${Math.round(result.score * 100)}%` : ""}</span>
                <strong>{result.title ?? result.id ?? "Untitled result"}</strong>
                <p>{result.excerpt ?? "No excerpt available."}</p>
              </article>
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
          <code>{'kb.search({"query":"customer RFP","limit":5})'}</code>
          <code>{'kb.brief({"query":"customer RFP","token_budget":1200})'}</code>
          <code>flux-kb search "customer RFP" --limit 5</code>
        </div>
      </Panel>
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
    </section>
  );
}

function JobsTab({ state, onRefresh }: { state: LoadState; onRefresh: () => void }) {
  const jobs = state.jobs.jobs ?? [];
  return (
    <section className="tab-grid">
      <Panel title="Job Queue" action={<button className="small-primary" type="button" onClick={onRefresh}><RefreshCcw size={15} /> Refresh</button>}>
        <DataRows rows={jobs} empty="No queued extraction jobs." />
      </Panel>
      <BacklogPanel health={state.health} blockedJobs={state.health.jobs?.blocked ?? 0} />
    </section>
  );
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
  onSync,
  onEdit,
  onOAuthStart,
  onOAuthPathSave
}: {
  profile?: MailProfile;
  hostStatus: string;
  hostCommand?: string;
  oauthProfile?: { profile_name?: string; status?: string; expires_at?: string | null; has_refresh_token?: boolean };
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

function ProfileDialog({ profile, onClose, onSave }: { profile?: MailProfile; onClose: () => void; onSave: (form: ProfileForm) => void }) {
  const [form, setForm] = useState<ProfileForm>(() => ({
    name: profile?.name ?? "gmail-capture",
    source_type: (profile?.source_type === "outlook_com" ? "outlook_com" : "imap"),
    account: profile?.account ?? "me@gmail.com",
    server: profile?.server ?? "imap.gmail.com",
    folder_paths: (profile?.folder_paths ?? ["FluxCapture"]).join("\n"),
    spool_path: profile?.spool_path ?? "private/mail-spool/gmail-capture",
    post_process_policy: profile?.post_process_policy ?? "move_to_processed",
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
            <option value="none">Leave in place</option>
            <option value="trash">Trash/delete</option>
          </select></label>
          <label>Interval seconds<input type="number" min="60" value={form.sync_interval_seconds} onChange={(event) => update("sync_interval_seconds", Number(event.target.value))} /></label>
          <label>Window days<input type="number" min="1" value={form.sync_window_days} onChange={(event) => update("sync_window_days", Number(event.target.value))} /></label>
          <label>Max messages/run<input type="number" min="1" value={form.max_messages_per_run} onChange={(event) => update("max_messages_per_run", Number(event.target.value))} /></label>
          <label className="checkbox-label"><input type="checkbox" checked={form.sync_enabled} onChange={(event) => update("sync_enabled", event.target.checked)} /> Scheduled sync enabled</label>
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

function MiniTable({ rows }: { rows: Array<[string, string, string]> }) {
  return (
    <div className="mini-table">
      {rows.map(([kind, source, status]) => (
        <div key={`${kind}-${source}`}>
          <span>{kind}</span>
          <span>{source}</span>
          <strong>{status}</strong>
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
