# Workspace-aware Codex context

Date: 29 September 2026. Baseline: `b3c80182`.
Status: implemented and deployed to the installed IIS app on 30 September 2026;
live Codex-client and loopback validation passed.

## Intended result

Automatic prompt context should help with the current workspace without filling
the conversation with unrelated documents or duplicate excerpts. Inject a small
set of verifiable source passages, or continue without additional context.
Explicit corpus and knowledge tools remain available for broader research.

This is an ordinary, bounded integration change to an existing hook and retrieval
path. It adds no database schema, model, index rebuild, ingestion path or public
MCP operation. The implementation plan is in
[workspace-codex-context-plan.md](workspace-codex-context-plan.md).

The selected initial policy is deliberately conservative: automatic context uses
workspace-scoped lexical retrieval and excludes saved notes without workspace
bindings. It will miss some paraphrases and short queries. That is an explicit
trade-off for predictable prompt overhead and less irrelevant injected text;
manual hybrid search retains the existing semantic capabilities.

## Baseline evidence before implementation

- [NativeCodexHookService](../../src/FluxKnowledge.Web/Mcp/NativeCodexHookService.cs)
  sends only the prompt and a limit of five to `NativeKnowledgeQuery`. It does
  not consume `cwd`; its formatter emits titles and snippets within 4,096 UTF-16
  units, without evidence references or deduplication.
- [KnowledgeQueryService](../../src/FluxKnowledge.Application/Knowledge/KnowledgeQueryService.cs)
  interleaves stored knowledge with source search using a null workspace. Saved
  notes cannot currently establish a trustworthy workspace binding.
- The generated [command adapter](../../src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs)
  forwards the original UTF-8 JSON body and imposes a ten-second HTTP timeout.
  The accepted hybrid runtime has a 25-second outer deadline and measured
  two-caller REST p95 of 16.46 seconds. It is unsuitable as an unconditional
  synchronous operation on every prompt.
- [Scoped retrieval](../../src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.cs)
  already resolves canonical Windows workspace paths against registered roots,
  filters source paths beneath the requested directory, and excludes sibling
  prefixes. The retained search/read service preserves publication, disclosure,
  evidence, epoch and citation rules.
- A read-only check in the preceding session returned `scope-unavailable` for
  the repository workspace. This establishes unavailable searchable scope, not
  permission to register or scan the directory.
