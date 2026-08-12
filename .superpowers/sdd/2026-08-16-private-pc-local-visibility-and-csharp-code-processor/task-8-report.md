# Task 8 validation report — private-PC local visibility and retained C#

Base: `bc2650e`.

## Executed evidence

- `scripts/dev/ensure-disposable-sql.ps1` validated the loopback-only SQL Server and returned a server-level connection string. Generated-catalogue tests then passed Domain 73/73, Integration 136/136 and Web 33/33, all with zero skips.
- The Integration set included local disclosure, retained-detail/source-original deletion, retained C# replay, lifecycle correction, reader and read-only CLI tests. It covers the exact C# planner/replan, parser/classifier, secret boundaries, blocked no-document diagnostic receipts, persisted attempt ownership, migrations/upgrades, receipt-first replay and two-connection recovery/fencing.
- `ensure-disposable-browser.ps1` validated the pre-existing cached Chromium executable with a local version probe. `test-browser.ps1` set the browser flag only in its child process and ran `RetainedBranchDetailBrowserTests`: 4/4 passed, 0 skipped, with generated SQL.
- The direct-loopback REST/native-MCP/CLI/composition suite passed 33/33; it covers remote and forwarded/proxy rejection, anonymous native MCP authority and no mutation tools.
- The affected Python local-detail, local-diagnostic, REST, CLI and MCP set was invoked unfiltered. It failed one existing runner-only stdio MCP test because the Windows subprocess helper cannot call `stderr.fileno()` under this harness. It was not counted as passed. The same set with only that case deselected passed its other 142 collected cases.
- Release build with `--warnaserror` completed with zero warnings/errors; EF reported no pending model changes; 15 package lock files parsed; `git diff --check`, the branch package-lock diff check and clean-working-tree check passed. Static Outlook and no-deep-model-probe guards passed.

## Final-review correction evidence

- RED: seven native and ten Python synthetic PEM-envelope/credential-URI cases failed before the scanners recognised the general forms. The production-registration preflight test did not compile before an instance preflight backed by a DI registration probe existed. Two activation assertions returned the old batch size of 16 instead of eight; the first configuration-boundary test attempt also failed to compile because it targeted a test-only helper and was replaced with tests through the actual production composition boundary.
- GREEN: full Domain passed **499/499**, including the production DI-registration preflight, default batch and hosted activation cap. The focused generated-database/native integration set passed **118/118**, skipped **0**, including native disclosure, retained detail, C# reader/CLI and lifecycle/replay. Focused REST/native-MCP/composition and native local/public projection tests passed **39/39**, skipped **0**.
- GREEN: the Python local disclosure, local/public projection and local diagnostic set passed all **34/34** collected cases. The child-scoped cached-Chromium retained-detail/browser set passed **4/4**, skipped **0**. Release `--warnaserror` again completed with zero warnings/errors, EF again reported no pending model change, the Gmail preservation guard passed against `33a4412`, and `git diff --check` passed.
- One correction-verification command failed before test collection because it named a non-existent guessed Python test file; the corrected explicit three-file set above passed. A post-GREEN production-factory binding edit then initially failed to compile because its required disclosure namespace import was missing; the import was added and the same focused Domain set passed 46/46. Neither failed invocation is counted as a test pass.
- The correction recognises standard RSA, EC, OpenSSH, encrypted and PGP private-key envelopes plus credential-bearing URIs in both scanners; exercises native and Python local/public withholding without using real credentials; and inspects the completed production service collection for forbidden Roslyn workspace/analyser/generator registrations. Its reduction of the shared retained-processor batch from the approved 16 to eight was rejected by the next independent review and is superseded by the concurrency-separation correction below.

## Final C# store-claim cap correction

- RED: `Csharp_claim_store_caps_direct_callers_at_the_automatic_replay_limit` seeded nine current eligible retained C# branches and called `ClaimCsharpCodeAsync` with 16. It failed as required: expected eight claims, actual nine.
- GREEN at that checkpoint: `SqlRetainedProcessorBranchStore.ClaimCsharpCodeAsync` clamped its direct caller input to the then-shared maximum. The next review correctly rejected that coupling because the shared maximum belongs to ZIP/TAR/OOXML and generic replay. The separate C# maximum and refreshed evidence are recorded below.
- Refreshed proportionate evidence: Domain **499/499**, focused Web direct-loopback/MCP/composition **39/39**, and child-scoped cached-Chromium browser **4/4**, all with zero skips. `FluxKnowledge.slnx` Release `--warnaserror` built with zero warnings/errors; EF reported no pending model changes; `git diff --check` and the Gmail guard against `c7663cb` passed.
- An initial build invocation named the nonexistent `FluxKnowledge.sln` and failed before a build began; the corrected `FluxKnowledge.slnx` invocation above is the recorded successful build evidence. The earlier unfiltered Python `stderr.fileno()` harness failure remains recorded below and was not rerun or relabelled by this C#-only correction.

## Shared and C# concurrency separation correction

- RED: production composition rejected configured `AutomaticReplayBatchSize=16` with `RetainedProcessors:AutomaticReplayBatchSize must be between 1 and 8`. The focused Domain run failed three behaviour assertions: the shared default was eight instead of 16, hosted ZIP requested eight instead of 16, and hosted OOXML force claims requested eight instead of the shared 16. A first parallel Domain/Web invocation also hit a shared build-output file lock before Domain tests ran; that infrastructure collision is not counted as behavioural evidence. The serial RED runs failed only the intended assertions.
- GREEN: `RetainedProcessorOptions.MaximumAutomaticReplayBatchSize` and its default/configuration ceiling are restored to 16. ZIP and TAR hosted activation pass 16 to ordinary claims; OOXML passes 16 to force claims and, after six synthetic force claims, ten to ordinary claims. `RetainedCsharpCodeProcessor.MaximumClaimBatchSize` is the separate fixed C# ceiling and is used by both hosted C# promotion/claim and `SqlRetainedProcessorBranchStore.ClaimCsharpCodeAsync`.
- Generated-SQL GREEN: a direct C# call requesting 16 from nine eligible branches claimed exactly eight, while a generic ZIP call claimed all 16 eligible branches. The refreshed focused native/generated-SQL matrix passed **136/136**, skipped **0**. Full Domain passed **502/502**, focused Web direct-loopback/MCP/composition passed **40/40**, and child-scoped cached-Chromium browser passed **4/4**, all with zero skips.
- `FluxKnowledge.slnx` Release `--warnaserror` built with zero warnings/errors, EF reported no pending model changes, `git diff --check` passed and the legacy Gmail preservation guard passed against `ca2d84c`. The earlier unfiltered Python `stderr.fileno()` harness failure remains non-green and was not relabelled.

## Non-green and unrun

- Failed: `tests/test_mcp_server.py::test_stdio_mcp_session_survives_backend_reset_during_brief` under the Windows test harness, at `stderr.fileno()`. The failure is outside product assertions; retained native MCP behavior is covered by the 33-test direct-loopback suite.
- Unrun: no live site, deployment, production migration, Outlook activation/profile operation, source-original reread, cloud/network parser, or model/runtime download/activation was attempted.

## Pending gate

Task 8 is awaiting the fresh independent whole-slice reviewer. This report does not claim final approval.
