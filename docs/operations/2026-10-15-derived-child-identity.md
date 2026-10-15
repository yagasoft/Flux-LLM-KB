# Derived document revision identity and collision containment

Repeated OOXML processing hit a named `SourceRevisions` unique-index conflict.
The exception escaped the archive-only item-failure guard, stopped the background
host and was followed by native shutdown failures. The native cause remains
unproved; this correction removes the verified managed trigger.

New OOXML text, binary document-input and media-metadata children include their
immutable parent revision ID in the opaque locator and inherit the parent's
revision number. Stable identities, member fingerprints and completion receipts
retain their existing semantics. Completion first looks up the exact parent-bound
child, so existing IDs, historical locators and revision numbers, artifacts and
activities remain intact. No data migration or historical rewrite is required.

The SQL store translates only the two named child identity indexes' 2601/2627
errors for added archive, document or media children. Translation occurs after
the transaction and context unwind. The existing lease and parent fences then
record an item failure. Other errors continue to surface, and host-wide exception
handling is unchanged.

Focused verification includes failing locator and conflict-classifier regressions,
then 260 passing application/domain checks. Five disposable SQL parent-revision
cases reproduced the stable-identity/revision collision before the store change.
All 15 new SQL cases now pass: unchanged/changed OOXML and media output, binary
document artifact references, concurrent replay of existing children, ordinary and forced
collision containment for both indexes, healthy continuation and no-content
completion. The broader affected OOXML, media and archive suite passes 91 checks,
including those 15. Tests use synthetic inputs, an owned disposable catalogue and retained
storage, without real models or production bindings.

Independent Astra implementation and exact operational review approved the source
change and ordinary incremental deployment. With explicit user authority, release
`b74a81ac` was deployed without a migration. Both installed payloads match the
candidate, both rollback payloads match their predecessors, configurations and the
companion task definition are unchanged, and the validation hold is released.

The complete feature suite passed 3,447 tests with 26 optional skips and zero build
warnings. Main verification encountered an unchanged disposable SQL administrative
timeout before its recovery assertions; an unchanged focused rerun passed. The
fixture now allows 30 seconds for guarded catalogue administration, preserving its
five-second connection timeout, exact ownership/file guards, fresh-connection
restoration and runtime assertions. Prescribed feature closeout remains the
integration mechanism.

Live observation retained one worker for 612.2 seconds and all 120 health probes
returned HTTP 200. No matching native crash, host stop or CPU-cleanup deadline was
observed. One file-watcher permission warning remains a source-access issue. An
earlier 105.7-second observation stopped when a readiness request exceeded its
separate ten-second observer budget; that failure is preserved. The second
observation used the updater's existing 120-second probe budget.

The affected OOXML parent branch completed and published one new child matching
the reported content and previous-locator fingerprints. Its new locator is bound
to its parent, and both revisions are 2. The child's downstream extraction was
still running with a pipeline result present; this is not full indexing acceptance.
Preserve paired rollback payloads and existing children. Native cause/prevention
and the broader frozen retrieval acceptance remain open.
