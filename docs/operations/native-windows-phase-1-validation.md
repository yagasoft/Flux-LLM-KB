# Native Windows Phase 1 validation

## Decision

Phase 1 is verified complete as its defined disposable UTF-8 file vertical
slice. The local Windows evidence covers the real SQL Server provider,
Full-Text migration, registration-to-published pipeline, immutable USearch
generation, SQL hydration, Kestrel/Blazor projection and browser search flow.
It also includes a scoped IIS checkpoint on a site bound only to localhost:
the retained labelled record completed naturally through Publish, readiness and
search succeeded, and a checksum-verified SQL backup was created. The roadmap
records this bounded Phase 1 item at 100%.

This is not an external production deployment, a cutover, or a claim of
replacement-programme completion. The checkpoint exercised a local native SQL
database, IIS application pool and backup/verify sequence, but did not exercise
Outlook integration, model or GPU work, complete MCP/CLI parity, legacy cutover
or an externally reachable endpoint.

## Validation environment

| Field | Checked value |
| --- | --- |
| Date | 2026-07-27 |
| Environment class | Local Windows development/test host; disposable SQL catalogues and Kestrel, plus an isolated IIS checkpoint bound only to localhost; not an external production target or cutover |
| Operating system | Microsoft Windows NT 10.0.22631.0 |
| .NET SDK | 10.0.300 |
| Live-validation base | `6b92e1660a5b1247ae5355f2fed42a2b49ad3b12` |
| SQL opt-in | Process-scoped integrated localhost server-level test connection; each test created a `FluxKnowledge_Phase1Tests_<guid>` catalogue and cleanup verification found zero remaining catalogues |
| Browser opt-in | Process-scoped browser-test opt-in; Kestrel test host and local Playwright Chromium 1228 |
| IIS and SQL checkpoint | The deployed assembly matched its staged payload, target-only configuration was preserved, SQL readiness passed, and the database file mapping matched the approved canonical native locations |
| Backup checkpoint | A newly named local backup completed with `CHECKSUM`; `RESTORE VERIFYONLY ... WITH CHECKSUM` succeeded. No restore was performed. |

## Command evidence

All commands ran on 2026-07-27 in the environment class above.

| Command | State | Evidence |
| --- | --- | --- |
| `dotnet tool restore` | passed | `dotnet-ef` 10.0.10 restored successfully. |
| `dotnet restore FluxKnowledge.slnx --locked-mode` | passed after repair | The first run failed with `NU1004` because three lock files omitted already-declared package/project references. `dotnet restore FluxKnowledge.slnx` regenerated only those entries; the required locked restore then passed. |
| `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` | passed | 0 warnings and 0 errors. |
| `dotnet test FluxKnowledge.slnx --configuration Release --no-build --no-restore --filter Category!=Browser` | passed | Domain: 54 passed; Integration: 60 passed, including the disposable SQL matrix; Web: 24 passed. No selected test failed. |
| `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --no-restore --filter Category=Browser` | passed | 1 guarded browser vertical-slice test passed against Kestrel, disposable SQL and local Chromium. |
| Disposable-catalogue cleanup query | passed | Returned zero generated `FluxKnowledge_Phase1Tests_` catalogues after the test runs. |
| `git diff --check` | passed | Exit code 0; no whitespace errors before final review. |
| Localhost IIS checkpoint | passed | The isolated site stayed loopback-only; liveness and readiness returned 200, the retained record completed through Publish without replay, and the deployed payload matched staging. |
| Native SQL readiness and file mapping | passed | `validate-sql` passed and a read-only `sys.database_files` query confirmed the canonical native data and log locations. |
| SQL backup verification | passed | A new backup completed with `CHECKSUM`, and `RESTORE VERIFYONLY ... WITH CHECKSUM` reported a valid backup set. |

The lock-file repair is part of this milestone because the required locked
restore could not otherwise pass:

- `src/FluxKnowledge.Web/packages.lock.json` now records the existing
  `Microsoft.AspNetCore.App.Internal.Assets` direct reference.
