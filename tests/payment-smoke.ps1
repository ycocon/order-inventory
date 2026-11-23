# Run from the Compose project directory against the local demo.
[CmdletBinding()]
param([string] $BaseUrl)

$ErrorActionPreference = 'Stop'
if (-not $BaseUrl) {
    $binding = docker compose port simulator 8080
    if ($LASTEXITCODE -ne 0 -or -not $binding) { throw 'Start the Docker demo before running this test.' }
    $BaseUrl = "http://$binding"
}
$BaseUrl = $BaseUrl.TrimEnd('/')
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(12)

function Wait-Ready([string] $Url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $state = Invoke-RestMethod -Uri "$Url/health/ready" -TimeoutSec 2
            if ($state.status -eq 'ready') { return }
        } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The simulator did not become ready at $Url."
}

function Start-Authorization([string] $Url, [guid] $Id, [long] $Amount, [string] $Currency, [switch] $DropReply) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, "$Url/api/authorizations/$Id")
    $body = @{ amountMinor = $Amount; currency = $Currency } | ConvertTo-Json -Compress
    $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    if ($DropReply) { $request.Headers.Add('X-Simulator-Drop-Reply', 'true') }
    return @{ Request = $request; Task = $client.SendAsync($request) }
}

function Complete-Authorization($Operation) {
    try {
        $response = $Operation.Task.GetAwaiter().GetResult()
        try {
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Status = [int] $response.StatusCode; Body = $content | ConvertFrom-Json }
        } finally { $response.Dispose() }
    } finally { $Operation.Request.Dispose() }
}

$alternateId = $null
try {
    Wait-Ready $BaseUrl
    $raceId = [Guid]::NewGuid()
    $calls = 1..10 | ForEach-Object { Start-Authorization $BaseUrl $raceId 1250 'USD' }
    $results = @($calls | ForEach-Object { Complete-Authorization $_ })
    $created = @($results | Where-Object Status -eq 201)
    if ($created.Count -ne 1 -or @($results | Where-Object Status -eq 200).Count -ne 9) {
        throw 'Concurrent requests should yield one creation and nine replays.'
    }
    $original = $created[0].Body
    if ($original.decision -notin @('approved', 'declined')) { throw 'Unexpected authorization decision.' }
    foreach ($result in $results) {
        if ($result.Body.operationId -ne $raceId.ToString() -or $result.Body.decision -ne $original.decision -or
            $result.Body.decidedAt -ne $original.decidedAt) { throw 'Replays changed the original decision.' }
    }
    foreach ($conflict in @(
        (Complete-Authorization (Start-Authorization $BaseUrl $raceId 1400 'USD')),
        (Complete-Authorization (Start-Authorization $BaseUrl $raceId 1250 'EUR'))
    )) {
        if ($conflict.Status -ne 409) { throw 'Changing amount or currency must conflict.' }
    }
    Write-Output 'PASS: ten concurrent submissions recorded one unchanged payment decision.'

    $lostId = [Guid]::NewGuid()
    $connectionDropped = $false
    try { $null = Complete-Authorization (Start-Authorization $BaseUrl $lostId 599 'USD' -DropReply) }
    catch [System.Net.Http.HttpRequestException] { $connectionDropped = $true }
    if (-not $connectionDropped) { throw 'The test client should lose the HTTP reply.' }
    $before = Invoke-RestMethod "$BaseUrl/api/authorizations/$lostId"
    docker compose restart simulator
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart the simulator.' }
    Wait-Ready $BaseUrl
    $after = Invoke-RestMethod "$BaseUrl/api/authorizations/$lostId"
    if ($after.operationId -ne $lostId.ToString() -or $after.amountMinor -ne 599 -or $after.currency -ne 'USD' -or
        $after.decision -ne $before.decision -or $after.decidedAt -ne $before.decidedAt) {
        throw 'Restart did not preserve the original decision.'
    }
    Write-Output 'PASS: a payment whose reply disappeared survived a simulator container restart.'

    $otherDecision = if ($original.decision -eq 'approved') { 'declined' } else { 'approved' }
    $alternateId = docker compose run --detach --rm --no-deps --publish '127.0.0.1::8080' --env "Simulator__Decision=$otherDecision" simulator
    if ($LASTEXITCODE -ne 0 -or $alternateId -notmatch '^[0-9a-f]{64}$') { throw 'Could not start the alternate simulator.' }
    $alternateBinding = docker port $alternateId '8080/tcp'
    if ($LASTEXITCODE -ne 0 -or -not $alternateBinding) { throw 'Could not find the alternate simulator address.' }
    $alternateUrl = "http://$alternateBinding"
    Wait-Ready $alternateUrl
    $fresh = Complete-Authorization (Start-Authorization $alternateUrl ([Guid]::NewGuid()) 1250 'USD')
    $same = Complete-Authorization (Start-Authorization $alternateUrl $raceId 1250 'USD')
    if ($fresh.Status -ne 201 -or $fresh.Body.decision -ne $otherDecision -or
        $same.Status -ne 200 -or $same.Body.decision -ne $original.decision) {
        throw 'A policy switch should change new decisions and preserve recorded ones.'
    }
    Write-Output 'PASS: approval and decline both work, and a policy switch preserves the old decision.'

    $sql = @'
SELECT
    NOT has_database_privilege('payment_simulator_app', 'retail', 'CONNECT')
    AND NOT has_database_privilege('retail_app', 'payment_simulator', 'CONNECT')
    AND has_table_privilege('payment_simulator_app', 'payment_authorizations', 'SELECT')
    AND has_table_privilege('payment_simulator_app', 'payment_authorizations', 'INSERT')
    AND NOT has_table_privilege('payment_simulator_app', 'payment_authorizations', 'UPDATE')
    AND NOT has_table_privilege('payment_simulator_app', 'payment_authorizations', 'DELETE');
'@
    $isolation = docker compose exec -T db psql --no-psqlrc --username postgres --dbname payment_simulator --tuples-only --no-align --set ON_ERROR_STOP=1 --command $sql
    if ($LASTEXITCODE -ne 0 -or $isolation -ne 't') { throw 'The simulator ledger privileges are not isolated.' }
    Write-Output 'PASS: database logins are isolated and the simulator can only read or insert ledger entries.'
} finally {
    if ($alternateId -match '^[0-9a-f]{64}$') { docker stop $alternateId | Out-Null }
    $client.Dispose()
}
