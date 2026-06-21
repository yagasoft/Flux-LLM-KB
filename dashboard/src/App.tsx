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
  recent_errors?: string[];
  extractors?: Record<string, { ok?: boolean; message?: string }>;
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
    profiles?: Array<{ profile_name?: string; status?: string; expires_at?: string | null }>;
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
  roots?: Array<Record<string, unknown>>;
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

export default function App() {
  const [state, setState] = useState<LoadState>(emptyState);
  const [activeTab, setActiveTab] = useState<TabId>("mail");
  const [selectedName, setSelectedName] = useState<string>("");
  const [loading, setLoading] = useState(true);
  const [toast, setToast] = useState<string>("");
  const [debugOpen, setDebugOpen] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [profileDialog, setProfileDialog] = useState<MailProfile | "new" | null>(null);
  const [settingEditor, setSettingEditor] = useState<SettingRow | null>(null);
  const [settingValue, setSettingValue] = useState("");
  const [confirmSetting, setConfirmSetting] = useState<SettingRow | null>(null);
  const [errorDetail, setErrorDetail] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState<SearchResult[]>([]);
  const [searchOpen, setSearchOpen] = useState(false);
  const [theme, setTheme] = useState(() => localStorage.getItem("flux-dashboard-theme") ?? "light");

  async function load() {
    setLoading(true);
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
    setLoading(false);
  }

  useEffect(() => {
    void load();
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("flux-dashboard-theme", theme);
  }, [theme]);

  const profiles = state.mail.profiles ?? [];
  const selectedProfile = useMemo(() => {
    return profiles.find((profile) => profile.name === selectedName) ?? profiles.find((profile) => profile.source_type === "outlook_com") ?? profiles[0];
  }, [profiles, selectedName]);

  useEffect(() => {
    if (!selectedName && selectedProfile?.name) {
      setSelectedName(selectedProfile.name);
    }
  }, [selectedName, selectedProfile?.name]);

  useEffect(() => {
    if (settingEditor) {
      setSettingValue(String(settingEditor.value ?? ""));
    }
  }, [settingEditor]);

  async function requestProfileSync(profile = selectedProfile) {
    if (!profile) {
      setToast("Select a mail profile first.");
      return;
    }
    setToast(`Sync request queued for ${profile.name}...`);
    if (profile.source_type === "outlook_com") {
      const payload = await sendJson<{ status?: string }>("/api/outlook-host/request-sync", "POST", { profile_name: profile.name });
      setToast(payload?.status ? `Outlook sync request ${payload.status}.` : "Outlook sync request queued.");
    } else {
      const payload = await sendJson<{ count?: number }>("/api/mail/sync", "POST", { profile_name: profile.name });
      setToast(`IMAP sync completed for ${payload?.count ?? 0} profile(s).`);
    }
    await load();
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
    const payload = await sendJson<{ auth_url?: string; status?: string }>("/api/mail/oauth/gmail/start", "POST", {
      profile_name: profile.name,
      client_config_path: clientConfigPath
    });
    if (payload.auth_url) {
      window.open(payload.auth_url, "_blank", "noopener,noreferrer");
      setToast("Gmail OAuth opened in a new browser tab.");
    } else {
      setToast(payload.status ?? "Gmail OAuth setup started.");
    }
    await load();
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
            <button className="primary-action" type="button" onClick={() => void requestProfileSync()}>
              <RefreshCcw size={17} />
              Sync Now
            </button>
            <div className="menu-wrap">
              <button className="ghost-action" aria-label="More actions" type="button" onClick={() => setMoreOpen((open) => !open)}>
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
          <div className="toast" role="status">
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
            blockedJobs={blockedJobs}
            restartRows={restartRows}
            debugOpen={debugOpen}
            onDebugToggle={() => setDebugOpen((open) => !open)}
            onAddProfile={() => setProfileDialog("new")}
            onEditProfile={setProfileDialog}
            onSelectProfile={(profile) => setSelectedName(profile.name)}
            onSyncProfile={(profile) => void requestProfileSync(profile)}
            onErrorDetail={setErrorDetail}
            onSettingsTab={() => setActiveTab("settings")}
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
            onRefresh={() => void load()}
            onSync={() => void sendJson("/api/crawl/sync", "POST", { dry_run: false }).then(() => load()).then(() => setToast("Corpus sync completed."))}
            onWatch={(enabled) => void sendJson("/api/crawl/watch", "POST", { enabled }).then(() => load()).then(() => setToast(enabled ? "Watch enabled." : "Watch disabled."))}
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

      {selectedProfile?.source_type === "imap" && activeTab === "mail" && (
        <OAuthAction profile={selectedProfile} onStart={(clientPath) => void startGmailOAuth(selectedProfile, clientPath)} />
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
  blockedJobs,
  restartRows,
  debugOpen,
  onDebugToggle,
  onAddProfile,
  onEditProfile,
  onSelectProfile,
  onSyncProfile,
  onErrorDetail,
  onSettingsTab
}: {
  state: LoadState;
  loading: boolean;
  profiles: MailProfile[];
  selectedProfile?: MailProfile;
  hostStatus: string;
  host: OutlookStatus["host"];
  oauthProfiles: Array<{ profile_name?: string; status?: string }>;
  mailErrors: number;
  blockedJobs: number;
  restartRows: SettingRow[];
  debugOpen: boolean;
  onDebugToggle: () => void;
  onAddProfile: () => void;
  onEditProfile: (profile: MailProfile) => void;
  onSelectProfile: (profile: MailProfile) => void;
  onSyncProfile: (profile: MailProfile) => void;
  onErrorDetail: (error: string) => void;
  onSettingsTab: () => void;
}) {
  return (
    <>
      <section className="main-grid">
        <Panel className="profiles-panel" title="Mail Profiles" action={<button className="small-primary" type="button" onClick={onAddProfile}><Plus size={15} /> Add Profile</button>}>
          <div className="table-toolbar">
            <span>{loading ? "Refreshing..." : `Showing ${profiles.length} profile${profiles.length === 1 ? "" : "s"}`}</span>
            <div>
              <button className="icon-button" type="button" aria-label="Filter profiles"><SlidersHorizontal size={18} /></button>
              <button className="icon-button" type="button" aria-label="Profile table options"><MoreVertical size={18} /></button>
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
            onSync={() => selectedProfile && onSyncProfile(selectedProfile)}
            onEdit={() => selectedProfile && onEditProfile(selectedProfile)}
          />
        </Panel>
      </section>

      <section className="lower-grid">
        <BacklogPanel health={state.health} blockedJobs={blockedJobs} />
        <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
        <Panel title={`Settings Changes Requiring Restart (${restartRows.length})`} action={<button className="ghost-action compact" type="button" onClick={onSettingsTab}>Review</button>}>
          <SettingsPreview rows={restartRows} />
        </Panel>
      </section>

      <section className="host-grid">
        <OutlookHostPanel host={host} hostStatus={hostStatus} pending={state.outlook.pending_requests?.length ?? 0} />
        <Panel title="Developer Debug Drawer" action={<button className="ghost-action compact" type="button" onClick={onDebugToggle}><Terminal size={15} /> {debugOpen ? "Hide" : "Open"}</button>}>
          <p className="muted">Raw payloads are kept out of the primary dashboard. Open this drawer only for diagnostics.</p>
          {debugOpen && <pre className="debug-drawer">{JSON.stringify(state, null, 2)}</pre>}
        </Panel>
      </section>
    </>
  );
}

function HealthTab({ state, hostStatus, restartRows, onErrorDetail, onApplySettings }: { state: LoadState; hostStatus: string; restartRows: SettingRow[]; onErrorDetail: (error: string) => void; onApplySettings: () => void }) {
  const runtimeRows = Object.entries(state.health.runtime ?? {});
  return (
    <section className="tab-grid">
      <Panel title="System Health">
        <div className="status-grid">
          <StatusTile label="Database" ok={state.health.database?.ok} message={state.health.database?.message} />
          {runtimeRows.map(([key, value]) => <StatusTile key={key} label={key} ok={value.ok} message={value.message} />)}
          <StatusTile label="Outlook Host" ok={hostStatus === "running"} message={hostStatusLabel(hostStatus)} />
        </div>
      </Panel>
      <RecentErrors errors={state.health.recent_errors ?? []} onErrorDetail={onErrorDetail} />
      <Panel title={`Runtime Actions (${restartRows.length})`} action={<button className="small-primary" type="button" onClick={onApplySettings}>Apply acknowledged</button>}>
        <SettingsPreview rows={restartRows} />
      </Panel>
    </section>
  );
}

function CorpusTab({ state, onRefresh, onSync, onWatch }: { state: LoadState; onRefresh: () => void; onSync: () => void; onWatch: (enabled: boolean) => void }) {
  const roots = state.crawl.roots ?? [];
  const status = state.crawl.status ?? {};
  return (
    <section className="tab-grid">
      <Panel title="Corpus Monitor" action={<button className="small-primary" type="button" onClick={onSync}><RefreshCcw size={15} /> Sync corpus</button>}>
        <div className="corpus-actions">
          <button className="ghost-action compact" type="button" onClick={onRefresh}>Refresh</button>
          <button className="ghost-action compact" type="button" onClick={() => onWatch(true)}>Enable watch</button>
          <button className="ghost-action compact" type="button" onClick={() => onWatch(false)}>Disable watch</button>
        </div>
        <MiniTable rows={[
          ["Roots", "configured", String(roots.length)],
          ["Active watch", "status", String(status.active_watch_roots ?? state.health.watcher?.active_roots ?? 0)],
          ["Disabled watch", "status", String(status.disabled_watch_roots ?? state.health.watcher?.disabled_roots ?? 0)],
          ["Stale", "watcher", String(state.health.watcher?.stale_count ?? 0)]
        ]} />
      </Panel>
      <Panel title="Monitored Roots">
        <DataRows rows={roots} empty="No monitored roots configured yet." />
      </Panel>
      <Panel title="Extractor Availability">
        <div className="status-grid">
          {Object.entries(state.health.extractors ?? {}).map(([key, value]) => <StatusTile key={key} label={key} ok={value.ok} message={value.message} />)}
          {Object.keys(state.health.extractors ?? {}).length === 0 && <p className="muted">Extractor status is not available yet.</p>}
        </div>
      </Panel>
    </section>
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
            <td>{(profile.folder_paths ?? []).slice(0, 2).join(" / ") || "-"}</td>
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
                <button type="button" aria-label={`Sync ${profile.name}`} onClick={(event) => { event.stopPropagation(); onSync(profile); }}><RefreshCcw size={15} /></button>
                <button type="button" aria-label={`Edit ${profile.name}`} onClick={(event) => { event.stopPropagation(); onEdit(profile); }}><Wrench size={15} /></button>
                <button type="button" aria-label={`More ${profile.name}`} onClick={(event) => { event.stopPropagation(); onSelect(profile); }}><MoreVertical size={15} /></button>
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

function Inspector({ profile, hostStatus, hostCommand, onSync, onEdit }: { profile?: MailProfile; hostStatus: string; hostCommand?: string; onSync: () => void; onEdit: () => void }) {
  if (!profile) return <div className="empty-inspector"><p className="muted">Select or create a mail profile.</p></div>;
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
        <button className="sync-selected" type="button" onClick={onSync}>
          <RefreshCcw size={17} />
          Sync selected profile
        </button>
        <button className="ghost-action compact" type="button" onClick={onEdit}>
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

function OAuthAction({ profile, onStart }: { profile: MailProfile; onStart: (clientPath: string) => void }) {
  const [open, setOpen] = useState(false);
  const [clientPath, setClientPath] = useState("private/google-oauth-client.json");
  if (!open) {
    return (
      <button className="floating-oauth" type="button" onClick={() => setOpen(true)}>
        <KeyRound size={16} /> Gmail OAuth
      </button>
    );
  }
  return (
    <div className="floating-card">
      <header>
        <strong>Gmail OAuth for {profile.name}</strong>
        <button type="button" aria-label="Close Gmail OAuth" onClick={() => setOpen(false)}><X size={16} /></button>
      </header>
      <label>Private client JSON<input value={clientPath} onChange={(event) => setClientPath(event.target.value)} /></label>
      <button className="small-primary" type="button" onClick={() => onStart(clientPath)}>Start Gmail OAuth</button>
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

async function getJson<T>(url: string, fallback: T): Promise<T> {
  try {
    const response = await fetch(url);
    if (!response.ok) return fallback;
    return await response.json();
  } catch {
    return fallback;
  }
}

async function sendJson<T>(url: string, method: "POST" | "PUT", body: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status}`);
  }
  return await response.json();
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
  const oauth = oauthProfiles.find((row) => row.profile_name === profile.name)?.status ?? "OAuth2";
  return <span className={String(oauth).includes("expired") ? "auth warn" : "auth good"}><CheckCircle2 size={16} /> {oauth}</span>;
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
