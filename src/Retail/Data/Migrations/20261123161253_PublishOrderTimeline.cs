using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Retail.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishOrderTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_outbox",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_outbox", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_order_outbox_order_events_event_id",
                        column: x => x.event_id,
                        principalTable: "order_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_timeline",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_timeline", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_outbox_unsent",
                table: "order_outbox",
                column: "event_id",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_order_timeline_order_id_version",
                table: "order_timeline",
                columns: new[] { "order_id", "version" },
                unique: true);

            migrationBuilder.Sql("INSERT INTO order_outbox (event_id) SELECT id FROM order_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_outbox");

            migrationBuilder.DropTable(
                name: "order_timeline");
        }
    }
}
