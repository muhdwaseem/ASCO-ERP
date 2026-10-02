using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseInvoiceProServiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CostCenterId",
                table: "PurchaseInvoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NonTaxableAmount",
                table: "PurchaseInvoiceLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "ProServiceInvoiceEnabled",
                table: "CompanySetups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseInvoices_CostCenterId",
                table: "PurchaseInvoices",
                column: "CostCenterId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseInvoices_CostCenters_CostCenterId",
                table: "PurchaseInvoices",
                column: "CostCenterId",
                principalTable: "CostCenters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseInvoices_CostCenters_CostCenterId",
                table: "PurchaseInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseInvoices_CostCenterId",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "CostCenterId",
                table: "PurchaseInvoices");

            migrationBuilder.DropColumn(
                name: "NonTaxableAmount",
                table: "PurchaseInvoiceLines");

            migrationBuilder.DropColumn(
                name: "ProServiceInvoiceEnabled",
                table: "CompanySetups");
        }
    }
}
