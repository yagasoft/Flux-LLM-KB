# Native go-live bootstrap diagnostic

## Scope

The latest one-shot run reaches the SQL bootstrap child but fails with only the
generic `native-go-live-bootstrap` step. Add a bounded diagnostic path so the
next confirmed invocation identifies the safe bootstrap phase without exposing
the connection string, SQL text, server errors, or child stderr.

## Change

1. Have the SQL child emit exactly one allowlisted reason token on failure:
   operation plus connection, SNI/load, script-parse, or one-based SQL-batch
   phase. No exception text or payload may cross the child boundary.
2. Make the parent accept only that grammar, preserve the generic fail-closed
   result for malformed output, and put the accepted token on the existing
   `native-go-live-bootstrap` record.
3. Add a narrow non-dry-run contract proving the token is bounded and that a
   forced child failure records it without leaking test connection material.

## Verification

Run the focused bootstrap contract and an independent scoped review, then run
the existing `scripts/dev/complete-feature.ps1` go-live entry point with the
already-authorised confirmations. That flow remains the sole mutating entry
point and retains all existing clean-slate safeguards.
