using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class EngineFixesFor1128 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ComplexGatewayJoinStates",
                table: "ComplexGatewayJoinStates");

            migrationBuilder.AddColumn<Guid>(
                name: "PendingInstanceId",
                table: "TimerSchedulers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedTokenCount",
                table: "ComplexGatewayJoinStates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScopeId",
                table: "ComplexGatewayJoinStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions",
                columns: new[] { "SignalName", "Bucket", "WorkflowInstanceId", "ActivityId", "HostActivityInstanceId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ComplexGatewayJoinStates",
                table: "ComplexGatewayJoinStates",
                columns: new[] { "WorkflowInstanceId", "FirstActivityInstanceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ComplexGatewayJoinStates",
                table: "ComplexGatewayJoinStates");

            migrationBuilder.DropColumn(
                name: "PendingInstanceId",
                table: "TimerSchedulers");

            migrationBuilder.DropColumn(
                name: "ExpectedTokenCount",
                table: "ComplexGatewayJoinStates");

            migrationBuilder.DropColumn(
                name: "ScopeId",
                table: "ComplexGatewayJoinStates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions",
                columns: new[] { "SignalName", "Bucket", "WorkflowInstanceId", "ActivityId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ComplexGatewayJoinStates",
                table: "ComplexGatewayJoinStates",
                columns: new[] { "WorkflowInstanceId", "GatewayActivityId" });
        }
    }
}
