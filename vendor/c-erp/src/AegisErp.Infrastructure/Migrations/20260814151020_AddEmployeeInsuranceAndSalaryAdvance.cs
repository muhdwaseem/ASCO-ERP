using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeInsuranceAndSalaryAdvance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SalaryAdvanceDeduction",
                table: "PayrollRunLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SalaryAdvanceId",
                table: "PayrollRunLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InsuranceCoverageType",
                table: "Employees",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "InsurancePolicyExpiryDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InsurancePolicyNumber",
                table: "Employees",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InsuranceProvider",
                table: "Employees",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalaryAdvances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MonthlyDeductionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RemainingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalVoucherId = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryAdvances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryAdvances_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalaryAdvances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalaryAdvances_JournalVouchers_JournalVoucherId",
                        column: x => x.JournalVoucherId,
                        principalTable: "JournalVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLines_SalaryAdvanceId",
                table: "PayrollRunLines",
                column: "SalaryAdvanceId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_CompanyId",
                table: "SalaryAdvances",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_EmployeeId",
                table: "SalaryAdvances",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAdvances_JournalVoucherId",
                table: "SalaryAdvances",
                column: "JournalVoucherId");

            migrationBuilder.AddForeignKey(
                name: "FK_PayrollRunLines_SalaryAdvances_SalaryAdvanceId",
                table: "PayrollRunLines",
                column: "SalaryAdvanceId",
                principalTable: "SalaryAdvances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PayrollRunLines_SalaryAdvances_SalaryAdvanceId",
                table: "PayrollRunLines");

            migrationBuilder.DropTable(
                name: "SalaryAdvances");

            migrationBuilder.DropIndex(
                name: "IX_PayrollRunLines_SalaryAdvanceId",
                table: "PayrollRunLines");

            migrationBuilder.DropColumn(
                name: "SalaryAdvanceDeduction",
                table: "PayrollRunLines");

            migrationBuilder.DropColumn(
                name: "SalaryAdvanceId",
                table: "PayrollRunLines");

            migrationBuilder.DropColumn(
                name: "InsuranceCoverageType",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "InsurancePolicyExpiryDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "InsurancePolicyNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "InsuranceProvider",
                table: "Employees");
        }
    }
}
