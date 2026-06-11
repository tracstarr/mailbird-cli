<#
.SYNOPSIS
    Install the bundled "mailbird" skill (SKILL.md + compiled mailbird-cli) into your per-user
    Claude skills folder.

.DESCRIPTION
    Ensures the CLI is built (runs build.ps1 if bin\mailbird-cli.exe is missing), then copies
    .claude\skills\mailbird to $HOME\.claude\skills\mailbird. The skill ships a self-contained
    binary — nothing else to install. Re-run with -Force to overwrite an existing install.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $HOME '.claude\skills\mailbird'),
    [switch] $Force
)
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot '.claude\skills\mailbird'
if (-not (Test-Path -LiteralPath (Join-Path $src 'SKILL.md'))) { throw "Skill source not found at $src" }

# Make sure the binary is compiled and staged before copying.
if (-not (Test-Path -LiteralPath (Join-Path $src 'bin\mailbird-cli.exe'))) {
    Write-Host 'mailbird-cli not built yet — building…' -ForegroundColor DarkGray
    & (Join-Path $PSScriptRoot 'build.ps1')
}

if (Test-Path -LiteralPath $Destination) {
    if (-not $Force) { throw "Destination already exists: $Destination  (re-run with -Force to overwrite)" }
    Remove-Item -LiteralPath $Destination -Recurse -Force
}

Copy-Item -LiteralPath $src -Destination $Destination -Recurse -Force

Write-Host "Installed 'mailbird' skill to $Destination" -ForegroundColor Green
Write-Host "CLI: $Destination\bin\mailbird-cli.exe   (self-contained — no runtime needed)"
Write-Host "Open a new Claude Code session for the skill to be discovered."
