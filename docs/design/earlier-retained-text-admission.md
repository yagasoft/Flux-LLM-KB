# Earlier retained text admission and status

An unchanged source revision retains its original classification. A later scan
can recognise valid text under revised rules and create an earlier-format pending text
activity, while pipeline registration still refuses the original classification.
Repeated scans therefore leave an activity pending without an executable job.
This change repairs that lifecycle through the existing explicit
`policy_reprocess` contract and reports the current state accurately.

## Admission invariants

Keep immutable classifications, original terminal history, last good publication,
root configuration and scope checks, physical binding uniqueness, retained
checksum verification and strict full-buffer UTF-8 validation.

Use the existing eligible policy predecessor when a revision has both an older
policy refusal and idle text activity. That predecessor owns the sole successor
relation; admission cancels the idle sibling. A request against the sibling is
refused, including concurrent requests, so it cannot leave the older block current.

The additional primary route accepts only exact `phase-3a-v1` text activity
with its original descriptor, in-process execution, no capability or reason, no
attempt or attempt timestamp, and no pipeline receipt. First admission also
requires no other unresolved owner, recognised policy successor, processor branch
or pipeline record for the revision. Retained qualification happens before the
mutation transaction; these absence conditions are rechecked under the existing
serializable publication fence before creating a successor.

The primary idle activity is then cancelled with policy and successor evidence.
Revalidation requires that evidence, its exact durable relation and the matching
same-revision/hash text successor and admission proof. Arbitrary cancelled
retention history grants no authority. Configuration drift can refresh only a
never-executed, unlinked successor after fresh qualification. Receipt replay is
idempotent; a fresh reprocess cannot restart completed work.

After a successful policy commit or receipt replay, native finalization offers
only that exact unlinked pending text successor to the established planner. The
planner rechecks eligibility under its own publication fence and atomically
creates the record, job and outbox delivery. It does not wait for an active scan
to finish. The policy receipt survives an interruption on either side of job
registration; replay repeats the bounded hand-off and wakes the outbox even if
the job was already linked. The existing reconciliation path remains a fallback.

## Status behaviour

Source detail and overview share a bounded SQL predicate for genuinely unbound
earlier-format text. They show `Blocked` with
`retained-text-policy-reassessment-required`, rather than implying that an
executor will pick the activity up. This projection does not rewrite stored
history or establish eligibility. A current older policy owner keeps its single
authoritative reason in both views. Verified successors and actual jobs retain
their normal status. Current publication, selected processor branches, explicit
unsupported outcomes, real failures and unconfirmed bindings keep their precedence.
An older extension refusal above the lower of the configured root and canonical
text limits displays the effective size refusal. File guidance explains the
required retained-policy assessment.

## Verification and release

The first disposable result is retained input -> exact native action -> durable
successor and job/outbox -> normal worker stages -> publication -> matching source
detail and overview. Regressions cover both real earlier-format shapes, both predecessor
orders, concurrent commands, a late record/owner, malformed or foreign cancellation
proofs, source scope and immutable history, configuration drift, restart and
idempotency. Model-free embedding and the existing disposable pipeline/index
fixture exercise publication. Existing dashboard scale checks cover the new SQL
projection under concurrent writes.

Production recovery uses a sealed metadata cohort, retained-only qualification,
fresh per-item native preview/commit and durable receipts. Qualification metadata
alone cannot authorise a mutation. Preserve refused, unsupported, oversized and
superseded inputs, and leave existing indexing work running. No source-wide
automatic admission, classification edit, general job replay, schema change or
model acquisition is introduced. Use reviewed routine incremental deployment;
after new pending-primary admissions, retain compatible binaries and use reviewed
forward recovery rather than an older predecessor that lacks this proof path.
