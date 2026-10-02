using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicantReferenceToSalesInvoiceLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicantReference",
                table: "SalesInvoiceLines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceSalesInvoiceLineId",
                table: "DirectExpenses",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpenses_SourceSalesInvoiceLineId",
                table: "DirectExpenses",
                column: "SourceSalesInvoiceLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_DirectExpenses_SalesInvoiceLines_SourceSalesInvoiceLineId",
                table: "DirectExpenses",
                column: "SourceSalesInvoiceLineId",
                principalTable: "SalesInvoiceLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DirectExpenses_SalesInvoiceLines_SourceSalesInvoiceLineId",
                table: "DirectExpenses");

            migrationBuilder.DropIndex(
                name: "IX_DirectExpenses_SourceSalesInvoiceLineId",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "ApplicantReference",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "SourceSalesInvoiceLineId",
                table: "DirectExpenses");
        }
    }
}
