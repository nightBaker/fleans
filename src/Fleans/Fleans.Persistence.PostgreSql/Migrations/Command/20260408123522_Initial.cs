using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageCorrelations",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageCorrelations", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MessageStartEventListeners",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageStartEventListeners", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MessageStartEventRegistrations",
                columns: table => new
                {
                    MessageName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProcessDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageStartEventRegistrations", x => new { x.MessageName, x.ProcessDefinitionKey });
                });

            migrationBuilder.CreateTable(
                name: "ProcessDefinitions",
                columns: table => new
                {
                    ProcessDefinitionId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProcessDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DeployedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Workflow = table.Column<string>(type: "text", nullable: false),
                    BpmnXml = table.Column<string>(type: "text", nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessDefinitions", x => x.ProcessDefinitionId);
                });

            migrationBuilder.CreateTable(
                name: "SignalCorrelations",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalCorrelations", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SignalStartEventListeners",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalStartEventListeners", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SignalStartEventRegistrations",
                columns: table => new
                {
                    SignalName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProcessDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalStartEventRegistrations", x => new { x.SignalName, x.ProcessDefinitionKey });
                });

            migrationBuilder.CreateTable(
                name: "TimerSchedulers",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProcessDefinitionId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FireCount = table.Column<int>(type: "integer", nullable: false),
                    MaxFireCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimerSchedulers", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GrainId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowSnapshots",
                columns: table => new
                {
                    GrainId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSnapshots", x => x.GrainId);
                });

            migrationBuilder.CreateTable(
                name: "MessageSubscriptions",
                columns: table => new
                {
                    MessageName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    HostActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageSubscriptions", x => x.MessageName);
                    table.ForeignKey(
                        name: "FK_MessageSubscriptions_MessageCorrelations_MessageName",
                        column: x => x.MessageName,
                        principalTable: "MessageCorrelations",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsStarted = table.Column<bool>(type: "boolean", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutionStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessDefinitionId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ParentWorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentActivityId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowInstances_ProcessDefinitions_ProcessDefinitionId",
                        column: x => x.ProcessDefinitionId,
                        principalTable: "ProcessDefinitions",
                        principalColumn: "ProcessDefinitionId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SignalSubscriptions",
                columns: table => new
                {
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SignalName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    HostActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalSubscriptions", x => new { x.SignalName, x.WorkflowInstanceId, x.ActivityId });
                    table.ForeignKey(
                        name: "FK_SignalSubscriptions_SignalCorrelations_SignalName",
                        column: x => x.SignalName,
                        principalTable: "SignalCorrelations",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GatewayForks",
                columns: table => new
                {
                    ForkInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedTokenIds = table.Column<string>(type: "text", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayForks", x => x.ForkInstanceId);
                    table.ForeignKey(
                        name: "FK_GatewayForks_WorkflowInstances_WorkflowInstanceId",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimerCycleTracking",
                columns: table => new
                {
                    HostActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimerActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TimerType = table.Column<int>(type: "integer", nullable: false),
                    TimerExpression = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimerCycleTracking", x => new { x.HostActivityInstanceId, x.TimerActivityId });
                    table.ForeignKey(
                        name: "FK_TimerCycleTracking_WorkflowInstances_WorkflowInstanceId",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTasks",
                columns: table => new
                {
                    ActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Assignee = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CandidateGroups = table.Column<string>(type: "text", nullable: false),
                    CandidateUsers = table.Column<string>(type: "text", nullable: false),
                    ExpectedOutputVariables = table.Column<string>(type: "text", nullable: true),
                    ClaimedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TaskState = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTasks", x => x.ActivityInstanceId);
                    table.ForeignKey(
                        name: "FK_UserTasks_WorkflowInstances_WorkflowInstanceId",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowActivityInstanceEntries",
                columns: table => new
                {
                    ActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    ChildWorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    MultiInstanceIndex = table.Column<int>(type: "integer", nullable: true),
                    ActivityType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsExecuting = table.Column<bool>(type: "boolean", nullable: false),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    CancellationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    VariablesId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErrorCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    MultiInstanceTotal = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutionStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowActivityInstanceEntries", x => x.ActivityInstanceId);
                    table.ForeignKey(
                        name: "FK_WorkflowActivityInstanceEntries_WorkflowInstances_WorkflowI~",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowConditionSequenceStates",
                columns: table => new
                {
                    GatewayActivityInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConditionalSequenceFlowId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Result = table.Column<bool>(type: "boolean", nullable: false),
                    IsEvaluated = table.Column<bool>(type: "boolean", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowConditionSequenceStates", x => new { x.GatewayActivityInstanceId, x.ConditionalSequenceFlowId });
                    table.ForeignKey(
                        name: "FK_WorkflowConditionSequenceStates_WorkflowInstances_WorkflowI~",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVariableStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Variables = table.Column<string>(type: "text", nullable: false),
                    ParentVariablesId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVariableStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowVariableStates_WorkflowInstances_WorkflowInstanceId",
                        column: x => x.WorkflowInstanceId,
                        principalTable: "WorkflowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayForks_WorkflowInstanceId",
                table: "GatewayForks",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessDefinitions_ProcessDefinitionKey",
                table: "ProcessDefinitions",
                column: "ProcessDefinitionKey");

            migrationBuilder.CreateIndex(
                name: "IX_TimerCycleTracking_WorkflowInstanceId",
                table: "TimerCycleTracking",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTasks_WorkflowInstanceId",
                table: "UserTasks",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowActivityInstanceEntries_WorkflowInstanceId",
                table: "WorkflowActivityInstanceEntries",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowConditionSequenceStates_WorkflowInstanceId",
                table: "WorkflowConditionSequenceStates",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_GrainId_Version",
                table: "WorkflowEvents",
                columns: new[] { "GrainId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_ProcessDefinitionId",
                table: "WorkflowInstances",
                column: "ProcessDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVariableStates_WorkflowInstanceId",
                table: "WorkflowVariableStates",
                column: "WorkflowInstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayForks");

            migrationBuilder.DropTable(
                name: "MessageStartEventListeners");

            migrationBuilder.DropTable(
                name: "MessageStartEventRegistrations");

            migrationBuilder.DropTable(
                name: "MessageSubscriptions");

            migrationBuilder.DropTable(
                name: "SignalStartEventListeners");

            migrationBuilder.DropTable(
                name: "SignalStartEventRegistrations");

            migrationBuilder.DropTable(
                name: "SignalSubscriptions");

            migrationBuilder.DropTable(
                name: "TimerCycleTracking");

            migrationBuilder.DropTable(
                name: "TimerSchedulers");

            migrationBuilder.DropTable(
                name: "UserTasks");

            migrationBuilder.DropTable(
                name: "WorkflowActivityInstanceEntries");

            migrationBuilder.DropTable(
                name: "WorkflowConditionSequenceStates");

            migrationBuilder.DropTable(
                name: "WorkflowEvents");

            migrationBuilder.DropTable(
                name: "WorkflowSnapshots");

            migrationBuilder.DropTable(
                name: "WorkflowVariableStates");

            migrationBuilder.DropTable(
                name: "MessageCorrelations");

            migrationBuilder.DropTable(
                name: "SignalCorrelations");

            migrationBuilder.DropTable(
                name: "WorkflowInstances");

            migrationBuilder.DropTable(
                name: "ProcessDefinitions");
        }
    }
}
