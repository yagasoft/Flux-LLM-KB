import {
  AlertCircle,
  Archive,
  BriefcaseBusiness,
  CheckCircle2,
  ChevronDown,
  Clock3,
  CloudCog,
  Database,
  Folder,
  Gauge,
  HeartPulse,
  Inbox,
  ListFilter,
  Mail,
  MoreVertical,
  Play,
  RefreshCcw,
  Search,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Square,
  Terminal,
  Wrench
} from "lucide-react";
import type { ReactNode } from "react";
import { useEffect, useMemo, useState } from "react";

type HealthPayload = {
  database?: { ok?: boolean; message?: string };
  runtime?: Record<string, { ok?: boolean; message?: string }>;
  watcher?: { active_roots?: number; disabled_roots?: number; stale_count?: number };
  jobs?: { pending?: number; failed?: number; blocked?: number };
  retrieval?: { episodes?: number; sources?: number; source_assets?: number; asset_chunks?: number; embeddings?: number };
  recent_errors?: string[];
};

type MailProfile = {
  name: string;
  source_type: "imap" | "outlook_com" | string;
  account?: string | null;
  server?: string | null;
  folder_paths?: string[];
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
    profiles?: Array<{ profile_name?: string; status?: string }>;
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

type LoadState = {
  health: HealthPayload;
  mail: MailStatus;
  outlook: OutlookStatus;
  settings: SettingRow[];
};

const emptyState: LoadState = {
  health: {},
  mail: { profiles: [] },
  outlook: { profiles: [], pending_requests: [] },
  settings: []
};

export default function App() {
  const [state, setState] = useState<LoadState>(emptyState);
  const [selectedName, setSelectedName] = useState<string>("");
  const [loading, setLoading] = useState(true);
  const [syncMessage, setSyncMessage] = useState<string>("");
  const [debugOpen, setDebugOpen] = useState(false);

  async function load() {
    setLoading(true);
    const [health, mail, outlook, settings] = await Promise.all([
      getJson<HealthPayload>("/api/dashboard/health", {}),
      getJson<MailStatus>("/api/mail/status", { profiles: [] }),
      getJson<OutlookStatus>("/api/outlook-host/status", { profiles: [], pending_requests: [] }),
      getJson<SettingRow[]>("/api/settings", [])
    ]);
    setState({ health, mail, outlook, settings });
    setLoading(false);
  }

  useEffect(() => {
    void load();
  }, []);

  const profiles = state.mail.profiles ?? [];
  const selectedProfile = useMemo(() => {
    return profiles.find((profile) => profile.name === selectedName) ?? profiles.find((profile) => profile.source_type === "outlook_com") ?? profiles[0];
  }, [profiles, selectedName]);

  useEffect(() => {
    if (!selectedName && selectedProfile?.name) {
      setSelectedName(selectedProfile.name);
    }
  }, [selectedName, selectedProfile?.name]);

  async function requestOutlookSync() {
    if (!selectedProfile) return;
    setSyncMessage("Sync request queued...");
    const response = await fetch("/api/outlook-host/request-sync", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ profile_name: selectedProfile.name })
    });
    const payload = await response.json();
    setSyncMessage(payload?.status ? `Request ${payload.status}` : "Request queued");
    await load();
  }

  const health = state.health;
  const runtime = health.runtime ?? {};
  const host = state.outlook.host ?? {};
  const hostStatus = host.status ?? "host_offline";
  const mailErrors = state.mail.errored_messages ?? 0;
  const blockedJobs = health.jobs?.blocked ?? 0;
  const oauthProfiles = state.mail.oauth?.profiles ?? [];

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
          <NavItem icon={<HeartPulse size={20} />} label="Health" />
          <NavItem icon={<Folder size={20} />} label="Corpus" />
          <NavItem icon={<Mail size={20} />} label="Mail" active />
          <NavItem icon={<Settings size={20} />} label="Settings" />
          <NavItem icon={<Search size={20} />} label="Retrieval" />
          <NavItem icon={<ListFilter size={20} />} label="Jobs" />
        </nav>
        <div className="sidebar-card">
          <span>Version 0.5.0</span>
          <strong>Build: 2026-06-21</strong>
        </div>
        <div className="theme-toggle" aria-label="Theme">
          <button className="active">Light</button>
          <button>Dark</button>
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
            <div className="search-box">
              <Search size={17} />
              <span>Search anything...</span>
              <kbd>Ctrl K</kbd>
            </div>
            <button className="primary-action" onClick={() => void requestOutlookSync()} disabled={!selectedProfile}>
              <RefreshCcw size={17} />
              Sync Now
            </button>
            <button className="ghost-action">
              More <ChevronDown size={16} />
            </button>
          </div>
        </header>

        <section className="health-strip" aria-label="Health summary">
          <MetricCard icon={<Database />} label="PostgreSQL" value={runtime.postgresql?.ok ? "Healthy" : "Blocked"} hint={runtime.postgresql?.message ?? health.database?.message ?? "database"} tone={runtime.postgresql?.ok ? "green" : "red"} />
          <MetricCard icon={<Gauge />} label="Watcher" value={(health.watcher?.active_roots ?? 0) > 0 ? "Running" : "Idle"} hint={`${health.watcher?.active_roots ?? 0} active roots - ${health.watcher?.stale_count ?? 0} stale`} tone="blue" />
          <MetricCard icon={<Mail />} label="Mail Worker" value={(state.mail.enabled_profiles ?? 0) > 0 ? "Ready" : "Stopped"} hint={`${state.mail.enabled_profiles ?? 0} profiles enabled`} tone="purple" />
          <MetricCard icon={<BriefcaseBusiness />} label="Blocked Jobs" value={String(blockedJobs)} hint={`${health.jobs?.pending ?? 0} pending - ${health.jobs?.failed ?? 0} failed`} tone="orange" />
        </section>

        <section className="main-grid">
          <Panel className="profiles-panel" title="Mail Profiles" action={<button className="small-primary">+ Add Profile</button>}>
            <div className="table-toolbar">
              <span>{loading ? "Refreshing..." : `Showing ${profiles.length} profile${profiles.length === 1 ? "" : "s"}`}</span>
              <div>
                <SlidersHorizontal size={18} />
                <MoreVertical size={18} />
              </div>
            </div>
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
                  <tr key={profile.name} className={profile.name === selectedProfile?.name ? "selected" : ""} onClick={() => setSelectedName(profile.name)}>
                    <td>
                      <button className="row-select" aria-label={`Select ${profile.name}`}>
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
                        <button aria-label={`Sync ${profile.name}`} onClick={(event) => { event.stopPropagation(); setSelectedName(profile.name); void requestOutlookSync(); }}><RefreshCcw size={15} /></button>
                        <button aria-label={`Edit ${profile.name}`}><Wrench size={15} /></button>
                        <button aria-label={`More ${profile.name}`}><MoreVertical size={15} /></button>
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
          </Panel>

          <Panel className="inspector-panel" title="Profile Details" action={<span className={selectedProfile?.sync_enabled ? "state-pill enabled" : "state-pill"}>{selectedProfile?.sync_enabled ? "Enabled" : "Manual"}</span>}>
            <Inspector profile={selectedProfile} hostStatus={hostStatus} hostCommand={host.command} syncMessage={syncMessage} onSync={() => void requestOutlookSync()} />
          </Panel>
        </section>

        <section className="lower-grid">
          <Panel title={`Extraction Backlog (${health.jobs?.pending ?? 0})`}>
            <MiniTable rows={[
              ["OCR", "mail", "queued"],
              ["Video", "corpus", blockedJobs ? "blocked" : "idle"],
              ["Embeddings", "retrieval", `${health.retrieval?.embeddings ?? 0} vectors`],
              ["Assets", "corpus", `${health.retrieval?.asset_chunks ?? 0} chunks`]
            ]} />
          </Panel>
          <Panel title="Recent Errors">
            <div className="error-list">
              {(health.recent_errors ?? []).slice(0, 4).map((error, index) => (
                <div className="error-item" key={`${error}-${index}`}>
                  <span className="error-dot" />
                  <div>
                    <strong>{error}</strong>
                    <span>Captured from shared health service</span>
                  </div>
                  <button>View</button>
                </div>
              ))}
              {(health.recent_errors ?? []).length === 0 && <p className="muted">No recent errors.</p>}
            </div>
          </Panel>
          <Panel title={`Settings Changes Requiring Restart (${restartSettings(state.settings).length})`}>
            <div className="settings-list">
              {restartSettings(state.settings).slice(0, 4).map((setting) => (
                <div className="settings-row" key={setting.key}>
                  <span>{setting.key}</span>
                  <strong>{String(setting.value)}</strong>
                  <em>{setting.apply_mode}</em>
                </div>
              ))}
              {restartSettings(state.settings).length === 0 && <p className="muted">No pending restart-class settings.</p>}
            </div>
          </Panel>
        </section>

        <section className="host-grid">
          <Panel title="Outlook COM host" action={<span className={`state-pill ${hostStatus === "running" ? "enabled" : "warning"}`}>{hostStatus}</span>}>
            <div className="host-layout">
              <div>
                <p className="muted">Outlook COM runs outside Docker in the logged-in Windows user session. Docker services coordinate through this API and the shared private spool.</p>
                <code>{host.command ?? "flux-kb outlook-host run"}</code>
              </div>
              <div className="host-stats">
                <Stat label="Heartbeat" value={formatDate(host.heartbeat_at)} />
                <Stat label="Pending requests" value={String(state.outlook.pending_requests?.length ?? 0)} />
                <Stat label="Last error" value={host.last_error ?? "-"} />
              </div>
            </div>
          </Panel>
          <Panel title="Developer Debug Drawer" action={<button className="ghost-action compact" onClick={() => setDebugOpen((open) => !open)}><Terminal size={15} /> {debugOpen ? "Hide" : "Open"}</button>}>
            <p className="muted">Raw payloads are kept out of the primary dashboard. Open this drawer only for diagnostics.</p>
            {debugOpen && <pre className="debug-drawer">{JSON.stringify(state, null, 2)}</pre>}
          </Panel>
        </section>
      </main>
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

function NavItem({ icon, label, active = false }: { icon: ReactNode; label: string; active?: boolean }) {
  return <a className={active ? "nav-item active" : "nav-item"} href="#">{icon}<span>{label}</span></a>;
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
  return <span className={oauth.includes("expired") ? "auth warn" : "auth good"}><CheckCircle2 size={16} /> {oauth}</span>;
}

function Inspector({ profile, hostStatus, hostCommand, syncMessage, onSync }: { profile?: MailProfile; hostStatus: string; hostCommand?: string; syncMessage: string; onSync: () => void }) {
  if (!profile) return <p className="muted">Select or create a mail profile.</p>;
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
        <span>Host Availability</span>
        <strong className={hostStatus === "running" ? "good-text" : "warn-text"}>{profile.source_type === "outlook_com" ? hostStatusLabel(hostStatus) : "docker worker"}</strong>
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
      {syncMessage && <div className="sync-message">{syncMessage}</div>}
      <button className="sync-selected" onClick={onSync}>
        <RefreshCcw size={17} />
        Sync selected profile
      </button>
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
