# Native Windows Phase 1 Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a locally verifiable, native-Windows vertical slice in which an approved local UTF-8 file becomes a durable SQL Server PipelineRecord, progresses through an SQL outbox and in-process worker, receives deterministic CPU embeddings, is published as a validated immutable USearch generation, appears as a hydrated hybrid-search result, and is visible through a new Blazor Interactive Server UI plus the kb.search and kb.brief MCP tools.

**Architecture:** Build a .NET 10 modular monolith. FluxKnowledge.Web is the only executable host and contains Blazor Interactive Server, REST, hosted Streamable HTTP MCP, in-process background workers and presentation notifications over the existing Blazor SignalR circuit. Domain and Application own platform-neutral contracts; SQL Server owns all durable truth; the embedded USearch index is an immutable, rebuildable projection whose active pointer is held in SQL. A local deterministic token-hash embedding provider is the only Phase 1 inference provider.

**Tech Stack:** .NET SDK 10.0.300; ASP.NET Core 10 / Blazor Web App with Interactive Server; SQL Server and EF Core SQL Server 10.0.10; Cloud.Unum.USearch 2.19.3; ModelContextProtocol.AspNetCore 1.4.0; xUnit 2.9.3; Microsoft.NET.Test.Sdk 18.8.1; Microsoft.Playwright 1.61.0 for the gated browser smoke test; IIS is a later host only, not used by automated tests.

## Global constraints

- Work only in E:/LLM KB/.worktrees/flux-native-windows on branch codex/flux-native-windows. Never edit, reset, stage or otherwise touch E:/LLM KB/.worktrees/pipeline-job-lifecycle-r1-2-restart.
- Do not call the Flux plugin or any Flux KB tool in this task. The legacy Python code is a read-only behavioural reference; it is not a dependency of the new application.
- The deployed target is one IIS-hosted ASP.NET Core process. Do not introduce Docker, WSL, RabbitMQ, Vespa, Elasticsearch, an external worker service, a separate microservice, a local SQLite fallback, or a user-attached database file.
- SQL Server is canonical. The production database must be SQL-created at I:/FluxKnowledge/Sql/Data/FluxKnowledge.mdf and I:/FluxKnowledge/Sql/Log/FluxKnowledge_log.ldf. Application startup validates that state only. It must never use AttachDbFilename, silently create a database, change SQL file locations, or fall back to another store.
- Implement the provisioning command and its non-mutating validation tests, but do not execute provisioning, create the target database, alter I: ACLs, deploy to IIS, restart IIS/SQL Server, cut over, or migrate legacy data without a separate current-conversation approval.
- A native SQL integration test is permitted only when FLUXKNOWLEDGE_TEST_SQL_CONNECTION is explicitly supplied. It must create a uniquely named database beginning FluxKnowledge_Phase1Tests_, reject a supplied Initial Catalog, and never point at FluxKnowledge or the I: database files.
- No model, model runtime, model conversion, GPU cache, Hugging Face/Ollama/Paddle artefact, CUDA/DirectML provider or GPU scheduler is downloaded, installed, activated or parity-tested. The deterministic provider is CPU-only and has no external model asset.
- Preserve the permanent Phase 2+ contracts now: six public Job states only; provenance/revision rules; durable mini-task/lane schema; atomic stage completion; immutable USearch generations; SQL hydration; and a clean Outlook COM boundary. Do not implement Outlook, Gmail, crawling, OCR, media, archive, code analysis, real GPU work, model adapters, the Codex bridge, or unapproved MCP tools in this phase.
- Use Central Package Management and checked-in NuGet lock files. Restore with locked mode after the first restore. Treat compiler warnings as errors and leave no warnings behind.
- Use tests first for changed behaviour. Pure project scaffolding may be created without a red/green cycle. Do not weaken an assertion to make a check pass.
- Keep public repository content free of source files, test databases, USearch indexes, connection strings, private content, raw transcript data, credentials and model artefacts. Add local runtime locations to .gitignore.
- Do not update dashboard manuals, screenshots, DOCX manuals or rendered manual assets. Update docs/roadmap.md only after Phase 1 behaviour has fresh verification evidence.
- This is a Phase 1 milestone, not full feature closeout. Do not run scripts/dev/complete-feature.ps1 for this milestone: it closes the legacy Python/Docker feature path and can perform operational steps outside this approved scope. Use it only at an authorised full-feature closeout after it has been made appropriate for the new target.

## Scope boundary and exit criteria

Phase 1 is complete only when all of the following are true in a disposable local test database:

1. Registering a UTF-8 file under a configured local ingress root creates one SourceIdentity revision, PipelineRecord, Worker Queued Job and unique DispatchMessage.
2. The hosted outbox pump claims and completes the text stages atomically, produces canonical SQL chunks/vectors and emits a presentation event only after SQL commit.
3. A USearch candidate can be built from SQL vectors, saved, reopened, validated and atomically placed before a short SQL transaction selects it as active. Candidate failure leaves the prior generation active.
4. Search combines SQL full-text candidates and active-generation ANN candidates using C# reciprocal-rank fusion, then hydrates only current, non-deleted SQL data. The result exposes source/provenance and a concise explanation.
5. The new Blazor app shows real SQL-backed overview, pipeline-record and search projections; reconnect/initialisation reloads from SQL rather than trusting a browser memory cache.
6. Hosted MCP exposes only kb.search and kb.brief in this phase. Both preserve the current read-only three-attempt transient retry and temporary-unavailable content envelope. Their existing argument names and defaults are accepted; unsupported advanced scoping is explicitly rejected rather than silently claimed.
7. Unit, SQL integration, MCP and browser checks pass where their explicit opt-in environment is present. No deployment, target database provisioning, model activity or legacy mutation occurs.

The following stay deliberately outside Phase 1: the remaining 52 legacy MCP tools and plugin/hook parity; scheduler behaviour beyond the durable shape; production data provisioning; full job timeline; code/extraction/media/archive routes; file monitoring; Gmail; Outlook VSTO; authentication/authorisation hardening; IIS publishing; backup/restore; and legacy retirement.

## Solution and file map

Create the following source layout. Existing Python directories under src/flux_llm_kb and tests remain untouched.

| Path | Responsibility |
| --- | --- |
| global.json | Pin the installed .NET SDK family and permit only latest patch roll-forward. |
| Directory.Build.props | Common net10.0, nullable, implicit-usings, deterministic-build and warning policy. |
| Directory.Packages.props | Central, stable package versions and lock-file policy. |
| .config/dotnet-tools.json | Local dotnet-ef 10.0.10 tool manifest. |
| FluxKnowledge.slnx | Native replacement solution only; no legacy Python projects. |
| src/FluxKnowledge.Domain | Public Job states, source/revision, pipeline-stage, dispatch and GPU mini-task value contracts. |
| src/FluxKnowledge.Application | Commands, read models, application ports, stage orchestration, deterministic embedding interface and status-event interface. |
| src/FluxKnowledge.Infrastructure.SqlServer | EF Core model/migrations, SQL transaction/claim repositories, provisioner and Full-Text query implementation. |
| src/FluxKnowledge.Infrastructure.Usearch | USearch generation builder, validator, atomic placement, active reader and ANN query adapter. |
| src/FluxKnowledge.Infrastructure.Inference | CPU-only deterministic token-hash embedding provider. |
| src/FluxKnowledge.Integrations | UTF-8 local-file adapter with root confinement and content hashing. |
| src/FluxKnowledge.Web | Sole web host: Blazor, REST, MCP, health checks, hosted pump and circuit presentation feed. |
| src/FluxKnowledge.Cli | Explicit operator commands, initially provision-sql and validate-sql only. |
| tests/FluxKnowledge.Domain.Tests | Fast domain and application unit tests. |
| tests/FluxKnowledge.Integration.Tests | Explicitly gated native-SQL, transaction, outbox and USearch tests. |
| tests/FluxKnowledge.Web.Tests | REST, MCP-tool, SignalR-circuit and browser-smoke tests. |

