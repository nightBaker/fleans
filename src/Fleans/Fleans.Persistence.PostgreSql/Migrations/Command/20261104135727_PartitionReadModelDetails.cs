using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class PartitionReadModelDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InstanceCompletedAt",
                table: "WorkflowVariableStates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InstanceCompletedAt",
                table: "WorkflowConditionSequenceStates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InstanceCompletedAt",
                table: "WorkflowActivityInstanceEntries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Pre-1.0: a fresh database is the upgrade path. Fail rather than drop details.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM "WorkflowActivityInstanceEntries")
                     OR EXISTS (SELECT 1 FROM "WorkflowVariableStates")
                     OR EXISTS (SELECT 1 FROM "WorkflowConditionSequenceStates") THEN
                    RAISE EXCEPTION 'PartitionReadModelDetails needs empty read-model detail tables: pre-1.0 the upgrade path is a fresh database';
                  END IF;
                END $$;
                """);

            // The tables become range-partitioned by InstanceCompletedAt (#980): month partitions are
            // created by PartitionedReadModelRetention, everything else lands in the default one. The
            // primary keys must contain the partition column, and the foreign keys to WorkflowInstances
            // go: the purge drops a month whole, after its instance rows, without a cascade.
            foreach (var (table, key) in Partitioned)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}" RENAME TO "{table}_unpartitioned";
                    CREATE TABLE "{table}" (LIKE "{table}_unpartitioned" INCLUDING DEFAULTS)
                        PARTITION BY RANGE ("InstanceCompletedAt");
                    DROP TABLE "{table}_unpartitioned";
                    ALTER TABLE "{table}" ADD CONSTRAINT "PK_{table}" PRIMARY KEY ({key}, "InstanceCompletedAt");
                    CREATE INDEX "IX_{table}_WorkflowInstanceId" ON "{table}" ("WorkflowInstanceId");
                    CREATE TABLE "{table}_default" PARTITION OF "{table}" DEFAULT;
                    """);
            }
        }

        private static readonly (string Table, string Key)[] Partitioned =
        [
            ("WorkflowActivityInstanceEntries", "\"ActivityInstanceId\""),
            ("WorkflowVariableStates", "\"Id\""),
            ("WorkflowConditionSequenceStates", "\"GatewayActivityInstanceId\", \"ConditionalSequenceFlowId\""),
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, key) in Partitioned)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}" RENAME TO "{table}_partitioned";
                    CREATE TABLE "{table}" (LIKE "{table}_partitioned" INCLUDING DEFAULTS);
                    DROP TABLE "{table}_partitioned";
                    ALTER TABLE "{table}" ADD CONSTRAINT "PK_{table}" PRIMARY KEY ({key});
                    CREATE INDEX "IX_{table}_WorkflowInstanceId" ON "{table}" ("WorkflowInstanceId");
                    ALTER TABLE "{table}" ADD CONSTRAINT "FK_{table}_WorkflowInstances_WorkflowInstanceId"
                        FOREIGN KEY ("WorkflowInstanceId") REFERENCES "WorkflowInstances" ("Id") ON DELETE CASCADE;
                    """);
            }

            migrationBuilder.DropColumn(
                name: "InstanceCompletedAt",
                table: "WorkflowVariableStates");

            migrationBuilder.DropColumn(
                name: "InstanceCompletedAt",
                table: "WorkflowConditionSequenceStates");

            migrationBuilder.DropColumn(
                name: "InstanceCompletedAt",
                table: "WorkflowActivityInstanceEntries");
        }
    }
}
