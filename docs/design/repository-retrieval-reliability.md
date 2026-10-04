# Repository retrieval reliability

Date: 3 October 2026. Baseline: main `687f1707`; installed stability release
`1b5bd5f6`. Updated 4 October 2026: release `a248811a` was integrated through the
required closeout script and deployed with the additive proof migration and
automatic backfill. Live acceptance exposed scoped SQL latency, a whole-file
disclosure false positive and 43 terminal Embed checkpoints from an earlier
source withdrawal. Correction release `4f9393ef` completed mandatory integration
and incremental deployment; current exact method and documentation reads pass
through MCP/REST/CLI. After the parallel page-plan timeout, scoped-only serial
correction `404a38b5` was integrated and incrementally deployed. All 40 frozen
semantic searches passed before recovery at two callers with p95 16.2 seconds, including exact
current citations and reads. The separately approved 32-job recovery has now
published every original record, preserving 329 saved vectors and embedding only
the 2,188 missing positions. All 32 sources passed exact current cited readback.
Post-recovery semantic acceptance remains open: one timeout in the first frozen
run and index-update fallback throughout its single complete repeat failed the gate.
A later normal
IIS idle shutdown exposed an unattended-discovery hosting gap; its scoped correction
and live verification remain pending. This design follows
the [repository coverage design](repository-workspace-coverage.md)
and has a separate [implementation plan](repository-retrieval-reliability-plan.md).

## Outcome and scope

Make the configured repository useful through cited code reads and semantic
search across the whole eligible repository. Preserve the single Git-discovered
source, automatic watcher/rescan reconciliation, C# symbols/references, code
formats, cited documentation, private/untracked/build/model exclusions and
content-safety checks. No individual file lists or repository partitioning.

This increment addresses two demonstrated retrieval failures and completes their
acceptance. It does not reopen the completed publication recovery, acquire models,
change ranking/model profiles, enlarge transport/parser limits or update dashboard
manuals. Terminal Embed recovery requires separate explicit operational authority;
installing its action does not authorise processing jobs. AGENTS requires no change. Reconciliation of the retained
older branch, OCR, Outlook and wider source-lifecycle acceptance remain side notes.

## Evidence and remaining uncertainty

- The 1 October acceptance recorded 1,044 published paths with exact Git membership
  and current bytes. Those are historical observations, not today's inventory.
- The retained 17:05 UTC documentation receipt verifies current post-merge citation
  binding. The public acceptance paragraph now reflects that completed check.
  Any subsequent merge requires its own automatic-publication freshness check.
- A fresh 3 October search reproduced `scope-capacity-exceeded`; another returned
  `index-updating`. These are separate conditions. The latter is legitimate during
  publication and requires a current-generation baseline before performance claims.
- At the deployed baseline, `SqlCorpusRetrievalReader.Dense.cs` loads up to 10,001 scoped vector payloads and
  refuses more than 10,000. The engine also opens the global ANN lease for scoped
  searches; opening it loads all generation vectors for validation.
- At the deployed baseline, the saved code citation at UTF-16 offset 4,658, length 568 succeeds with
  zero context and fails with 1,024 extra units. A read-only probe against the
  deployed detector found the returned 1,592 units safe; its 5,925-unit complete-line
  disclosure window mistakes a C# block for a JSON candidate exceeding 4,096 units.
  Independently, the complete 1,336-unit OwnsRootScanAsync method passes, while its
  1,149-unit brace-leading body fails the leading-JSON heuristic.

These probes identify false positives; they do not prove every withheld passage
safe. The actual method declaration is later in the file, beyond offset 34,000.
Parsing an arbitrary clipped window cannot prove whether it starts inside a raw
string, comment or disabled preprocessor region. That rules out a blanket
“C# parsed, therefore safe” exception.

## Decisions and alternatives

| Decision | Selected approach | Rejected alternative and reason |
| --- | --- | --- |
| Code disclosure | Application-generated, versioned structural-brace proof tied to exact canonical text; all credential checks still run | Raise JSON limits, exempt code files, or repeatedly shrink the guard: these weaken protection or leave the required method read unresolved |
| Proof origin | Parse complete canonical C# text once within existing C# processor protections | Parse unanchored read fragments or reuse physical-file symbol offsets: lexical state and CRLF/UTF-16 correspondence are not established |
| Proof storage | Optional artifact-linked header and indexed proof spans | Large offset arrays in citation metadata would hydrate or repeatedly parse unrelated spans on every read |
| Scoped semantic candidates | Exact keyset-paged SQL scan with bounded top-100 accumulation | Raise the 10,000 cap or filter global ANN top-k: the first retains a size cliff; the second can miss every relevant in-scope passage |
| Larger-scale optimisation | Measure the exact scan first | Per-root ANN indexes, new caches and model changes add lifecycle complexity before a measured need |

