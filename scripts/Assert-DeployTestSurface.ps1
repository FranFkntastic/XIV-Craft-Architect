param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('DeployWeb', 'DeployLodestone')]
    [string] $Surface,

    [string] $ProjectPath = 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj',

    [string] $Configuration = 'Release',

    [int] $MinimumTests = 1
)

$ErrorActionPreference = 'Stop'

$filter = "Surface=$Surface"
$arguments = @(
    'test',
    $ProjectPath,
    '--configuration',
    $Configuration,
    '-p:UseSharedCompilation=false',
    '--filter',
    $filter,
    '--list-tests',
    '--logger',
    'console;verbosity=minimal'
)

Write-Host "Validating deploy test surface '$Surface' with filter '$filter'."
$output = & dotnet @arguments 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }

if ($exitCode -ne 0) {
    throw "Unable to list tests for deploy surface '$Surface'. dotnet test exited with code $exitCode."
}

$tests = @()
$collect = $false
foreach ($line in $output) {
    $text = [string]$line
    if ($text -match '^The following Tests are available:') {
        $collect = $true
        continue
    }

    if (-not $collect) {
        continue
    }

    $trimmed = $text.Trim()
    if ($trimmed.Length -eq 0) {
        continue
    }

    $tests += $trimmed
}

if ($tests.Count -lt $MinimumTests) {
    throw "Deploy surface '$Surface' selected $($tests.Count) tests. Expected at least $MinimumTests. Add [Trait(TestTraits.Surface, TestTraits.$Surface)] to deploy-critical tests or fix the workflow filter."
}

Write-Host "Deploy surface '$Surface' selected $($tests.Count) test(s)."
