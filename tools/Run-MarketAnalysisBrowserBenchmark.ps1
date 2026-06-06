param(
    [string]$Url = "http://localhost:5000",
    [int]$DevToolsPort = 9223,
    [string]$ChromePath = "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    [string]$ChromeProfile = "C:\tmp\chrome-market-analysis-benchmark",
    [int]$ProcessTimeoutSeconds = 180,
    [switch]$NoBuild,
    [switch]$KeepChrome,
    [switch]$CleanBuildArtifacts,
    [string]$HarnessCommandLine = "",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$HarnessArgs
)

$ErrorActionPreference = "Stop"

function Quote-ProcessArgument {
    param([string]$Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Test-PortListening {
    param([int]$Port)

    $pattern = "[:.]$Port\s+.*LISTENING"
    return [bool](netstat -ano | Select-String -Pattern $pattern)
}

function Wait-PortListening {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortListening -Port $Port) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for port $Port to listen."
}

function Invoke-DotnetProcess {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds
    )

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = "dotnet"
    $process.StartInfo.Arguments = ($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join " "
    $process.StartInfo.WorkingDirectory = (Resolve-Path ".").Path
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true

    [void]$process.Start()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            $process.Kill()
        }
        catch {
            Write-Warning "Failed to kill timed-out dotnet process $($process.Id): $($_.Exception.Message)"
        }

        throw "dotnet process timed out after $TimeoutSeconds seconds."
    }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }

    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Warning $stderr.TrimEnd()
    }

    if ($process.ExitCode -ne 0) {
        throw "dotnet process exited with code $($process.ExitCode)."
    }
}

function Split-CommandLine {
    param([string]$CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return @()
    }

    $matches = [regex]::Matches($CommandLine, '"([^"]*)"|''([^'']*)''|(\S+)')
    $values = @()
    foreach ($match in $matches) {
        if ($match.Groups[1].Success) {
            $values += $match.Groups[1].Value
        }
        elseif ($match.Groups[2].Success) {
            $values += $match.Groups[2].Value
        }
        else {
            $values += $match.Groups[3].Value
        }
    }

    return $values
}

$repoRoot = (Resolve-Path ".").Path
$projectPath = Join-Path $repoRoot "tools\MarketAnalysisBrowserBenchmark\MarketAnalysisBrowserBenchmark.csproj"
$dllPath = Join-Path $repoRoot "tools\MarketAnalysisBrowserBenchmark\bin\Debug\net8.0\MarketAnalysisBrowserBenchmark.dll"
$devToolsEndpoint = "http://127.0.0.1:$DevToolsPort"
$chromeProcess = $null
$HarnessArgs = @($HarnessArgs) + @(Split-CommandLine -CommandLine $HarnessCommandLine)
$processOnly = $HarnessArgs -contains "--process-only"

try {
    if (-not $NoBuild) {
        & dotnet build $projectPath --no-restore
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "No-restore build failed; retrying with restore."
            & dotnet build $projectPath
            if ($LASTEXITCODE -ne 0) {
                throw "Harness build failed."
            }
        }
    }

    if (-not (Test-Path $dllPath)) {
        throw "Harness DLL not found at $dllPath. Run without -NoBuild first."
    }

    if (-not $processOnly -and -not (Test-PortListening -Port $DevToolsPort)) {
        if (-not (Test-Path $ChromePath)) {
            throw "Chrome was not found at $ChromePath."
        }

        if (Test-Path $ChromeProfile) {
            Remove-Item -LiteralPath $ChromeProfile -Recurse -Force
        }

        New-Item -ItemType Directory -Path $ChromeProfile | Out-Null
        $chromeArgs = @(
            "--headless=new",
            "--remote-debugging-port=$DevToolsPort",
            "--user-data-dir=$ChromeProfile",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            $Url
        )
        $chromeProcess = Start-Process -FilePath $ChromePath -ArgumentList $chromeArgs -WindowStyle Hidden -PassThru
        Wait-PortListening -Port $DevToolsPort
    }

    $arguments = @($dllPath)
    if (-not $processOnly) {
        $arguments += @(
            "--url",
            $Url,
            "--devtools",
            $devToolsEndpoint
        )
    }

    $arguments += $HarnessArgs

    Invoke-DotnetProcess -Arguments $arguments -TimeoutSeconds $ProcessTimeoutSeconds
}
finally {
    if (-not $KeepChrome -and $chromeProcess -ne $null) {
        Stop-Process -Id $chromeProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }

    Get-Process MarketAnalysisBrowserBenchmark -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if ($CleanBuildArtifacts) {
        foreach ($relative in @("tools\MarketAnalysisBrowserBenchmark\bin", "tools\MarketAnalysisBrowserBenchmark\obj")) {
            $target = Resolve-Path $relative -ErrorAction SilentlyContinue
            if ($null -ne $target -and $target.Path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $target.Path -Recurse -Force
            }
        }
    }
}
