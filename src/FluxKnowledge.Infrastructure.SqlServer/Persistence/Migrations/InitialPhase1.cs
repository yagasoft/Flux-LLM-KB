using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndexGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    IndexPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    MetadataChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    VectorCount = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexGenerations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StableKey = table.Column<string>(type: "nvarchar(768)", maxLength: 768, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ActiveIndexGenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexState", x => x.Id);
                    table.CheckConstraint("CK_IndexState_Singleton", "[Id] = 1");
                    table.ForeignKey(
                        name: "FK_IndexState_IndexGenerations_ActiveIndexGenerationId",
                        column: x => x.ActiveIndexGenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    RootLineageRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentRevisionRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentStage = table.Column<int>(type: "int", nullable: false),
                    CompletionCriteriaMet = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRecords", x => x.Id);
                    table.CheckConstraint("CK_PipelineRecords_ContentHash", "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_PipelineRecords_PipelineRecords_ParentRevisionRecordId",
                        column: x => x.ParentRevisionRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PipelineRecords_PipelineRecords_RootLineageRecordId",
                        column: x => x.RootLineageRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PipelineRecords_SourceIdentities_SourceIdentityId",
                        column: x => x.SourceIdentityId,
                        principalTable: "SourceIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SearchText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => x.Id);
                    table.CheckConstraint("CK_Artifacts_ContentHash", "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_Artifacts_PipelineRecords_PipelineRecordId",
                        column: x => x.PipelineRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_PipelineRecords_PipelineRecordId",
                        column: x => x.PipelineRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicState = table.Column<int>(type: "int", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ErrorDetails = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_PipelineRecords_PipelineRecordId",
                        column: x => x.PipelineRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DispatchGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    DispatchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_PipelineRecords_PipelineRecordId",
                        column: x => x.PipelineRecordId,
                        principalTable: "PipelineRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TextChunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    StartOffset = table.Column<int>(type: "int", nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextChunks", x => x.Id);
                    table.CheckConstraint("CK_TextChunks_ContentHash", "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_TextChunks_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GpuMiniTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    PriorityLane = table.Column<int>(type: "int", nullable: false),
                    ModelRuntimeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SettingsFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EstimatedBytes = table.Column<long>(type: "bigint", nullable: false),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuMiniTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpuMiniTasks_Jobs_ParentJobId",
                        column: x => x.ParentJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorDetails = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAttempts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vectors",
                columns: table => new
                {
                    VectorId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TextChunkId = table.Column<long>(type: "bigint", nullable: false),
                    ModelFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Values = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    IndexGenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vectors", x => x.VectorId);
                    table.CheckConstraint("CK_Vectors_ContentHash", "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_Vectors_IndexGenerations_IndexGenerationId",
                        column: x => x.IndexGenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vectors_TextChunks_TextChunkId",
                        column: x => x.TextChunkId,
                        principalTable: "TextChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "IndexState",
                columns: new[] { "Id", "ActiveIndexGenerationId", "UpdatedAtUtc" },
                values: new object[] { 1, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_PipelineRecordId_SourceRevision_Stage",
                table: "Artifacts",
                columns: new[] { "PipelineRecordId", "SourceRevision", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_PipelineRecordId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "PipelineRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_IdempotencyKey",
                table: "GpuMiniTasks",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_ParentJobId",
                table: "GpuMiniTasks",
                column: "ParentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_State_PriorityLane_CreatedAtUtc",
                table: "GpuMiniTasks",
                columns: new[] { "State", "PriorityLane", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexState_ActiveIndexGenerationId",
                table: "IndexState",
                column: "ActiveIndexGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_JobId_AttemptNumber",
                table: "JobAttempts",
                columns: new[] { "JobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PipelineRecordId",
                table: "Jobs",
                column: "PipelineRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PublicState_DueAtUtc",
                table: "Jobs",
                columns: new[] { "PublicState", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DispatchedAtUtc_DueAtUtc",
                table: "OutboxMessages",
                columns: new[] { "DispatchedAtUtc", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_IdempotencyKey",
                table: "OutboxMessages",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PipelineRecordId",
                table: "OutboxMessages",
                column: "PipelineRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRecords_ParentRevisionRecordId",
                table: "PipelineRecords",
                column: "ParentRevisionRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRecords_RootLineageRecordId",
                table: "PipelineRecords",
                column: "RootLineageRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRecords_SourceIdentityId_Revision",
                table: "PipelineRecords",
                columns: new[] { "SourceIdentityId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceIdentities_SourceKind_StableKey",
                table: "SourceIdentities",
                columns: new[] { "SourceKind", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TextChunks_ArtifactId_Ordinal",
                table: "TextChunks",
                columns: new[] { "ArtifactId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vectors_IndexGenerationId",
                table: "Vectors",
                column: "IndexGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_Vectors_TextChunkId_ModelFingerprint_SourceRevision_IndexGenerationId",
                table: "Vectors",
                columns: new[] { "TextChunkId", "ModelFingerprint", "SourceRevision", "IndexGenerationId" },
                unique: true);

            migrationBuilder.Sql(
                """
                IF CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')) = 1
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = N'FluxKnowledge')
                    BEGIN
                        CREATE FULLTEXT CATALOG [FluxKnowledge] AS DEFAULT;
                    END;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM sys.fulltext_indexes
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[Artifacts]'))
                    BEGIN
                        CREATE FULLTEXT INDEX ON [dbo].[Artifacts]
                        (
                            [SearchText] LANGUAGE 1033
                        )
                        KEY INDEX [PK_Artifacts]
                        ON [FluxKnowledge]
                        WITH CHANGE_TRACKING AUTO;
                    END;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')) = 1
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM sys.fulltext_indexes
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[Artifacts]'))
                    BEGIN
                        DROP FULLTEXT INDEX ON [dbo].[Artifacts];
                    END;

                    IF EXISTS (
                        SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = N'FluxKnowledge')
                    BEGIN
                        DROP FULLTEXT CATALOG [FluxKnowledge];
                    END;
                END;
                """);

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "GpuMiniTasks");

            migrationBuilder.DropTable(
                name: "IndexState");

            migrationBuilder.DropTable(
                name: "JobAttempts");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "Vectors");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "IndexGenerations");

            migrationBuilder.DropTable(
                name: "TextChunks");

            migrationBuilder.DropTable(
                name: "Artifacts");

            migrationBuilder.DropTable(
                name: "PipelineRecords");

            migrationBuilder.DropTable(
                name: "SourceIdentities");
        }
    }
}
