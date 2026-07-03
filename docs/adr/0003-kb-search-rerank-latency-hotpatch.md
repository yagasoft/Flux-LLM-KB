# ADR 0003: KB Search Rerank Latency Hot Patch Reference

## Status

Accepted as the rollback reference for the July 2026 KB Search latency work.

## Context

`/api/search` with `query="GPU scheduler resident model eviction"`,
`limit=1`, `root_name="llm-kb"`, and `scope_mode="local_first"` previously
took about 280 seconds. Isolated model-runner timings showed that a single
resident one-passage Qwen AWQ rerank call was fast enough, so the problem was
not simply "AWQ is slow".

Stage-level validation showed the search path could run local plus global
fallback and send dozens of hydrated candidates to rerank. A first live patch
bounded the pool to 12 candidates per scope, but one 8-passage AWQ microbatch
still held a rerank lease for 694.606 seconds. The final hot patch kept Qwen
reranking and the 12-candidate quality window, then changed the default
rerank microbatch size to one passage per model-runner call.

## Decision

Keep this implementation as the known-good fallback reference:

- Preserve `local_first` and global fallback behaviour.
- Pass the user-facing result limit into Vespa reranking as `rerank_limit`.
- For small-result searches, rerank a conservative pool of
  `min(retrieval.rerank_top_n, max(limit * 4, 12))` hydrated candidates per
  scope rather than all hydrated candidates.
- Keep Qwen as the final reranker.
- Default `retrieval.rerank_microbatch_size` to `1`, with a runtime setting and
  `FLUX_KB_RETRIEVAL_RERANK_MICROBATCH_SIZE` override available.
- Expose non-sensitive diagnostics for rerank input count, microbatch size,
  microbatch count, and passage character/word summaries.

The local git tag for this rollback point is:

`kb-search-rerank-hotpatch-reference-20260703`

## Validation Evidence

After hot-patching live and restarting the API/model-runner only when an active
lease remained:

- `/api/explain` for the reproducer completed in `30,967 ms`.
- Explain diagnostics showed local rerank `input_count=12`,
  `microbatch_size=1`, `microbatch_count=12`, `latency_ms=10,840`.
- Explain diagnostics showed global fallback rerank `input_count=12`,
  `microbatch_size=1`, `microbatch_count=12`, `latency_ms=4,159`.
- `/api/search` for the same reproducer completed in `10,370 ms` with one
  `global_fallback` result.
- The `/api/search` validation window created 2 released embedding leases and
  24 released rerank leases; the maximum rerank lease duration was `1.269 s`.
- A final scheduler query returned no `running` or `waiting` leases.

## Consequences

- This reference improves live latency without disabling Qwen reranking.
- The batching change should not reduce relevance because it changes request
  granularity, not candidate scoring.
- The rerank-pool cap can affect quality if the expected result is below the
  bounded pool before rerank. Future quality benchmarking should compare pool
  sizes 12, 20, 40, and 80 before replacing this fallback.
- If later experiments regress quality or latency, restore this reference by
  creating a new branch from the tag or cherry-picking the tagged commit.
  Avoid destructive reset commands unless explicitly approved.
