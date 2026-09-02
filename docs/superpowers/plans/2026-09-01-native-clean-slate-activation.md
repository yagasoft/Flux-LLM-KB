# Native clean-slate activation implementation plan

> **Status: Superseded.** This historical plan is replaced by the approved [2 September 2026 native one-shot hard-cutover plan](2026-09-02-native-hard-cutover.md).

> **For the implementer:** Execute this plan inline, in order, in the dedicated \`codex/native-clean-slate-activation\` worktree. Do not use the Flux plugin or its MCP tools in this task.

**Goal:** Replace the active Python/PostgreSQL Codex integration with the native loopback plugin; repair strict-production Sources and Outlook pages; activate implemented native workers; and leave all unprovisioned provider features disabled.

**Architecture:** Production composition will retain its strict path and operator safeguards while registering the persisted source and Outlook control planes that the navigation exposes. A native local hook endpoint and generated PowerShell hook adapter will preserve the Codex hook protocol without Python or PostgreSQL. The guarded clean-slate release sequence will activate only ready, implemented services, register and prove the native plugin, then remove the legacy Codex plugin.

**Tech stack:** .NET/C# web host and integration projects, SQL Server, PowerShell deployment and Codex CLI registration, xUnit, existing native browser/integration test harnesses.

**Approved specification:** \`docs/superpowers/specs/2026-09-01-native-clean-slate-activation-design.md\`

**Non-negotiable operational limits:**

- The destructive clean slate is exactly \`I:\\FluxKnowledge\` and the \`FluxKnowledge\` SQL catalogue. Do not delete or migrate \`J:\\FluxLLMKB\`.
- Keep loopback-only access, application-owned path enforcement and input validation. These are safety boundaries, not feature flags.
- Enable the implemented source, Outlook and native worker paths. Do not download or provision future model, OCR, ASR, GPU, FFmpeg or network parsing providers. Their runtime flags remain false.
- Execute actual release only through \`scripts/dev/complete-feature.ps1\` after focused verification has passed. It must show its existing confirmations and fresh command evidence.

## Task 1: make strict production compose its advertised control planes

**Files:**

- Modify: \`src/FluxKnowledge.Web/WebHostComposition.cs\`
- Test: \`tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs\`
- Test: \`tests/FluxKnowledge.Web.Tests/Browser/Phase3ASourceManagementBrowserTests.cs\`
- Test: \`tests/FluxKnowledge.Web.Tests/Browser/NativeOutlookConfigurationBrowserTests.cs\`

**Step 1: Write the failing composition tests.**

Construct the strict-production service collection with the same configuration shape used by the live host. Assert that it resolves the source and Outlook page services and that the required SQL stores are registered:

\`\`\`csharp
using var provider = services.BuildServiceProvider();
Assert.IsType<SourceRootService>(provider.GetRequiredService<SourceRootService>());
Assert.IsType<OutlookPageState>(provider.GetRequiredService<OutlookPageState>());
Assert.IsAssignableFrom<ISourceRootStore>(provider.GetRequiredService<ISourceRootStore>());
Assert.IsAssignableFrom<IOutlookCaptureStore>(provider.GetRequiredService<IOutlookCaptureStore>());
\`\`\`

Add browser/endpoint assertions that \`GET /sources\` and \`GET /outlook\` are successful in strict production composition. Run the two focused test projects and confirm this fails because strict composition does not register those services.

**Step 2: Move control-plane registrations out of the strict-path exclusion.**

Keep SQL stores, \`SourceRootService\`, \`SourceScanControl\`, Outlook stores and \`OutlookPageState\` unconditional where their dependencies satisfy strict path rules. Drive optional hosted services from explicit configuration, not from \`!strictProductionPaths\`:

\`\`\`csharp
var workersEnabled = configuration.GetValue<bool>("Worker:Enabled");
if (workersEnabled)
{
    services.AddHostedService<SourceScanWorker>();
}
\`\`\`

Preserve the existing source-root policy, SQL fencing and Outlook COM guard. Replace the broad strict-production removal of all \`IHostedService\` registrations with a narrow removal of only services not approved for the native production configuration.

**Step 3: Run the focused tests.**

\`\`\`powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~WebHostCompositionTests|FullyQualifiedName~Phase3ASourceManagementBrowserTests|FullyQualifiedName~NativeOutlookConfigurationBrowserTests"
\`\`\`

Expected: tests pass and the old DI failure cannot recur. Inspect the diff for any accidental weakening of strict-path checks.

**Step 4: Commit the coherent batch.**

\`\`\`powershell
git add src/FluxKnowledge.Web/WebHostComposition.cs tests/FluxKnowledge.Web.Tests
git commit -m "fix: compose source and Outlook pages in production"
\`\`\`

## Task 2: express the native production capability boundary in configuration

**Files:**

- Modify: \`src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveWindowsAdapters.cs\`
- Modify: \`src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveWindowsHostPorts.cs\`
- Modify: \`src/FluxKnowledge.Web/Configuration/NativeGoLiveRuntimeOptions.cs\`
- Test: \`tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveOneShotAdmissionTests.cs\`
- Test: \`tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveLiveGateCompositionTests.cs\`
- Test: \`tests/native/native-go-live-contract.ps1\`

**Step 1: Write failing configuration tests.**

Test the generated strict-production configuration has source/worker/Outlook operations enabled, but has every unprovisioned provider flag disabled:

\`\`\`csharp
Assert.True(configuration["Worker:Enabled"]);
Assert.True(configuration["OutlookCapture:Enabled"]);
Assert.False(configuration["Runtime:Model:Enabled"]);
Assert.False(configuration["Runtime:Gpu:Enabled"]);
Assert.False(configuration["Runtime:Ocr:Enabled"]);
Assert.False(configuration["Runtime:Asr:Enabled"]);
Assert.False(configuration["Runtime:Ffmpeg:Enabled"]);
Assert.False(configuration["Runtime:NetworkParsing:Enabled"]);
\`\`\`

Add an admission test that accepts these operational flags only when their native services are present, and rejects any enabled provider flag without a ready provider.

**Step 2: Implement provider-aware validation and serialisation.**

Generate configuration that enables the implemented worker and Outlook paths. Replace blanket \`ValidateDisabled\` handling with validation that permits those operations and independently rejects an enabled runtime capability when its provider readiness contract is absent. Do not fabricate a provider or turn a future runtime flag on.

**Step 3: Run focused checks.**

\`\`\`powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NativeGoLiveOneShotAdmissionTests|FullyQualifiedName~NativeGoLiveLiveGateCompositionTests"
pwsh -NoProfile -File tests/native/native-go-live-contract.ps1
\`\`\`

Expected: operational services may be enabled; unprovisioned runtime providers remain disabled and fail validation if switched on without readiness.

**Step 4: Commit.**

\`\`\`powershell
git add src/FluxKnowledge.Integrations src/FluxKnowledge.Web/Configuration tests/FluxKnowledge.Integration.Tests tests/native/native-go-live-contract.ps1
git commit -m "feat: activate provisioned native operations"
\`\`\`

## Task 3: supply a native-compatible Codex hook implementation

**Files:**

- Modify: \`src/FluxKnowledge.Web/Program.cs\`
- Modify: \`src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs\`
- Add: \`src/FluxKnowledge.Web/Mcp/NativeCodexHookService.cs\`
- Add: \`src/FluxKnowledge.Web/Endpoints/NativeCodexHookEndpoints.cs\`
- Test: \`tests/FluxKnowledge.Web.Tests/Mcp/NativeCodexHookServiceTests.cs\`
- Test: \`tests/FluxKnowledge.Web.Tests/Endpoints/NativeCodexHookEndpointTests.cs\`
- Test: \`tests/FluxKnowledge.Integration.Tests/Codex/NativeCodexPluginMarketplaceTests.cs\`

**Step 1: Create failing hook-protocol tests.**

Use the legacy-hook tests as the compatibility contract. Cover the input/output envelope and the three events:

\`\`\`csharp
Assert.True(response.GetProperty("continue").GetBoolean());
Assert.Equal("UserPromptSubmit", response
    .GetProperty("hookSpecificOutput")
    .GetProperty("hookEventName").GetString());
\`\`\`

Test \`PreCompact\` returns continuation without mutation; \`Stop\` is idempotent for the same turn identity; invalid input and backend error still return \`continue: true\` plus a sanitised diagnostic. Assert generated plugin material contains a hook declaration and PowerShell adapter, and no \`python\`, \`flux_llm_kb\`, \`PostgreSQL\` or legacy executable reference.

**Step 2: Implement the loopback hook boundary.**

Add a local-only endpoint accepting \`POST /native/v1/codex/hooks/{eventName}\`. Map it in \`Program.cs\` next to other native endpoints. The service must call the existing native facade/store interfaces, use bounded sanitised text, and return Codex envelopes even on recoverable failure:

\`\`\`csharp
return Results.Json(new
{
    @continue = true,
    hookSpecificOutput = new { hookEventName = eventName, additionalContext },
});
\`\`\`

Extend the plugin writer to emit \`hooks/hooks.json\` and an \`invoke-native-hook.ps1\` adapter that reads stdin and invokes the loopback endpoint. The adapter must fail open (\`continue: true\`) on transport failure; it must not call the old Python module.

**Step 3: Run focused tests.**

\`\`\`powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NativeCodexHook"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NativeCodexPluginMarketplaceTests"
\`\`\`

Expected: all hook events are compatible and plugin output has only native loopback dependencies.

**Step 4: Commit.**

\`\`\`powershell
git add src/FluxKnowledge.Web src/FluxKnowledge.Integrations/Codex tests/FluxKnowledge.Web.Tests tests/FluxKnowledge.Integration.Tests/Codex
git commit -m "feat: add native Codex hook compatibility"
\`\`\`

## Task 4: make guarded release register native first and remove legacy second

**Files:**

- Modify: \`src/FluxKnowledge.Integrations/Windows/NativeGoLive/GuardedNativeGoLiveHost.cs\`
- Modify: \`src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveWindowsHostPorts.cs\`
- Modify: \`scripts/dev/update-native-windows.ps1\`
- Test: \`tests/FluxKnowledge.Integration.Tests/Codex/NativeCodexPluginRegistrarTests.cs\`
- Test: \`tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveOneShotAdmissionTests.cs\`
- Test: \`tests/native/complete-feature-dryrun.ps1\`

**Step 1: Write failing sequence tests.**

Capture the port call order. Require that plugin generation, native plugin registration, loopback MCP initialisation/tools-list and all hook probes occur before the exact legacy plugin uninstall. Require that failing any native proof prevents legacy removal. Require worker and Outlook scheduled tasks are enabled only after successful publication and app start.

**Step 2: implement narrow, idempotent ports and orchestration.**

Add native Windows/Codex ports for task enablement and exact plugin management. The legacy action must use the literal plugin identity \`flux-llm-kb@flux-llm-kb-local\`, never a broad plugin or directory delete. Retain the four existing clean-slate confirmations. Only accept the two authorised destructive targets:

\`\`\`csharp
if (!string.Equals(target.DatabaseName, "FluxKnowledge", StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected clean-slate catalogue.");
\`\`\`

Update the developer entrypoint so it invokes the guarded closeout path rather than a separate direct deployment sequence.

**Step 3: Run focused release-contract tests.**

\`\`\`powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NativeCodexPluginRegistrarTests|FullyQualifiedName~NativeGoLiveOneShotAdmissionTests"
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1
\`\`\`

Expected: exact destructive scope, native proof precedes legacy removal and no clean-slate action is reachable without confirmations.

**Step 4: Commit.**

\`\`\`powershell
git add src/FluxKnowledge.Integrations/Windows scripts/dev/update-native-windows.ps1 tests/FluxKnowledge.Integration.Tests tests/native/complete-feature-dryrun.ps1
git commit -m "feat: cut over Codex to the native plugin"
\`\`\`

## Task 5: release, observe and close out the accepted vertical slice

**Files:**

- Modify: \`docs/roadmap.md\`
- Use: \`scripts/dev/complete-feature.ps1\`

**Step 1: run the code-quality and full relevant verification matrix.**

\`\`\`powershell
dotnet build FluxKnowledge.slnx --no-restore --nologo
dotnet test FluxKnowledge.slnx --no-restore --nologo
git diff --check
\`\`\`

Expected: build/test exit successfully, zero warnings, and no whitespace errors. Fix the cause of any failure before release.

**Step 2: update the roadmap.**

Update only the affected native cutover/operations entries with delivered behaviour and explicitly name the deferred provider provisioning. Do not change dashboard manuals or screenshots.

**Step 3: perform the explicitly authorised guarded clean-slate closeout.**

Run the repository-required command from this \`codex/\` branch with the existing clean-slate confirmations. Preserve its complete JSON output and stop if it reports \`failed_step\`:

\`\`\`powershell
pwsh -NoProfile -File scripts/dev/complete-feature.ps1
\`\`\`

**Step 4: obtain fresh live evidence.**

Probe loopback health/readiness, \`GET /sources\`, \`GET /outlook\`, native MCP initialise plus tools-list, \`UserPromptSubmit\`/\`PreCompact\`/\`Stop\` hook calls, the active native plugin list, the removed legacy plugin identity and the enabled worker/Outlook scheduled tasks. Confirm configuration keeps all unprovisioned runtime flags false. Do not state live success without this command output.

**Step 5: final closeout.**

Commit the roadmap update, report live evidence and remaining intentionally deferred provider work. Retain the worktree and branch until the repository closeout has succeeded.
