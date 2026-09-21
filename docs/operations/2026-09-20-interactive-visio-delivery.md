# Interactive Visio delivery, 20 September 2026

## Scope

Installed Microsoft Visio 16.0.20228.20190 (VisioPro2021Retail) is the actual
VSDX interpreter. The on-demand `documents run-visio` command runs in the
logged-in Windows session. It is not an IIS/service processor and is not
registered with the background document worker. No new runtime, package, model,
OCR/PDF feature, schema migration or source scan is part of this increment.

The command requires an exact retained original revision, SHA-256 and the
`phase-6-vsdx-retained-visio-v2` fingerprint. It creates one successor input and
one document result, reuses normalisation/indexing/embedding/publication, and
preserves the original source identity. Internal ZIP parts never become corpus
entries. Existing terminal branches and the last good publication are retained.

## Safety and operational contract

- A whole-command, handle-relative exclusive lock is held through recovery,
  ownership, extraction and cleanup. Existing Visio sessions are refused.
- Visio runs on a dedicated STA. Its window is mapped to a Windows process;
  the mapped new VISIO process, session, handle and creation time establish
  ownership before assigning the kill-on-close Job Object and opening input.
- Input is a held private retained copy, opened read-only with macros/events
  disabled and refresh declined. Active embedded objects, macros, external
  relationships and data connections are refused before activation.
- Existing ZIP security validation and strict semantic-part UTF-8 checks remain.
  No alternative encoding is guessed. Traversal/output/runtime limits apply.
- Exact durable dispatch/job leases and transactional source eligibility fence
  publication. An expired interactive job still prevents deletion until an
  exact recovery can establish that the prior command/Visio process is gone.
- Cleanup failure never becomes a successful or terminal extraction. Shared
  retained-file preservation and original-owner publication use existing stores.
- Deployment uses only the unchanged incremental updater. It publishes the Web
  payload, not the CLI. The desktop command is run from the reviewed Release CLI;
  after closeout that command is available from the main checkout's Release build.

## Verification record

Status: implementation, deployment and scoped live verification passed. Earlier
failed attempts and their exact corrections remain below as diagnostic history.

Focused SQL/worker/publication regression run: 16 passed, zero failed/skipped.
The new tests first exposed generic claim eligibility, dispatch ownership,
expired completion and deletion fencing failures. These were corrected without
changing existing terminal ownership. Additional actual-COM/CLI tests are opt-in;
an ordinary suite run without `FLUX_KB_RUN_ACTUAL_VISIO=1` is not COM evidence.

Fresh final checks:

```powershell
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'
$env:FLUX_KB_RUN_ACTUAL_VISIO='1'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~VisioActualComExtractorTests|FullyQualifiedName~VisioScopedPipelineIntegrationTests' --logger 'console;verbosity=minimal'
Remove-Item Env:\FLUX_KB_RUN_ACTUAL_VISIO
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~VisioDocumentPreflightTests|FullyQualifiedName~VisioDocumentProvenanceTests' --logger 'console;verbosity=minimal'
git diff --check
```

Release build: zero warnings/errors. Final full suite: Domain 698, Integration 1,243,
Web 290 and OutlookHost 72 passed (2,303 total); 17 existing browser-only skips,
zero failures. Actual-Visio/SQL suite: 16/16 passed with opt-in enabled, including
the final live-process cancellation observer. Preflight/provenance: 6/6 passed.

The generated public diagram contains two ordered pages and nine shapes. Literal
expectations independently authored through Visio's supported object model check
Unicode text, grouped child text, master-derived text and its local override,
an expanded `12345` field, shape data `42`, and both ends/direction of one connector.
Each expected passage occurs once; repeated extraction is identical. A third
invocation is cancelled after observing a live Visio process; no result is accepted
and no live Visio remains. The real CLI path also runs against disposable SQL and
retained bytes, preserving the old terminal branch and producing one Extract
artifact/one queued Normalise job. Replay creates neither another result nor job.
An expired paused job cannot be recovered through a held competing command lock;
after release, exact recovery settles it without creating an artifact.

The first actual extraction attempt refused before opening content because it
incorrectly treated Visio's `ProcessID` as a Windows PID. A bounded retry did
not solve the mismatch and left an automation process requiring cleanup. These
failed attempts are retained as diagnostics, not reported as passing extraction.
The task-created orphan was independently identified by its exact handle, creation
time, executable and interactive session, then terminated and its exit confirmed.
Further actual runs exposed a writer/read-share conflict, incorrect late-bound
cell properties/tuple typing, and already-terminated processes briefly remaining
in Windows enumeration. The fixes retain no-follow held reads and require positive
exit evidence; they do not ignore unknown/live processes. Malformed relationship
XML also received a failing test before correction: it now returns the stable
`visio-document-container-invalid` reason instead of escaping as raw parser details.

