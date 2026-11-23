[CmdletBinding()]
param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot '../release'),
    [switch]$BuildFromSource
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$version = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../VERSION')).Trim()
$name = "order-inventory-demo-$version"
$project = 'order-inventory-release-test-' + [guid]::NewGuid().ToString('N').Substring(0,10)
$unpacked = (New-Item -Path (Join-Path $root $project) -ItemType Directory).FullName
$keys = 'COMPOSE_PROJECT_NAME','COMPOSE_FILE','RETAIL_PORT','SIMULATOR_PORT','RETAIL_IMAGE','SIMULATOR_IMAGE',
    'E2E_BASE_URL','DEMO_STAFF_PASSWORD','DEMO_CUSTOMER_PASSWORD','SIMULATOR_DECISION','SIMULATOR_DROP_NEW_REPLIES'
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
$started = $false
Push-Location $unpacked
try {
    Expand-Archive -LiteralPath (Join-Path $root "$name.zip") -DestinationPath $unpacked
    if (Test-Path -LiteralPath '.env') { throw 'The release archive must omit local credentials.' }

    $env:COMPOSE_FILE = 'compose.yaml'
    $env:COMPOSE_PROJECT_NAME = $project
    $env:RETAIL_PORT = '0'
    $env:SIMULATOR_PORT = '0'
    $env:RETAIL_IMAGE = $null
    $env:SIMULATOR_IMAGE = $null
    $env:SIMULATOR_DECISION = 'approved'
    $env:SIMULATOR_DROP_NEW_REPLIES = 'false'
    $env:DEMO_STAFF_PASSWORD = $null
    $env:DEMO_CUSTOMER_PASSWORD = $null
    $started = $true
    if ($BuildFromSource) {
        & ./demo.ps1
    } else {
        & ./demo.ps1 -Images (Join-Path $root "$name-images.tar")
        $metadata = Get-Content -LiteralPath (Join-Path $root "$name-images.json") -Raw | ConvertFrom-Json
        foreach ($service in 'retail','simulator','timeline-relay','timeline-reader') {
            $container = docker compose ps -q $service
            if ($LASTEXITCODE -or -not $container) { throw "Missing release container $service." }
            $actual = docker inspect --format '{{.Image}}' $container
            $expected = if ($service -eq 'simulator') { $metadata.images.simulator } else { $metadata.images.retail }
            if ($LASTEXITCODE -or $actual -ne $expected) { throw "$service did not launch the packaged image." }
        }
    }

    $config = ConvertFrom-StringData (Get-Content -LiteralPath '.env' -Raw)
    $env:DEMO_STAFF_PASSWORD = $config.DEMO_STAFF_PASSWORD
    $env:DEMO_CUSTOMER_PASSWORD = $config.DEMO_CUSTOMER_PASSWORD
    $retailAddress = docker compose port retail 8080
    if ($LASTEXITCODE -or -not $retailAddress) { throw 'Could not find the HTTP port for the extracted demo.' }
    $env:E2E_BASE_URL = "http://$retailAddress"
    $npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
    & $npm --prefix (Join-Path $PSScriptRoot 'e2e') test -- --output (Join-Path $unpacked 'browser-results')
    if ($LASTEXITCODE) { throw 'Playwright failed against the extracted demo.' }

    Write-Output "PASS: extracted $name on a fresh database."
} finally {
    if ($started -and (Test-Path -LiteralPath '.env')) { docker compose -p $project down --volumes }
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
    Pop-Location
    if ([IO.Path]::GetDirectoryName($unpacked) -eq $root -and [IO.Path]::GetFileName($unpacked) -eq $project) {
        Remove-Item -LiteralPath $unpacked -Recurse -Force
    }
}