## 1. Cited code text with unchanged credential protection

### Trusted, optional syntax proof

At canonical indexing, use the existing Roslyn dependency and the exact
`StageArtifact.SearchText`, after normalisation. Do not copy spans from the separate
C# fact branch, whose input representation can differ. An extension may select a
candidate for processing; it never grants disclosure.

Generate a proof only from a complete, error-free C# parse with the current
supported C# 14/Roslyn 5.0 parser identity. Reuse the existing protections:
4 MiB UTF-8 input, 4,000,000 decoded UTF-16 units, 200,000 syntax nodes and nesting
depth 256. Cancellation and unsupported syntax fail closed. These existing limits
remain protections, not additional repository/file-list caps.

Record only opening `{` offsets proved to introduce an unambiguous namespace/type,
member body or control-flow block under such a declaration. Do not mark bare
top-level blocks, initialisers, collection expressions, arrays, indexers,
attributes, interpolation delimiters, strings, comments, skipped tokens or
disabled preprocessor text. Pure JSON is never granted a code exception.

Use Roslyn's bounded literal/trivia interpretation to inspect ordinary, verbatim,
raw and interpolated string text, and trivia including comments and disabled text,
with the same credential and
encoded-content checks. Never execute expressions or assume a computed,
concatenated or ambiguously encoded value is safe. Record protected/ambiguous
canonical ranges when a credential is detected or the relevant literal/encoded
boundary cannot be established within the existing budgets. These ranges remain
effective when the containing method's opening brace is outside a read window.
Clean passages elsewhere in the artifact remain eligible: withholding the entire
file would regress the already verified safe excerpts in credential-test files.
Literal/trivia inspection and span production share the parser's resource and
cancellation accounting; they do not start unbounded secondary traversals.

Use two derived SQL tables:

- `CanonicalCodeDisclosureProofs`: artifact ID, canonical SHA-256 and UTF-16 length,
  parser/classifier fingerprint, result state, span count and deterministic
  proof checksum. Artifact ownership supplies the immutable pipeline record and
  revision; reads must match them. States distinguish ready, unsupported and
  invalid input without publishing partial proof.
- `CanonicalCodeDisclosureSpans`: artifact ID, classifier fingerprint, absolute
  UTF-16 start/end and kind (structural opening brace or protected/ambiguous range),
  with an artifact/start seek key and foreign key to the proof. Structural spans
  are exactly one actual opening brace. Protected intervals are merged to remove
  overlap/duplication; they are never exceptions to scanning. Existing parser
  limits bound production. Store a per-row integrity checksum over the proof
  identity, canonical hash, fingerprint, span and kind; verify returned rows.

Only application-owned indexing/backfill code can write this projection. Neither
source metadata nor any API request may supply trusted proof. Parse outside the
publication transaction; persist the artifact/proof/spans together for new
indexing. A backfill transaction rechecks the immutable artifact identity, content
hash and current eligibility before atomically publishing its entire proof.
Uniqueness and same-fingerprint comparison make concurrent writers idempotent.
Failure/cancellation publishes no ready partial map. Artifact cleanup removes the
derived rows. Ready rows are immutable; a new classifier version needs a new proof.
The application-owned SQL store is the trust boundary. Checksums detect accidental
or cross-binding corruption; they are not authentication against a database writer
able to rewrite both content and checksums.

### Read-time use

SQL candidate/context reads obtain only proof spans intersecting their existing
bounded canonical ranges. A proof window carries artifact/hash/version identity,
absolute start and the applicable spans; it is an internal value, never an
MCP/REST/CLI input or output. Invalid binding, count, ordering, range, character or
fingerprint produces no exceptions to the existing detector.
Read the header, full persisted span count and selected spans coherently for the
same artifact/fingerprint. Compare the server-side count with the header without
hydrating the complete map; verify each selected span's bound checksum. A missing
row or a corrupt offset moved onto a brace inside a string must grant no exception.

