# Repository live validation

Date: 30 September 2026. Status: dispatch-correction deployment verified; the existing source is enabled and publication is advancing. Complete repository retrieval/brief/freshness acceptance remains pending.

The routine incremental IIS update deployed `597bf0bea4812c885bd6cf40d81cc438af490e3e`. The reviewed and actual release candidates and installed application/interactive-host payloads matched across all 272 files. The updater completed held readiness and unchanged retained/pipeline-state checks before releasing its owned validation hold. Subsequent live, readiness and index probes returned HTTP 200 with Healthy index state. No schema migration, index rebuild or model acquisition occurred; recovery payloads are retained.

The existing Git-tracked source was resumed through native preview/commit with a retained idempotency command. The next complete scan discovered 1,030 current files, including the two added corrective tests, without changing the root's discovery policy or maintaining filenames. Its 900-second cadence, source-text opt-in, empty optional patterns and existing 16 MiB per-file protection remain configured.

At 17:11 UTC, 880 of 881 C# branches were complete. Text publication was still processing. Four rejected embedding attempts belonged to older document revisions suppressed by the resumed scan; they cannot publish those superseded revisions. Complete current publication counts, cited code/document retrieval, brief quality, scope and live documentation freshness remain unverified. This record will be completed from observed outcomes rather than from scan counts alone.

Subsequent investigation found repeated host-stop events from an oversized C# reference display in `SqlRetainedTextRegistrationStore.cs`. The processor omitted the existing 4,096-unit bound that SQL correctly enforces. Supported native pause completed at root configuration revision 4; subsequent live/readiness/index probes were HTTP 200 with Healthy index state. Retention and recovery remain preserved. The processor-side check passed 44 domain and 85 integration tests with no skips, a zero-warning build and independent technical review; local verification of the actual file returned the expected blocked outcome. Full closeout and exact-release operational review precede another corrective deployment. GPU completions alone are insufficient evidence that publication is advancing.

This newly tracked operational record provides a genuine addition for the automatic-discovery check after normal Git closeout. No additional source or discovery-policy edit is required. See the [coverage acceptance](2026-09-30-repository-workspace-coverage-acceptance.md) for the local invariant tests, transport restrictions and deferred scope.

The subsequent incremental release `8a8f578f6810852490dd3b74e526a19a1f6edde4` passed exact operational review and all 272 candidate/installed hash and length checks. Its owned hold was released after unchanged-state validation; live/readiness/index probes returned HTTP 200 with Healthy index state. Native resume completed at configuration revision 5. Two complete scans discovered 1,031 current paths, including this new record without a filename list or root-policy change. C# processing completed 880 branches and normally blocked the known oversized-reference file; no further host-stop event was observed during this validation window.

At 18:29 UTC, 33 current text records were published, including C#, PowerShell, project/build files and Markdown, with no terminal failure on a current revision. Further read-only evidence proved dispatch delay behind GPU-owned jobs while 80 ready publication deliveries remained unleased. A local correction applies the existing exact worker-claim rules before dispatch selection, retaining authoritative claim and race checks. Focused SQL checks passed 27/27 and compatibility checks passed 121/121, without skips; independent technical review found no blocking findings. Full closeout, incremental release and complete cited retrieval/freshness acceptance remain pending; GPU completions alone still do not establish publication coverage.

The first dispatch-correction closeout stopped at an existing OCR citation-replacement fixture: both alternatives contained “phrase”, so SQL Full-Text legitimately returned the selected metadata for the unpublished successor query once asynchronous indexing caught up. The fixture now uses disjoint terms and additionally checks the exact successor record and owner; visibility and stale-evidence assertions remain. No production retrieval change is needed.

The dispatch correction subsequently passed all 21 required closeout steps;
both checkouts passed 2,796 tests with zero failures and the same 20 opt-in skips.
Builds had zero warnings/errors. Independent technical and exact-release
operational review approved their gates. The authorised incremental updater
installed `3b92f17b96de1dc53f646b9bd1b10ae035bc7be3` in release
`20260930T193133Z-3b92f17b96de`; all 272 reviewed, actual candidate and installed
files matched by hash and length. Held unchanged-state validation succeeded,
the owned hold was released, and fresh health/readiness/index probes returned
HTTP 200 with Healthy index state. The outer wrapper propagated the updater's
accepted Robocopy exit code 1 despite its successful JSON result; installed
payload and live probes independently confirmed success, without a replay.

Native resume completed at configuration revision 7 without changing the
Git discovery mode, empty optional patterns, source-text opt-in, 900-second
cadence or 16 MiB per-file protection. At 20:14 UTC, all 1,032 current paths
were distinct, 595 were text-published and none had a current terminal failure.
The 882 C# outcomes comprise 881 completions and the single normal existing
reference-limit refusal. Publication is observed directly, rather than inferred
from GPU completions. All 272 payload files, recovery data and model-store
boundaries remain accounted for.

Native MCP, REST and CLI reads preserved the same exact documentation citation
binding. Main/nested scopes resolved to the repository; sibling-prefix and
unregistered-worktree searches refused scope. Native source-text reads succeeded
for SQL, project props, Razor and CSS; PowerShell's existing disclosure checks
withheld a larger context while accepting its safe cited passage with zero extra
context. No disclosure protection was relaxed. The first genuine fresh CLI
brief used three searches and four successful reads, supported its factual
claims and reported incomplete evidence. Complete counts, the other two briefs,
code-body queries and genuine tracked-document replacement remain pending.

The documentation closeout's main test run exposed a wall-clock race in the
two-request GPU measurement fixture: slower SQL setup could consume its normal
two-second queue deadline before dispatch. A deliberate 2.1-second setup delay
reproduced the same empty-dispatch assertion; using the fixture's existing
manual clock passed with that delay. The final test uses the same manual clock
for scheduler and executor, checks both handoffs and preserves every trace,
span, batch, native-measurement and capacity assertion. The probe delay is
removed. Production deadlines and the separate expiry tests are unchanged;
this test-only correction requires no application redeployment.
