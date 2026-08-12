# Phase 4 native Outlook ingress design

## Status and decision

Status: approved design direction; implementation starts only after review of
this written specification and an approved implementation plan.

Phase 4 for the native Windows replacement is Outlook-only. It imports full
content and attachments from classic desktop Outlook through COM in the signed-in
Windows user's session. It neither designs nor activates Gmail ingress. Existing
legacy Gmail code, configuration, surfaces and documentation are retained
unchanged; they are outside this native Phase 4 scope.

This design does not authorise a live Outlook connection, a COM process launch,
mailbox access, source migration, deployment or any mailbox mutation.

**2026-08-12 access-boundary amendment:** The user approved removal of local
operator authentication for this private-PC deployment. Outlook configuration
uses an anonymous, direct-loopback-only UI: it accepts mutation traffic only
from `127.0.0.1` or `::1`, rejects non-loopback and forwarded/proxied traffic,
and must not configure Windows, Negotiate, cookie or token authentication for
this capability. Antiforgery, the explicit UI-only mutation boundary, append-only
sanitised audit evidence, default-disabled capture and all COM/session/fencing
checks remain required. This is a deployment-topology decision, not permission
to expose `/outlook` on a LAN, through a reverse proxy or on any shared host.

## Goals

1. Let a trusted local operator configure read-only classic Outlook COM capture
   profiles and selected folders in the native application UI.
2. Export complete Outlook messages and attachments to an application-owned
   private spool, with durable provenance and atomic visibility to the corpus
   pipeline.
3. Detect new items and items moved into configured folders promptly without
   trusting Outlook events as the authoritative record.
4. Retain unsupported message parts, attachments and watched files as durable
   deferred work so a later explicitly activated processor can consume them
   without re-reading the source mailbox or filesystem.
5. Make configuration, capture state, spool health, catches-up and deferrals
   observable to the local operator without exposing raw mail or credentials.

## Boundaries and non-goals

- Classic desktop Outlook COM is the only source adapter in this phase. New
  Outlook, Microsoft Graph, Exchange Web Services, IMAP, Gmail and every other
  external ingress are excluded.
- The COM host runs outside IIS and Docker in the logged-in Windows user
  session. IIS, the app worker, Docker services and the Phase 2 deterministic
  worker never access COM.
- The Outlook profile owns mailbox authentication. No OAuth token, password,
  app secret or raw credential is stored in app configuration, SQL evidence,
  public output or the spool manifest.
- Capture is strictly read/export-only. It never moves, deletes, categorises,
  flags, marks read, replies to or otherwise mutates a mailbox item.
- The Phase 2 deterministic worker remains default-disabled and is not reused
  for Outlook or file content. Outlook source access is a separate trusted host
  capability with its own approval and operational checkpoint.
- The existing legacy Gmail capability is neither removed nor invoked. It does
  not participate in native profile UI, migration, scheduler or validation.
- No model, GPU, runtime/driver probe, network connector beyond Outlook COM,
  RabbitMQ, Docker, Vespa, processor activation or legacy migration belongs to
  this design checkpoint.
- The native `/outlook` configuration surface is anonymous only on a private,
  direct loopback IIS binding. It does not use a Windows identity or another
  application authentication scheme. IIS keeps anonymous access enabled, and
  deployment must not enable Windows/Negotiate authentication for this feature.

## Architecture

### Native Outlook COM host

An app-owned Outlook host runs as a single instance in the interactive Windows
user session. It uses the user's already configured classic Outlook profile and
is the only component permitted to instantiate COM objects. It reads only
enabled folders that a local operator has explicitly selected.

The host receives durable, profile-scoped catch-up requests and reports bounded
status to the native application. It may register COM item notifications as
wake hints but must not export directly from a notification callback. Host
restart, Outlook restart, an event-loss condition or a stale heartbeat all
request the same durable reconciliation path.

The host is not a general file or process runner. It has no model, GPU, driver,
container, queue, external-web or legacy capability. It can access only Outlook
COM, the configured private spool and the narrow application-owned local
control/data boundary defined for this adapter.

### Authoritative capture state

SQL holds the authoritative native profile, selected-folder, catch-up,
deduplication, export and deferred-capability state. Each selected folder has a
canonical Outlook identity, a display name for the local UI, an incremental
basis, an overlap-safe cursor and durable status. The canonical folder identity
is never inferred from a display name after configuration.

