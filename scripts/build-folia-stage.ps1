param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$foliaRoot = Join-Path $repoRoot 'third_party\folia-major'
$adapterRoot = Join-Path $repoRoot 'src\Vesper.FoliaStage'

if (-not (Test-Path (Join-Path $foliaRoot 'package.json'))) {
    throw "Missing third_party/folia-major submodule. Run: git submodule update --init --recursive"
}

Copy-Item (Join-Path $adapterRoot 'vesper-stage.html') (Join-Path $foliaRoot 'vesper-stage.html') -Force
Copy-Item (Join-Path $adapterRoot 'vite.vesper.config.ts') (Join-Path $foliaRoot 'vite.vesper.config.ts') -Force
Copy-Item (Join-Path $adapterRoot 'src\vesperStage.tsx') (Join-Path $foliaRoot 'src\vesperStage.tsx') -Force

Push-Location $foliaRoot
try {
    if (-not $SkipInstall -and -not (Test-Path (Join-Path $foliaRoot 'node_modules'))) {
        pnpm install --ignore-scripts
    }
    npx vite build --config vite.vesper.config.ts
}
finally {
    Pop-Location
}
