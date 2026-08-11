# Task 6 report — disabled recovery and deployment contract

## Delivered capability

Commits `e945746..0c2e085` add default-disabled durable recovery, a plan-only
deployment target, a sanitised post-deploy validator and closeout gates. Recovery
reads only durable hint and stale-lease candidates; release is bound to the exact
catch-up ID, profile, fencing token and observed expiry. Pending hint filtering
occurs before the bounded batch limit.

The Web host registers no COM or process service. When recovery is explicitly
enabled it registers only SQL-backed recovery. The configuration projection
honours normal environment/command-line precedence, emits only the effective
enabled state and exits before application or COM-host construction. Deployment
keeps Outlook disabled, starts no host and registers no Windows Service.

The closeout sequence contains focused legacy Gmail regressions and an owned-path
diff guard immediately before feature and main commit boundaries. The validator
allows only timestamps, loopback status, migration IDs, disabled state, aggregate
counts and private-schema policy in a future validation record.

## Evidence

Fresh Task 7 verification passed recovery 3/3, the native closeout dry-run
contract, the native deployment-plan contract, 117 focused Gmail tests and the
legacy Gmail diff guard. EF reported no pending model changes.

No deployment, migration, IIS change, COM activation, Outlook connection,
mailbox action or validation-record creation occurred.
