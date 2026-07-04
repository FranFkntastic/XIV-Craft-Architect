$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

throw @"
GitHub Pages is no longer an app deployment target.

Current app deployments:
  main      -> https://xivcraftarchitect.com
  local-dev -> https://dev.xivcraftarchitect.com

Push main/local-dev to trigger:
  .github/workflows/deploy-vps-web.yml

The remaining GitHub Pages workflow only publishes the moved notice:
  .github/workflows/deploy-web.yml
"@
