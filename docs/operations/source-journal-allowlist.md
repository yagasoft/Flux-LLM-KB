# USN journal diagnosis and allowlist changes

Open **Sources and indexing**, select a source, and open **USN journal
diagnostics** under **Source updates**. **Check journal now** uses the app's
existing read transport on an approved volume. A successful query and read
confirm current journal access. The separate source coverage and namespace
fields describe what FluxKnowledge has reconciled. A source can have partial
coverage when its durable cursor has caught up. A missing checkpoint leaves
lag unmeasured.

For an enabled source, **Reconcile source metadata** queues the existing paced
metadata sweep through the confirmed source command. It uses the normal
permissions, path guards and change detection rules. Review **Last finished
scan issues** for paths the indexer could not read or safely open. A helper
refusal points to the protected policy or indexing identity. If Windows reports
an inactive journal, an administrator needs to check journal provisioning
on the host. If Windows is deleting the journal, wait for it to finish before
checking again.

## Save from the dashboard

An installation with the separately approved Windows operator can apply an allowlist change from the dashboard. Select the desired volumes, choose **Review allowlist change**, then **Save allowlist**. Confirm the exact change in the same tab. The operator uses the signed-in Windows account, checks it against the configured operator SIDs, and rechecks the source impact before beginning. When the operation finishes, the receipt links back to the dashboard.

The dashboard and operator use distinct loopback ports. The operator is addressed as `http://localhost:<port>` so a Windows browser can use the existing Windows session under its intranet authentication policy. Use a regular browser profile that supports integrated Windows authentication. If a credential prompt appears, cancel it and check the operator configuration and browser profile with the administrator.

Save updates the app configuration and helper policy under a durable hold. IIS and the helper restart, and the held app verifies journal transport. The operation preserves the installed payloads and does not run migration DDL or retire verification requests. The receipt distinguishes an applied change, a verified reversal and a hold requiring recovery.

The one-time operator setup pins the installed payloads, helper task, database incarnation and operator SIDs. The setup script provides a hash-bound installation plan. A subsequent app release changes the relevant fingerprints and blocks further saves until the operator binding is reviewed and refreshed. Keep the protected receipt directory when planning that refresh. The plain HTTP loopback handoff assumes other local processes are trusted with the information already available in the local dashboard. Where that assumption does not hold, qualify a browser-authenticated transport before enabling Save.

## Review and download a proposal

1. Open **Manage USN journal allowlist**. Select fixed NTFS drives by their
   canonical volume identities. A volume can affect multiple saved sources.
2. Select **Review allowlist change** and check the added and removed volumes
   and the affected sources. Verify any sources whose volume mapping is
   unavailable. The source list reflects the time of the preview.
3. Confirm and download the proposal. Keep the JSON and its displayed SHA-256
   together in the operations workspace. Confirmation checks for changed
   source impact and the downloaded file pins both volume sets.
4. Use the existing reviewed incremental IIS journal activation procedure.
   Pass `-ApplyIncrementalSourceScanning`, `-SourceJournalAllowlistOnly`,
   `-SourceJournalOptionsPath <proposal.json>` and
   `-ExpectedSourceJournalOptionsSha256 <displayed SHA-256>`. Retain its normal
   target and database bindings and reviewed Web, Outlook host and helper
   payload requirements. Begin with `-PlanOnly` and check the before and after
   sets, exact configuration hashes and protected helper target.
5. Activate with `-Apply` only after the applicable independent review and
   explicit authority for the exact targets. The updater stops and restarts the
   app and helper and verifies the effective pipe, exact volume set and
   query/read access under the held IIS identity before releasing admission.

The updater rejects a proposal if its expected active set differs from the
installed configuration, or if the app and helper policies already disagree.
Refresh the dashboard after the current activation and review a new proposal.
Use the reviewed journal activation procedure to resolve a policy mismatch.
The allowlist workflow requires at least one volume.
Use the separate reviewed feature-off procedure to turn off journalling.

After activation, refresh the source diagnostics when authorised. An added
volume becomes eligible for journal coverage. Its roots still need valid
checkpoints and metadata baselines. A removed volume uses fallback source
updates. Follow the incremental updater's recovery instructions if
activation fails, retaining its hold and evidence. Use a fresh, reviewed
proposal to reverse a successfully activated allowlist change.
