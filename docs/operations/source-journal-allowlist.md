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

## Review a volume change

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
