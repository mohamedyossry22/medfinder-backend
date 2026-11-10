using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedFinder.src.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestClosing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "DrugRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveredTargetId",
                table: "DrugRequests",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "DrugRequests");

            migrationBuilder.DropColumn(
                name: "DeliveredTargetId",
                table: "DrugRequests");
        }
    }
}
