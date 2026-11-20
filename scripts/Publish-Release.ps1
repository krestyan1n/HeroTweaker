param(
    [string]$Configuration = 'Release',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'HeroTweaker.csproj'
$frontendDirectory = Join-Path $projectRoot 'frontend'
$artifactRoot = Join-Path $projectRoot 'artifacts\release'
$isolatedBuildOutput = Join-Path $artifactRoot 'build-bin'
$publishDirectory = Join-Path $artifactRoot 'publish\win-x64'
$archivePath = Join-Path $artifactRoot 'HeroTweaker-win-x64.zip'
$hashPath = "$archivePath.sha256"
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Project file not found: $projectFile"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDirectory).Path
    $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    if (-not $resolvedPublish.StartsWith($resolvedArtifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a publish path outside artifacts: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
if (Test-Path -LiteralPath $hashPath) { Remove-Item -LiteralPath $hashPath -Force }

$publishArgs = @(
    'publish', $projectFile,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    "-p:BaseOutputPath=$isolatedBuildOutput\",
    '-o', $publishDirectory
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:AssemblyVersion=$Version"
    $publishArgs += "-p:FileVersion=$Version"
}

Push-Location $projectRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $frontendDirectory 'node_modules'))) {
        & npm ci --prefix frontend
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
    }
    & npm run build --prefix frontend
    if ($LASTEXITCODE -ne 0) { throw "React/Tailwind build failed with exit code $LASTEXITCODE" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$applicationPath = Join-Path $publishDirectory 'HeroTweaker.exe'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Published apphost was not created: $applicationPath"
}

# Archive the directory contents at ZIP root so the updater can safely install a flat app folder.
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash  HeroTweaker-win-x64.zip" -Encoding ascii

Write-Host "Release package: $archivePath"
Write-Host "SHA-256: $hash"
