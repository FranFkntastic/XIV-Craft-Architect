param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\packages")
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
$resolvedOutput = Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force -Path $OutputPath)
$franthropyRoot = & (Join-Path $PSScriptRoot "Resolve-Franthropy.ps1")
$franthropyFiltering = Join-Path $franthropyRoot "src\Franthropy.Filtering\Franthropy.Filtering.csproj"
$franthropyFfxiv = Join-Path $franthropyRoot "src\Franthropy.FFXIV\Franthropy.FFXIV.csproj"

dotnet pack $franthropyFiltering `
    --configuration $Configuration `
    --output $resolvedOutput `
    /p:ContinuousIntegrationBuild=true

dotnet pack $franthropyFfxiv `
    --configuration $Configuration `
    --output $resolvedOutput `
    /p:ContinuousIntegrationBuild=true

dotnet pack $project `
    --configuration $Configuration `
    --output $resolvedOutput `
    /p:FranthropyRoot=$franthropyRoot `
    /p:ContinuousIntegrationBuild=true
