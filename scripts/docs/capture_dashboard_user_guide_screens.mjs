import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import fs from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { responseForApiRequest } from "./dashboard_user_guide_fixtures.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..", "..");
const dashboardDir = path.join(repoRoot, "dashboard");
const screensDir = path.join(repoRoot, "docs", "user-guide", "screens");
const dashboardRequire = createRequire(path.join(dashboardDir, "package.json"));
const { chromium } = dashboardRequire("@playwright/test");

const scenarios = [
  { slug: "global-controls", tab: "overview", waitFor: "System Overview", action: openMoreActions },
  { slug: "overview", tab: "overview", waitFor: "System Overview" },
  { slug: "automation", tab: "automation", waitFor: "Guarded Automation" },
  { slug: "automation-after-run", tab: "automation", waitFor: "Guarded Automation", action: runGuardedAutomation },
  { slug: "diagnostics", tab: "diagnostics", waitFor: "Actionable Diagnostics" },
  { slug: "diagnostics-detail", tab: "diagnostics", waitFor: "Actionable Diagnostics", action: openDiagnosticDetail },
  { slug: "performance", tab: "performance", waitFor: "Acceleration" },
  { slug: "performance-benchmark", tab: "performance", waitFor: "Acceleration", action: runTuningBenchmark },
  { slug: "corpus", tab: "corpus", waitFor: "Corpus Monitor" },
  { slug: "corpus-root-form", tab: "corpus", waitFor: "Corpus Monitor", action: openRootForm },
  { slug: "mail", tab: "mail", waitFor: "Mail Profiles" },
  { slug: "mail-profile-form", tab: "mail", waitFor: "Mail Profiles", action: openMailProfileForm },
  { slug: "retrieval", tab: "retrieval", waitFor: "Retrieval Console" },
  { slug: "retrieval-result-detail", tab: "retrieval", waitFor: "Retrieval Console", action: openSearchResultDetail },
  { slug: "retrieval-code-diagnostics", tab: "retrieval", waitFor: "Code Diagnostics", action: runCodeDiagnostics },
  { slug: "review", tab: "review", waitFor: "Claim Review" },
  { slug: "review-capture-decision", tab: "review", waitFor: "Capture Review Queue", action: openCaptureDecision },
  { slug: "settings", tab: "settings", waitFor: "Runtime Settings" },
  { slug: "settings-editor", tab: "settings", waitFor: "Runtime Settings", action: openSettingEditor },
  { slug: "jobs", tab: "jobs", waitFor: "Job Queue" },
  { slug: "result-detail", tab: "retrieval", waitFor: "Retrieval Console", action: openSearchResultDetail }
];

async function main() {
  const port = await getAvailablePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const server = startViteServer(port);
  let browser;
  try {
    await waitForServer(baseUrl);
    await fs.mkdir(screensDir, { recursive: true });
    browser = await launchBrowser();
    const context = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
      deviceScaleFactor: 1,
      colorScheme: "light"
    });
    await context.addInitScript(() => {
      window.localStorage.clear();
      window.sessionStorage.clear();
    });
    const page = await context.newPage();
    await page.route("**/api/**", async (route) => {
      const request = route.request();
      let body;
      const postData = request.postData();
      if (postData) {
        try {
          body = JSON.parse(postData);
        } catch {
          body = postData;
        }
      }
      const payload = responseForApiRequest(request.url(), request.method(), body);
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(payload)
      });
    });
    await page.addStyleTag({
      content: `
        *, *::before, *::after {
          animation-duration: 0s !important;
          animation-delay: 0s !important;
          transition-duration: 0s !important;
          caret-color: transparent !important;
        }
      `
    });

    for (const scenario of scenarios) {
      await captureScenario(page, baseUrl, scenario);
    }
  } finally {
    await browser?.close().catch(() => undefined);
    stopServer(server);
  }
}

async function launchBrowser() {
  try {
    return await chromium.launch();
  } catch (error) {
    const message = String(error?.message ?? error);
    if (!message.includes("Executable doesn't exist")) {
      throw error;
    }
    for (const channel of ["chrome", "msedge"]) {
      try {
        return await chromium.launch({ channel });
      } catch {
        // Try the next locally installed browser channel.
      }
    }
    throw error;
  }
}

