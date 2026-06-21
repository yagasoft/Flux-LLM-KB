import { render, screen, waitFor } from "@testing-library/react";
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
  recent_errors: ["ffprobe command not found"]
};

const mail = {
  enabled_profiles: 2,
  exported_messages: 10,
  errored_messages: 1,
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
  }
];

describe("Flux dashboard", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/dashboard/health") return json(health);
      if (url === "/api/dashboard/crawl") return json({ roots: [], status: { active_watch_roots: 1, disabled_watch_roots: 2 } });
      if (url === "/api/dashboard/jobs") return json({ jobs: [{ id: "job-1", job_type: "corpus_extract_pdf", status: "pending" }] });
      if (url === "/api/dashboard/retrieval-stats") return json({ retrieval: health.retrieval, duplicate_assets: 0 });
      if (url === "/api/mail/status") return json(mail);
      if (url === "/api/outlook-host/status") return json(outlook);
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
      if (url.endsWith("/enable") || url.endsWith("/disable")) return json({ status: "updated" });
      return json({});
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  test("renders the operations console without primary raw JSON panels", async () => {
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByText("PostgreSQL")).toBeInTheDocument();
    expect(screen.getByText("Outlook COM host")).toBeInTheDocument();
    expect(screen.getByRole("table", { name: "Mail profiles" })).toBeInTheDocument();
    expect(screen.getByText("outlook-catchup")).toBeInTheDocument();
    expect(screen.getByText("host_offline")).toBeInTheDocument();
    expect(screen.queryByText(/"database"/)).not.toBeInTheDocument();
  });

  test("manual Outlook sync creates a host request", async () => {
    const user = userEvent.setup();
    render(<App />);

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

  test("add profile opens a real form and persists an IMAP profile", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
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
