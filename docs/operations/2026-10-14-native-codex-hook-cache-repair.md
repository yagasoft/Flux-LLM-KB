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

## Canonical adapter location, 15 November 2026

The installed cache again contained the definitions but lacked the adapter. The
app-owned canonical adapter was intact. The reviewed cleanup manifest did not
target the cache, and the bounded available logs did not identify a removal actor.
These observations do not attribute the loss to cleanup, redeployment or a Codex
restart. Restoring the missing leaf fixed invocation but retained that dependency.

New registrations generate literal, quoted commands for the canonical adapter
beneath the configured marketplace. They no longer depend on a script copied into
the installed cache. Tests omit that copy and run all three configured events
through Windows PowerShell and PowerShell 7, including Unicode stdin and canonical
paths containing spaces, apostrophes, dollar signs, backticks and ampersands.

`scripts/deploy/upgrade-native-codex-hook-paths.ps1` upgrades the canonical and
cached definitions after checking the exact plugin identity, matching MCP
definitions, reviewed adapter hash and original definition hash. Preview is the
default. Apply replaces only those two files; a partially completed upgrade can be
resumed without changing an already canonical definition. The adapter body,
endpoint, event routing and fail-open behaviour remain unchanged. It starts no
watchdog and changes no hook trust record. Codex requires review of the changed
definitions before they run through the host.
