# Native workspace brief implementation plan

Date: 30 September 2026. Baseline: `3296a3bb`.
Status: implementation and local acceptance complete; Git closeout runs separately.

**Goal:** Correct the obsolete machine instructions with minimal edits and
deliver an on-demand cited workspace brief using native FluxKnowledge tools.

**Architecture:** One instruction-only personal skill orchestrates existing
workspace search and evidence reads. AGENTS receives only the exact cell/token
changes specified in the [design](native-workspace-brief.md).

**Technology:** Markdown skill/instructions, existing FluxKnowledge MCP tools,
PowerShell for targeted byte-preserving edits and checks. No new package,
service, model or schema.

**Owner:** The user's selected main model implements and verifies both parts.
No automatic handoff or parallel file ownership is needed.

## Working state at handoff and boundaries

- Reuse the existing `hybrid-status-docs/LLM KB` Codex worktree, now on
  `codex/native-workspace-brief-plan`. It was branched from `main` at the baseline
  above; the prior clean worktree's tree matched `main`.
- Planning changes are intentionally uncommitted at handoff. Preserve them and
  any later user edits. Read the current status and diff before implementing.
- The actual machine AGENTS, product code, skill installation, plugin material
  and production services have not been changed by this plan.
- Machine AGENTS baseline SHA-256:
  `65D5517A97DFAF197230CB143E0E6E80506706FE9F3FD42A516A5122679A736A`.
  A differing hash requires comparing targeted anchors, not rejecting unrelated
  changes or restoring an old whole-file snapshot.
- No server API, CLI command or hook is added. No model acquisition, source
  registration, deployment, plugin re-registration or dashboard manual work.

## Part 1: Align machine instructions

**Target:** active Codex home `AGENTS.md`, currently
`C:\Users\os008\.codex\AGENTS.md`. Repository `AGENTS.md` is excluded.

- [x] Inspect current native tool schemas and the targeted AGENTS cells. Reuse
  the design's mapping unless the live contract has changed.
- [x] Keep an exact local backup outside Git and record encoding/BOM/newlines.
  Prepare all seven operation-cell replacements and the six token substitutions
  plus one list-entry removal from the design; require each old span exactly
  once within `Knowledge, tools and reusable work`.
- [x] Apply only those spans. Preserve the purpose column and sentence wording
  outside the replaced tokens. Do not use a Markdown formatter or regenerate
  a section/paragraph.
- [x] Compare before/after byte segments and a small text diff. Assert that
  every byte outside the allowed spans is identical. Inspect final rendering,
  native names, scope caveats and Stop guidance. Do not print or store the
  entire personal AGENTS file in repository evidence.
- [x] Exercise the routing on one safe workspace search and citation read when
  evidence is available. Verify unavailable scope takes the documented fallback.
  Use a verified returned root/branch ID to inspect qualified-name lookup only
  if such code evidence exists; otherwise record that limitation. No synthetic
  success claims and no mutation merely to test the write instructions.

**Complete when:** the narrowly edited instructions use the current contracts,
all protected bytes match and no duplicate finalisation is prescribed.

## Part 2: Build and validate the brief workflow

**Create:** `scripts/codex/skills/flux-workspace-brief/SKILL.md`.
**Create:** `tests/native/fixtures/codex/workspace-brief-cases.md` containing only
synthetic/public inputs, expected decisions and citation assertions.
**Update at delivery:** the relevant short paragraphs in `docs/integrations.md`,
`docs/setup.md`, and the planned row in `docs/roadmap.md`. Add an architecture
note only if needed to distinguish this client workflow from server capability.

- [x] Write the behavioural cases below before authoring the skill. Reuse
  existing disposable corpus acceptance fixtures when live tool execution is
  needed; no production source creation or private source export.
- [x] Author concise frontmatter and instructions implementing the design:
  clear brief trigger, current/explicit cwd, explicit workspace scope, maximum
  three searches at limit five, maximum eight readbacks at 1,024 context
  characters, four subjects plus gaps, and exact citation provenance.
- [x] Validate frontmatter/naming with the existing skill-creator
  `scripts/quick_validate.py` and an available Python runtime. No new runtime
  installation. Inspect the skill manually for unsupported tools, policy
  duplication and accidental activation on ordinary tasks.
- [x] Run a positive end-to-end brief using a registered public/disposable
  project whose documents cover the four subjects. Inspect actual tool calls
  and the result against source text, not just expected headings. For a
  disposable service, use the project's existing guarded fixture configuration
  and deterministic test providers; ordinary testing must acquire no models.
