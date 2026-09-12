#requires -Version 7.2
param([string]$DotNet = 'dotnet', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $manifest = Get-Content -LiteralPath (Join-Path $root 'vendor/7zip/manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $root ('vendor/7zip/' + $file.Name)) -Algorithm SHA256).Hash -ne $file.Value) { throw ('Bundled engine checksum mismatch: ' + $file.Name) }
    }
    & $DotNet build AutoExtractor.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        & $DotNet run --project tests/AutoExtractor.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
        & $DotNet run --project tests/AutoExtractor.App.SmokeTests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Desktop smoke tests failed' }
    }
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    $publishPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'AutoExtractor-win-x64'))
    if (-not $publishPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid artifact directory' }
    if (Test-Path -LiteralPath $publishPath) { Remove-Item -LiteralPath $publishPath -Recurse -Force }
    & $DotNet publish src/AutoExtractor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -o $publishPath --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath README.md,LICENSE,THIRD_PARTY_NOTICES.md -Destination $publishPath
    New-Item -ItemType Directory -Force (Join-Path $publishPath 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs/images') -Destination (Join-Path $publishPath 'docs/images') -Recurse
    if (Test-Path -LiteralPath (Join-Path $root 'docs/VERIFICATION.md')) { Copy-Item -LiteralPath (Join-Path $root 'docs/VERIFICATION.md') -Destination (Join-Path $publishPath 'docs/VERIFICATION.md') }
    # The runtime distribution notices are included explicitly with the portable output.
    $noticeRoot = Join-Path $root 'vendor/dotnet'
    if (Test-Path -LiteralPath $noticeRoot) { Copy-Item -LiteralPath $noticeRoot -Destination (Join-Path $publishPath 'runtime-notices') -Recurse }
    & (Join-Path $PSScriptRoot 'verify-portable.ps1') -PackageDirectory $publishPath
    $zipPath = Join-Path $artifactRoot 'AutoExtractor-v0.1.0-win-x64.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    & (Join-Path $root 'vendor/7zip/7z.exe') a -tzip '-mx=7' $zipPath $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Portable ZIP creation failed' }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    ($hash + '  ' + [IO.Path]::GetFileName($zipPath)) | Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Output ('Portable application: ' + $publishPath)
    Write-Output ('Release archive: ' + $zipPath)
} finally { Pop-Location }