Any protected/ambiguous range intersecting the actual searched body/model input or
returned context withholds that value, including a literal whose beginning lies
outside the window. The complete guard retains and validates every intersecting
proof span; a protected range wholly outside the returned interval does not alone
withhold a safe excerpt. The returned interval must lie wholly inside the verified
guard. A partially missing or invalid proof is never used to
skip structural braces while losing its protected ranges. Current source
eligibility is checked independently of proof.

Apply proof consistently to searchable body/model-input checks and cited reads.
Translate offsets explicitly when a context header precedes a body; metadata and
header text receive ordinary scanning. Search-time and read-time safety must not
disagree merely because their windows start at different positions.

The detector continues scanning the original, unchanged text:

1. Run existing sentinel, assignment, header, URI, private-key and encoded-content
   checks over the whole value; honour protected/ambiguous spans in actual output.
2. Run conservative raw and transport-escaped quoted credential-property/token
   checks over the entire original bounded value, including malformed/truncated
   keys. This preserves protection formerly supplied incidentally by an enclosing
   body-brace candidate, including `// "password":"synthetic"`.
3. Exclude only proved structural opening-brace positions from JSON-candidate
   enumeration and the leading-JSON heuristic. All other candidates, including
   those in strings/comments/disabled text, retain existing JSON checks. In the
   validated-code path only, a candidate closed within the existing 4,096-unit
   scanner budget does not exceed that budget merely because unrelated guard text
   follows it. Keep the bounded tail-token checks and full-guard raw/escaped scans.
   Unclosed, malformed or oversized candidates retain conservative refusal;
   unproved/generic evaluation is unchanged.
4. Preserve the final native envelope protection.

Preserve the complete-line disclosure guard and its 2,048-unit halo, 16 Ki-unit
whole-text scan budget, 4,096-unit JSON-candidate budget, depth 32, 64 candidates,
encoded recursion protections, 4,096 extra-context limit and 256 KiB response
ceiling. No redaction, fabricated context, repeated shrinking or partial-method
substitution is presented as exact full-method evidence. Missing/unproved/invalid
code can still return `content-withheld`; this is reported as a limitation.

### Existing artifacts

An additive schema migration creates the optional projection. New canonical
artifacts receive proof normally. A supervised, model-free derived-projection
worker fills missing/current-policy proofs from retained canonical artifacts,
one bounded artifact at a time, using the same builder and atomic writer.

It never reads source originals, changes source configuration, republishes text,
changes chunk/search-input hashes, creates vectors, rebuilds ANN or retries jobs.
Unsupported/invalid content is terminal for that artifact/fingerprint; transient
SQL failures use bounded backoff and shutdown cancellation is contained. Concurrent
instances can compute redundantly but commit one identical projection. New
publication/deletion wins over stale backfill work through the commit recheck.
The worker uses the existing deployment-hold/mutation admission guard and performs
no projection writes while the release validation hold is active.

Expose bounded counts/status through the existing internal diagnostics style;
do not record source text, credentials or complete parser diagnostics. The future
production release must explicitly include both the additive migration and
automatic proof backfill. Local implementation approval does not authorise either
operation against production.

### Implemented disclosure details

Named `EvaluateCode` and `EvaluateDecodedText` methods preserve the original
`Evaluate` signature and its conservative default for reflection-based callers.
Only derived proof and lexical rank are excluded from authoritative passage
equality; each independently loaded proof is still checked at its consumer.

Concatenations remain protected through parentheses, casts, nullable suppression,
coalescing, conditional selection and switch-expression arms. Compound addition
assignments containing literals are protected without reconstructing prior values.
Constant interpolation holes are reconstructed and scanned jointly. Unknown holes
are permitted only for simple identifiers/member references in recognised SQL text,
at standalone value boundaries outside quotes, quoted identifiers and comments.
They are never evaluated. Placeholder text and joined known text both undergo the
existing checks. Other computed, formatted, composite or ambiguous interpolations
remain protected. This bounded exception preserves the actual root-scan method
without trusting SQL-looking encoded or quoted credential fragments.

Anchored switch-statement bodies, lambdas and anonymous methods have structural
braces; switch expressions and initialisers do not. The unchanged 16 Ki-unit read
fetch now reserves the full allowed before-context plus its guard halo, so an
end-of-file citation can return the requested context with exact canonical offsets.

## 2. Scoped semantic search without a total-vector cutoff