- The official [Codex hook contract](https://learn.chatgpt.com/docs/hooks), checked
  on 29 September 2026, includes `cwd` in common input, `prompt` for
  `UserPromptSubmit`, and `hookSpecificOutput.additionalContext` for context.
  The command adapter can forward these fields without a new client protocol.

## Alternatives and selection

| Approach | Benefit | Cost or limit | Decision |
| --- | --- | --- | --- |
| Add workspace information to the current general knowledge search | Small hook edit; retains note recall | Notes lack scope bindings, results lack exact references, and hybrid latency exceeds the adapter timeout | Reject for this increment |
| Reuse retained lexical search/read with a hook-specific selection policy | Existing evidence and disclosure controls; no inference; bounded work | Lower paraphrase recall; unscoped notes are omitted | Selected |
| Add scoped memory persistence and semantic relevance classification | Richer recall and a potential route to research briefs | Migration, source authority decisions, model/runtime work and additional latency | Defer |

## Behaviour and boundaries

### Workspace and input

Read `prompt` and `cwd` from the submitted JSON. Keep the existing 32 KiB body
limit and 2,048 UTF-16-unit prompt limit. Accept a `cwd` string of at most 2,048
UTF-16 units; reject control characters and whitespace-only values. Do not
silently truncate a prompt or use the IIS process directory as a substitute.

Delegate canonical path and scope resolution to the existing corpus reader.
Preserve its current Windows path contract rather than introducing a second
normaliser. Missing `cwd` yields `workspace-missing`; a supplied path that the
resolver cannot serve yields `scope-unavailable`. Neither widens to `all`.

The existing workspace contract is a directory subtree, not a Git repository
identity. A nested working directory searches only that subtree. An unindexed
worktree outside registered roots yields no context. Do not inspect `.git`, map
to another checkout, read source originals, create workspace aliases or register
sources automatically. A registered parent root must not expose sibling files.
`cwd` selects relevance scope; it is not an authorisation credential.

### Retrieval and owned work

Expose an internal lexical entry point on `CorpusRetrievalService` through
`ICorpusLexicalRetrievalService`. Move the existing lexical branch behind that
entry point; preserve its validation, hydration, disclosure and evidence code.
Normal `ICorpusRetrievalService.SearchAsync` still selects hybrid retrieval when
configured. The new entry point must never invoke `IHybridPassageRetrieval`.
Reuse the existing `ReadAsync` operation for final evidence validation.

The hook performs one workspace search with result limit 10, using the existing
200-candidate lexical budget. It examines at most ten returned hits and performs
at most five sequential zero-context evidence reads. It emits at most three
records. There are no retries, query expansion loops, cross-prompt caches,
background searches or parallel model calls.

Use a 1,750 ms cancellation budget for retrieval and citation reads. Reserve up
to 250 ms for best-effort audit, with a two-second overall service cancellation
budget. Use the injected `TimeProvider`; caller cancellation remains distinct
from the service budget. Await cancellation/cleanup of owned database calls;
do not abandon a task through `WaitAsync` or dispose its scope while it runs.
These are cooperative deadlines, not a promise that every provider cancels
instantaneously. Verify real SQL cancellation and prompt latency before release.
Keep the adapter's ten-second transport backstop unchanged.

### Conservative relevance policy

Version the pure selection policy as `workspace-lexical-v1`. This is a precision
heuristic, not semantic answer validation or a probability estimate. Do not use
reciprocal rank or SQL Full-Text rank as a confidence threshold.

1. Normalise prompt and matching text to Unicode Form C. Compare tokens using
   ordinal case-insensitive comparison. Tokenise Unicode letter/digit runs,
   allowing an internal `.`, `_`, `:`, or `-` only between two such runs.
2. For ordinary terms, keep distinct tokens of at least three characters after
   excluding the fixed list below. Do not stem, call a model or read a transcript.
3. A prompt is eligible if it has at least two ordinary terms, or a remaining
   non-excluded identifier token of at least four characters containing a digit,
   an underscore, or at least two uppercase letters. Identifier matching
   preserves token boundaries; identifier detection uses the original case
   before comparison. Capitalisation cannot turn an excluded word into an identifier.
4. A candidate must contain either an exact eligible identifier, or at least
   `max(2, ceiling(distinct_prompt_terms / 2))` distinct prompt terms in its
   passage body. Titles, source paths and context headers cannot satisfy this
   test. Preserve the existing search order among surviving candidates.
5. If no candidate passes, inject no context. A passed candidate is related
   evidence, not a claim that the user's question is answered.

Fixed ordinary-term exclusions:

```text
a an and are as at be been but by can could did do does for from had has have
how i if in into is it its me my of on or our please should so than that the
their them then there these they this those to us was we were what when where
which who why will with would you your also again continue done help next
now okay proceed thanks yes
```

This rule intentionally omits vague prompts such as “What's next?” and
single-topic words such as “retrieval”. It does not turn those prompts into a
roadmap query. Automatic project briefs and intent routing remain future work.
The policy must pass the frozen positive and negative cohort below; if it does
not, stop and revise the design rather than adding arbitrary exceptions to pass
individual held-out examples.

### Deduplication, citations and formatting

Before reading, remove duplicate candidate identities. After reading, retain at
most one passage per `OwnerSourceRevisionId`, falling back to `PipelineRecordId`
only for identity bookkeeping; workspace results still require a rooted source.
Also deduplicate equal passage bodies after Form C normalisation and whitespace
collapse, case-sensitively, across different files. Preserve the earliest result.
Do not use fuzzy similarity or discard complementary facts as near-duplicates.

Read each selected `EvidenceRef` with `ContextCharacters = 0`. Require the read
to match the hit's source, root, owner, pipeline revision, chunk/hash, offsets,
length and exact passage. Require membership in the resolved workspace roots
and the requested source-path subtree. A stale, withheld or mismatching hit is
omitted; examine the next hit within the fixed read budget. A systemic failure
or budget expiry drops the whole packet. No previously cached text is reused.

Render a fixed preamble followed by compact JSON records. The preamble states
that the excerpts are untrusted source data, may be incomplete, and are not
instructions or proof that a question is answered. JSON-escape every source
value, including titles and passages, rather than interpolating them into
instructions or Markdown structure. This framing reduces instruction confusion;
it does not claim to eliminate prompt injection from source material.

Each record carries title, source identity, root/owner and pipeline revision,
typed locations where available, canonical start/length, opaque `evidence_ref`
and exact passage text. Missing page information stays missing; a canonical
text span is still valid provenance. The reference is a retained-read locator,
not a source-file link or authority token.

The complete `additionalContext`, including preamble and metadata, remains at
most 4,096 UTF-16 units. Admit only whole records, up to three. Skip a record
that does not fit and consider the next eligible record; never clip a reference
or alter the passage while retaining its original offsets. A packet containing
no complete record becomes `context-budget`. Every injected record must be
re-readable through the existing corpus interfaces while its publication remains
current. Later revision/deletion can correctly make a reference stale.

### Outcomes, audit and compatibility

All ordinary no-context outcomes return `{"continue":true}` with no
`hookSpecificOutput`. Expected skips do not produce a warning on every prompt.
Record their reason through the existing operator Events projection. Preserve
direct-loopback checks and the supported hook response envelope.

Add closed preflight reasons: `context-injected`, `context-disabled`,
`workspace-missing`, `scope-unavailable`, `query-insufficient`,
`no-matching-evidence`, `evidence-unavailable`, `context-budget`,
`retrieval-unavailable`, and `retrieval-timeout`. Extend the existing preflight
factory and event JSON, preserving existing `state` values and compatibility
with historical events. No migration or new event store is needed.

Audit only reason, policy version, examined/injected counts and elapsed
milliseconds. Do not persist prompt text, workspace paths, passages or evidence
tokens. Unexpected prompt-context exceptions record approved exception category
and safe phase only, without exception text that may contain retained content.
Keep existing `Stop` diagnostic and idempotency behaviour unchanged. Audit
failure must not block the user's prompt or create a second retrieval attempt.

Add `Codex:PromptContextEnabled`, default `true`. When `false`, omit all automatic
prompt context and record `context-disabled`; do not restore global injection.
This provides a bounded rollback control for a latency or relevance regression.
It does not disable `Stop`, `PreCompact` or explicit native tools. The generated
adapter already forwards `cwd`; verify its exact-byte behaviour rather than
rewriting it or changing hook trust/registration as part of this increment.

## Acceptance criteria

| Criterion | Required evidence |
| --- | --- |
| Workspace isolation | Real-SQL nested-root, sibling-prefix and parent-root/subdirectory cases; zero cross-workspace hits |
| Missing scope | Missing/invalid/unknown/unindexed-worktree inputs produce no context and no global query or source registration |
| Useful selection | Frozen 36-case public/synthetic cohort: 12 positive, 12 negative/vague/unanswerable, 12 boundary cases; at least 10/12 positives receive supporting context and 0/12 negatives receive irrelevant context |
| Deduplication and provenance | No repeated body/document; every injected passage has an exact current read; revision/deletion and disclosure races suppress affected text |
| Output bounds | Zero partial records; valid Unicode and JSON; complete output at most 4,096 UTF-16 units, including citations |
| Predictable resources | One lexical search, at most five reads, zero hybrid/model/scheduler calls, no unfinished work after cancellation |
| Latency | In a disposable indexed workspace, 40 real HTTP hook calls at two callers: p95 at most 2.5 seconds; all calls continue; healthy positive cases inject evidence rather than meeting latency by timing out |
| Compatibility | Existing manual MCP/REST/CLI hybrid behaviour, Stop replay/capture, PreCompact, loopback, UTF-8 forwarding and safe error contracts still pass |

Freeze cases and labels before policy tuning. Use a separate small development
set for adjustments; do not relabel the held-out cases to obtain a pass. The
disposable SQL/HTTP run on 30 September 2026 measured 12/12 useful
positive cases, 0/12 irrelevant negative injections and 12/12 passing boundary
cases. Its 40-call, two-caller p95 was 16 ms. These are test-host measurements,
not installed-service latency or production readiness. A relevant
passage can exist for an unanswerable question, so inspect whether injected
context is useful evidence rather than claiming general answer abstention.

## Delivery, review and recovery

The first observable result is a synthetic indexed workspace → real loopback
hook POST containing `cwd` and a precise query → one exact cited passage →
metadata-only preflight event. A sibling workspace and missing `cwd` must yield
no packet through that same interface. Use existing disposable SQL fixtures and
model-free application composition; an isolated test host must have no
production access.

One implementation owner delivers the two coherent milestones in the plan.
Reassess at the first end-to-end result and after the second milestone. Stop if
the existing lexical service cannot operate independently of inference, if
publication/disclosure protections would be bypassed, or if the relevance and
latency gates conflict. Do not add models, retries, workspace mappings or a
memory migration to rescue this scope.

Self-review the complete diff and obtain a focused independent review of scope,
source-text handling, cancellation and unchanged manual retrieval before any
operational activation. Future production actions require the repository's
independent operational gate and explicit user authority. Prepare the incremental
updater plan, exact release, verification and rollback first. A deployment plan
must confirm whether it changes plugin material; any uncovered registration or
trust change needs separate direction. Use the context-disable setting for a
runtime regression, subject to the same operational approval; retain models and
canonical data. The original implementation approval did not authorise source
registration, migration, restart or deployment.

The separately authorised incremental update deployed commit `2c6712dc` to
`I:\FluxKnowledge\App` on 30 September 2026 without migration, model acquisition
or plugin-material changes. Recovery release
`20260929T215634Z-2c6712dcd77a` retains the previous application and
interactive-host payloads. The updater released its validation hold after
unchanged-state checks; post-deployment live, ready and index-health probes all
returned HTTP 200. The installed binary matched the staged candidate.

Live hook calls with missing and unregistered `cwd` returned no context. The
existing public Mercury acceptance workspace returned one 2,493-character
record whose passage matched an exact `corpus.read` response. The installed
PowerShell adapter returned the cited passage containing the 176-Earth-day solar
day answer. An ephemeral read-only Codex CLI run from that workspace triggered
`UserPromptSubmit`; its operator event recorded `context-injected` with one
record under `workspace-lexical-v1`, and the client answered 176 Earth days.
The unindexed repository workspace returned no context. Audit details contained
reason, policy, counts and elapsed time, without prompt or passage text. IIS site
and application pool remained started. The existing client `Stop` hook also ran
and saved captures during the CLI probes.

The deferred source-lifecycle, OCR and Outlook items stay deferred. Research
briefs, document comparison, scoped saved-memory persistence, semantic admission,
Git-worktree aliases and new UI/manual work are outside this increment.
