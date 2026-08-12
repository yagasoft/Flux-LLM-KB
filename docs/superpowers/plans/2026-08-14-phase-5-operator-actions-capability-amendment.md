# Operator Actions capability amendment implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development task-by-task.

**Goal:** Deliver local, capability-bound policy override, retry and reversible ignore without weakening retained-processor safety.

**Architecture:** Extend the retained-branch store as the sole mutation owner; immutable capability/policy and identity ledgers enforce requests, while a read-only projection feeds the loopback UI/API.

**Tech Stack:** .NET, EF Core, SQL Server, Blazor, ASP.NET Core antiforgery.

## Global Constraints

- Deny every exact hard-denial reason in the approved design; no parser/original/model/Outlook work.
- Use database UTC, serialisable locking, action/operation replay first and restrictive FKs.
- Direct loopback + same-origin + antiforgery only; no MCP/CLI mutation. Trusted local detail may show useful retained-derived data; external/public/export output is sanitised and secrets remain excluded.
- Generated disposable SQL tests must use a configured safe fixture; configure one when absent and never represent a skipped test as passed.

### Task 1: Durable capability and identity foundation

**Files:** Domain/action contracts; retained-branch port/store; SQL entities/configuration/context/migration/snapshot; integration tests.

- [ ] Add failing disposable-SQL tests for hard-denial trigger, exact capability tuple FK, operation/action ledgers, historical receipt replay and ignored-head first-operation race; run RED.
- [ ] Implement immutable policies, hard-denial trigger, action/operation ledgers, historical receipts and operation-first ignore heads; generate migration/designer/snapshot.
- [ ] Run focused GREEN and generated-database upgrade proving atomic predecessor receipt-to-history plus action/operation-ledger population (no invented runnable capability), release build and diff check; commit `feat: add capability-bound operator action foundation`; obtain fresh independent Task 1 review.

### Task 2: Fenced override, retry and reconciliation

**Files:** retained-branch store, activation service/hosted service, domain contracts, integration tests.

- [ ] Add failing tests for every hard-deny/non-eligibility, policy override vs retry, operation/action replay precedence, stale action/version, distinct-operation races, claim isolation, all request and ignore state transitions, ignore reversal/new generation, cancellation/disable/expiry reconciliation; run RED.
- [ ] Implement the exact serialisable transitions and database-time reconciliation using policy/handler fences.
- [ ] Run focused GREEN, release build and diff check; commit `feat: add fenced override and retry lifecycle`; obtain fresh independent Task 2 review.

### Task 3: Loopback projection, REST and UI

**Files:** projection store/service, loopback gate, endpoint group, DTOs, Operator Actions page/nav/state, Web tests.

- [ ] Add failing GET/list and POST `/override`, `/retry`, `/ignore`, `/unignore` tests: 201 create/200 replay, fixed 400/403/404/409/503 mapping, loopback/forwarded/antiforgery/same-origin authority, UI include-ignored, audit/status privacy and no-MCP/CLI; run RED.
- [ ] Implement read-only projection and direct-loopback GET/POST UI/API with post-commit public refresh.
- [ ] Run focused GREEN, browser/privacy tests, release build and diff check; commit `feat: add local operator actions UI and API`; obtain fresh independent Task 3 review.

### Task 4: Final verification and review

- [ ] Run combined focused SQL matrix only if configured, otherwise report skipped; run Web matrix, Gmail guard, release build, model check and diff check.
- [ ] Obtain fresh independent whole-slice review; update roadmap evidence and resolve findings before any next Phase 5 slice.
