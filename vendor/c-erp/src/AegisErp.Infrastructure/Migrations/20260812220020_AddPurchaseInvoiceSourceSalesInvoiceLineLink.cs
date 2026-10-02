using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseInvoiceSourceSalesInvoiceLineLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceSalesInvoiceLineId",
                table: "PurchaseInvoices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_SourceSalesInvoiceLineId",
                table: "PurchaseInvoices",
                column: "SourceSalesInvoiceLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseInvoices_SalesInvoiceLines_SourceSalesInvoiceLineId",
                table: "PurchaseInvoices",
                column: "SourceSalesInvoiceLineId",
                principalTable: "SalesInvoiceLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseInvoices_SalesInvoiceLines_SourceSalesInvoiceLineId",
                table: "PurchaseInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoices_SourceSalesInvoiceLineId",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "SourceSalesInvoiceLineId",
                table: "PurchaseInvoices");
        }
    }
}
