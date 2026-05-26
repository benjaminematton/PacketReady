using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderPayerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payer_id",
                table: "providers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "payer-a-national-hmo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payer_id",
                table: "providers");
        }
    }
}
