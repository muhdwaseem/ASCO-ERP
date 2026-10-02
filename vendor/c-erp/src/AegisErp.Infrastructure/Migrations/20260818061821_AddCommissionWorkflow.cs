using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "Items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedAgentId",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedEmployeeId",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    AgentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Email = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BankName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Iban = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReportsToEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Agents_Employees_ReportsToEmployeeId",
                        column: x => x.ReportsToEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionPositionRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RateMultiplier = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionPositionRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionPositionRates_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionProfitSlabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    GrossProfitFrom = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossProfitTo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CommissionAdditionPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionProfitSlabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionProfitSlabs_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemCategories_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAssignmentHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: true),
                    AgentId = table.Column<int>(type: "integer", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAssignmentHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAssignmentHistories_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAssignmentHistories_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAssignmentHistories_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerAssignmentHistories_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesCommissionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    SalesInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    SalesInvoiceLineId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: true),
                    AgentId = table.Column<int>(type: "integer", nullable: true),
                    GrossProfit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsEligible = table.Column<bool>(type: "boolean", nullable: false),
                    AgentCommission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AdjustedProfit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployeeCommission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesCommissionRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesCommissionRecords_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesCommissionRecords_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesCommissionRecords_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesCommissionRecords_SalesInvoiceLines_SalesInvoiceLineId",
                        column: x => x.SalesInvoiceLineId,
                        principalTable: "SalesInvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesCommissionRecords_SalesInvoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionCategoryRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ItemCategoryId = table.Column<int>(type: "integer", nullable: false),
                    BaseCommissionPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionCategoryRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionCategoryRates_CompanySetups_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommissionCategoryRates_ItemCategories_ItemCategoryId",
                        column: x => x.ItemCategoryId,
                        principalTable: "ItemCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_CategoryId",
                table: "Items",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AssignedAgentId",
                table: "Customers",
                column: "AssignedAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AssignedEmployeeId",
                table: "Customers",
                column: "AssignedEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CompanyId",
                table: "Agents",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CompanyId_AgentCode",
                table: "Agents",
                columns: new[] { "CompanyId", "AgentCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ReportsToEmployeeId",
                table: "Agents",
                column: "ReportsToEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionCategoryRates_CompanyId",
                table: "CommissionCategoryRates",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionCategoryRates_ItemCategoryId",
                table: "CommissionCategoryRates",
                column: "ItemCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionPositionRates_CompanyId",
                table: "CommissionPositionRates",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionProfitSlabs_CompanyId",
                table: "CommissionProfitSlabs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssignmentHistories_AgentId",
                table: "CustomerAssignmentHistories",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssignmentHistories_CompanyId",
                table: "CustomerAssignmentHistories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssignmentHistories_CustomerId_EffectiveFrom",
                table: "CustomerAssignmentHistories",
                columns: new[] { "CustomerId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAssignmentHistories_EmployeeId",
                table: "CustomerAssignmentHistories",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemCategories_CompanyId",
                table: "ItemCategories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemCategories_CompanyId_Name",
                table: "ItemCategories",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommissionRecords_AgentId",
                table: "SalesCommissionRecords",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommissionRecords_CompanyId",
                table: "SalesCommissionRecords",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommissionRecords_EmployeeId",
                table: "SalesCommissionRecords",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommissionRecords_SalesInvoiceId",
                table: "SalesCommissionRecords",
                column: "SalesInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommissionRecords_SalesInvoiceLineId",
                table: "SalesCommissionRecords",
                column: "SalesInvoiceLineId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Agents_AssignedAgentId",
                table: "Customers",
                column: "AssignedAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Employees_AssignedEmployeeId",
                table: "Customers",
                column: "AssignedEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Items_ItemCategories_CategoryId",
                table: "Items",
                column: "CategoryId",
                principalTable: "ItemCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Agents_AssignedAgentId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Employees_AssignedEmployeeId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_Items_ItemCategories_CategoryId",
                table: "Items");

            migrationBuilder.DropTable(
                name: "CommissionCategoryRates");

            migrationBuilder.DropTable(
                name: "CommissionPositionRates");

            migrationBuilder.DropTable(
                name: "CommissionProfitSlabs");

            migrationBuilder.DropTable(
                name: "CustomerAssignmentHistories");

            migrationBuilder.DropTable(
                name: "SalesCommissionRecords");

            migrationBuilder.DropTable(
                name: "ItemCategories");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Items_CategoryId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AssignedAgentId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AssignedEmployeeId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "AssignedAgentId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AssignedEmployeeId",
                table: "Customers");
        }
    }
}
