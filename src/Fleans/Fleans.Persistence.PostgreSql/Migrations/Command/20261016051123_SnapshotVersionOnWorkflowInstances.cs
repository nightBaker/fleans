using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class SnapshotVersionOnWorkflowInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowSnapshots");

            migrationBuilder.AddColumn<int>(
                name: "SnapshotVersion",
                table: "WorkflowInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotVersion",
                table: "WorkflowInstances");

            migrationBuilder.CreateTable(
                name: "WorkflowSnapshots",
                columns: table => new
                {
                    GrainId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSnapshots", x => x.GrainId);
                });
        }
    }
}
