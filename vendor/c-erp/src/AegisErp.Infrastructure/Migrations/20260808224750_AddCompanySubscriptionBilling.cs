using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanySubscriptionBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SubscriptionAmount",
                table: "CompanySetups",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SubscriptionEnabled",
                table: "CompanySetups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionGraceDays",
                table: "CompanySetups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SubscriptionPaidThroughDate",
                table: "CompanySetups",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanySubscriptionPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanySetupId = table.Column<int>(type: "integer", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CoversThrough = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanySubscriptionPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanySubscriptionPayments_CompanySetups_CompanySetupId",
                        column: x => x.CompanySetupId,
                        principalTable: "CompanySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanySubscriptionPayments_CompanySetupId_PaymentDate",
                table: "CompanySubscriptionPayments",
                columns: new[] { "CompanySetupId", "PaymentDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanySubscriptionPayments");

            migrationBuilder.DropColumn(
                name: "SubscriptionAmount",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "SubscriptionEnabled",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "SubscriptionGraceDays",
                table: "CompanySetups");

            migrationBuilder.DropColumn(
                name: "SubscriptionPaidThroughDate",
                table: "CompanySetups");
        }
    }
}