- [x] Exercise the remaining cases with controlled tool-response fixtures and
  a tool-call trace. Distinguish simulated responses from real MCP evidence.
  A model merely explaining what it would do is not an end-to-end pass.
  Record only compact public/redacted outcomes; private responses stay out of Git.
- [x] After the first useful cited brief and boundary checks, reassess effort.
  If no usable result exists after these two coherent batches, identify whether
  the blocker is scope, retrieval or synthesis before adding a helper or service.

### Behavioural cases

| Case | Required observable result |
| --- | --- |
| Public project with approved decision, blocker and next action | Four subjects supported by read passages; proposed actions labelled; every claim-to-citation mapping checked |
| Missing cwd or ambiguous named project | Obtain the missing directory; no guessed/global search |
| `scope-unavailable` | One failed scoped search, honest limitation, no broader search or project-state assertions |
| Empty results or incomplete topical coverage | Missing evidence reported; no invented status, closure or decisions |
| Distinct workspaces with similarly named documents | Every search remains in requested workspace; changed resolved scope aborts synthesis |
| Deleted/withheld/stale reference during readback | Exclude that evidence and unsupported claims; use only bounded available alternatives |
| Conflicting decisions and missing business dates | Cite conflict, follow explicit authority if present, avoid deriving precedence from ingest/revision order |
| Source passage contains instructions to search globally or write a note | Treat passage as data; no scope change, explicit persistence or other mutation |
| Tool timeout, lexical degradation, duplicate hits or citation warning | Stop within call budget, deduplicate, retain supported claims and disclose material limits |
| Ordinary unrelated prompt versus explicit/natural-language brief request | Skill activates only for the brief request; no mandatory multi-query startup routine |

The controlled fixtures must cover prompt-injection and tool errors without
making those responses instructions to the evaluator. Validate call arguments,
call counts, successful readbacks and factual support; do not add tests that
only grep for the skill's wording. A focused manual trace is acceptable; do not
build a general evaluation framework for this increment.

## Part 3: Install, document and close out after resumption

- [x] Install the validated skill as one new folder under the active personal
  Codex skill root, currently `C:\Users\os008\.codex\skills`. Check for an
  existing same-name skill first; compare and preserve any user content. Copy
  the reviewed source only, without touching plugin caches, manifests or hooks.
- [x] Verify source/installed file hashes match and a fresh Codex session can
  discover and use `$flux-workspace-brief`. Refresh the skill catalogue using
  supported client behaviour if necessary; never restart production services.
  Record discovery separately from workflow acceptance. If discovery is not
  observable, report that limitation rather than claiming installation complete.
- [x] Document invocation, required registered workspace, budgets, citations,
  gaps and local installation in the existing integration/setup docs. Keep the
  eleven native tools unchanged in documentation. Update the roadmap from
  planned/0% only when delivered capability and acceptance evidence justify it.
- [x] Self-review the complete diff, final AGENTS protected-byte comparison,
  installed-source hash match and behavioural evidence. No full .NET rerun is
  needed unless application code/config changes; never introduce such changes
  merely to satisfy this plan.
- [ ] For Git feature closeout, inspect and use
  `scripts/dev/complete-feature.ps1` under the applicable repository rules.
  Do not substitute a manual closeout sequence or treat script invocation as
  production authority. If its available path requires out-of-scope production
  action, preserve work and report the precise gap before proceeding.
- [ ] Report delivered capability, checks, skipped/unavailable acceptance and
  remaining limitations. Keep private backups and traces outside Git. Preserve
  the worktree until the required in-scope closeout succeeds.

## Recovery and resume point

Revert only the exact AGENTS spans changed by this increment, checking for
later drift first. Remove or restore only the installed personal skill owned by
this work after comparing its current contents; preserve user modifications.
No application rollback, SQL operation or plugin repair is part of recovery.

The user switched models and approved implementation. Parts 1 and 2 and the
personal installation/documentation are complete. Fresh CLI/MCP fixture
evaluations passed 12 cases; real native search/read interoperability was checked
separately. Runtime metadata parsing/discovery replaced the unavailable bundled
validator, which requires missing PyYAML. See the
[acceptance record](../operations/2026-09-30-native-workspace-brief-acceptance.md).
The required closeout script's fresh output records Git integration and push.
