[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = 'order-inventory-payment-test-' + [guid]::NewGuid().ToString('N').Substring(0,10)
$keys = 'COMPOSE_FILE','RETAIL_PORT','SIMULATOR_PORT','SIMULATOR_DECISION','SIMULATOR_DROP_NEW_REPLIES'
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & ./demo.ps1 -InitializeOnly
    $env:COMPOSE_FILE = 'compose.yaml' + [IO.Path]::PathSeparator + 'tests/compose.timeline.yaml'
    $env:RETAIL_PORT = '0'
    $env:SIMULATOR_PORT = '0'
    $env:SIMULATOR_DECISION = 'approved'
    $env:SIMULATOR_DROP_NEW_REPLIES = 'false'
    docker compose -p $project up -d --build
    if ($LASTEXITCODE) { throw 'Could not start the isolated timeline stack.' }
    node ./tests/e2e/timeline.js $project
    if ($LASTEXITCODE) { throw 'Timeline test failed.' }
} catch {
    docker compose -p $project logs --tail 30 timeline-relay timeline-reader
    throw
} finally {
    docker compose -p $project down --volumes
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
    Pop-Location
}

