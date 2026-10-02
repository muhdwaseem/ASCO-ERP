using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGratuityLeaveWpsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EmiratesIdExpiryDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmiratesIdNumber",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GratuityEligible",
                table: "Employees",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LabourCardExpiryDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LabourCardNumber",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PassportExpiryDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassportNumber",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "VisaExpiryDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WpsAgentId",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MohreEstablishmentId",
                table: "CompanySetups",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GratuityPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    TerminationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    YearsOfService = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    BasicSalaryUsed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CalculatedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpenseAccountId = table.Column<int>(type: "integer", nullable: true),
                    JournalVoucherId = table.Column<int>(type: "integer", nullable: true),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidFromBankAccountId = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GratuityPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GratuityPayments_Accounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GratuityPayments_Accounts_PaidFromBankAccountId",
                        column: x => x.PaidFromBankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GratuityPayments_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GratuityPayments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GratuityPayments_JournalVouchers_JournalVoucherId",
                        column: x => x.JournalVoucherId,
                        principalTable: "JournalVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaveRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecisionBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DecisionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaveRequests_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaveRequests_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GratuityPayments_CompanyId",
                table: "GratuityPayments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GratuityPayments_EmployeeId",
                table: "GratuityPayments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_GratuityPayments_ExpenseAccountId",
                table: "GratuityPayments",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GratuityPayments_JournalVoucherId",
                table: "GratuityPayments",
                column: "JournalVoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_GratuityPayments_PaidFromBankAccountId",
                table: "GratuityPayments",
                column: "PaidFromBankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_CompanyId",
                table: "LeaveRequests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequests_EmployeeId",
                table: "LeaveRequests",
                column: "EmployeeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GratuityPayments");

            migrationBuilder.DropTable(
                name: "LeaveRequests");

            migrationBuilder.DropColumn(
                name: "EmiratesIdExpiryDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "EmiratesIdNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "GratuityEligible",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LabourCardExpiryDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LabourCardNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PassportExpiryDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PassportNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "VisaExpiryDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "WpsAgentId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "MohreEstablishmentId",
                table: "CompanySetups");
        }
    }
}
