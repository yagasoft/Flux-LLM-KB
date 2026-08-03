# Native Windows Phase 2 scheduler validation

## Decision

The approved Phase 2 strict-priority scheduler foundation was merged and
validated on 2026-08-03 through the native Windows closeout path. This is a
local, loopback-only IIS checkpoint. SQL Server remains authoritative; the
USearch projection remains derived; no model, GPU runtime, external-access,
legacy or cutover action occurred.

The validation covers the scheduler control plane only: durable hand-off,
strict lane/FIFO compatible batching, event-driven wake handling, explicit
safe-boundary/capacity-release lifecycle fencing, uncertainty isolation and
read-only local status. It does not activate an executor or GPU workload.

## Evidence

| Check | Result |
| --- | --- |
| Release base | `82ef7cac0c209cfaad00ce8d2d4a8c1b9177dcaa` on `main`, confirmed equal to `origin/main` |
| Native Release build | Passed with zero warnings and zero errors |
| Full Release test run | Domain 129/129, Integration 209/209, Web 50/50; no skipped or failed tests with the authorised disposable-SQL and guarded-browser opt-ins |
| Native closeout and deployment contracts | Passed |
| SQL migrations | All six Phase 2 scheduler migrations present, ending at `20260802191240_AddGpuSchedulerOpaqueKeyCanonicality` |
| SQL validation | `validate-sql` reported the local SQL Server ready |
| IIS scope | The fixed site and dedicated application pool were started; the only binding was loopback |
| Endpoint probes | Liveness, readiness, index health, GPU scheduler status and search each returned HTTP 200 |
| Deployment payload | The staged and deployed web assembly SHA-256 matched: `539C5E05736200B34BEDDEA65B419AD794260129C32E4E922CCFAA3758B02E95` |
| Backup and rollback | A new checksum backup passed `RESTORE VERIFYONLY ... WITH CHECKSUM`; the deployment retained its rollback payload. No restore was performed. |

The first native deployment attempt correctly applied the scheduler migrations
and left the application healthy, but its Windows PowerShell liveness probe
reported a false failure because it omitted `-UseBasicParsing`. The follow-up
release added that flag, a native deployment contract assertion, and an
independent review. The final closeout then passed deployment and all probes.

## Boundary

This checkpoint does not authorise external exposure, IIS-site expansion,
executor/result implementation, model or GPU activation, or legacy retirement.
Those remain separate approval-gated work.