- `tests/FluxKnowledge.Domain.Tests/packages.lock.json` now records the existing
  `FluxKnowledge.Infrastructure.Inference` project reference.
- `tests/FluxKnowledge.Web.Tests/packages.lock.json` now records the existing
  `FluxKnowledge.Integration.Tests` project reference.

## Acceptance matrix

The exact baseline test command for every non-browser row below was:

```powershell
dotnet test FluxKnowledge.slnx --configuration Release --no-build --no-restore --filter Category!=Browser
```

### Domain state, provenance and future GPU contract

State: passed on 2026-07-27 in the local Windows development/test environment.

- [`PublicJobStateTests.Public_states_and_wire_values_are_exactly_the_six_permanent_values`](../../tests/FluxKnowledge.Domain.Tests/Jobs/PublicJobStateTests.cs)
- [`PublicJobStateTests.Pending_is_a_derived_name_for_worker_queued_only`](../../tests/FluxKnowledge.Domain.Tests/Jobs/PublicJobStateTests.cs)
- [`PublicJobStateTests.Capacity_return_keeps_the_job_in_its_existing_queue_family`](../../tests/FluxKnowledge.Domain.Tests/Jobs/PublicJobStateTests.cs)
- [`PipelineRecordTests.New_revision_keeps_source_identity_and_links_to_prior_revision`](../../tests/FluxKnowledge.Domain.Tests/Pipeline/PipelineRecordTests.cs)
- [`PipelineRecordTests.Invariant_bearing_contracts_do_not_expose_public_constructors_or_writable_state`](../../tests/FluxKnowledge.Domain.Tests/Pipeline/PipelineRecordTests.cs)
- [`PipelineRecordTests.Completion_criteria_are_met_only_by_a_terminal_publish_transition`](../../tests/FluxKnowledge.Domain.Tests/Pipeline/PipelineRecordTests.cs)
- [`GpuMiniTaskTests.Priority_lane_is_part_of_the_durable_contract_without_activating_gpu_work`](../../tests/FluxKnowledge.Domain.Tests/Gpu/GpuMiniTaskTests.cs)

These tests verify permanent contracts only. They do not activate a GPU
scheduler, runtime or model.

### Configuration and provisioner safety

State: passed for non-mutating validation on 2026-07-27. No provisioning command
ran.

