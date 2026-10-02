using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountPnlSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PnlSection",
                table: "Accounts",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            // Best-effort backfill for existing accounts: Income -> OperatingIncome, Expense ->
            // OperatingExpense, with a name-based heuristic bump to CostOfGoodsSold. Users can
            // always reclassify individual accounts afterwards from Chart of Accounts.
            migrationBuilder.Sql(
                "UPDATE \"Accounts\" SET \"PnlSection\" = 'OperatingIncome' WHERE \"Type\" = 'Income' AND \"PnlSection\" IS NULL;");
            migrationBuilder.Sql(
                "UPDATE \"Accounts\" SET \"PnlSection\" = 'OperatingExpense' WHERE \"Type\" = 'Expense' AND \"PnlSection\" IS NULL;");
            migrationBuilder.Sql(
                "UPDATE \"Accounts\" SET \"PnlSection\" = 'CostOfGoodsSold' WHERE \"Type\" = 'Expense' AND (LOWER(\"Name\") LIKE '%cost of goods%' OR LOWER(\"Name\") LIKE '%cost of sales%');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PnlSection",
                table: "Accounts");
        }
    }
}