Change the dense-reader input from `ICorpusAnnLease` to its existing base
`ICorpusGenerationLease`. For root/workspace requests, the scheduled callback owns
the SQL generation lease directly and does not open native ANN. For all-corpus
requests, ownership still transfers to `ICorpusAnnLeaseFactory.OpenAsync`, which
owns cleanup even if opening fails. Exactly one owner disposes each acquired
lease, after the work using it has stopped.

For root/workspace scope, seek through the captured generation in ascending
`VectorId` pages of 256 rows using `VectorId > lastSeen`. This is a memory/transport
batch size, not a total membership limit. At the current 1,024 dimensions, each
full page carries approximately 1 MiB of vector payload, plus bounded row overhead.
The existing composite generation/vector key supports the seek.

Live measurement found repeated large joins/sorts for each page. Scoped pages
now seek the existing `(GenerationId, VectorId)` membership key and order by its
joined vector ID, using constant `FORCESEEK`, `LOOP JOIN`, `FORCE ORDER` and
scoped-only `MAXDOP 1` hints. The serial page avoids parallel sorts that consume
the generation before returning `TOP`; it does not serialise other queries.
All eligibility predicates remain before `TOP`; argument binding and all-corpus
ANN behaviour are unchanged. Complete drains returned identical IDs for full,
nested, sparse and empty scopes. Sparse scopes incur more point lookups, so retain
their measured cost in the release evidence rather than implying universal speedup.

Every page repeats the current publication, root/workspace containment, active
generation, epoch/version, model, dimension, source revision, chunk hash and
search-input hash predicates. Validate all returned payloads, including low-ranked
rows: byte length, checksum, finite values and normalisation. Enforce positive,
unique, strictly advancing IDs. Check cancellation during scoring as well as I/O.

Maintain a top-100 heap containing only double-precision distance and vector/chunk
IDs, ordered exactly as today by distance then vector ID. Discard page payloads
after scoring. Read all eligible pages before returning semantic success; no
unreported prefix or global top-k filtering. Hydrate only the final passage set
and reapply its publication/scope bindings.

Check lease currency before/after each page and after final hydration/assembly.
Retirement, publication/version change, lost session or cancellation discards all
partial dense work. Preserve existing `index-updating`, timeout/unavailable and
lexical fallback behaviour. Keep `scope-capacity-exceeded` wire compatibility even
though this scoped implementation no longer emits it solely for member count.

Retain top-100 candidate budgets, fusion/reranking behaviour, query limits,
GPU-first/resident-CPU fallback, the deployed 20-second GPU execution bound and
25-second outer deadline. This algorithm bounds memory, not total CPU time.
If it cannot meet the acceptance workload, stop at the measured bottleneck and
revise the design; do not silently increase deadlines or introduce another
repository-size refusal. The existing all-corpus ANN path is unchanged.

## Acceptance and release

### Explicit recovery of retained terminal Embed checkpoints

The first release found 1,023 of 1,066 current eligible paths published; the other
43 had terminal `embedding-checkpoint-source-unavailable` failures predating Apply.
Read-only evidence found compatible drafts, 457 saved vectors, 2,889 canonical
chunks and 117 completed GPU attempts with confirmed cleanup and no active slot.
The timing supports a temporary source withdrawal during repository recovery;
it does not establish a new deployment failure. These are historical counts.

Expose `embedding_retry` with one `jobId` through existing native preview/confirmed
commit, idempotency and audit surfaces. Admit only the exact current retained
record/source under an enabled root, a terminal failed Embed job and its completed
unsuccessful delivery, with no live owner, competing work, rebuild/supersession or
downstream success. Validate the retained draft's epoch/profile, canonical text,
chunks/search inputs, vector payloads/counts/checksum and every GPU request/task/
batch/dispatch. GPU history must be completed, cleanup-confirmed, slot-free and
bound to the exact inputs and result-payload digest. Other histories refuse.

Confirmation binds row versions and bounded hash commitments for paged inputs and
GPU history. Commit revalidates under the existing publication fence, locks
dispatch before job, audits the previous failure and advances both lease
generations atomically with its receipt. Preserve job/delivery IDs, dispatch
generation/key, attempts, draft and saved vectors; resume only missing batches
through the normal worker and publication path. Old owners/callbacks must not
replay inference or alter a new claim. A lost response replays its receipt; stale
confirmation, cancellation or pre-commit failure cannot partially queue recovery.

Prepare a fresh exact current cohort and source-byte/checkpoint evidence after
integration. Independent review and explicit user approval precede any production
terminal processing. Stop on changed bindings or contradictory state. Recovery
does not reset drafts, fake ownership, republish files, rebuild ANN or acquire models.

