# Task 6 durable retained C# lifecycle report

## Outcome

The independently rejected Task 6 base `dc9b78f` has a focused lifecycle correction ready for fresh independent review. The correction adds no public Operator Action, REST, CLI or MCP C# mutation surface and never reads a source original.

## Delivered correction

- Replan now accepts only the exact `.cs` `DocumentParsing` holding route with the approved version, capability, reason and legacy descriptor sentinel. Under a serialisable lock it either fences active unclaimed legacy `TextExtraction` routes or records `csharp-code-legacy-text-conflict` and a bounded audit event without creating a C# branch.
- Generic claim and commit paths reject both `CodeParsing` activities and the C# processor fingerprint, including malformed cross-route combinations. Only the dedicated C# claim returns the persisted attempt identity.
- Default activation, direct promotion and direct C# claim are inert until the exact generated migration, tables, equality FK, collation, immutable triggers, insert fences and receipt-closure trigger are present. Hosted activation also requires the exact local handler, processor and preflight before capability registration.
- Missing-receipt completion revalidates the claimed attempt, activity, revision and immutable retained-artifact hash/length binding under serialisable locks and SQL current UTC fences. Expired, rebound, cancelled and superseded work cannot persist facts.
- Success completion validates exact diagnostic codes, contiguous ordinals, representation-derived withheld counts, every fact fingerprint, document/completion fingerprints and decoded-character/line counts. Syntax-invalid completion validates the corresponding attempt-owned blocked diagnostic and blocked-completion contract. Exact replay remains receipt-first and preserves the original blocked attempt ownership.
- Generated migration `20260820070404_HardenRetainedCsharpLifecycle` adds restrictive receipt/document identity equality, stronger checks, immutable update/delete triggers, post-receipt insert fences and success/blocked closure triggers. The writer flushes facts before the receipt inside one serialisable transaction, making the receipt the database closure point without exposing partial committed state.

## RED/GREEN evidence

Focused REDs exposed missing readiness/count contracts, a generated-FK collation mismatch, pre-migration direct-claim failure, corrupt retained-binding claims/completions, a malformed generic C# claim/commit path and incomplete readiness detection after the closure trigger was removed. Each failed before its scoped correction.

Fresh GREEN evidence:

- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter "(FullyQualifiedName~RetainedCsharpCodeReplayIntegrationTests|FullyQualifiedName~RetainedCsharpCodeLifecycleCorrectionIntegrationTests)" --logger "console;verbosity=quiet"` — passed 21/21: the original 6 replay tests plus 15 correction tests. Coverage includes exact route rejection/fencing/conflict, readiness and inert pre-migration activation, generic-claim exclusion, corrupt/rebound/cancelled/stale fences, concurrent claim/reclaim/restart, valid success and blocked conflicts, later-attempt replay ownership, secret withheld/scan-failure atomicity, immutable/insert-fence trigger behaviour, and generated migration upgrade/downgrade/reapply.
- `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RetainedCsharpCodeProcessorTests" --logger "console;verbosity=quiet"` — passed 40/40.
- `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --startup-project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --configuration Release --no-build` — passed: no pending model changes.
- `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` — passed with zero warnings and zero errors.
- `pwsh -NoProfile -File scripts/dev/assert-legacy-gmail-unchanged.ps1 -RepositoryRoot . -BaselineRef dc9b78f` — passed for the correction diff.
- `git diff --check` — passed; the tracked snapshot line-ending notice is informational.

## Known failing/unrun evidence

- The pre-existing OOXML override fixture mismatch recorded by the base report was not rerun and is not claimed as passed.
- The whole-branch Gmail guard against `main` was not rerun; only the correction diff from `dc9b78f` passed. No Outlook guard script was available.
- Outlook was not started or enabled. No browser/live validation, source-original read, non-disposable database, external parser/network call, model/runtime activation, deployment, push or merge was performed.

## Review focus

Review exact holding-route and legacy-text exclusivity, applied-schema/handler/processor readiness before registration/promotion/claim, generic C# exclusion, receipt-before-fence replay, current retained-binding and attempt fences, success/blocked field validation, schema closure/immutability and migration down/up safety. Confirm the correction introduces no public C# mutation transport.

## Outcome-closure correction after independent review

The fresh review of `dc9b78f..2bb7ea3` rejected two remaining P1 gaps: a syntax-invalid receipt could acquire a document after receipt insertion, and a success receipt could close over blocked diagnostics inserted before it. It also required field-by-field replay-conflict evidence for both completion-fingerprint fields, all three withheld counts and the ordered diagnostic-code wire value.

The correction adds generated migration `20260820101021_CloseRetainedCsharpMixedOutcomes`, designer and snapshot. Its document insert fence rejects every post-receipt document, while its success-receipt outcome fence rejects any branch that already owns a blocked diagnostic. The existing blocked-diagnostic insert fence continues to reject post-success inserts. Readiness now requires the new migration and both exact trigger identities. Receipt lookup now precedes missing-receipt canonical validation, so an immutable receipt returns deterministic conflict for any non-exact field while an exact later-attempt replay retains the original receipt and blocked-diagnostic ownership.

Focused TDD evidence:

