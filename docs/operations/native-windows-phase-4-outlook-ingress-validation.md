# Native Outlook ingress validation

- Started at (UTC): 2026-08-12T13:01:36.6359937Z
- Completed at (UTC): 2026-08-12T13:01:36.7946217Z
- Loopback status codes: live=200; ready=200; status=200
- Required migrations: 20260811093501_AddNativeOutlookIngress; 20260812102333_AddOutlookBrowseTargetPath
- Outlook recovery enabled: false
- Aggregate counts: profiles=1; folders=1; exports=16; pending catch-ups=1
- Private schema policy: passed

## Follow-up bounded diagnostic retry

- Date (UTC): 2026-08-12
- Scope: one manually requested, read-only catch-up for the already configured exact folder.
- Diagnostic mode: enabled with an explicit application-owned per-user private output path.
- Host result: completed successfully (exit code 0); no private COM diagnostic file was created.
- Aggregate result: exports increased from 16 to 18; deferred and blocked counts remained 0; the SQL-authoritative folder cursor advanced.
- Post-run safety: the profile was paused immediately after verification, leaving no further host claim eligible.

## Downstream processing check

- Date (UTC): 2026-08-12
- SQL-authoritative aggregate evidence: 18 Outlook exports have source revisions; 28 private retained artifacts and 28 source activities exist.
- Processing evidence: 18 text activities are linked to pipeline records. Ten unsupported attachment activities remain durable deferred capability work; none is blocked or discarded.
- Public-surface check: the deployed configuration projection reports Outlook disabled; loopback live and ready probes returned HTTP 200.