Each exported item has a stable profile, folder and Outlook `EntryID` identity,
content fingerprint, export state and immutable provenance. A re-observed
`EntryID` replays its prior accepted export; a conflict in the recorded source
identity or content fingerprint is blocked for operator review rather than
overwritten. Cursors advance only after the complete export and its durable
catalogue/deferred entries commit successfully.

### Ready-export ingestion boundary

Filesystem promotion and SQL ingestion are deliberately separate operations.
Once a private `ready/<export-id>` directory exists, one SQL-authoritative
ready-export ingestion transaction validates and catalogues that directory,
commits or replays the Outlook receipt, creates the required source revisions,
private artifacts and supported/deferred activities, records bounded
conflict/blocked evidence, and advances the folder cursor last.

No independent receipt operation may advance an Outlook cursor. A SQL failure
leaves the cursor unchanged and the ready directory retained for idempotent
retry; this design does not claim that filesystem promotion and SQL are one
transaction. When the existing source-revision model requires `SourceRootId`,
the Outlook profile holds an immutable private canonical source-root
provenance binding. It is never inferred from names; otherwise the established
source identity mechanism is reused without a new binding.

The default incremental basis is `last_modification_time`. It captures both
new items and older items moved into a selected folder. A small bounded overlap
is resurveyed on every catch-up and deduplicated by canonical folder plus
`EntryID`; it is not a completion inference. `received_time` may be an explicit
profile choice for folders that require receipt chronology, but it must carry a
clear UI warning that moved historical items may require a manual reconciliation.

### Event hints and reconciliation

COM `ItemAdd`, `ItemChange`, move-related and equivalent folder notifications
only create coalesced, metadata-only wake hints. A durable catch-up request is
the unit that the host claims. The catch-up sorts the selected folder by the
profile's chosen COM timestamp, applies the previous cursor minus the overlap,
then validates each candidate against durable source identity and prior export
state before reading content.

Consequently, a missed COM notification, Outlook outage, host crash or IIS
restart may delay capture but cannot advance a cursor or lose a discoverable
item. The next scheduled or operator-requested catch-up converges the folder.

## Private export and retained content

For each accepted Outlook item, the host writes only below that profile's
operator-configured private spool:

1. create `_inflight/<export-id>`;
2. write a sanitised manifest, canonical body text, the original `.msg` when
   available, and each attachment using collision-safe generated names;
3. calculate content and manifest fingerprints, record source identity and
   private relative sidecar references durably;
4. atomically promote the directory to `ready/<export-id>`; and
5. commit or replay the durable export receipt and advance the folder cursor.

The corpus observes only complete `ready` exports. An incomplete directory,
missing file, checksum mismatch, access error or SQL failure leaves the cursor
unchanged and becomes retryable or explicitly blocked evidence. Cleanup is
conservative: a later recovery may remove only verified abandoned inflight
directories that have no committed export receipt.

Raw body text, attachments, `.msg` files, Outlook identifiers, private spool
paths and COM diagnostics are never stored in public projections, audit detail,
REST/MCP/CLI output, SignalR payloads or source control. SQL retains only the
private relative sidecar reference, fingerprints, bounded classification and
sanitised lifecycle evidence needed to recover the export.

## Deferred processor model

Every retained body, attachment and watched file is classified against the
durably registered local processor capabilities. A supported, explicitly
enabled processor may create its normal activity. An unsupported artifact
creates a `DeferredCapability` record containing the artifact's immutable
provenance, fingerprint and required capability; it is pending capability, not
a failed capture.

When a suitable processor is later implemented and explicitly activated, it
claims matching deferred records from the retained private artifact. Claiming
uses the artifact fingerprint and processor-version identity as its idempotency
key. It neither contacts Outlook nor rereads a watched original file. A missing
or invalid retained sidecar blocks the deferred record with exact evidence; it
never creates a fabricated completion or silently discards the artifact.

Until such a processor exists, the local UI may show attachment filename,
declared media type, size, provenance and deferred capability. It must not
render raw attachment or mail content merely because it is retained.

## Local operator experience

The native Blazor UI is the supported local configuration surface, modelled on
the existing watched-folder flow. On the approved private PC it lets the local
interactive user:

- create, edit, pause and remove Outlook capture profiles;
- submit one explicit private canonical-folder display path for host-mediated,
  read-only resolution to a single canonical classic-Outlook folder;
- choose a private spool location subject to local path, ACL, capacity and
  writability validation;
- select `last_modification_time` or `received_time`, configure a bounded
  schedule and request a manual catch-up; and
- inspect profile, folder and spool status, last successful catch-up, retained
  export counts, deferred-capability counts, blocked reasons and host health.

