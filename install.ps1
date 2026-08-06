<#
.SYNOPSIS
    Install the bundled skills (mailbird + email-style-analysis) into your per-user Claude Code
    and/or Codex skills folders.

.DESCRIPTION
    Ensures the CLI is built (runs build.ps1 if bin\mailbird-cli.exe is missing), then copies every
    skill under .claude\skills to each target's skills directory:

        Claude  ->  $HOME\.claude\skills\<name>          (or -Destination)
        Codex   ->  $env:CODEX_HOME\skills\<name>        (defaults to $HOME\.codex\skills)

    The mailbird skill ships a self-contained binary — nothing else to install. Re-run with -Force
    to overwrite an existing install.

.EXAMPLE
    .\install.ps1 -Force
.EXAMPLE
    .\install.ps1 -Target Claude -Force
.EXAMPLE
    .\install.ps1 -Skill mailbird -Force
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Claude', 'Codex')]
    [string]   $Target = 'All',
    [string[]] $Skill,                # default: every skill under .claude\skills
    [string]   $Destination,          # override the Claude skills root (back-compat)
    [switch]   $Force
)
$ErrorActionPreference = 'Stop'

# Normalize a path for comparison without requiring it to exist yet.
function Resolve-PathSafe([string] $p) {
    return [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/').ToLowerInvariant()
}

$srcRoot = Join-Path $PSScriptRoot '.claude\skills'
if (-not (Test-Path -LiteralPath $srcRoot)) { throw "Skill source not found at $srcRoot" }

# Which skills to install.
$skills = if ($Skill) {
    $Skill | ForEach-Object {
        $p = Join-Path $srcRoot $_
        if (-not (Test-Path -LiteralPath (Join-Path $p 'SKILL.md'))) { throw "No such skill: $_ (looked in $p)" }
        Get-Item -LiteralPath $p
    }
} else {
    Get-ChildItem -LiteralPath $srcRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') }
}
if (-not $skills) { throw "No skills found under $srcRoot" }

# Make sure the binary is compiled and staged before copying.
$cliSrc = Join-Path $srcRoot 'mailbird\bin\mailbird-cli.exe'
if (($skills.Name -contains 'mailbird') -and -not (Test-Path -LiteralPath $cliSrc)) {
    Write-Host 'mailbird-cli not built yet — building…' -ForegroundColor DarkGray
    & (Join-Path $PSScriptRoot 'build.ps1')
}

# Resolve target skill roots.
$roots = [ordered]@{}
if ($Target -in 'All', 'Claude') {
    # -Destination historically pointed at the mailbird skill folder itself; accept either that or a root.
    $claudeRoot = if ($Destination) {
        if ((Split-Path -Leaf $Destination) -eq 'mailbird') { Split-Path -Parent $Destination } else { $Destination }
    } else { Join-Path $HOME '.claude\skills' }
    $roots['Claude'] = $claudeRoot
}
if ($Target -in 'All', 'Codex') {
    # Always install into the standard ~/.codex. If CODEX_HOME points somewhere else (e.g. Orca runs
    # Codex out of its own runtime home), that's a second real home — install there too.
    $roots['Codex'] = Join-Path (Join-Path $HOME '.codex') 'skills'
    if ($env:CODEX_HOME) {
        $alt = Join-Path $env:CODEX_HOME 'skills'
        if ((Resolve-PathSafe $alt) -ne (Resolve-PathSafe $roots['Codex'])) { $roots['Codex (CODEX_HOME)'] = $alt }
    }
}

foreach ($t in $roots.Keys) {
    $root = $roots[$t]
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    foreach ($s in $skills) {
        $dest = Join-Path $root $s.Name
        if (Test-Path -LiteralPath $dest) {
            if (-not $Force) { throw "Destination already exists: $dest  (re-run with -Force to overwrite)" }
            Remove-Item -LiteralPath $dest -Recurse -Force
        }
        Copy-Item -LiteralPath $s.FullName -Destination $dest -Recurse -Force
        Write-Host "[$t] installed '$($s.Name)' -> $dest" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'mailbird CLI is self-contained — no .NET runtime needed.'
Write-Host 'Start a NEW Claude Code / Codex session for the updated skills to be picked up.'
