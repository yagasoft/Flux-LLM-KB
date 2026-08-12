# Native Outlook ingress validation

- Started at (UTC): 2026-08-12T11:34:06.5609091Z
- Completed at (UTC): 2026-08-12T11:34:06.7164630Z
- Loopback status codes: live=200; ready=200; status=200
- Required migrations: 20260811093501_AddNativeOutlookIngress; 20260812102333_AddOutlookBrowseTargetPath
- Outlook recovery enabled: false
- Aggregate counts: profiles=1; folders=0; exports=0; pending catch-ups=0
- Private schema policy: passed

## Bounded interactive validation (2026-08-12)

- Completed at (UTC): 2026-08-12T12:00:23.4192927Z
- Scope: one operator-selected canonical folder on the approved local test profile.
- Result: the read-only host resolved and bound exactly one folder, then
  ingested 16 exports. The configured folder reported zero deferred and zero
  blocked exports after the run.
- Catch-up terminal outcome: `AccessDenied` after the exported items were
  committed. No raw COM diagnostics, folder identifiers, content, attachment
  data, credentials or private spool location were recorded.
- Safety closeout: the profile was paused immediately after validation; no new
  host claim is eligible. No mailbox mutation was requested or performed.
