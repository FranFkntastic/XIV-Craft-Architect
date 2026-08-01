$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$dependencyRoot = Join-Path $repositoryRoot "artifacts\dependencies\Franthropy"
$revisionPath = Join-Path $repositoryRoot "eng\Franthropy.version"
$revision = (Get-Content -LiteralPath $revisionPath -Raw).Trim()

if ($revision -notmatch '^[0-9a-f]{40}$') {
    throw "eng/Franthropy.version must contain one exact lowercase commit SHA."
}

$mutex = [System.Threading.Mutex]::new($false, 'Local\FFXIVCraftArchitect.FranthropyDependency')
$lockTaken = $false
try {
    try {
        $lockTaken = $mutex.WaitOne([TimeSpan]::FromMinutes(2))
    }
    catch [System.Threading.AbandonedMutexException] {
        $lockTaken = $true
    }
    if (-not $lockTaken) {
        throw "Timed out waiting for another Franthropy dependency resolution to finish."
    }

    $gitDirectory = Join-Path $dependencyRoot ".git"
    if (Test-Path -LiteralPath $dependencyRoot -PathType Leaf) {
        throw "The Franthropy dependency path is a file: $dependencyRoot"
    }

    if (-not (Test-Path -LiteralPath $gitDirectory)) {
        if (Test-Path -LiteralPath $dependencyRoot) {
            $existing = @(Get-ChildItem -LiteralPath $dependencyRoot -Force)
            if ($existing.Count -ne 0) {
                throw "The Franthropy dependency path exists but is not an owned Git checkout: $dependencyRoot"
            }
        }
        else {
            New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
        }

        & git -C $dependencyRoot init --quiet
        if ($LASTEXITCODE -ne 0) { throw "Could not initialize the Franthropy dependency checkout." }
        & git -C $dependencyRoot remote add origin https://github.com/FranFkntastic/Franthropy.git
        if ($LASTEXITCODE -ne 0) { throw "Could not configure the Franthropy dependency remote." }
    }

    $origin = (& git -C $dependencyRoot remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $origin -notin @(
            'https://github.com/FranFkntastic/Franthropy.git',
            'https://github.com/FranFkntastic/Franthropy')) {
        throw "The dependency checkout does not point to the expected Franthropy repository."
    }

    $dirty = & git -C $dependencyRoot status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect the Franthropy dependency checkout." }
    if ($dirty) { throw "The Franthropy dependency checkout is dirty; refusing to overwrite it." }

    & git -C $dependencyRoot fetch --quiet --depth=1 origin $revision
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch pinned Franthropy revision $revision." }
    & git -C $dependencyRoot checkout --quiet --detach $revision
    if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned Franthropy revision $revision." }

    $actual = (& git -C $dependencyRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) {
        throw "Franthropy dependency checkout did not resolve to the pinned revision."
    }

    $dependencyRoot
}
finally {
    if ($lockTaken) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
