# Run from the Compose project directory against a running local demo.
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5188',
    [string] $RetailService = 'retail',
    [string] $StaffPassword = $env:DEMO_STAFF_PASSWORD,
    [string] $CustomerPassword = $env:DEMO_CUSTOMER_PASSWORD,
    [scriptblock] $RestartRetail
)

$ErrorActionPreference = 'Stop'
if (-not $StaffPassword -or -not $CustomerPassword) {
    throw 'Set DEMO_STAFF_PASSWORD and DEMO_CUSTOMER_PASSWORD or supply the two password parameters.'
}
$BaseUrl = $BaseUrl.TrimEnd('/')

function New-DemoLogin([string] $Role, [string] $Password) {
    $jar = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $anonymous = Invoke-RestMethod -Uri "$BaseUrl/api/session" -WebSession $jar
    $headers = @{ 'X-CSRF-TOKEN' = $anonymous.csrfToken }
    $body = @{ email = "$Role@demo.local"; password = $Password } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$BaseUrl/api/login" -Method Post -ContentType 'application/json' -Body $body -Headers $headers -WebSession $jar
    $signedIn = Invoke-RestMethod -Uri "$BaseUrl/api/session" -WebSession $jar
    if ($signedIn.user.role -ne $Role) { throw "Expected $Role session." }
    return @{ jar = $jar; headers = @{ 'X-CSRF-TOKEN' = $signedIn.csrfToken } }
}

function Send-DemoCommand($Session, [string] $Path, $InputBody) {
    $params = @{
        Uri = "$BaseUrl$Path"; Method = 'Post'; WebSession = $Session.jar
        Headers = $Session.headers; ContentType = 'application/json'; Body = ($InputBody | ConvertTo-Json)
    }
    $null = Invoke-RestMethod @params
}

function Assert-OneUnit($Session, [string] $Sku) {
    $products = Invoke-RestMethod -Uri "$BaseUrl/api/products" -WebSession $Session.jar
    $match = @($products | Where-Object { $_.sku -eq $Sku })
    if ($match.Count -ne 1 -or $match[0].available -ne 1) {
        throw "Expected exactly one unit of $Sku in the customer catalog."
    }
}

function Wait-Retail {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $null = Invoke-RestMethod -Uri "$BaseUrl/health/ready" -TimeoutSec 2
            return
        } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw "Retail did not become ready. $_" }
            Start-Sleep -Milliseconds 400
        }
    } while ($true)
}

Wait-Retail
$staff = New-DemoLogin 'staff' $StaffPassword
$sku = 'S-' + [Guid]::NewGuid().ToString('N').Substring(0,12).ToUpperInvariant()
Send-DemoCommand $staff '/api/products' @{ sku = $sku; name = 'Restart smoke item'; active = $true; unitPriceMinor = 125 }
$receipt = @{ sku = $sku; quantity = 1; reason = 'Restart smoke test'; requestKey = [Guid]::NewGuid().ToString() }
Send-DemoCommand $staff '/api/receipts' $receipt
Send-DemoCommand $staff '/api/receipts' $receipt
Assert-OneUnit (New-DemoLogin 'customer' $CustomerPassword) $sku

if ($RestartRetail) {
    & $RestartRetail
} else {
    & docker compose restart $RetailService
    if ($LASTEXITCODE -ne 0) { throw "Compose could not restart $RetailService." }
}
Wait-Retail

$staff = New-DemoLogin 'staff' $StaffPassword
Send-DemoCommand $staff '/api/receipts' $receipt
Assert-OneUnit (New-DemoLogin 'customer' $CustomerPassword) $sku
Write-Output "PASS: $sku has one available unit after the receipt was replayed before and after restart."
