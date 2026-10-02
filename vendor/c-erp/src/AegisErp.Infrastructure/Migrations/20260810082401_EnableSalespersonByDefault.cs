using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnableSalespersonByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No schema change — SalespersonEnabled already existed. Per the client's request this
            // now defaults to on for every company (new ones get it from the C# property default;
            // this flips every company that already exists, including ones nobody has opened yet).
            migrationBuilder.Sql("UPDATE \"CompanySetups\" SET \"SalespersonEnabled\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"CompanySetups\" SET \"SalespersonEnabled\" = FALSE;");
        }
    }
}
