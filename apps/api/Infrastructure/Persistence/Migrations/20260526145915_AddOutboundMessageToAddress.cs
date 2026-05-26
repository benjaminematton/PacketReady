using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundMessageToAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add to_address. Existing rows are backfilled to the empty
            //    string so the column can be NOT NULL; the empty value is a
            //    sentinel — the dispatcher's per-row try/catch logs and skips
            //    rather than misrouting (see also OutboundMessage.Compose's
            //    factory which refuses an empty toAddress on new writes).
            migrationBuilder.AddColumn<string>(
                name: "to_address",
                table: "outbound_messages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // 2. held_until was nullable for the (long-defunct) "Held" status
            //    path. Status now only takes Queued|Sent|Cancelled and every
            //    write goes through OutboundMessage.Compose which stamps
            //    composed_at + holdDuration. Backfill any orphan NULL rows to
            //    composed_at + 10 min (the production hold default) before
            //    flipping the column to NOT NULL.
            migrationBuilder.Sql(@"
                UPDATE outbound_messages
                SET held_until = composed_at + INTERVAL '10 minutes'
                WHERE held_until IS NULL;
            ");

            migrationBuilder.AlterColumn<System.DateTimeOffset>(
                name: "held_until",
                table: "outbound_messages",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(System.DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            // 3. Drop the legacy check constraint that still allowed 'Held'
            //    and re-add the current Queued|Sent|Cancelled enum. EF's
            //    HasCheckConstraint diff is unreliable for body-only changes,
            //    so we hand-drive the DROP/ADD.
            migrationBuilder.Sql(@"
                ALTER TABLE outbound_messages
                DROP CONSTRAINT IF EXISTS ck_outbound_messages_status_values;
                ALTER TABLE outbound_messages
                ADD CONSTRAINT ck_outbound_messages_status_values
                CHECK (status IN ('Queued', 'Sent', 'Cancelled'));
            ");

            // 4. Supporting index for IntakeTurnJob.GetMostRecentToAddressAsync
            //    (WHERE provider_id = @x ORDER BY composed_at DESC LIMIT 1).
            //    Cheap to add now, expensive to forget at scale.
            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_provider_id_composed_at",
                table: "outbound_messages",
                columns: new[] { "provider_id", "composed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbound_messages_provider_id_composed_at",
                table: "outbound_messages");

            migrationBuilder.Sql(@"
                ALTER TABLE outbound_messages
                DROP CONSTRAINT IF EXISTS ck_outbound_messages_status_values;
                ALTER TABLE outbound_messages
                ADD CONSTRAINT ck_outbound_messages_status_values
                CHECK (status IN ('Queued', 'Held', 'Sent', 'Cancelled'));
            ");

            migrationBuilder.AlterColumn<System.DateTimeOffset>(
                name: "held_until",
                table: "outbound_messages",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(System.DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: false);

            migrationBuilder.DropColumn(
                name: "to_address",
                table: "outbound_messages");
        }
    }
}
