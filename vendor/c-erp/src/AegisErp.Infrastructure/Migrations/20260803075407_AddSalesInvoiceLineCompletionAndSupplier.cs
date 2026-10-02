using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesInvoiceLineCompletionAndSupplier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "SalesInvoiceLines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                table: "SalesInvoiceLines",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompleted",
                table: "SalesInvoiceLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "SalesInvoiceLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_SupplierId",
                table: "SalesInvoiceLines",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_Vendors_SupplierId",
                table: "SalesInvoiceLines",
                column: "SupplierId",
                principalTable: "Vendors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoiceLines_Vendors_SupplierId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoiceLines_SupplierId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "IsCompleted",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "SalesInvoiceLines");
        }
    }
}
