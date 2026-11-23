[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'release'),
    [switch]$IncludeImages
)

$ErrorActionPreference = 'Stop'
$destination = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$version = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a release version such as 1.0.0.' }
$name = "order-inventory-demo-$version"
$archivePath = Join-Path $destination "$name.zip"

# Package only source, docs, and runnable fixtures. Local credentials and build output stay outside.
$rootFiles = '.dockerignore', '.gitattributes', '.gitignore', 'README.md', 'VERSION',
    'compose.yaml', 'demo.ps1', 'package.ps1', 'Dockerfile', 'Retail.sln'
$trees = '.github', 'docs', 'infra', 'src', 'tests', 'web'
$skipDirectories = 'bin', 'obj', 'node_modules', 'dist', 'TestResults', 'test-results', 'playwright-report'
$textExtensions = '.cs', '.csproj', '.ts', '.tsx', '.css', '.html', '.js', '.mjs',
    '.json', '.jsonl', '.yaml', '.yml', '.ps1', '.sh', '.md'
function Get-ReleaseFiles([string]$directory) {
    Get-ChildItem -LiteralPath $directory -File | Where-Object {
        ($_.Name -eq 'Dockerfile' -or $_.Extension -in $textExtensions) -and
        -not $_.Name.StartsWith('.') -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
    }
    Get-ChildItem -LiteralPath $directory -Directory | Where-Object {
        $_.Name -notin $skipDirectories -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
    } | ForEach-Object { Get-ReleaseFiles $_.FullName }
}

$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = foreach ($file in $rootFiles) { Get-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Force }
    foreach ($tree in $trees) { $files += @(Get-ReleaseFiles (Join-Path $PSScriptRoot $tree)) }
    foreach ($file in $files | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative) | Out-Null
    }
} finally { $zip.Dispose() }
$artifacts = @($archivePath)

if ($IncludeImages) {
    # Tag the running demo images by their actual IDs, so the package uses the images just verified.
    Push-Location $PSScriptRoot
    try {
        $references = @{ retail = "order-inventory/retail:$version"; simulator = "order-inventory/payment-simulator:$version" }
        $imageIds = [ordered]@{}
        foreach ($service in 'retail','simulator') {
            $container = docker compose ps -q $service
            if ($LASTEXITCODE -or -not $container) { throw "Start the verified Compose service $service before packaging." }
            $id = docker inspect --format '{{.Image}}' $container
            if ($LASTEXITCODE -or -not $id) { throw "Could not identify the running image for $service." }
            $runningReference = docker inspect --format '{{.Config.Image}}' $container
            if ($LASTEXITCODE -or -not $runningReference) { throw "Could not identify the running tag for $service." }
            $taggedId = docker image inspect --format '{{.Id}}' $runningReference
            if ($LASTEXITCODE -or $taggedId -ne $id) {
                throw "The running $service uses an older image than its tag. Recreate and verify the demo before packaging."
            }
            docker image tag $runningReference $references[$service]
            if ($LASTEXITCODE) { throw "Could not tag $service for release." }
            $imageIds[$service] = $id
        }
        $platform = docker image inspect --format '{{.Os}}/{{.Architecture}}' $references.retail
        if ($LASTEXITCODE) { throw 'Could not identify the platform of the packaged images.' }
        $tarPath = Join-Path $destination "$name-images.tar"
        docker image save --output $tarPath $references.retail $references.simulator
        if ($LASTEXITCODE) { throw 'Could not export the packaged images.' }
        $metadata = Join-Path $destination "$name-images.json"
        [ordered]@{
            version = $version
            platform = $platform
            commit = $env:GITHUB_SHA
            images = $imageIds
        } | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8
        $artifacts += $tarPath, $metadata
    } finally { Pop-Location }
}

$checksumPath = Join-Path $destination "$name-sha256.txt"
$artifacts | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
} | Set-Content -LiteralPath $checksumPath -Encoding utf8
$artifacts + $checksumPath | Write-Output

