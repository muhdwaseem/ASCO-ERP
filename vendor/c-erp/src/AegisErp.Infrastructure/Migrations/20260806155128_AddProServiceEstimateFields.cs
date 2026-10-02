using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProServiceEstimateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingAddressSnapshot",
                table: "Estimates",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Estimates",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactMobile",
                table: "Estimates",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPerson",
                table: "Estimates",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactTrn",
                table: "Estimates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Estimates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "EstimateLines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BankCharge",
                table: "EstimateLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GovtFee",
                table: "EstimateLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_OrganizationId",
                table: "Estimates",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Estimates_CustomerOrganizations_OrganizationId",
                table: "Estimates",
                column: "OrganizationId",
                principalTable: "CustomerOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Estimates_CustomerOrganizations_OrganizationId",
                table: "Estimates");

            migrationBuilder.DropIndex(
                name: "IX_Estimates_OrganizationId",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "BillingAddressSnapshot",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ContactMobile",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ContactPerson",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ContactTrn",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "BankCharge",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "GovtFee",
                table: "EstimateLines");
        }
    }
}
