using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class AddWorkflowInstanceBusinessId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessId",
                table: "WorkflowInstances",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_BusinessId_ProcessDefinitionId",
                table: "WorkflowInstances",
                columns: new[] { "BusinessId", "ProcessDefinitionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowInstances_BusinessId_ProcessDefinitionId",
                table: "WorkflowInstances");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "WorkflowInstances");
        }
    }
}
