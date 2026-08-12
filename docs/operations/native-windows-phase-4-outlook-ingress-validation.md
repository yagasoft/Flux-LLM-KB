# Native Outlook ingress validation

- Started at (UTC): 2026-08-12T16:31:27.1094278Z
- Completed at (UTC): 2026-08-12T16:31:27.3054250Z
- Loopback status codes: live=200; ready=200; status=200
- Required migrations: 20260811093501_AddNativeOutlookIngress; 20260812102333_AddOutlookBrowseTargetPath
- Outlook recovery enabled: false
- Aggregate counts: profiles=1; folders=1; exports=18; pending catch-ups=0
- Private schema policy: passed

## Disabled-profile scheduled-task probe

- Date (UTC): 2026-08-12
- Installed policy: ready and enabled; hidden; one fixed action; limited interactive principal; one logon trigger and one 15-minute repeating trigger; `IgnoreNew`; 14-minute execution limit.
- Disabled-state precondition: enabled profiles=0; pending or running browse requests=0; pending or running catch-ups=0.
- Scheduler result: the task entered running state, returned to ready, advanced its last-run time and completed with result 0 in 3.381 seconds.
- No-COM evidence: Outlook process count remained one before and after, with no new Outlook process; enabled-profile and pending-work counts remained zero after the run. The verified durable-work gate therefore had no COM-eligible work.
- Safety state: the Outlook profile remained disabled; no profile enablement or mailbox-validation action was performed.
