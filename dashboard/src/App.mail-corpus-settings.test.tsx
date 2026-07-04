import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test, vi } from "vitest";
import App from "./App";
import { crawl, dashboardTestState as state, deferredResponse, errorJson, health, json, mail, outlook, setupDashboardTest } from "./test/appHarness";

describe("Flux dashboard", () => {
  setupDashboardTest();

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
    state.healthPayload = {
      ...health,
      recent_errors: [corpusError],
      status: { ...health.status, recent_errors: [corpusError] }
    };
    state.mailPayload = { ...(mail as Record<string, unknown>), errored_messages: 0 };
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
      expect(state.postProcessDryRunPayload).toEqual({ limit: 5 });
    });
    expect(await screen.findByText("Post-process dry-run planned 1 action.")).toBeInTheDocument();
  });

  test("manual IMAP sync surfaces the created run state", async () => {
    state.mailSyncPayload = {
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
    state.mailSyncPayload = {
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
    state.searchPayload = [
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

  test("mail profile row more menu deletes profile after confirmation", async () => {
    Object.defineProperty(window, "innerHeight", { configurable: true, value: 760 });
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 1900 });
    const deleteDeferred = deferredResponse();
    state.pendingFetchResponses["/api/mail/profiles/gmail-capture"] = deleteDeferred;
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    const table = screen.getByRole("table", { name: "Mail profiles" });
    const moreButton = screen.getByRole("button", { name: "More gmail-capture" });
    vi.spyOn(moreButton, "getBoundingClientRect").mockReturnValue({
      x: 1840,
      y: 720,
      top: 720,
      bottom: 748,
      left: 1840,
      right: 1868,
      width: 28,
      height: 28,
      toJSON: () => ({})
    } as DOMRect);
    await user.click(moreButton);

    const menu = screen.getByRole("menu", { name: "gmail-capture profile actions" });
    expect(table).not.toContainElement(menu);
    expect(menu).toHaveStyle({ position: "fixed" });
    expect(Number.parseFloat(menu.style.top)).toBeLessThan(720);
    expect(Number.parseFloat(menu.style.left)).toBeLessThanOrEqual(1712);
    expect(within(menu).getByRole("menuitem", { name: "View details" })).toBeInTheDocument();
    await user.click(within(menu).getByRole("menuitem", { name: "Delete profile" }));

    const dialog = screen.getByRole("dialog", { name: "Delete mail profile" });
    expect(dialog).toHaveTextContent("mail-gmail-capture");
    expect(dialog).toHaveTextContent("private spool");
    await user.click(within(dialog).getByRole("button", { name: "Delete profile and private spool" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith("/api/mail/profiles/gmail-capture", expect.objectContaining({ method: "DELETE" }));
    });
    expect(screen.queryByRole("dialog", { name: "Delete mail profile" })).not.toBeInTheDocument();
    const pendingRow = within(table).getByText("gmail-capture").closest("tr");
    expect(pendingRow).toHaveAttribute("aria-busy", "true");
    expect(pendingRow).toHaveTextContent("Deleting...");
    state.mailPayload = {
      ...(state.mailPayload as typeof mail),
      profiles: (state.mailPayload as typeof mail).profiles.filter((profile: { name: string }) => profile.name !== "gmail-capture")
    };
    deleteDeferred.resolve(json(state.mailProfileDeleteResponse));
    expect(await screen.findByText(/Mail profile gmail-capture deleted/)).toBeInTheDocument();
    await waitFor(() => {
      expect(within(table).queryByText("gmail-capture")).not.toBeInTheDocument();
    });
  });

  test("failed mail profile delete clears busy state and keeps the row", async () => {
    const deleteDeferred = deferredResponse();
    state.pendingFetchResponses["/api/mail/profiles/gmail-capture"] = deleteDeferred;
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    const table = screen.getByRole("table", { name: "Mail profiles" });
    await user.click(screen.getByRole("button", { name: "More gmail-capture" }));
    await user.click(within(screen.getByRole("menu", { name: "gmail-capture profile actions" })).getByRole("menuitem", { name: "Delete profile" }));
    await user.click(within(screen.getByRole("dialog", { name: "Delete mail profile" })).getByRole("button", { name: "Delete profile and private spool" }));

    const pendingRow = within(table).getByText("gmail-capture").closest("tr");
    expect(pendingRow).toHaveAttribute("aria-busy", "true");
    expect(pendingRow).toHaveTextContent("Deleting...");

    deleteDeferred.resolve(errorJson({ error: { message: "delete failed" } }, 500, "Server Error"));

    expect(await screen.findByRole("alert")).toHaveTextContent(/Could not delete mail profile gmail-capture: .*delete failed/);
    const row = within(table).getByText("gmail-capture").closest("tr");
    expect(row).not.toHaveAttribute("aria-busy", "true");
    expect(row).not.toHaveTextContent("Deleting...");
  });

  test("mail profile delete shows spool cleanup warning from API", async () => {
    state.mailProfileDeleteResponse = {
      profile_name: "gmail-capture",
      root_name: "mail-gmail-capture",
      deleted: true,
      profile: { deleted: true },
      corpus_root: { deleted: true },
      search_index: { deleted: 2, records_deleted: 2 },
      sidecars: { deleted: 1, missing: 0, blocked: 0, failed: 0, errors: [] },
      spool: {
        status: "blocked",
        deleted: false,
        blocked_reason: "resolved path is not under a private mail-spool profile directory"
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Mail" }));
    await user.click(screen.getByRole("button", { name: "More gmail-capture" }));
    await user.click(within(screen.getByRole("menu", { name: "gmail-capture profile actions" })).getByRole("menuitem", { name: "Delete profile" }));
    await user.click(within(screen.getByRole("dialog", { name: "Delete mail profile" })).getByRole("button", { name: "Delete profile and private spool" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Mail profile gmail-capture deleted");
    expect(alert).toHaveTextContent("Spool cleanup blocked");
    expect(alert).toHaveTextContent("private mail-spool profile directory");
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

  test("corpus root delete dims the row and keeps it until purge completes", async () => {
    const deleteDeferred = deferredResponse();
    state.pendingFetchResponses["/api/crawl/roots/docs?purge_index=true"] = deleteDeferred;
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    const table = await screen.findByRole("table", { name: "Monitored roots" });
    await user.click(screen.getByRole("button", { name: "Delete docs" }));
    expect(screen.getByRole("dialog", { name: "Delete watched path" })).toHaveTextContent("does not delete files from disk");
    await user.click(screen.getByRole("button", { name: "Delete watched path and purge index" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith("/api/crawl/roots/docs?purge_index=true", expect.objectContaining({ method: "DELETE" }));
    });
    expect(screen.queryByRole("dialog", { name: "Delete watched path" })).not.toBeInTheDocument();
    const pendingRow = within(table).getByText("docs").closest("tr");
    expect(pendingRow).toHaveAttribute("aria-busy", "true");
    expect(pendingRow).toHaveTextContent("Deleting...");

    state.crawlPayload = {
      ...(state.crawlPayload as typeof crawl),
      root_summaries: [],
      roots: []
    };
    deleteDeferred.resolve(json({ id: "docs", deleted: true, purged_index: true }));

    expect(await screen.findByText("Watched path docs deleted and index rows purged. Files on disk were not deleted.")).toBeInTheDocument();
    await waitFor(() => {
      expect(within(table).queryByText("docs")).not.toBeInTheDocument();
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

    await user.click(screen.getByRole("button", { name: "Edit retrieval.embedding_model" }));
    await user.clear(screen.getByLabelText("Setting value"));
    await user.type(screen.getByLabelText("Setting value"), "Snowflake/custom-test-model");
    await user.click(screen.getByRole("button", { name: "Save setting" }));
    expect(screen.getByRole("dialog", { name: "Confirm setting change" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Confirm and save" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/settings/retrieval.embedding_model",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({ value: "Snowflake/custom-test-model", confirmed: true, reason: "dashboard update" })
        })
      );
    });
  });
});
