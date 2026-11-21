# Automatic Windows source access

Automatic recovery is disabled by default. It separates the indexing process's
read access from an administrator-controlled helper's narrowly scoped ACL writes.
Trusting a source does not make its files readable to an IIS application pool.
Moved files and sync replacements can retain permissions belonging to an older
Windows account, even when their parent folder grants access.

## Behaviour and boundaries

Source registration and Preview remain read-only. They report the Windows SID
performing the check. Successful interactive administrator access does not prove
that the indexing account can read the source.

Normal discovery and source capture first attempt the existing guarded read.
Only a classified native access-denied result can request recovery. The client
sends the exact denied component inside the admitted source to a local named
pipe. The helper authenticates the actual caller token and checks effective read
access using that token, including deny-only and restricted groups.

The pipe grants the exact indexing SID only data read/write, attributes read,
descriptor read and synchronisation (`0x120083`). Windows requires attributes
read even when the client's explicit desired-access mask omits it. Tests use a
caller that cannot rely on the pipe owner's administrative grant. Instance
creation, write attributes and permission/owner changes remain excluded. The
persistent server disconnects completed or broken connections before accepting
another; an empty or owner-refused request grants no source access.

The administrator policy binds each root's canonical path and physical identity
to one indexing SID. The default recovery adds only missing `0x100081` rights for
reading data or listing a directory, reading attributes and synchronising.
Directory grants can be inheritable. The default preserves existing ACEs, owner,
group and inheritance/protection flags. IIS receives no ACL-changing rights.

In the dashboard, open a saved filesystem source and select **Manage automatic
file access repair**. Confirm enable or disable in the Windows-authenticated local
operator. New sources start with this permission off. The operator saves a
protected grant for the exact registration and physical folder. Saves take effect
without redeploying or restarting the app or helper. Disabling prevents future
inheritance edits and leaves repaired ACLs in place. Keep the operator review
link if the source will be deleted, moved or taken offline. The grant can still
be revoked from that link. A new registration for the same folder starts off.

An ordinary single-link file must fail a guarded read with
native access denied, and the helper must independently confirm both effective
read denial for the authenticated indexer and a protected DACL. It projects the
exact file's inherited DACL from its held immediate parent, preserves explicit
ACEs and owner/group, and refuses any applicable read deny or projected ACL
that still fails the read check. The file-only DACL write can change rights for
other accounts through parent inheritance. A OneDrive placeholder is eligible
only under the exact enabled registration, after a guarded read returns access
denied against the same NTFS file ID. The helper confirms the registered OneDrive
sync root and checks that on-disk ranges cover every byte through the end of the
file. It holds the exact file against competing data writes while checking and
applying the ACL. These metadata checks do not download content. Cloud-only,
partially resident, empty and indeterminate placeholders stay deferred. Other
reparse points remain excluded. Existing directory recovery is unchanged.

The next automatic metadata reconciliation uses the same guarded file read.
It can recover a previously deferred file during that scan, including one with
unchanged size and modification time, without rehashing content when existing
metadata can be reused. Partial journal coverage alone grants no repair
authority. Dispatch on a denied file in an incremental scan requires a current
source configuration, owned scan and accepted recovery generation.

An exact durable intent precedes each permission write. Physical path, ACL and
effective access are checked around the write. A confirmed result removes that
intent and allows a fresh guarded read. An uncertain result retains the intent
and is recorded as a scan issue without another capture attempt in the same
scan. The helper compares the held file and parent around the write. Windows
does not make that comparison and write atomic against external ACL editors. Transport has
one absolute three-second request budget and never automatically replays a sent
request. Recovery is limited to 16 distinct components per read attempt; ordinary
reconciliation can continue deeper progress on a later attempt.

| Case | Behaviour after approved activation |
| --- | --- |
| Ordinary new file or subfolder | Inherits the root/directory read grant; normal guarded processing. |
| Moved file retaining older ACLs under the default policy | Adds the missing read ACE on demand, preserves the old ACL, then retries. |
| Inheritance disabled on an ordinary file in an opted-in source | Enables inheritance on the exact file when the parent grants effective read access and the file passes the other safeguards. |
| Repeated repair or already readable item | Confirms actual access without another write. |
| Effective explicit/inherited deny, restricted-token exclusion or unsupported ACL | Preserves the restriction and reports administrator review. |
| Cloud-only, unavailable or actively changing content | Reports the provider/stability problem; does not download, hydrate or edit content. |
| Junction, symbolic link, unsupported reparse point, hard-linked file or path escape | Refuses recovery; path and physical-identity checks remain active. |
| Replaced source root, denied ancestor outside the approved root, or new source | Requires a reviewed root policy/parent-access decision. A matching text path alone is insufficient. |
| Helper unavailable or permission write itself denied | Reports the helper/item failure and leaves ordinary reconciliation retryable. |

