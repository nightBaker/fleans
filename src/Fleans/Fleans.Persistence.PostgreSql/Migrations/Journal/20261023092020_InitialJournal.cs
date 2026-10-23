using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Journal
{
    /// <inheritdoc />
    public partial class InitialJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowEventBatches",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstVersion = table.Column<int>(type: "integer", nullable: false),
                    LastVersion = table.Column<int>(type: "integer", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEventBatches", x => new { x.InstanceId, x.FirstVersion });
                });

            migrationBuilder.CreateTable(
                name: "WorkflowSnapshots",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSnapshots", x => x.InstanceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowEventBatches");

            migrationBuilder.DropTable(
                name: "WorkflowSnapshots");
        }
    }
}
