using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorPaymentAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentContentType",
                table: "VendorPayments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AttachmentData",
                table: "VendorPayments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentFileName",
                table: "VendorPayments",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ChequeDate",
                table: "VendorPayments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMode",
                table: "VendorPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Cash");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceNo",
                table: "VendorPayments",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VendorPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorPaymentId = table.Column<int>(type: "integer", nullable: false),
                    PurchaseInvoiceLineId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorPaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorPaymentAllocations_PurchaseInvoiceLines_PurchaseInvoi~",
                        column: x => x.PurchaseInvoiceLineId,
                        principalTable: "PurchaseInvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorPaymentAllocations_VendorPayments_VendorPaymentId",
                        column: x => x.VendorPaymentId,
                        principalTable: "VendorPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorPaymentAllocations_PurchaseInvoiceLineId",
                table: "VendorPaymentAllocations",
                column: "PurchaseInvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorPaymentAllocations_VendorPaymentId",
                table: "VendorPaymentAllocations",
                column: "VendorPaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorPaymentAllocations");

            migrationBuilder.DropColumn(
                name: "AttachmentContentType",
                table: "VendorPayments");

            migrationBuilder.DropColumn(
                name: "AttachmentData",
                table: "VendorPayments");

            migrationBuilder.DropColumn(
                name: "AttachmentFileName",
                table: "VendorPayments");

            migrationBuilder.DropColumn(
                name: "ChequeDate",
                table: "VendorPayments");

            migrationBuilder.DropColumn(
                name: "PaymentMode",
                table: "VendorPayments");

            migrationBuilder.DropColumn(
                name: "ReferenceNo",
                table: "VendorPayments");
        }
    }
}
