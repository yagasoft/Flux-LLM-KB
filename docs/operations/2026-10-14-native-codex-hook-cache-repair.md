# Native Codex hook cache repair

An installed FluxKnowledge cache contained hook definitions referencing
`hooks/invoke-native-hook.ps1` but lacked that adapter. Both UserPromptSubmit and
Stop exited before reaching the app. The canonical marketplace contained the
adapter; repairing the marketplace alone did not restore the installed copy.

The missing cached file was restored without replacing existing files or changing
hook definitions, plugin trust, application payloads or services. A distinctly
labelled synthetic Stop saved its exact body and one completed native receipt.
Identical replay preserved one note and one receipt. This does not backfill earlier
missing summaries or establish that a later Codex-triggered Stop has run.

`scripts/deploy/repair-native-codex-hook-cache.ps1` makes that bounded repair
repeatable. Pass explicit source/cached plugin roots and the independently reviewed
SHA-256 of the canonical adapter. With no `-Apply`, it reports the plan and writes
nothing. Apply restores only a missing adapter. Matching existing bytes are a
no-op; conflicting files, mismatched plugin identities or hook/MCP definitions,
hash mismatches and reparse points are refused. It never invokes the hook or
restarts the application. Save its JSON receipt with operational evidence.

Production repair requires fresh, explicit user approval under `AGENTS.md`.
All automated checks use a disposable marketplace and installed-cache fixture,
without production paths or endpoints. The focused regression covers planning,
exact restoration, replay, preservation and refusal cases. Normal startup and
plugin status remain read-only.
