# Task 4 report: native worker supervision safety proof

## Delivered boundary

- Native-worker audit events now have one dedicated factory that accepts only a
  closed lifecycle class, opaque instance correlation, bounded reason code and
  UTC observation time. It cannot receive process paths, pipe names, nonces,
  command lines, raw diagnostics, source data or model data.
- Both SQL lifecycle-audit write paths use that factory. The persisted operator
  event therefore has only `kind` and optional `reasonCode` details, with
  `native_worker.*` event type and opaque instance correlation.
- Schema mapping tests explicitly prove that both private worker tables omit
  pipe, nonce, command, path, raw diagnostic, source, model, settings and
  environment columns.
- The existing restart recovery proof now also checks exact uncertainty evidence
  is written without process launch, termination or lifecycle completion
  mutation.

## RED evidence

The new audit-boundary tests were written before the factory existed. Running:

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~PipelineOperatorEventIntegrationTests|FullyQualifiedName~SchemaMappingTests|FullyQualifiedName~NativeWorkerSupervisorServiceTests.Restart_with_an_active_recovery_candidate" --no-restore
```

failed at compilation with `CS0117`: `OperatorEventDraft` did not contain
`NativeWorkerLifecycle`. This was the expected missing-boundary failure.

## GREEN evidence

- The same focused test command passed: 24 passed, 2 existing SQL-backed tests
  skipped because the disposable SQL connection is not configured.
- `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~McpEndpointRegistrationTests"` passed: 7 passed, 2 SQL-backed checks skipped.
- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SqlGpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~NativeWorkerSupervisorServiceTests|FullyQualifiedName~PipelineOperatorEventIntegrationTests|FullyQualifiedName~SchemaMappingTests"` passed: 44 passed, 5 SQL-backed checks skipped.
- `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed before staging and committing.

## Files changed

- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/OperatorEventAppender.cs`
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeWorkerInstanceStore.cs`
- `tests/FluxKnowledge.Integration.Tests/Persistence/PipelineOperatorEventIntegrationTests.cs`
- `tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs`
- `tests/FluxKnowledge.Integration.Tests/Workers/NativeWorkerSupervisorServiceTests.cs`

## Commit

Implementation and tests: `ce773b7` (`test: prove native worker supervision safety`).

## Residual risk

The new persistence audit assertion and existing SQL recovery/public-projection
checks are compiled but skipped in this workspace because
`FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is unset. No substitute connection was
configured. They must run in the disposable-SQL CI or an explicitly configured
disposable test catalogue; no deployment or live migration was attempted.
