# Overview refresh on 18 November 2026

## Diagnosis

After a source reconciliation completed at 11:12:26 Cairo time, Overview was
still showing its 11:07:57 sample and the busy notice. The source detail read
reflected the reconciled inventory. The busy notice records exhaustion of the
read budget. A consistency rejection has a separate notice. The affected read
has no command trace.

The release base was `375cbdd4`. An unchanged 30,000-file, 20,000-job staging
fixture exhausted its initial public Overview read at 2.049 s, after fetching
30,001 revision and 20,002 activity rows. Cancellation reached the running-code
lookup as it began. Earlier reads and materialisation consumed nearly the full budget before that lookup began.

Earlier [status evidence](2026-11-18-startup-and-indexing-recovery.md#live-acceptance)
identified a separate, expensive plan for the bulk source-activity query. The
captured statement accumulated 3.908 s of SQL CPU and 997,558 logical reads
with no blocking session. The root primary-key histogram described a retired
root. The recorded disposable statistics comparison reduced logical reads
from 399,032 to 9,815 for the same 30,000 selected file outcomes.

## Correction

The bulk activity statement reads the same current revisions, activity
eligibility, branch and publication evidence. A separate scalar read from
`SourceRootConfigurations` captures each required threshold within the same Snapshot transaction
or existing LegacyFenced boundary. Classification uses the exact captured
limit and keeps the existing reason and size boundary rules. This is a code-only change.

The coordinator and direct SQL reader use one five-second budget value. The
active attempt includes setup, SQL, retry delays and preparation. Cancellation
waits for resource cleanup. The start throttle, automatic failure backoff,
last-good fallback and independent file drilldown budget retain their
existing behaviour. The successful timestamp still identifies the committed
image used for the counts.

## Isolated qualification

The fixture uses synthetic data, owned disposable SQL catalogues, the
application EF retry policy and the Snapshot consistency mode. The browser
runs on an isolated loopback host.

| 30,000 files and 20,000 jobs | Overview and file pages | Writer commits | Combined p95 | Maximum Overview |
| --- | ---: | ---: | ---: | ---: |
| Previous SQL with five-second Overview control | 24/24 | 730 | 1.226 s | 1.568 s |
| Revised SQL with five-second Overview | 24/24 | 787 | 1.369 s | 2.160 s |

Both dense series passed the existing 2.5-second performance assertion and
exact count, category and pagination checks. The revised sparse case kept
30,000 files and 1,000 jobs. It passed 24/24 reads with 403 overlapping
commits, a 746 ms combined p95 and an 840 ms maximum Overview. On the slowest revised Overview, the
job-facts insert was the longest individual observed SQL command at 279 ms.
The run measures cumulative work across the database, EF and projection.
The latest matched stale-histogram control used 8,973 warm logical reads and
the revised query used 9,009 before the statistics refresh, with the same
file outcomes. The contemporary control also selected a low-cost plan. These timings
measure the revised reader's behaviour under load and leave the earlier bad
plan as historical evidence.

The real dashboard published an in-budget sample after a three-second SQL
wait while a source write committed. Known and Pending both used the
earlier committed counts and the next refresh showed the newly committed
counts. The other browser case exhausted the budget, retained the previous
timestamp and recovered on manual refresh. Controlled-clock tests verify
both five-second deadlines and automatic backoff. The focused suites passed
69 unit and composition checks, 125 SQL regression checks and two browser
cases. The scale cases are qualified separately from the routine SQL suite.

The local TRX receipts are in the owned Overview SQL staging deployment.
`baseline-scale.trx` contains the two-second failure.
`control-scale.trx`, `revised-scale.trx`, `control-stale-policy.trx` and
`revised-stale-policy.trx` contain the comparison.
`revised-sparse-scale.trx` contains the sparse series.
`overview-unit-final.trx`, `overview-sql-regressions.trx` and
`overview-browser.trx` contain focused results.

The combined `bc44ff03` release deployed this correction and passed the integration
owner's live sampling/refresh checks. The later `14b5ed32` forward recovery retains
the same Overview change. Final indexing, source-removal and closeout acceptance
remain with the integration owner.