The dependency direction is Domain <- Application <- Infrastructure implementations <- Web/CLI. Web may reference all application/infrastructure projects for composition only. No Domain or Application type may expose DbContext, SQL connection, USearch index, MCP SDK, Razor or GPU SDK types.

## Shared protocol and state definitions

These names and shapes are fixed in the first implementation batch so later phases extend instead of replace them.

    public enum PublicJobState
    {
        WorkerQueued,
        WorkerProcessing,
        GpuQueued,
        GpuProcessing,
        Completed,
        Failed
    }

    public sealed record PipelineRecordId(Guid Value);
    public sealed record SourceIdentityId(Guid Value);
    public sealed record JobId(Guid Value);
    public sealed record DispatchMessageId(Guid Value);
    public sealed record IndexGenerationId(Guid Value);

    public sealed record RegisterUtf8FileCommand(
        string FullPath,
        string RequestedBy,
        string? SourceLabel);

    public sealed record RegisterUtf8FileResult(
        PipelineRecordId PipelineRecordId,
        JobId InitialJobId,
        DispatchMessageId InitialDispatchMessageId,
        bool ExistingReceipt);

    public sealed record SearchRequest(
        string Query,
        int Limit,
        string ScopeMode,
        string? Cwd,
        string? RootName,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Filters);

    public sealed record SearchHit(
        PipelineRecordId PipelineRecordId,
        string SourceIdentity,
        long Revision,
        string Title,
        string Snippet,
        double Score,
        IReadOnlyList<string> Explanation);

    public sealed record SearchResponse(
        IReadOnlyList<SearchHit> Results,
        int CandidateCount,
        string ActiveIndexGeneration,
        string ScopeNote);

    public sealed record StatusChanged(
        PipelineRecordId? PipelineRecordId,
        string Projection,
        DateTimeOffset OccurredAtUtc);

    public sealed record OverviewProjection(
        int WorkerQueuedCount,
        int WorkerProcessingCount,
        int GpuQueuedCount,
        int GpuProcessingCount,
        int CompletedCount,
        int FailedCount,
        int IndexedRecordCount,
        string ActiveIndexGeneration);

Use lower-case spaced wire values in PublicJobStateExtensions: worker queued, worker processing, GPU queued, GPU processing, completed and failed. Pending is a derived predicate that is true only for WorkerQueued; it is never a seventh wire value. Due time, attempt count, lease owner, lease expiry, lease generation, reason and error details are fields, not public states.

The Phase 1 text route is Identify -> Extract -> Normalise -> CanonicalIndex -> Embed -> Publish. Identify is synchronous registration work: it resolves the canonical local source identity, content hash and source revision before the first durable Extract Job is created. Every worker stage receives a DispatchMessage containing PipelineRecordId, source revision, stage, operation, dispatch generation and idempotency key. A successful transition validates revision/idempotency, writes the artefact/audit data, completes the current Job, creates the next Job and outbox row, then commits. The wake-up notification follows that commit.

## Task 1: scaffold the native solution and reproducible build

**Files:**

- Create: global.json
- Create: Directory.Build.props
- Create: Directory.Packages.props
- Create: .config/dotnet-tools.json
- Create: FluxKnowledge.slnx
- Create: src/FluxKnowledge.Domain/FluxKnowledge.Domain.csproj
- Create: src/FluxKnowledge.Application/FluxKnowledge.Application.csproj
- Create: src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj
- Create: src/FluxKnowledge.Infrastructure.Usearch/FluxKnowledge.Infrastructure.Usearch.csproj
- Create: src/FluxKnowledge.Infrastructure.Inference/FluxKnowledge.Infrastructure.Inference.csproj
- Create: src/FluxKnowledge.Integrations/FluxKnowledge.Integrations.csproj
- Create: src/FluxKnowledge.Web/FluxKnowledge.Web.csproj
- Create: src/FluxKnowledge.Web/Program.cs
- Create: src/FluxKnowledge.Cli/FluxKnowledge.Cli.csproj
- Create: src/FluxKnowledge.Cli/Program.cs
- Create: tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj
- Create: tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj
- Create: tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj
- Modify: .gitignore

- [x] **Step 1: Create the project graph without changing legacy files.**

    Use SDK-style projects. FluxKnowledge.Web uses Microsoft.NET.Sdk.Web; all others use Microsoft.NET.Sdk. Every project targets net10.0. Enable Nullable, ImplicitUsings, TreatWarningsAsErrors, AnalysisLevel latest and deterministic builds in Directory.Build.props.

    Use these centrally pinned packages:

    - Microsoft.EntityFrameworkCore.SqlServer 10.0.10
    - Microsoft.EntityFrameworkCore.Design 10.0.10, private assets
    - Microsoft.AspNetCore.Mvc.Testing 10.0.10
    - Microsoft.AspNetCore.SignalR.Client 10.0.10
    - ModelContextProtocol.AspNetCore 1.4.0
    - Cloud.Unum.USearch 2.19.3
    - Microsoft.NET.Test.Sdk 18.8.1
    - xunit 2.9.3
    - xunit.runner.visualstudio 3.1.5, private assets
    - Microsoft.Playwright 1.61.0

    Set ManagePackageVersionsCentrally and CentralPackageTransitivePinningEnabled to true. Set RestorePackagesWithLockFile to true. Create per-project packages.lock.json files after the first restore and commit them. Add dotnet-ef 10.0.10 to the local tool manifest.

    Add only native-project ignores: .vs/, artifacts/, TestResults/, .tools/, appsettings.Local.json, *.mdf, *.ndf, *.ldf, FluxKnowledgeData/, and FluxKnowledgeIndexes/. Do not ignore source or test files broadly.

    The starting Program.cs is a minimal WebApplication that returns a 200 response at /health/live. It does not connect to SQL, start a worker or map MCP yet.

- [x] **Step 2: Verify the mechanical scaffold.**

    Run:

    dotnet tool restore
    dotnet restore FluxKnowledge.slnx
    dotnet restore FluxKnowledge.slnx --locked-mode
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore
    dotnet test FluxKnowledge.slnx --configuration Release --no-build
    git diff --check

    Expected: restore creates checked-in lock files; the Release build and empty test assemblies pass with zero warnings; no Python test, Docker file or legacy source path changes.

- [x] **Step 3: Commit the self-contained scaffold.**

    Run:

    git add global.json Directory.Build.props Directory.Packages.props .config/dotnet-tools.json FluxKnowledge.slnx .gitignore src/FluxKnowledge.* tests/FluxKnowledge.*
    git commit -m "chore: scaffold native Windows phase 1 solution"

## Task 2: establish permanent domain contracts and public-state invariants

**Files:**

