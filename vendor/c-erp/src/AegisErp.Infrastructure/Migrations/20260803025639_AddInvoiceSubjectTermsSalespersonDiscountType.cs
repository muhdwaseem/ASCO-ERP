using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceSubjectTermsSalespersonDiscountType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename rather than drop+add — existing lines' discount percentages must survive.
            // Every pre-existing value was a percentage, so DiscountType defaults to "Percent"
            // for them (not the EF-scaffolded "", which isn't a valid enum member).
            migrationBuilder.RenameColumn(
                name: "DiscountPercent",
                table: "SalesInvoiceLines",
                newName: "DiscountValue");

            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountValue",
                table: "SalesInvoiceLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldPrecision: 5,
                oldScale: 2);

            migrationBuilder.AddColumn<string>(
                name: "Salesperson",
                table: "SalesInvoices",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "SalesInvoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsAndConditions",
                table: "SalesInvoices",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountType",
                table: "SalesInvoiceLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Percent");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceDefaultTermsAndConditions",
                table: "CompanySetups",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Salesperson",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "TermsAndConditions",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "DiscountType",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "InvoiceDefaultTermsAndConditions",
                table: "CompanySetups");

            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountValue",
                table: "SalesInvoiceLines",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.RenameColumn(
                name: "DiscountValue",
                table: "SalesInvoiceLines",
                newName: "DiscountPercent");
        }
    }
}
