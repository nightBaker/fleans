using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class CompactJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #867: the journal moves to Orleans-binary batches. Pre-1.0 there is no upgrade path,
            // so the old JSON journal is dropped and the snapshots (a cache over it) are cleared —
            // otherwise the new InstanceId key would collide on its default value.
            migrationBuilder.Sql("DELETE FROM \"WorkflowSnapshots\";");

            migrationBuilder.DropTable(
                name: "WorkflowEvents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WorkflowSnapshots",
                table: "WorkflowSnapshots");

            migrationBuilder.DropColumn(
                name: "GrainId",
                table: "WorkflowSnapshots");

            migrationBuilder.AddColumn<Guid>(
                name: "InstanceId",
                table: "WorkflowSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_WorkflowSnapshots",
                table: "WorkflowSnapshots",
                column: "InstanceId");

            migrationBuilder.CreateTable(
                name: "WorkflowEventBatches",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstVersion = table.Column<int>(type: "integer", nullable: false),
                    LastVersion = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEventBatches", x => new { x.InstanceId, x.FirstVersion });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowEventBatches");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WorkflowSnapshots",
                table: "WorkflowSnapshots");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                table: "WorkflowSnapshots");

            migrationBuilder.AddColumn<string>(
                name: "GrainId",
                table: "WorkflowSnapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_WorkflowSnapshots",
                table: "WorkflowSnapshots",
                column: "GrainId");

            migrationBuilder.CreateTable(
                name: "WorkflowEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GrainId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_GrainId_Version",
                table: "WorkflowEvents",
                columns: new[] { "GrainId", "Version" },
                unique: true);
        }
    }
}