- [`SqlServerOptionsValidatorTests.Production_connection_string_cannot_attach_a_user_database`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerOptionsValidatorTests.Production_options_require_the_approved_sql_owned_file_paths`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerOptionsValidatorTests.Startup_readiness_does_not_contain_database_creation_or_file_movement`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerOptionsValidatorTests.Readiness_requires_exactly_the_canonical_data_and_log_files`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerOptionsValidatorTests.Readiness_requires_artifacts_search_text_in_the_fluxknowledge_fulltext_catalog`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerOptionsValidatorTests.Readiness_query_includes_the_active_validated_index_state`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs)
- [`SqlServerProvisionerTests.Backup_target_rejects_device_prefixed_i_drive_paths`](../../tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerProvisionerTests.cs)
- [`SchemaMappingTests.Model_uses_only_the_sql_server_provider_and_standard_server_connection`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
- [`SchemaMappingTests.Native_fixture_rejects_catalog_and_file_attachment_keys`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
- [`SchemaMappingTests.Native_fixture_fails_closed_for_unverifiable_or_i_drive_file_paths`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
- [`SchemaMappingTests.Ambiguous_create_failure_still_runs_generated_database_cleanup`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
- [`SchemaMappingTests.Vector_hash_migration_preserves_payload_integrity_and_backfills_chunk_identity`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)

The live migration tests passed against generated disposable catalogues:

[`NativeSchemaMigrationTests.Native_migration_creates_only_the_generated_phase_one_catalog`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
and
[`NativeSchemaMigrationTests.Membership_and_vector_hash_migrations_backfill_safely_and_block_snapshot_only_downgrade`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs).
The Full-Text catalog and index operations are explicitly non-transactional,
which is required by SQL Server and is guarded by
[`Initial_phase_full_text_operations_are_transaction_suppressed`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs).

### Native SQL registration, claims, leases and atomic transitions

State: passed against generated disposable SQL catalogues. The matrix covers
real registration, competing claims, lease reclamation, atomic stage
transitions and hosted dispatch:

- [`RegistrationPersistenceTests.Same_bytes_return_original_ids_and_changed_bytes_append_a_linked_revision`](../../tests/FluxKnowledge.Integration.Tests/Persistence/RegistrationPersistenceTests.cs)
- [`ClaimConcurrencyTests.Two_workers_cannot_claim_the_same_due_job`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`ClaimConcurrencyTests.Two_dispatchers_cannot_claim_the_same_due_outbox_message`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`ClaimConcurrencyTests.Claim_paths_handle_connections_reused_after_serializable_registration`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`ClaimConcurrencyTests.Expired_processing_job_is_reclaimed_under_a_new_lease_generation`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`StageTransitionAtomicityTests.Duplicate_delivery_returns_the_durable_original_transition`](../../tests/FluxKnowledge.Integration.Tests/Persistence/StageTransitionAtomicityTests.cs)
- [`StageTransitionAtomicityTests.Failure_after_artifact_write_rolls_back_the_entire_transition`](../../tests/FluxKnowledge.Integration.Tests/Persistence/StageTransitionAtomicityTests.cs)
- [`OutboxPumpTests.Hosted_pump_drains_extract_and_normalise_but_leaves_canonical_index_queued`](../../tests/FluxKnowledge.Integration.Tests/Workers/OutboxPumpTests.cs)

The command that exercised this matrix was:

```powershell
dotnet test FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-build --no-restore
```

The claim SQL resets a pooled connection to read committed isolation before
using `READPAST`, and uses `READCOMMITTEDLOCK` so the claim pattern remains
valid when read-committed snapshot isolation is enabled. The test above proves
the behaviour after a serializable registration transaction reuses the same
one-connection pool. The local IIS checkpoint then exercised retry-enabled
registration and stage transitions against the locally provisioned native SQL
database; no external production connection was used.

### Deterministic embedding and immutable USearch

State: deterministic embedding, local USearch save/reopen, SQL-authoritative
publication and rebuild checks passed. The SQL tests use generated disposable
catalogues and an app-owned temporary index root.

Passed:

- [`DeterministicTokenHashEmbeddingProviderTests.Same_normalised_text_produces_the_same_unit_vector_without_a_model_asset`](../../tests/FluxKnowledge.Domain.Tests/Indexing/DeterministicTokenHashEmbeddingProviderTests.cs)
- [`DeterministicTokenHashEmbeddingProviderTests.Ascii_alphanumeric_tokens_use_fnv1a_low_byte_dimension_and_high_bit_sign`](../../tests/FluxKnowledge.Domain.Tests/Indexing/DeterministicTokenHashEmbeddingProviderTests.cs)
- [`UsearchGenerationTests.Candidate_is_saved_reopened_validated_and_placed_as_an_immutable_generation`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Validator_rejects_a_reopened_index_with_correct_keys_but_wrong_vector_payloads`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Validator_rejects_a_reopened_single_vector_Pearson_index`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Active_reader_reuses_a_matching_generation_and_replaces_it_after_the_sql_pointer_changes`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)

Passed with disposable SQL:

- [`SqlToUsearchRebuildTests.Candidate_validation_failure_preserves_the_prior_active_pointer_and_immutable_directory`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Rebuild_after_index_root_deletion_uses_sql_membership_and_keeps_the_active_generation_searchable`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Prebuilt_snapshot_is_superseded_by_a_newer_publish_without_pointer_regression`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Concurrent_same_candidate_activation_creates_one_generation_and_membership_snapshot`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Worker_produced_vector_round_trips_through_hybrid_search_and_preserves_stale_chunk_protection`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Completed_publish_replay_does_not_duplicate_membership_or_replace_a_valid_placement`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Failed_terminal_publish_rolls_back_the_completion_flag`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)

### SQL Full-Text, ANN, RRF and hydration

