using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLockRecurringRemindersShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "SalesInvoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAtUtc",
                table: "SalesInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedBy",
                table: "SalesInvoices",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "SalesInvoices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceDefaultNotes",
                table: "CompanySetups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceDefaultPaymentTermsDays",
                table: "CompanySetups",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumberPrefix",
                table: "CompanySetups",
                type: "text",
                nullable: false,
                defaultValue: "INV");

            migrationBuilder.AddColumn<string>(
                name: "ReminderDaysAfterDue",
                table: "CompanySetups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderDaysBeforeDue",
                table: "CompanySetups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ReminderOnDueDate",
                table: "CompanySetups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InvoiceReminderLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsAutomated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceReminderLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceReminderLogs_SalesInvoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurringInvoiceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RepeatEvery = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextGenerationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Narration = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringInvoiceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceProfiles_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceProfiles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringInvoiceProfileLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecurringInvoiceProfileId = table.Column<int>(type: "integer", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RevenueAccountId = table.Column<int>(type: "integer", nullable: false),
                    CostCenterId = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringInvoiceProfileLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceProfileLines_Accounts_RevenueAccountId",
                        column: x => x.RevenueAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceProfileLines_CostCenters_CostCenterId",
                        column: x => x.CostCenterId,
                        principalTable: "CostCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceProfileLines_RecurringInvoiceProfiles_Recur~",
                        column: x => x.RecurringInvoiceProfileId,
                        principalTable: "RecurringInvoiceProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_ShareToken",
                table: "SalesInvoices",
                column: "ShareToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceReminderLogs_SalesInvoiceId",
                table: "InvoiceReminderLogs",
                column: "SalesInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceProfileLines_CostCenterId",
                table: "RecurringInvoiceProfileLines",
                column: "CostCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceProfileLines_RecurringInvoiceProfileId",
                table: "RecurringInvoiceProfileLines",
                column: "RecurringInvoiceProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceProfileLines_RevenueAccountId",
                table: "RecurringInvoiceProfileLines",
                column: "RevenueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceProfiles_CompanyId",
                table: "RecurringInvoiceProfiles",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceProfiles_CustomerId",
                table: "RecurringInvoiceProfiles",
                column: "CustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceReminderLogs");

            migrationBuilder.DropTable(
                name: "RecurringInvoiceProfileLines");

            migrationBuilder.DropTable(
                name: "RecurringInvoiceProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_ShareToken",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "LockedAtUtc",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ShareToken",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "InvoiceDefaultNotes",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "InvoiceDefaultPaymentTermsDays",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "InvoiceNumberPrefix",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "ReminderDaysAfterDue",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "ReminderDaysBeforeDue",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "ReminderOnDueDate",
                table: "CompanySetups");
        }
    }
}
