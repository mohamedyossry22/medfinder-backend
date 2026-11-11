using System;
using MedFinder.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MedFinder.src.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261111180000_Points")]
    public partial class Points : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // وقت وصول الطلب للصيدلية (الصفوف القديمة بتاخد وقت الـ migration، ومالهاش تأثير لأنها خلصت)
            migrationBuilder.AddColumn<DateTime>(
                name: "NotifiedAt",
                table: "RequestTargets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "FreeAds",
                table: "Pharmacies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FreeAdsMonth",
                table: "Pharmacies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CostSource",
                table: "Ads",
                type: "text",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "PointsLedger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PharmacyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PointsLedger_Pharmacies_PharmacyId",
                        column: x => x.PharmacyId,
                        principalTable: "Pharmacies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedger_PharmacyId_CreatedAt",
                table: "PointsLedger",
                columns: new[] { "PharmacyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedger_TargetId_Reason",
                table: "PointsLedger",
                columns: new[] { "TargetId", "Reason" },
                unique: true,
                filter: "\"TargetId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PointsLedger");
            migrationBuilder.DropColumn(name: "CostSource", table: "Ads");
            migrationBuilder.DropColumn(name: "FreeAdsMonth", table: "Pharmacies");
            migrationBuilder.DropColumn(name: "FreeAds", table: "Pharmacies");
            migrationBuilder.DropColumn(name: "NotifiedAt", table: "RequestTargets");
        }
    }
}
