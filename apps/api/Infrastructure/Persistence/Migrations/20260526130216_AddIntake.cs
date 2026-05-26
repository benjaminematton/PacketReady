using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "intake_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    state_payload = table.Column<string>(type: "jsonb", nullable: false),
                    turns_consumed = table.Column<int>(type: "integer", nullable: false),
                    turn_budget = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_transition_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intake_sessions", x => x.id);
                    table.CheckConstraint("ck_intake_sessions_state_values", "state IN ('Pending', 'AwaitingProvider', 'AgentProcessing', 'Complete', 'Escalated')");
                    table.CheckConstraint("ck_intake_sessions_turn_budget_positive", "turn_budget >= 1");
                    table.CheckConstraint("ck_intake_sessions_turns_consumed_non_negative", "turns_consumed >= 0");
                    table.CheckConstraint("ck_intake_sessions_turns_within_budget", "turns_consumed <= turn_budget");
                    table.ForeignKey(
                        name: "FK_intake_sessions_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "magic_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_magic_links", x => x.id);
                    table.CheckConstraint("ck_magic_links_expires_after_issued", "expires_at > issued_at");
                    table.ForeignKey(
                        name: "FK_magic_links_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    held_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    composed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_messages", x => x.id);
                    table.CheckConstraint("ck_outbound_messages_kind_values", "kind IN ('IntakeInvitation', 'Followup', 'CompletionNotice')");
                    table.CheckConstraint("ck_outbound_messages_sent_at_pairing", "(status = 'Sent' AND sent_at IS NOT NULL) OR (status <> 'Sent' AND sent_at IS NULL)");
                    table.CheckConstraint("ck_outbound_messages_status_values", "status IN ('Queued', 'Held', 'Sent', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_outbound_messages_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intake_sessions_state_last_transition",
                table: "intake_sessions",
                columns: new[] { "state", "last_transition_at" });

            migrationBuilder.CreateIndex(
                name: "ux_intake_sessions_provider",
                table: "intake_sessions",
                column: "provider_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_magic_links_provider_issued",
                table: "magic_links",
                columns: new[] { "provider_id", "issued_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_status_held_until",
                table: "outbound_messages",
                columns: new[] { "status", "held_until" });

            migrationBuilder.CreateIndex(
                name: "ux_outbound_messages_dedup",
                table: "outbound_messages",
                columns: new[] { "provider_id", "turn_id", "kind" },
                unique: true);

            // Back-populate: every existing provider gets one intake_sessions
            // row so post-P5 queries that join providers → intake_sessions
            // don't blow up on pre-P5 rows. Two branches:
            //
            //   (1) Provider has a readiness score → state = 'Complete' with
            //       the latest score id baked into state_payload. "This
            //       provider has been fully scored — treat them as if the
            //       lifecycle had run end-to-end."
            //   (2) Provider has no readiness score (P3/P4 dev rows that
            //       never hit Score) → state = 'Pending'. The intake hasn't
            //       started yet for them, which is the truthful answer.
            //
            // jsonb_build_object emits ISO-8601 for timestamptz and the
            // standard UUID format for uuid — matches STJ's default
            // DateTimeOffset / Guid parsers, so EF rehydrate round-trips
            // without a custom converter.
            //
            // UNIQUE (provider_id) makes both inserts idempotent against
            // a partial run; the WHERE NOT EXISTS guard makes them
            // re-runnable on a DB that already has some intake_sessions
            // rows (e.g. a dev who hand-seeded before applying).
            migrationBuilder.Sql(@"
INSERT INTO intake_sessions
  (id, provider_id, state, state_payload, turns_consumed, turn_budget, created_at, last_transition_at)
SELECT
  gen_random_uuid(),
  p.id,
  'Complete',
  jsonb_build_object(
    'kind', 'Complete',
    'readinessScoreId', latest_score.id,
    'completedAt', latest_score.computed_at
  ),
  0,
  8,
  p.created_at,
  latest_score.computed_at
FROM providers p
JOIN LATERAL (
  SELECT id, computed_at
  FROM readiness_scores
  WHERE provider_id = p.id
  ORDER BY computed_at DESC
  LIMIT 1
) latest_score ON TRUE
WHERE NOT EXISTS (
  SELECT 1 FROM intake_sessions s WHERE s.provider_id = p.id
);

INSERT INTO intake_sessions
  (id, provider_id, state, state_payload, turns_consumed, turn_budget, created_at, last_transition_at)
SELECT
  gen_random_uuid(),
  p.id,
  'Pending',
  jsonb_build_object(
    'kind', 'Pending',
    'createdAt', p.created_at
  ),
  0,
  8,
  p.created_at,
  p.created_at
FROM providers p
LEFT JOIN readiness_scores rs ON rs.provider_id = p.id
WHERE rs.id IS NULL
  AND NOT EXISTS (
    SELECT 1 FROM intake_sessions s WHERE s.provider_id = p.id
  );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intake_sessions");

            migrationBuilder.DropTable(
                name: "magic_links");

            migrationBuilder.DropTable(
                name: "outbound_messages");
        }
    }
}
