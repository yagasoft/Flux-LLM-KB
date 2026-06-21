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
    restart_required: true
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
  oauth: {
    profiles: [
      { profile_name: "gmail-capture", status: "blocked_auth_required", has_refresh_token: false }
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
  }
];

describe("Flux dashboard", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/dashboard/health") return json(health);
      if (url === "/api/dashboard/crawl") return json(crawl);
      if (url === "/api/dashboard/jobs") return json({ jobs: [{ id: "job-1", job_type: "corpus_extract_pdf", status: "pending" }] });
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
      if (url === "/api/mail/sync") return json({ profiles: [{ profile: "gmail-capture", status: "completed", exported: 0 }], count: 1 });
      if (url === "/api/mail/oauth/gmail/start") return json({ status: "pending_user_auth", auth_url: "https://accounts.google.com/o/oauth2/v2/auth?state=test" });
      if (url === "/api/search") return json([{ kind: "corpus_chunk", title: "Dashboard Operations", excerpt: "dashboard search result", score: 0.91 }]);
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
      if (url === "/api/crawl/sync") return json({ root_name: JSON.parse(String(init?.body)).root_name ?? null, dry_run: JSON.parse(String(init?.body)).dry_run });
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

    await user.click(screen.getByRole("button", { name: "View error ffprobe command not found" }));
    expect(screen.getByRole("dialog", { name: "Error detail" })).toHaveTextContent("ffprobe command not found");
  });
});

function json(payload: unknown): Response {
  return {
    ok: true,
    json: async () => payload
  } as Response;
}
