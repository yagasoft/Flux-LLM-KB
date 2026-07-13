# Flux Windows hook repair design

Status: approved by the user's 13 July 2026 instruction to proceed, deploy, and live-validate.

## Problem

The Flux plugin's three Windows hook commands pass a CMD-style
`%PLUGIN_ROOT%` path to PowerShell. Codex therefore sends PowerShell a
literal, non-existent file path. Once that path is corrected, the wrappers can
select an injected `FLUX_KB_PYTHON` interpreter that cannot import
`flux_llm_kb`, and they currently mask a native Python failure by returning
PowerShell success.

## Goal

Make the installed Flux plugin run its UserPromptSubmit, PreCompact, and Stop
hooks correctly on Windows while preserving the existing non-Windows command
and hook-event contracts.

## Chosen design

Use a shared PowerShell runner in the plugin package.

1. Each `commandWindows` value will start its existing event-specific wrapper
   through PowerShell's `Join-Path $env:PLUGIN_ROOT` expression rather than a
   CMD placeholder.
2. The event-specific wrappers will delegate to one common runner with their
   unchanged event name.
3. The runner will buffer the hook JSON before probing candidates, prefer
   `FLUX_KB_PYTHON` only when it can import `flux_llm_kb`, then fall back to
   `python`. It will replay the buffered JSON to the selected interpreter and
   report a non-zero status if that command fails.

The shared runner avoids three diverging implementations and keeps the public
event names and standard input/output protocol unchanged.

## Rejected alternatives

- Replace only `%PLUGIN_ROOT%` with another string: this fixes the visible
  error but leaves an invalid interpreter override and masked failures.
- Hard-code a machine-specific Python path: this would break portable plugin
  installs and production/local differences.
- Change Codex global configuration: the defect belongs to the plugin package
  and should be corrected at its source.

## Verification

- Assert all Windows hook commands use PowerShell environment resolution and
  contain no CMD placeholder.
- Execute the PreCompact wrapper under Windows PowerShell with an invalid
  override and assert it falls back to a usable Python while preserving hook
  JSON.
- Execute the wrapper with a probe-successful fake interpreter that fails the
  real command, and assert Codex receives a non-zero failure status.
- Run focused tests, the project suite, plugin installation/status checks, the
  mandated closeout script, and its deployment/live probes.

## Non-goals

- No dashboard or user-manual changes.
- No changes to non-Windows hook commands, event payload schema, or capture
  policy.
