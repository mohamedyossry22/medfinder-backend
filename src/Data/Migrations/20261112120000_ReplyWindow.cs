using System;
using MedFinder.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedFinder.src.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261112120000_ReplyWindow")]
    public partial class ReplyWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // مهلة رد الصيدليات (120 دقيقة للطلبات الجديدة)
            migrationBuilder.AddColumn<DateTime>(
                name: "ReplyDeadline",
                table: "DrugRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // الطلبات القديمة تفضل على سلوكها الأصلي (المهلة = نهاية الطلب)
            migrationBuilder.Sql("UPDATE \"DrugRequests\" SET \"ReplyDeadline\" = \"ExpiresAt\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReplyDeadline", table: "DrugRequests");
        }
    }
}
