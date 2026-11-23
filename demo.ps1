[CmdletBinding()]
param([switch]$InitializeOnly, [string]$Images)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot '.env'
$archive = if ($Images) { (Resolve-Path -LiteralPath $Images).Path }

function New-DemoPassword {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return 'Aa1-' + ([BitConverter]::ToString($bytes).Replace('-', ''))
}

if (-not (Test-Path -LiteralPath $configPath)) {
    $settings = @(
        "POSTGRES_PASSWORD=$(New-DemoPassword)"
        "RETAIL_DB_PASSWORD=$(New-DemoPassword)"
        "DEMO_STAFF_PASSWORD=$(New-DemoPassword)"
        "DEMO_CUSTOMER_PASSWORD=$(New-DemoPassword)"
        "SIMULATOR_DB_PASSWORD=$(New-DemoPassword)"
        "RABBITMQ_PASSWORD=$(New-DemoPassword)"
    )
    $stream = [IO.File]::Open($configPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream)
        try { $writer.WriteLine(($settings -join [Environment]::NewLine)) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
} else {
    foreach ($key in 'SIMULATOR_DB_PASSWORD', 'RABBITMQ_PASSWORD') {
        if (-not (Select-String -LiteralPath $configPath -Pattern "^\s*$key\s*=" -Quiet)) {
            [IO.File]::AppendAllText($configPath, [Environment]::NewLine +
                "$key=$(New-DemoPassword)" + [Environment]::NewLine)
        }
    }
}
Write-Output 'The demo account passwords are in the local .env file.'
if ($InitializeOnly) { return }
Push-Location $PSScriptRoot
$previousRetailImage = $env:RETAIL_IMAGE
$previousSimulatorImage = $env:SIMULATOR_IMAGE
try {
    if ($archive) {
        $version = (Get-Content -Raw -LiteralPath VERSION).Trim()
        docker image load --input $archive
        if ($LASTEXITCODE -ne 0) { throw 'Could not load the packaged Docker images.' }
        $env:RETAIL_IMAGE = "order-inventory/retail:$version"
        $env:SIMULATOR_IMAGE = "order-inventory/payment-simulator:$version"
        docker compose up --no-build -d
    } else {
        docker compose up --build -d
    }
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose could not start the demo.' }
    Write-Output 'Use docker compose port retail 8080 to find the local address (default http://localhost:5188).'
    Write-Output 'Use docker compose port simulator 8080 to find the payment simulator address (default http://localhost:5189).'
} finally {
    $env:RETAIL_IMAGE = $previousRetailImage
    $env:SIMULATOR_IMAGE = $previousSimulatorImage
    Pop-Location
}
