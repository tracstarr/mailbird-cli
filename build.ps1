<#
.SYNOPSIS
    Compile mailbird-cli and stage it into the bundled skill; optionally package a .skill file.

.DESCRIPTION
    Publishes src\mailbird-cli into .claude\skills\mailbird\bin so the skill ships a ready-to-run
    binary.

    Default: a SELF-CONTAINED single-file exe (win-x64) — one mailbird-cli.exe with the .NET runtime
    and native SQLite bundled in, so the target machine needs nothing installed (~30-40 MB).

    -FrameworkDependent: a small build (~2 MB, several dlls) that instead requires the .NET 8 runtime.

    -Package also zips the skill into dist\mailbird.skill.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Package
.EXAMPLE
    .\build.ps1 -FrameworkDependent -Package
#>
[CmdletBinding()]
param(
    [switch] $FrameworkDependent,
    [switch] $Package
)
$ErrorActionPreference = 'Stop'

$root  = $PSScriptRoot
$proj  = Join-Path $root 'src\mailbird-cli'
$skill = Join-Path $root '.claude\skills\mailbird'
$bin   = Join-Path $skill 'bin'

if (Test-Path -LiteralPath $bin) { Remove-Item -LiteralPath $bin -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '-p:DebugType=none', '-o', $bin)
if ($FrameworkDependent) {
    Write-Host 'Publishing mailbird-cli (framework-dependent, needs .NET 8 runtime)…' -ForegroundColor DarkGray
    & dotnet publish $proj @common --self-contained false | Out-Null
}
else {
    Write-Host 'Publishing mailbird-cli (self-contained single-file)…' -ForegroundColor DarkGray
    & dotnet publish $proj @common --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true | Out-Null
}
Get-ChildItem $bin -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $bin 'mailbird-cli.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'build failed: mailbird-cli.exe not produced' }
$mb = [math]::Round(((Get-ChildItem $bin -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "Built $exe  (bin: $mb MB)" -ForegroundColor Green

if ($Package) {
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist 'mailbird.zip'
    $skf = Join-Path $dist 'mailbird.skill'
    Remove-Item $zip, $skf -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $skill -DestinationPath $zip
    Rename-Item $zip $skf
    Write-Host "Packaged $skf ($([math]::Round((Get-Item $skf).Length/1MB,1)) MB)" -ForegroundColor Green
}
