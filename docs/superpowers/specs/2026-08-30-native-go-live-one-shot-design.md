# Native go-live one-shot clean-slate design

## Goal

Replace deployment-only resumable native go-live with a one-shot, fail-closed clean-slate flow. This does not change native application behaviour, retained-data protections, or Phase 5 processing.

## Admission and failure model

`scripts/dev/complete-feature.ps1` remains the sole closeout/go-live entry point. The existing destructive confirmations remain mandatory. A go-live invocation is admitted only when the canonical root `I:\FluxKnowledge` and target application database are absent, or when that same confirmed invocation wipes them first.

Every existing root, database, journal, marker, SQL object, certificate, procedure, partial publish, or other deployment state is disposable only through that confirmed clean wipe. The deployment flow never reads ownership markers, determines provenance, adopts historic layouts, repairs, resumes, or recovers state.

An interruption, cancellation, failed validation, failed command, or uncertain observation fails closed. A later go-live is a new explicitly confirmed invocation that wipes the root and target database again. There is no deployment recovery state.

## One-shot sequence

1. Validate confirmed one-shot arguments, canonical root, target catalogue name, fixed hierarchy and no legacy/Flux path.
2. Confirm both root and target database are absent, or wipe both under the same invocation before proceeding.
3. Create the fixed `I:\FluxKnowledge` hierarchy with existing no-follow/handle-safe primitives.
4. Provision the target catalogue, bootstrap only the required procedures, and create the application identity with exactly `CONNECT`, `db_datareader`, and `db_datawriter`. Reject all additional database or server authority.
5. Publish the staged immutable payload and write the canonical production configuration through the no-follow writer. Do not expose the integrated connection value.
6. Validate the stored configuration bytes and run the real strict-production Web composition validation without starting a listener. Require retained-only ingress, empty initial roots, and disabled Outlook, workers/schedulers, model/GPU/media/FFmpeg/network parsing and all Phase 6 capabilities.
7. Clear bootstrap state before any child process, start IIS only after successful validation, and perform the allowed local probes.

## Removed deployment machinery

Remove deployment-only journal/session/store/factory types; CAS/fencing; phases and recovery prefixes; root markers/adoption records/proofs; historic-root adoption; cross-run authority recovery/re-authorisation; resume/repair/replay paths; recovery SQL lifecycle paths; and deployment crash/recovery tests.

No deployment code may use `I:\FluxKnowledge\Recovery` for coordination. The ordinary fixed hierarchy may contain an empty `Recovery` directory only where required by the non-deployment native layout.

The removal explicitly excludes ordinary application functionality such as derived-index or worker recovery; those systems stay unchanged.

## Safeguards retained

- Existing explicit destructive confirmations and `complete-feature.ps1` as the only entry point.
- Fixed canonical hierarchy and no-follow/handle-safe filesystem boundaries.
- No secrets in durable output, diagnostics, configuration validation output, or child environments.
- Exact least-privilege SQL application identity and fail-closed authority verification.
- No Flux or legacy integration paths, Outlook activation, model/runtime download, GPU work, FFmpeg, network parsing, or Phase 6 activation.
- Production configuration and disabled-capability validation before IIS starts.
- No live wipe, database action, SQL/IIS/VSS/Codex action, marketplace registration, merge, push, or deployment under this implementation authority.

## Acceptance criteria

- A one-shot dry-run accepts only absent state or an explicitly confirmed same-invocation wipe.
- Existing/unknown state without wipe confirmation is rejected without mutation.
- No deployment journal, marker, recovery/adoption/replay API or test remains.
- SQL app identity has precisely the required authority and no residual bootstrap lifecycle authority.
- Published configuration is canonical, no-follow validated, secret-safe, and composition-validated before IIS start.
- Focused tests, full native Release suite, locked restore, zero-warning Release build, and EF pending-model verification pass.