State: C# RRF, query validation, REST projection and the combined SQL
Full-Text, ANN and SQL hydration test passed. The integration test waits for
the asynchronous Full-Text population before asserting the lexical result.

Passed:

- [`ReciprocalRankFusionTests.Reciprocal_rank_fusion_uses_one_over_sixty_plus_rank_and_breaks_ties_by_vector_id`](../../tests/FluxKnowledge.Domain.Tests/Search/ReciprocalRankFusionTests.cs)
- [`SearchQueryValidatorTests.Validate_rejects_scope_that_would_change_phase_one_semantics`](../../tests/FluxKnowledge.Domain.Tests/Search/SearchQueryValidatorTests.cs)
- [`SearchEndpointTests.Search_endpoint_returns_hydrated_results_not_usarch_only_rows`](../../tests/FluxKnowledge.Web.Tests/Endpoints/SearchEndpointTests.cs)
- [`SearchEndpointTests.Search_endpoint_returns_a_non_retryable_problem_for_unsupported_or_malformed_scope`](../../tests/FluxKnowledge.Web.Tests/Endpoints/SearchEndpointTests.cs)
- [`HealthEndpointTests.Ready_endpoint_delegates_to_the_canonical_non_mutating_validator`](../../tests/FluxKnowledge.Web.Tests/Endpoints/HealthEndpointTests.cs)

Passed with disposable SQL:

- [`HybridSearchIntegrationTests.Hybrid_search_hydrates_only_current_non_deleted_candidates_and_explains_contributions`](../../tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs)
- [`HybridSearchIntegrationTests.Hybrid_search_accepts_plain_text_multiword_query`](../../tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs)
- [`HybridSearchIntegrationTests.Hybrid_search_accepts_plain_text_punctuation`](../../tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs)

The SQL implementation uses `FREETEXTTABLE` for ordinary natural-language
input, rather than passing unparsed multiword text to `CONTAINSTABLE`. The IIS
checkpoint returned HTTP 200 and the retained source for both an ordinary
multiword phrase and its punctuation variant.

### REST and MCP

State: passed on 2026-07-27 for the Phase 1 REST routes and the two approved
read-only MCP tools.

- [`PipelineEndpointContractTests.Utf8_file_registration_returns_accepted_receipt`](../../tests/FluxKnowledge.Web.Tests/Endpoints/PipelineEndpointContractTests.cs)
- [`PipelineEndpointContractTests.Pipeline_records_endpoint_serialises_the_SQL_projection`](../../tests/FluxKnowledge.Web.Tests/Endpoints/PipelineEndpointContractTests.cs)
- [`ReadonlyMcpRetryExecutorTests.Read_only_search_recreates_its_operation_three_times_after_transient_failures`](../../tests/FluxKnowledge.Domain.Tests/Mcp/ReadonlyMcpRetryExecutorTests.cs)
- [`ReadonlyMcpRetryExecutorTests.Permanent_io_failure_is_attempted_once`](../../tests/FluxKnowledge.Domain.Tests/Mcp/ReadonlyMcpRetryExecutorTests.cs)
- [`KnowledgeMcpToolsTests.Brief_returns_the_legacy_temporary_unavailable_content_envelope_after_three_attempts`](../../tests/FluxKnowledge.Web.Tests/Mcp/KnowledgeMcpToolsTests.cs)
- [`KnowledgeMcpToolsTests.Search_returns_a_non_transient_tool_error_envelope_for_a_permanent_io_failure`](../../tests/FluxKnowledge.Web.Tests/Mcp/KnowledgeMcpToolsTests.cs)
- [`McpEndpointRegistrationTests.Mcp_endpoint_advertises_only_the_two_approved_read_only_tools`](../../tests/FluxKnowledge.Web.Tests/Mcp/McpEndpointRegistrationTests.cs)

This is evidence for `kb.search` and `kb.brief`, not the remaining 52 legacy
tool names, plugin installation, hooks, CLI parity or retirement readiness.

### Blazor projection, reconnect and browser vertical slice

State: projection, notification, reconnect invalidation, safe-root and the
guarded browser vertical slice passed.

Passed:

