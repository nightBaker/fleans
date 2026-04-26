using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.Sqlite.Migrations.Command
{
    /// <inheritdoc />
    public partial class ErrorCodeStringType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite does not support ALTER COLUMN. Use the rename+copy pattern.
            migrationBuilder.Sql(@"
                ALTER TABLE ""WorkflowActivityInstanceEntries"" ADD COLUMN ""ErrorCode_new"" TEXT;
                UPDATE ""WorkflowActivityInstanceEntries"" SET ""ErrorCode_new"" = CAST(""ErrorCode"" AS TEXT);
            ");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                table: "WorkflowActivityInstanceEntries");

            migrationBuilder.RenameColumn(
                name: "ErrorCode_new",
                table: "WorkflowActivityInstanceEntries",
                newName: "ErrorCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""WorkflowActivityInstanceEntries"" ADD COLUMN ""ErrorCode_old"" INTEGER;
                UPDATE ""WorkflowActivityInstanceEntries"" SET ""ErrorCode_old"" = CAST(""ErrorCode"" AS INTEGER) WHERE ""ErrorCode"" IS NOT NULL AND ""ErrorCode"" GLOB '[0-9]*';
            ");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                table: "WorkflowActivityInstanceEntries");

            migrationBuilder.RenameColumn(
                name: "ErrorCode_old",
                table: "WorkflowActivityInstanceEntries",
                newName: "ErrorCode");
        }
    }
}
