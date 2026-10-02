using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectExpenseVatInvoiceAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AmountsIncludeVat",
                table: "DirectExpenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentContentType",
                table: "DirectExpenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AttachmentData",
                table: "DirectExpenses",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentFileName",
                table: "DirectExpenses",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorInvoiceNo",
                table: "DirectExpenses",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRate",
                table: "DirectExpenseLines",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountsIncludeVat",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "AttachmentContentType",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "AttachmentData",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "AttachmentFileName",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "VendorInvoiceNo",
                table: "DirectExpenses");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "DirectExpenseLines");
        }
    }
}
