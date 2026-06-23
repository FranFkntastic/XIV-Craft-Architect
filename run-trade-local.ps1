[CmdletBinding()]
param(
    [int]$WebPort = 5001,
    [int]$LookupPort = 5128,
    [switch]$NoBuild,
    [switch]$StopExisting,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$logRoot = Join-Path $repoRoot ".devlogs\trade-local"
$lookupProject = Join-Path $repoRoot "src\FFXIV Craft Architect.LodestoneLookup\FFXIV Craft Architect.LodestoneLookup.csproj"
$webProject = Join-Path $repoRoot "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj"

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

function Stop-PortOwner {
    param([int]$Port)

    if (-not $StopExisting) {
        return
    }

    $owners = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique

    foreach ($owner in $owners) {
        if ($owner -and $owner -ne $PID) {
            Write-Host "Stopping existing process $owner on port $Port"
            Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-DotnetProject {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [string]$Url,
        [string]$LogName
    )

    $stdout = Join-Path $logRoot "$LogName.out.log"
    $stderr = Join-Path $logRoot "$LogName.err.log"
    Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue

    $arguments = "run --project `"$ProjectPath`" --urls `"$Url`""
    if ($NoBuild) {
        $arguments += " --no-build"
    }

    Write-Host "Starting $Name at $Url"
    $process = Start-Process -FilePath "dotnet" `
        -ArgumentList $arguments `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru `
        -WindowStyle Hidden

    [PSCustomObject]@{
        Name = $Name
        Process = $process
        Url = $Url
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Wait-ForEndpoint {
    param(
        [string]$Name,
        [string]$Url,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                Write-Host "$Name ready: $Url"
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    throw "$Name did not become ready at $Url within $TimeoutSeconds seconds."
}

Stop-PortOwner -Port $LookupPort
Stop-PortOwner -Port $WebPort

$lookupUrl = "http://localhost:$LookupPort"
$webUrl = "http://localhost:$WebPort"
$started = @()

try {
    $started += Start-DotnetProject -Name "Lodestone lookup helper" -ProjectPath $lookupProject -Url $lookupUrl -LogName "lodestone-$LookupPort"
    $started += Start-DotnetProject -Name "Trade web app" -ProjectPath $webProject -Url $webUrl -LogName "web-$WebPort"

    Wait-ForEndpoint -Name "Lodestone lookup helper" -Url $lookupUrl
    Wait-ForEndpoint -Name "Trade web app" -Url $webUrl

    Write-Host ""
    Write-Host "FFXIV Craft Architect Trade local stack is running."
    Write-Host "Web app:          $webUrl"
    Write-Host "Lodestone helper: $lookupUrl"
    Write-Host "Logs:             $logRoot"
    Write-Host "Press Ctrl+C to stop both processes."

    if (-not $NoBrowser) {
        Start-Process "$webUrl/trade/crafters"
    }

    while ($true) {
        foreach ($entry in $started) {
            if ($entry.Process.HasExited) {
                throw "$($entry.Name) exited with code $($entry.Process.ExitCode). Check $($entry.Stdout) and $($entry.Stderr)."
            }
        }

        Start-Sleep -Seconds 1
    }
}
finally {
    foreach ($entry in $started) {
        if ($entry.Process -and -not $entry.Process.HasExited) {
            Write-Host "Stopping $($entry.Name)"
            Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
