#requires -Version 7.2
param([string]$PackageDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (-not $PackageDirectory) { $PackageDirectory=Join-Path $root 'artifacts/AutoExtractor-win-x64' }
$package=[IO.Path]::GetFullPath($PackageDirectory)
$artifacts=Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$trace=Join-Path $artifacts ('portable-host-'+[Guid]::NewGuid().ToString('N')+'.txt')
$preview=Join-Path $artifacts ('portable-preview-'+[Guid]::NewGuid().ToString('N')+'.png')
$saved=@{}
$variables=@('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','COREHOST_TRACE','COREHOST_TRACEFILE')
foreach($name in $variables) { $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
try {
    $env:DOTNET_ROOT=Join-Path $artifacts 'no-installed-runtime'
    $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP='0'
    $env:COREHOST_TRACE='1'
    $env:COREHOST_TRACEFILE=$trace
    $process=Start-Process -FilePath (Join-Path $package 'AutoExtractor.exe') -ArgumentList '--render-preview',('"'+$preview+'"') -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Portable startup timed out' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $preview)) { throw 'Portable startup failed' }
    $content=Get-Content -LiteralPath $trace -Raw
    $expected="Loaded library from " + (Join-Path $package 'coreclr.dll')
    if (-not $content.Contains($expected)) { throw 'Runtime was not loaded from the portable package' }
    $manifest=Get-Content -LiteralPath (Join-Path $package 'tools/7zip/manifest.json') -Raw | ConvertFrom-Json
    foreach($file in $manifest.files.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $package ('tools/7zip/'+$file.Name))).Hash -ne $file.Value) { throw 'Portable engine checksum mismatch' }
    }
    Write-Output 'PASS portable startup with system runtime lookup disabled; local CoreCLR and bundled engine hashes verified.'
    Write-Output ('Preview: '+$preview)
} finally {
    foreach($name in $variables) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
}