- Create: src/FluxKnowledge.Domain/Jobs/PublicJobState.cs
- Create: src/FluxKnowledge.Domain/Jobs/PublicJobStateExtensions.cs
- Create: src/FluxKnowledge.Domain/Jobs/Job.cs
- Create: src/FluxKnowledge.Domain/Jobs/DispatchMessage.cs
- Create: src/FluxKnowledge.Domain/Pipeline/PipelineStage.cs
- Create: src/FluxKnowledge.Domain/Pipeline/PipelineRecord.cs
- Create: src/FluxKnowledge.Domain/Pipeline/SourceIdentity.cs
- Create: src/FluxKnowledge.Domain/Pipeline/Artifact.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuMiniTask.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuPriorityLane.cs
- Create: src/FluxKnowledge.Domain/Common/Identifiers.cs
- Create: src/FluxKnowledge.Domain/Common/DomainInvariantException.cs
- Create: src/FluxKnowledge.Application/Contracts/RegistrationContracts.cs
- Create: src/FluxKnowledge.Application/Contracts/SearchContracts.cs
- Create: src/FluxKnowledge.Application/Contracts/StatusContracts.cs
- Create: src/FluxKnowledge.Application/Ports/IPipelineStore.cs
- Create: src/FluxKnowledge.Application/Ports/IOutboxStore.cs
- Create: src/FluxKnowledge.Application/Ports/IJobClaimStore.cs
- Create: src/FluxKnowledge.Application/Ports/IEmbeddingProvider.cs
- Create: src/FluxKnowledge.Application/Ports/ISearchService.cs
- Create: src/FluxKnowledge.Application/Ports/IStatusEventPublisher.cs
- Create: tests/FluxKnowledge.Domain.Tests/Jobs/PublicJobStateTests.cs
- Create: tests/FluxKnowledge.Domain.Tests/Pipeline/PipelineRecordTests.cs
- Create: tests/FluxKnowledge.Domain.Tests/Gpu/GpuMiniTaskTests.cs

- [x] **Step 1: Write failing invariant tests.**

    Add the following representative tests before implementing the types:

    public sealed class PublicJobStateTests
    {
        [Fact]
        public void Pending_is_a_derived_name_for_worker_queued_only()
        {
            var queued = Job.CreateQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Extract, "extract");

            Assert.Equal(PublicJobState.WorkerQueued, queued.PublicState);
            Assert.True(queued.IsPending);
            Assert.Equal("worker queued", queued.PublicState.ToWireValue());
            Assert.DoesNotContain("pending", PublicJobStateExtensions.AllWireValues);
        }

        [Fact]
        public void Capacity_return_keeps_the_job_in_its_existing_queue_family()
        {
            var processing = Job
                .CreateGpuQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Embed, "embed")
                .ClaimGpu("gpu-worker", DateTimeOffset.Parse("2026-07-26T09:00:00Z"));
            var returned = processing.ReturnForCapacity(DateTimeOffset.Parse("2026-07-26T10:00:00Z"));

            Assert.Equal(PublicJobState.GpuQueued, returned.PublicState);
            Assert.Equal(DateTimeOffset.Parse("2026-07-26T10:00:00Z"), returned.DueAtUtc);
            Assert.DoesNotContain(returned.PublicState.ToWireValue(), new[] { "retrying", "blocked", "parked" });
        }
    }

    public sealed class PipelineRecordTests
    {
        [Fact]
        public void New_revision_keeps_source_identity_and_links_to_prior_revision()
        {
            var source = SourceIdentity.ForLocalFile("C:/ingress/readme.txt");
            var first = PipelineRecord.Register(source, 1, "hash-a", null);
            var second = first.CreateRevision(2, "hash-b");

            Assert.Equal(first.SourceIdentityId, second.SourceIdentityId);
            Assert.Equal(first.Id, second.ParentRevisionRecordId);
            Assert.Equal(2, second.Revision);
            Assert.NotEqual(first.ContentHash, second.ContentHash);
        }
    }

    public sealed class GpuMiniTaskTests
    {
        [Fact]
        public void Priority_lane_is_part_of_the_durable_contract_without_activating_gpu_work()
        {
            var task = GpuMiniTask.Create(
                JobId.New(), 4, GpuPriorityLane.DocumentIndexing,
                "future-model-key", "future-fingerprint", 256, 16_384, "idempotency");

            Assert.Equal(GpuPriorityLane.DocumentIndexing, task.PriorityLane);
            Assert.Equal(PublicJobState.GpuQueued, task.InitialParentJobState);
        }
    }

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore

    Expected: compilation fails because the domain types, factories and wire-value rules do not yet exist.

- [x] **Step 2: Implement the value types and invariant-preserving transitions.**

    Implement immutable aggregates. Job methods are the only state-transition entry points:

    - CreateQueued and CreateGpuQueued initialise a queued state and due time.
    - ClaimWorker and ClaimGpu require the matching queue state, set a lease owner/expiry and increment the lease generation.
    - Complete and Fail require the matching lease generation.
    - ReturnForCapacity changes only processing to its corresponding queued state and sets the due time.
    - A terminal Job cannot be claimed or completed twice.

    PipelineRecord owns SourceIdentityId, revision, content hash, root lineage ID, optional parent record ID, current stage, completion criteria and derived status. It never permits a different content hash to overwrite an existing revision.

    DispatchMessage has no infrastructure-specific fields. It stores PipelineRecordId, source revision, PipelineStage, operation, dispatch generation, idempotency key and safe scheduling fields only.

    GpuMiniTask is schema-ready only. It stores parent Job/revision, lane, model/runtime key, settings fingerprint, estimated bytes, admission generation and idempotency key. Define the global lane order as InteractiveRetrieval, DocumentIndexing, ImageOcr, ImageEnrichment and VideoOrUnknown. It must not create a scheduler, use a GPU SDK or introduce a seventh Job state.

- [x] **Step 3: Run focused checks.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore

    Expected: all domain invariant tests pass; build is warning-free.

- [x] **Step 4: Commit the contract batch.**

    Run:

    git add src/FluxKnowledge.Domain src/FluxKnowledge.Application tests/FluxKnowledge.Domain.Tests
    git commit -m "feat: define native pipeline and job contracts"

## Task 3: add safe SQL Server configuration, schema and provisioner boundaries

**Files:**

