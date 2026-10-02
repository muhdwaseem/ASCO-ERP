using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalVoucherApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalDecisionAtUtc",
                table: "JournalVouchers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalDecisionBy",
                table: "JournalVouchers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalNote",
                table: "JournalVouchers",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "JournalVouchers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PostedBy",
                table: "JournalVouchers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedForApprovalAtUtc",
                table: "JournalVouchers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedForApprovalBy",
                table: "JournalVouchers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAtUtc",
                table: "JournalVouchers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                table: "JournalVouchers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalDecisionAtUtc",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionBy",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "ApprovalNote",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "PostedBy",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "SubmittedForApprovalAtUtc",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "SubmittedForApprovalBy",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "VoidedAtUtc",
                table: "JournalVouchers");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                table: "JournalVouchers");
        }
    }
}
