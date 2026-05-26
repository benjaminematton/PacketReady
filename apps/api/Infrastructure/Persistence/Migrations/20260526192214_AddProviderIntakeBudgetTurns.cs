using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderIntakeBudgetTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "intake_budget_turns",
                table: "providers",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddCheckConstraint(
                name: "ck_providers_intake_budget_turns_positive",
                table: "providers",
                sql: "intake_budget_turns >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_providers_intake_budget_turns_positive",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "intake_budget_turns",
                table: "providers");
        }
    }
}
