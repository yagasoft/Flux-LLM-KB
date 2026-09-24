# Scoped lexical passage ranking investigation plan

Date: 2026-09-24. Baseline: main `23c7078075fcce4dca6300b1c21ef02726ad2005`.

**Status: investigation concluded with no ranking change.** The fresh
development and held-out questions were initially frozen before evaluating
two small development prototypes. Both failed the no-regression gate; the
held-out ranking results remain unopened. A source-only independent audit
corrected five held-out labels before any held-out run. Since the implementation
owner drafted and could see those questions, a future production acceptance
claim still needs a separately authored blinded cohort. See the
[investigation record](../operations/2026-09-24-scoped-lexical-ranking-investigation.md).
The implementation and deployment steps below remain a conditional plan for a
future candidate, not a record of work performed.

## Objective and boundaries

Improve the chance that scoped lexical search returns the answer passage in its
first five hits, if evidence supports a small general correction. The existing
pilot's 11/24 answer-present result is diagnostic evidence, not an acceptance
set for a fix. Its three OCR extraction failures remain separately reported.
Successful operational delivery of search/read is already established.

Retain the [corpus retrieval contract](corpus-retrieval.md), SQL Full-Text,
ordinal exact-match priority, scope and lifecycle filtering, evidence identity,
UTF-16 offsets, disclosure rules, provenance and MCP/REST/CLI behaviour. Keep
the 200-candidate budget, 512-term budget, 1,024-unit passage bound and maximum
two passages per logical document. Do not increase result length to inflate
span recall. No source-format-specific ranking boosts, query dictionaries,
document/path/title exceptions, semantic scoring, model acquisition or model
activation. No extraction changes, re-OCR, canonical rechunking, SQL schema or
Full-Text-index migration in this increment.

The first observable result is a paired baseline/candidate search-and-read
comparison through the existing service over a frozen disposable SQL corpus.
A production change is conditional on independent held-out benefit and all
correctness, performance, review and operational gates. A no-change finding is
a valid result; operationally working search is not a reason to change ranking.

## Current implementation and hypotheses

- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.cs`
  applies eligibility and scope before `TOP (200)`, orders ordinal full-query
  matches first, then SQL Full-Text rank and chunk ID. It retrieves chunks of
  at most 2,048 UTF-16 units. Full-Text expansion is obtained separately through
  `GetLexicalTermsAsync` using the installed English SQL word breaker.
- `src/FluxKnowledge.Application/Search/CorpusRetrievalService.cs` consumes that
  order and allows two hits per logical document. For a non-exact hit,
  `FindLexicalAnchor` chooses the earliest boundary-verified expanded term. It
  does not currently choose the passage with the strongest multi-term evidence.
  The selected slice is then revalidated and mapped to citation metadata.
- `src/FluxKnowledge.Application/Ports/ICorpusRetrievalReader.cs` carries chunk
  content, identity and Full-Text rank. Its term list is flat; it does not prove
  that two expansions represent two independent query concepts.

These facts suggest candidate-order and excerpt-selection hypotheses. They do
not establish the cause of any particular PDF miss. The saved pilot has partial
gold labels, so first audit whether a supposed miss is an equivalent correct
answer rather than assume every different span is wrong. Preserve original
scores and add any such diagnostic annotation separately.

## 1. Establish the diagnostic and evaluation data

After the user resumes, inspect the existing private pilot and the actual SQL
candidate path without changing ranking. Keep private material beneath the
existing `E:\Codex Workspaces\corpus-retrieval-evaluation` assessment root,
outside Git; create a separate lexical assessment directory. Do not overwrite
the original manifests, scores or extraction judgements.

For each original question, trace the first point where its answer is lost:

| Category | Evidence to capture | Implication |
| --- | --- | --- |
| Extraction omission or changed meaning | Source expectation versus retained canonical text | Upstream failure; never a ranking improvement or a retrieval denominator silently removed. |
| Retained but not representable in one allowed passage | Gold offsets versus chunk boundaries and passage limit | Record as a contract limitation and retain it in answer-present reporting. Do not change chunking or citation contracts here. |
| Missing candidate | Gold chunk absent from the scoped 200 candidates; record lexical match and budget warnings | Local reranking cannot recover it. Do not widen the budget or introduce synonyms merely for that question. |
| Wrong chunk order or document cap | Gold chunk present, but displaced before the final top five or two-per-document selection | Consider a bounded non-exact candidate-order correction only if the same cause recurs across documents. |
| Wrong excerpt within a selected chunk | Candidate contains the gold span, returned slice does not | Consider passage-window selection while retaining the SQL candidate order. |
| Correct eligibility/disclosure refusal or stale evidence | Record the rejection stage without exposing withheld content | Preserve the refusal; investigate a wrong expectation separately. |
| Evaluation ambiguity | Another source-supported answer, duplicate passage or uncertain gold | Diagnose explicitly; never edit old labels to manufacture a gain. |

Record candidate rank, Full-Text rank, verified term positions, exact-match
tier, selected excerpt offsets and the rejection/diversity step privately.
Use test-only diagnostics or a private probe; do not expose source text or
internal traces in a new public API. Audit UTF-16 slicing and denominator
handling before relying on the evaluator.

### Fresh development and held-out cohorts

Create both cohorts before changing ranking. The original questions are used
only for diagnosis and synthetic regression-test design. They are not counted
as development or held-out gains. Prefer existing retained text and source
provenance; the task does not require a new OCR benchmark or live indexing.

Use the following bounded target **for each** fresh cohort:

| Source stratum | Distinct document families | Questions |
| --- | ---: | ---: |
| Native PDF | 4 | 8 |
| Ordinary plain text | 2 | 4 |
| DOCX | 2 | 4 |
| XLSX | 2 | 4 |
| Visio | 2 | 4 |
| OCR-derived retained text | 2 | 4 |
| Total | 14 | 28 |

Within each stratum, pair a literal/identifier/value question with a natural
multi-term question per document. Labels must come from meaningful source
content, not phrases selected because a candidate scorer favours them. Include
late passages, competing sections, repeated headers/contents lists and realistic
in-scope distractors where the sampled documents naturally contain them. Record
OCR image, scanned PDF and mixed-PDF provenance separately; do not relabel an
image as a PDF or claim unsupported format coverage. This lexical cohort is
distinct from the semantic model-selection gate.

Split by document family before question writing: source originals, revisions,
near-duplicate exports, archive copies and sibling format renditions belong to
one family. Exclude every original pilot family from both fresh cohorts. Keep the
new development and held-out source pools, including their distractors,
disjoint. Verify source/canonical hashes and manually check obvious related
versions; filenames alone are not a leakage check.

Use a bounded independent labelling assignment with only the sampled sources
and retained text. The labeller must not receive rankings, candidate scores,
ranking code or preferred hypotheses. Record each question's scope, source
family, source/canonical/chunk hashes, extraction status, source location and
all independently identified acceptable canonical answer spans. Multi-span
answers must specify the required spans before scoring. Verify the labels
against the originals and canonical UTF-16 slices before freezing; ambiguous
labels are resolved or excluded with a recorded reason before any ranking is
viewed. Do not exclude extraction failures to achieve a favourable denominator.

Freeze dated, hashed manifests for source membership, labels, distractors,
query order, scope, software/SQL settings and scoring rules. The implementation
owner receives development data and only held-out coverage counts and hashes;
held-out query text, labels and outputs stay out of development diagnostics.
Keep this separation explicit in the evaluation runner and handoff. Freeze the
whole source-to-result denominator as well as the answer-present subset.

If sufficient independent sources or trustworthy labels are unavailable, state
the specific data gap and stop the acceptance experiment. Do not silently lower
the quota, reuse versions of pilot documents or substitute synthetic content as
empirical held-out evidence. Synthetic fixtures remain useful for correctness.

**Checkpoint:** after this diagnostic/data batch, require a reproducible failure
mechanism on at least two independent development document families. Otherwise
record no justified ranking change and stop implementation.

## 2. Evaluate at most two small development candidates

Start with the least invasive correction supported by the diagnostic counts:

1. **Excerpt selection only:** keep SQL candidate order and exact-query slicing;
   for non-exact hits choose a bounded window around the strongest verified
   lexical evidence instead of the first term. Use term boundaries and the
   existing case/accent/inflection semantics. Repeating one word must not beat
   evidence for several independent query terms. Tie-break deterministically
   and retain the existing fallback for single-term or uninformative queries.
2. **Non-exact candidate reranking, only if needed:** within the same scoped
   200 candidates, rank evidence in the proposed passage by a small,
   lexicographic rule such as independent query-term coverage and proximity,
   with SQL rank and stable chunk identity as tie-breaks. Keep every ordinal
   full-query match ahead of non-exact matches. Apply the unchanged document
   cap after ranking. Do not change SQL eligibility, candidate generation or
   diversify by adding file-type weights.

Choose the exact rule only after diagnosis, and write down why it addresses the
general mechanism before evaluating it. If independent term grouping is needed,
verify the installed SQL parser's metadata before extending its internal result;
counting every inflection as a separate query concept is not acceptable. Prefer
one change over a combined ranker/window rewrite. No coefficient grid search,
extra stopword dictionary, cascading variants or per-example exceptions.

Compare each candidate with the unchanged baseline on the same new development
snapshot. Select at most one, based on the predeclared gates below; prefer the
smaller diff on a tie. Preserve failed candidates' scores. The old pilot may
explain behaviour but cannot supply validation credit. If neither candidate
generalises across development documents, discard the ranking change. Reassess
after this second batch rather than adding another heuristic.

## 3. Scoring, guardrails and the one held-out check

Score the actual returned top five bounded passages, not an internal candidate
pool, an expanded `read` context or a whole-document hit. A passage succeeds
only if the right source/revision and all required canonical answer spans are
covered. Score document recall separately. Keep correct alternative spans as
frozen in the labels; do not add alternatives after seeing a result.

Report, by file type and query form, paired passage Recall@5, passage MRR@5
(zero beyond rank five), and relevant-document Recall@5 and @10. Show counts,
denominators and a query-level gain/loss ledger. Report extraction omissions,
changed meaning, unsupported spans, refusals and candidate-budget exhaustion
separately, with the all-source denominator retained. Small cohorts support a
bounded decision, not claims of general statistical superiority.

Freeze these acceptance rules before development scores are inspected:

- At least two additional answer-present questions succeed in top five on each
  fresh cohort, with gains on at least two document families; aggregate passage
  MRR@5 must not decrease. Native-PDF passage Recall@5 must improve to claim a
  correction of the pilot's principal PDF failure.
- No file-type stratum loses passage Recall@5, passage MRR@5 or document
  Recall@5/@10. Retain every baseline-successful designated exact identifier or
  value query in top five. List non-exact individual regressions even where a
  class's net score rises; they are not hidden by aggregate gains.
- Zero scope/lifecycle leakage, invalid evidence references, wrong provenance,
  UTF-16 slicing errors, altered disclosure boundaries or search/read mismatch.
  Existing long-exact-query bounded behaviour remains unchanged.
- Warm end-to-end p95 at concurrency one and four remains below two seconds,
  rises by no more than 20% and by no more than 50 ms relative to the matched
  baseline. Use paired/interleaved baseline/candidate runs, a fixed shuffled
  query order, completed Full-Text population and at least five timed repeats
  per question after warm-up. Record cold latency separately. Also measure the
  200-candidate/512-term bound with public synthetic content; do not add
  unbounded scans, occurrence lists or per-candidate SQL round-trips.

Finish the relevant correctness checks and development latency checks first.
Lock the chosen code revision, evaluator revision, parameters, manifests and
acceptance rules before opening the held-out results. Run baseline and that
single chosen candidate once as a paired quality evaluation over the untouched
held-out snapshot. Predetermined latency repetitions are part of that one run,
not opportunities to select a favourable result. Do not inspect held-out ranks
between variants or alter labels, formulae or thresholds afterwards.

If the held-out gate fails, revert/discard the candidate through ordinary
targeted Git changes, preserve the evidence and report no deployable gain.
Do not tune against the exposed set and call it held-out again. A genuinely
invalid run requires an explicit documented cause; if results were exposed,
a corrected quality decision needs fresh independent data.

## 4. Implementation and verification map

The intended change is local to `CorpusRetrievalService.cs`. Extract one pure
passage-selection helper only if needed to isolate its meaningful tests; do
not refactor neighbouring retrieval code. Change `SqlCorpusRetrievalReader.cs`
and `ICorpusRetrievalReader.cs` only if a demonstrated term-grouping requirement
cannot be met by the current data. The evidence codec, citation mapper, public
contracts and persistence schema should remain unchanged.

Keep a model-free, opt-in evaluation runner and synthetic evaluator tests in
`probes/lexical-passage-evaluation/`; accept private manifests by explicit path
and emit private results to the assessment directory. Reuse the generated
disposable SQL database safety pattern from `NativeSqlServerFixture` and the
real service/HTTP composition in `CorpusRetrievalEndToEndTests`. Verify corpus
hashes and Full-Text readiness. Do not seed private fixtures in Git, mutate
production data, log private connection strings or simulate SQL tokenisation
with an unrelated text-search engine. Ordinary and full-suite tests use
synthetic fixtures only; closeout must not automatically consume or rerun the
private held-out experiment.

Use test-first changes for the chosen behaviour, with public synthetic fixtures
derived from the mechanism rather than copied from pilot questions:

- Early weak term or repeated heading versus later multi-term evidence; a
  single repeated word versus distinct terms; equal-evidence stable ties.
- Exact identifiers with punctuation, numbers, long exact queries, irregular
  inflections, short words, case/accent variants and substring false matches.
- Late-chunk matches, supplementary Unicode characters and chunk-edge spans;
  returned `passage == canonical UTF-16 slice` and zero-context `read` equality.
- Stronger out-of-scope distractors; root/workspace/sibling boundaries; the
  two-per-document cap and candidate/term-budget warnings.
- Deleted/replaced/suppressed publication, owner identity and independent
  archive members; disclosure markers outside the visible passage; evidence
  codec and source/page/Visio provenance invariants.

Extend `tests/FluxKnowledge.Integration.Tests/Search/ScopedCorpusRetrievalTests.cs`
and `tests/FluxKnowledge.Web.Tests/Endpoints/CorpusRetrievalEndToEndTests.cs`.
Put pure-helper tests, if applicable, beside the existing domain search tests.
Use actual SQL Full-Text integration for word-breaking claims. Retain the
existing MCP/REST/CLI smoke checks and do not add a production diagnostic API.

After coherent changes, run the affected search tests first; at the complete
branch gate run locked restore, zero-warning Release build, the full native
suite, repository contracts and required endpoint/transport checks. A skipped
required SQL test is not a pass. Reuse unchanged verification evidence rather
than repeating experiments after documentation-only edits.

## 5. Review, delivery and rollback

Obtain independent review of the complete chosen diff, dataset separation,
metrics/gain-loss ledger, invariant tests and remaining limitations before
feature closeout. Use `scripts/dev/complete-feature.ps1` for closeout and report
its `failed_step`/`log_path` if it fails. Keep the dedicated task worktree.

Only after a passing held-out result, prepare the incremental
IIS updater's `-PlanOnly` output, exact payload, test evidence and prior-payload
rollback. Obtain the required independent Astra predeployment assessment; reuse
existing user deployment authority where the action/target/scope still match.
No Full-Text migration is needed or to be repeated for this ranking-only fix.

After authorised deployment, verify health plus scoped search/read over REST,
MCP and CLI, exact identifiers, scope boundaries, evidence validity, latency and
explicit lexical mode. Live smoke validation verifies deployment; it is not a
second held-out tuning round. The updater restores prior payloads automatically
on deployment failure. For a regression detected after successful deployment,
prepare a targeted ranking revert and redeploy it through the same reviewed
incremental plan/apply path; there is no assumed standalone rollback switch.
SQL canonical data,
the existing Full-Text index and evidence keys must remain intact; references
created by either payload should still resolve for unchanged eligible text.

Publish only aggregate results and methodology in an operations note; update
the roadmap with measured delivery and remaining gaps. If evidence does not
justify a fix, report that plainly and retain the current ranking. Update no
dashboard manuals or model-related artefacts as part of this work.
