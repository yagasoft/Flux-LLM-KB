# Flux Windows hook repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair Windows Flux plugin hooks so they resolve the installed plugin
path, choose a usable Python interpreter, and accurately return failures.

**Architecture:** Keep the plugin's three public wrapper filenames and event
names. Add one internal PowerShell runner that owns interpreter probing,
standard-input buffering/replay, and exit-code propagation. The manifest uses
PowerShell environment access to locate the existing wrappers.

**Tech Stack:** JSON plugin manifest, Windows PowerShell, Python pytest.

## Global Constraints

- Preserve the existing non-Windows `command` values and Codex hook events.
- Do not change dashboard/manual assets.
- Run Python tests with the worktree `src` path, not the main checkout.
- Use `scripts/dev/complete-feature.ps1` for commit, merge, push, deployment,
  and live validation.

---

### Task 1: Cover the Windows hook contract

**Files:**
- Modify: `tests/test_codex_integration.py`
- Test: `tests/test_codex_integration.py`

**Interfaces:**
- Consumes: `plugins/flux-llm-kb/hooks/hooks.json`
- Produces: regression assertions for every command handler.

- [ ] **Step 1: Write failing manifest and wrapper process tests**

Add assertions that every `commandWindows` string contains
`Join-Path $env:PLUGIN_ROOT` and no `%PLUGIN_ROOT%` placeholder. Add
Windows-only subprocess tests that invoke `pre_compact.ps1` with a bad
`FLUX_KB_PYTHON` override and a probe-successful fake Python that fails the
real hook command.

- [ ] **Step 2: Run the focused test to verify it fails**

Run:

    J:\FluxLLMKB\python\python.exe -m pytest tests/test_codex_integration.py -q

Expected: failure because the current manifest has a CMD placeholder and the
current wrapper neither falls back nor propagates the fake Python failure.

### Task 2: Implement the shared Windows hook runner

**Files:**
- Create: `plugins/flux-llm-kb/scripts/invoke_hook.ps1`
- Modify: `plugins/flux-llm-kb/scripts/user_prompt_submit.ps1`
- Modify: `plugins/flux-llm-kb/scripts/pre_compact.ps1`
- Modify: `plugins/flux-llm-kb/scripts/stop.ps1`
- Modify: `plugins/flux-llm-kb/hooks/hooks.json`
- Test: `tests/test_codex_integration.py`

**Interfaces:**
- Consumes: stdin JSON payload, `FLUX_KB_PYTHON`, `python`, and the event
  name.
- Produces: the selected Python command's stdout/stderr and a non-zero status
  when the command fails.

- [ ] **Step 1: Add the runner**

The runner accepts an event name, buffers stdin before interpreter probes,
accepts an override only after `import flux_llm_kb` succeeds, falls back to
`python`, invokes `python -m flux_llm_kb.cli hook <event>`, and exits with
`$LASTEXITCODE`.

- [ ] **Step 2: Delegate each existing wrapper**

Each event-specific script invokes `invoke_hook.ps1` via `$PSScriptRoot`
and exits with the delegated result.

- [ ] **Step 3: Correct manifest path resolution**

Each `commandWindows` command invokes its existing wrapper using:

    powershell -NoProfile -ExecutionPolicy Bypass -Command "& (Join-Path $env:PLUGIN_ROOT 'scripts\<wrapper>.ps1')"

- [ ] **Step 4: Run focused tests to verify they pass**

Run:

    J:\FluxLLMKB\python\python.exe -m pytest tests/test_codex_integration.py tests/test_hooks.py -q

Expected: all selected tests pass, including the Windows-only subprocess
coverage on Windows.

### Task 3: Verify and close out

**Files:**
- Modify if required by verification: `docs/roadmap.md`

**Interfaces:**
- Consumes: branch changes and project closeout script.
- Produces: merged, pushed, deployed code plus live validation evidence.

- [ ] **Step 1: Run repository verification**

Run the focused suite, full pytest suite, and plugin syntax/status checks.
Review `git diff --check` and the final diff for scope.

- [ ] **Step 2: Update roadmap only if the repair changes a tracked item**

Inspect the affected roadmap entry and update only its progress and remaining
work if this hook repair is roadmap-significant.

- [ ] **Step 3: Run mandatory closeout**

Run `scripts/dev/complete-feature.ps1` from the branch with deployment and
live validation enabled. If it reports a failure, stop, fix only its named
failure, and rerun it.
