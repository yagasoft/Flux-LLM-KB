# Overview removed-work profiling on 20 November 2026

Three approved live refreshes on tracing release `d8915c57` took 4.45, 4.72 and
5.02 seconds. The last click joined the shared read that exhausted the
five-second budget. SQL Server request samples were limited to the FluxKnowledge
IIS worker and the application database. The recurring statement
`0x1C738CB0397050B6` maps to `RemovedJobFactsSql`. The highest samples for the
three reads recorded 3.06, 3.29 and 3.61 seconds of SQL CPU, and approximately
444,000 to 447,000 logical reads. None showed a blocking session on that statement.
The companion GPU blocker statement reached 0.39 to 0.40 seconds in the sampled
windows. Job-fact materialisation was observed up to 0.25 seconds elapsed.
The trace does not time the shorter commands or managed projection steps
individually.

The cached plan estimated about 36 job results and included clustered scans
of Jobs and OutboxMessages, with an index spool over outbox rows. The job
statement expanded the same release-proof JSON input nine times. This was a
cached estimated plan. It does not establish individual operator reads or
actual candidate cardinality.

The [18 November source-policy correction](2026-11-18-overview-refresh.md) used
a separate input read to simplify a different expensive query. Its large
logical-read reduction came from a disposable statistics experiment. The
current correction uses the same design principle and materialises exact
removed-job candidates and release proofs in indexed, connection-local tables.
It computes leased outbox delivery matches over candidate jobs first and uses
the same revision, stage, operation and lease checks. The two blocker
statements consume these inputs within the original Snapshot transaction or
legacy activity fence. Durable deletion checks still read the underlying
ownership evidence.

The paired benchmark used one immutable disposable SQL snapshot with 20,000
synthetic files, 10,000 jobs, unrelated GPU history, additional dispatched
outbox history, and an empty release-proof parameter. It alternated the old
and new query paths. All six reads returned identical sorted results of 8,001
job blockers and zero GPU blockers. The candidate figures include creating,
populating and dropping both temporary tables and marking outbox matches.

| Read | Old elapsed | Candidate elapsed | Old logical reads | Candidate logical reads |
| --- | ---: | ---: | ---: | ---: |
| First | 1,619.0 ms | 728.1 ms | 668,613 | 399,600 |
| Warm range | 682.2–706.9 ms | 272.1–298.7 ms | 668,523–668,532 | 399,555–399,560 |

Focused tests exercised exact, stale and duplicate GPU proofs, an exact leased
outbox match, dispatched and case-mismatched deliveries, and cancellation on
an already open connection. All 16 final focused SQL and browser checks
passed. An earlier broader run had a legacy consistency rejection and a
dense file-page latency failure. Each passed on a separate rerun. The
dense rerun recorded all 24 reads with a 1.84-second combined maximum.
Astra independently reviewed the attribution, ownership predicates, session
lifecycle and benchmark limits. The staging performance impact with populated
release proofs remains unmeasured. The profiling artefacts are in the
20 November Overview evidence directory under `sql-profile-20261120-193729`.

## Live acceptance

Reviewed release `ba477be9` deployed as `20261120T180340Z-ba477be901f2` without a
schema migration. Both liveness and readiness returned HTTP 200 after the
updater released its hold. All three manual refreshes advanced their browser
sample and each published through the coordinator. Server read durations were
1.85, 1.12 and 1.18 seconds. The last click joined a read already under way.
The previous three live attempts took 4.45, 4.72 and 5.02 seconds, including
one timeout.

The rewritten removed-job statement reached 380 ms elapsed and 2,470 logical
reads in sampled requests. Those are observations while the query was running,
so they are lower bounds on completed work. The dashboard showed the same
2,582 searchable files and 2,731 known files as the earlier live profile.
These are bounded live checks across two observation windows. Deployment and
correlated browser and server traces are retained under `removed-proof-release`.
