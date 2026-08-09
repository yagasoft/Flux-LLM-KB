# Phase 3B watcher, Corpus and Events validation

Status: passed on 2026-08-10. This is a local, loopback-only, non-production
IIS validation record. It is not a production rollout or legacy cutover.

## Delivered scope

Phase 3B adds advisory local file-watcher hints that coalesce into the existing
durable scan controls, a SQL-authoritative PipelineRecord-led Corpus explorer,
and a durable Events dashboard. SQL remains authoritative; USearch remains a
derived index. The watcher neither owns source truth nor bypasses periodic
reconciliation.

The deployed migration target was
`20260809110000_AddPhase3BWatcherCorpusEvents`. Direct SQL validation confirmed
that this migration is present, `dbo.SourceRootWatchStates` exists, and
`dbo.AuditEvents.SourceRootId` is present. The deployment retained a SQL backup
and IIS rollback payload.

## Verification evidence

| Check | Result |
| --- | --- |
| Release build with warnings treated as errors | Passed with zero warnings and errors |
| Disposable-SQL Release tests | Domain 276/276, Integration 323/323, Web 76/76; three browser-gated Web tests skipped in that run |
| Guarded browser matrix | 3/3 passed |
| Loopback health | `/health/live` and `/health/ready` returned HTTP 200 |
| Browser smoke | Corpus loaded its SQL-authoritative projection; Events showed durable event history and live-tail controls |

## Live watcher proof

An ephemeral UTF-8 sentinel was created beneath the existing local Phase 3A
scan-validation root. The durable database evidence showed a
`watch.batch_detected` event, a released normal `SourceScanRequest`, and an
unsuppressed source revision for the sentinel. The sentinel was then deleted.
A subsequent watcher batch and `source.removed` event were recorded, and the
same source revision gained a suppression timestamp. The sentinel was removed
after the proof.

This verifies the intended advisory path: local change hint to durable scan
control to retained source state. It does not claim indexing of deferred content
or activate any processor, GPU/model, process, external, MCP mutation, Docker,
RabbitMQ, Vespa, or legacy action.

## Closeout

The feature branch was squash-merged into `main`, pushed, and its dedicated
worktree removed by the repository closeout script. The deployment script now
requires the Phase 3B migration target during its preflight, update and final
validation stages.