- Create: src/FluxKnowledge.Infrastructure.SqlServer/Configuration/SqlServerOptions.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Configuration/SqlServerOptionsValidator.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/*.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/*.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/InitialPhase1.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContextFactory.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Provisioning/SqlServerProvisioner.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Provisioning/SqlServerReadinessValidator.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/ServiceCollectionExtensions.cs
- Create: src/FluxKnowledge.Cli/Commands/ProvisionSqlCommand.cs
- Create: src/FluxKnowledge.Cli/Commands/ValidateSqlCommand.cs
- Create: tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerOptionsValidatorTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Support/NativeSqlServerFactAttribute.cs
- Create: tests/FluxKnowledge.Integration.Tests/Support/NativeSqlServerFixture.cs
- Create: tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs

- [x] **Step 1: Write the failing safety and mapping tests.**

    Add unit tests which prove the configuration rejects user-owned database attachment and exact-path drift:

    [Theory]
    [InlineData("Server=.;AttachDbFilename=C:\\\\temp\\\\FluxKnowledge.mdf;Integrated Security=true")]
    [InlineData("Data Source=.;Initial Catalog=FluxKnowledge;User Instance=true;Integrated Security=true")]
    public void Production_connection_string_cannot_attach_a_user_database(string connectionString)
    {
        var result = SqlServerOptionsValidator.Validate(new SqlServerOptions
        {
            ConnectionString = connectionString,
            DataFilePath = "I:/FluxKnowledge/Sql/Data/FluxKnowledge.mdf",
            LogFilePath = "I:/FluxKnowledge/Sql/Log/FluxKnowledge_log.ldf"
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Production_options_require_the_approved_sql_owned_file_paths()
    {
        var options = SqlServerOptions.ForProduction(
            "Server=localhost;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            "I:/other/FluxKnowledge.mdf",
            "I:/FluxKnowledge/Sql/Log/FluxKnowledge_log.ldf");

        var error = Assert.Throws<OptionsValidationException>(() => SqlServerOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("I:/FluxKnowledge/Sql/Data/FluxKnowledge.mdf", error.Message);
    }

    [Fact]
    public void Startup_readiness_does_not_contain_database_creation_or_file_movement()
    {
        var script = new SqlServerReadinessValidator().BuildValidationSql();

        Assert.DoesNotContain("CREATE DATABASE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER DATABASE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ATTACH", script, StringComparison.OrdinalIgnoreCase);
    }

    Add a schema-mapping test using the EF model (not a database) which asserts:

    - SourceIdentities has a unique source-kind/stable-key index.
    - PipelineRecords has a unique SourceIdentityId/revision index.
    - OutboxMessages.IdempotencyKey is unique.
    - Vectors.VectorId is a stable bigint and stores model fingerprint, dimensions, content hash, revision, deletion state and index-generation ID.
    - GpuMiniTasks stores its required future lane fields.
    - No table maps a local SQLite provider or an AttachDbFilename connection string.

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore

    Expected: unit tests fail for missing validation/schema types. Integration tests report skipped with a clear message unless FLUXKNOWLEDGE_TEST_SQL_CONNECTION is intentionally configured.

- [x] **Step 2: Implement the SQL model and migration.**

    Model the following SQL-owned tables and foreign keys:

    - SourceIdentities
    - PipelineRecords
    - Jobs
    - JobAttempts
    - OutboxMessages
    - Artifacts
    - TextChunks
    - Vectors
    - IndexGenerations
    - IndexState, one active-generation pointer row
    - AuditEvents
    - GpuMiniTasks

    Store vector float values as a deterministic binary encoding with a documented little-endian format, model fingerprint and dimension count; do not put a USearch-only primary record in SQL. Store immutable content hashes as lower-case SHA-256 hex. Use UTC DateTimeOffset fields and a rowversion concurrency column on mutable aggregate rows.

    The initial migration must create a FluxKnowledge full-text catalog and a full-text index over Artifacts.SearchText using raw SQL guarded by SERVERPROPERTY('IsFullTextInstalled') = 1. If full-text is unavailable, provision/readiness must report not-ready instead of silently substituting a non-SQL lexical engine.

    Implement two separate paths:

    1. SqlServerReadinessValidator opens the configured application connection, validates catalog name, schema migration state, SQL Full-Text availability and that the database file paths reported by sys.database_files exactly match the approved I: locations. It performs no DDL, DML, ACL change, backup or service operation.
    2. SqlServerProvisioner is reached only by the CLI provision-sql command. It requires an explicit administrator connection, canonical paths and an explicit --backup-target argument outside I:. It validates the drive/path, creates directories/ACL instructions, issues CREATE DATABASE with FILENAME/LOG ON SQL Server, runs migrations, and returns a structured result. The command must require --confirm-provision and exit before any mutation when that switch is absent.

    The CLI validate-sql command calls the readiness validator only. It does not start the web host or hosted services.

    Register the DbContext and repositories through AddFluxKnowledgeSqlServer. The runtime connection string comes from ConnectionStrings:FluxKnowledge; checked-in appsettings files contain no real connection string.

- [x] **Step 3: Add the guarded native SQL fixture.**

    NativeSqlServerFixture must:

    - Be disabled unless FLUXKNOWLEDGE_TEST_SQL_CONNECTION is non-empty.
    - Parse the supplied server-level connection string and reject Initial Catalog, AttachDbFilename, Database=FluxKnowledge and any file-attach key.
    - Generate FluxKnowledge_Phase1Tests_<guid> as the only test catalog name.
    - Create/drop only that generated test database and never use I: file paths.
    - Apply the native migration and dispose/drop the test database even after a failed test.

    Do not make the main test command create a database. The opt-in environment variable is the evidence of an intentionally supplied disposable test server.

- [x] **Step 4: Run focused checks and commit.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore

    If a disposable server has been explicitly configured, additionally run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Persistence

    Expected: unit/model checks pass; guarded tests are skipped without the variable or pass against only a generated test catalog. Never substitute the production database string.

    Run:

    git add src/FluxKnowledge.Infrastructure.SqlServer src/FluxKnowledge.Cli tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Integration.Tests
    git commit -m "feat: add canonical SQL Server foundation"

## Task 4: implement transactional registration, claims and the in-process outbox worker

**Files:**

- Create: src/FluxKnowledge.Application/Pipeline/RegisterUtf8FileHandler.cs
- Create: src/FluxKnowledge.Application/Pipeline/StageTransitionService.cs
- Create: src/FluxKnowledge.Application/Pipeline/StageTransitionRequest.cs
- Create: src/FluxKnowledge.Application/Workers/IStageWorker.cs
- Create: src/FluxKnowledge.Application/Workers/IOutboxPump.cs
- Create: src/FluxKnowledge.Application/Workers/ExtractUtf8StageWorker.cs
- Create: src/FluxKnowledge.Application/Workers/NormaliseTextStageWorker.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlOutboxStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlJobClaimStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlStageTransitionStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxPumpService.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/ChannelOutboxWakeSignal.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs
- Create: src/FluxKnowledge.Integrations/Files/Utf8FileSourceReader.cs
- Create: src/FluxKnowledge.Integrations/Files/LocalIngressOptions.cs
- Create: src/FluxKnowledge.Integrations/Files/LocalIngressOptionsValidator.cs
- Create: tests/FluxKnowledge.Domain.Tests/Pipeline/RegisterUtf8FileHandlerTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Persistence/ClaimConcurrencyTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Persistence/StageTransitionAtomicityTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Workers/OutboxPumpTests.cs

- [x] **Step 1: Write failing registration and atomicity tests.**

    Add a unit test that makes idempotency observable:

    [Fact]
    public async Task Same_file_revision_creates_one_record_job_and_outbox_message()
    {
        var command = new RegisterUtf8FileCommand("C:/ingress/a.txt", "test", "a.txt");

        var first = await _handler.HandleAsync(command, CancellationToken.None);
        var second = await _handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(first.PipelineRecordId, second.PipelineRecordId);
        Assert.Equal(first.InitialJobId, second.InitialJobId);
        Assert.True(second.ExistingReceipt);
        Assert.Single(_store.Records);
        Assert.Single(_store.Jobs);
        Assert.Single(_store.OutboxMessages);
    }

    Add native SQL tests, marked with NativeSqlServerFact, that run two concurrent claims against a single eligible Job and a single eligible OutboxMessage:

    [Fact]
    public async Task Two_workers_cannot_claim_the_same_due_job()
    {
        var job = await _fixture.SeedWorkerQueuedJobAsync();
        var claims = await Task.WhenAll(
            _fixture.JobClaims.ClaimNextDueAsync("worker-a", _fixture.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None),
            _fixture.JobClaims.ClaimNextDueAsync("worker-b", _fixture.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Single(claims.Where(static claim => claim is not null));
        Assert.Equal(job.Id, claims.Single(static claim => claim is not null)!.JobId);
    }

    Add an atomicity test which injects a failure after writing a stage artefact but before creating the next Job. It must prove no artefact, completed Job, successor Job or successor DispatchMessage persists. Add a duplicate-delivery test which invokes the same idempotency key twice and proves the durable original transition is returned without duplicate artefacts/jobs.

    Add a lease-recovery test which seeds a WorkerProcessing Job with an expiry in the past and proves the next normal ClaimNextDueAsync call returns it as WorkerProcessing under a new lease generation. It must not need a special reconciliation worker or a new public state.

    Add an outbox stage-boundary test which registers a test UTF-8 file, starts the hosted pump with a deterministic clock and waits for the persisted Normalise artefact plus its queued CanonicalIndex successor. It must assert the real database rows and outbox transition, rather than merely asserting that an in-memory handler was called. Vectors and the active USearch generation are asserted by Task 5 after its workers exist.

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore

    Expected: tests fail because handlers, stores, worker claims and transaction semantics are absent.

- [x] **Step 2: implement the constrained UTF-8 adapter and registration transaction.**

    LocalIngressOptions contains one or more canonical allowed roots. Utf8FileSourceReader must resolve the supplied path, reject paths outside those roots, reject non-UTF-8 input and calculate the SHA-256 content hash from the exact bytes. It does not watch directories, crawl, parse other formats or copy files into the repository.

    RegisterUtf8FileHandler must derive a stable source identity from canonical local path, obtain the next revision only when the file content hash differs, and create all four initial durable items in one transaction:

    1. SourceIdentity or existing equivalent;
    2. PipelineRecord with the current revision and provenance;
    3. Extract WorkerQueued Job;
    4. matching OutboxMessage with a unique idempotency key.

    Registering the same bytes/revision returns the original durable IDs and ExistingReceipt true. A changed hash makes a linked new PipelineRecord revision; it must not overwrite the older record.

    ExtractUtf8StageWorker reopens the registered path and re-hashes its exact bytes before it writes the Extract artefact. If the source file has changed since registration, it fails the claimed Job with the precise reason "source content changed before extraction; register a new revision"; it must not attach new bytes to an older PipelineRecord. NormaliseTextStageWorker normalises Unicode to FormKC and line endings to LF, then passes a versioned text artefact to CanonicalIndex.

- [x] **Step 3: implement claims, stage transition and the hosted pump.**

    Implement SQL claims as one atomic UPDATE ... OUTPUT operation using appropriate SQL Server locking hints. A claim requires due time <= current UTC time and either no lease or an expired lease. It changes the Job to WorkerProcessing or a DispatchMessage to Processing, sets owner/expiry/generation and returns exactly one row. Completion/failure uses the claimed lease generation in its WHERE predicate.

    StageTransitionStore begins a SQL transaction and carries out exactly this order:

    1. validate PipelineRecord revision and DispatchMessage idempotency;
    2. persist the immutable artefact and audit event;
    3. complete the claimed current Job;
    4. create the next WorkerQueued Job where the stage has a successor;
    5. insert its unique DispatchMessage;
    6. commit.

    The outbox pump is an IHostedService inside FluxKnowledge.Web. It drains due messages at startup, after a ChannelOutboxWakeSignal notification and at a 60-second fallback interval. It dispatches only after a successful claim. The worker may publish a StatusChanged event only after StageTransitionStore has committed. It never acknowledges before commit, creates a separate queue service or relies on the Channel for durability. It claims only operations for which an IStageWorker is registered; an unregistered later-stage DispatchMessage stays WorkerQueued/due and is not treated as a failed delivery.

    Task 4 registers Extract and Normalise. Task 5 adds CanonicalIndex, Embed and Publish to the same registry, completing the Phase 1 text route without changing the transition contract. A claimed operation that loses its registered handler fails with an explicit non-retryable reason; it is never silently dropped.

- [ ] **Step 4: prove concurrency and local worker behaviour.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Workers|FullyQualifiedName~StageTransition

    With the explicit disposable server environment, also run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~ClaimConcurrency|FullyQualifiedName~StageTransitionAtomicity

    Expected: unguarded registration tests pass; guarded SQL tests prove one winner for a claim, rollback on injected transition failure, idempotent duplicate delivery and startup/wake execution.

- [x] **Step 5: commit the durable pipeline batch.**

    Run:

    git add src/FluxKnowledge.Application src/FluxKnowledge.Infrastructure.SqlServer src/FluxKnowledge.Integrations tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Integration.Tests
    git commit -m "feat: run UTF-8 records through durable SQL outbox"

## Task 5: add deterministic CPU embedding, canonical chunks and immutable USearch generations

**Files:**

- Create: src/FluxKnowledge.Application/Indexing/TextChunker.cs
- Create: src/FluxKnowledge.Application/Indexing/CanonicalIndexStageWorker.cs
- Create: src/FluxKnowledge.Application/Indexing/EmbedStageWorker.cs
- Create: src/FluxKnowledge.Application/Indexing/PublishStageWorker.cs
- Create: src/FluxKnowledge.Application/Ports/IIndexGenerationStore.cs
- Create: src/FluxKnowledge.Application/Ports/IAnnIndex.cs
- Create: src/FluxKnowledge.Infrastructure.Inference/DeterministicTokenHashEmbeddingProvider.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/UsearchIndexOptions.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationValidator.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationStore.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/UsearchAnnIndex.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/AtomicGenerationPlacement.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/ServiceCollectionExtensions.cs
- Create: tests/FluxKnowledge.Domain.Tests/Indexing/DeterministicTokenHashEmbeddingProviderTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs

- [x] **Step 1: write failing deterministic-index tests.**

    Add an embedding test that fixes the algorithm rather than merely checking non-empty output:

    [Fact]
    public async Task Same_normalised_text_produces_the_same_unit_vector_without_a_model_asset()
    {
        var provider = new DeterministicTokenHashEmbeddingProvider();

        var first = await provider.EmbedAsync("Café   PLAN", CancellationToken.None);
        var second = await provider.EmbedAsync("Café plan", CancellationToken.None);

        Assert.Equal("deterministic-tokenhash-v1:256", first.ModelFingerprint);
        Assert.Equal(256, first.Values.Count);
        Assert.Equal(first.Values, second.Values);
        Assert.InRange(first.Values.Sum(static value => value * value), 0.9999F, 1.0001F);
    }

    Add a Usearch snapshot test:

    [Fact]
    public async Task Failed_candidate_validation_leaves_the_existing_generation_active()
    {
        var active = await _fixture.PublishGenerationAsync(_fixture.ValidVectors());
        await Assert.ThrowsAsync<IndexGenerationValidationException>(
            () => _fixture.BuildCandidateWithInjectedReopenFailureAsync(_fixture.ValidVectors()));

        Assert.Equal(active.Id, await _fixture.IndexState.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.True(File.Exists(active.IndexPath));
    }

    Add a rebuild test which deletes the entire local index root after vectors are in SQL, calls RebuildFromSqlAsync and proves an equivalent candidate generation is active. It must not read the deleted directory as recovery input.

    Add the complete hosted vertical-slice test moved from Task 4: register a test UTF-8 file, run the pump with the full Extract/Normalise/CanonicalIndex/Embed/Publish worker registry, and assert the final record has canonical text/chunks/vectors plus an active generation pointer.

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Usearch

    Expected: compilation/test failures for missing deterministic provider and generation manager.

- [x] **Step 2: implement CPU-only canonical text/index stages.**

    DeterministicTokenHashEmbeddingProvider must:

    - normalise input with Unicode FormKC and invariant lower-case;
    - tokenize with a documented ASCII-letter/digit boundary rule;
    - hash each token with FNV-1a 64;
    - map it to one of 256 dimensions, derive sign from a hash bit, accumulate term weight;
    - L2-normalise non-empty vectors and return an all-zero vector only for empty normalised text;
    - return the fixed fingerprint deterministic-tokenhash-v1:256.

    It must not reference HTTP, GPU, ONNX, CUDA, DirectML, a model directory or a network client.

    TextChunker creates stable, bounded chunks with ordinal and source text span. CanonicalIndexStageWorker writes the normalised text and chunks as SQL artefacts. EmbedStageWorker writes canonical SQL vector rows with a stable SQL-issued bigint VectorId. PublishStageWorker asks UsearchGenerationBuilder to build a candidate from eligible current SQL vectors.

- [x] **Step 3: implement safe generation build and publication.**

    UsearchIndexOptions requires an app-owned local data root outside the repository and deployment directory. Candidate staging lives under that same root so the final Directory.Move stays on one volume. The root is configuration-only; it is not a package cache and no model path is accepted.

    The publication sequence is fixed:

    1. enumerate eligible SQL vectors for a candidate IndexGenerationId;
    2. build USearch under a unique staging directory;
    3. save it, reopen it with the same dimensions/metric, validate IDs, vector count, dimensions and metadata checksum;
    4. atomically move the complete candidate directory to its immutable final directory;
    5. in a short SQL transaction set IndexState.ActiveGenerationId and retire the prior pointer;
    6. publish StatusChanged after that transaction.

    On any error before step 5, delete only the candidate staging directory and leave the active pointer/directory unchanged. A generation never overwrites another generation path. Store enough metadata in SQL to validate and rebuild from vectors even if every USearch file is deleted.

    **Approved implementation correction:** `Vectors` remains the stable canonical vector store. Add an immutable `IndexGenerationVectors` snapshot-membership relation (GenerationId, VectorId) so one stable vector can belong to multiple immutable corpus-wide generations. The candidate's membership is all eligible, current, non-deleted vectors; it is written with the short SQL activation transaction and retained for validation/rebuild of historical generations. Add a code-only migration with a safe backfill from the existing `Vectors.IndexGenerationId` relationship. Do not execute that migration against any target database in this phase.

    The builder returns one immutable candidate snapshot: the exact vectors, membership checksum, model fingerprint, dimensions and placed path used to create the index. The Publish transition carries that single snapshot into a serialisable SQL activation transaction. That transaction re-evaluates the eligible corpus using each source's latest revision before deletion filtering, compares it to the candidate checksum and membership, then creates the membership rows and advances the active pointer together. If the candidate has been superseded, it completes without advancing the pointer; a later current Publish owns the next generation. A replay must recognise a valid placed candidate and a concurrent placement winner without overwriting either directory.

    UsearchAnnIndex reads the active generation ID from SQL before each query. It reuses an in-memory index only when its immutable generation ID matches the SQL pointer; otherwise it opens and validates the newly pointed-to directory before serving candidates. A ReaderWriterLockSlim protects the cached index replacement. Phase 1 never deletes a placed generation directory, so an in-flight search sees one complete immutable generation and no query can observe a partly written candidate.

- [x] **Step 4: run focused index checks and commit.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Indexing

    With the explicit disposable server environment, run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~SqlToUsearchRebuild

    Expected: deterministic vectors are repeatable; generation save/reopen/validate succeeds; a candidate failure preserves the old active generation; SQL rebuild works after index-directory deletion.

    Run:

    git add src/FluxKnowledge.Application src/FluxKnowledge.Infrastructure.Inference src/FluxKnowledge.Infrastructure.Usearch tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Integration.Tests
    git commit -m "feat: build rebuildable USearch projections from SQL"

## Task 6: implement hydrated hybrid search and its shared REST query use case

**Files:**

- Create: src/FluxKnowledge.Application/Search/HybridSearchService.cs
- Create: src/FluxKnowledge.Application/Search/ReciprocalRankFusion.cs
- Create: src/FluxKnowledge.Application/Search/SearchQueryValidator.cs
- Create: src/FluxKnowledge.Application/Ports/ILexicalSearch.cs
- Create: src/FluxKnowledge.Application/Ports/ISearchHydrator.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlFullTextSearch.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlSearchHydrator.cs
- Create: src/FluxKnowledge.Infrastructure.Usearch/Search/UsearchNearestNeighbourQuery.cs
- Create: src/FluxKnowledge.Web/Endpoints/SearchEndpoints.cs
- Create: tests/FluxKnowledge.Domain.Tests/Search/ReciprocalRankFusionTests.cs
- Create: tests/FluxKnowledge.Domain.Tests/Search/SearchQueryValidatorTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Endpoints/SearchEndpointTests.cs

- [x] **Step 1: write failing search and hydration tests.**

    Add a fusion test that fixes the ranking rule:

    [Fact]
    public void Reciprocal_rank_fusion_uses_one_over_sixty_plus_rank_and_breaks_ties_by_vector_id()
    {
        var fused = ReciprocalRankFusion.Combine(
            new[] { new RankedCandidate(8, 1), new RankedCandidate(4, 2) },
            new[] { new RankedCandidate(4, 1), new RankedCandidate(8, 2) });

        Assert.Equal(new long[] { 4, 8 }, fused.Select(static item => item.VectorId));
        Assert.Equal(2D / 61D, fused[0].Score, 10);
    }

    Add integration tests that seed:

    - a current vector/chunk that matches both SQL full-text and ANN;
    - a deleted vector;
    - a vector whose pipeline record revision is no longer current;
    - a candidate vector absent from SQL.

    Assert that only the current non-deleted result is hydrated, that its source identity/revision/snippet appear in SearchHit, and that an explanation lists lexical and semantic candidate contributions without exposing raw internal paths.

    Add an endpoint test:

    [Fact]
    public async Task Search_endpoint_returns_hydrated_results_not_usarch_only_rows()
    {
        var response = await _client.GetFromJsonAsync<SearchResponse>("/api/search?query=restart&limit=5");

        var hit = Assert.Single(response!.Results);
        Assert.Equal("C:/ingress/guide.txt", hit.SourceIdentity);
        Assert.Contains(hit.Explanation, static item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, static item => item.StartsWith("semantic:", StringComparison.Ordinal));
    }

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Search

    Expected: the new search tests fail because validation, candidate readers, fusion and endpoint are absent.

- [x] **Step 2: implement one shared query path.**

    SearchQueryValidator requires non-empty trimmed query text, accepts limit 1 through 50, accepts only local_first and workspace_boosted as scope_mode values, and rejects malformed filters. Phase 1 has one local corpus; if workspace_boosted, cwd, root_name or filters would change semantics, return a clear non-retryable unsupported-scope result rather than claiming broad retrieval exists.

    SqlFullTextSearch uses the SQL Server Full-Text index through parameterised SQL/EF Core APIs. UsearchNearestNeighbourQuery embeds the query with the deterministic provider and reads the active immutable generation. Both return ranked stable VectorId candidates only.

    ReciprocalRankFusion combines candidates by 1/(60 + rank), preserves source list/ranks for explanation, then breaks equal scores by ascending VectorId for reproducibility. SqlSearchHydrator joins candidates back to SQL and rejects a row if Vector.IsDeleted is true, the chunk/record is deleted, the vector revision mismatches the current PipelineRecord revision, or the content hash no longer matches. USearch rows are never sent directly to REST/MCP/UI.

    HybridSearchService is the single application use case used by REST, MCP and Blazor. GET /api/search accepts query, limit and the compatible optional scope parameters and returns SearchResponse. It contains no alternative ranking/business logic.

- [x] **Step 3: run focused checks and commit.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Search

    With the explicit disposable server environment, run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~HybridSearch

    Expected: RRF/rules pass deterministically; integration proves SQL hydration filters stale/deleted candidates; REST returns the same application result.

    Run:

    git add src/FluxKnowledge.Application src/FluxKnowledge.Infrastructure.SqlServer src/FluxKnowledge.Infrastructure.Usearch src/FluxKnowledge.Web tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Integration.Tests tests/FluxKnowledge.Web.Tests
    git commit -m "feat: add hydrated native hybrid search"

## Task 7: host kb.search and kb.brief through Streamable HTTP MCP

**Files:**

- Create: src/FluxKnowledge.Application/Mcp/ReadonlyMcpRetryExecutor.cs
- Create: src/FluxKnowledge.Application/Mcp/McpErrorEnvelope.cs
- Create: src/FluxKnowledge.Application/Mcp/McpTransientFailureClassifier.cs
- Create: src/FluxKnowledge.Web/Mcp/KnowledgeMcpTools.cs
- Create: src/FluxKnowledge.Web/Mcp/McpResultFactory.cs
- Create: src/FluxKnowledge.Web/Mcp/McpServiceCollectionExtensions.cs
- Modify: src/FluxKnowledge.Web/Program.cs
- Create: tests/FluxKnowledge.Domain.Tests/Mcp/ReadonlyMcpRetryExecutorTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Mcp/KnowledgeMcpToolsTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Mcp/McpEndpointRegistrationTests.cs

- [x] **Step 1: write failing parity tests for the two approved MCP tools.**

    Write the retry test to match the current legacy semantics exactly for read-only operations:

    [Fact]
    public async Task Read_only_search_recreates_its_operation_three_times_after_transient_failures()
    {
        var attempts = 0;
        var executor = new ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero);

        var result = await executor.ExecuteAsync(
            "kb.search",
            _ =>
            {
                attempts++;
                if (attempts < 3) throw new ConnectionResetException("backend connection reset");
                return Task.FromResult("recovered");
            },
            CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal("recovered", result.Value);
    }

    Write the terminal transient test:

    [Fact]
    public async Task Brief_returns_the_legacy_temporary_unavailable_content_envelope_after_three_attempts()
    {
        var tools = CreateToolsThatAlwaysThrow<TimeoutException>("backend timed out");

        var result = await tools.Brief("restart", 1200, null, null, "local_first", null, CancellationToken.None);
        var payload = ParseFirstTextBlock(result);

        Assert.False(result.IsError);
        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("temporary_unavailable", payload.GetProperty("status").GetString());
        Assert.Equal("mcp.temporary_unavailable", payload.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("mcp", payload.GetProperty("error").GetProperty("component").GetString());
        Assert.Equal("kb.brief", payload.GetProperty("error").GetProperty("stage").GetString());
        Assert.True(payload.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Equal(503, payload.GetProperty("error").GetProperty("status_code").GetInt32());
    }

    Add tool-signature tests that require these exact parameters/defaults:

    - kb.search(query, limit = 5, cwd = null, root_name = null, scope_mode = "local_first", filters = null)
    - kb.brief(query, token_budget = 1200, cwd = null, root_name = null, scope_mode = "local_first", filters = null)

    Add an endpoint registration test that sends MCP initialise/discovery traffic to /mcp and proves the advertised set contains kb.search and kb.brief and no unimplemented legacy tool.

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Mcp
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Mcp

    Expected: failures because MCP retry policy/tools/endpoint mapping do not exist.

- [x] **Step 2: implement MCP over the shared query use case.**

    Register ModelContextProtocol.AspNetCore as a stateless Streamable HTTP server and map its default /mcp endpoint. The composition root must use this shape:

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<KnowledgeMcpTools>();

    app.MapMcp();

    Mark each public tool method with McpServerTool and Description attributes. Register KnowledgeMcpTools only; later tools are not advertised as stubs.

    KnowledgeMcpTools must call ISearchService only. kb.search serialises the SearchResponse as the successful tool content. kb.brief calls the same query path, selects the top hydrated results within token_budget and returns a plain text brief on success, matching the current direct-string success behaviour.

    ReadonlyMcpRetryExecutor retries only recognised transient SQL/network/index exceptions three total attempts with 200 ms and 800 ms back-off. It creates a new scoped query operation for each retry. Do not retry validation errors or any future mutating tool.

    After final transient failure, return a non-error MCP call result whose first text content block is JSON:

    {
      "ok": false,
      "status": "temporary_unavailable",
      "settings_mutated": false,
      "error": {
        "code": "mcp.temporary_unavailable",
        "component": "mcp",
        "stage": "<tool name>",
        "retryable": true,
        "status_code": 503
      }
    }

    Preserve the legacy message/user_action wording in full:

    - message: Flux memory backend is temporarily unavailable while running <tool name>.
    - user_action: Retry after the Flux API, database, or search service finishes restarting.

    A non-transient failure returns the equivalent mcp.tool_error payload with status tool_error, retryable false and status_code 500. Its MCP result also remains IsError false, because the legacy client treats the structured content envelope as the error surface.

    No tool invokes Flux, PostgreSQL, Docker, model code or a separate HTTP backend. There is no stdio bridge in this phase.

- [x] **Step 3: run focused MCP checks and commit.**

    Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Mcp
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Mcp
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore

    Expected: tests prove the three-attempt/temporary-unavailable behaviour, argument defaults and hosted tool discovery; build has no warnings.

    Run:

    git add src/FluxKnowledge.Application src/FluxKnowledge.Web tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Web.Tests
    git commit -m "feat: host phase one MCP search tools"

## Task 8: build the visible Blazor Interactive Server vertical slice and SQL-backed live status

**Files:**

- Create: src/FluxKnowledge.Web/Components/App.razor
- Create: src/FluxKnowledge.Web/Components/Routes.razor
- Create: src/FluxKnowledge.Web/Components/Layout/MainLayout.razor
- Create: src/FluxKnowledge.Web/Components/Layout/NavMenu.razor
- Create: src/FluxKnowledge.Web/Components/Pages/Overview.razor
- Create: src/FluxKnowledge.Web/Components/Pages/PipelineRecords.razor
- Create: src/FluxKnowledge.Web/Components/Pages/Search.razor
- Create: src/FluxKnowledge.Web/Components/Shared/StatusCount.razor
- Create: src/FluxKnowledge.Web/Components/Shared/SearchResults.razor
- Create: src/FluxKnowledge.Web/Components/Status/StatusEventFeed.cs
- Create: src/FluxKnowledge.Web/Components/Status/StatusEventCircuitHandler.cs
- Create: src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs
- Create: src/FluxKnowledge.Web/Components/Status/OverviewProjectionState.cs
- Create: src/FluxKnowledge.Web/Endpoints/PipelineEndpoints.cs
- Create: src/FluxKnowledge.Web/Endpoints/HealthEndpoints.cs
- Create: src/FluxKnowledge.Web/wwwroot/css/app.css
- Modify: src/FluxKnowledge.Web/Program.cs
- Create: tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Components/PipelineRecordsProjectionTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Components/StatusEventFeedTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs

- [x] **Step 1: write failing projection and circuit-feed tests.**

    Add a projection-state test:

    [Fact]
    public async Task Overview_state_reloads_the_SQL_projection_after_a_status_event()
    {
        var reader = new FakeProjectionReader(
            new OverviewProjection(1, 0, 0, 0, 0, 0, 1, "generation-a"));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(new OverviewProjection(2, 0, 0, 0, 0, 0, 2, "generation-a"));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "pipeline", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    Add a projection test that stores 1,000 Jobs and verifies the count component renders 999+ only at 1,000, while 999 renders exactly 999.

    Add a browser test marked [Trait("Category", "Browser")] and skipped unless FLUXKNOWLEDGE_BROWSER_TESTS=1 plus the disposable SQL environment are supplied. It must:

    1. place a known UTF-8 file under the test-only configured ingress root;
    2. POST it to /api/pipeline-records/utf8-file;
    3. wait for the overview indexed count and pipeline record to update;
    4. submit the search page form;
    5. verify the returned title/snippet originates from the SQL-hydrated record.

    Run:

    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Components

    Expected: tests fail because pages/projection feed are absent.

- [x] **Step 2: implement minimal, accessible pages with no fake future screens.**

    Configure AddRazorComponents().AddInteractiveServerComponents() and map the interactive server render mode. The initial navigation contains only Overview, Pipeline records and Search; do not add blank links for Jobs, GPU, integrations or settings.

    Overview is a SQL projection with Worker Queued, Worker Processing, GPU Queued, GPU Processing, Completed and Failed counts plus active index generation. It never derives Pending from a broader aggregate. Pipeline records shows source identity, revision, current stage, derived status, content hash prefix and timestamps. Search uses the same ISearchService as REST/MCP and shows source, revision, score, snippet and explanation.

    POST /api/pipeline-records/utf8-file accepts RegisterUtf8FileCommand data and returns 202 Accepted with RegisterUtf8FileResult. GET /api/pipeline-records and GET /api/pipeline-records/{id} return SQL projections. GET /health/live is process-only; GET /health/ready invokes non-mutating SQL/schema/active-index validation.

    Implement StatusEventFeed as an in-process singleton fan-out with bounded subscriber channels. Implement StatusEventCircuitHandler as a scoped CircuitHandler that publishes a reconnect event when a Blazor circuit comes up. Components always load their SQL projection during initialisation and reload it on a matching feed event. The live message is only an invalidation signal; durable state always comes from SQL.

    Do not create a server-side HubConnection from a Razor component back to the same host. The existing Interactive Server circuit is the SignalR presentation transport in Phase 1. The feed/circuit abstraction permits a browser SignalR client or hub later without changing the domain/application contracts.

    Use semantic headings, labelled inputs, keyboard-operable navigation, visible focus styles, responsive layout, a polite live region for status change and no React/JavaScript dashboard port.

- [ ] **Step 3: provision and run the gated browser test only when approved test infrastructure exists.**

    The browser package and browser binary are test-only. Install Chromium only for the disposable local test context:

    pwsh tests/FluxKnowledge.Web.Tests/bin/Release/net10.0/playwright.ps1 install chromium

    Then run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    $env:FLUXKNOWLEDGE_BROWSER_TESTS = "1"
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter Category=Browser

    Expected: browser test passes against the generated disposable SQL catalog and local Kestrel test host. It must not use IIS, I:, a target database, a model download or legacy runtime.

- [x] **Step 4: run focused UI checks and commit.**

    Run:

    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Components|FullyQualifiedName~Endpoints
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore

    Expected: projection/feed/endpoint tests pass; browser test remains explicitly skipped without its two opt-in variables; build has no warnings.

    Run:

    git add src/FluxKnowledge.Web tests/FluxKnowledge.Web.Tests
    git commit -m "feat: show live native pipeline projections"

## Task 9: run the Phase 1 verification matrix, review the boundary and record evidence

**Files:**

- Modify: docs/roadmap.md
- Create: docs/operations/native-windows-phase-1-validation.md
- Modify: docs/superpowers/plans/2026-07-26-native-windows-phase-1.md

- [x] **Step 1: review each Phase 1 acceptance criterion against evidence.**

    Record the exact command, date, environment class, pass/fail state and linkable test name for:

    - domain Job state, provenance and future GPU-contract tests;
    - configuration/provisioner safety tests;
    - native SQL registration, duplicate, concurrent claim, lease-generation and atomic rollback tests;
    - deterministic embedding, USearch save/reopen/failed-candidate/rebuild tests;
    - SQL Full-Text plus ANN/RRF/hydration tests;
    - REST, MCP retry/envelope/discovery tests;
    - Blazor projection/reconnect and gated browser vertical-slice tests.

    The validation document must distinguish checked facts from unrun opt-in tests. It must not claim the target I: database, IIS, a backup, Outlook, a model, GPU work, full MCP parity or legacy retirement is complete.

- [x] **Step 2: run the baseline suite.**

    Run:

    dotnet tool restore
    dotnet restore FluxKnowledge.slnx --locked-mode
    dotnet build FluxKnowledge.slnx --configuration Release --no-restore
    dotnet test FluxKnowledge.slnx --configuration Release --no-build --filter Category!=Browser
    git diff --check
    git status --short

    Expected: all non-opt-in native tests pass with no warnings; package lock files are unchanged; git diff has no whitespace errors.

- [ ] **Step 3: run native SQL and browser evidence only with explicit disposable-test configuration.**

    Do not infer permission from the plan. When the caller supplies a server-level disposable-test connection string, run:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = "<server-level disposable-test connection string>"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-build
    $env:FLUXKNOWLEDGE_BROWSER_TESTS = "1"
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --filter Category=Browser

    If no such configuration/approval exists, record the two suites as not run and leave docs/roadmap.md Phase 1 progress short of verified completion. Do not substitute a production or target connection string.

- [x] **Step 4: update project intent only after evidence supports it.**

    Update the native replacement programme entry in docs/roadmap.md with the real Phase 1 percentage and remaining work. State that the delivered capability is a local UTF-8 vertical slice, list passed evidence, and separately identify unrun SQL/browser checks and operational blockers. Keep Phase 2+ requirements visible; do not mark them complete because their contracts exist.

    Update this plan's checkboxes to reflect actual completed steps and append a short evidence table. Do not modify the approved design's target contracts unless a test exposes a real incompatibility; in that event stop, document the conflict and obtain a design decision before changing permanent contracts.

- [x] **Step 5: perform one whole-branch review and commit the verified milestone.**

    Review for:

    - compliance with the approved architecture and phase boundary;
    - accidental legacy/Python/Docker changes;
    - SQL authority and no AttachDbFilename/fallback;
    - job-state and transaction invariants;
    - SQL-to-USearch rebuild safety;
    - MCP tool/error compatibility;
    - no unapproved model/GPU/production operation;
    - private-data, connection-string and generated-index leakage;
    - test gaps and unrun opt-in checks.

    If the review finds an issue, fix it and rerun the affected focused command before a full recheck. Then run:

    git add docs/roadmap.md docs/operations/native-windows-phase-1-validation.md docs/superpowers/plans/2026-07-26-native-windows-phase-1.md
    git commit -m "docs: record native Windows phase one evidence"

    Do not invoke scripts/dev/complete-feature.ps1, publish, deploy, restart services, create the I: database, change ACLs or archive/delete any worktree as part of this milestone.

### Task 9 evidence

| Date | Environment class | Command or review | Result |
| --- | --- | --- | --- |
| 2026-07-27 | Local Windows development/test host; .NET SDK 10.0.300 | `dotnet tool restore` | Passed; `dotnet-ef` 10.0.10 restored. |
| 2026-07-27 | Same; no SQL/browser opt-ins | `dotnet restore FluxKnowledge.slnx --locked-mode` | Passed after three stale lock entries were regenerated and the locked command rerun. |
| 2026-07-27 | Same | `dotnet build FluxKnowledge.slnx --configuration Release --no-restore` | Passed with 0 warnings and 0 errors. |
| 2026-07-27 | Same | `dotnet test FluxKnowledge.slnx --configuration Release --no-build --filter Category!=Browser` | Passed: Domain 44, Integration 29 with 21 SQL-opt-in skips, Web 21. |
| 2026-07-27 | Same | Disposable SQL integration project | Not run; no approved server-level disposable-test connection was supplied. |
| 2026-07-27 | Same | `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-build --filter Category=Browser` | Not run; SQL and browser opt-ins were absent. |
| 2026-07-27 | Whole native branch | Architecture, phase, legacy, SQL authority, Job/transaction, USearch rebuild, MCP, model/GPU, private-data and generated-index review | Accepted as an incomplete local Phase 1 milestone; detailed findings and remaining gates are in [the validation record](../../operations/native-windows-phase-1-validation.md). |

## Final implementation hand-off

Implement tasks in order. After each task, report the delivered runtime capability, focused evidence, remaining blocker and whether it advances the visible vertical slice or only reduces risk. Stop for a new decision if:

- a permanent contract conflicts with the existing legacy behaviour;
- a SQL transaction, lease or idempotency invariant fails;
- USearch cannot save/reopen/validate on the installed native ABI;
- a required test would target I:, the target FluxKnowledge database or a non-disposable environment;
- the implementation requires a model/runtime/GPU activation;
- MCP transport semantics differ from the captured kb.search/kb.brief contract.

The first operational approval required during execution is not an implementation change: it is permission to execute the explicit provision-sql command against the intended SQL Server target. Until then, all database evidence stays inside the caller-supplied generated disposable test catalog.
