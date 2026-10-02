using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxCodeTypeAndAccountMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Kind",
                table: "TaxCodes",
                newName: "TaxType");

            // The renamed column still holds the OLD TaxCodeKind string values on any row that
            // existed before this migration — "Output"/"Input" aren't valid VatTaxType members, so
            // without remapping them here, loading that row would throw. "Exempt"/"ReverseCharge"
            // already match a VatTaxType member name and need no change. There's no lossless
            // inverse for Output/Input (both collapse to StandardRated), so Down() doesn't attempt
            // to remap them back.
            migrationBuilder.Sql("UPDATE \"TaxCodes\" SET \"TaxType\" = 'StandardRated' WHERE \"TaxType\" IN ('Output', 'Input');");

            migrationBuilder.AddColumn<int>(
                name: "InputAccountId",
                table: "TaxCodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputAccountId",
                table: "TaxCodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_InputAccountId",
                table: "TaxCodes",
                column: "InputAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_OutputAccountId",
                table: "TaxCodes",
                column: "OutputAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaxCodes_Accounts_InputAccountId",
                table: "TaxCodes",
                column: "InputAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxCodes_Accounts_OutputAccountId",
                table: "TaxCodes",
                column: "OutputAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaxCodes_Accounts_InputAccountId",
                table: "TaxCodes");

            migrationBuilder.DropForeignKey(
                name: "FK_TaxCodes_Accounts_OutputAccountId",
                table: "TaxCodes");

            migrationBuilder.DropIndex(
                name: "IX_TaxCodes_InputAccountId",
                table: "TaxCodes");

            migrationBuilder.DropIndex(
                name: "IX_TaxCodes_OutputAccountId",
                table: "TaxCodes");

            migrationBuilder.DropColumn(
                name: "InputAccountId",
                table: "TaxCodes");

            migrationBuilder.DropColumn(
                name: "OutputAccountId",
                table: "TaxCodes");

            migrationBuilder.RenameColumn(
                name: "TaxType",
                table: "TaxCodes",
                newName: "Kind");
        }
    }
}
