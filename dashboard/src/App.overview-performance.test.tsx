import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test, vi } from "vitest";
import App from "./App";
import { crawl, dashboardTestState as state, deferredResponse, errorJson, health, json, mail, outlook, setupDashboardTest } from "./test/appHarness";

describe("Flux dashboard", () => {
  setupDashboardTest();

  test("defaults to overview and renders a friendly read-only status console", async () => {
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Overview" })).toHaveClass("active");
    expect(screen.getByRole("heading", { name: "System Overview" })).toBeInTheDocument();
    expect(screen.getByText("What needs attention")).toBeInTheDocument();
    expect(screen.getByText("Flux handled automatically")).toBeInTheDocument();
    expect(screen.getByText("Next safe action")).toBeInTheDocument();
    expect(screen.getByText("Database paths")).toBeInTheDocument();
    expect(screen.getByText("Outlook Host")).toBeInTheDocument();
    expect(screen.getByText("Host Agent")).toBeInTheDocument();
    expect(screen.getByText("Codex Integration")).toBeInTheDocument();
    expect(screen.getByText("Codex restart required")).toBeInTheDocument();
    expect(screen.getByText(/Auto-refresh every 1s/i)).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Mail profiles" })).not.toBeInTheDocument();
    expect(screen.queryByText(/"database"/)).not.toBeInTheDocument();
  });

  test("top health chips distinguish API and host database paths", async () => {
    state.healthPayload = {
      ...health,
      database: {
        ok: false,
        message: "host-published database blocked",
        checks: {
          service: { ok: true, message: "database reachable", required: true, label: "API database" },
          host_published: { ok: false, message: "connection failed", required: true, label: "Host database" }
        }
      }
    };

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    const apiDbChip = screen.getAllByText("API DB").map((item) => item.closest(".status-chip")).find(Boolean);
    const hostDbChip = screen.getAllByText("Host DB").map((item) => item.closest(".status-chip")).find(Boolean);
    expect(apiDbChip).toHaveTextContent("Healthy");
    expect(hostDbChip).toHaveTextContent("Blocked");
    expect(screen.queryByText("PG")).not.toBeInTheDocument();
  });

  test("settings system section exposes Codex hooks deployment and runtime controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Settings" }));
    expect(screen.getByRole("heading", { name: "Codex Hooks" })).toBeInTheDocument();
    expect(screen.getByText("Preflight brief")).toBeInTheDocument();
    expect(screen.getByText("Turn capture")).toBeInTheDocument();
    expect(screen.getByText("codex_hook.preflight_injected")).toBeInTheDocument();
    expect(screen.getByText("MCP tools")).toBeInTheDocument();
    expect(screen.getByText("kb.brief ready")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Deployment" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /^Runtime Actions/ })).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getAllByText("codex.hooks.enabled").length).toBeGreaterThan(0);
    });
    expect(screen.getByText("Enable Flux Codex hook policy evaluation.")).toBeInTheDocument();
  });

  test("performance shows acceleration capabilities, cache layout, and family telemetry", async () => {
    const user = userEvent.setup();
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Operations" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Performance" }));
    expect(screen.getByRole("heading", { name: "Acceleration" })).toBeInTheDocument();
    expect(screen.getByText("NVIDIA")).toBeInTheDocument();
    expect(screen.getByText("nvidia-smi not found")).toBeInTheDocument();
    expect(screen.getByText("Local Model")).toBeInTheDocument();
    expect(screen.getByText("disabled")).toBeInTheDocument();
    expect(screen.getByText("Watcher Policy")).toBeInTheDocument();
    expect(screen.getAllByText("watchdog").length).toBeGreaterThan(0);
    expect(screen.getByText("auto")).toBeInTheDocument();
    expect(screen.getByText("D:/FluxLLMKB/private/cache")).toBeInTheDocument();
    expect(screen.getByText("Container resources")).toBeInTheDocument();
    expect(screen.getByText("2 running / 2 reported")).toBeInTheDocument();
    expect(screen.getByText("memory 1.25 GB / 5 GB")).toBeInTheDocument();
    expect(screen.getByText("flux-llm-kb-api; CPU 12.34%; memory 512 MB / 2 GB (25%); writable 128 MB; block I/O 1 MB / 64 MB")).toBeInTheDocument();
    expect(screen.getAllByText("media").length).toBeGreaterThan(0);
    expect(screen.getByText("p95 95ms; OCR 6 hit / 2 miss; ASR 4 hit / 1 miss; 9 segments; Vision 5 hit / 2 miss; 3 descriptions; 1 blocked; 4 decorative skips; Frames 6 sampled; thumbnails 7 hit / 8 miss; Search index 10 vectors; 2 skipped; 1 batches; cache 3 hit / 4 miss")).toBeInTheDocument();
    expect(screen.getByText("Family Backpressure")).toBeInTheDocument();
    expect(screen.getByText("cap 1/1")).toBeInTheDocument();
    expect(screen.getByText("Cap Reached; oldest 120s; retry 2; blocked locks 1; parser 3 hit / 1 miss; 5 manifest skips")).toBeInTheDocument();
    expect(screen.getByText("Benchmark History")).toBeInTheDocument();
    expect(screen.getByText("image-heavy")).toBeInTheDocument();
    expect(screen.getByText("Scan / warm / pass 2")).toBeInTheDocument();
    expect(screen.getByText("10 files/s; -250ms; +2 files/s")).toBeInTheDocument();
    expect(screen.getByText("after-deploy; desktop-after; Monitored Root; hash 4; workers 3; 8 manifest skips; model disabled; 2 blocked")).toBeInTheDocument();
    expect(screen.getByText("Reliability Gate")).toBeInTheDocument();
    expect(screen.getByText("Reliability Matrix")).toBeInTheDocument();
    expect(screen.getByText("1 ready / 1 partial / 1 not run")).toBeInTheDocument();
    expect(screen.getByText("benchmark bench-docs")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud reliability evidence and clear blocked or pending work.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Operator Evidence" })).toBeInTheDocument();
    expect(screen.getByText("VSS Snapshot")).toBeInTheDocument();
    expect(screen.getAllByText("Hold").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Provider Acceleration")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud reliability evidence.")).toBeInTheDocument();
    expect(screen.getByText("Code feedback reported misses.")).toBeInTheDocument();
    expect(screen.getAllByText("Partial").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Synthetic reliability evidence is current.")).toBeInTheDocument();
    expect(screen.getByText("Run scoped host/cloud calibration for the selected root.")).toBeInTheDocument();
    expect(await screen.findByText("docs / partial")).toBeInTheDocument();
    expect(screen.getByText("crawler.hash_parallelism")).toBeInTheDocument();
    expect(screen.getByText("needs comparison")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run reliability gate" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run all roots" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run reliability diagnostics" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run host/cloud calibration" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run cache readiness" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run tuning diagnostics" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Run reliability gate" }));
    expect(state.reliabilityRunPayload).toEqual({ scope: "root", root_name: "docs", max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true });
    await user.click(screen.getByRole("button", { name: "Run all roots" }));
    expect(state.reliabilityRunPayload).toEqual({ scope: "all_roots", max_files: 1000, passes: 2, include_cache_readiness: false, include_tuning: true, evidence_level: "full" });
    await user.click(screen.getByRole("button", { name: "Run scan benchmark" }));
    expect(state.benchmarkRunPayload).toEqual({ fixture: "all", files: 10, mode: "scan", passes: 2, workers: 1, family: "all", scope: "synthetic", scenario: "standard" });
    await user.click(screen.getByRole("button", { name: "Run tuning diagnostics" }));
    expect(state.benchmarkRunPayload).toEqual({ fixture: "all", files: 10, mode: "scan", passes: 2, workers: 1, family: "all", scope: "synthetic", scenario: "tuning" });
    expect(await screen.findByText("Manual candidates")).toBeInTheDocument();
    expect(screen.getByText("crawler.hash_parallelism")).toBeInTheDocument();
    expect(screen.getByText("current 1 -> candidate 4")).toBeInTheDocument();
    expect(screen.getAllByText("office").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("3 pending")).toBeInTheDocument();
  });

  test("performance shows privacy-safe model activity and scheduler metrics", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("2 recent / 0 active")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("model-runner")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("ollama")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Retrieval")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Vision OCR")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Scheduler")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("1 running / 1 waiting")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("2 rejected / 1 timed out")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("1 recent eviction")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("8120/16380 MB")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("Snowflake/snowflake-arctic-embed-l-v2.0")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("/v1/rerank")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("redacted failure")).toBeInTheDocument();
  });

  test("performance labels model activity dependency blockers without failure wording", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      recent_count: 1,
      active_count: 0,
      service_breakdown: [
        { service: "worker", count: 1, active: 0, failures: 0 }
      ],
      class_breakdown: [
        { activity_class: "vision_ocr", count: 1 }
      ],
      events: [
        {
          id: "event-paddle-dependency",
          service: "worker",
          endpoint: "/v1/ocr/document",
          action: "ocr_document",
          activity_class: "vision_ocr",
          caller_surface: "worker",
          model: "PaddleOCR-VL",
          status: "blocked_missing_dependency",
          started_at: "2026-07-03T01:24:58+00:00",
          completed_at: "2026-07-03T01:25:00+00:00",
          duration_ms: 15359,
          error_class: "DependencyError",
          error_message: "PaddleOCR-VL requires additional dependencies"
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("PaddleOCR-VL requires additional dependencies")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("worker missing dependency")).toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText("worker failure")).not.toBeInTheDocument();
  });

  test("performance labels stale model activity without active work", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      recent_count: 1,
      active_count: 0,
      service_breakdown: [
        { service: "model-runner", count: 1, active: 0, failures: 0 }
      ],
      class_breakdown: [
        { activity_class: "retrieval", count: 1 }
      ],
      events: [
        {
          id: "event-stale-rerank",
          service: "model-runner",
          endpoint: "/v1/rerank",
          action: "rerank",
          activity_class: "retrieval",
          caller_surface: "mcp",
          model: "Qwen/Qwen3-Reranker-4B",
          status: "stale_running",
          started_at: "2026-07-03T01:00:00+00:00",
          completed_at: "2026-07-03T01:30:00+00:00",
          duration_ms: 1800000,
          error_class: "ModelActivityStale",
          error_message: "Model activity exceeded the stale threshold without a finish update."
        }
      ]
    };
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("1 recent / 0 active")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("model-runner / Stale")).toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText("model-runner / Stale Running")).not.toBeInTheDocument();
  });

  test("performance hides control-plane health events from model activity", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      recent_count: 2,
      active_count: 0,
      service_breakdown: [
        { service: "model-runner", count: 2, active: 0, failures: 0 }
      ],
      class_breakdown: [
        { activity_class: "control_plane", count: 1 },
        { activity_class: "retrieval", count: 1 }
      ],
      events: [
        {
          id: "event-health",
          service: "model-runner",
          endpoint: "/health",
          action: "health",
          activity_class: "control_plane",
          caller_surface: "",
          model: "",
          status: "completed",
          started_at: "2026-07-03T01:25:50+00:00",
          completed_at: "2026-07-03T01:25:50+00:00",
          duration_ms: 10,
          error_class: null,
          error_message: null
        },
        {
          id: "event-rerank",
          service: "model-runner",
          endpoint: "/v1/rerank",
          action: "rerank",
          activity_class: "retrieval",
          caller_surface: "mcp",
          model: "Qwen/Qwen3-Reranker-4B",
          status: "completed",
          started_at: "2026-07-03T01:25:58+00:00",
          completed_at: "2026-07-03T01:26:00+00:00",
          duration_ms: 1842,
          error_class: null,
          error_message: null
        }
      ]
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("/v1/rerank")).toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText("/health")).not.toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText("Control Plane")).not.toBeInTheDocument();
  });

  test("performance shows Paddle OCR model activity by default", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      active_count: 0,
      recent_count: 2,
      total_count: 2,
      events: [
        {
          id: "event-health",
          service: "model-runner",
          endpoint: "/health",
          action: "health",
          activity_class: "control_plane",
          caller_surface: "",
          model: "",
          status: "completed",
          started_at: "2026-07-03T01:25:50+00:00",
          completed_at: "2026-07-03T01:25:50+00:00",
          duration_ms: 10,
          error_class: null,
          error_message: null
        },
        {
          id: "event-ocr",
          service: "paddle-runner",
          endpoint: "/v1/ocr/image",
          action: "ocr_image",
          activity_class: "vision_ocr",
          caller_surface: "worker",
          model: "PP-OCRv5",
          status: "completed",
          started_at: "2026-07-03T01:26:01+00:00",
          completed_at: "2026-07-03T01:26:04+00:00",
          duration_ms: 340,
          error_class: null,
          error_message: null
        }
      ]
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("/v1/ocr/image")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByText("PP-OCRv5; Worker; Vision OCR")).toBeInTheDocument();
    expect(within(panel as HTMLElement).queryByText("/health")).not.toBeInTheDocument();
  });

  test("performance paginates model activity with clickable page numbers", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      limit: 50,
      offset: 0,
      total_count: 125,
      has_next: true,
      page_count: 3,
      recent_count: 50
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const pager = screen.getByLabelText("Model activity paging");
    expect(within(pager).getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent)).toEqual([
      "Previous model activity page",
      "Current model activity page 1",
      "Go to model activity page 2",
      "Go to model activity page 3",
      "Next model activity page"
    ]);
    expect(within(pager).getByText("1-2 of 125 model activity events")).toBeInTheDocument();

    await user.click(within(pager).getByRole("button", { name: "Go to model activity page 3" }));

    await waitFor(() => {
      const latestUrl = state.modelActivityRequestUrls.at(-1) ?? "";
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("limit")).toBe("50");
      expect(params.get("offset")).toBe("100");
    });
  });

  test("performance model activity uses semantic tables outside the jobs tab", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    const panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByRole("table", { name: "Model service activity" })).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByRole("table", { name: "Model activity events" })).toBeInTheDocument();
  });

  test("performance can opt into control-plane model activity diagnostics", async () => {
    const user = userEvent.setup();
    state.modelActivityPayload = {
      ...state.modelActivityPayload,
      recent_count: 2,
      active_count: 0,
      service_breakdown: [
        { service: "model-runner", count: 2, active: 0, failures: 0 }
      ],
      class_breakdown: [
        { activity_class: "control_plane", count: 1 },
        { activity_class: "retrieval", count: 1 }
      ],
      events: [
        {
          id: "event-health",
          service: "model-runner",
          endpoint: "/health",
          action: "health",
          activity_class: "control_plane",
          caller_surface: "",
          model: "",
          status: "completed",
          started_at: "2026-07-03T01:25:50+00:00",
          completed_at: "2026-07-03T01:25:50+00:00",
          duration_ms: 10,
          error_class: null,
          error_message: null
        },
        {
          id: "event-rerank",
          service: "model-runner",
          endpoint: "/v1/rerank",
          action: "rerank",
          activity_class: "retrieval",
          caller_surface: "mcp",
          model: "Qwen/Qwen3-Reranker-4B",
          status: "completed",
          started_at: "2026-07-03T01:25:58+00:00",
          completed_at: "2026-07-03T01:26:00+00:00",
          duration_ms: 1842,
          error_class: null,
          error_message: null
        }
      ]
    };

    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Performance" }));

    let panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).queryByText("/health")).not.toBeInTheDocument();

    await user.click(within(panel as HTMLElement).getByRole("checkbox", { name: "Show control-plane diagnostics" }));

    await waitFor(() => {
      const latestUrl = state.modelActivityRequestUrls.at(-1) ?? "";
      expect(latestUrl.startsWith("/api/dashboard/model-activity?")).toBe(true);
      const params = new URLSearchParams(latestUrl.split("?")[1]);
      expect(params.get("include_control_plane")).toBe("true");
      expect(params.get("offset")).toBe("0");
      expect(params.get("limit")).toBe("50");
    });
    panel = screen.getByRole("heading", { name: "Model activity" }).closest(".panel");
    expect(within(panel as HTMLElement).getByText("/health")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getAllByText("Control Plane").length).toBeGreaterThan(0);
  });

  test("retrieval tab owns code diagnostics and code-search quality controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Retrieval" }));

    expect(screen.getByRole("heading", { name: "Code Diagnostics" })).toBeInTheDocument();
    expect(screen.getByText("Code Assets")).toBeInTheDocument();
    expect(screen.getByText("7 symbols / 9 refs")).toBeInTheDocument();
    expect(screen.getByText("python 4; typescript 3; Parsed 6; Fallback 1")).toBeInTheDocument();
    expect(screen.getByText("Code Feedback")).toBeInTheDocument();
    expect(screen.getByText("2 feedback events")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Submit code feedback" }));
    expect(state.codeFeedbackPayload).toMatchObject({ surface: "dashboard", miss_category: "missing_symbol" });
    await user.clear(screen.getByLabelText("Code search query"));
    await user.type(screen.getByLabelText("Code search query"), "build_invoice");
    await user.clear(screen.getByLabelText("Code path glob"));
    await user.type(screen.getByLabelText("Code path glob"), "src/*.py");
    await user.click(screen.getByRole("button", { name: "Run code search" }));
    expect(state.codeSearchRequestUrl).toContain("/api/code/search?");
    expect(state.codeSearchRequestUrl).toContain("query=build_invoice");
    expect(state.codeSearchRequestUrl).toContain("relationship=call");
    expect(state.codeSearchRequestUrl).toContain("path_glob=src%2F*.py");
    expect(state.codeSearchRequestUrl).toContain("include_generated=false");
    expect(await screen.findByText("OrderService.build_invoice")).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Symbol lookup query"));
    await user.type(screen.getByLabelText("Symbol lookup query"), "OrderService.build_invoice");
    await user.click(screen.getByRole("button", { name: "Lookup code symbol" }));
    expect(state.codeSymbolRequestUrl).toContain("/api/code/symbols?");
    expect(state.codeSymbolRequestUrl).toContain("symbol=OrderService.build_invoice");
    expect(await screen.findByText("test_build_invoice_returns_ready_status")).toBeInTheDocument();
  });

  test("diagnostics tab owns operational diagnostics and safe remediation controls", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Operations" });
    await user.click(screen.getByRole("button", { name: "Diagnostics" }));

    expect(screen.getByRole("heading", { name: "Operational Diagnostics" })).toBeInTheDocument();
    expect(screen.getByText("Blocked jobs")).toBeInTheDocument();
    expect(screen.getByText("1 blocked locks")).toBeInTheDocument();
    expect(screen.getByText("Job job-1 is blocked.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry corpus job" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Apply diagnostic filters" }));
    expect(fetch).toHaveBeenCalledWith("/api/diagnostics/all?root_name=docs&status=blocked_missing_dependency&family=office&include_details=true");
    vi.spyOn(window, "confirm").mockReturnValueOnce(true);
    await user.click(screen.getByRole("button", { name: "Retry corpus job" }));
    expect(state.diagnosticsActionPayload).toEqual({
      action: "retry_corpus_job",
      target_type: "job",
      target_id: "job-1",
      root_name: "docs",
      family: "office",
      reason: "operator diagnostic remediation"
    });
  });

  test("automation tab lists guarded actions manual blocks audit trail and can run a guarded pass", async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: "Automation" }));

    expect(await screen.findByRole("heading", { name: "Guarded Automation" })).toBeInTheDocument();
    expect(screen.getByText("Guarded Auto")).toBeInTheDocument();
    expect(screen.getByText("Refresh retrieval evidence")).toBeInTheDocument();
    expect(screen.getByText("Ingest approved captures")).toBeInTheDocument();
    expect(screen.getByText("Delete or purge data")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Automation Audit Trail" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Run guarded pass now" }));

    await waitFor(() => {
      expect(state.automationRunPayload).toEqual({ mode: "guarded", dry_run: false, limit: 25 });
    });
    expect(screen.getByText(/Guarded automation completed/i)).toBeInTheDocument();
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
      const healthCalls = vi.mocked(fetch).mock.calls.filter(([url]) => String(url) === "/api/dashboard/health");
      expect(healthCalls.length).toBeGreaterThanOrEqual(2);
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
});
