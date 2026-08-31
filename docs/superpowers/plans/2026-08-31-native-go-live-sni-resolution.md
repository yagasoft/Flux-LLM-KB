# Native go-live bootstrap SNI resolution repair

## Context

The confirmed one-shot closeout passed all branch and main verification and
committed `157b186`, then failed at `native-go-live-bootstrap` before any SQL
command or clean-slate admission. IIS was replaced first by design; the old
root and catalogue were not wiped or changed.

Read-only reproduction proved that the fresh PowerShell SQL child loaded the
published managed `Microsoft.Data.SqlClient` assembly but could not resolve
its architecture-specific native SNI library. Supplying only the payload's
`runtimes\win-x64\native` directory to that child made a canonical integrated
read-only `SELECT 1` succeed.

## Constraints

- Keep the one-shot fail-closed model. Add no recovery, resume, adoption,
  journal, state machine or new live entry point.
- Preserve the exact managed dependency and checksum-bound published payload;
  do not download or activate anything.
- Do not use Flux tools, mutate IIS/VSS/SQL/the root during tests, or expose
  connection/credential data.
- The corrected smoke must be fresh-child, bounded, read-only and use only a
  synthetic/read-only SQL probe.

## Task 1: bind the exact native SqlClient runtime asset to the child

1. Add a test that fails because the existing fresh child lacks native SNI
   resolution, and verifies the corrected child can open its canonical local
   connection and run `SELECT 1` without bootstrap SQL execution.
2. Resolve the published native directory for the current Windows process
   architecture, verify its exact SNI asset is present, and expose that
   directory only to the fresh SQL child’s native DLL search path.
3. Keep all child output secret-free and bounded. Run RED then GREEN and the
   existing closeout/bootstrap/native contracts. Commit the repair.

## Review and verification

- Independent task and whole-branch review must confirm no live SQL lifecycle
  command appears in the regression, architecture selection fails closed,
  native resolution reaches only the child and the one-shot model remains
  unchanged.
- Before a new invocation, run locked restore, zero-warning Release build,
  focused native and disposable integration checks, full native Release suite,
  EF no-pending-model verification and `git diff --check`.
- The next live execution is a new `scripts/dev/complete-feature.ps1`
  invocation with fresh full confirmations; no prior operation is resumed.
