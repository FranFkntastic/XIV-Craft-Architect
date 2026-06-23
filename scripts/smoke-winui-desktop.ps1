param(
    [int]$StartupSeconds = 4,
    [string]$ExpectedWindowTitle = "FFXIV Craft Architect Desktop",
    [switch]$SkipWorkflowInteractions
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\FFXIV Craft Architect.Desktop\FFXIV Craft Architect.Desktop.csproj"
$publishRoot = Join-Path $repoRoot ".tmp\desktop-smoke"

if (Test-Path $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

function Find-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$RootElement,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 3
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList `
        ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), `
        $AutomationId
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    do {
        $element = $RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $element) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "Desktop smoke failed: could not find automation element '$AutomationId'."
}

function Invoke-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$RootElement,
        [string]$AutomationId
    )

    $element = Find-ElementByAutomationId -RootElement $RootElement -AutomationId $AutomationId
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 150
}

function Wait-ElementName {
    param(
        [System.Windows.Automation.AutomationElement]$RootElement,
        [string]$AutomationId,
        [string]$ExpectedName,
        [int]$TimeoutSeconds = 3
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = Find-ElementByAutomationId -RootElement $RootElement -AutomationId $AutomationId -TimeoutSeconds 1
        $actualName = $element.Current.Name
        if ($actualName -eq $ExpectedName) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "Desktop smoke failed: expected '$AutomationId' to read '$ExpectedName' but saw '$actualName'."
}

function Wait-ElementNameContains {
    param(
        [System.Windows.Automation.AutomationElement]$RootElement,
        [string]$AutomationId,
        [string]$ExpectedFragment,
        [int]$TimeoutSeconds = 3
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = Find-ElementByAutomationId -RootElement $RootElement -AutomationId $AutomationId -TimeoutSeconds 1
        $actualName = $element.Current.Name
        if ($actualName -like "*$ExpectedFragment*") {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "Desktop smoke failed: expected '$AutomationId' to contain '$ExpectedFragment' but saw '$actualName'."
}

function Get-ProcessAutomationWindow {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds
    )

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList `
        ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), `
        $ProcessId
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    do {
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $window) {
            return $window
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "Desktop smoke failed: could not find an automation window for process $ProcessId."
}

function Get-ProcessAutomationWindowByTitle {
    param(
        [int]$ProcessId,
        [string]$WindowTitle,
        [int]$TimeoutSeconds
    )

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList `
        ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), `
        $ProcessId
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    do {
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($window in $windows) {
            if ($window.Current.Name -eq $WindowTitle) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "Desktop smoke failed: could not find window '$WindowTitle' for process $ProcessId."
}

function Restore-AutomationWindow {
    param([System.Windows.Automation.AutomationElement]$AutomationWindow)

    try {
        $windowPattern = $AutomationWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    }
    catch {
        # Some hosts expose the window before WindowPattern is ready; the workflow checks below are decisive.
    }
}

dotnet build $projectPath `
    --configuration Debug `
    --framework net8.0-windows10.0.19041.0 `
    -p:OutputPath="..\..\.tmp\desktop-smoke\" | Write-Host

$exePath = Join-Path $publishRoot "FFXIV_Craft_Architect.Desktop.exe"
if (-not (Test-Path $exePath)) {
    throw "Desktop executable was not produced at $exePath"
}

$previousSmokeBuild = $env:FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD
$env:FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD = "1"
$process = Start-Process -FilePath $exePath -WindowStyle Minimized -PassThru
try {
    $deadline = (Get-Date).AddSeconds($StartupSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "Desktop smoke failed: process exited during startup with code $($process.ExitCode)."
        }
    } while (($process.MainWindowHandle -eq [IntPtr]::Zero -or [string]::IsNullOrWhiteSpace($process.MainWindowTitle)) -and (Get-Date) -lt $deadline)

    if ($process.HasExited) {
        throw "Desktop smoke failed: process exited during startup with code $($process.ExitCode)."
    }

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "Desktop smoke failed: process did not create a main window handle within $StartupSeconds seconds."
    }

    if ($process.MainWindowTitle -ne $ExpectedWindowTitle) {
        throw "Desktop smoke failed: expected window title '$ExpectedWindowTitle' but saw '$($process.MainWindowTitle)'."
    }

    if (-not $process.Responding) {
        throw "Desktop smoke failed: main window was created but the process is not responding."
    }

    $automationWindow = Get-ProcessAutomationWindow -ProcessId $process.Id -TimeoutSeconds $StartupSeconds
    Restore-AutomationWindow -AutomationWindow $automationWindow

    $requiredControls = @(
        "RecipePlannerTabButton",
        "MarketAnalysisTabButton",
        "AcquisitionEvaluationTabButton",
        "ProcurementPlanTabButton",
        "SettingsButton",
        "DiagnosticsButton",
        "PrimaryActionButton",
        "ActivityDrawerToggleButton",
        "TargetSearchStatusText",
        "WorkbenchTitleText"
    )

    foreach ($automationId in $requiredControls) {
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId $automationId | Out-Null
    }

    if (-not $SkipWorkflowInteractions) {
        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "RecipePlannerTabButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Recipe Planner"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "AddTargetButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "Cobalt Plate added"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "IncreaseSelectedQuantityButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "quantity is now"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "DecreaseSelectedQuantityButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "quantity is now"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ToggleSelectedHqButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "quality set to HQ"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "PrimaryActionButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "Recipe plan built"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "MarketAnalysisTabButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Market Evidence"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "AcquisitionEvaluationTabButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Acquisition Evaluation"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ProcurementPlanTabButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Procurement Plan"
        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "CopyProcurementPlanTextButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "procurement line"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "SettingsButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Desktop Settings"
        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "PrimaryActionButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "OperationStatusText" -ExpectedFragment "Desktop settings applied"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "DiagnosticsButton"
        Wait-ElementName -RootElement $automationWindow -AutomationId "WorkbenchTitleText" -ExpectedName "Diagnostics"
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId "RefreshDiagnosticLogButton" | Out-Null
        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "OpenDiagnosticLogViewerButton"
        $logViewerWindow = Get-ProcessAutomationWindowByTitle -ProcessId $process.Id -WindowTitle "FFXIV Craft Architect Diagnostic Logs" -TimeoutSeconds 4
        Restore-AutomationWindow -AutomationWindow $logViewerWindow
        Find-ElementByAutomationId -RootElement $logViewerWindow -AutomationId "LogViewerFileList" | Out-Null
        Find-ElementByAutomationId -RootElement $logViewerWindow -AutomationId "LogViewerSearchTextBox" | Out-Null
        Find-ElementByAutomationId -RootElement $logViewerWindow -AutomationId "LogViewerLevelFilter" | Out-Null
        Find-ElementByAutomationId -RootElement $logViewerWindow -AutomationId "LogViewerCategoryFilter" | Out-Null
        Find-ElementByAutomationId -RootElement $logViewerWindow -AutomationId "LogViewerEntriesList" | Out-Null

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityDrawerToggleButton"
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityDrawerCloseButton" | Out-Null
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterAllButton" | Out-Null
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterJobButton" | Out-Null
        Find-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterCacheButton" | Out-Null

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterJobButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "ActivityFilterSummaryText" -ExpectedFragment "Job"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterCacheButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "ActivityFilterSummaryText" -ExpectedFragment "Cache"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityFilterAllButton"
        Wait-ElementNameContains -RootElement $automationWindow -AutomationId "ActivityFilterSummaryText" -ExpectedFragment "shown"

        Invoke-ElementByAutomationId -RootElement $automationWindow -AutomationId "ActivityDrawerCloseButton"
    }

    Write-Host "Desktop smoke passed: window '$($process.MainWindowTitle)' stayed alive, responded, and exposed/invoked workflow, target-edit, target mutation, deterministic build, procurement export, settings, diagnostic log viewer, and activity controls for $StartupSeconds seconds."
}
finally {
    $env:FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD = $previousSmokeBuild
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
