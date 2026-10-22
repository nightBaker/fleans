using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class DropWorkflowInstanceProjectedVersionAndETag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ETag",
                table: "WorkflowInstances");

            migrationBuilder.DropColumn(
                name: "ProjectedVersion",
                table: "WorkflowInstances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ETag",
                table: "WorkflowInstances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectedVersion",
                table: "WorkflowInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
