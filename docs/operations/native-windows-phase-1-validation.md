# Native Windows Phase 1 validation

## Decision

Phase 1 has a buildable local UTF-8 vertical slice and passing non-opt-in
evidence. It is not verified complete. The disposable SQL Server integration
suite and the SQL-backed browser vertical slice were not run because the
environment did not provide the required disposable-SQL opt-in. No target
database, IIS site, backup, Outlook integration, model, GPU work, complete MCP
surface or legacy cutover was exercised or claimed.

The roadmap records Phase 1 at 80%. The weighting is 60 points for the
implemented local slice, 20 for the reproducible non-opt-in build and tests, 15
for the still-unrun disposable SQL matrix and 5 for the still-unrun guarded
browser test.

## Validation environment

| Field | Checked value |
| --- | --- |
| Date | 2026-07-27 |
| Environment class | Local Windows development/test host; not IIS and not a production or target SQL environment |
| Operating system | Microsoft Windows NT 10.0.22631.0 |
| .NET SDK | 10.0.300 |
| Final-fix review base | `eb5ce1205e752275ff8ae26a0a8b65512ea55aa3` |
| SQL opt-in | `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` absent |
| Browser opt-in | `FLUXKNOWLEDGE_BROWSER_TESTS` absent |
| Target-drive rule | An `I:` drive exists; no command in this validation accessed or changed it |

## Command evidence

All commands ran on 2026-07-27 in the environment class above.

| Command | State | Evidence |
| --- | --- | --- |
| `dotnet tool restore` | passed | `dotnet-ef` 10.0.10 restored successfully. |
| `dotnet restore FluxKnowledge.slnx --locked-mode` | passed after repair | The first run failed with `NU1004` because three lock files omitted already-declared package/project references. `dotnet restore FluxKnowledge.slnx` regenerated only those entries; the required locked restore then passed. |
| `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` | passed | 0 warnings and 0 errors. |
| `dotnet test FluxKnowledge.slnx --configuration Release --no-build --filter Category!=Browser` | passed with explicit skips | Domain: 54 passed; Integration: 30 passed and 23 SQL-opt-in tests skipped; Web: 23 passed. No selected test failed. |
| `git diff --check` | passed | Exit code 0; no whitespace errors before final review. |
| `git status --short` | passed | Showed only the four blocking-review corrections, their focused tests, the generated EF migration and the affected evidence files before final review. |

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
dotnet test FluxKnowledge.slnx --configuration Release --no-build --filter Category!=Browser
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

