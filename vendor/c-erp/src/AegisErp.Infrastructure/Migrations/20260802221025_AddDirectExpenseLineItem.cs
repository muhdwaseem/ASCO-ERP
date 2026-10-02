using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectExpenseLineItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ItemId",
                table: "DirectExpenseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpenseLines_ItemId",
                table: "DirectExpenseLines",
                column: "ItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_DirectExpenseLines_Items_ItemId",
                table: "DirectExpenseLines",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DirectExpenseLines_Items_ItemId",
                table: "DirectExpenseLines");

            migrationBuilder.DropIndex(
                name: "IX_DirectExpenseLines_ItemId",
                table: "DirectExpenseLines");

            migrationBuilder.DropColumn(
                name: "ItemId",
                table: "DirectExpenseLines");
        }
    }
}
