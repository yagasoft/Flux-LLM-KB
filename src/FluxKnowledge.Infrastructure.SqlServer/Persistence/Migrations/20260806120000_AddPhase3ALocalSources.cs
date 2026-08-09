using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase3ALocalSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessorKind = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProcessorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutionClass = table.Column<int>(type: "int", nullable: false),
                    AcceptedClassificationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutputContract = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProcessorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IsRunnable = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RegistrationEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCapabilities", x => x.Id);
                    table.CheckConstraint("CK_SourceCapabilities_NativeExecutorLater_NotRunnable", "[ExecutionClass] <> 2 OR [IsRunnable] = CONVERT(bit, 0)");
                });

            migrationBuilder.CreateTable(
                name: "SourceRootConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalPathFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', [CanonicalPath]), 2)", stored: true, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Recursive = table.Column<bool>(type: "bit", nullable: false),
                    IncludePatternsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExcludePatternsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FollowLinks = table.Column<bool>(type: "bit", nullable: false),
                    MaximumFileBytes = table.Column<long>(type: "bigint", nullable: false),
                    AllowedClassificationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CrawlMode = table.Column<int>(type: "int", nullable: false),
                    ReconciliationCadenceSeconds = table.Column<long>(type: "bigint", nullable: false),
                    LastScanStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LastScanCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LastScanEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PermissionEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HealthEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRootConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StableSourceIdentity = table.Column<string>(type: "nvarchar(768)", maxLength: 768, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CanonicalPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalPathFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', [CanonicalPath]), 2)", stored: true, collation: "Latin1_General_100_BIN2"),
                    ParentSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Classification = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    FileCreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    FileLastWriteAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    DiscoveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    DiscoveryEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuppressedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RetainUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RetentionEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRevisions", x => x.Id);
                    table.CheckConstraint("CK_SourceRevisions_ContentSha256", "LEN([ContentSha256]) = 64 AND [ContentSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_SourceRevisions_SourceRevisions_ParentSourceRevisionId",
                        column: x => x.ParentSourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceRevisions_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceScanRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestKind = table.Column<int>(type: "int", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    IsReleased = table.Column<bool>(type: "bit", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    DiscoveredFileCount = table.Column<int>(type: "int", nullable: false),
                    IndexedFileCount = table.Column<int>(type: "int", nullable: false),
                    DeferredFileCount = table.Column<int>(type: "int", nullable: false),
                    BlockedFileCount = table.Column<int>(type: "int", nullable: false),
                    ErrorFileCount = table.Column<int>(type: "int", nullable: false),
                    AuditEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceScanRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceScanRequests_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityKind = table.Column<int>(type: "int", nullable: false),
                    ExecutionClass = table.Column<int>(type: "int", nullable: false),
                    ProcessorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    InputFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequiredCapability = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    AttemptEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultingPipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultingPipelineRecordRevision = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceActivities_PipelineRecords_ResultingPipelineRecordId_ResultingPipelineRecordRevision",
                        columns: x => new { x.ResultingPipelineRecordId, x.ResultingPipelineRecordRevision },
                        principalTable: "PipelineRecords",
                        principalColumns: new[] { "Id", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceActivities_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    StoreRelativePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RetainUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ReferenceCount = table.Column<long>(type: "bigint", nullable: false),
                    RetentionEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceArtifacts", x => x.Id);
                    table.CheckConstraint("CK_SourceArtifacts_ContentSha256", "LEN([ContentSha256]) = 64 AND [ContentSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_SourceArtifacts_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceScanJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceScanRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ErrorDetails = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceScanJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceScanJobs_SourceScanRequests_SourceScanRequestId",
                        column: x => x.SourceScanRequestId,
                        principalTable: "SourceScanRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceScanOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceScanRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    DispatchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceScanOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceScanOutbox_SourceScanRequests_SourceScanRequestId",
                        column: x => x.SourceScanRequestId,
                        principalTable: "SourceScanRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "SourceCapabilities",
                columns: new[] { "Id", "AcceptedClassificationsJson", "ExecutionClass", "IsRunnable", "OutputContract", "ProcessorFingerprint", "ProcessorKind", "ProcessorVersion", "RegisteredAtUtc", "RegisteredBy", "RegistrationEvidenceJson" },
                values: new object[] { new Guid("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"), "[\"text/plain\"]", 0, true, "pipeline:extract-utf8", "phase-3a-inprocess-text-metadata-v1", "text-metadata", "phase-3a-v1", new DateTimeOffset(new DateTime(2026, 8, 6, 12, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "system", null });

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivities_ResultingPipelineRecordId_ResultingPipelineRecordRevision",
                table: "SourceActivities",
                columns: new[] { "ResultingPipelineRecordId", "ResultingPipelineRecordRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivities_SourceRevisionId_ActivityKind_ProcessorVersion_InputFingerprint",
                table: "SourceActivities",
                columns: new[] { "SourceRevisionId", "ActivityKind", "ProcessorVersion", "InputFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivities_State_ExecutionClass",
                table: "SourceActivities",
                columns: new[] { "State", "ExecutionClass" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceArtifacts_ContentSha256",
                table: "SourceArtifacts",
                column: "ContentSha256");

            migrationBuilder.CreateIndex(
                name: "IX_SourceArtifacts_SourceRevisionId",
                table: "SourceArtifacts",
                column: "SourceRevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCapabilities_ProcessorKind_ProcessorVersion_ProcessorFingerprint",
                table: "SourceCapabilities",
                columns: new[] { "ProcessorKind", "ProcessorVersion", "ProcessorFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRevisions_ParentSourceRevisionId",
                table: "SourceRevisions",
                column: "ParentSourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceRevisions_SourceRootId_CanonicalPathFingerprint_ContentSha256",
                table: "SourceRevisions",
                columns: new[] { "SourceRootId", "CanonicalPathFingerprint", "ContentSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRevisions_SourceRootId_StableSourceIdentity_Revision",
                table: "SourceRevisions",
                columns: new[] { "SourceRootId", "StableSourceIdentity", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRootConfigurations_CanonicalPathFingerprint",
                table: "SourceRootConfigurations",
                column: "CanonicalPathFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanJobs_SourceScanRequestId",
                table: "SourceScanJobs",
                column: "SourceScanRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanJobs_State_DueAtUtc",
                table: "SourceScanJobs",
                columns: new[] { "State", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanOutbox_DispatchedAtUtc_DueAtUtc",
                table: "SourceScanOutbox",
                columns: new[] { "DispatchedAtUtc", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanOutbox_IdempotencyKey",
                table: "SourceScanOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanOutbox_SourceScanRequestId",
                table: "SourceScanOutbox",
                column: "SourceScanRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanRequests_IsReleased_State",
                table: "SourceScanRequests",
                columns: new[] { "IsReleased", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceScanRequests_SourceRootId_RequestedAtUtc",
                table: "SourceScanRequests",
                columns: new[] { "SourceRootId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceActivities");

            migrationBuilder.DropTable(
                name: "SourceArtifacts");

            migrationBuilder.DropTable(
                name: "SourceCapabilities");

            migrationBuilder.DropTable(
                name: "SourceScanJobs");

            migrationBuilder.DropTable(
                name: "SourceScanOutbox");

            migrationBuilder.DropTable(
                name: "SourceRevisions");

            migrationBuilder.DropTable(
                name: "SourceScanRequests");

            migrationBuilder.DropTable(
                name: "SourceRootConfigurations");
        }
    }
}