The live migration tests
[`NativeSchemaMigrationTests.Native_migration_creates_only_the_generated_phase_one_catalog`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
and
[`NativeSchemaMigrationTests.Membership_and_vector_hash_migrations_backfill_safely_and_block_snapshot_only_downgrade`](../../tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs)
were skipped because the disposable SQL opt-in was absent.

### Native SQL registration, claims, leases and atomic transitions

State: not run. The tests were discovered by the baseline command and reported
as skipped, not passed:

- [`RegistrationPersistenceTests.Same_bytes_return_original_ids_and_changed_bytes_append_a_linked_revision`](../../tests/FluxKnowledge.Integration.Tests/Persistence/RegistrationPersistenceTests.cs)
- [`ClaimConcurrencyTests.Two_workers_cannot_claim_the_same_due_job`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`ClaimConcurrencyTests.Two_dispatchers_cannot_claim_the_same_due_outbox_message`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`ClaimConcurrencyTests.Expired_processing_job_is_reclaimed_under_a_new_lease_generation`](../../tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs)
- [`StageTransitionAtomicityTests.Duplicate_delivery_returns_the_durable_original_transition`](../../tests/FluxKnowledge.Integration.Tests/Persistence/StageTransitionAtomicityTests.cs)
- [`StageTransitionAtomicityTests.Failure_after_artifact_write_rolls_back_the_entire_transition`](../../tests/FluxKnowledge.Integration.Tests/Persistence/StageTransitionAtomicityTests.cs)
- [`OutboxPumpTests.Hosted_pump_drains_extract_and_normalise_but_leaves_canonical_index_queued`](../../tests/FluxKnowledge.Integration.Tests/Workers/OutboxPumpTests.cs)

The exact approved test command remains unrun:

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-build
```

It requires a caller-supplied server-level connection to a generated disposable
catalog. A production, target or `I:` connection is not an acceptable
substitute.

### Deterministic embedding and immutable USearch

State: deterministic embedding and local USearch save/reopen checks passed;
SQL-to-USearch publication and rebuild checks were not run.

Passed:

- [`DeterministicTokenHashEmbeddingProviderTests.Same_normalised_text_produces_the_same_unit_vector_without_a_model_asset`](../../tests/FluxKnowledge.Domain.Tests/Indexing/DeterministicTokenHashEmbeddingProviderTests.cs)
- [`DeterministicTokenHashEmbeddingProviderTests.Ascii_alphanumeric_tokens_use_fnv1a_low_byte_dimension_and_high_bit_sign`](../../tests/FluxKnowledge.Domain.Tests/Indexing/DeterministicTokenHashEmbeddingProviderTests.cs)
- [`UsearchGenerationTests.Candidate_is_saved_reopened_validated_and_placed_as_an_immutable_generation`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Validator_rejects_a_reopened_index_with_correct_keys_but_wrong_vector_payloads`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Validator_rejects_a_reopened_single_vector_Pearson_index`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)
- [`UsearchGenerationTests.Active_reader_reuses_a_matching_generation_and_replaces_it_after_the_sql_pointer_changes`](../../tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs)

Not run because they require disposable SQL:

- [`SqlToUsearchRebuildTests.Candidate_validation_failure_preserves_the_prior_active_pointer_and_immutable_directory`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Rebuild_after_index_root_deletion_uses_sql_membership_and_keeps_the_active_generation_searchable`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Prebuilt_snapshot_is_superseded_by_a_newer_publish_without_pointer_regression`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Concurrent_same_candidate_activation_creates_one_generation_and_membership_snapshot`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Worker_produced_vector_round_trips_through_hybrid_search_and_preserves_stale_chunk_protection`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Completed_publish_replay_does_not_duplicate_membership_or_replace_a_valid_placement`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)
- [`SqlToUsearchRebuildTests.Failed_terminal_publish_rolls_back_the_completion_flag`](../../tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs)

### SQL Full-Text, ANN, RRF and hydration

State: C# RRF, query validation and REST projection passed; the combined SQL
Full-Text, ANN and SQL hydration test did not run.

Passed:

- [`ReciprocalRankFusionTests.Reciprocal_rank_fusion_uses_one_over_sixty_plus_rank_and_breaks_ties_by_vector_id`](../../tests/FluxKnowledge.Domain.Tests/Search/ReciprocalRankFusionTests.cs)
- [`SearchQueryValidatorTests.Validate_rejects_scope_that_would_change_phase_one_semantics`](../../tests/FluxKnowledge.Domain.Tests/Search/SearchQueryValidatorTests.cs)
- [`SearchEndpointTests.Search_endpoint_returns_hydrated_results_not_usarch_only_rows`](../../tests/FluxKnowledge.Web.Tests/Endpoints/SearchEndpointTests.cs)
- [`SearchEndpointTests.Search_endpoint_returns_a_non_retryable_problem_for_unsupported_or_malformed_scope`](../../tests/FluxKnowledge.Web.Tests/Endpoints/SearchEndpointTests.cs)
- [`HealthEndpointTests.Ready_endpoint_delegates_to_the_canonical_non_mutating_validator`](../../tests/FluxKnowledge.Web.Tests/Endpoints/HealthEndpointTests.cs)

Not run:

- [`HybridSearchIntegrationTests.Hybrid_search_hydrates_only_current_non_deleted_candidates_and_explains_contributions`](../../tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs)

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

State: projection, notification, reconnect invalidation and safe-root tests
passed; the browser vertical slice was not run.

Passed:

