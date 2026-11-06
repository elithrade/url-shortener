using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shortener
{
    /// <inheritdoc />
    public partial class AddClickStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "click_stats",
                columns: table => new
                {
                    Code = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_click_stats", x => new { x.Code, x.Day });
                    table.ForeignKey(
                        name: "FK_click_stats_links_Code",
                        column: x => x.Code,
                        principalTable: "links",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "click_stats");
        }
    }
}
