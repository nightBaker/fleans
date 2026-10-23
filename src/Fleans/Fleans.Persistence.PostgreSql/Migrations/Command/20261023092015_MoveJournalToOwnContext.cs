using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class MoveJournalToOwnContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #879: the journal tables now belong to FleanJournalDbContext, whose own migrations
            // create them (in this database by default). Pre-1.0: no upgrade path.
            migrationBuilder.DropTable(
                name: "WorkflowEventBatches");

            migrationBuilder.DropTable(
                name: "WorkflowSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowEventBatches",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstVersion = table.Column<int>(type: "integer", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastVersion = table.Column<int>(type: "integer", nullable: false),
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
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSnapshots", x => x.InstanceId);
                });
        }
    }
}