async function captureScenario(page, baseUrl, scenario) {
  await page.goto(`${baseUrl}/?tab=${scenario.tab}`, { waitUntil: "networkidle" });
  await page.getByText(scenario.waitFor, { exact: false }).first().waitFor({ timeout: 20_000 });
  await page.addStyleTag({ content: "body { background: #f4f7fb !important; }" }).catch(() => undefined);
  if (scenario.action) {
    await scenario.action(page);
    await page.waitForTimeout(250);
  }
  await page.screenshot({
    path: path.join(screensDir, `${scenario.slug}.png`),
    fullPage: false
  });
}

async function openMoreActions(page) {
  await page.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menu", { name: "More actions" }).waitFor();
}

async function runGuardedAutomation(page) {
  await page.getByRole("button", { name: "Run guarded pass now" }).click();
  await page.getByText("Guarded automation completed", { exact: false }).waitFor();
}

async function openDiagnosticDetail(page) {
  await page.getByRole("button", { name: /Show diagnostic detail office\.extractor_missing_dependency/i }).click();
  await page.getByText("Public-safe fixture detail", { exact: false }).first().waitFor();
}

async function runTuningBenchmark(page) {
  await page.getByRole("button", { name: "Run tuning diagnostics" }).click();
  await page.getByText("Public-safe benchmark fixture completed", { exact: false }).waitFor();
}

async function openRootForm(page) {
  await page.getByRole("button", { name: /Add Watched Path/i }).click();
  await page.getByRole("dialog", { name: /Add Watched Path/i }).waitFor();
}

async function openMailProfileForm(page) {
  await page.getByRole("button", { name: /Add Profile/i }).click();
  await page.getByRole("dialog", { name: /Add Mail Profile/i }).waitFor();
}

async function openSearchResultDetail(page) {
  await page.getByLabel("Dashboard search").fill("operator dashboard");
  await page.getByLabel("Dashboard search").press("Enter");
  await page.getByText("Operator dashboard runbook", { exact: false }).waitFor();
  await page.getByText("Operator dashboard runbook", { exact: false }).first().click();
  await page.getByRole("dialog", { name: /Operator dashboard runbook/i }).waitFor();
}

async function runCodeDiagnostics(page) {
  await page.getByRole("button", { name: "Run code search" }).click();
  await page.getByText("SearchService.search", { exact: false }).waitFor();
  await page.getByRole("button", { name: "Lookup code symbol" }).click();
  await page.getByText("symbol row", { exact: false }).waitFor();
}

async function openCaptureDecision(page) {
  await page.getByRole("button", { name: /Approve capture job job-review/i }).click();
  await page.getByRole("dialog", { name: /Approve Capture/i }).waitFor();
}

async function openSettingEditor(page) {
  await page.getByRole("button", { name: /Edit retrieval\.token_budget/i }).click();
  await page.getByRole("dialog", { name: /Edit Setting/i }).waitFor();
}

function startViteServer(port) {
  const viteBin = path.join(dashboardDir, "node_modules", "vite", "bin", "vite.js");
  const child = spawn(
    process.execPath,
    [viteBin, "--host", "127.0.0.1", "--port", String(port), "--strictPort"],
    {
      cwd: dashboardDir,
      env: { ...process.env, BROWSER: "none" },
      stdio: ["ignore", "pipe", "pipe"],
      shell: false
    }
  );
  let stderr = "";
  child.stderr.on("data", (chunk) => {
    stderr += chunk.toString();
  });
  child.on("exit", (code) => {
    if (code !== null && code !== 0) {
      process.stderr.write(stderr);
    }
  });
  return child;
}

function stopServer(child) {
  if (!child || child.killed) return;
  child.kill("SIGTERM");
}

async function waitForServer(baseUrl) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(baseUrl);
      if (response.ok) return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }
  throw new Error(`Timed out waiting for dashboard dev server at ${baseUrl}`);
}

async function getAvailablePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 5173;
      server.close(() => resolve(port));
    });
  });
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
