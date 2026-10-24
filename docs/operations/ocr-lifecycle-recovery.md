# OCR lifecycle recovery

OCR has a twenty-minute execution deadline and a 30-second ownership heartbeat.
Cleanup, result storage and scheduler settlement are separate durable facts.
The Overview shows the global OCR capacity owner and the next action even when a
particular source is selected. A missing Windows job or unverifiable owner is a
blocked condition; neither elapsed time nor a saved successful result proves
native cleanup. Normal recovery never repeats inference automatically.

The canonical incremental updater has a separate `-ApplyOcrLifecycleRecovery`
mode for an exact earlier uncertain reservation with stored output. Prepare it
with `-PlanOnly -OcrRecoveryBatchId <guid> -OcrRecoveryOperationRoot <new local
directory under I:\Codex Workspaces> -OcrDesktopClassificationsPath <private JSON>`.
The version 1 classification file contains `Entries` with exact `Path`, `Sha256`,
`DesktopIdentity`, `Reason` and supporting `Evidence`. Evidence is either
`Authenticode` with signer subject/thumbprint or `AppxPackage` with verified
package name/full name, publisher, installation location and Store/System signature
kind. Preparation verifies these facts; executable version metadata remains a
separate observation and may be absent. Unknown entries and interpreter/runtime
exclusions refuse. The final plan copies the verified entries and input digest;
apply never rereads the mutable classification file. Preparation publishes matching Web,
OutlookHost and model-free CLI payloads and writes a reviewable private plan. It
does not stop or mutate production. The plan binds database incarnation and
history, the pinned additive migration, configuration and payload hashes, exact
reservation/generation/result identity and positively classified desktop GPU
executables. Model artefacts remain under `J:\Models` and are not acquired.

After the required independent review and explicit operational authority, apply
the same plan with `-Apply -ApplyOcrLifecycleRecovery -OcrRecoveryPlanPath <path>
-ExpectedOcrRecoveryPlanSha256 <hash>`. Admissions are held before exact IIS and
companion owners stop. The privileged verifier brackets complete driver queries
with OS process incarnations, rejects eligible OCR runtimes and unclassified GPU
processes, and revalidates stopped owners and the hold before settlement. Private
observations remain beside the plan. SQL accepts that live verifier through the
maintenance entry point, preserving original result bytes and using normal
idempotent settlement; a bare receipt hash cannot release capacity.

Every boundary writes durable intent. Failures retain the hold and stopped
owners. Before candidate start intent, prior matching payloads can be restored
while stopped; after start intent, forward recovery is required. Keep the
additive schema, stored result, receipts and rollback payloads. An interrupted
attempt requires explicit `-ResumeOcrLifecycleRecovery` with the same plan/hash;
it revalidates current bindings and stopped/native state. Durable intent phases
also resume after abrupt process loss, establishing the hold and stopping exact
owners again before mutation. A partial predecessor backup can be completed only
from installed bytes matching the pinned predecessor while held/stopped; partial
activation requires verified predecessor backups. Completed replay checks exact
payloads, commit, configuration and task policy without restarting owners. Never use a Down migration or direct slot-state
update to undo a committed settlement. Unrelated historical uncertain tasks and
dead letters are outside this operation.

Ordinary code-only maintenance recognises this exact additive OCR migration after
the reviewed index migration suffix. The index schema fingerprint and original
index upgrade script remain unchanged; unknown or repeated successors refuse.