Only source preflight can recover permissions. Publication-fence revalidation,
retained artifact reads and Preview cannot change permissions. A successful
discovery clears its old per-item evidence; existing current-revision projections
exclude superseded failed outcomes. A later successful scan replaces old scan
issues. Current unresolved issues retain actionable reason codes, rather than
being dismissed because a repair request was sent.

## Prepare and approve an installation

Use isolated fixtures to verify the pipeline before requesting production
authority. A source folder grant, privileged task, helper policy and application
activation need explicit approval for their exact target and scope.

1. Build the CLI and prepare a complete model-free payload manifest containing
   the immutable commit, every relative filename, byte length and SHA-256.
2. Run `source-access plan-root-read --request <json>` with a closed JSON request
   containing `RootPath`, `IndexerSid` and `ProtectedRoots`. This captures the root
   physical identity and prior ACL without changing it. The plan explicitly says
   `ActualIndexerReadVerified: false`; the helper's real caller token and supported
   indexing reads establish that later.
3. Prepare version 1 policy JSON with `PipeName`, `IndexerSid`, `Roots` (each has
   `Path`, `IdentityFingerprint`, `InheritableRead` and optionally
   `RepairFileInheritance`, which defaults to false) and `ProtectedRoots`. Include
   application/runtime/storage, helper, Windows and model locations as protected.
   Pipe names start with `FluxKnowledge.SourceAccess.`. Hash the policy and plan.
4. Review `scripts/deploy/install-source-access-broker.ps1 -PlanOnly` using those
   exact hashes and verify the listed `file_inheritance_repair_roots`. It makes no
   installation or scheduled-task changes. The helper
   requires a new private directory directly under a fixed NTFS volume root.
5. After approval, `-Apply` additionally requires
   `-ConfirmPrivilegedHelper -ConfirmSourceRootReadGrant`. The installer exclusively
   creates and pins the protected namespace, verifies the sealed payload and
   runtime overrides, applies one exact root grant, then registers and starts one
   protected SYSTEM startup task. An existing installation is never overwritten.
6. Separately activate the unprivileged IIS client through the approved incremental
   updater using `-SourceAccessPipeName <approved pipe>`. It changes only
   `SourceAccess:Enabled=true` and `SourceAccess:PipeName` under the stopped-worker
   deployment hold. `-DisableSourceAccessRecovery` disables the client through the
   same path. PlanOnly shows the exact prior/candidate configuration hashes without
   printing configuration contents. Exact old bytes are retained privately and
   restored before predecessor startup if activation fails before hold release;
   unknown drift retains the hold. There is no database migration or model acquisition.
   If historical GPU uncertainty prevents the ordinary drain, an explicitly
   reviewed version 2 `OfflineCodeMaintenanceReceipt` may bind this activation
   under `RecoverStoppedPool`. It requires fresh physical writer-exit proof and
   exact source-access before/after hashes, pipe, helper payload/policy, protected
   filesystem/task descriptors and root identity. These bindings are rechecked
   before activation and hold release. Version 1 remains code-only. A start intent
   consumes the stop proof; ambiguous startup needs a fresh stopped recovery.
   Neither route settles historical GPU records or admits arbitrary configuration.
7. Validate read-only Preview and a bounded set of actual indexing-account reads
   through normal discovery, capture, publication and supported readback. Do not
   start whole-source ingestion to prove access recovery.

The root grant only affects that root's ACL and future inheritance. Existing
children are not rewritten. On-demand grants for current and future descendants
are a separate continuing authority in the approved helper policy; an earlier
one-off stale-permission batch is not that authority.

## Recovery and rollback

The helper policy, payload and journal are writable only by SYSTEM and
administrators, with no reparse points or multi-link policy/journal files. The
policy stays pinned until the task stops for a reviewed update. The dashboard
and helper need a coordinated initial payload update, and the protected local
operator needs its reviewed package refresh. Use the canonical incremental
updater’s `-RefreshSourceAccessHelper` mode and the reviewed `-PlanOnly` digest
for the initial payload change. The release holds both writers until the helper
and the held IIS process confirm transport. Refresh and verify the installed
operator package once the payload release has settled. Subsequent per-source saves preserve
the journal allowlist and the installed legacy repair policy. Existing static
inheritance opt-ins remain managed by the protected installation. Unexpected
ownership, permissions, identities or pending intents fail closed.

Disable the client through a reviewed incremental update and stop the exact helper
task to stop further automatic grants. Preserve the installation, payload and
pending intents. Existing source read grants remain; removing them, restoring an
old ACL or removing inherited rights from later descendants needs a separately
reviewed revocation plan. Ordinary deployment rollback still follows the
incremental updater's paired payload and hold-release rules.

The local regression suite covers effective token checks, ordinary inheritance,
protected/moved/replaced files, repeated/concurrent repairs, denied access,
uncertain writes, path escapes, unavailable content, pipe ownership/deadlines and
the discovery-to-retained-readback path. The isolated native installation contract
also covers preclaimed folders/junctions, generic ACL rights and observational
planning. These tests do not establish production activation or full-source
indexing completion.