The folder resolver accepts one private absolute root-to-leaf display path with
non-empty segments separated by `/` and returns exactly one resolved canonical
folder identity and safe display metadata only. It compares complete segments
using the Windows case-insensitive display-name convention, without trimming,
leaf/suffix matching or first-match fallback. It uses a bounded directed
traversal and never previews, searches or returns a mailbox hierarchy to the UI,
or exports message contents to the UI. A malformed, missing, over-limit or
ambiguous path is a bounded durable failure and cannot enable a profile, create
a capture cursor or select a different folder. The submitted path and resolved
COM identifiers remain private configuration/reconciliation data. The submitted
path may travel once as the input of the private direct-loopback Blazor circuit,
but is never echoed, broadcast, retained in circuit state or exposed through
SignalR projections, logs, REST, MCP, CLI, audit or validation records; those
surfaces use only allow-listed status/reason codes and safe display metadata.
Profile
changes are direct-loopback-only mutations protected by topology enforcement,
antiforgery and append-only sanitised audit conventions. They create durable
configuration/catch-up state but do not themselves instantiate COM or mutate a
mailbox. They do not require or record a Windows user identity.

The private browse-request schema retains the target path only for a current
request. Existing rows created before this rule, or rows with a null/invalid
target, are never backfilled or inferred from display names, profiles or folder
rows. Claim/recovery makes them unclaimable or records a sanitised terminal
failure, without a folder, cursor or capture side effect.

The Outlook-host status UI may display configured folder display names and the
configured spool location because these are needed for local operation. It may
not display raw content, attachment bytes, credentials, process identifiers,
internal `EntryID` values or unbounded COM exception payloads.

Native REST, MCP and CLI surfaces remain read-only for Outlook capture state.
They may report bounded status and sanitised counters but cannot create profiles,
browse Outlook folders, request sync, enable capture or start a host. Existing
legacy Gmail and Outlook surfaces remain untouched by this native-only phase;
their later retirement or consolidation requires a separate decision.

## Failure, recovery and safety rules

- A missing Windows session, Outlook profile, COM dependency, folder access
  permission, stale host, event loss, cancellation, unsupported message shape,
  spool capacity problem, I/O exception or database conflict creates only a
  precise blocked/retryable record and bounded operator diagnostic.
- No failure advances a cursor, marks an export complete, clears deferred work,
  removes a mailbox item, releases unrelated work or enables a processor.
- A host restart takes a new lease only after the prior lease is stale. Duplicate
  event hints and catch-up deliveries replay the same durable operation rather
  than duplicate an export or audit entry.
- A manual catch-up is a durable, profile-scoped request and is safe to repeat.
  It does not broaden folder scope or override a disabled profile.
- A configuration request that is not a direct loopback request is rejected
  before it can create browse, profile or catch-up work. Forwarded-address
  headers never satisfy this boundary.
- Configuration changes apply only to future catch-up claims. They neither
  silently reinterpret existing export provenance nor migrate legacy Gmail
  profiles.

### Opt-in private host diagnostics

The bounded non-production validation exposed that a single catch-up terminal
reason of `AccessDenied` is too coarse: it does not prove that Outlook
programmatic access was denied. The correction is a smallest host-only verbose
error switch, not a diagnostic subsystem or an application-data feature. It is
disabled by default and has the grammar
`--run-once [--verbose-com-errors [--verbose-com-errors-output <absolute-private-path>]]`.
Diagnostic flags alone never activate COM; an output argument without verbose
mode is rejected. With no output argument, raw diagnostics go only to the
directly attached interactive console error stream and are suppressed when that
stream is redirected or unattached; they are never sent through the ordinary
logger. With an output argument, the host writes a single new file below that explicit existing
application-owned private directory, or writes to the explicit private file.
The only accepted root is the existing per-user local path
`%LocalAppData%\FluxKnowledge\OutlookDiagnostics`; the resolved directory chain
must contain no reparse point, every existing segment must be owned by the
interactive user without broad read or write access, and the output must remain
below that root. An output file is always created fresh and atomically; an
existing file, hard-link or reparse destination is rejected. It
rejects repository, temporary, broad local, relative, absent-parent,
non-private and non-local targets. It creates no SQL
entity, migration, profile/configuration/UI field, API, SignalR payload, audit
record, operation receipt, background service or additional recovery worker.

