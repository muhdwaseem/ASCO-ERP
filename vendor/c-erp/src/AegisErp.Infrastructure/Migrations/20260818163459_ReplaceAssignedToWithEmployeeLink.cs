using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAssignedToWithEmployeeLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "EstimateLines");

            migrationBuilder.AddColumn<int>(
                name: "AssignedToEmployeeId",
                table: "SalesInvoiceLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedToEmployeeId",
                table: "EstimateLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_AssignedToEmployeeId",
                table: "SalesInvoiceLines",
                column: "AssignedToEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_AssignedToEmployeeId",
                table: "EstimateLines",
                column: "AssignedToEmployeeId");

            migrationBuilder.AddForeignKey(
                name: "FK_EstimateLines_Employees_AssignedToEmployeeId",
                table: "EstimateLines",
                column: "AssignedToEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_Employees_AssignedToEmployeeId",
                table: "SalesInvoiceLines",
                column: "AssignedToEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EstimateLines_Employees_AssignedToEmployeeId",
                table: "EstimateLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoiceLines_Employees_AssignedToEmployeeId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoiceLines_AssignedToEmployeeId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_EstimateLines_AssignedToEmployeeId",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "AssignedToEmployeeId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "AssignedToEmployeeId",
                table: "EstimateLines");

            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "SalesInvoiceLines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "EstimateLines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }
    }
}
