param(
    [Parameter(Mandatory = $true)]
    [string]$PlanPath,

    [string]$Scenario = "plan-1743",
    [string]$OutputDirectory = "C:\tmp\market-analysis-benchmark-suite",
    [string]$BaselineRepoRoot = "C:\Users\gianf\.codex\worktrees\a2bd\FFXIV Craft Architect C# Edition",
    [string]$StabilizationRepoRoot = (Resolve-Path ".").Path,
    [string]$BaselineLabel = "local-dev-1cd9938",
    [string]$StabilizationLabel = "stabilization",
    [int]$BaselineAppPort = 5002,
    [int]$StabilizationAppPort = 5003,
    [string]$BaselineUrl = "",
    [string]$StabilizationUrl = "",
    [int]$BaselineDevToolsPort = 9232,
    [int]$StabilizationDevToolsPort = 9233,
    [string]$SystemProfile = "clean",
    [int]$ProcessTimeoutSeconds = 420,
    [switch]$IncludeBrowser,
    [switch]$IncludeFake,
    [switch]$IncludeWarmSequence,
    [switch]$OwnDevServers,
    [switch]$AppendDocsSummary,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Resolve-RequiredPath {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Convert-ToCommandText {
    param([string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join " "
}

function Invoke-Git {
    param(
        [string]$RepoRoot,
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previous = (Get-Location).Path
    try {
        Set-Location -LiteralPath $RepoRoot
        $output = & git @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) {
            if ($AllowFailure) {
                return ""
            }

            throw "git $($Arguments -join ' ') failed in $RepoRoot"
        }

        $text = ($output -join "`n").Trim()
        if (-not $AllowFailure -and [string]::IsNullOrWhiteSpace($text)) {
            throw "git $($Arguments -join ' ') returned no output in $RepoRoot"
        }

        return $text
    }
    finally {
        Set-Location -LiteralPath $previous
    }
}

function Get-GitState {
    param([string]$RepoRoot)

    $dirtyState = Invoke-Git -RepoRoot $RepoRoot -Arguments @("status", "--short") -AllowFailure
    [ordered]@{
        repoRoot = $RepoRoot
        branch = Invoke-Git -RepoRoot $RepoRoot -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
        commit = Invoke-Git -RepoRoot $RepoRoot -Arguments @("rev-parse", "HEAD")
        shortCommit = Invoke-Git -RepoRoot $RepoRoot -Arguments @("rev-parse", "--short", "HEAD")
        dirty = -not [string]::IsNullOrWhiteSpace($dirtyState)
        dirtyState = $dirtyState
    }
}

function Test-PortListening {
    param([int]$Port)

    $pattern = "[:.]$Port\s+.*LISTENING"
    return [bool](netstat -ano | Select-String -Pattern $pattern)
}

function Get-PortProcessIds {
    param([int]$Port)

    $pattern = "[:.]$Port\s+.*LISTENING\s+(\d+)\s*$"
    $matches = netstat -ano | Select-String -Pattern $pattern
    return @($matches | ForEach-Object {
        if ($_.Matches.Count -gt 0) {
            [int]$_.Matches[0].Groups[1].Value
        }
    } | Select-Object -Unique)
}

function Get-ProcessCommandLine {
    param([int]$ProcessId)

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    return $process?.CommandLine
}

function Wait-PortListening {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortListening -Port $Port) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for port $Port to listen."
}

function Start-WebServer {
    param(
        [string]$RepoRoot,
        [int]$Port,
        [string]$Label
    )

    if (Test-PortListening -Port $Port) {
        return [ordered]@{
            owned = $false
            pid = $null
            port = $Port
            label = $Label
            note = "Port already listening; treating server as externally managed."
            existingPids = @(Get-PortProcessIds -Port $Port)
        }
    }

    $projectPath = Join-Path $RepoRoot "src\FFXIV Craft Architect.Web\FFXIV Craft Architect.Web.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Web project was not found at $projectPath"
    }

    $arguments = @(
        "run",
        "--project",
        $projectPath,
        "--urls",
        "http://127.0.0.1:$Port"
    )
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = "dotnet"
    foreach ($argument in $arguments) {
        [void]$process.StartInfo.ArgumentList.Add($argument)
    }

    $process.StartInfo.WorkingDirectory = $RepoRoot
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    [void]$process.Start()
    Wait-PortListening -Port $Port

    return [ordered]@{
        owned = $true
        pid = $process.Id
        port = $Port
        label = $Label
        repoRoot = $RepoRoot
        commandLine = Get-ProcessCommandLine -ProcessId $process.Id
        note = "Started by suite runner."
        existingPids = @()
    }
}

function Stop-OwnedServer {
    param($Server)

    if ($null -ne $Server -and $Server.owned -and $Server.pid) {
        Stop-ProcessTree -ProcessId $Server.pid
    }
}

function Stop-ProcessTree {
    param([int]$ProcessId)

    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
    foreach ($child in @($children)) {
        Stop-ProcessTree -ProcessId $child.ProcessId
    }

    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function New-RunRecord {
    param(
        [string]$Id,
        [string]$Layer,
        [string]$Label,
        [string]$RepoRoot,
        [string[]]$Command,
        [string]$OutputPath,
        [string]$TargetAppRepoRoot = ""
    )

    [ordered]@{
        id = $Id
        layer = $Layer
        label = $Label
        repoRoot = $RepoRoot
        targetAppRepoRoot = $TargetAppRepoRoot
        outputPath = $OutputPath
        status = "pending"
        exitCode = $null
        startedAtUtc = $null
        completedAtUtc = $null
        command = $Command
        commandText = Convert-ToCommandText -Arguments $Command
        stdout = ""
        stderr = ""
        error = $null
    }
}

function Invoke-RunRecord {
    param(
        [object]$Run,
        [int]$TimeoutSeconds
    )

    $Run.startedAtUtc = [DateTimeOffset]::UtcNow
    if ($DryRun) {
        $Run.status = "dry-run"
        $Run.completedAtUtc = [DateTimeOffset]::UtcNow
        return $Run
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = $Run.command[0]
    foreach ($argument in $Run.command[1..($Run.command.Count - 1)]) {
        [void]$process.StartInfo.ArgumentList.Add($argument)
    }

    $process.StartInfo.WorkingDirectory = $Run.repoRoot
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true

    try {
        [void]$process.Start()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                Stop-ProcessTree -ProcessId $process.Id
            }
            catch {
                Write-Warning "Failed to kill timed-out process $($process.Id): $($_.Exception.Message)"
            }

            $Run.status = "timeout"
            $Run.error = "Timed out after $TimeoutSeconds seconds."
        }
        else {
            $Run.exitCode = $process.ExitCode
            $Run.status = if ($process.ExitCode -eq 0) { "completed" } else { "failed" }
        }

        $Run.stdout = $process.StandardOutput.ReadToEnd()
        $Run.stderr = $process.StandardError.ReadToEnd()
    }
    catch {
        $Run.status = "failed"
        $Run.error = $_.Exception.Message
    }
    finally {
        $Run.completedAtUtc = [DateTimeOffset]::UtcNow
    }

    return $Run
}

function New-CliLiveRun {
    param(
        [string]$Label,
        [string]$RepoRoot
    )

    $output = Join-Path $OutputDirectory "$Scenario-$Label-cli-live-cold.json"
    $project = Join-Path $RepoRoot "tools\MarketAnalysisProbe\MarketAnalysisProbe.csproj"
    $command = @(
        "dotnet",
        "run",
        "--project",
        $project,
        "--",
        $PlanPath,
        "--region",
        "--region-dc-concurrency",
        "2",
        "--per-dc-chunk-concurrency",
        "3",
        "--initial-chunk-size",
        "10",
        "--min-chunk-size",
        "5",
        "--json-out",
        $output
    )

    New-RunRecord `
        -Id "$Label-cli-live-cold" `
        -Layer "cli-core" `
        -Label $Label `
        -RepoRoot $RepoRoot `
        -Command $command `
        -OutputPath $output `
        -TargetAppRepoRoot $RepoRoot
}

function New-CliFakeRun {
    param(
        [string]$Label,
        [string]$RepoRoot
    )

    $output = Join-Path $OutputDirectory "$Scenario-$Label-cli-fake-live-shaped-504.json"
    $project = Join-Path $RepoRoot "tools\MarketAnalysisProbe\MarketAnalysisProbe.csproj"
    $command = @(
        "dotnet",
        "run",
        "--project",
        $project,
        "--",
        $PlanPath,
        "--region",
        "--region-dc-concurrency",
        "2",
        "--per-dc-chunk-concurrency",
        "3",
        "--initial-chunk-size",
        "10",
        "--min-chunk-size",
        "5",
        "--fake-http-scenario",
        "liveshaped504pressure",
        "--fake-504-delay-ms",
        "250",
        "--fake-success-delay-ms",
        "0",
        "--json-out",
        $output
    )

    New-RunRecord `
        -Id "$Label-cli-fake-live-shaped-504" `
        -Layer "cli-core-mechanism-only" `
        -Label $Label `
        -RepoRoot $RepoRoot `
        -Command $command `
        -OutputPath $output `
        -TargetAppRepoRoot $RepoRoot
}

function New-BrowserRun {
    param(
        [string]$Label,
        [string]$AppRepoRoot,
        [string]$Url,
        [int]$AppPort,
        [int]$DevToolsPort,
        [string]$CacheState,
        [string]$RunSuffix,
        [int]$WarmIndex = 0
    )

    $suiteRoot = $StabilizationRepoRoot
    $outputName = if ($WarmIndex -gt 0) {
        "$Scenario-$Label-browser-$RunSuffix-$WarmIndex.json"
    }
    else {
        "$Scenario-$Label-browser-$RunSuffix.json"
    }
    $runId = if ($WarmIndex -gt 0) {
        "$Label-browser-$RunSuffix-$WarmIndex"
    }
    else {
        "$Label-browser-$RunSuffix"
    }
    $output = Join-Path $OutputDirectory $outputName
    $wrapper = Join-Path $suiteRoot "tools\Run-MarketAnalysisBrowserBenchmark.ps1"
    $profile = Join-Path $OutputDirectory "chrome-$Scenario-$runId"
    if ([string]::IsNullOrWhiteSpace($Url)) {
        $Url = "http://127.0.0.1:$AppPort"
    }
    $harnessArgs = @(
        "--navigate",
        "--enable-developer-mode",
        "--import-native-plan",
        $PlanPath,
        "--click-benchmark-id",
        "main-nav-market-analysis",
        "--click-benchmark-id",
        "market-analysis-run",
        "--wait-market-analysis-completion",
        "--scenario",
        $Scenario,
        "--cache-state",
        $CacheState,
        "--system-profile",
        $SystemProfile,
        "--label",
        $runId,
        "--output",
        $output,
        "--timeout-seconds",
        "240"
    )
    $command = @(
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $wrapper,
        "-Url",
        $Url,
        "-DevToolsPort",
        "$DevToolsPort",
        "-ChromeProfile",
        $profile,
        "-ProcessTimeoutSeconds",
        "$ProcessTimeoutSeconds"
    ) + $harnessArgs

    New-RunRecord `
        -Id $runId `
        -Layer "browser" `
        -Label $Label `
        -RepoRoot $suiteRoot `
        -Command $command `
        -OutputPath $output `
        -TargetAppRepoRoot $AppRepoRoot
}

function Write-DocsSummary {
    param($Manifest)

    if (-not $AppendDocsSummary) {
        return
    }

    $summaryPath = Join-Path $StabilizationRepoRoot "docs\planning\MARKET_ANALYSIS_SCALING_BENCHMARK_RESULTS.md"
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        Write-Warning "Benchmark results doc not found: $summaryPath"
        return
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $tick = [char]96
    $lines.Add("")
    $lines.Add("### $(Get-Date -Format 'yyyy-MM-dd'): $Scenario Benchmark Suite Manifest")
    $lines.Add("")
    $lines.Add("- Manifest: $tick$($Manifest.manifestPath)$tick")
    $lines.Add("- System profile: $tick$SystemProfile$tick")
    $lines.Add("- Dry run: $tick$DryRun$tick")
    $lines.Add("")
    $lines.Add("| Run | Layer | Label | Status | Output |")
    $lines.Add("| --- | --- | --- | --- | --- |")
    foreach ($run in $Manifest.runs) {
        $lines.Add("| $($run.id) | $($run.layer) | $($run.label) | $($run.status) | $tick$($run.outputPath)$tick |")
    }

    Add-Content -LiteralPath $summaryPath -Value $lines
}

function Get-ServerWarnings {
    param([object[]]$Servers)

    $warnings = [System.Collections.Generic.List[string]]::new()
    foreach ($server in @($Servers)) {
        if ($null -eq $server -or $server.owned -or $server.existingPids.Count -eq 0) {
            continue
        }

        $expectedRoot = [string]$server.repoRoot
        $commandLines = @($server.existingProcessCommandLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($commandLines.Count -eq 0) {
            $warnings.Add("External server '$($server.label)' on port $($server.port) exposed no command line for repo-root verification.")
            continue
        }

        $matched = $commandLines | Where-Object {
            $_.IndexOf($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        }
        if ($matched.Count -eq 0) {
            $warnings.Add("External server '$($server.label)' on port $($server.port) did not expose expected repo root '$expectedRoot' in its command line.")
        }
    }

    return @($warnings)
}

$resolvedPlanPath = Resolve-RequiredPath -Path $PlanPath -Description "Plan file"
$PlanPath = $resolvedPlanPath
$BaselineRepoRoot = Resolve-RequiredPath -Path $BaselineRepoRoot -Description "Baseline repo root"
$StabilizationRepoRoot = Resolve-RequiredPath -Path $StabilizationRepoRoot -Description "Stabilization repo root"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$worktreeList = Invoke-Git -RepoRoot $StabilizationRepoRoot -Arguments @("worktree", "list", "--porcelain")
$servers = @()
$runs = [System.Collections.Generic.List[object]]::new()

try {
    $baselineServer = if ($IncludeBrowser) {
        if ($OwnDevServers -and -not $DryRun) {
            Start-WebServer -RepoRoot $BaselineRepoRoot -Port $BaselineAppPort -Label $BaselineLabel
        }
        else {
            [ordered]@{
                owned = $false
                pid = $null
                port = $BaselineAppPort
                label = $BaselineLabel
                repoRoot = $BaselineRepoRoot
                note = if ($DryRun -and $OwnDevServers) { "Dry run: would start and own server." } elseif (Test-PortListening -Port $BaselineAppPort) { "Externally managed server detected." } else { "Externally managed server not detected." }
                existingPids = @(Get-PortProcessIds -Port $BaselineAppPort)
                existingProcessCommandLines = @(Get-PortProcessIds -Port $BaselineAppPort | ForEach-Object { Get-ProcessCommandLine -ProcessId $_ })
            }
        }
    }
    else {
        $null
    }

    $stabilizationServer = if ($IncludeBrowser) {
        if ($OwnDevServers -and -not $DryRun) {
            Start-WebServer -RepoRoot $StabilizationRepoRoot -Port $StabilizationAppPort -Label $StabilizationLabel
        }
        else {
            [ordered]@{
                owned = $false
                pid = $null
                port = $StabilizationAppPort
                label = $StabilizationLabel
                repoRoot = $StabilizationRepoRoot
                note = if ($DryRun -and $OwnDevServers) { "Dry run: would start and own server." } elseif (Test-PortListening -Port $StabilizationAppPort) { "Externally managed server detected." } else { "Externally managed server not detected." }
                existingPids = @(Get-PortProcessIds -Port $StabilizationAppPort)
                existingProcessCommandLines = @(Get-PortProcessIds -Port $StabilizationAppPort | ForEach-Object { Get-ProcessCommandLine -ProcessId $_ })
            }
        }
    }
    else {
        $null
    }

    if ($baselineServer) {
        $servers += $baselineServer
    }

    if ($stabilizationServer) {
        $servers += $stabilizationServer
    }

    $runs.Add((New-CliLiveRun -Label $BaselineLabel -RepoRoot $BaselineRepoRoot))
    $runs.Add((New-CliLiveRun -Label $StabilizationLabel -RepoRoot $StabilizationRepoRoot))

    if ($IncludeFake) {
        $runs.Add((New-CliFakeRun -Label $BaselineLabel -RepoRoot $BaselineRepoRoot))
        $runs.Add((New-CliFakeRun -Label $StabilizationLabel -RepoRoot $StabilizationRepoRoot))
    }

    if ($IncludeBrowser) {
        $baselinePortListening = $DryRun -or (Test-PortListening -Port $BaselineAppPort)
        $stabilizationPortListening = $DryRun -or (Test-PortListening -Port $StabilizationAppPort)
        if ($baselinePortListening) {
            $runs.Add((New-BrowserRun -Label $BaselineLabel -AppRepoRoot $BaselineRepoRoot -Url $BaselineUrl -AppPort $BaselineAppPort -DevToolsPort $BaselineDevToolsPort -CacheState "browser-cold" -RunSuffix "cold"))
            if ($IncludeWarmSequence) {
                foreach ($index in 1..3) {
                    $runs.Add((New-BrowserRun -Label $BaselineLabel -AppRepoRoot $BaselineRepoRoot -Url $BaselineUrl -AppPort $BaselineAppPort -DevToolsPort $BaselineDevToolsPort -CacheState "browser-warm" -RunSuffix "warm" -WarmIndex $index))
                }
            }
        }
        else {
            $runs.Add([ordered]@{
                id = "$BaselineLabel-browser-cold"
                layer = "browser"
                label = $BaselineLabel
                repoRoot = $BaselineRepoRoot
                outputPath = $null
                status = "skipped"
                reason = "No server listening on port $BaselineAppPort."
                command = @()
                commandText = ""
            })
        }

        if ($stabilizationPortListening) {
            $runs.Add((New-BrowserRun -Label $StabilizationLabel -AppRepoRoot $StabilizationRepoRoot -Url $StabilizationUrl -AppPort $StabilizationAppPort -DevToolsPort $StabilizationDevToolsPort -CacheState "browser-cold" -RunSuffix "cold"))
            if ($IncludeWarmSequence) {
                foreach ($index in 1..3) {
                    $runs.Add((New-BrowserRun -Label $StabilizationLabel -AppRepoRoot $StabilizationRepoRoot -Url $StabilizationUrl -AppPort $StabilizationAppPort -DevToolsPort $StabilizationDevToolsPort -CacheState "browser-warm" -RunSuffix "warm" -WarmIndex $index))
                }
            }
        }
        else {
            $runs.Add([ordered]@{
                id = "$StabilizationLabel-browser-cold"
                layer = "browser"
                label = $StabilizationLabel
                repoRoot = $StabilizationRepoRoot
                outputPath = $null
                status = "skipped"
                reason = "No server listening on port $StabilizationAppPort."
                command = @()
                commandText = ""
            })
        }
    }

    for ($index = 0; $index -lt $runs.Count; $index++) {
        $run = $runs[$index]
        if ($run.status -eq "pending") {
            Write-Host "[$($index + 1)/$($runs.Count)] $($run.id)"
            $runs[$index] = Invoke-RunRecord -Run $run -TimeoutSeconds $ProcessTimeoutSeconds
        }
        else {
            Write-Host "[$($index + 1)/$($runs.Count)] $($run.id) skipped: $($run.reason)"
        }
    }
}
finally {
    if ($OwnDevServers) {
        foreach ($server in $servers) {
            Stop-OwnedServer -Server $server
        }
    }
}

$manifestPath = Join-Path $OutputDirectory "$Scenario-suite-manifest.json"
$serverWarnings = Get-ServerWarnings -Servers $servers
$manifest = [ordered]@{
    toolVersion = "phase2-benchmark-suite"
    createdAtUtc = [DateTimeOffset]::UtcNow
    manifestPath = $manifestPath
    scenario = $Scenario
    planPath = $PlanPath
    outputDirectory = $OutputDirectory
    dryRun = [bool]$DryRun
    includeBrowser = [bool]$IncludeBrowser
    includeFake = [bool]$IncludeFake
    includeWarmSequence = [bool]$IncludeWarmSequence
    ownDevServers = [bool]$OwnDevServers
    systemProfile = $SystemProfile
    worktreeList = $worktreeList
    baseline = Get-GitState -RepoRoot $BaselineRepoRoot
    stabilization = Get-GitState -RepoRoot $StabilizationRepoRoot
    servers = $servers
    serverWarnings = $serverWarnings
    runs = @($runs)
}

$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-DocsSummary -Manifest $manifest
Write-Host "Wrote manifest: $manifestPath"