- [`OverviewProjectionTests.Overview_state_reloads_the_SQL_projection_after_a_status_event`](../../tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs)
- [`StatusEventFeedTests.Feed_delivers_a_published_status_invalidation_to_each_subscriber`](../../tests/FluxKnowledge.Web.Tests/Components/StatusEventFeedTests.cs)
- [`StatusEventFeedTests.Connection_up_publishes_a_reconnect_invalidation`](../../tests/FluxKnowledge.Web.Tests/Components/StatusEventFeedTests.cs)
- [`PipelineRecordsProjectionTests.Status_count_formats_the_visible_upper_bound`](../../tests/FluxKnowledge.Web.Tests/Components/PipelineRecordsProjectionTests.cs)
- [`PipelineRecordsProjectionTests.Pipeline_projection_names_the_job_due_time_truthfully`](../../tests/FluxKnowledge.Web.Tests/Components/PipelineRecordsProjectionTests.cs)
- [`BrowserTestRootTests.Safe_root_uses_a_non_I_drive_candidate_when_the_temp_root_is_on_I`](../../tests/FluxKnowledge.Web.Tests/Components/BrowserTestRootTests.cs)

Not run:

- [`PhaseOneVerticalSliceBrowserTests.Sql_backed_utf8_registration_is_visible_in_the_interactive_search_slice`](../../tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs)

The exact guarded browser command remains unrun:

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --filter Category=Browser
```

No browser opt-in was supplied, Chromium was not installed, and no Kestrel/IIS
target was started for this evidence.

## Whole-branch boundary review

Review range: native replacement merge base
`48b6a34670d290f4eff555951de5c41c0994c55b` through the final correction wave.

Decision: independently approved source-only for a local, incomplete Phase 1
milestone, with the guarded checks and later phases still open.

- The branch adds the isolated .NET modular-monolith solution and its tests. It
  does not modify legacy Python code, Docker assets, deployment scripts, user
  manuals, screenshots or DOCX assets.
- SQL Server remains the configured authority. Production code uses the SQL
  Server provider and standard server connections; `AttachDbFilename` and user
  instance strings appear only in rejection tests.
- The public six-state Job contract, source revision lineage and transactional
  stage/outbox design match the approved architecture. Non-SQL contracts pass;
  live concurrency, lease and rollback proof remains unrun.
- USearch is a derived immutable projection. Local save/reopen/validation tests
  pass; SQL-authoritative rebuild and failed-candidate pointer preservation
  remain unrun.
- MCP discovery exposes only the approved Phase 1 `kb.search` and `kb.brief`
  tools, with retry and error-envelope checks. This is deliberately not full
  MCP parity.
- No model download, inference runtime, GPU operation, provisioning, deployment,
  restart, IIS action, database creation, ACL change, migration or cutover ran.
  GPU types are future durable contracts only.
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

Residual concerns:

- all 23 SQL-dependent integration tests and the browser vertical slice still
  require explicit disposable-test configuration;
- a completed Publish replay can build or reuse a derived candidate directory
  before SQL recognises the already-completed dispatch. SQL authority and the
  active pointer remain protected, but orphan-candidate cleanup remains a
  Phase 2 hardening item that needs guarded SQL evidence;
- the SQL hydration explanation test rejects backslash path leakage but does not
  independently assert every forward-slash form. Current explanation production
  emits rank contributions only; strengthen the guarded assertion when that
  suite can run.

## Remaining gates

1. Provide an isolated server-level disposable-test connection, then run the
   complete integration project and preserve the generated-catalog cleanup
   evidence.
2. With the same disposable SQL configuration and explicit browser opt-in, run
   the guarded browser vertical slice using an already-provisioned compatible
   Chromium runtime.
3. Address Phase 2 durability, scheduler and cleanup work, then Phase 3 full
   MCP/plugin/REST/CLI parity.
4. Treat target SQL provisioning, IIS setup, backup/restore, Outlook, model/GPU
   activation and legacy retirement as later approval-gated milestones.
5. Before any native deployment, review and apply
   `20260727055755_DistinguishVectorIdentityAndPayloadChecksum` through the
   approved native operator path, then create and validate an active USearch
   generation. Until that state exists, `/health/ready` must remain unready.