- RED: the initial 13-case generated-SQL matrix failed all 13 cases before implementation. The readiness probe remained true without the document fence, both mixed-outcome writes succeeded and replay-field mutations raised pre-replay validation exceptions instead of returning deterministic conflicts. After replacing an accidental same-code reorder with a literal changed ordered-code value, the final two ordered-code cases were rerun against the old validation order and failed 2/2 for the expected pre-replay validation reason.
- GREEN: the same focused closure/replay/readiness matrix passed 13/13, and the exact generated-schema trigger probe passed 1/1.
- GREEN: combined `RetainedCsharpCodeReplayIntegrationTests` plus `RetainedCsharpCodeLifecycleCorrectionIntegrationTests` passed 33/33 against a fresh generated disposable SQL catalogue. This includes generated upgrade, downgrade/reapply, both mixed-outcome directions and exact replay/conflict coverage.
- GREEN: focused `RetainedCsharpCodeProcessorTests` passed 40/40.
- GREEN: EF reported no pending model changes; the Release solution build completed with zero warnings and zero errors; the Gmail correction-diff guard from `2bb7ea3` passed; `git diff --check` passed with only the tracked snapshot line-ending notice.

No Outlook process/profile, browser/live validation, source-original read, non-disposable database, external parser/network call, deployment, push or merge was performed. No public transport file is in the correction diff.

## Inert C# activation aggregation correction

An approved-slice regression was found by the surrounding OOXML test suite: the default-enabled but not-ready C# activation returned an all-zero, disabled result which was aggregated with a real legacy Office designation. That changed the observable capability from `document-office-legacy-structural-extract` to the synthetic `retained-archives` aggregate. The C# default and readiness gate remain unchanged. Activation now excludes only disabled zero-work results when choosing the visible aggregate; disabled results that performed real work, such as a legacy designation, remain observable.

Focused RED/GREEN evidence:

- RED: `OoxmlStructuralTextProcessorTests.Legacy_cfb_is_redesignated_from_the_private_retained_reader_even_when_ooxml_is_disabled` failed with expected `document-office-legacy-structural-extract` and actual `retained-archives` before the production change.
- GREEN: the same test passed 1/1 after the one-file aggregation correction.
- GREEN: surrounding domain activation and C# parser tests passed 116/116; generated-disposable-SQL C# lifecycle/replay tests passed 41/41.
- GREEN: `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` completed with zero warnings and zero errors; `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build` reported no pending model changes; `git diff --check` passed.

No Outlook process/profile, browser/live validation, source-original read, non-disposable database, external parser/network call, deployment, push or merge was performed.

## Closure-readiness correction after independent re-review

The re-review of `2bb7ea3..61a62b5` found four remaining P1 gaps. This focused correction amends the still-unreleased generated migration `20260820101021_CloseRetainedCsharpMixedOutcomes`; no schema-model change was required, so its existing Designer and model snapshot remain generated and aligned.

- Before the closure migration records itself, it now fails closed if a `20260820070404` database already contains either a blocked receipt plus code document or blocked diagnostics plus a success receipt. Two separately generated prior-migration catalogues seed one invalid direction each; each independently proves the migration ID is absent after its SQL failure.
- Readiness now verifies every one of the 13 C# safety triggers is present on its expected table, enabled and has the exact generated `sys.sql_modules` SHA-256 definition. The disposable test disables and replaces each trigger independently, observes fail-closed readiness, then restores its captured generated definition.
- Receipt-first replay validates the complete canonical success-or-blocked shape and all nested blocked-diagnostic branch/attempt identities before accepting an immutable replay. Invalid shapes now return `csharp-code-completion-conflict` without a write.
- The document-insert, success-receipt and blocked-diagnostic fences take the same branch `UPDLOCK, HOLDLOCK` key. Two generated-SQL two-connection barriers prove the conflicting document and receipt, and blocked diagnostic and success receipt, cannot both commit.

Strict RED/GREEN evidence for this correction:

- RED: `RetainedCsharpCodeLifecycleCorrectionIntegrationTests` initially failed replay of a success with a non-null blocked fingerprint, replay of a blocked completion with non-empty success facts, both nested blocked-diagnostic ownership mutations, an old-schema mixed-outcome upgrade, and disabled/altered readiness triggers. The first race invocation exposed an incorrect test claim setup before production code changed; that test setup was corrected before GREEN and is not claimed as a direct race RED.
- RED: after splitting the two generated-catalogue cases, a temporary local mutation of the closure guard from `OR` to `AND` ran the two-case filter and failed 2/2 with the expected “no exception was thrown”. The generated migration was restored unchanged before GREEN.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~Closing_migration_fails_closed_for_" --logger "console;verbosity=minimal"` — 2/2 independently generated upgrade cases passed.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~RetainedCsharpCodeLifecycleCorrectionIntegrationTests" --logger "console;verbosity=minimal"` — 35/35 passed against generated disposable SQL, including per-trigger disable/alter, two independently seeded old-schema closure directions, all new replay conflicts and both transaction barriers.
- GREEN: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~RetainedCsharpCode" --logger "console;verbosity=minimal"` — 40/40 passed.
- GREEN: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --no-restore --filter "FullyQualifiedName~RetainedCsharpCodeProcessorTests" --logger "console;verbosity=minimal"` — 40/40 passed.
- GREEN: `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` — zero warnings and zero errors; EF reports no pending model changes; `git diff --check` passed.
- The Gmail guard against `main` still fails only for the pre-existing branch differences it reports. The same guard against correction base `61a62b5` passed, and this correction diff contains no Gmail-owned path.
