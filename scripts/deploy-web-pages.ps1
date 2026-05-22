$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$branch = (git branch --show-current).Trim()
if ($branch -ne "main" -and $branch -ne "master") {
    throw "GitHub Pages deployment runs from main/master. Current branch is '$branch'. Switch to main/master or merge this work there before deploying."
}

$status = git status --porcelain
if (-not [string]::IsNullOrWhiteSpace($status)) {
    throw "Working tree has uncommitted changes. Commit or stash them before deploying."
}

Write-Host "Building solution in Release..."
dotnet build "FFXIV Craft Architect.sln" -c Release

Write-Host "Running tests in Release..."
dotnet test "FFXIV Craft Architect.sln" -c Release --no-build

Write-Host "Pushing '$branch' to origin. GitHub Actions will publish the web app to GitHub Pages..."
git push origin $branch

Write-Host ""
Write-Host "Deployment workflow:"
Write-Host "https://github.com/FranFkntastic/XIV-Craft-Architect/actions/workflows/deploy-web.yml"
Write-Host ""
Write-Host "Live site:"
Write-Host "https://franfkntastic.github.io/XIV-Craft-Architect"
