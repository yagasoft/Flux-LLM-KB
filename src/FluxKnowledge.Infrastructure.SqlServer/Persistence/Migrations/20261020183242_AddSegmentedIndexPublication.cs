using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentedIndexPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProjectionChangeFloorVersion",
                table: "IndexState",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RequiredIndexFormat",
                table: "IndexState",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "FormatVersion",
                table: "IndexGenerations",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SegmentCount",
                table: "IndexGenerations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "IndexProjectionChanges",
                columns: table => new
                {
                    CorpusEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusVersion = table.Column<long>(type: "bigint", nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexProjectionChanges", x => new { x.CorpusEpoch, x.CorpusVersion, x.ScopeKind, x.ScopeId });
                    table.CheckConstraint("CK_IndexProjectionChanges_Scope", "[CorpusVersion] >= 0 AND [ScopeKind] BETWEEN 1 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "IndexPublicationCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedCorpusVersion = table.Column<long>(type: "bigint", nullable: false),
                    PublicationCorpusVersion = table.Column<long>(type: "bigint", nullable: false),
                    BaseGenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DraftGenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerLeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OwnerLeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResultGenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AffectedScopesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BeforeChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    AfterChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexPublicationCandidates", x => x.Id);
                    table.CheckConstraint("CK_IndexPublicationCandidates_State", "[State] BETWEEN 0 AND 3 AND [ExpectedCorpusVersion] >= 0 AND [PublicationCorpusVersion] >= [ExpectedCorpusVersion]");
                    table.ForeignKey(
                        name: "FK_IndexPublicationCandidates_IndexGenerations_BaseGenerationId",
                        column: x => x.BaseGenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexPublicationCandidates_IndexGenerations_DraftGenerationId",
                        column: x => x.DraftGenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexPublicationCandidates_IndexGenerations_ResultGenerationId",
                        column: x => x.ResultGenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexPublicationCandidates_Jobs_OwnerJobId",
                        column: x => x.OwnerJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndexSegments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ModelFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    NativeFormat = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DirectoryPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    MembershipChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FileChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FileLength = table.Column<long>(type: "bigint", nullable: false),
                    VectorCount = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ProducingCandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetirementOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    SealedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ReclaimedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexSegments", x => x.Id);
                    table.CheckConstraint("CK_IndexSegments_Profile", "[Dimensions] > 0 AND [VectorCount] > 0 AND [FileLength] > 0");
                    table.CheckConstraint("CK_IndexSegments_State", "[State] BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_IndexSegments_IndexPublicationCandidates_ProducingCandidateId",
                        column: x => x.ProducingCandidateId,
                        principalTable: "IndexPublicationCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndexGenerationSegments",
                columns: table => new
                {
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexGenerationSegments", x => new { x.GenerationId, x.SegmentId });
                    table.CheckConstraint("CK_IndexGenerationSegments_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_IndexGenerationSegments_IndexGenerations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexGenerationSegments_IndexSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "IndexSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndexSegmentOwners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    OwnerInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerProcessId = table.Column<int>(type: "int", nullable: false),
                    OwnerStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    OwnerMachineFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexSegmentOwners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndexSegmentOwners_IndexSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "IndexSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndexSegmentVectors",
                columns: table => new
                {
                    SegmentId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    VectorId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexSegmentVectors", x => new { x.SegmentId, x.VectorId });
                    table.ForeignKey(
                        name: "FK_IndexSegmentVectors_IndexSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "IndexSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexSegmentVectors_Vectors_VectorId",
                        column: x => x.VectorId,
                        principalTable: "Vectors",
                        principalColumn: "VectorId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CurrentIndexMembers",
                columns: table => new
                {
                    CorpusEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VectorId = table.Column<long>(type: "bigint", nullable: false),
                    SegmentId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentIndexMembers", x => new { x.CorpusEpoch, x.VectorId });
                    table.ForeignKey(
                        name: "FK_CurrentIndexMembers_IndexSegmentVectors_SegmentId_VectorId",
                        columns: x => new { x.SegmentId, x.VectorId },
                        principalTable: "IndexSegmentVectors",
                        principalColumns: new[] { "SegmentId", "VectorId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "IndexState",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ProjectionChangeFloorVersion", "RequiredIndexFormat" },
                values: new object[] { 0L, 1 });

            migrationBuilder.CreateIndex(
                name: "IX_CurrentIndexMembers_CorpusEpoch_OwnerSourceRevisionId_VectorId",
                table: "CurrentIndexMembers",
                columns: new[] { "CorpusEpoch", "OwnerSourceRevisionId", "VectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrentIndexMembers_CorpusEpoch_SourceIdentityId_VectorId",
                table: "CurrentIndexMembers",
                columns: new[] { "CorpusEpoch", "SourceIdentityId", "VectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrentIndexMembers_CorpusEpoch_SourceRootId_VectorId",
                table: "CurrentIndexMembers",
                columns: new[] { "CorpusEpoch", "SourceRootId", "VectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrentIndexMembers_SegmentId_VectorId",
                table: "CurrentIndexMembers",
                columns: new[] { "SegmentId", "VectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerationSegments_GenerationId_Ordinal",
                table: "IndexGenerationSegments",
                columns: new[] { "GenerationId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerationSegments_SegmentId",
                table: "IndexGenerationSegments",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexPublicationCandidates_BaseGenerationId",
                table: "IndexPublicationCandidates",
                column: "BaseGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexPublicationCandidates_DraftGenerationId",
                table: "IndexPublicationCandidates",
                column: "DraftGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexPublicationCandidates_OwnerJobId",
                table: "IndexPublicationCandidates",
                column: "OwnerJobId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexPublicationCandidates_ResultGenerationId",
                table: "IndexPublicationCandidates",
                column: "ResultGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexPublicationCandidates_State_CreatedAtUtc",
                table: "IndexPublicationCandidates",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexSegmentOwners_SegmentId_OwnerInstanceId",
                table: "IndexSegmentOwners",
                columns: new[] { "SegmentId", "OwnerInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndexSegments_ProducingCandidateId",
                table: "IndexSegments",
                column: "ProducingCandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexSegments_State_CreatedAtUtc",
                table: "IndexSegments",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexSegmentVectors_VectorId",
                table: "IndexSegmentVectors",
                column: "VectorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrentIndexMembers");

            migrationBuilder.DropTable(
                name: "IndexGenerationSegments");

            migrationBuilder.DropTable(
                name: "IndexProjectionChanges");

            migrationBuilder.DropTable(
                name: "IndexSegmentOwners");

            migrationBuilder.DropTable(
                name: "IndexSegmentVectors");

            migrationBuilder.DropTable(
                name: "IndexSegments");

            migrationBuilder.DropTable(
                name: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "ProjectionChangeFloorVersion",
                table: "IndexState");

            migrationBuilder.DropColumn(
                name: "RequiredIndexFormat",
                table: "IndexState");

            migrationBuilder.DropColumn(
                name: "FormatVersion",
                table: "IndexGenerations");

            migrationBuilder.DropColumn(
                name: "SegmentCount",
                table: "IndexGenerations");
        }
    }
}
