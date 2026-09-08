# AGENTS.md

## Hard rule: central model cache and download prohibition

**The sole canonical model store for this application is `J:\Models`. Read and
apply this section before ANY model-related command. This is a cost-safety
requirement, not a preference.** It applies to every agent, task, context restart,
branch, worktree, test, benchmark, setup/deployment script, native adapter and
legacy tool used for this application.

- **Check the cache before EVERY model download, without exception.** Inspect
  `J:\Models` and its persistent inventory first. Also inspect relevant existing
  legacy/provider caches and configured cache paths before concluding that an
  artifact is missing. A new context, checkout, package version or empty default
  provider cache is never evidence that the model must be downloaded again.
- All reusable real-model weights, shards, tokenizers, vocabularies, upstream model/processor configs,
  vision projectors, ONNX/GGUF exports, inference engines and model download
  staging belong under `J:\Models`. Do not put them in a worktree, repository,
  build/publish output, deployment recovery payload, temporary directory, user
  profile cache or another drive. Provider-specific subdirectories are allowed
  only beneath this root. Do not bundle weights into application releases.
  Small synthetic test fixtures and public non-secret acquisition specifications
  may remain in Git; downloaded model payloads may not. Ordinary tests must not
  acquire real models.
- Identify artifacts by upstream repository and immutable revision, exact file,
  format/precision and SHA-256 plus byte length; verify local completeness and
  integrity. Record reusable inventory/verification receipts under `J:\Models`,
  outside Git. Model display names and mutable tags such as `main` or `latest`
  are not sufficient identities. Include tokenizer/projector/config dependencies
  and conversion inputs, tool versions and output hashes where applicable.
- **Normal application operation is offline for model acquisition.** Pass
  explicit verified local paths and use providers' offline/local-files-only
  modes. Never allow startup, import, restore, tests, model loading, fallback,
  retry, deployment or a context switch to initiate an implicit download. Audit
  wrappers and transitive loaders too; a helper command or library API is not an
  exemption. Configure cache locations per application/process; do not silently
  change other applications' machine-wide cache settings.
- **A cache miss is a stop condition, not download permission.** Before any
  explicit acquisition, present the exact missing artifacts and immutable
  revisions, caches checked, expected transfer bytes and destination. Obtain
  explicit approval for that acquisition. General feature/model-evaluation
  approval does not authorise duplicate downloads, replacement downloads or
  downloads of additional model variants. If bytes, identity or prior cache
  state are uncertain, stop and investigate; never assume a full re-download is
  acceptable.
- Reuse verified existing artifacts. When legacy cache content is useful,
  inventory it and propose a non-destructive adoption into `J:\Models` before
  copying or moving it. Preserve originals and their consumers unless separately
  authorised. Never purge, overwrite, relocate or "repair" cached models merely
  to fix a test, installation, dependency mismatch or loader error.
- Any approved acquisition must recheck the cache while holding a per-artifact
  lock, transfer only missing content, preserve/resume verified partial work
  where supported, verify the result, and publish it atomically. Record the
  acquisition receipt and actual bytes transferred. Never use force-download,
  cache-clearing or retry loops that can repeatedly fetch the same payload.
- If `J:\Models` is unavailable, unwritable, resolves outside J: through a
  link/reparse point, or lacks sufficient space, stop with an actionable reason.
  **There is no fallback cache or download destination.** Deployment, rollback,
  branch/worktree cleanup and context resets must leave this store intact.
- Before enabling any new model adapter or acquisition path, retain tests proving
  cache hits perform zero downloads, misses fail closed without authorisation,
  repeated/concurrent requests do not duplicate transfers, and an unavailable
  J: drive never causes fallback elsewhere. A written directive does not replace
  this required runtime enforcement.

## Repository Guidance

- Keep public repo content free of private memories, raw transcripts, credentials, embeddings from private material, and generated private wiki exports.
- Prefer PostgreSQL + pgvector as the primary persistence backend.
- Use MCP, CLI, and REST as first-class integration surfaces.
- Use tests for behavior changes and run focused verification before reporting completion.
- For a routine deployment, use `scripts/deploy/update-native-iis-incremental.ps1`: review its `-PlanOnly` output, then use `-Apply` only with current user authority. Do not use the full clean-slate/native GoLive path, or actions that clean the installation, configure VSS, destroy/bootstrap SQL, register Codex, or remove a legacy plugin, unless the user explicitly names and authorises that full path in the current conversation. If an advanced deployment need is not covered by the incremental updater, explain the gap and request direction before using another deployment action.
- Treat `docs/roadmap.md` and `docs/architecture.md` as durable project intent.
- After each roadmap-significant session or turn, update `docs/roadmap.md`
  `Progress %` and `Remaining Work` entries for affected roadmap items before
  closeout.
- Do not update `docs/user-guide/dashboard-user-manual.md`, its DOCX/screenshots, or rendered manual assets unless the user explicitly asks for manual updates in the current turn. Dashboard UI, automation behavior, operator API, setup-doc, or screenshot changes may ship without manual regeneration when no explicit manual request is present.
