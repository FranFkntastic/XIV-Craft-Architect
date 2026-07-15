[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\FFXIV Craft Architect.Core\Data\world-directory.json')
)

$ErrorActionPreference = 'Stop'

$worldsUrl = 'https://universalis.app/api/v2/worlds'
$dataCentersUrl = 'https://universalis.app/api/v2/data-centers'

Write-Host 'Fetching the release world directory from Universalis...'
$worldsResponse = Invoke-RestMethod -Uri $worldsUrl -TimeoutSec 30
$dataCentersResponse = Invoke-RestMethod -Uri $dataCentersUrl -TimeoutSec 30
$worlds = @($worldsResponse | ForEach-Object { $_ })
$dataCenters = @($dataCentersResponse | ForEach-Object { $_ })

if ($worlds.Count -lt 80) {
    throw "Universalis returned only $($worlds.Count) worlds; refusing to replace the packaged directory."
}

if ($dataCenters.Count -lt 10) {
    throw "Universalis returned only $($dataCenters.Count) data centers; refusing to replace the packaged directory."
}

$worldById = @{}
foreach ($world in $worlds) {
    $worldById[[int]$world.id] = [string]$world.name
}

foreach ($dataCenter in $dataCenters) {
    foreach ($worldId in $dataCenter.worlds) {
        if (-not $worldById.ContainsKey([int]$worldId)) {
            throw "Data center '$($dataCenter.name)' references unknown world ID $worldId."
        }
    }
}

$worldIdToName = [ordered]@{}
foreach ($world in ($worlds | Sort-Object id)) {
    $worldIdToName[[string][int]$world.id] = [string]$world.name
}

$dataCenterToWorlds = [ordered]@{}
foreach ($dataCenter in ($dataCenters | Sort-Object name)) {
    $dataCenterToWorlds[[string]$dataCenter.name] = @(
        $dataCenter.worlds |
            ForEach-Object { $worldById[[int]$_] } |
            Sort-Object
    )
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$topology = [ordered]@{
    worldIdToName = $worldIdToName
    dataCenterToWorlds = $dataCenterToWorlds
}

if (Test-Path -LiteralPath $resolvedOutput) {
    $existing = Get-Content -LiteralPath $resolvedOutput -Raw | ConvertFrom-Json
    $existingTopology = [ordered]@{
        worldIdToName = $existing.worldIdToName
        dataCenterToWorlds = $existing.dataCenterToWorlds
    }
    $currentTopologyJson = $topology | ConvertTo-Json -Depth 8 -Compress
    $existingTopologyJson = $existingTopology | ConvertTo-Json -Depth 8 -Compress
    if ($currentTopologyJson -ceq $existingTopologyJson) {
        Write-Host "World topology is unchanged; left $resolvedOutput untouched."
        exit 0
    }
}

$snapshot = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    source = 'Universalis /api/v2/worlds + /api/v2/data-centers'
    worldIdToName = $worldIdToName
    dataCenterToWorlds = $dataCenterToWorlds
}

$outputDirectory = Split-Path -Parent $resolvedOutput
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8

Write-Host "Wrote $($worlds.Count) worlds across $($dataCenters.Count) data centers to $resolvedOutput"
Write-Host 'Review and commit this generated snapshot; clients receive it with the next deployment.'
