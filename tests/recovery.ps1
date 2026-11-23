[CmdletBinding()]
param([Alias('BaselineOnly')][switch]$Benchmark, [ValidateSet('both','steady','burst')][string]$Workload = 'both')
$ErrorActionPreference = 'Stop'
$project = 'order-inventory-payment-test-' + [guid]::NewGuid().ToString('N').Substring(0,10)
$keys = 'COMPOSE_FILE','RETAIL_PORT','SIMULATOR_PORT','SIMULATOR_DECISION','SIMULATOR_DROP_NEW_REPLIES'
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & ./demo.ps1 -InitializeOnly
    # Restarted containers must keep the ports held by the test clients.
    $ports = @(foreach ($unused in 1..2) {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $socket.Start()
        $socket
    })
    try {
        $env:RETAIL_PORT = [string]$ports[0].LocalEndpoint.Port
        $env:SIMULATOR_PORT = [string]$ports[1].LocalEndpoint.Port
    } finally { foreach ($socket in $ports) { $socket.Stop() } }
    $env:COMPOSE_FILE = if ($Benchmark) { 'compose.yaml' } else {
        'compose.yaml' + [IO.Path]::PathSeparator + 'tests/compose.recovery.yaml'
    }
    $env:SIMULATOR_DROP_NEW_REPLIES = if ($Benchmark) { 'false' } else { 'true' }
    $env:SIMULATOR_DECISION = 'approved'
    docker compose -p $project up -d --build
    if ($LASTEXITCODE) { throw 'Could not start the isolated stack.' }
    if ($Benchmark) {
        node ./tests/e2e/baseline.js $project $Workload
    } else {
        node ./tests/e2e/recovery.js $project
    }
    if ($LASTEXITCODE) { throw 'The test reported one or more failures. Read the measured results above.' }
} finally {
    docker compose -p $project down --volumes
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
    Pop-Location
}

