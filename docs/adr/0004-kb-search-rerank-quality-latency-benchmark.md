# ADR 0004: KB Search Rerank Quality-Latency Benchmark

## Status

Accepted for the next implementation after the July 2026 hot patch reference.

## Context

The rollback reference in ADR 0003 proved that bounding the rerank pool and
using one-passage Qwen AWQ microbatches fixed the severe `/api/search`
latency issue. The remaining question was whether a better quality/latency
trade-off existed without disabling Qwen or shrinking the candidate window too
aggressively.

## Decision

Keep Qwen reranking enabled, keep the 12-candidate small-result rerank pool,
keep the default passage cap at 1536 tokens, and change the default
`retrieval.rerank_microbatch_size` from 1 to 2.

Do not replace the current local/global fallback flow with the experimental
single-final-rerank flow yet. Do not raise the default pool to 20, 40, or 80
without new quality evidence. Do not lower the default passage cap to 384 or
768 tokens from this benchmark alone.

## Evidence

The metadata-only benchmark harness ran the standard synthetic retrieval suite
at limits 1 and 5 plus the live `llm-kb` latency reproducer. It did not mutate
runtime settings.

Standard suite results:

- `vespa_no_rerank`: 17/17 at limits 1 and 5, but this is not enough evidence
  to remove Qwen because the synthetic cases include many exact-token matches.
- `pool12_mb1_tok1536`: 17/17 at limits 1 and 5.
- `pool20_mb1_tok1536`, `pool40_mb1_tok1536`, and `pool80_mb1_tok1536`: 17/17
  at limits 1 and 5, with no quality gain over pool 12 and higher rerank
  volume.
- `pool20_mb1_tok768` and `pool20_mb1_tok384`: 17/17, but the synthetic
  passages were short enough that this does not prove safety for long evidence.
- `pool20_mb2_tok1536`: 17/17, faster than microbatch 1 on synthetic cases.
- `pool20_mb4_tok1536`: 17/17 on synthetic cases, but failed the live latency
  probe badly.
- `single_final_pool20_mb1_tok1536`: 14/17 at limit 1 and 14/17 at limit 5,
  missing `code-symbol`, `code-exact-definition`, and `code-cross-root`.

Live reproducer highlights:

- `pool12_mb1_tok1536`: matched the pool80 top result in `9.411 s`.
- `pool12_mb2_tok1536`: matched the pool80 top result in `5.579 s`.
- `pool12_mb1_tok384`: matched the pool80 top result in `7.739 s`, but this
  single live query does not justify lowering the default passage cap.
- `pool20_mb1_tok1536`: matched the pool80 top result in `16.227 s`.
- `pool40_mb1_tok1536`: matched the pool80 top result in `37.879 s`.
- `pool80_mb1_tok1536`: oracle comparison run took `47.226 s`.
- `pool20_mb4_tok1536`: took `451.117 s` and produced a maximum rerank lease
  of `461.700 s`.

After hot-patching the selected microbatch-2 default and restarting only the API
container, the live `/api/search` reproducer completed in `6.628 s`. The
validation window created 2 released embedding leases and 12 released rerank
leases; the maximum rerank lease duration was `0.805 s`, and no leases remained
`running` or `waiting`.

## Consequences

- Microbatch 2 is the best tested default: it preserves Qwen quality, reduces
  request count versus microbatch 1, and avoids the long-lease behaviour seen at
  microbatch 4 and 8.
- Pool 12 remains the safest latency default from current evidence. Pool 20+
  did not improve benchmark quality and materially increased live latency.
- The single-final-rerank idea needs design work before production use because
  the harness version regressed code-aware cases.
- The ADR 0003 tag remains the fallback if later representative live queries
  show microbatch 2 is unsafe.
