using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class AddComplexGatewayAndConditionalStartEventTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComplexGatewayJoinStates",
                columns: table => new
                {
                    GatewayActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WaitingTokenCount = table.Column<int>(type: "integer", nullable: false),
                    HasFired = table.Column<bool>(type: "boolean", nullable: false),
                    ActivationCondition = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FirstActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplexGatewayJoinStates", x => new { x.WorkflowInstanceId, x.GatewayActivityId });
                    table.ForeignKey(
                        name: "FK_ComplexGatewayJoinStates_WorkflowInstances_WorkflowInstance~",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConditionalStartEventListeners",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProcessDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConditionExpression = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsRegistered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConditionalStartEventListeners", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ConditionalStartEventRegistryEntries",
                columns: table => new
                {
                    ProcessDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConditionExpression = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConditionalStartEventRegistryEntries", x => new { x.ProcessDefinitionKey, x.ActivityId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplexGatewayJoinStates");

            migrationBuilder.DropTable(
                name: "ConditionalStartEventListeners");

            migrationBuilder.DropTable(
                name: "ConditionalStartEventRegistryEntries");
        }
    }
}
