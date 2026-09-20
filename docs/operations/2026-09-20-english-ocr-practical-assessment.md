# English OCR practical assessment

Status: prepared from the source images and public annotations before inspecting the retained PaddleOCR-VL output. This is the bounded practical acceptance check approved for the private knowledge base; it does not replace the retained literal-CER diagnostics or claim that their former thresholds passed.

## Scope

The fixed candidate is PaddleOCR-VL 1.6 on `gpu:0`, revision
`c5630abae1d940eafe0697512a0325494b02ab42`, with the retained local layout
and page-orientation companions. The assessment is English-only. Its inputs
are public, local evaluation material outside Git; no private document text,
model payload, or prediction is committed here.

The checklist deliberately uses existing retained predictions where available.
Expected passages, identifiers and table relationships were taken from the
rendered originals or their source annotations, and were not supplied to the
provider.

## Fixed retrieval checks

| Input | Coverage | Useful retrieval question | Independently checked expectation |
| --- | --- | --- | --- |
| `magazine-8071` | Genuine low-quality four-column magazine scan | What does the article say fathers bring to raising children? | The title and the statement that fathers bring a unique presence and special strength are available together. |
| `magazine-8087` | Genuine multi-column magazine scan | Which hotel was open in Dubrovnik, and what quantity of roof tiles was delivered? | `Argentina` is the hotel and `250,000` is the tile quantity. |
| `phototest-left` | Rotated English page | What sentence demonstrates the rotated-page text? | The `12 point text` sentence and the quick-brown-dog sentence are both retrievable in normal reading order. |
| `en-01` | Clean high-resolution sans page | What reference, amount and review date does screening record 01 contain? | `REF-202601`, `123.45`, and `2026-09-01` are present in context. |
| `en-09` | Blurred low-contrast monospace two-column page | What is the reference amount and Column A code? | `REF-202609`, `151.05`, and `A-09-2026` are present; left-column text precedes the right-column review text. |
| `PMC4003957_018_00` | Structured public exercise-plan table | What date/time and exercise intensity are stated for swimming? | `2013/08/17`, `2:30 pm`, and the `Swimming`/`Moderate` row relationship are preserved. |
| `PMC5332562_005_00` | Dense public statistical table | What RMSE is associated with the rural-income RS row? | The rural/income/RS relationship yields `76.527`, not a number from another row. |

The final acceptance decision also requires the integrated path to preserve the
original document identity and page provenance, and to surface refusal rather
than invent text where a page cannot be read. Minor punctuation and layout
differences are acceptable only when they do not change the answer,
number/identifier, or retrieval result.

## Retained-output result

The retained GPU outputs passed all ten concrete checks above: seven passage or
identifier checks, two table-relationship/order checks, and the degraded
two-column ordering check. Every checked identifier/number occurred once,
except the generic word `income`, which correctly occurs in several table rows;
the checked rural/income/RS/`76.527` relationship occurred in the intended
context. No checked answer was duplicated or moved into a meaning-changing
order.

This is a practical retrieval pass, not a claim of perfect transcription. The
retained literal-CER/exact-match diagnostics still contain failures and remain
visible as such. The provider does not expose a block-confidence value. A
provider failure is refused rather than indexed; successful OCR text remains
marked as OCR with page/block provenance, so a reader can distinguish it from
native text and inspect the original page when necessary.

## Integrated PDF boundary check

The fresh opt-in local tests used the supplied Syncfusion licence and the
verified local GPU bundle. A truly blank PDF page remained outside OCR with the
`pdf-no-extractable-text` disposition. A mixed PDF containing native text on
page 1 and a rotated public scan on page 2 retained the native page, selected
only page 2 for OCR, and extracted the independently checked rotated English
phrases. The fixed worker ran successfully on `gpu:0`; neither test used a
network source, downloader, provider cache fallback, or a model outside
`J:\Models`.

## Scoped live result

The deployed IIS identity processed a single image-only PDF made from the public
rotated Tesseract image. The complete GPU handoff, durable result, normalisation,
indexing, embedding and publication path succeeded. All three independently
checked phrases (`12 point text`, `quick brown dog`, `lazy fox`) were present in
the retained text and returned useful search snippets under the original PDF
identity. Page 1 retained OCR provenance, a 270-degree orientation correction
and two blocks. Handoff creation to continuation took 15,430 ms; this is not a
measurement of GPU inference alone.

The stricter repetition diagnostic **failed**: four identical copies of the
dog/fox sentence in the original became one copy in the prediction. The original
expected counts and failed assertion remain unchanged. No unique checked
passage, identifier, value or table relationship was lost in this fixture, so
independent review accepted this as non-consequential for the revised practical
retrieval gate. This does not establish exact transcription, general absence of
omissions, or the former CER threshold. The live source was then removed through
the app; its pipeline, OCR/GPU and search data were verified absent.

An existing native-text PDF separately retained its original identity, all 14
page records and checked page-specific identifiers. Useful search retrieved it;
not every query's leading snippet contained the desired phrase. Page provenance
is verified in retained artefact metadata, not exposed in the current public
search DTO. Mixed native/scanned pages were tested locally; same-page mixtures
of native text and scanned text remain outside region-level OCR coverage.

See [delivery evidence](2026-09-20-english-ocr-live-delivery.md) for commands,
deployment, isolation and operational limitations.

## Non-goals

This is not Arabic OCR, a new model comparison, a new quality framework, an
exact-text/CER pass claim, or a replacement for live validation. The retained
exact-match and CER reports remain diagnostics, including their recorded
failures.
