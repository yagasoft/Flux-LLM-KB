# FluxKnowledge operator guide

Open the provisioned native application at `http://127.0.0.1:5137`. The interface
shows retained application state for the trusted local user. Source registration
and operational actions remain subject to the application's confirmation and
path policies.

## Pages

| Page | Purpose |
| --- | --- |
| Overview | Inspect application, pipeline and scheduler status. |
| Sources and indexing | Register folders and inspect scan, indexed, deferred, blocked and error counts. |
| Outlook capture | Inspect and manage the provisioned local capture configuration and its status. |
| Operator actions | Review bounded actions and their durable outcomes. |
| Corpus | Browse original document identities, revisions, publication state and retained detail. |
| Pipeline records | Follow durable work, stage transitions and failure evidence. |
| Events | Inspect retained operational events. |
| Search | Search eligible retained text and follow its source identity. |
| Retained C# facts | Search syntax-derived code facts and inspect retained branch detail. |

## Register a source

In **Sources and indexing**, select **Add folder**, enter its local folder path
and display name, choose whether to include subfolders, and specify any include
or exclude rules. Select **Preview**, inspect validation and the proposed scope,
then confirm the registration. Only local fixed-drive NTFS folders accepted by
the configured root policy can be saved.

Open the source's **View** link to inspect discovery and reconciliation status.
Counts distinguish published content from content waiting for a capability or
blocked by policy. Recognised files are not necessarily searchable; consult
[file-type coverage](../file-type-coverage.md) for supported extraction routes.

## Pause, resume and delete

Use the source lifecycle controls on the source list or detail page. Review
the action's preview and confirmation before committing it. Pause stops new
admission and publication while preserving work; resume makes eligible work
available again.

Delete removes application-owned source data through a tracked operation. It
waits for in-flight work, protects shared content and updates search projections.
The source files themselves and the model store are not removed. A waiting or
blocked deletion should be diagnosed from its phase and reason; an expired
lease is not proof that an external process has terminated.

## Search and document detail

Search results use the original document identity. Office packages and VSDX
files publish as one logical document. Use corpus and pipeline detail to inspect
the selected revision and extraction outcome. Current search does not expose
all retained page/block metadata and does not provide a cited passage-read API.

English OCR requires a provisioned offline runtime. Repeated text and mixed
native/scanned regions can be incomplete. Retrying a command for a terminal
failed OCR revision returns its existing outcome; it does not reset that work.
Interactive Visio requires the logged-in desktop companion and refuses an
already-open user Visio session.

## Interpreting status

Live updates refresh projections of committed state. Reopen the relevant detail
page after reconnecting to read the current durable status. A deferred item
needs its named capability or policy condition addressed; a blocked item needs
its recorded reason inspected. Use supported operator actions where available.
Do not treat a successful metadata step as completed text extraction.

## Data and operational boundaries

Local views can show retained content, paths, hashes and parser diagnostics,
but secret-bearing material is withheld. Do not copy private results into public
issues or repository documents. See [safety](../safety.md).

Deployment, database migration, desktop task activation and model acquisition
are separate operational workflows. The UI does not grant authority to perform
those actions. See [setup](../setup.md) and [integrations](../integrations.md)
for the native operational and client contracts.