The first safe end-to-end result is a disposable Git source containing a supported
C# method beyond 16 Ki units: normal discovery → canonical artifact and atomic
proof → publication → search → exact cited method read through a real transport.
The same fixture must withhold embedded credentials in strings, comments, disabled
text and malformed/encoded JSON. This projection is the minimum foundation needed
to distinguish code safely; it is not a separate preparatory milestone.

The second result is a disposable published scope above 10,000 vectors whose best
semantic match lies after the former cutoff, returned and cited without opening
global ANN. Prove cancellation, competing publication/deletion, corrupt late rows,
scope containment, deterministic ties and single-owner cleanup.

Before a release, run the affected combined suites and required repository
closeout with zero new warnings; obtain independent review of security,
migration/concurrency and the complete diff. Prepare the incremental updater's
PlanOnly output, exact payload, migration/backfill target, probes and rollback.
Obtain user authority for that concrete release. No clean-slate path, dead-letter
processing or new model acquisition is part of it.

After an authorised release, independently verify proof completeness for accepted
targets and unchanged canonical/vector hashes; current Git publication, source
configuration and process health; full method readback and documentation freshness
through MCP/REST/CLI; and a frozen representative workload of 40 searches at two
callers spanning repository root and nested scopes. Require exact citations and
in-scope results throughout, semantic-ready responses on a quiescent supported
generation, and p95 under the existing measured 20-second acceptance envelope.
Report updates/timeouts separately; do not count lexical fallback as semantic
success. Reconcile transient index-updating with recorded publication/index stamps.

Rollback keeps the additive projection/schema and restores a compatible prior
payload through the reviewed incremental recovery path. Prior binaries ignore
proof and revert to conservative disclosure and the former scoped capacity
refusal; canonical text, vectors and source configuration are unchanged. Do not
downgrade past the already documented Git-root compatibility boundary.

## Healthy projection checks without search exclusion

Post-recovery acceptance exposed periodic exclusive recovery ownership even while
the projection remained healthy. The locally implemented correction uses Shared
session ownership for healthy SQL/path/native validation. It retains the existing
serializable publication fence and complete integrity checks. Startup, recovery,
publication and filesystem mutation still require Exclusive ownership.

A detected fault leaves Healthy before recovery admission; a concurrent reader
notification is preserved. Shared ownership is disposed before Exclusive is
acquired, and recovery rereads SQL rather than mutating from the earlier snapshot.
Rebuild admission drains only proven-exited query owners inside its existing
Exclusive transaction; running or unknown ownership refuses admission, and failed
admission rolls back drainage. Shared probes leave query registrations untouched.
Disposable concurrency, recovery and web composition checks pass. Production
activation and the unchanged final semantic acceptance gate remain pending.

## Unattended discovery hosting correction

IIS currently uses `OnDemand`, a 20-minute terminating idle timeout and disabled
application preload. Both watchers and the 15-minute reconciliation timer run in
the Web process, so an idle shutdown can stop automatic discovery until another
HTTP request. This is a hosting gap, distinct from the earlier unhandled crashes.

Keep the existing host and discovery cadence. The incremental updater's opt-in
`-EnableUnattendedDiscovery` applies `AlwaysRunning`, zero idle timeout and enabled
root-application preload for FluxKnowledge only. Application Initialization must
already be enabled. PlanOnly reports the captured and target settings; Apply records
the original tuple, drains GPU work and proves worker exit before committing the
three settings together. Failure restores and verifies the captured tuple before
restarting the predecessor. Unknown configuration drift or unverified restoration
retains the admission hold. Migrations, rebuilds and other recovery modes cannot
combine with this change.

Before HTTP probes, the updater requires a fresh native managed-start event bound
to the current pool worker, its creation time and canonical application path.
Missing or unreadable evidence follows the same held rollback path; predecessor
startup uses its restored original hosting policy.

Production Apply requires separate explicit authority and immediate independent
operational review. Prove managed-host startup without an external request, then
automatic watcher/rescan publication over more than the old idle window using
read-only SQL/process observations without HTTP keep-alive probes. Preserve normal
recycle and rapid-fail protection. No second scheduler or model acquisition is needed.

Reassess after the two substantive implementation batches, or earlier if either
safe end-to-end result is blocked. Finish only when the stated supported code and
scope cases pass. Keep the existing roadmap progress unchanged until then.
