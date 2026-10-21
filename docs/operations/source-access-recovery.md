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

The administrator policy binds each root's canonical path and physical identity
to one indexing SID. The helper can add only missing `0x100081` rights: read data
or list a directory, read attributes and synchronise. Directory grants are
inheritable by files and subdirectories. It preserves existing ACEs, owner,
group, inheritance/protection flags and unrelated permissions. It does not reset
ACLs or refresh old inherited permissions. IIS receives no ACL-changing rights.

An exact durable intent precedes each permission write. Physical path, ACL and
effective access are checked around the write. A confirmed result removes that
intent and allows a fresh guarded read. A write with an uncertain outcome retains
its intent and requires verification; it is not blindly replayed. Transport has
one absolute three-second request budget and never automatically replays a sent
request. Recovery is limited to 16 distinct components per read attempt; ordinary
reconciliation can continue deeper progress on a later attempt.

| Case | Behaviour after approved activation |
| --- | --- |
| Ordinary new file or subfolder | Inherits the root/directory read grant; normal guarded processing. |
| Moved file retaining older ACLs | Adds the missing read ACE on demand, preserves the old ACL, then retries. |
| Protected inheritance or synced replacement | Repairs the newly encountered physical item on demand; no recursive ACL reset. |
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
   `Path`, `IdentityFingerprint`, `InheritableRead`) and `ProtectedRoots`. Include
   application/runtime/storage, helper, Windows and model locations as protected.
   Pipe names start with `FluxKnowledge.SourceAccess.`. Hash the policy and plan.
4. Review `scripts/deploy/install-source-access-broker.ps1 -PlanOnly` using those
   exact hashes. It makes no installation or scheduled-task changes. The helper
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
policy stays pinned until the task stops for a reviewed update. Unexpected
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
