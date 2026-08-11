# Task 4 report — isolated default-disabled Outlook host

## Delivered capability

Commits `8adc879..a383d54` add a separate `net10.0-windows` STA executable and
fake-COM test project. The host uses SQL browse/catch-up claims, renews and fences
leases, records event callbacks as hints, performs overlap catch-up, promotes
private exports and reports durable completion. Restart replay deduplicates
EntryIDs and can rebind a ready export to a renewed claim without duplicating
ingestion.

COM construction is behind a factory that requires Windows, the interactive
signed-in user/session, one-instance ownership and an enabled, unexpired durable
browse or catch-up claim bound to the same host identity. The adapter contract has
no mailbox mutation member. COM activation, use, release and singleton ownership
remain on the STA dispatcher; event arguments are released after the hint is
recorded.

## Evidence

Independent remediation closed the activation-order, lease-loss, recovery,
ready-export and claim-receipt findings. Final whole-branch review then found
that ordinary private-spool/SQL failures and a normal ready-export fencing race
could escape the loop. RED tests reproduced both paths. The loop now maps only
expected spool/validation/database failures to sanitised
`RetryableHostFailure`/`IngestionFailed`, and maps `OutlookReadyExportLeaseException`
to `LeaseLost`/`LeaseStale`; neither path completes the catch-up or advances its
folder cursor.

Fresh Task 7 verification passed all 33 Outlook-host tests with no skips. The
Release solution build completed with zero warnings and zero errors.

Only fake COM was used. No classic Outlook process, real profile, mailbox,
deployment, service registration or autostart action was used.
