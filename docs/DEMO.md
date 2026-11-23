# Reviewer demo

Use Docker with Linux containers, Docker Compose v2, and PowerShell 7. The first startup can need an internet connection for the base images or build dependencies.

## Start from the source ZIP

Extract `order-inventory-demo-1.0.0.zip` and open PowerShell in the extracted directory.

```powershell
./demo.ps1
```

If you also downloaded the matching Linux amd64 app images, put the image tar next to `demo.ps1` and start with this command. Compose still pulls PostgreSQL and RabbitMQ if they are missing. [Docker image loading](https://docs.docker.com/reference/cli/docker/image/load/)

```powershell
./demo.ps1 -Images ./order-inventory-demo-1.0.0-images.tar
```

Open [the retail app](http://localhost:5188). The launcher creates fresh demo passwords in the local `.env`. Stop the demo with `docker compose down` from the same directory. Restarting preserves the named volumes.

## Show one order end to end

1. Sign in as `staff@demo.local` using `DEMO_STAFF_PASSWORD` from `.env`. Use **Receive stock** to add one unit of **FIELD-NOTES**.
2. In a separate browser profile or private window, sign in as `customer@demo.local` using `DEMO_CUSTOMER_PASSWORD`. Add the notebook to the basket and submit the order. Stock is reserved at submission. Copy the order reference after payment confirms.
3. Back in the staff window, open **Orders**, find the reference and select **Review**. Show the saved payment ID, reservation and timeline. If the timeline is catching up, select **Refresh details**.
4. Dispatch the order. The customer can select **Refresh orders** to see it marked **Dispatched**. The staff view records the stock leaving inventory and who dispatched it.

## Reproduce the failure tests

Node.js 24 is needed for the automated checks below. Install the checked-in test dependencies first. These scripts use their own Docker Compose projects and generate their own test orders.

```powershell
npm ci --prefix tests/e2e
./tests/payment-flow.ps1
./tests/timeline.ps1
```

The first script races 100 signed-in buyers for one unit, tests a payment decline and recovers an approval whose reply was lost. The second kills the RabbitMQ publisher and reader at separate checkpoints and verifies that duplicate delivery results in one timeline entry. [Payment recovery results](../tests/step-6-results.md) · [RabbitMQ test results](../tests/step-7-results.md)

To repeat the two k6 workloads, run `./tests/recovery.ps1 -Benchmark`. Allow roughly six minutes of traffic plus time for payment and timeline work to finish. [Benchmark inputs and results](BENCHMARKS.md)

## Publish a portfolio release

Use a version tag in the source repository and attach the verified source ZIP, the matching app image tar, the image metadata and the SHA-256 checksums. Link the walkthrough and case study from the repository’s front page. Keep the tests and benchmark evidence alongside the code. The included GitHub Actions workflow builds and tests the app, exports the image IDs that ran those checks, launches the extracted package with those images, and uploads the files as a workflow artifact. [GitHub Actions artifacts](https://docs.github.com/en/actions/tutorials/store-and-share-data)

With the demo running and Playwright installed, the maintainer can build and check the same files locally.

```powershell
./package.ps1 -IncludeImages
./tests/package-smoke.ps1
```