- [`OverviewProjectionTests.Overview_state_reloads_the_SQL_projection_after_a_status_event`](../../tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs)
- [`StatusEventFeedTests.Feed_delivers_a_published_status_invalidation_to_each_subscriber`](../../tests/FluxKnowledge.Web.Tests/Components/StatusEventFeedTests.cs)
- [`StatusEventFeedTests.Connection_up_publishes_a_reconnect_invalidation`](../../tests/FluxKnowledge.Web.Tests/Components/StatusEventFeedTests.cs)
- [`PipelineRecordsProjectionTests.Status_count_formats_the_visible_upper_bound`](../../tests/FluxKnowledge.Web.Tests/Components/PipelineRecordsProjectionTests.cs)
- [`PipelineRecordsProjectionTests.Pipeline_projection_names_the_job_due_time_truthfully`](../../tests/FluxKnowledge.Web.Tests/Components/PipelineRecordsProjectionTests.cs)
- [`BrowserTestRootTests.Safe_root_uses_a_non_I_drive_candidate_when_the_temp_root_is_on_I`](../../tests/FluxKnowledge.Web.Tests/Components/BrowserTestRootTests.cs)

Passed against Kestrel, disposable SQL and local Chromium:

- [`PhaseOneVerticalSliceBrowserTests.Sql_backed_utf8_registration_is_visible_in_the_interactive_search_slice`](../../tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs)

