using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectExpensePayLater : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "BankAccountId",
                table: "DirectExpenses",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "IsPayLater",
                table: "DirectExpenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DirectExpensePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    PaymentNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DirectExpenseId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    FiscalPeriodId = table.Column<int>(type: "integer", nullable: false),
                    BankAccountId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReferenceNo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ChequeDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Narration = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    JournalVoucherId = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectExpensePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectExpensePayments_Accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectExpensePayments_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectExpensePayments_DirectExpenses_DirectExpenseId",
                        column: x => x.DirectExpenseId,
                        principalTable: "DirectExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectExpensePayments_FiscalPeriods_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectExpensePayments_JournalVouchers_JournalVoucherId",
                        column: x => x.JournalVoucherId,
                        principalTable: "JournalVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectExpensePaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DirectExpensePaymentId = table.Column<int>(type: "integer", nullable: false),
                    DirectExpenseLineId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectExpensePaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectExpensePaymentAllocations_DirectExpenseLines_DirectEx~",
                        column: x => x.DirectExpenseLineId,
                        principalTable: "DirectExpenseLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectExpensePaymentAllocations_DirectExpensePayments_Direc~",
                        column: x => x.DirectExpensePaymentId,
                        principalTable: "DirectExpensePayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePaymentAllocations_DirectExpenseLineId",
                table: "DirectExpensePaymentAllocations",
                column: "DirectExpenseLineId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePaymentAllocations_DirectExpensePaymentId",
                table: "DirectExpensePaymentAllocations",
                column: "DirectExpensePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_BankAccountId",
                table: "DirectExpensePayments",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_CompanyId",
                table: "DirectExpensePayments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_CompanyId_PaymentNo",
                table: "DirectExpensePayments",
                columns: new[] { "CompanyId", "PaymentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_DirectExpenseId",
                table: "DirectExpensePayments",
                column: "DirectExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_FiscalPeriodId",
                table: "DirectExpensePayments",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectExpensePayments_JournalVoucherId",
                table: "DirectExpensePayments",
                column: "JournalVoucherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectExpensePaymentAllocations");

            migrationBuilder.DropTable(
                name: "DirectExpensePayments");

            migrationBuilder.DropColumn(
                name: "IsPayLater",
                table: "DirectExpenses");

            migrationBuilder.AlterColumn<int>(
                name: "BankAccountId",
                table: "DirectExpenses",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