When explicitly enabled, the host emits the actual COM stage, HRESULT/error
code, exception type and message only to this private local diagnostic channel.
The channel is outside the repository, SQL, spool manifests, source artifacts,
normal host logs and public application surfaces. REST, MCP, CLI status,
SignalR, UI projections, validation records and audit details continue to
exclude it. If an output location is supplied it must be an explicit
application-owned private location; the host does not infer one or persist an
otherwise normal diagnostic record.

Every COM-boundary failure has one canonical stage token:
`activation_session`, `folder_subscription`, `enumeration`, `message_open`,
`message_body`, `attachment_enumeration` or `attachment_byte_property`. A
permission or programmatic-access denial is reported only when Outlook or
Windows supplies explicit denial evidence. A generic programmatic-access prompt,
guard or approval message is not a denial. Any other `COMException` retains its
exact stage and is classified as a generic staged COM failure, not as
`AccessDenied`.

Already committed exports remain committed. This correction deliberately adds
no per-item continuation, synthetic blocked/deferred receipt or cursor-skip
behaviour: a staged COM failure follows the existing safe catch-up failure path
and cannot advance a cursor over an unrepresented item. The diagnostic pass
must keep the profile paused until focused RED/GREEN evidence and an independent
review pass, followed by separate explicit authority for a new bounded
read-only claim.

## Acceptance evidence

Implementation is acceptable only when fresh tests prove all of the following:

1. The host cannot use COM when not in the logged-in Windows user context, and
   no IIS/Docker/Phase 2 worker process can instantiate it.
2. The anonymous direct-loopback UI can configure an Outlook-only profile,
   canonical folder and private spool without a Windows identity, while every
   non-loopback or forwarded request is rejected and native REST/MCP/CLI remain
   read-only.
3. A full message with multiple attachments becomes one complete atomic private
   export with immutable provenance; a partial export is never visible to the
   corpus and cannot advance the cursor.
4. A COM event hint causes prompt capture, while a missing hint, host restart or
   Outlook restart is recovered by the durable `last_modification_time` catch-up
   with overlap and no duplicate export.
5. An older item moved into a selected folder is detected under the default
   `last_modification_time` basis. Repeated `EntryID` observation and repeated
   catch-up are idempotent.
6. The host performs no COM mailbox mutation calls, including move, delete,
   category, flag, mark-read and reply operations.
7. Unsupported attachments and watched files create visible `DeferredCapability`
   evidence, then a later explicitly enabled matching processor consumes the
   retained artifact exactly once without reopening Outlook or the original
   watched file.
8. Public/read-only status is sanitised, while the local UI displays configured
   folder and spool state without raw message content, attachments, credentials,
   internal Outlook identifiers or raw COM diagnostics.
9. Live validation uses only an explicitly approved non-production Outlook
   profile and test folder. It proves deployment, host health, full export,
   deferred retention and restart reconciliation without mailbox mutation.

## Risks and decisions retained

| Risk or decision | Treatment |
| --- | --- |
| Classic Outlook COM is unavailable in the interactive user session | Report `blocked_outlook_unavailable`; do not fall back to Graph, IMAP, Gmail or IIS COM. |
| COM events are lossy | Treat every event as a hint and make the SQL-backed cursor catch-up authoritative. |
| Older mail is moved into a capture folder | Use `last_modification_time` by default and resurvey a bounded overlap. |
| Raw mail and attachments are sensitive | Retain them only in ignored private spools/sidecars; expose only authorised local metadata and sanitised evidence. |
| A local configuration route is unauthenticated | Bind IIS and the UI to direct loopback only, reject forwarded/proxied addresses, retain antiforgery and never expose the route on a LAN, reverse proxy or shared host. |
| Future processor is absent | Retain artifact plus `DeferredCapability`; do not fail capture or invent extracted content. |
| Legacy Gmail behaviour is still needed | Preserve it untouched and outside native Phase 4; do not merge it into this design. |
| User accidentally configures a broad folder | Require one explicit canonical-folder path, resolve exactly one folder read-only, show only safe status in the local UI and support pause before a catch-up claim; no mailbox-wide discovery or multi-folder completion. |

## Review and approval gate

This design fixes the native Phase 4 boundary to read-only classic Outlook COM
capture with full private retention and deferred local processing. It excludes
Gmail from the new native implementation while preserving the legacy Gmail
system unchanged. It deliberately does not authorise code, schema, COM access,
external mailbox connection, processor activation, deployment or live
validation.

Please review this written specification. After approval, the next step is a
separate Outlook-only implementation plan; implementation and external Outlook
access remain blocked until that plan is approved.
