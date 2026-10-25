using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class SignalBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SignalSubscriptions_SignalCorrelations_SignalName",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalCorrelations",
                table: "SignalCorrelations");

            migrationBuilder.RenameColumn(
                name: "Key",
                table: "SignalCorrelations",
                newName: "SignalName");

            migrationBuilder.AddColumn<int>(
                name: "Bucket",
                table: "SignalSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Bucket",
                table: "SignalCorrelations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions",
                columns: new[] { "SignalName", "Bucket", "WorkflowInstanceId", "ActivityId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalCorrelations",
                table: "SignalCorrelations",
                columns: new[] { "SignalName", "Bucket" });

            migrationBuilder.AddForeignKey(
                name: "FK_SignalSubscriptions_SignalCorrelations_SignalName_Bucket",
                table: "SignalSubscriptions",
                columns: new[] { "SignalName", "Bucket" },
                principalTable: "SignalCorrelations",
                principalColumns: new[] { "SignalName", "Bucket" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SignalSubscriptions_SignalCorrelations_SignalName_Bucket",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SignalCorrelations",
                table: "SignalCorrelations");

            migrationBuilder.DropColumn(
                name: "Bucket",
                table: "SignalSubscriptions");

            migrationBuilder.DropColumn(
                name: "Bucket",
                table: "SignalCorrelations");

            migrationBuilder.RenameColumn(
                name: "SignalName",
                table: "SignalCorrelations",
                newName: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalSubscriptions",
                table: "SignalSubscriptions",
                columns: new[] { "SignalName", "WorkflowInstanceId", "ActivityId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SignalCorrelations",
                table: "SignalCorrelations",
                column: "Key");

            migrationBuilder.AddForeignKey(
                name: "FK_SignalSubscriptions_SignalCorrelations_SignalName",
                table: "SignalSubscriptions",
                column: "SignalName",
                principalTable: "SignalCorrelations",
                principalColumn: "Key",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
