# Self-service indexing recovery implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal** Deliver the approved app-wide navigation and indexing recovery workflow for current blockers, with evidence, actionable controls and progress in the app.

**Architecture** Reuse the authoritative status snapshot, owning workers and the native confirmation service. Each feature exposes only scope-bound operations whose permission, evidence and versions can be rechecked at commit. Automation candidates stay guided until isolated tests and independent review establish the positive path and durable attempt limits.

**Tech stack** Blazor, .NET, EF Core, SQL Server, xUnit, Playwright browser tests.

**Spec** [Design](../specs/2026-11-19-self-service-indexing-recovery-design.md) and [action inventory](../specs/2026-11-19-self-service-recovery-actions.md).

## Constraints and review focus

- Import tested restoration checkpoints between substantial batches and before acceptance. Use the isolated feature branch and test deployments.
- Preserve historic attempt evidence, route compatibility, native ownership fences, frozen source admissions and existing model-cache rules.
- Test stale snapshots, no snapshot and source scope changes. Preserve references and ensure a delayed response cannot replace another issue's selection.
- Test preview scope, permission, stale target, idempotent repeat and lost receipt. When a request is accepted report progress through terminal evidence.
- A live owner, withheld native cleanup, superseded input or exhausted automatic budget must never result in competing work. Observe real owner activity and show explicit prerequisites.
- Separate production activation, deployment and live checks from implementation.

## Batch one

- [x] Add focused Overview tests for issue grouping, current action vs history, source scope and stale status. Run existing projection test target.
- [x] Update MainLayout/NavMenu, Overview and existing file/attention widgets. Map current reasons to accurate user-facing wording, scopes and actions. Keep raw evidence links in expandable UI.
- [x] Add guided controls using NativeCorpusCommandService preview and commit for source processing/rescan, resume and exact embedding or publication retry. Test preview scope, reject stale targets, distinguish accepted from recovered.
- [x] Add SQL tests for scan retry against disabled, paused and changed source configuration. Fence the scan's current root inside the existing native commit. Check regressions and native preview compatibility.
- [x] Verify on an isolated browser/app, including responsive and keyboard states, then reassess against the inventory and align with restoration.

## Batch two

- [x] Surface existing scoped source, worker, GPU and index continuation evidence. Link to Activity and source or operation progress for authoritative results. Describe unavailable evidence without inferring completion.
- [x] Wire scoped document reprocessing, archive/proc action, Outlook catch-up and prerequisite or maintenance links. Reuse the owning services and exact binding/capability checks. Test once through each normal browser entry point.
- [x] Assess Embed and OCR automation against native evidence. Retain guided recovery for both candidates. The Embed integration fixture proves existing continuation and the digest budget. OCR cannot prove the same physical input across normal withdrawal and rediscovery from the existing settlement evidence. See the qualification results in the action inventory.
- [x] Run focused regression, concurrency and browser checks after synchronising restoration. Update architecture and roadmap. The isolated browser smoke, receipt replay, stale-scope refusals and scope-specific SQL checks passed across the focused runs.
- [ ] Shared integration and full closeout are owned by Restore indexing progress after all participating chats finish. Production activation and validation require their separate authorisation.
