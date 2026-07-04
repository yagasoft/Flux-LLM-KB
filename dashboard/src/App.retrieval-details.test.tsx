import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test, vi } from "vitest";
import App from "./App";
import { crawl, dashboardTestState as state, deferredResponse, errorJson, health, json, mail, outlook, setupDashboardTest } from "./test/appHarness";

describe("Flux dashboard", () => {
  setupDashboardTest();

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
    expect(screen.getByText("Dashboard search result with highlighted operations.")).toBeInTheDocument();
    await user.click(screen.getByText("Why this result"));
    expect(screen.getByText("Corpus Lexical, Vespa Hybrid")).toBeInTheDocument();
    expect(screen.getByText("local")).toBeInTheDocument();
    expect(screen.getByText("Lifecycle penalties")).toBeInTheDocument();
    expect(screen.getByText("state 1.000, retention 0.600")).toBeInTheDocument();
    expect(screen.getByText("Exact duplicates")).toBeInTheDocument();
    expect(screen.getByText("Same document versions")).toBeInTheDocument();
    expect(screen.getByText("1 suppressed")).toBeInTheDocument();
    expect(screen.getByText("Semantic duplicates")).toBeInTheDocument();
    expect(screen.getAllByText("2 suppressed").length).toBeGreaterThanOrEqual(2);

    await user.click(screen.getByRole("button", { name: "View error ffprobe command not found" }));
    expect(screen.getByRole("dialog", { name: "Error detail" })).toHaveTextContent("ffprobe command not found");
  });

  test("retrieval filters use explain endpoint and render exclusion trace", async () => {
    state.explainPayload = {
      query: "customer rfp",
      results: [],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filters: {
        logical_kinds: ["mail"],
        current_only: true,
        lifecycle_states: [],
        include_suppressed: true
      },
      filter_trace: {
        excluded: [
          { id: "chunk-file", title: "File result", kind: "file", reason: "logical_kind", score: 0.8 },
          { id: "episode-old", title: "Old decision", kind: "episode", reason: "current_only", score: 0.7, lifecycle_state: "retired" }
        ]
      },
      suppression: {
        exact_duplicates: [{ title: "RFP", suppressed_count: 3, reason: "exact_content_duplicate" }],
        version_families: [{ title: "Proposal", suppressed_count: 1, reason: "same_document_version_family" }],
        semantic_duplicates: [{ title: "Proposal Copy", suppressed_count: 2, reason: "semantic_near_duplicate" }]
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(screen.getByLabelText("Search focus"), "mail");
    await user.click(screen.getByLabelText("Current evidence only"));
    await user.click(screen.getByLabelText("Show suppressed diagnostics"));
    await user.type(screen.getByLabelText("Dashboard search"), "customer rfp{enter}");

    await screen.findByText("Filtered out 2 candidates");
    expect(screen.getByText("File result - logical kind")).toBeInTheDocument();
    expect(screen.getByText("Old decision - current only")).toBeInTheDocument();
    expect(screen.getByText("Suppressed evidence")).toBeInTheDocument();
    expect(screen.getByText("Exact duplicates: 3")).toBeInTheDocument();
    expect(screen.getByText("Version families: 1")).toBeInTheDocument();
    expect(screen.getByText("Semantic duplicates: 2")).toBeInTheDocument();
    expect(state.explainRequestPayload).toEqual({
      query: "customer rfp",
      limit: 8,
      filters: {
        logical_kinds: ["mail"],
        current_only: true,
        lifecycle_states: [],
        include_suppressed: true
      }
    });
  });

  test("retrieval search focus maps docs and code filters to explain", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(screen.getByLabelText("Search focus"), "docs");
    await user.type(screen.getByLabelText("Dashboard search"), "agent guidance{enter}");

    await waitFor(() => expect(state.explainRequestPayload).toEqual({
      query: "agent guidance",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["text", "document", "image"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    }));

    state.searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        file_kind: "code",
        title: "OrderService.build_invoice",
        excerpt: "def build_invoice(order_id): return order_id",
        score: 0.99,
        streams: ["code_symbol_exact"],
        snippet: { text: "def build_invoice(order_id): return order_id", matched_terms: ["build_invoice"] },
        retrieval_explanation: {
          score: 0.99,
          streams: ["code_symbol_exact"],
          raw_scores: { code_symbol_exact: 2.5 },
          scope: { label: "local" }
        }
      }
    ];
    await user.clear(screen.getByLabelText("Dashboard search"));
    await user.selectOptions(screen.getByLabelText("Search focus"), "code");
    await user.type(screen.getByLabelText("Dashboard search"), "build_invoice{enter}");

    expect(await screen.findByText("OrderService.build_invoice")).toBeInTheDocument();
    expect(screen.getByText("Why this result")).toBeInTheDocument();
    expect(state.explainRequestPayload).toEqual({
      query: "build_invoice",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["code"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    });
  });

  test("balanced code-heavy results show diagnostic and rerun docs files", async () => {
    state.explainPayload = {
      query: "closeout failed_step log_path",
      results: [
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "code",
          title: "src/hooks.py::failed_step",
          excerpt: "failed_step = result.failed_step",
          score: 0.24,
          streams: ["code_symbol_exact"],
          snippet: { text: "failed_step = result.failed_step", matched_terms: ["failed_step"] },
          retrieval_explanation: {
            score: 0.24,
            streams: ["code_symbol_exact"],
            raw_scores: { code_symbol_exact: 0.24 },
            scope: { label: "local" }
          }
        },
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "code",
          title: "src/hooks.py::log_path",
          excerpt: "log_path = result.log_path",
          score: 0.2,
          streams: ["code_symbol_exact"],
          snippet: { text: "log_path = result.log_path", matched_terms: ["log_path"] }
        }
      ],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filter_trace: { excluded: [] },
      suppression: {}
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.type(screen.getByLabelText("Dashboard search"), "closeout failed_step log_path{enter}");

    expect(await screen.findByText("Balanced results are code-heavy.")).toBeInTheDocument();
    expect(screen.getAllByText(/code - 1 matched term/i).length).toBeGreaterThanOrEqual(2);

    state.explainPayload = {
      query: "closeout failed_step log_path",
      results: [
        {
          kind: "corpus_chunk",
          logical_kind: "file",
          file_kind: "text",
          title: "AGENTS.md",
          excerpt: "If closeout fails, report failed_step and log_path.",
          score: 0.91,
          streams: ["corpus_lexical"],
          snippet: {
            text: "If closeout fails, report failed_step and log_path.",
            matched_terms: ["failed_step", "log_path"]
          }
        }
      ],
      brief: { text: "", token_budget: 0, packed: [], excluded: [] },
      filter_trace: { excluded: [] },
      suppression: {}
    };
    await user.click(screen.getByRole("button", { name: "Rerun Docs/files" }));

    expect(await screen.findByText("AGENTS.md")).toBeInTheDocument();
    expect(state.explainRequestPayload).toEqual({
      query: "closeout failed_step log_path",
      limit: 8,
      filters: {
        logical_kinds: ["file"],
        file_kinds: ["text", "document", "image"],
        current_only: false,
        lifecycle_states: [],
        include_suppressed: false
      }
    });
  });

  test("retrieval tab runs and displays retrieval benchmark history", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));

    expect(await screen.findByRole("heading", { name: "Retrieval Benchmarks" })).toBeInTheDocument();
    expect(screen.getAllByText("baseline").length).toBeGreaterThan(0);
    expect(screen.getByText("top1 80.0%")).toBeInTheDocument();
    expect(screen.getByText("brief dilution 20.0%")).toBeInTheDocument();
    expect(screen.getByText("top1 +10.0%")).toBeInTheDocument();
    expect(screen.getByText("brief dilution -5.0%")).toBeInTheDocument();
    expect(screen.getByText("High confidence: 3")).toBeInTheDocument();
    expect(screen.getByText("Semantic threshold 0.86")).toBeInTheDocument();
    expect(screen.getByText("3/4 calibration cases passed")).toBeInTheDocument();
    expect(screen.getByText("scope-filter")).toBeInTheDocument();
    expect(screen.getByText("top1 miss, scope miss")).toBeInTheDocument();
    expect(screen.getByText("current only - low confidence")).toBeInTheDocument();
    expect(screen.getByText("Expected evidence was not ranked first.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Run retrieval benchmark" }));

    await waitFor(() => {
      expect(state.retrievalBenchmarkRunPayload).toEqual({
        suite: "standard",
        label: "dashboard",
        limit_per_query: 5,
        persist: true
      });
    });
    await waitFor(() => {
      expect(screen.getAllByText("nightly").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("settings_mutated false")).toBeInTheDocument();
    expect(screen.getByText("Synthetic semantic duplicate calibration passed for 3/4 cases at threshold 0.86.")).toBeInTheDocument();
  });

  test("retrieval tab runs governance-shadow benchmark and displays shadow evidence", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));
    await user.selectOptions(await screen.findByLabelText("Benchmark suite"), "governance-shadow");
    await user.click(screen.getByRole("button", { name: "Run retrieval benchmark" }));

    await waitFor(() => {
      expect(state.retrievalBenchmarkRunPayload).toEqual({
        suite: "governance-shadow",
        label: "dashboard",
        limit_per_query: 5,
        persist: true
      });
    });
    expect(await screen.findByText("Governance shadow evaluation")).toBeInTheDocument();
    expect(screen.getByText("proposal precision 75.0%")).toBeInTheDocument();
    expect(screen.getByText("guardrails 1/1 passed")).toBeInTheDocument();
  });

  test("diagnostics renders structured errors with details, copy, and target navigation", async () => {
    const writeText = vi.fn(async () => undefined);
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    Object.defineProperty(globalThis.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    expect(window.navigator.clipboard?.writeText).toBe(writeText);
    state.healthPayload = {
      ...health,
      recent_error_details: [
        {
          code: "mail.oauth_unavailable",
          message: "OAuth database unavailable",
          severity: "error",
          component: "mail",
          stage: "oauth",
          retryable: true,
          user_action: "Open Mail and recheck OAuth configuration.",
          technical_detail: "mail OAuth lookup failed for gmail-capture",
          target: { type: "mail_profile", id: "gmail-capture" },
          links: [{ label: "Mail", tab: "mail", profile: "gmail-capture" }],
          status_code: null
        },
        {
          code: "corpus.job_failed",
          message: "PDF extraction failed",
          severity: "error",
          component: "worker",
          stage: "corpus_extract_pdf",
          retryable: true,
          user_action: "Open Jobs and inspect the failed task.",
          technical_detail: "job-1 failed while extracting docs/proposal.pdf",
          target: { type: "job", id: "job-1" },
          links: [{ label: "Jobs", tab: "jobs" }],
          status_code: null
      }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Diagnostics" }));
    const panel = screen.getByRole("heading", { name: "Actionable Diagnostics" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("OAuth database unavailable")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Open Mail and recheck OAuth configuration.")).toBeInTheDocument();

    await user.click(within(panel as HTMLElement).getByRole("button", { name: "Show diagnostic detail mail.oauth_unavailable" }));
    expect(within(panel as HTMLElement).getByText("mail OAuth lookup failed for gmail-capture")).toBeInTheDocument();

    const expandedPanel = screen.getByRole("heading", { name: "Actionable Diagnostics" }).closest(".panel");
    await user.click(within(expandedPanel as HTMLElement).getByRole("button", { name: "Copy diagnostic mail.oauth_unavailable" }));
    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"code": "mail.oauth_unavailable"'));
    });

    await user.click(within(expandedPanel as HTMLElement).getByRole("button", { name: "Open Mail for mail.oauth_unavailable" }));
    expect(await screen.findByRole("heading", { name: "Mail Profiles" })).toBeInTheDocument();
    expect(screen.getAllByText("gmail-capture").length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: "Diagnostics" }));
    await user.click(screen.getByRole("button", { name: "Open Jobs for corpus.job_failed" }));
    expect(await screen.findByRole("heading", { name: "Job Queue" })).toBeInTheDocument();
  });

  test("structured API error envelopes produce readable error toasts", async () => {
    state.crawlSyncErrorPayload = {
      error: {
        code: "crawl.root_invalid",
        message: "Watched path is missing",
        severity: "error",
        component: "crawler",
        stage: "validate_path",
        retryable: false,
        user_action: "Choose an existing directory.",
        technical_detail: "directory does not exist: E:/Missing",
        target: { type: "root", id: "docs" },
        links: [{ label: "Corpus", tab: "corpus" }],
        status_code: 400
      }
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Corpus" }));
    await user.click(screen.getByRole("button", { name: "Sync docs" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveClass("error");
    expect(alert).toHaveTextContent("Watched path is missing");
    expect(alert).not.toHaveTextContent("code=crawl.root_invalid");
  });

  test("clicking a mail search result opens a sanitized in-app mail detail viewer", async () => {
    state.searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "mail",
        id: "chunk-mail",
        title: "Mail: Customer RFP",
        summary: "From Sender; folder FluxCapture; 1 attachment.",
        source_path: "export-1/manifest.json",
        detail_ref: { kind: "corpus_chunk", id: "chunk-mail" },
        related_evidence_count: 2
      }
    ];
    state.resultDetailPayload = {
      logical_kind: "mail",
      title: "Mail: Customer RFP",
      mail: {
        subject: "Customer RFP",
        sender: "Sender <sender@example.com>",
        recipients: ["me@example.com"],
        received_at: "Tue, 23 Jun 2026 10:00:00 +0000",
        profile_name: "gmail-capture",
        source_folder: "FluxCapture",
        post_process_state: "exported"
      },
      body: {
        format: "html",
        html_sanitized: '<p>Please <strong>review</strong> the RFP.</p>',
        text: ""
      },
      attachments: [{ title: "rfp.pdf", path: "export-1/attachments/rfp.pdf", status: "metadata_only" }],
      related_evidence: [{ title: "body.html", path: "export-1/body.html", relationship: "body" }],
      provenance: [{ path: "export-1/manifest.json" }]
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "customer rfp{enter}");
    await user.click(await screen.findByRole("button", { name: /Mail: Customer RFP/ }));

    const dialog = await screen.findByRole("dialog", { name: "Mail: Customer RFP" });
    expect(dialog).toHaveTextContent("Sender <sender@example.com>");
    expect(dialog).toHaveTextContent("me@example.com");
    expect(dialog).toHaveTextContent("gmail-capture");
    expect(dialog).toHaveTextContent("FluxCapture");
    expect(dialog).toHaveTextContent("Please review the RFP.");
    expect(dialog).toHaveTextContent("rfp.pdf");
    expect(dialog.innerHTML).not.toContain("onclick");
    expect(dialog.innerHTML).not.toContain("<script");
    expect(screen.queryByText("export-1/body.txt")).not.toBeInTheDocument();
  });

  test("file result detail previews text, copies path, and routes open and reveal actions", async () => {
    const writeText = vi.fn(async () => undefined);
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "clipboard", {
      configurable: true,
      value: { writeText }
    });
    expect(window.navigator.clipboard?.writeText).toBe(writeText);
    state.searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        id: "chunk-file",
        asset_id: "asset-file",
        title: "Project Plan",
        excerpt: "Milestone details",
        source_path: "plans/project-plan.md",
        detail_ref: { kind: "corpus_chunk", id: "chunk-file" }
      }
    ];
    state.resultDetailPayload = {
      logical_kind: "file",
      title: "Project Plan",
      asset_id: "asset-file",
      metadata: { path: "plans/project-plan.md", canonical_path: "E:/Flux Docs/plans/project-plan.md", status: "indexed" },
      preview: { available: true, text: "Milestone details and owners.", chunks: [] },
      actions: {
        copy_path: { available: true, path: "E:/Flux Docs/plans/project-plan.md" },
        open: { available: true },
        reveal: { available: true }
      },
      related_evidence: [],
      provenance: []
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "project plan{enter}");
    await user.click(await screen.findByRole("button", { name: /Project Plan/ }));

    expect(await screen.findByRole("dialog", { name: "Project Plan" })).toHaveTextContent("Milestone details and owners.");
    const copyButton = screen.getByRole("button", { name: "Copy path" });
    expect(screen.getByText("E:/Flux Docs/plans/project-plan.md")).toBeInTheDocument();
    expect(copyButton).toBeEnabled();
    await user.click(copyButton);
    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith("E:/Flux Docs/plans/project-plan.md");
    });
    await user.click(screen.getByRole("button", { name: "Dismiss notification" }));
    await user.click(screen.getByRole("button", { name: "Open with default app" }));
    await user.click(screen.getByRole("button", { name: "Reveal in folder" }));

    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        "/api/corpus/assets/asset-file/actions",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ action: "open" }) })
      );
      expect(fetch).toHaveBeenCalledWith(
        "/api/corpus/assets/asset-file/actions",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ action: "reveal" }) })
      );
    });
    expect(screen.queryByText("Open request opened.")).not.toBeInTheDocument();
    expect(screen.queryByText("Reveal request opened.")).not.toBeInTheDocument();
  });

  test("file detail disables unavailable actions with readable reasons", async () => {
    state.searchPayload = [
      {
        kind: "corpus_chunk",
        logical_kind: "file",
        id: "chunk-deleted",
        asset_id: "asset-deleted",
        title: "Deleted Proposal",
        excerpt: "deleted",
        source_path: "archive/deleted.docx",
        detail_ref: { kind: "corpus_chunk", id: "chunk-deleted" }
      }
    ];
    state.resultDetailPayload = {
      logical_kind: "file",
      title: "Deleted Proposal",
      asset_id: "asset-deleted",
      metadata: { path: "archive/deleted.docx", canonical_path: "E:/Flux Docs/archive/deleted.docx", status: "deleted" },
      preview: { available: false, text: "", chunks: [] },
      actions: {
        copy_path: { available: true, path: "E:/Flux Docs/archive/deleted.docx" },
        open: { available: false, disabled_reason: "Asset is deleted from the index." },
        reveal: { available: false, disabled_reason: "Asset is deleted from the index." }
      },
      related_evidence: [],
      provenance: []
    };
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.type(screen.getByLabelText("Dashboard search"), "deleted proposal{enter}");
    await user.click(await screen.findByRole("button", { name: /Deleted Proposal/ }));

    expect(await screen.findByRole("dialog", { name: "Deleted Proposal" })).toHaveTextContent("No extracted text is available.");
    expect(screen.getByRole("button", { name: "Open with default app" })).toBeDisabled();
    expect(screen.getByText("Asset is deleted from the index.")).toBeInTheDocument();
  });
});
