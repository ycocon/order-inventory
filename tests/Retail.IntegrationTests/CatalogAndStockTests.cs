using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retail.Orders;
using Xunit;

namespace Retail.IntegrationTests;

public sealed class CatalogAndStockTests(RetailFixture fixture) : IClassFixture<RetailFixture>
{
    [Fact]
    public async Task Concurrent_receipt_retries_record_one_unit_and_one_movement()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        var sku = NewSku();
        Assert.Equal(HttpStatusCode.Created,
            (await staff.PostAsJsonAsync("/api/products", Product(sku, "Desk lamp"))).StatusCode);

        var key = Guid.NewGuid().ToString();
        var receipt = new { sku, quantity = 1, reason = "Opening shipment", requestKey = key };
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => staff.PostAsJsonAsync("/api/receipts", receipt)));
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, attempts.Count(x => x.StatusCode == HttpStatusCode.OK));

        var changed = receipt with { quantity = 2 };
        Assert.Equal(HttpStatusCode.Conflict,
            (await staff.PostAsJsonAsync("/api/receipts", changed)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await staff.PostAsJsonAsync("/api/receipts", receipt with { reason = "Other reason" })).StatusCode);

        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var product = Assert.Single(await Products(customer), row => row.GetProperty("sku").GetString() == sku);
        Assert.Equal(1, product.GetProperty("available").GetInt64());
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var audit = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM stock_receipts WHERE sku = @sku),
                   (SELECT count(*) FROM stock_movements WHERE sku = @sku)
            """, conn);
        audit.Parameters.AddWithValue("sku", sku);
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Staff_edit_reaches_customer_and_inactive_product_is_hidden()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var sku = NewSku();
        Assert.Equal(HttpStatusCode.Created,
            (await staff.PostAsJsonAsync("/api/products", Product(sku, "Starter kit"))).StatusCode);
        var url = $"/api/products/{sku}";
        (await staff.PutAsJsonAsync(url, Product(sku, "Updated kit", 275))).EnsureSuccessStatusCode();
        var visible = Assert.Single(await Products(customer), p => p.GetProperty("sku").GetString() == sku);
        Assert.Equal("Updated kit", visible.GetProperty("name").GetString());
        Assert.Equal(275, visible.GetProperty("unitPriceMinor").GetInt64());
        (await staff.PutAsJsonAsync(url, Product(sku, "Updated kit", 275, false))).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await Products(customer), p => p.GetProperty("sku").GetString() == sku);
        var hidden = Assert.Single(await Products(staff), p => p.GetProperty("sku").GetString() == sku);
        Assert.False(hidden.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Customer_cannot_change_stock_and_staff_writes_require_antiforgery()
    {
        var sku = NewSku();
        var product = Product(sku, "Access check");
        using var anonymous = fixture.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/products")).StatusCode);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/products", product)).StatusCode);
        using var staff = await SignIn("staff", fixture.StaffPassword);
        (await staff.PostAsJsonAsync("/api/products", product)).EnsureSuccessStatusCode();
        var receipt = new { sku, quantity = 1, reason = "Unauthorized change", requestKey = Guid.NewGuid().ToString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/receipts", receipt)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsJsonAsync($"/api/products/{sku}", product)).StatusCode);
        staff.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var noToken = await staff.PostAsJsonAsync("/api/receipts", receipt);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var unchanged = Assert.Single(await Products(customer), p => p.GetProperty("sku").GetString() == sku);
        Assert.Equal(0, unchanged.GetProperty("available").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Receipt_requires_positive_whole_units(int quantity)
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        var sku = NewSku();
        (await staff.PostAsJsonAsync("/api/products", Product(sku, "Stock rule"))).EnsureSuccessStatusCode();
        var payload = new { sku, quantity, reason = "Invalid quantity", requestKey = Guid.NewGuid().ToString() };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await staff.PostAsJsonAsync("/api/receipts", payload)).StatusCode);
        var row = Assert.Single(await Products(staff), p => p.GetProperty("sku").GetString() == sku);
        Assert.Equal(0L, row.GetProperty("available").GetInt64());
    }

    [Fact]
    public async Task Opposite_baskets_reserve_every_line_or_nothing()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var first = await SignIn("customer", fixture.CustomerPassword);
        using var second = await SignIn("customer", fixture.CustomerPassword);
        var a = await StockOne(staff);
        var b = await StockOne(staff);

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/orders", Basket(a, b)),
            second.PostAsJsonAsync("/api/orders", Basket(b, a)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var orders = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var winner = Assert.Single(orders, o => o.GetProperty("status").GetString() == "awaiting_payment");
        var loser = Assert.Single(orders, o => o.GetProperty("status").GetString() == "rejected");
        Assert.Equal("insufficient_stock", loser.GetProperty("rejectionReason").GetString());
        foreach (var order in orders)
            Assert.Equal(2, order.GetProperty("lines").GetArrayLength());

        Assert.Equal(new long[] { 2, 2, 1, 1, 1 }, await Audit(winner.GetProperty("orderId").GetGuid()));
        Assert.Equal(new long[] { 0, 0, 0, 0, 1 }, await Audit(loser.GetProperty("orderId").GetGuid()));
        var stock = (await Products(first)).Where(row => new[] { a, b }.Contains(row.GetProperty("sku").GetString())).ToArray();
        Assert.Equal(2, stock.Length);
        Assert.All(stock, row => Assert.Equal(0L, row.GetProperty("available").GetInt64()));
    }

    [Fact]
    public async Task Ten_checkout_retries_keep_original_reference_and_prices()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var a = await StockOne(staff);
        var b = await StockOne(staff);
        var basket = Basket(a, b);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => customer.PostAsJsonAsync("/api/orders", basket)));
        var original = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, attempts.Count(r => r.StatusCode == HttpStatusCode.OK));
        var reference = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        foreach (var attempt in attempts.Where(response => response != original))
            Assert.Equal(reference, (await attempt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid());

        (await staff.PutAsJsonAsync($"/api/products/{a}", Product(a, "Repriced", 200, active: false))).EnsureSuccessStatusCode();
        var repeating = await customer.PostAsJsonAsync("/api/orders", basket with { Lines = basket.Lines!.Reverse().ToArray() });
        Assert.Equal(HttpStatusCode.OK, repeating.StatusCode);
        var repeated = await repeating.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reference, repeated.GetProperty("orderId").GetGuid());
        Assert.Equal(250L, repeated.GetProperty("totalMinor").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync("/api/orders", basket with
        {
            Lines = [new CheckoutLine(a, 2), new CheckoutLine(b, 1)]
        })).StatusCode);
        Assert.Equal(new long[] { 2, 2, 1, 1, 1 }, await Audit(reference));
    }

    [Fact]
    public async Task Failed_payment_work_insert_rolls_back_basket_and_holds()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var a = await StockOne(staff);
        var b = await StockOne(staff);
        var basket = Basket(a, b);
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, db);
            await command.ExecuteNonQueryAsync();
        }
        await Execute("ALTER TABLE payment_work ADD CONSTRAINT force_checkout_rollback CHECK (attempt_count < 0) NOT VALID");
        try
        {
            using var failure = await customer.PostAsJsonAsync("/api/orders", basket);
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        }
        finally
        {
            await Execute("ALTER TABLE payment_work DROP CONSTRAINT force_checkout_rollback");
        }

        await using var audit = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM customer_orders WHERE checkout_key = @key),
                   (SELECT count(*) FROM stock_holds WHERE sku IN (@a, @b)),
                   (SELECT count(*) FROM stock_movements WHERE sku IN (@a, @b) AND kind = 'reserve'),
                   (SELECT sum(reserved)::bigint FROM stock_positions WHERE sku IN (@a, @b))
            """, db);
        audit.Parameters.AddWithValue("key", basket.RequestKey!);
        audit.Parameters.AddWithValue("a", a);
        audit.Parameters.AddWithValue("b", b);
        await using (var reader = await audit.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            for (var column = 0; column < 4; column++) Assert.Equal(0L, reader.GetInt64(column));
        }
        Assert.Equal(HttpStatusCode.Created, (await customer.PostAsJsonAsync("/api/orders", basket)).StatusCode);
    }

    private async Task<string> StockOne(HttpClient staff)
    {
        var sku = NewSku();
        (await staff.PostAsJsonAsync("/api/products", Product(sku, "Checkout fixture"))).EnsureSuccessStatusCode();
        (await staff.PostAsJsonAsync("/api/receipts", new { sku, quantity = 1,
            reason = "Checkout fixture", requestKey = Guid.NewGuid().ToString() })).EnsureSuccessStatusCode();
        return sku;
    }

    [Fact]
    public async Task Concurrent_dispatches_return_one_record_and_consume_each_line_once()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var orderId = await ConfirmOrder(staff, customer);
        using var secondStaff = await CreateAccountAndSignIn("staff", fixture.StaffPassword);
        var url = $"/api/staff/orders/{orderId}/dispatch";
        var requests = await Task.WhenAll(staff.PostAsync(url, null), secondStaff.PostAsync(url, null));
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.OK);
        var recorded = await requests[0].Content.ReadFromJsonAsync<JsonElement>();
        var replayed = await requests[1].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(recorded.GetRawText(), replayed.GetRawText());
        Assert.Equal(new long[] { 0, 0, 0, 2, 2, 1, 1 }, await DispatchAudit(orderId));

        var customerOrder = await customer.GetFromJsonAsync<JsonElement>($"/api/orders/{orderId}");
        Assert.Equal("dispatched", customerOrder.GetProperty("status").GetString());
        var detail = await staff.GetFromJsonAsync<JsonElement>($"/api/staff/orders/{orderId}");
        Assert.Equal("approved", detail.GetProperty("payment").GetProperty("decision").GetString());
        Assert.Equal(recorded.GetProperty("staffId").GetString(), detail.GetProperty("dispatch").GetProperty("staffId").GetString());
        Assert.Equal(recorded.GetProperty("dispatchedAt").GetString(), detail.GetProperty("dispatch").GetProperty("dispatchedAt").GetString());
        Assert.Equal(3, detail.GetProperty("history").GetArrayLength());
        Assert.Equal(4, detail.GetProperty("stock").GetArrayLength());
    }

    [Fact]
    public async Task Dispatch_commit_failure_restores_order_and_all_reserved_stock()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        var orderId = await ConfirmOrder(staff, customer);
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        await using var install = new NpgsqlCommand("""
            CREATE FUNCTION fail_dispatch_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Fixture failure at dispatch commit'; END;
            $$;
            CREATE CONSTRAINT TRIGGER fail_dispatch_at_commit AFTER INSERT ON order_dispatches
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_dispatch_fixture();
            """, db);
        await install.ExecuteNonQueryAsync();
        try
        {
            using var failure = await staff.PostAsync($"/api/staff/orders/{orderId}/dispatch", null);
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        }
        finally
        {
            await using var uninstall = new NpgsqlCommand("""
                DROP TRIGGER fail_dispatch_at_commit ON order_dispatches;
                DROP FUNCTION fail_dispatch_fixture();
                """, db);
            await uninstall.ExecuteNonQueryAsync();
        }

        Assert.Equal(new long[] { 2, 2, 2, 0, 0, 0, 0 }, await DispatchAudit(orderId));
        var persisted = await customer.GetFromJsonAsync<JsonElement>($"/api/orders/{orderId}");
        Assert.Equal("confirmed", persisted.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsync($"/api/staff/orders/{orderId}/dispatch", null)).StatusCode);
        Assert.Equal(new long[] { 0, 0, 0, 2, 2, 1, 1 }, await DispatchAudit(orderId));
    }

    [Fact]
    public async Task Order_visibility_and_dispatch_are_enforced_on_the_server()
    {
        using var staff = await SignIn("staff", fixture.StaffPassword);
        using var customer = await SignIn("customer", fixture.CustomerPassword);
        using var otherCustomer = await CreateAccountAndSignIn("customer", fixture.CustomerPassword);
        var orderId = await ConfirmOrder(staff, customer);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.GetAsync($"/api/orders/{orderId}")).StatusCode);
        Assert.Empty(await otherCustomer.GetFromJsonAsync<JsonElement[]>("/api/orders") ?? []);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/staff/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherCustomer.GetAsync($"/api/staff/orders/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/staff/orders/{orderId}/dispatch", null)).StatusCode);
        using var anonymous = fixture.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/staff/orders")).StatusCode);

        var listing = await staff.GetFromJsonAsync<JsonElement>($"/api/staff/orders?q={orderId}&status=confirmed");
        Assert.Equal(orderId, Assert.Single(listing.GetProperty("items").EnumerateArray()).GetProperty("orderId").GetGuid());
        Assert.Equal("customer@demo.local", listing.GetProperty("items")[0].GetProperty("customer").GetString());
        var subtotal = await customer.PostAsJsonAsync("/api/orders", Basket(await StockOne(staff)));
        var pending = (await subtotal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsync($"/api/staff/orders/{pending}/dispatch", null)).StatusCode);
        staff.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync($"/api/staff/orders/{orderId}/dispatch", null)).StatusCode);
        Assert.Equal(new long[] { 2, 2, 2, 0, 0, 0, 0 }, await DispatchAudit(orderId));
    }

    private async Task<HttpClient> CreateAccountAndSignIn(string role, string password)
    {
        using var scope = fixture.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var email = $"fixture-{Guid.NewGuid():N}@demo.local";
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return await SignIn(role, password, email);
    }

    private async Task<Guid> ConfirmOrder(HttpClient staff, HttpClient customer)
    {
        var checkout = await customer.PostAsJsonAsync("/api/orders", Basket(await StockOne(staff), await StockOne(staff)));
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var orderId = (await checkout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        // The fixture has no simulator URL. Seed the worker's committed result for dispatch-specific tests.
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        await using var sql = new NpgsqlCommand("""
            UPDATE customer_orders SET status = 'Confirmed' WHERE id = @id;
            UPDATE payment_intents SET decision = 'approved' WHERE order_id = @id;
            UPDATE payment_work SET status = 'complete' WHERE operation_id IN
                (SELECT operation_id FROM payment_intents WHERE order_id = @id);
            INSERT INTO order_events (id, order_id, version, kind, recorded_at)
            VALUES (gen_random_uuid(), @id, 2, 'OrderConfirmed', clock_timestamp());
            """, db);
        sql.Parameters.AddWithValue("id", orderId);
        await sql.ExecuteNonQueryAsync();
        return orderId;
    }

    private async Task<long[]> DispatchAudit(Guid orderId)
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT sum(p.on_hand)::bigint FROM stock_positions p JOIN order_lines l USING (sku) WHERE l.order_id = @id),
                   (SELECT sum(p.reserved)::bigint FROM stock_positions p JOIN order_lines l USING (sku) WHERE l.order_id = @id),
                   (SELECT count(*) FROM stock_holds WHERE order_id = @id AND state = 'active'),
                   (SELECT count(*) FROM stock_holds WHERE order_id = @id AND state = 'consumed' AND resolved_at IS NOT NULL),
                   (SELECT count(*) FROM stock_movements WHERE operation_id = @id AND kind = 'dispatch' AND actor_id IS NOT NULL),
                   (SELECT count(*) FROM order_dispatches WHERE order_id = @id),
                   (SELECT count(*) FROM order_events WHERE order_id = @id AND kind = 'OrderDispatched')
            """, db);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 7).Select(reader.GetInt64).ToArray();
    }

    private static CheckoutInput Basket(params string[] skus) =>
        new(Guid.NewGuid().ToString(), skus.Select(sku => new CheckoutLine(sku, 1)).ToArray());

    private async Task<long[]> Audit(Guid orderId)
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM stock_holds WHERE order_id = @id AND state = 'active'),
                   (SELECT count(*) FROM stock_movements WHERE operation_id = @id AND kind = 'reserve'),
                   (SELECT count(*) FROM payment_intents WHERE order_id = @id),
                   (SELECT count(*) FROM payment_work w JOIN payment_intents p USING (operation_id) WHERE p.order_id = @id AND w.status = 'due'),
                   (SELECT count(*) FROM order_events WHERE order_id = @id)
            """, db);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, 5).Select(reader.GetInt64).ToArray();
    }

    private static string NewSku() => $"T-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
    private static object Product(string sku, string name, int unitPriceMinor = 125, bool active = true)
        => new { sku, name, unitPriceMinor, active };
    private static async Task<JsonElement[]> Products(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement[]>("/api/products"))!;

    private async Task<HttpClient> SignIn(string role, string password, string? email = null)
    {
        var client = fixture.NewClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        email ??= $"{role}@demo.local";
        (await client.PostAsJsonAsync("/api/login", new { email, password })).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var signedIn = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(role, signedIn.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", signedIn.GetProperty("csrfToken").GetString());
        return client;
    }
}
