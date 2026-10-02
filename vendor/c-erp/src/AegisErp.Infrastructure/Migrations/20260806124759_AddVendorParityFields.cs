using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorParityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingAddressLine1",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingAddressLine1Arabic",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingAddressLine2",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingAddressLine2Arabic",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingAttention",
                table: "Vendors",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCity",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCountry",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingEmirate",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingFax",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingPhone",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingZip",
                table: "Vendors",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Vendors",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            // Existing vendors predate this column — backfill with the migration date rather than
            // DateTime.MinValue, since "01 Jan 0001" would display as an obviously-broken date.
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "Vendors",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(2026, 8, 6, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<decimal>(
                name: "CreditLimit",
                table: "Vendors",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DisplayNameArabic",
                table: "Vendors",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mobile",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OpeningBalance",
                table: "Vendors",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "Vendors",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Salutation",
                table: "Vendors",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingAddressLine1",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingAddressLine1Arabic",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingAddressLine2",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingAddressLine2Arabic",
                table: "Vendors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingAttention",
                table: "Vendors",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingCity",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingCountry",
                table: "Vendors",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingEmirate",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingFax",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingPhone",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingZip",
                table: "Vendors",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxTreatment",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorLanguage",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VendorType",
                table: "Vendors",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkPhone",
                table: "Vendors",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VendorContactPersons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    Salutation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Email = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    WorkPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Mobile = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Designation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Department = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorContactPersons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorContactPersons_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorCustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    CustomFieldDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorCustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorCustomFieldValues_CustomFieldDefinitions_CustomFieldD~",
                        column: x => x.CustomFieldDefinitionId,
                        principalTable: "CustomFieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorCustomFieldValues_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorDocuments_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorTags_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorContactPersons_VendorId",
                table: "VendorContactPersons",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCustomFieldValues_CustomFieldDefinitionId",
                table: "VendorCustomFieldValues",
                column: "CustomFieldDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCustomFieldValues_VendorId",
                table: "VendorCustomFieldValues",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDocuments_VendorId",
                table: "VendorDocuments",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorTags_TagId",
                table: "VendorTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorTags_VendorId",
                table: "VendorTags",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorContactPersons");

            migrationBuilder.DropTable(
                name: "VendorCustomFieldValues");

            migrationBuilder.DropTable(
                name: "VendorDocuments");

            migrationBuilder.DropTable(
                name: "VendorTags");

            migrationBuilder.DropColumn(
                name: "BillingAddressLine1",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingAddressLine1Arabic",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingAddressLine2",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingAddressLine2Arabic",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingAttention",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingCity",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingEmirate",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingFax",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingPhone",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BillingZip",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "CreditLimit",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "DisplayNameArabic",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "Mobile",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "OpeningBalance",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "PlaceOfSupply",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "Salutation",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingAddressLine1",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingAddressLine1Arabic",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingAddressLine2",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingAddressLine2Arabic",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingAttention",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingCity",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingCountry",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingEmirate",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingFax",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingPhone",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ShippingZip",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "TaxTreatment",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "VendorLanguage",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "VendorType",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "WorkPhone",
                table: "Vendors");
        }
    }
}
