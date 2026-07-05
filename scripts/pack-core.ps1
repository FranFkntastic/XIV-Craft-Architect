param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\packages")
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
$resolvedOutput = Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force -Path $OutputPath)

dotnet pack $project `
    --configuration $Configuration `
    --output $resolvedOutput `
    /p:ContinuousIntegrationBuild=true