The command that exercised the guarded browser slice was:

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --no-restore --filter Category=Browser
```

The test host rendered the real static assets and antiforgery middleware, then
proved the overview page, UTF-8 registration, hosted pipeline completion,
SQL-backed pipeline-record projection and hydrated search. This guarded test
uses Kestrel; the separate localhost-only IIS checkpoint is recorded below.

### Localhost IIS checkpoint

State: passed on 2026-07-27 for the exact release base above. This was an
isolated native-Windows deployment checkpoint, not an external production
release or legacy cutover.

- Published the release payload to a fresh staging directory, stopped only the
  dedicated application pool, copied the payload, and confirmed the deployed
  assembly hash matched staging. Target-only production configuration was
  preserved.
- Kept the IIS binding loopback-only. The overview, liveness and readiness
  endpoints returned HTTP 200 after startup.
- Allowed the already-registered labelled UTF-8 record to be reclaimed by the
  hosted pump. It completed naturally through Publish; no re-registration,
  replay, acknowledgement or dead-letter action was used.
- Confirmed the REST search endpoint returned HTTP 200 and the retained source
  for both a multiword query and a punctuation variant.
- Ran the non-mutating native SQL readiness validator and verified the database
  data and log file mapping. Created a new local backup with `CHECKSUM` and
  validated it with `RESTORE VERIFYONLY ... WITH CHECKSUM`; no restore occurred.

## Whole-branch boundary review

Review range: native replacement merge base
`48b6a34670d290f4eff555951de5c41c0994c55b` through the final correction wave.

Decision: the source corrections, complete local Phase 1 acceptance matrix and
scoped localhost IIS checkpoint are verified. An external production deployment
review remains a separate operational gate; this checkpoint deliberately kept
the binding local and did not cut over legacy operation.

- The branch adds the isolated .NET modular-monolith solution and its tests. It
  does not modify legacy Python code, Docker assets, deployment scripts, user
  manuals, screenshots or DOCX assets.
- SQL Server remains the configured authority. Production code uses the SQL
  Server provider and standard server connections; `AttachDbFilename` and user
  instance strings appear only in rejection tests.
- The public six-state Job contract, source revision lineage and transactional
  stage/outbox design match the approved architecture. Disposable-SQL
  concurrency, lease, rollback and completion proof now pass.
- USearch is a derived immutable projection. Local save/reopen/validation tests
  and SQL-authoritative rebuild, failed-candidate pointer preservation and
  worker-to-hybrid-search round-trip evidence pass.
- MCP discovery exposes only the approved Phase 1 `kb.search` and `kb.brief`
  tools, with retry and error-envelope checks. This is deliberately not full
  MCP parity.
- The local test runs created and removed generated disposable SQL catalogues.
  The isolated IIS checkpoint provisioned and validated the local native target,
  including one application-pool restart and backup verification. It did not
  expose an external endpoint, process legacy data, or perform a cutover. GPU
  types remain future durable contracts only.
- No connection secret, private export, generated USearch index or private
  content is tracked by the milestone.

Review corrections:

- regenerated three stale package lock entries so locked restore is truthful;
- relabelled the SQL job due time from the incorrect “Last activity” to
  “Due / scheduled” and added a focused test;
- added direct `OnConnectionUpAsync` reconnect-invalidation and REST 400
  problem-envelope coverage.
- separated durable vector-to-chunk identity from vector-payload integrity,
  including a backfilled EF migration and a guarded worker-to-hybrid-search
  round trip;
- made terminal Publish set the durable completion flag inside the transition
  transaction and strengthened the guarded replay assertions;
- composed Web readiness through the canonical non-mutating SQL validator and
  included validated active-generation state in that contract;
- canonicalised filesystem device paths before enforcing the non-`I:` backup
  boundary.
- corrected the USearch integrity diagnostic so it names payload checksums
  rather than the now-distinct content identity.
- suppressed transactions for the SQL Server Full-Text catalog and index
  migration operations, as required by SQL Server;
- normalised pooled claim connections to read committed isolation and made their
  `READPAST` locking safe with read-committed snapshot isolation;
- added the missing application antiforgery middleware and moved pipeline-record
  ordering before the SQL projection so the live endpoint translates;
- made the guarded test wait for asynchronous Full-Text population and avoid a
  test-only concurrent uncommitted Full-Text lock while retaining the intended
  single-generation assertion.
- ran registration, transition and failure transactions inside the configured
  SQL retry execution strategy, with a fresh context per retry;
- changed raw natural-language Full-Text searching to `FREETEXTTABLE`, with
  multiword and punctuation regression coverage;
- made the serializable-registration connection-reuse test independent of the
  wall-clock time at which it runs.

Residual concerns:

- the completed Phase 2 recovery slice now cleans aged, unreferenced derived
  candidates while preserving SQL-referenced paths; derived-index orphan
  cleanup is therefore no longer a residual Phase 2 hardening item;
- the SQL hydration explanation test rejects backslash path leakage but does not
  independently assert every forward-slash form. Current explanation production
  emits rank contributions only; strengthen the guarded assertion in a later
  search hardening pass;
- the local checkpoint is intentionally loopback-only. Any external endpoint,
  identity, exposure change or legacy cutover remains separately approval-gated.

## Remaining deployment and programme gates

1. For any external production target, approve the application and
   administrator SQL identities, backup destination, IIS site/binding and
   deployment root, plus the app-owned ingress and USearch roots. This local
   loopback checkpoint does not authorise wider exposure.
2. Once an external target and its native operator procedure are approved,
   provision and migrate it, create and validate an active USearch generation,
   deploy to the approved IIS site, and live-validate readiness and rollback.
3. Complete the remaining Phase 2 strict-priority scheduling and broader
   projection work, then Phase 3 full MCP/plugin/REST/CLI parity.
4. Treat Outlook, model/GPU activation and legacy retirement as separate
   approval-gated milestones.

## Phase 2 local derived-index recovery verification

At `b263e53` on 2026-07-27, local verification confirmed recovery of a missing
or invalid active derived index from immutable SQL membership, bounded retries
for recoverable failures, safe cleanup of aged unreferenced candidates,
readiness gating until validation, and a sanitised local status projection. SQL
remains authoritative and recovery does not replace the active pointer.

- Locked restore passed. The Release `-warnaserror` build passed with 0 warnings
  and 0 errors.
- The non-browser solution test run against local disposable SQL passed: Domain
  64/64, Integration 96/96 and Web 36/36. The enabled browser Web slice passed
  1/1.
- The disposable recovery-catalogue query returned 0, and
  `git diff --check 5994007..HEAD` passed.

No new IIS, live or deployment validation occurred. This work did not restart
IIS or change external access, legacy operation, models or GPU scope.
