# Order & Inventory · Checkout through dispatch

Staff can manage products, receive stock, review orders, and dispatch confirmed orders. Customers can browse products, submit a whole basket, and see their recent orders. The local demo uses React, TypeScript, Vite, ASP.NET Core 10, Identity, EF Core, PostgreSQL 18, and RabbitMQ.

Follow the [reviewer demo](docs/DEMO.md) for a short walkthrough, or see the [case study](docs/CASE-STUDY.md) and [benchmark results](docs/BENCHMARKS.md).

## Run the demo

Start Docker with Linux containers. From this directory, run the following command in PowerShell 7.

```powershell
./demo.ps1
```

The launcher generates local passwords once in the ignored `.env` file and runs `docker compose up --build -d`. The app listens on [localhost:5188](http://localhost:5188). Set `RETAIL_PORT` in `.env` to use another port. To stop the demo, run `docker compose down`. Its named volumes preserve stock, user accounts, and login keys.

| Account | Password entry in your local .env |
|---|---|
| staff@demo.local | DEMO_STAFF_PASSWORD |
| customer@demo.local | DEMO_CUSTOMER_PASSWORD |

## Try the first workflow

1. Sign in as staff and add a product. Use a distinct SKU, a name, and a price. The demo displays prices in USD.
2. Click **Receive stock**, select that SKU, enter **1** and a reason, then record the receipt.
3. Sign in as a customer and look up the product. It has one available unit. Restarting the retail app preserves it.

If a receipt reply is lost, retry the pending receipt. Each receipt has a request key scoped to the signed-in staff member. PostgreSQL commits the receipt, stock increment, and audit record together. Replaying the same request returns the saved receipt. Changing its details under the same key returns a conflict. A fresh receipt intentionally adds more units.

## Try a checkout

Stock two products, sign in as a customer, and add both to your basket. **Submit order** creates an order reference. If every line can be reserved, the order starts as **Awaiting payment** and the background worker sends its saved payment to the simulator. The catalog updates **Recent orders** automatically.

- Approval shows **Confirmed** and keeps the stock reserved.
- Decline shows **Rejected · payment declined** and returns each held item to available stock.
- If any line is short at checkout, the order shows **Rejected · insufficient stock** and that checkout holds zero units.

After a lost checkout reply, **Retry checkout** uses the original request key. If the simulator's reply is lost, the worker looks up the saved payment ID while keeping the stock reserved.

To try a decline, set `SIMULATOR_DECISION=declined` in the project’s local `.env`, rerun the demo’s startup command, and place a **new** order. When finished, set the value back to `approved` and start the demo the same way. A simulator policy change applies to new authorizations. Existing payment decisions keep their original outcome.

## Dispatch a confirmed order

1. Place an order as a customer and copy its reference from **Recent orders** once it is **Confirmed**.
2. Sign in as staff. Open **Orders** in the header, search for the reference, and select **Review**. Check the payment decision, order timeline, and stock movements.
3. Select **Dispatch order**, then **Confirm dispatch**. Every held unit leaves on-hand stock, and the reservation is consumed. Refresh the customer’s **Recent orders** to see **Dispatched**.

Two dispatch requests for the same order produce one dispatch record with the original staff member and time. The stock movements, dispatch record, and order status commit together.

## Try the payment simulator

The local demo starts the separate simulator at [localhost:5189](http://localhost:5189/health/ready). It uses a separate database and login on the same PostgreSQL server. The setup job creates its database during startup, including when you already have a retail database from an earlier step.

Use a fresh GUID to start a pretend payment. Amounts are in minor currency units, so `1250 USD` represents $12.50.

```powershell
$operationId = [Guid]::NewGuid()
$request = @{ amountMinor = 1250; currency = 'USD' } | ConvertTo-Json
$url = "http://localhost:5189/api/authorizations/$operationId"
Invoke-RestMethod $url -Method Put -ContentType 'application/json' -Body $request
Invoke-RestMethod $url
```

| Request | Result |
|---|---|
| PUT with a fresh ID and valid terms | `201 Created` and the saved authorization |
| PUT with the same ID, amount, and currency | `200 OK` and the original authorization |
| PUT with the same ID and changed terms | `409 Conflict` |
| GET by operation ID | Saved authorization, or `404 Not Found` |

New payments are approved by default. In the local Compose demo, sending `X-Simulator-Drop-Reply: true` with PUT exercises a connection drop after the database commit. The standalone service disables this fault hook unless explicitly enabled.

## Run the checks

The integration suite uses a disposable PostgreSQL 18 container. Run it with a working Docker engine and the .NET 10 SDK.

```powershell
dotnet test Retail.sln
```

With the local demo running, the following smoke check creates a uniquely named product, replays a receipt, restarts the retail service, and checks the customer's balance again.

```powershell
$settings = ConvertFrom-StringData (Get-Content .env -Raw)
./tests/stock-smoke.ps1 -StaffPassword $settings.DEMO_STAFF_PASSWORD -CustomerPassword $settings.DEMO_CUSTOMER_PASSWORD
```

With the Docker demo running, run the Playwright suite from this directory. The suite reads the local `.env` and creates uniquely named test products and orders in the demo database.

```powershell
npm ci --prefix tests/e2e
Push-Location tests/e2e
npx playwright install chromium
npm test
Pop-Location
```

Run `./tests/payment-smoke.ps1` from this directory to verify simultaneous requests, changed terms, a lost reply and restart, both decisions, and database permissions. GitHub Actions runs this simulator check along with the PostgreSQL integration tests, the retail restart check, and the Playwright browser suite.

The integration suite checks concurrent dispatch, rollback at commit, and access from a second customer account. Playwright checks the full flow from checkout to the customer seeing the dispatched order.

After installing the Playwright npm dependencies, run `./tests/payment-flow.ps1` from this directory to exercise the entire checkout and payment path. It starts a disposable Compose project to test 100 signed-in buyers for one unit, a decline that releases stock, and a lost simulator reply. The test leaves the main demo and its data available.

Staff can find overdue orders under **Orders → Payment needs attention** and use **Resume payment check** in the order details. See the [Step 6 test results](tests/step-6-results.md) for recovery evidence, load measurements and reproduction commands.

## Order timeline delivery

An order change saves an outbox entry in the same PostgreSQL transaction. The relay publishes the event to the durable RabbitMQ queue `orders.timeline` and waits for broker confirmation before marking it sent. The reader saves the timeline entry under its stable event ID before acknowledging the message. Staff see the entries in order version sequence and can refresh details while updates are pending. Checkout, payment and dispatch use the current order and stock records in PostgreSQL. [RabbitMQ confirms and acknowledgements](https://www.rabbitmq.com/docs/confirms)

Run `./tests/timeline.ps1` after installing the Playwright npm dependencies. The test uses its own Docker Compose project, kills the relay and reader at their respective crash points, and checks redelivery, out-of-order events, the dead-letter queue and recovery after a broker outage. The RabbitMQ management port is bound to loopback for this test. [Step 7 test results](tests/step-7-results.md)
