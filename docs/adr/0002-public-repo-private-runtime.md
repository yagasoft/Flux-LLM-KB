# ADR 0002: Public Repo, Private Runtime Data

## Status

Accepted.

## Context

The repository is public, but the system handles private interaction history.

## Decision

Commit only code, docs, migrations, synthetic fixtures, and example config.
Keep live memories, transcripts, embeddings from private content, generated
private wiki exports, and secrets outside Git.

## Consequences

- The project can be developed openly.
- Runtime data remains local and private by default.
- Tests must use synthetic fixtures.

