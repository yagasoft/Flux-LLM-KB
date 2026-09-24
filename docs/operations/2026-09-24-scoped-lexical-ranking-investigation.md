# Scoped lexical passage ranking investigation

Date: 2026-09-24. Code baseline: `23c7078075fcce4dca6300b1c21ef02726ad2005`.

## Decision

Keep the deployed lexical passage selection unchanged. Two small, format-neutral
window-selection prototypes improved the aggregate development score but both
regressed a previously correct DOCX answer passage. This fails the
[predeclared no-regression gate](../design/lexical-passage-ranking-plan.md).
Neither prototype was implemented in the application, run on the held-out set,
deployed or used to enable semantic retrieval.

## Data and separation

The existing pilot's 11/24 answer-present passage Recall@5 and three OCR
extraction failures were used only to diagnose failure stages. In that pilot,
three misses had the right top-five chunk but the wrong excerpt, and seven had
the right document but the wrong top-five chunk. These categories use the
original gold spans and do not assert that every different passage is an
incorrect alternative answer.

Before changing any ranking code, a fresh local roster was formed with 14
distinct source families and 28 source-first questions in each of development
and held-out. Each cohort has eight native-PDF questions and four each for
plain text, DOCX, XLSX, Visio and OCR-derived images. The cohorts and the pilot
share no source originals or known variants. Source and canonical hashes,
answer spans in UTF-16 offsets, extraction status, query order and scoring
rules were frozen outside Git before the development comparisons. All 56
selected answers are present in one allowed canonical chunk. An independent
source-only audit then found five held-out labels needing clearer source
wording, visible Visio text or a narrower span. They were corrected **before
any held-out ranking run**, with the original manifest retained and both
versions hashed. The revised held-out manifest SHA-256 is
`ea91ab9f900e94b58f6d7a696ef8359beb1a463c9ebefbfb53ec13730a9c8fd4`
(prior SHA-256:
`037e741bafebb027a4e078c289d91be3d4571dc496d0d56a8ff644aeb9fe4316`).
No held-out ranking result was opened. The implementation owner drafted the
questions and could see them; the independent audit verified sources and spans
but was not a fully blinded question-authoring handoff. A future production
acceptance claim should therefore use a separately authored, document-disjoint
held-out cohort under the plan's blind handoff rule. This corrected cohort
remains useful for source-audited diagnostics, not as a spent test set.

The application extractors produced the retained PDF, Office and Visio text
used for evaluation. Saved OCR output was normalised with the application's
block-order and table rules; original public OCR images were checked visually.
The local PDF probe used the extractor's test licence-registration callback;
it did not exercise the deployed licence configuration.
The search baseline ran through `CorpusRetrievalService` and SQL Full-Text in
a generated disposable database, including zero-context `read` equality.
Source identities in that database were evaluation aliases. The test seeded
canonical publications directly, so it does not measure ingestion reliability
or every production lifecycle/provenance path.

Extraction screening was kept separate. Of 45 locally available spreadsheet
candidates probed, 17 produced complete structural text and 28 were refused by
the application extractor. One candidate PDF required OCR and was replaced
before labels were frozen. These are extraction-stage observations, not
retrieval misses or improvements. The earlier pilot's three OCR extraction
failures remain in its own denominator.

## Development results

Passage scores use the actual baseline's top-five bounded passages. The two
candidate scores are offline excerpt simulations over those same ordered hits
and frozen canonical chunks; they are **not** .NET service, latency or
disclosure-validation results. Both candidates keep exact-query slices and
document ordering fixed. The second rule additionally requires two distinct
direct query words of at least four characters, avoiding short function words;
it is the final development variant under the plan's two-candidate limit.

| File type | Questions | Baseline passage R@5 / MRR@5 | Each prototype R@5 / MRR@5 | Document R@5 / R@10, unchanged |
| --- | ---: | ---: | ---: | ---: |
| Native PDF | 8 | 5 / 0.625 | 8 / 0.917 | 8 / 8 |
| Plain text | 4 | 2 / 0.333 | 2 / 0.333 | 3 / 3 |
| DOCX | 4 | 3 / 0.750 | 2 / 0.500 | 4 / 4 |
| XLSX | 4 | 4 / 1.000 | 4 / 1.000 | 4 / 4 |
| Visio | 4 | 3 / 0.750 | 4 / 1.000 | 4 / 4 |
| OCR-derived image | 4 | 3 / 0.750 | 4 / 1.000 | 4 / 4 |
| **All** | **28** | **20 / 0.691** | **24 / 0.810** | **27 / 27** |

Both prototypes gained the same three PDF questions, one Visio question and
one OCR question (development IDs d02, d04, d08, d22 and d26), while losing
one DOCX question (d16). The loss occurs where
important query concepts are inflected or paraphrased in the source: direct
word coverage favours a later cluster, leaving the correct answer outside the
1,024-unit passage. This is a general limitation of the proposed rule, not a
file-type-specific exception to tune away. Two other development questions
still returned the correct chunk with the wrong excerpt.

The baseline exact-query result is 13/14. The remaining exact candidate was
first in SQL order and present in the retained text, but the existing
surrounding-text disclosure guard refused it. This is a disclosure refusal,
not a candidate-ranking or extraction failure, and no guard was weakened.
The all-question denominator above retains it. The baseline's single query
latencies were recorded privately, but no repeated or concurrent candidate
latency test was run because the relevance gate had already failed.

## Consequence

No ranking, schema, index, migration, search/read contract, model, deployment
or live-service change follows from this investigation. A future lexical
approach would need a principled way to group SQL Full-Text expansions by
original query concept and a fresh development decision before spending the
sealed held-out set. The result here does not claim that the new cohort is
directly comparable with the original pilot.
