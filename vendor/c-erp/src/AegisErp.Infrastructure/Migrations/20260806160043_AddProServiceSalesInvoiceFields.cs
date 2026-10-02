using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProServiceSalesInvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingAddressSnapshot",
                table: "SalesInvoices",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "SalesInvoices",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactMobile",
                table: "SalesInvoices",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPerson",
                table: "SalesInvoices",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactTrn",
                table: "SalesInvoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "SalesInvoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "SalesInvoiceLines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BankCharge",
                table: "SalesInvoiceLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GovtFee",
                table: "SalesInvoiceLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_OrganizationId",
                table: "SalesInvoices",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_CustomerOrganizations_OrganizationId",
                table: "SalesInvoices",
                column: "OrganizationId",
                principalTable: "CustomerOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_CustomerOrganizations_OrganizationId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_OrganizationId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "BillingAddressSnapshot",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ContactMobile",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ContactPerson",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ContactTrn",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "BankCharge",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "GovtFee",
                table: "SalesInvoiceLines");
        }
    }
}
