import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test, vi } from "vitest";
import App from "./App";
import { crawl, dashboardTestState as state, deferredResponse, errorJson, health, json, mail, outlook, setupDashboardTest } from "./test/appHarness";

describe("Flux dashboard", () => {
  setupDashboardTest();

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
    expect(state.retentionPolicyUpdatePayload).toEqual({
      half_life_days: 90,
      min_confidence: 0.35,
      action: "deprioritize",
      reason: "live review"
    });
  });

  test("review tab shows governance automation digest guardrails and recovery actions", async () => {
    const user = userEvent.setup();
    vi.spyOn(window, "prompt").mockReturnValue("Reviewed governance evidence");
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(await screen.findByRole("heading", { name: "Governance Automation" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Governance Digest" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Guardrails" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Recovery" })).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Governance actions" });
    expect(within(table).getByText("Stale Tag")).toBeInTheDocument();
    expect(within(table).getByText("protected memory")).toBeInTheDocument();
    expect(screen.getByText("Inspect Blocked Governance")).toBeInTheDocument();
    expect(screen.getAllByText("claim:claim-old").length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: "Run shadow" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/run",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ mode: "shadow", limit: 25 })
        })
      );
    });
    expect(state.governanceRunPayload).toEqual({ mode: "shadow", limit: 25 });

    await user.click(within(table).getByRole("button", { name: "Apply governance action gov-action-1" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/actions/gov-action-1/apply",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ rationale: "Reviewed governance evidence", confirm: true })
        })
      );
    });
    expect(state.governanceApplyPayload).toEqual({ rationale: "Reviewed governance evidence", confirm: true });

    await user.click(screen.getByRole("button", { name: "Recover governance action gov-applied-1" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/governance/actions/gov-applied-1/recover",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ rationale: "Reviewed governance evidence", confirm: true })
        })
      );
    });
    expect(state.governanceRecoverPayload).toEqual({ rationale: "Reviewed governance evidence", confirm: true });
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
    expect(state.captureReviewDecisionPayload).toEqual({ decision: "approve", rationale: "Verified source summary" });
    expect(await screen.findByText("No pending capture review jobs.")).toBeInTheDocument();
    expect(await screen.findByText("capture.review_approved")).toBeInTheDocument();
    expect(screen.getByText("Verified source summary")).toBeInTheDocument();
  });

  test("capture review status filters and ingests approved jobs", async () => {
    const user = userEvent.setup();
    state.captureReviewPayload = {
      jobs: [
        {
          id: "job-review",
          job_type: "codex_backfill",
          status: "approved",
          payload: { status: "approved", path: "session.json", ingestion: { status: "approved" } },
          updated_at: "2026-06-23T10:05:00+00:00"
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Review" }));

    await user.selectOptions(await screen.findByLabelText("Capture status"), "approved");
    await waitFor(() => {
      expect(state.captureReviewRequestUrl).toContain("status=approved");
    });

    await user.click(screen.getByRole("button", { name: "Ingest approved" }));

    await waitFor(() => {
      expect(state.captureReviewIngestPayload).toEqual({ limit: 25, dry_run: false });
    });
    expect(await screen.findByText("capture.ingested")).toBeInTheDocument();
    expect(screen.getByText("session.json")).toBeInTheDocument();
    expect(screen.getByText("episode-1")).toBeInTheDocument();
  });

  test("job queue renders readable rows and expandable details instead of primary raw JSON", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Background jobs" });
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

  test("job queue filters and pages corpus history through the jobs API", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 75,
      limit: 50,
      offset: 0,
      has_next: true,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"],
        sources: ["capture_jobs", "mail_sync_runs"]
      }
    };
    const updatedFromIso = new Date("2026-06-25T00:00").toISOString();
    const updatedToIso = new Date("2026-06-26T23:59").toISOString();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await screen.findByRole("table", { name: "Background jobs" });

    await user.click(screen.getByRole("button", { name: "Job status filter" }));
    const statusOptions = screen.getByRole("group", { name: "Job status options" });
    await user.click(within(statusOptions).getByRole("checkbox", { name: "Failed" }));
    await user.click(within(statusOptions).getByRole("checkbox", { name: "Retrying Locked" }));
    await user.click(screen.getByRole("button", { name: "Job root filter" }));
    const rootOptions = screen.getByRole("group", { name: "Job root options" });
    await user.click(within(rootOptions).getByRole("checkbox", { name: "docs" }));
    await user.click(within(rootOptions).getByRole("checkbox", { name: "mail" }));
    await user.click(screen.getByRole("button", { name: "Job type filter" }));
    const typeOptions = screen.getByRole("group", { name: "Job type options" });
    await user.click(within(typeOptions).getByRole("checkbox", { name: "Extract PDF" }));
    await user.click(within(typeOptions).getByRole("checkbox", { name: "Sync Root" }));
    await user.click(screen.getByRole("button", { name: "Job source filter" }));
    const sourceOptions = screen.getByRole("group", { name: "Job source options" });
    await user.click(within(sourceOptions).getByRole("checkbox", { name: "Capture jobs" }));
    await user.click(within(sourceOptions).getByRole("checkbox", { name: "Mail sync runs" }));
    await user.type(screen.getByLabelText("Updated from filter"), "2026-06-25T00:00");
    await user.type(screen.getByLabelText("Updated to filter"), "2026-06-26T23:59");
    await user.click(screen.getByRole("button", { name: "Apply job filters" }));

    await waitFor(() => {
      const queryUrl = state.jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.getAll("job_source")).toEqual(["capture_jobs", "mail_sync_runs"]);
      expect(params.get("updated_from")).toBe(updatedFromIso);
      expect(params.get("updated_to")).toBe(updatedToIso);
      expect(params.get("limit")).toBe("50");
      expect(params.get("offset")).toBe("0");
    });

    await user.click(screen.getByRole("button", { name: "Sort jobs by Status" }));

    await waitFor(() => {
      const latestUrl = state.jobsRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("sort_by")).toBe("status");
      expect(params.get("sort_dir")).toBe("asc");
      expect(params.get("offset")).toBe("0");
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
    });
    await waitFor(() => {
      const savedState = JSON.parse(localStorage.getItem("flux-dashboard-state") ?? "{}") as { jobSort?: Record<string, unknown> };
      expect(savedState.jobSort).toEqual({ sort_by: "status", sort_dir: "asc" });
    });
    await user.click(screen.getByRole("button", { name: "Sort jobs by Status" }));
    await waitFor(() => {
      const latestUrl = state.jobsRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("sort_by")).toBe("status");
      expect(params.get("sort_dir")).toBe("desc");
    });

    await waitFor(() => {
      const savedState = JSON.parse(localStorage.getItem("flux-dashboard-state") ?? "{}") as { jobFilters?: Record<string, unknown> };
      expect(savedState.jobFilters).toMatchObject({
        status: ["failed", "retrying_locked"],
        root_name: ["docs", "mail"],
        job_type: ["corpus_extract_pdf", "corpus_sync_root"],
        job_source: ["capture_jobs", "mail_sync_runs"],
        updated_from: "2026-06-25T00:00",
        updated_to: "2026-06-26T23:59"
      });
    });

    const pager = screen.getByLabelText("Job history paging");
    expect(within(pager).getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent)).toEqual([
      "Previous jobs page",
      "Current jobs page 1",
      "Go to jobs page 2",
      "Next jobs page"
    ]);
    const currentPage = within(pager).getByRole("button", { name: "Current jobs page 1" });
    expect(currentPage).toHaveAttribute("aria-current", "page");
    const requestCountBeforeCurrentClick = state.jobsRequestUrls.length;
    await user.click(currentPage);
    expect(state.jobsRequestUrls).toHaveLength(requestCountBeforeCurrentClick);

    await user.click(within(pager).getByRole("button", { name: "Go to jobs page 2" }));

    await waitFor(() => {
      const latestUrl = state.jobsRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("offset")).toBe("50");
      expect(params.get("limit")).toBe("50");
      expect(params.get("sort_by")).toBe("status");
      expect(params.get("sort_dir")).toBe("desc");
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.getAll("job_source")).toEqual(["capture_jobs", "mail_sync_runs"]);
    });

    await user.click(screen.getByRole("button", { name: "Next jobs page" }));

    await waitFor(() => {
      const latestUrl = state.jobsRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("offset")).toBe("50");
      expect(params.get("limit")).toBe("50");
      expect(params.get("sort_by")).toBe("status");
      expect(params.get("sort_dir")).toBe("desc");
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.getAll("job_source")).toEqual(["capture_jobs", "mail_sync_runs"]);
    });
  });

  test("job filter menus close on outside click and Escape", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [],
      count: 0,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await user.click(screen.getByRole("button", { name: "Job status filter" }));
    expect(screen.getByRole("group", { name: "Job status options" })).toBeInTheDocument();

    await user.click(screen.getByRole("heading", { name: "Operations" }));
    expect(screen.queryByRole("group", { name: "Job status options" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Job root filter" }));
    expect(screen.getByRole("group", { name: "Job root options" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("group", { name: "Job root options" })).not.toBeInTheDocument();
  });

  test("job queue opens corpus job target files and containing folders without success toasts", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const openFile = await screen.findByRole("button", { name: "Open job target file docs/failed.pdf" });
    const openFolder = screen.getByRole("button", { name: "Open containing folder for job target docs/failed.pdf" });
    expect(openFile).toHaveAttribute("title", "Open file");
    expect(openFolder).toHaveAttribute("title", "Open containing folder");

    await user.click(openFile);
    await waitFor(() => {
      expect(state.corpusJobFileActionRequests).toEqual([
        { url: "/api/dashboard/jobs/job-failed/file-actions", body: { action: "open" } }
      ]);
    });
    expect(screen.queryByText("Open request opened.")).not.toBeInTheDocument();

    await user.click(openFolder);

    await waitFor(() => {
      expect(state.corpusJobFileActionRequests).toEqual([
        { url: "/api/dashboard/jobs/job-failed/file-actions", body: { action: "open" } },
        { url: "/api/dashboard/jobs/job-failed/file-actions", body: { action: "reveal" } }
      ]);
    });
    expect(screen.queryByText("Open containing folder request opened.")).not.toBeInTheDocument();
  });

  test("job queue shows rejected file action details as a warning toast", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf"]
      }
    };
    state.corpusJobFileActionPayload = { state: "not_allowed", message: "reveal failed", reason: "host_action_failed" };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const openFolder = await screen.findByRole("button", { name: "Open containing folder for job target docs/failed.pdf" });
    await user.click(openFolder);

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveClass("warning");
    expect(alert).toHaveTextContent("Open containing folder request was rejected: reveal failed.");
  });

  test("job queue restores persisted history filters on load", async () => {
    localStorage.setItem("flux-dashboard-state", JSON.stringify({
      activeTab: "jobs",
      jobFilters: {
        status: ["failed", "retrying_locked"],
        root_name: ["docs", "mail"],
        job_type: ["corpus_extract_pdf", "corpus_sync_root"],
        updated_from: "2026-06-25T00:00",
        updated_to: "2026-06-26T23:59"
      },
      jobSort: { sort_by: "target", sort_dir: "asc" }
    }));
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };
    const updatedFromIso = new Date("2026-06-25T00:00").toISOString();
    const updatedToIso = new Date("2026-06-26T23:59").toISOString();

    render(<App />);

    await screen.findByRole("table", { name: "Background jobs" });
    expect(screen.getByRole("button", { name: "Job status filter" })).toHaveTextContent("2 statuses");
    expect(screen.getByRole("button", { name: "Job root filter" })).toHaveTextContent("2 roots");
    expect(screen.getByRole("button", { name: "Job type filter" })).toHaveTextContent("2 types");
    expect(screen.getByLabelText("Updated from filter")).toHaveValue("2026-06-25T00:00");
    expect(screen.getByLabelText("Updated to filter")).toHaveValue("2026-06-26T23:59");
    await waitFor(() => {
      const queryUrl = state.jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed", "retrying_locked"]);
      expect(params.getAll("root_name")).toEqual(["docs", "mail"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf", "corpus_sync_root"]);
      expect(params.get("updated_from")).toBe(updatedFromIso);
      expect(params.get("updated_to")).toBe(updatedToIso);
      expect(params.get("sort_by")).toBe("target");
      expect(params.get("sort_dir")).toBe("asc");
      expect(params.get("limit")).toBe("50");
      expect(params.get("offset")).toBe("0");
    });
  });

  test("job queue restores legacy scalar history filters as single selections", async () => {
    localStorage.setItem("flux-dashboard-state", JSON.stringify({
      activeTab: "jobs",
      jobFilters: {
        status: "failed",
        root_name: "docs",
        job_type: "corpus_extract_pdf"
      }
    }));
    state.jobsPayload = {
      jobs: [],
      count: 0,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "retrying_locked"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };

    render(<App />);

    await screen.findByRole("button", { name: "Job status filter" });
    expect(screen.getByRole("button", { name: "Job status filter" })).toHaveTextContent("Failed");
    expect(screen.getByRole("button", { name: "Job root filter" })).toHaveTextContent("docs");
    expect(screen.getByRole("button", { name: "Job type filter" })).toHaveTextContent("Extract PDF");
    await waitFor(() => {
      const queryUrl = state.jobsRequestUrls.find((url) => url.startsWith("/api/dashboard/jobs?"));
      expect(queryUrl).toBeTruthy();
      const params = new URLSearchParams(queryUrl?.split("?")[1]);
      expect(params.getAll("status")).toEqual(["failed"]);
      expect(params.getAll("root_name")).toEqual(["docs"]);
      expect(params.getAll("job_type")).toEqual(["corpus_extract_pdf"]);
    });
  });

  test("job queue exposes force retry only for eligible corpus jobs", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/failed.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        },
        {
          id: "job-cancelled",
          job_type: "corpus_sync_root",
          status: "cancelled_operator",
          payload: { root_name: "mail", profile_name: "outlook-catchup" },
          attempts: 1,
          updated_at: "2026-06-26T09:20:00+00:00"
        },
        {
          id: "job-completed",
          job_type: "corpus_extract_pdf",
          status: "completed",
          payload: { root_name: "docs", path: "docs/done.pdf" },
          attempts: 1,
          updated_at: "2026-06-26T09:10:00+00:00"
        },
        {
          id: "job-obsolete",
          job_type: "corpus_extract_pdf",
          status: "obsolete",
          payload: { root_name: "docs", path: "docs/obsolete.pdf" },
          attempts: 1,
          updated_at: "2026-06-26T09:05:00+00:00",
          delete_requested_at: "2026-07-01T09:00:00+00:00",
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        },
        {
          id: "job-marked-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/marked.pdf" },
          attempts: 2,
          updated_at: "2026-06-26T09:00:00+00:00",
          delete_requested_at: "2026-07-01T09:01:00+00:00",
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        }
      ],
      count: 5,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "cancelled_operator", "completed", "obsolete"],
        roots: ["docs", "mail"],
        job_types: ["corpus_extract_pdf", "corpus_sync_root"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByRole("button", { name: "Force retry corpus job job-failed" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Force retry corpus job job-cancelled" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job job-completed" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job job-obsolete" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job job-marked-failed" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Force retry corpus job job-cancelled" }));

    await waitFor(() => {
      expect(state.corpusRetryRequests).toContain("/api/dashboard/jobs/job-cancelled/retry");
    });
    expect(await screen.findByText("Corpus job queued for retry.")).toBeInTheDocument();
  });

  test("job queue marks terminal corpus jobs for delayed deletion", async () => {
    const user = userEvent.setup();
    const deleteRequestedAt = "2026-07-01T09:00:00+00:00";
    const formattedDeleteRequestedAt = new Date(deleteRequestedAt).toLocaleString([], { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
    state.jobsPayload = {
      jobs: [
        {
          id: "job-failed",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "docs/open.pdf" },
          attempts: 3,
          last_error: "extract failed",
          updated_at: "2026-06-26T09:30:00+00:00"
        },
        {
          id: "job-marked",
          job_type: "corpus_extract_pdf",
          status: "obsolete",
          payload: { root_name: "docs", path: "docs/missing.pdf" },
          attempts: 2,
          last_error: "missing dependency",
          updated_at: "2026-06-26T09:20:00+00:00",
          delete_requested_at: deleteRequestedAt,
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup"
        },
        {
          id: "job-policy",
          job_type: "corpus_extract_code",
          status: "blocked_by_policy",
          payload: { root_name: "docs", path: "src/large.py" },
          attempts: 1,
          last_error: "text file exceeds inline extraction limit",
          updated_at: "2026-06-26T09:15:00+00:00"
        },
        {
          id: "job-invalid",
          job_type: "corpus_extract_document",
          status: "blocked_invalid_source",
          payload: { root_name: "docs", path: "docs/broken.docx" },
          attempts: 1,
          last_error: "Package not found",
          updated_at: "2026-06-26T09:12:00+00:00"
        },
        {
          id: "job-running",
          job_type: "corpus_extract_pdf",
          status: "running",
          payload: { root_name: "docs", path: "docs/running.pdf" },
          attempts: 1,
          updated_at: "2026-06-26T09:10:00+00:00"
        }
      ],
      count: 5,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["failed", "obsolete", "blocked_by_policy", "blocked_invalid_source", "running"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf", "corpus_extract_code", "corpus_extract_document"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByRole("button", { name: "Mark corpus job job-failed for deletion" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mark corpus job job-policy for deletion" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mark corpus job job-invalid for deletion" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mark corpus job job-marked for deletion" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mark corpus job job-running for deletion" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job job-marked" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Restore deletion mark for corpus job job-marked" })).toBeInTheDocument();
    expect(screen.getByText("Blocked by policy")).toBeInTheDocument();
    expect(screen.getByText("Invalid source")).toBeInTheDocument();
    expect(screen.getAllByText("Obsolete").length).toBeGreaterThanOrEqual(1);

    await user.click(screen.getByRole("button", { name: "Mark corpus job job-failed for deletion" }));

    await waitFor(() => {
      expect(state.corpusDeleteRequests).toContain("/api/dashboard/jobs/job-failed/delete-request");
    });
    expect(await screen.findByText("Corpus job marked obsolete for deletion.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Restore deletion mark for corpus job job-marked" }));

    await waitFor(() => {
      expect(state.corpusRestoreRequests).toContain("/api/dashboard/jobs/job-marked/delete-request");
    });
    expect(await screen.findByText("Corpus job deletion mark restored.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-marked" }));

    expect(screen.getByText("Delete requested")).toBeInTheDocument();
    expect(screen.getByText(formattedDeleteRequestedAt)).toBeInTheDocument();
    expect(screen.getByText("Delete requested by")).toBeInTheDocument();
    expect(screen.getByText("dashboard")).toBeInTheDocument();
    expect(screen.getByText("Delete reason")).toBeInTheDocument();
    expect(screen.getByText("operator_cleanup")).toBeInTheDocument();
  });

  test("job queue explains maintenance-obsolete jobs without offering restore", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-maintenance",
          job_type: "corpus_extract_pdf_ocr_pages",
          status: "obsolete",
          payload: { root_name: "docs", path: "docs/scanned.pdf" },
          attempts: 3,
          last_error: "Invalid OCR version",
          updated_at: "2026-07-03T13:11:08+00:00",
          telemetry: {
            stage: "queued",
            result_status: "obsolete",
            obsolete_previous_status: "failed",
            obsolete_reason: "maintenance_reprocess_derived_state"
          }
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["obsolete"],
        roots: ["docs"],
        job_types: ["corpus_extract_pdf_ocr_pages"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect((await screen.findAllByText("Maintenance obsolete")).length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByRole("button", { name: "Restore deletion mark for corpus job job-maintenance" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-maintenance" }));

    expect(screen.getByText("Previous status")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("Obsolete reason")).toBeInTheDocument();
    expect(screen.getByText("Maintenance reprocess derived state")).toBeInTheDocument();
  });

  test("job queue distinguishes completed metadata-only corpus jobs", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-metadata",
          job_type: "corpus_extract_video",
          status: "completed",
          payload: { root_name: "docs", path: "meetings/long.mp4" },
          attempts: 1,
          updated_at: "2026-07-01T10:04:00+00:00",
          telemetry: {
            stage: "queued",
            result_status: "metadata_only",
            asr_duration_seconds: 5856,
            asr_segments: 0
          }
        }
      ],
      count: 1,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["completed"],
        roots: ["docs"],
        job_types: ["corpus_extract_video"]
      }
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByText("Completed Metadata Only")).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Background jobs" });
    expect(within(table).getByText("Metadata Only")).toBeInTheDocument();
    expect(within(table).queryByText("Queued")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-metadata" }));

    expect(screen.getByText("Result")).toBeInTheDocument();
    expect(screen.getAllByText("Metadata Only").length).toBeGreaterThanOrEqual(2);
  });

  test("job queue shows Outlook COM requests and cancel feedback", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "outlook_sync_requests:req-pending",
          source_id: "req-pending",
          job_source: "outlook_sync_requests",
          job_type: "outlook_sync_request",
          status: "pending",
          target: "outlook-catchup",
          root_name: "Outlook COM",
          attempts: 0,
          details: { profile_name: "outlook-catchup", requested_by: "dashboard" },
          created_at: "2026-06-27T16:29:01+00:00",
          updated_at: "2026-06-27T16:29:01+00:00"
        },
        {
          id: "outlook_sync_requests:req-claimed",
          source_id: "req-claimed",
          job_source: "outlook_sync_requests",
          job_type: "outlook_sync_request",
          status: "claimed",
          target: "outlook-catchup",
          root_name: "Outlook COM",
          attempts: 0,
          details: { profile_name: "outlook-catchup", requested_by: "dashboard", claimed_by: "host-1" },
          created_at: "2026-06-27T16:27:46+00:00",
          updated_at: "2026-06-27T16:28:00+00:00"
        }
      ],
      count: 2,
      limit: 50,
      offset: 0,
      has_next: false
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Background jobs" });
    expect(within(table).getAllByText("Outlook Sync Request")).toHaveLength(2);
    expect(within(table).getAllByText("outlook-catchup")).toHaveLength(2);
    expect(within(table).getByText("Pending")).toBeInTheDocument();
    expect(within(table).getByText("Claimed")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel Outlook request req-pending" }));
    await waitFor(() => {
      expect(state.outlookCancelRequests).toContain("/api/outlook-host/requests/req-pending/cancel");
    });
    expect(await screen.findByText("Outlook sync request cancelled.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel Outlook request req-claimed" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("cannot be cancelled mid-execution");
  });

  test("job queue shows non-corpus background work without corpus actions or console fetches", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "mail_sync_runs:mail-1",
          source_id: "mail-1",
          job_source: "mail_sync_runs",
          job_type: "mail_sync",
          status: "running",
          target: "gmail-capture",
          root_name: "mail",
          attempts: 2,
          progress: "3 exported / 5 seen",
          details: { profile_name: "gmail-capture", trigger: "scheduler", requested_by: "event-scheduler" },
          updated_at: "2026-07-06T08:00:00+00:00"
        },
        {
          id: "capture_jobs:cap-1",
          source_id: "cap-1",
          job_source: "capture_jobs",
          job_type: "corpus_extract_pdf",
          status: "failed",
          target: "docs/failed.pdf",
          root_name: "docs",
          attempts: 1,
          last_error: "extract failed",
          details: { payload: { root_name: "docs", path: "docs/failed.pdf" } },
          updated_at: "2026-07-06T07:59:00+00:00"
        }
      ],
      count: 2,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        sources: ["mail_sync_runs", "capture_jobs"],
        statuses: ["running", "failed"],
        roots: ["mail", "docs"],
        job_types: ["mail_sync", "corpus_extract_pdf"]
      }
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Background jobs" });
    expect(within(table).getByText("Mail Sync")).toBeInTheDocument();
    expect(within(table).getByText("gmail-capture")).toBeInTheDocument();
    expect(within(table).getByText("3 exported / 5 seen")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancel corpus job mail-1" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Force retry corpus job mail-1" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Force retry corpus job cap-1" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job mail_sync_runs:mail-1" }));
    expect(await screen.findByText("Mail sync runs")).toBeInTheDocument();
    expect(screen.getByText("Source details")).toBeInTheDocument();
    expect(screen.getByText(/event-scheduler/)).toBeInTheDocument();
    expect(state.jobToolInvocationRequestUrls).toHaveLength(0);
    expect(screen.queryByText("Console output")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job capture_jobs:cap-1" }));
    await waitFor(() => {
      expect(state.jobToolInvocationRequestUrls).toContain("/api/dashboard/jobs/cap-1/tool-invocations?limit=100");
    });
  });

  test("job queue shows corpus sync progress and cancel feedback", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-sync-pending",
          job_type: "corpus_sync_root",
          status: "pending",
          payload: { root_name: "mail-outlook-mohesr", profile_name: "outlook-mohesr", reason: "outlook_spool_sync" },
          attempts: 0,
          telemetry: { stage: "queued" },
          updated_at: "2026-06-27T16:29:01+00:00"
        },
        {
          id: "job-sync-running",
          job_type: "corpus_sync_root",
          status: "running",
          payload: { root_name: "mail-outlook-mohesr", profile_name: "outlook-mohesr", reason: "outlook_spool_sync" },
          attempts: 1,
          telemetry: {
            stage: "hashing",
            stage_index: 4,
            stage_total: 6,
            paths_done: 42,
            paths_total: 3292,
            files_done: 3,
            files_total: 8,
            files_seen: 35655,
            files_changed: 35371,
            jobs_queued: 120,
            current_path: "/app/private/mail-spool/outlook-mohesr/ready/export-42",
            progress_percent: 13
          },
          updated_at: "2026-06-27T16:30:01+00:00"
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    const table = await screen.findByRole("table", { name: "Background jobs" });
    expect(within(table).getByText("Progress")).toBeInTheDocument();
    expect(within(table).getAllByText("Sync Root")).toHaveLength(2);
    expect(within(table).getAllByText("outlook-mohesr")).toHaveLength(2);
    expect(within(table).getAllByText("mail-outlook-mohesr")).toHaveLength(2);
    expect(within(table).getByText("Paths 42/3292, stage 4/6 hashing, files 3/8")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show details for job job-sync-running" }));
    expect(screen.getByText("Stage")).toBeInTheDocument();
    expect(screen.getAllByText("Hashing").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Progress").length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText("Paths 42/3292, stage 4/6 hashing, files 3/8").length).toBeGreaterThan(0);
    expect(screen.getByText("Current path")).toBeInTheDocument();
    expect(screen.getByText("/app/private/mail-spool/outlook-mohesr/ready/export-42")).toBeInTheDocument();
    expect(screen.getByText("Files seen")).toBeInTheDocument();
    expect(screen.getByText("35655")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel corpus job job-sync-pending" }));
    await waitFor(() => {
      expect(state.corpusCancelRequests).toContain("/api/dashboard/jobs/job-sync-pending/cancel");
    });
    expect(await screen.findByText("Corpus job cancelled.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel corpus job job-sync-running" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("cannot be cancelled mid-execution");
  });

  test("corpus job action buttons show pending feedback while backend requests run", async () => {
    const cancelDeferred = deferredResponse();
    const retryDeferred = deferredResponse();
    const markDeferred = deferredResponse();
    const restoreDeferred = deferredResponse();
    state.pendingFetchResponses["/api/dashboard/jobs/job-pending/cancel"] = cancelDeferred;
    state.pendingFetchResponses["/api/dashboard/jobs/job-retry/retry"] = retryDeferred;
    state.pendingFetchResponses["/api/dashboard/jobs/job-mark/delete-request"] = markDeferred;
    state.pendingFetchResponses["/api/dashboard/jobs/job-restore/delete-request"] = restoreDeferred;
    state.jobsPayload = {
      jobs: [
        {
          id: "job-pending",
          job_type: "corpus_sync_root",
          status: "pending",
          payload: { root_name: "docs", path: "Root sync" },
          attempts: 0,
          updated_at: "2026-06-27T16:29:01+00:00"
        },
        {
          id: "job-retry",
          job_type: "corpus_extract_pdf",
          status: "failed",
          payload: { root_name: "docs", path: "failed.pdf" },
          attempts: 1,
          updated_at: "2026-06-27T16:30:01+00:00"
        },
        {
          id: "job-mark",
          job_type: "corpus_extract_document",
          status: "blocked_missing_dependency",
          payload: { root_name: "docs", path: "blocked.docx" },
          attempts: 1,
          updated_at: "2026-06-27T16:31:01+00:00"
        },
        {
          id: "job-restore",
          job_type: "corpus_extract_code",
          status: "obsolete",
          delete_requested_at: "2026-07-01T09:00:00+00:00",
          delete_requested_by: "dashboard",
          delete_reason: "operator_cleanup",
          payload: { root_name: "docs", path: "obsolete.py" },
          attempts: 1,
          updated_at: "2026-06-27T16:32:01+00:00"
        }
      ],
      count: 4,
      limit: 50,
      offset: 0,
      has_next: false,
      filter_options: {
        statuses: ["pending", "failed", "blocked_missing_dependency", "obsolete"],
        roots: ["docs"],
        job_types: ["corpus_sync_root", "corpus_extract_pdf", "corpus_extract_document", "corpus_extract_code"]
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await screen.findByRole("table", { name: "Background jobs" });

    const cancelButton = screen.getByRole("button", { name: "Cancel corpus job job-pending" });
    const retryButton = screen.getByRole("button", { name: "Force retry corpus job job-retry" });
    const markButton = screen.getByRole("button", { name: "Mark corpus job job-mark for deletion" });
    const restoreButton = screen.getByRole("button", { name: "Restore deletion mark for corpus job job-restore" });
    await user.click(cancelButton);
    await user.click(retryButton);
    await user.click(markButton);
    await user.click(restoreButton);

    expect(cancelButton).toBeDisabled();
    expect(cancelButton).toHaveTextContent("Cancelling...");
    expect(retryButton).toBeDisabled();
    expect(retryButton).toHaveTextContent("Retrying...");
    expect(markButton).toBeDisabled();
    expect(markButton).toHaveTextContent("Marking...");
    expect(restoreButton).toBeDisabled();
    expect(restoreButton).toHaveTextContent("Restoring...");

    cancelDeferred.resolve(json({ job_id: "job-pending", status: "cancelled_operator", cancelled: true }));
    retryDeferred.resolve(json({ settings_mutated: false, action: "retry_corpus_job", result: { job_id: "job-retry", status: "pending" } }));
    markDeferred.resolve(json({ job_id: "job-mark", status: "obsolete", delete_requested: true }));
    restoreDeferred.resolve(json({ job_id: "job-restore", status: "blocked_missing_dependency", delete_requested: false }));
    await waitFor(() => {
      expect(state.corpusCancelRequests).toContain("/api/dashboard/jobs/job-pending/cancel");
      expect(state.corpusRetryRequests).toContain("/api/dashboard/jobs/job-retry/retry");
      expect(state.corpusDeleteRequests).toContain("/api/dashboard/jobs/job-mark/delete-request");
      expect(state.corpusRestoreRequests).toContain("/api/dashboard/jobs/job-restore/delete-request");
    });
  });

  test("expanded job details show live console output refreshed by polling", async () => {
    const user = userEvent.setup();
    state.jobsPayload = {
      jobs: [
        {
          id: "job-video",
          job_type: "corpus_extract_video",
          status: "running",
          payload: { root_name: "media", path: "clips/demo.mp4" },
          attempts: 1,
          telemetry: { stage: "extracting" },
          updated_at: "2026-06-27T16:31:01+00:00"
        }
      ]
    };
    state.jobToolInvocationPayload = {
      job_id: "job-video",
      invocations: [
        {
          id: "inv-1",
          job_id: "job-video",
          command: ["python", "-m", "demo_tool"],
          cwd: "E:/LLM KB",
          status: "running",
          return_code: null,
          stdout: "first line\n",
          stderr: "warning line\n",
          started_at: "2026-06-27T16:31:02+00:00",
          completed_at: null,
          duration_ms: null
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));
    await user.click(await screen.findByRole("button", { name: "Show details for job job-video" }));

    expect(await screen.findByText("Console output")).toBeInTheDocument();
    expect(screen.getByText("python -m demo_tool")).toBeInTheDocument();
    expect(screen.getAllByText("Running").length).toBeGreaterThan(0);
    expect(screen.getByText("first line")).toBeInTheDocument();
    expect(screen.getByText("warning line")).toBeInTheDocument();
    expect(state.jobToolInvocationRequestUrls).toContain("/api/dashboard/jobs/job-video/tool-invocations?limit=100");

    state.jobToolInvocationPayload = {
      job_id: "job-video",
      invocations: [
        {
          id: "inv-1",
          job_id: "job-video",
          command: ["python", "-m", "demo_tool"],
          cwd: "E:/LLM KB",
          status: "running",
          return_code: null,
          stdout: "first line\nsecond line\n",
          stderr: "warning line\n",
          started_at: "2026-06-27T16:31:02+00:00",
          completed_at: null,
          duration_ms: null
        }
      ]
    };

    await waitFor(() => {
      expect(state.jobToolInvocationRequestUrls.filter((url) => url.includes("job-video/tool-invocations")).length).toBeGreaterThanOrEqual(2);
      expect(screen.getByText(/second line/)).toBeInTheDocument();
    }, { timeout: 2500 });
  });

  test("job queue keeps the readable empty state when no jobs are queued", async () => {
    state.jobsPayload = { jobs: [] };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Jobs" }));

    expect(await screen.findByText("No background jobs found.")).toBeInTheDocument();
    expect(screen.getByText("No active crawl jobs. Recent model activity and GPU scheduler activity were detected. 1 running lease, 1 waiting request.")).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Background jobs" })).not.toBeInTheDocument();
  });

  test("corpus dashboard surfaces unstable and locked indexing states", async () => {
    const root = (crawl.root_summaries[0]);
    state.crawlPayload = {
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
});
