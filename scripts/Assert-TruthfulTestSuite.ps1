[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$legacyProject = Join-Path $root 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj'
$testProjects = @(
    Join-Path $root 'src/FFXIV Craft Architect.SpecTests'
    Join-Path $root 'src/FFXIV Craft Architect.ContractTests'
)

if (Test-Path -LiteralPath $legacyProject) {
    throw 'Legacy FFXIV Craft Architect.Tests project still exists.'
}

$sourceFiles = @()
foreach ($project in $testProjects) {
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Required test project is missing: $project"
    }

    $sourceFiles += Get-ChildItem -LiteralPath $project -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}

$methodCount = 0
$lineCount = 0
$violations = @()
foreach ($file in $sourceFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $lineCount += @(Get-Content -LiteralPath $file.FullName).Count
    $methodCount += [regex]::Matches($text, '(?m)^\s*\[(Fact|Theory)(?:\([^\]]*\))?\]\s*$').Count

    if ($text -match '(?i)\bSkip\s*=') {
        $violations += "$($file.FullName): skipped test"
    }
    if ($text -match 'MarketAnalysisProbe') {
        $violations += "$($file.FullName): forbidden MarketAnalysisProbe dependency"
    }
    if ($text -match '\bMock<|\bnew\s+Mock\b') {
        $violations += "$($file.FullName): mock-based test"
    }
    if ($text -match '\[(Fact|Theory)') {
        if ($text -notmatch '\bAssert\.') {
            $violations += "$($file.FullName): test file has no assertion"
        }
    }
}

if ($methodCount -eq 0) {
    $violations += 'No test methods were discovered.'
}
if ($methodCount -gt 150) {
    $violations += "Test method ceiling exceeded: $methodCount > 150."
}
if ($lineCount -gt 10000) {
    $violations += "Test source ceiling exceeded: $lineCount > 10000."
}

$specProject = Get-Content -LiteralPath (Join-Path $testProjects[0] 'FFXIV Craft Architect.SpecTests.csproj') -Raw
foreach ($forbiddenReference in @('FFXIV Craft Architect.Web', 'FFXIV Craft Architect.LodestoneLookup', 'MarketAnalysisProbe', 'Moq', 'bunit')) {
    if ($specProject -match [regex]::Escape($forbiddenReference)) {
        $violations += "SpecTests references forbidden dependency: $forbiddenReference"
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw 'Truthful test-suite structure is invalid.'
}

Write-Output "Truthful suite structure valid: $methodCount test methods, $lineCount source lines."
