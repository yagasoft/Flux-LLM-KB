# English GPU OCR delivery: 20 September 2026

## Outcome and limits

The frozen English PaddleOCR-VL 1.6 PDF route is integrated, enabled and deployed.
Actual IIS-owned GPU extraction reached normalisation, indexing, embedding and
publication; useful live search returned the original document. This is a
bounded practical knowledge-base delivery, not an exact-transcription guarantee.

- Seven representative public inputs passed ten independently prepared retrieval,
  identifier, critical-number, table-relationship and ordering checks. A mixed
  native/scanned PDF passed the local integration check.
- The live rotated scan passed three distinct phrase/search checks. Its stricter
  repetition assertion failed: four identical sentences became one. No unique
  checked fact was lost. Independent review accepted the revised practical gate;
  the failed assertion and former CER/exact-match reports remain unchanged.
- Same-page mixtures of native and scanned text are not region-OCRed. The route
  selects pages without meaningful native text.
- Page/block provenance is retained in artefact metadata; current public search
  responses do not include those page fields. OCR is labelled, not assigned an
  invented confidence score.
- Terminal failed OCR revisions have no supported same-revision retry. Reprocess
  remains idempotent and does not reset old branch state.
- Interactive Visio processing is not implemented or live-verified by this
  increment. Arabic, new models/runtimes and unrelated providers are excluded.

See the [practical assessment](2026-09-20-english-ocr-practical-assessment.md).
The following evidence records the checked release, not a broad benchmark claim.

## Fixed implementation

PaddleOCR-VL revision `c5630abae1d940eafe0697512a0325494b02ab42`,
layout `7b48a7566925fa464281f930c58eee04fe2c862a` and orientation
`7330ab7039123e46af2dc03154b9969aa412c61d` are unchanged. GPU device is
`gpu:0`, orientation runs on CPU with the frozen 0.80 threshold, batch size is
one, and the retained adaptive provider-order setting is unchanged.

The model gate verifies the actual consumed local files and keeps verified
read-only handles. Python receives explicit local paths and offline/network
guards. No model transfer, model-copy/adoption, provider fallback or acquisition
occurred during this delivery.

The existing GPU scheduler owns dispatch and capacity. A durable exclusive
acknowledgement precedes inference; source-bound results continue through the
existing pipeline. Publication checks current source state transactionally.
Pause/deletion, cancellation, result replay and uncertain capacity retain their
existing ownership fences. Internal document parts never become independent
published documents.

## Fresh verification

Commands ran from the dedicated `codex/document-processors-plan` worktree.
The opt-in local GPU tests additionally used process-local
`FLUX_KB_RUN_LOCAL_PADDLE_OCR=1` and the configured licence-file variable;
licence contents were never printed or committed.

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode --ignore-failed-sources
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'

dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~PaddleOcrVlmLocalExecutorTests.Fixed_local' --logger 'console;verbosity=minimal'
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~PdfDocumentProcessingTests' --logger 'console;verbosity=minimal'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SourceDeletionIntegrationTests|FullyQualifiedName~NativeCorpusActionMatrixTests|FullyQualifiedName~LocalModelStoreTests|FullyQualifiedName~PaddleOcrVlmModelGateTests' --logger 'console;verbosity=minimal'
git diff --check
```

| Check | Observed result |
| --- | --- |
| Locked restore | Passed |
| Release warnings-as-errors build | 0 warnings, 0 errors |
| Full suite | 2,270 passed, 17 browser-only skips, 0 failed |
| Actual local GPU/PDF integration | 3/3 passed, 27 seconds |
| Whitespace-only PDF regression | Failed first; PDF focused suite 11/11 after correction |
| Actual read-only Windows ACL regression | Failed first with access denied; model-store suite 37/37 after correction |
| Public source-delete local-OCR admission | Matching case failed first; missing/cross-source/other-runtime remained refused |
| Combined deletion/action/model-gate checks | 105/105 passed |
| Handoff/publication/deletion/migration checks | 28/28 passed |
| Exact migration execution | Disposable up/down/up and injected pre-commit rollback passed |

The earlier failed full-suite output and live failures remain evidence, not
silently relabelled passes. The final full-suite log is
`E:\Temp\flux-ocr-final-suite-livefixes-20260920.log`.

## Migration and deployment

The additive `20260920122758_AddDocumentOcrRequestsAndArtifactMetadata`
migration was applied separately. The initial generated SQL shared a batch
between adding a column and adding its check constraint; SQL Server rejected it.
Read-back proved no schema/history change. A reviewed execution copy inserted
only a `GO` boundary after the column addition, retaining the same connection,
transaction and operations. Its SHA-256 is
`0EB396383947B2A8483825498639C0914069432C90EA294282398360320D0185`.
The exact execution copy passed disposable rollback/up/down/up before live use.
Old-code application rollback retains the additive schema; no production down
migration was performed.

```powershell
pwsh -NoProfile -File .superpowers/sdd/2026-09-19-visio-ocr-delivery/live-ops.ps1 -Mode Migrate
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot '<feature-worktree>' -PlanOnly
# Reviewed plan, current user authority and independent pre-deployment approval:
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot '<feature-worktree>' -Apply
```

The generic updater was not changed. Final deployment output:

```text
ok: true
mode: applied
commit: c5f7c554464d5a8e56c87c3bb57ed2c7e91fe622
migrations: false
clean_slate: false
deployment_validation_hold: released-after-unchanged-state-validation
readiness_remediation: null
```

Full output: `E:\Temp\flux-ocr-deployment-remediation-20260920.log`.
Recovery payload:
`I:\FluxKnowledge\Recovery\IncrementalUpdates\20260920T183820Z-c5f7c554464d\previous`.
Configuration/ACL recovery:
`I:\FluxKnowledge\Recovery\OcrEnablement-20260920`.
Model/GPU/OCR flags are enabled together; unrelated ASR/FFmpeg/network parsing
remain disabled. Only the IIS pool received the licence-path environment value
and required existing Python read/execute access. Model payload directories deny
that identity write/delete; receipt/scratch writes remain permitted.
No manual IIS restart, full GoLive, bootstrap, broad source replay or model
acquisition occurred. The updater emitted its existing PowerShell
WebAdministration compatibility warning, not a compiler warning.

## Scoped live evidence

One existing native-text PDF was reprocessed by its exact retained revision,
input hash and processor fingerprint using `documents reprocess`. All five
pipeline stages completed with one original-owner publication and 14 page
records. Checked page-specific identifiers remained correct. Search retrieved
the original, including one verified expected-passage snippet; two other query
hits did not contain the requested phrase in their leading snippet. Original
input bytes were unchanged; no watch-folder file was modified.

The separately approved temporary source held one image-only PDF made from a
public rotated Tesseract image, outside the watch folder. The first attempt
failed closed on model-directory access without publication or acquisition.
After the read-only-access fix, the synthetic PDF title alone was changed to
create a normal new revision, because no supported same-revision retry exists.
Page drawing bytes and rendered image hash were unchanged. This is not evidence
that terminal OCR retry is implemented.

The new revision completed with `document-ocr-complete`; all five stages and
one publication succeeded. The retained result digest was
`3640EF1F00478573665BB4BE53A177583AE8E2E8E35F8040F7639007170FBB7B`.
Page 1 recorded method OCR, orientation 270 and two blocks.
Handoff creation to continuation took 15,430 ms, not pure inference time.
Searches for `12 point text`, `quick brown dog` and `lazy fox` each returned
the original PDF with the expected phrase (ranks 8, 1 and 1 respectively).
Repeating the exact reprocess returned the same branch, replay=true and
`document-reprocess-not-claimed`, without creating another execution.

Deletion through the public app preview/commit interface completed and removed
the temporary root, both pipelines, revisions, scan requests, jobs, artefacts,
OCR requests, GPU tasks and publication. No orphan chunks/vectors or temporary
document search hits remained. The original synthetic input file was preserved.
The deletion tombstone/audit remains intentionally available.

Unrelated source rows matched their pre-deployment fingerprints exactly:

| Scope | Rows | SHA-256 |
| --- | ---: | --- |
| Other revisions | 15 | `B7ECF86200B162AE5C341FB179A353C1646E7BAE87B702042A28E7B2CF13B41A` |
| Other activities | 21 | `9D4360DA7D32C19873C496509F3E0AB718AC4FABDF946A978C9086E2F8FE3C04` |

The existing native PDF publication remained present. Final live/ready/index
health probes returned HTTP 200; GPU status was active=0, available=1,
reserved=0 and uncertain=0, with no Python child remaining.
Redacted durable-result evidence is retained locally at
`E:\Temp\flux-ocr-live-redacted-evidence-20260920.xml`; no private text enters Git.

## Closeout and retained safeguards

The approved closeout command is the repository script, without `-GoLive`:

```powershell
pwsh -NoProfile -File scripts/dev/complete-feature.ps1 -FeatureWorktree '<feature-worktree>' -MainRoot 'E:\LLM KB' -CommitMessage 'Complete offline English GPU OCR delivery'
```

It must verify, squash-merge, test merged main, commit, push and only then remove
the completed task branch/worktree. The final task response records the observed
script result; this record does not pre-claim its success.

No archive security defence was relaxed: encryption, invalid directories,
unsafe/absolute/drive/backslash/traversal paths, link/reparse risks, compressed,
entry, total-expanded, count, nesting and ratio bounds remain. Extracted/indexed
text remains strictly UTF-8. No cache fallback, private fixture publication,
licence disclosure, terminal ownership mutation or unrelated replay is permitted.

TDD and verification-before-completion guidance supplied failing regression
checks and evidence-led completion; PDF guidance supplied fixture rendering
checks; branch-finishing guidance is applied through the mandatory repository
closeout, not a substitute merge sequence.
