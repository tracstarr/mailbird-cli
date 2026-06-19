<#
.SYNOPSIS
  Extract a time/topic-spread sample of the user's own SENT emails into a corpus
  (plus optional slice files) for writing-style analysis. Read-only.

.DESCRIPTION
  Uses the bundled mailbird CLI (read-only against Mailbird's local Store.db) to:
    1. list a wide window of recent SENT messages for one account,
    2. stride-sample across that window (spreads the sample over time/topics, not just
       the latest thread),
    3. read each, strip the header + any quoted reply text, keep only the authored body,
    4. write all kept records to one corpus file (and, optionally, N slice files for
       parallel analysis).

  It never writes to the mail database. The corpus contains personal mail — treat it as
  private and delete it when the analysis is done.

.EXAMPLE
  .\extract-sent-corpus.ps1 -Account 1 -Target 200 -Slices 4
#>
param(
  [int]$Account = 1,            # AccountId (see: mailbird-cli accounts)
  [string]$Folder = "Sent",    # sent-mail folder name for that account (see: folders <id>)
  [int]$Target = 200,          # approx number of authored emails to keep
  [int]$Window = 1000,         # how many recent sent messages to sample ACROSS (time spread)
  [int]$Slices = 4,            # also write this many slice files (0 = corpus only)
  [string]$OutDir = $env:TEMP, # where to write the corpus/slices
  [string]$Cli                 # path to mailbird-cli.exe (auto-detected if omitted)
)
$ErrorActionPreference = "Stop"

# --- locate the CLI (sibling mailbird skill, or installed user skill) ---
if (-not $Cli) {
  $candidates = @(
    (Join-Path $PSScriptRoot "..\..\mailbird\bin\mailbird-cli.exe"),
    (Join-Path $HOME ".claude\skills\mailbird\bin\mailbird-cli.exe")
  )
  $Cli = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Cli -or -not (Test-Path $Cli)) {
  throw "mailbird-cli.exe not found. Pass -Cli <path> (it ships in the sibling 'mailbird' skill's bin/)."
}

$corpus = Join-Path $OutDir "style_corpus.txt"

# --- 1) wide recent window for time/topic spread ---
$rows = & $Cli list --folder $Folder --account $Account --limit $Window --json | Out-String | ConvertFrom-Json
if (-not $rows -or $rows.Count -eq 0) { throw "No messages in folder '$Folder' for account $Account." }

# --- 2) stride-sample to ~Target across the window ---
$stride = [Math]::Max(1, [int]([Math]::Floor($rows.Count / [Math]::Max(1, $Target))))
$picked = @()
for ($i = 0; $i -lt $rows.Count -and $picked.Count -lt $Target; $i += $stride) { $picked += $rows[$i] }

# --- 3) read each; strip header + quoted text; keep authored body ---
$records = New-Object System.Collections.Generic.List[string]
$recipients = @{}
foreach ($r in $picked) {
  $raw = & $Cli read $r.Id | Out-String
  # Body is everything after the 60-dash separator line the CLI prints under the headers.
  $idx = $raw.IndexOf("`n---")
  if ($idx -lt 0) { continue }
  $sep = $raw.IndexOf("`n", $idx + 1)
  if ($sep -lt 0) { continue }
  $body = $raw.Substring($sep + 1).Trim()
  if ($body.Length -lt 15) { continue }   # skip empties / one-word forwards
  $to = ""; $subj = ""
  foreach ($line in ($raw -split "`r?`n")) {
    if ($line -match "^To\s*:\s*(.+)$") { $to = $Matches[1].Trim() }
    elseif ($line -match "^Subject\s*:\s*(.+)$") { $subj = $Matches[1].Trim() }
  }
  if ($to) { $recipients[$to] = 1 }
  $records.Add("### [$($r.Id)] $($r.date) | To: $to | Subj: $subj`r`n$body`r`n<<<END>>>")
}
if ($records.Count -eq 0) { throw "Read $($picked.Count) messages but kept 0 bodies — check the folder/account." }

Set-Content -LiteralPath $corpus -Value ($records -join "`r`n") -Encoding UTF8

# --- 4) optional slice files (record-aligned) ---
$slicePaths = @()
if ($Slices -gt 0) {
  $per = [Math]::Ceiling($records.Count / $Slices)
  for ($q = 0; $q -lt $Slices; $q++) {
    $start = $q * $per
    if ($start -ge $records.Count) { break }
    $end = [Math]::Min(($q + 1) * $per - 1, $records.Count - 1)
    $p = Join-Path $OutDir ("style_corpus_slice{0}.txt" -f ($q + 1))
    Set-Content -LiteralPath $p -Value ($records[$start..$end] -join "`r`n") -Encoding UTF8
    $slicePaths += $p
  }
}

# --- 5) report (consumed by the caller) ---
[pscustomobject]@{
  corpus             = $corpus
  kept               = $records.Count
  listed             = $rows.Count
  stride             = $stride
  distinctRecipients = $recipients.Keys.Count
  dateRange          = "$($picked[-1].date)  ->  $($picked[0].date)"
  slices             = ($slicePaths -join "; ")
} | Format-List
