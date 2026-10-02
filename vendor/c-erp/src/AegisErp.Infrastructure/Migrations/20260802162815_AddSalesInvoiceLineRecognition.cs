using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesInvoiceLineRecognition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Recognition",
                table: "SalesInvoiceLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Direct");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Recognition",
                table: "SalesInvoiceLines");
        }
    }
}
