# Run from the project root after installing the Playwright npm dependencies.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = 'order-inventory-payment-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$keys = 'RETAIL_PORT', 'SIMULATOR_PORT', 'SIMULATOR_DECISION', 'SIMULATOR_DROP_NEW_REPLIES'
$original = @{}
foreach ($key in $keys) { $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
    $env:RETAIL_PORT = '0'
    $env:SIMULATOR_PORT = '0'
    $env:SIMULATOR_DECISION = 'approved'
    $env:SIMULATOR_DROP_NEW_REPLIES = 'false'
    & ./demo.ps1 -InitializeOnly
    docker compose -p $project up --build -d
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the isolated payment test stack.' }

    node ./tests/e2e/payment-flow.js $project race
    if ($LASTEXITCODE -ne 0) { throw 'Last-unit race failed.' }

    $env:SIMULATOR_DECISION = 'declined'
    docker compose -p $project up -d --no-deps simulator
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the test simulator to decline.' }
    node ./tests/e2e/payment-flow.js $project decline
    if ($LASTEXITCODE -ne 0) { throw 'Payment decline test failed.' }

    $env:SIMULATOR_DECISION = 'approved'
    $env:SIMULATOR_DROP_NEW_REPLIES = 'true'
    docker compose -p $project up -d --no-deps simulator
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the test simulator to drop new replies.' }
    node ./tests/e2e/payment-flow.js $project lost
    if ($LASTEXITCODE -ne 0) { throw 'Lost simulator response test failed.' }
} catch {
    docker compose -p $project logs --tail 60 retail simulator db
    throw
} finally {
    # This uniquely named stack and its volumes were created solely for this test.
    docker compose -p $project down --volumes
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $original[$key], 'Process') }
}
