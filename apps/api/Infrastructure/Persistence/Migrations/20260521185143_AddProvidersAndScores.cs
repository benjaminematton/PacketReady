using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProvidersAndScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    profile = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "readiness_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    tier = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    critical_count = table.Column<int>(type: "integer", nullable: false),
                    major_count = table.Column<int>(type: "integer", nullable: false),
                    minor_count = table.Column<int>(type: "integer", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_readiness_scores", x => x.id);
                    table.CheckConstraint("ck_readiness_scores_counts_non_negative", "critical_count >= 0 AND major_count >= 0 AND minor_count >= 0");
                    table.CheckConstraint("ck_readiness_scores_score_range", "score BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_readiness_scores_tier_values", "tier IN ('Red', 'Yellow', 'Green')");
                    table.ForeignKey(
                        name: "FK_readiness_scores_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_readiness_scores_provider_computed",
                table: "readiness_scores",
                columns: new[] { "provider_id", "computed_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "readiness_scores");

            migrationBuilder.DropTable(
                name: "providers");
        }
    }
}