Microsoft explicitly documents that
[Visio ProcessID is not the Windows PID](https://learn.microsoft.com/en-us/office/vba/api/visio.invisibleapp.processid).
The independently reviewed correction uses
[WindowHandle32](https://learn.microsoft.com/en-us/office/vba/api/visio.invisibleapp.windowhandle32)
and [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid),
with all new-process/current-session/held-handle checks retained. Text comes from
[Characters.Text](https://learn.microsoft.com/en-us/office/vba/api/visio.characters.text),
which expands fields and returns a group's own text; children are traversed separately.

## Earlier deployment and canary history

The updater's reviewed `-PlanOnly` result selects `I:\FluxKnowledge\App`, no
migrations, no clean slate, preservation of Config/Data/Runtime/Recovery/CodexPlugin,
held loopback probes and automatic payload rollback on deployment failure.
The independently approved `-Apply` ran at commit `3c508b6` and exited 1:
candidate readiness returned HTTP 503, and the automatic rollback readiness
probe also returned HTTP 503. The previous application payload was restored;
the failed candidate remains in incremental recovery storage. At that point the
IIS site and pool were Started, live and index-health returned HTTP 200, and ready
returned HTTP 503 with `OperatorActionRequired / ConfigurationInvalid`. The
deployment validation hold was absent. No manual restart, configuration change
or readiness bypass occurred during that failed attempt.

Read-only SQL inspection found an unretired, empty-path generation draft whose
declared vector count is one but which has no vectors, membership or matching
Embed artifact. The recovery store rejects this missing durable provenance as
an invalid operation. This is a concrete readiness blocker, not evidence of a
failed Visio extraction. The database still has an active generation; the health
projection's null generation does not mean that its SQL pointer was lost.
The source-deletion path selected generations only through vector membership,
so a source-owned Embed draft with no vector was not retired before its artifact
was deleted. A focused disposable-SQL regression failed on the retained orphan,
then passed after deletion also captured the exact generation IDs named by that
source's Embed artifacts inside the existing serializable transaction. The full
deletion/recovery group passed 59/59; the Release build remained warning-free,
the full suite passed 2,294 tests with 17 existing browser-only skips, and the
actual Visio/SQL/recovery proof passed 7/7. Independent review approved the
focused fix and an exact guarded one-row retirement. The separately authorised
production repair was then applied to that exact orphan and changed only its
retirement timestamp.

The only authorised private canary was the existing exact retained VSDX revision.
Before the successful final run, no root-wide rescan, source recreation or
watch-input write occurred. Post-attempt digests of unrelated revisions, branches, records,
artifacts, chunks, stable vector fields and source settings match their baseline.
The original file hash is unchanged, its predecessor remains blocked and it has
no new publication. The first exact canary command refused before Visio activation
with `document-reprocess-not-eligible`, creating no successor or publication. Its
predecessor structural branch was blocked with a finished same-generation
`office-document-container-invalid` attempt, while the older activity row remained
Pending. A focused regression failed on that exact combination, then passed after
eligibility accepted only this attempt-proven terminal shape. The predecessor was
not mutated and no Visio process remained.

Before deployment, the read-only original hash matched its retained revision;
the old structural branch remained blocked and no document publication existed.
Redacted unrelated revision/branch/record/artifact/root-configuration digests
were captured for post-canary comparison. GPU and OCR flags were already enabled
and are not changed by this work. No models were acquired or loaded.

## Final deployed outcome

The unchanged incremental updater was run again after each demonstrated blocker
was corrected. The final reviewed plan targeted `I:\FluxKnowledge\App`, applied
no migration or clean slate, preserved Config/Data/Runtime/Recovery/CodexPlugin,
and retained its validation hold, held loopback probes and automatic payload
rollback. Apply succeeded for commit `1444394d0e722a51fbb9d1491206f7a9e29fdba4`;
`/health/live`, `/health/ready` and `/api/index-health` then returned HTTP 200.

The first v2 canary refusal created no branch or publication. It demonstrated
that the proposed v2 descriptor had reused the immutable v1 capability ID. The
correction assigns v2 its own capability ID and retains the v1 registration
unchanged. A focused test failed before that change and passed afterwards.

The exact authorised retained VSDX then completed through installed Visio:
17 ordered pages, 3,210 shapes, 978 connector endpoints and 78,511 extracted
characters. The Visio traversal completed in 47,290 ms and left no VISIO process.
Normalise, canonical index, embed and publish all completed. One publication
selects the original document identity with 17-page/3,210-block Visio provenance;
two of three automatically selected native-text phrase searches returned that
identity. The missed phrase is retained as a retrieval limitation rather than
reported as a perfect result. No internal package member became an independent
source or corpus entry.

An exact replay returned `visio-already-extracted` with the same branch and
pipeline-record IDs and created no duplicate. The original file hash was unchanged.
Pre/post digests for unrelated revisions, branches, records, artefacts, chunks,
vectors and root configuration all matched. The terminal structural branch and
failed v1 Visio job remain immutable; the published v2 successor is additive.
No root-wide scan, source recreation, watch-input write, model operation or OCR/PDF
change occurred.
