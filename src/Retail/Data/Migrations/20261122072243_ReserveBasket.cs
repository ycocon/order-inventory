using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Retail.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReserveBasket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_stock_movements_AspNetUsers_staff_id",
                table: "stock_movements");

            migrationBuilder.RenameColumn(
                name: "staff_id",
                table: "stock_movements",
                newName: "actor_id");

            migrationBuilder.RenameIndex(
                name: "IX_stock_movements_staff_id",
                table: "stock_movements",
                newName: "IX_stock_movements_actor_id");

            migrationBuilder.CreateTable(
                name: "customer_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<string>(type: "text", nullable: false),
                    checkout_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_orders", x => x.id);
                    table.CheckConstraint("order_total_nonnegative", "total_minor >= 0");
                    table.ForeignKey(
                        name: "FK_customer_orders_AspNetUsers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_events_AspNetUsers_actor_id",
                        column: x => x.actor_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_events_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(48)", nullable: false),
                    product_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => new { x.order_id, x.sku });
                    table.CheckConstraint("valid_order_line", "quantity > 0 AND unit_price_minor >= 0");
                    table.ForeignKey(
                        name: "FK_order_lines_catalog_products_sku",
                        column: x => x.sku,
                        principalTable: "catalog_products",
                        principalColumn: "sku",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_intents",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_intents", x => x.operation_id);
                    table.CheckConstraint("payment_amount_nonnegative", "amount_minor >= 0");
                    table.ForeignKey(
                        name: "FK_payment_intents_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_holds",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(48)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_holds", x => new { x.order_id, x.sku });
                    table.CheckConstraint("hold_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_holds_order_lines_order_id_sku",
                        columns: x => new { x.order_id, x.sku },
                        principalTable: "order_lines",
                        principalColumns: new[] { "order_id", "sku" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_holds_stock_positions_sku",
                        column: x => x.sku,
                        principalTable: "stock_positions",
                        principalColumn: "sku",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_work",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_work", x => x.operation_id);
                    table.ForeignKey(
                        name: "FK_payment_work_payment_intents_operation_id",
                        column: x => x.operation_id,
                        principalTable: "payment_intents",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_orders_customer_id_checkout_key",
                table: "customer_orders",
                columns: new[] { "customer_id", "checkout_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_orders_customer_id_created_at",
                table: "customer_orders",
                columns: new[] { "customer_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_order_events_actor_id",
                table: "order_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_events_order_id_version",
                table: "order_events",
                columns: new[] { "order_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_sku",
                table: "order_lines",
                column: "sku");

            migrationBuilder.CreateIndex(
                name: "IX_payment_intents_order_id",
                table: "payment_intents",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_work_status_due_at",
                table: "payment_work",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_holds_sku",
                table: "stock_holds",
                column: "sku");

            migrationBuilder.AddForeignKey(
                name: "FK_stock_movements_AspNetUsers_actor_id",
                table: "stock_movements",
                column: "actor_id",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_stock_movements_AspNetUsers_actor_id",
                table: "stock_movements");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "payment_work");

            migrationBuilder.DropTable(
                name: "stock_holds");

            migrationBuilder.DropTable(
                name: "payment_intents");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "customer_orders");

            migrationBuilder.RenameColumn(
                name: "actor_id",
                table: "stock_movements",
                newName: "staff_id");

            migrationBuilder.RenameIndex(
                name: "IX_stock_movements_actor_id",
                table: "stock_movements",
                newName: "IX_stock_movements_staff_id");

            migrationBuilder.AddForeignKey(
                name: "FK_stock_movements_AspNetUsers_staff_id",
                table: "stock_movements",
                column: "staff_id",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
