using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HurryAppPleaseWork.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Results",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "text", nullable: false),
                    ImageMatrix = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResultsTemplate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Rect_X = table.Column<int>(type: "integer", nullable: false),
                    Rect_Y = table.Column<int>(type: "integer", nullable: false),
                    Rect_Width = table.Column<int>(type: "integer", nullable: false),
                    Rect_Height = table.Column<int>(type: "integer", nullable: false),
                    Template = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProbResultId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultsTemplate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResultsTemplate_Results_ProbResultId",
                        column: x => x.ProbResultId,
                        principalTable: "Results",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResultsTemplate_ProbResultId",
                table: "ResultsTemplate",
                column: "ProbResultId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResultsTemplate");

            migrationBuilder.DropTable(
                name: "Results");
        }
    }
}
