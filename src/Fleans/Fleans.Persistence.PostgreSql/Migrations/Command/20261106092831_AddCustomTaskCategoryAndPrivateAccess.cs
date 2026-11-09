using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleans.Persistence.PostgreSql.Migrations.Command
{
    /// <inheritdoc />
    public partial class AddCustomTaskCategoryAndPrivateAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "CustomTaskCatalogEntries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                table: "CustomTaskCatalogEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CustomTaskPrivateAccess",
                columns: table => new
                {
                    TaskType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AllowedProcessKeys = table.Column<string>(type: "text", nullable: false),
                    AllowListManaged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomTaskPrivateAccess", x => x.TaskType);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomTaskPrivateAccess");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "CustomTaskCatalogEntries");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                table: "CustomTaskCatalogEntries");
        }
    }
}
