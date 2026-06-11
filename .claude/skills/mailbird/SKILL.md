---
name: mailbird
description: >-
  Work with the user's Mailbird desktop app (Windows) from this machine via one bundled CLI
  (bin/mailbird-cli.exe). Two capabilities: (1) COMPOSE — open a pre-filled email draft for human
  review before sending (mailto: handoff; never auto-sends; cannot set From/attachments/HTML);
  (2) READ and SEARCH — query Mailbird's local Store.db read-only to full-text search mail, list
  folders and recent messages, and read a message's headers plus body. Use whenever the user wants
  to draft, compose, write, or reply to an email via Mailbird, OR to find, search, look up, read,
  or summarize existing emails in their Mailbird mailboxes.
---

# Mailbird (compose + read/search)

Everything runs through one bundled binary: **`bin/mailbird-cli.exe`** (next to this SKILL.md). It is
self-contained — no .NET runtime or other install needed. Composing opens a **draft** for the human
to send; reading/searching is **read-only** against the local DB.

## Setup

Point `$cli` at the bundled binary (in this skill's `bin/` folder). When installed for your user:

```powershell
$cli = Join-Path $HOME ".claude\skills\mailbird\bin\mailbird-cli.exe"
# (if the skill is installed inside a project, use that project's
#  .claude\skills\mailbird\bin\mailbird-cli.exe instead)
```

Run `& $cli` with no arguments for built-in usage.

---

## Capability 1 — Compose a draft for human review

Opens a pre-filled compose window in the running Mailbird. **Draft only — the human reviews and
clicks Send. Never sends automatically.**

```powershell
& $cli compose --to "recipient@example.com" --subject "Subject" --body "Plain-text body."
```

Long / multi-line body — write a temp file and use `--body-file` (avoids shell-escaping):
```powershell
$tmp = Join-Path $env:TEMP "mb_body.txt"
@"
Hi there,

First paragraph.
Second paragraph.
"@ | Set-Content -LiteralPath $tmp -Encoding UTF8
& $cli compose --to "recipient@example.com" --subject "Subject" --body-file $tmp
```

- Options: `--to` (required; comma-separated), `--subject`, `--body` / `--body-file`, `--cc`, `--bcc`,
  `--dry-run` (print the mailto URL, open nothing).
- Tell the user: draft only (they click Send); From = Mailbird's default account (not selectable —
  switchable in the composer); plain-text body, no HTML, no attachments.
- Generate a concise subject + clean plain-text body. Don't invent recipients — ask if unknown.

---

## Capability 2 — Read & search mail (read-only)

The CLI opens `Store.db` with `Mode=ReadOnly` and never writes.

```powershell
& $cli accounts                                  # mailboxes + AccountId (use to scope queries)
& $cli folders 4                                 # folders (+ counts) for an account
& $cli search "invoice overdue" --limit 10       # full-text search (subject/body/from/to)
& $cli list --folder Inbox --account 4 --unread  # recent messages, filtered
& $cli read 112097                               # one message: headers + body text
```

Append `--json` to any read/search command for machine-readable output (an array of row objects),
e.g. `& $cli search "receipt" --limit 5 --json | ConvertFrom-Json`.

### Workflow
1. `accounts` / `folders` first if you need an AccountId or folder name to scope a query.
2. `search` (or `list`) to find messages — note the `Id` column.
3. `read <Id>` to get the body, then answer/summarize for the user.

### Search syntax
- Default: plain words are AND, punctuation-safe (e.g. `search "self-test report"`).
- `--raw`: FTS5 syntax — phrases, `col:term` (Subject/Body/From_/To_/Cc/Bcc), `AND`/`OR`/`NOT`, `term*`.

### Notes
- Read-only; never modifies the DB; safe while Mailbird runs.
- The body shown is Mailbird's indexed text (HTML rendered to text) — ideal for reading/summarizing;
  it is not the raw MIME source.
- Dates are UTC. `rd` (IsRead): 1 = read, 0 = unread.
- Don't dump large raw rows back to the user — summarize and cite message `Id`s.
- Override the database location with the `MAILBIRD_STORE_DB` environment variable if needed.

See `references/store-db-schema.md` for the relevant Store.db tables/columns.
