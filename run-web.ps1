# FFXIV Craft Architect - Web Development Server
# Starts the Blazor WASM app with dotnet watch for local development

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "FFXIV Craft Architect - Web Dev Server" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Check if dotnet is available
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: dotnet CLI not found!" -ForegroundColor Red
    Write-Host "Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download"
    exit 1
}

# Display dotnet version
Write-Host ".NET Version: " -NoNewline
Write-Host (dotnet --version) -ForegroundColor Green
Write-Host ""

# Check base href in index.html
$indexHtml = "src\FFXIVCraftArchitect.Web\wwwroot\index.html"
if (Test-Path $indexHtml) {
    $content = Get-Content $indexHtml -Raw
    
    # Check if base href is set to "/" (for local dev)
    if ($content -notmatch '<base\s+href\s*=\s*"\/"') {
        Write-Host "WARNING: base href is NOT set to '/' in index.html" -ForegroundColor Yellow
        Write-Host "For local development, ensure: <base href=""/" />" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "To fix for local dev, run:" -ForegroundColor Gray
        Write-Host "  (Get-Content '$indexHtml') -replace 'base href\s*=\s*""/XIV-Craft-Architect/""', 'base href=""/""' | Set-Content '$indexHtml'" -ForegroundColor Gray
        Write-Host ""
        
        $continue = Read-Host "Continue anyway? (Y/N)"
        if ($continue -notmatch '^[Yy]') {
            exit 0
        }
        Write-Host ""
    }
}
else {
    Write-Host "WARNING: index.html not found at expected path" -ForegroundColor Yellow
}

$projectPath = "src\FFXIVCraftArchitect.Web\FFXIVCraftArchitect.Web.csproj"

# Verify project file exists
if (!(Test-Path $projectPath)) {
    Write-Host "ERROR: Project file not found: $projectPath" -ForegroundColor Red
    exit 1
}

# Check if port 5000 is already in use
$portInUse = netstat -ano | findstr :5000
if ($portInUse) {
    Write-Host "WARNING: Port 5000 is already in use!" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Details:"
    $portInUse | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    
    $killIt = Read-Host "Kill the process using port 5000? (Y/N)"
    if ($killIt -match '^[Yy]') {
        # Extract PID and kill it
        $portInUse -match '(\d+)$' | Out-Null
        $pidToKill = $matches[1]
        if ($pidToKill) {
            Write-Host "Killing process $pidToKill..." -ForegroundColor Yellow
            taskkill /PID $pidToKill /F | Out-Null
            Start-Sleep -Seconds 1
            Write-Host "Process killed. Starting fresh..." -ForegroundColor Green
            Write-Host ""
        }
    }
}

Write-Host "Starting dotnet watch..." -ForegroundColor Green
Write-Host "Project: $projectPath"
Write-Host "URL:     http://localhost:5000" -ForegroundColor Cyan
Write-Host ""
Write-Host "Press Ctrl+C to stop the server" -ForegroundColor Yellow
Write-Host "============================================"
Write-Host ""

# Run dotnet watch with hot reload disabled (more stable for WASM)
dotnet watch --project $projectPath --no-hot-reload

# Check exit code
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    if ($LASTEXITCODE -eq -532462766 -or $LASTEXITCODE -eq 0xE0434352) {
        Write-Host "ERROR: dotnet watch crashed with unhandled exception (-532462766)" -ForegroundColor Red
        Write-Host ""
        Write-Host "Most likely causes:"
        Write-Host "1. Port 5000 is already in use by another process"
        Write-Host "2. Corrupted build artifacts"
        Write-Host ""
        Write-Host "Try these fixes:"
        Write-Host "  • Run: netstat -ano | findstr :5000  (then: taskkill /PID <PID> /F)"
        Write-Host "  • Run: dotnet clean $projectPath"
        Write-Host "  • Run: dotnet restore $projectPath"
        Write-Host "  • Restart your terminal/VS Code"
    }
    else {
        Write-Host "ERROR: dotnet watch failed! (Exit code: $LASTEXITCODE)" -ForegroundColor Red
        Write-Host ""
        Write-Host "Common fixes:"
        Write-Host "1. Run: dotnet restore $projectPath"
        Write-Host "2. Check that port 5000 is not in use: netstat -ano | findstr :5000"
        Write-Host "3. Run: dotnet clean $projectPath"
    }
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}
