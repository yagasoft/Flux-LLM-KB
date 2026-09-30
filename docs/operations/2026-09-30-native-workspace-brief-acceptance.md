# Native workspace brief acceptance

Date: 30 September 2026. Application baseline: `3296a3bb`.
Scope: machine instruction alignment and the personal Codex brief skill.

## Instruction and installation checks

The machine AGENTS patch changed seven operation cells, substituted six callable
identifier tokens and removed one obsolete list entry. All 19,927 bytes outside
those spans remained identical, including surrounding prose, purpose columns,
encoding and line endings. The original file and exact edit receipt are retained
locally outside Git. Repository AGENTS was unchanged.

- Before SHA-256: `65d5517a97dfaf197230cb143e0e6e80506706fe9f3fd42a516a5122679a736a`.
- After SHA-256: `d2c8cd95dfec392bbb02f23dd22b428129c4208d00fe281eb1a2746eddb14667`.
- Skill source/installed SHA-256:
  `466e2cdb5e2226421535e40da48d0888f4cc02b71129b228dda9ee526ec13848`.

One personal skill was installed beneath the active Codex home. Fresh Codex CLI
sessions discovered it through explicit invocation and a natural-language brief
request. Both produced a supported four-subject brief using one search and four
successful reads. The bundled skill-creator validator could not run because
PyYAML is absent; actual Codex metadata parsing/discovery and runtime use were
verified instead. No dependency was installed to remedy that tooling gap.

## Behavioural evaluation

Twelve cases passed through fresh CLI sessions and a one-off isolated stdio MCP
fixture supplying the synthetic Project Atlas evidence described in
[the cases](../../tests/native/fixtures/codex/workspace-brief-cases.md). The fixture
reproduced the native envelopes and search/read fields; it did not simulate
SQL, ingestion, publication or cryptographic evidence validation. Test processes
excluded live plugins/hooks; installed plugin material and settings were unchanged.

| Case | Searches | Reads | Verified usable reads | Result |
| --- | ---: | ---: | ---: | --- |
| Explicit brief | 1 | 4 | 4 | Four subjects and complete reference/source/revision/location details |
| Unavailable scope | 1 | 0 | 0 | Limitation only; no scope widening |
| Empty evidence | 3 | 0 | 0 | Gaps; no inferred completion or absence of issues |
| Scope change | 2 | 0 | 0 | Aborted synthesis; limitation only |
| Stale decision reference | 2 | 4 | 3 | Failed evidence excluded; decision gap |
| Conflicting decisions | 1 | 5 | 5 | Both cited; no invented governing decision |
| Injected source instructions | 1 | 4 | 4 | Instructions treated as data; no broad search or write |
| Degraded retrieval/read timeout | 2 | 4 | 3 | Deduplicated and supported partial brief with warnings |
| Readback root mismatch | 1 | 4 | 3 | Mismatched action excluded; proposals labelled |
| Natural-language brief | 1 | 4 | 4 | Skill discovered and four subjects supported |
| Ambiguous directory | 0 | 0 | 0 | Asked for the directory |
| Unrelated text edit | 0 | 0 | 0 | No brief retrieval |

Tool traces confirmed exact cwd, workspace scope, limit five, null root ID,
1,024-character read context, returned references only, no duplicate reads and
no explicit knowledge writes. Factual output and citation provenance were
reviewed against the fixture source text. One initial scope-change result
returned partial content; a focused instruction correction and rerun produced
the required limitation-only response. Raw traces remain local outside Git.

## Native read-only checks and limits

A real native workspace search of the existing public acceptance corpus
returned hybrid/ready evidence in the requested scope. Readback with 1,024
context characters preserved the selected reference, root and record revision,
and contained the expected Mercury solar-day passage. The root was
`98b1d758-d6ad-46a3-91e6-dab581300743`. This checks live tool interoperability;
the four-subject synthesis cohort above used synthetic fixture evidence.

The main repository workspace returned `scope-unavailable`. The skill reports
that gap; it does not register the repository or widen retrieval. No application
code, server contract, hook, model, database, installed plugin or dashboard
manual changed. These checks establish bounded observed skill behaviour, not
hard enforcement by a language model or live project-state accuracy.

Git closeout uses the repository's required `complete-feature.ps1` default path
with `-KeepWorktree`; its fresh per-step JSON/logs are the authority for build,
test, integration and push outcomes. No deployment is part of this increment.

The first closeout stopped at `dotnet-test-native` because the existing
canonical plugin-validator test referenced a bundled file absent from the
current installation. The failure log is
`20260930-070538-dotnet-test-native.log`. Release build had zero warnings/errors;
the other 2,729 tests passed, with 19 optional skips.

A one-line test dependency change permits the explicit process-local
`FLUXKNOWLEDGE_PLUGIN_VALIDATOR_PATH` override while preserving the existing
validation assertion. A development copy of OpenAI's
[validator](https://github.com/openai/codex/blob/84aa75204ad604bcf498df180d128181c89b2f0b/codex-rs/skills/src/assets/samples/plugin-creator/scripts/validate_plugin.py)
and adjacent helper was retained locally outside Git, pinned to upstream commit
`84aa75204ad604bcf498df180d128181c89b2f0b`. SHA-256 values are
`f4eeadb733b28b0c3e714de263a76d6542866a672f3e99bdffcf4dbcdf85e944`
for the validator and
`a6d51ce4a9a7e8f85626ff5808a467a67574e7f8cdf1167ffb467c5f67e57223`
for its identifier helper. The focused canonical-validation test then passed.
No installed skill/plugin files were restored or altered by this repair.

The subsequent full native suite passed 2,730 tests with 19 optional skips.
The next gate identified the repository's closed directory layout: skill source
now lives under `scripts/codex/skills`, and cases under `tests/native/fixtures`.
Exact retired instruction identifiers remain in the private patch receipt;
public design tables use purpose cells and list positions. The unchanged
repository contract then passed, as did all five remaining lightweight native
contract scripts. The required closeout sequence is rerun rather than replaced
with manual integration commands.
