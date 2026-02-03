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

# Check and auto-fix base href for local development
$indexHtml = "src\FFXIVCraftArchitect.Web\wwwroot\index.html"
if (Test-Path $indexHtml) {
    $content = Get-Content $indexHtml -Raw
    
    # Check if base href is NOT set to "/" (GitHub Pages uses "/XIV-Craft-Architect/")
    if ($content -notmatch '<base\s+href\s*=\s*"\/"') {
        Write-Host "Auto-fixing base href for local development..." -ForegroundColor Yellow
        
        # Replace GitHub Pages base href with local dev base href
        $newContent = $content -replace '<base\s+href\s*=\s*"/[^"]*"', '<base href="/"'
        Set-Content -Path $indexHtml -Value $newContent -NoNewline
        
        Write-Host "✓ base href updated to '/' for local development" -ForegroundColor Green
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
