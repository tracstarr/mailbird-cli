---
name: mailbird
description: >-
  Work with the user's Mailbird desktop app (Windows) from this machine via one bundled CLI
  (bin/mailbird-cli.exe). Three capabilities: (1) DRAFT — create a real draft via the account's
  provider (Gmail API / Outlook IMAP) using Mailbird's own OAuth token, so it syncs into Mailbird's
  Drafts with the chosen From account and, for replies, attached to the correct thread (never sends);
  (2) COMPOSE — open a pre-filled compose window for human review (mailto: handoff; cannot set
  From/attachments/HTML); (3) READ and SEARCH — query Mailbird's local Store.db read-only to
  full-text search mail, list folders and recent messages, and read a message's headers, body, and
  ATTACHMENTS — every read returns the full local file path of each downloaded attachment, so you can
  open, parse, or analyze the actual PDF/image/document directly.
  Use whenever the user wants to draft, compose, write, or reply to an email via Mailbird, OR to
  find, search, look up, read, or summarize existing emails in their Mailbird mailboxes, OR to open,
  read, extract, or analyze an email attachment.
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
  `--signature "..."` / `--no-signature`, `--dry-run` (print the mailto URL, open nothing).
- Tell the user: draft only (they click Send); From = Mailbird's default account (not selectable —
  switchable in the composer); plain-text body, no HTML, no attachments.
- Generate a concise subject + clean plain-text body with paragraphs separated by blank lines. No
  signature is added by default; pass `--signature "..."` only if the user asks for one. Don't invent
  recipients — ask if unknown.
- Match the **user's own writing voice** if a writing-style profile is available in memory
  (`mailbird-writing-style`, produced by the `email-style-analysis` skill): use their greeting,
  sign-off, brevity, and register for that recipient type.

---

## Capability 2 — Create a draft via the provider API (recommended; sets account + threads replies)

Creates a **real draft on the mail server** using the OAuth token Mailbird already holds for that account
(Gmail REST API for Google accounts; IMAP `APPEND` for Outlook/Hotmail). The draft then syncs **into**
Mailbird's Drafts folder on the next poll. Unlike `compose`, this **picks the From account**, supports
**HTML**, and **attaches replies to the correct thread**. It **never sends** — only saves a draft.

```powershell
# New draft from a specific account
& $cli draft --account 1 --to "recipient@example.com" --subject "Subject" --body "Plain-text body."

# Reply that attaches to an existing thread — infers the account, thread, Re: subject, and recipient
& $cli draft --reply-to 112187 --body "Thanks — that works for me."
```

- Find the message id to reply to via `search`/`list`/`read` (the `Id` column), then pass it as `--reply-to`.
- Options: `--account ID` (required unless `--reply-to` supplies it), `--to` (comma-separated), `--subject`,
  `--body` / `--body-file`, `--cc`, `--bcc`, `--reply-to <messageId>`, `--html` (treat body as HTML),
  `--signature "..."` / `--no-signature`, `--dry-run` (print what it would do, create nothing), `--json`.
- **Formatting:** a plain-text body is sent as multipart/alternative with real paragraph spacing — separate
  paragraphs with a **blank line**, and use single newlines for line breaks within a paragraph. So write the
  body with proper structure (greeting, paragraphs, closing) rather than one run-on line. Pass `--html` only
  if you're supplying HTML yourself.
- **Voice:** write the body in the **user's own email style** if a writing-style profile is available in
  memory (`mailbird-writing-style`, produced by the `email-style-analysis` skill). Match their greeting,
  sign-off, brevity, pet phrases, and the register they'd use for that recipient. Capture their rhythm and
  phrasing — but keep the draft clean (don't reproduce typos); they review before sending.
- **Signature:** none is added by default (Mailbird applies the account's own signature on send, so a
  draft-body signature would duplicate it). If the user explicitly wants one in the draft body, pass
  `--signature "Name\nTitle"` (use `\n` for line breaks) or set the `MAILBIRD_SIGNATURE` env var; when set,
  it is appended after a blank line at the end (including on replies). Don't also type it into the body.
- On a reply: the account is inherited from the parent (a thread is account-specific), the subject becomes
  `Re: <original>`, and `--to` defaults to the original sender — override any of these explicitly.
- Requires an **OAuth Google or Microsoft account** (it reads the token read-only from Store.db; password
  accounts aren't supported). Keep Mailbird running so the token stays fresh. The draft appears in Mailbird,
  correctly threaded, after the next sync (seconds to a couple of minutes; an account that isn't the one
  in active view in Mailbird may take noticeably longer to poll its Drafts folder).
- Tell the user: draft only (they review and click Send); it's created server-side and syncs in. Don't
  invent recipients — ask if unknown.

### Track and revise drafts: `draft list` / `draft edit` / `draft delete`

Once a draft has synced into Mailbird you can list, edit, or delete it **by its local message Id** — the
same `Id` shown by `search`/`list`/`read`. The CLI resolves that Id back to the provider's draft (Gmail
`drafts.update`/`drafts.delete`; Outlook via the IMAP UID) and operates on it server-side, so the change
syncs back into Mailbird. **Never sends.**

```powershell
& $cli draft list                                  # drafts that have synced into Mailbird (Id, account, subject, to)
& $cli draft list --account 1                       # scope to one account
& $cli draft edit 112465 --body "Revised text."     # rewrite the body in place (keeps to/subject/thread)
& $cli draft edit 112465 --subject "New subject"    # change just the subject; body/recipients unchanged
& $cli draft delete 112465                           # remove the draft (server-side; Mailbird drops it on next poll)
```

- `draft edit <Id>` revises **in place** (Gmail keeps the same draft; Outlook appends the new version and
  removes the old over IMAP). Any field you don't pass keeps its current value — `--subject`, `--to`,
  `--cc`, `--bcc`, `--body`/`--body-file`, `--html`, `--signature`/`--no-signature` all override.
  If you omit `--body`, it reuses the draft's existing text (HTML formatting may simplify), so for a real
  body change always pass the new `--body`. Reply threading is preserved.
- `draft delete <Id>` only acts on actual drafts (it refuses any non-draft message). `--dry-run` on either
  shows what it would do without changing anything; add `--json` for machine-readable output.
- Both need the draft to have **synced into Mailbird first** (so it has a local Id and a provider handle).
  A brand-new draft that hasn't appeared in Mailbird yet has no Id to target — wait for it to sync.

---

## Capability 3 — Read & search mail (read-only)

The CLI opens `Store.db` with `Mode=ReadOnly` and never writes.

```powershell
& $cli accounts                                  # mailboxes + AccountId (use to scope queries)
& $cli folders 4                                 # folders (+ counts) for an account
& $cli search "invoice overdue" --limit 10       # full-text search (subject/body/from/to)
& $cli list --folder Inbox --account 4 --unread  # recent messages, filtered
& $cli read 112097                               # one message: headers + ATTACHMENT PATHS + body text
```

Append `--json` to any read/search command for machine-readable output (an array of row objects),
e.g. `& $cli search "receipt" --limit 5 --json | ConvertFrom-Json`.

### Workflow
1. `accounts` / `folders` first if you need an AccountId or folder name to scope a query.
2. `search` (or `list`) to find messages — note the `Id` column, and the `att` column (count of real
   attachments on that message).
3. `read <Id>` to get the body **and the full path of every attachment**, then answer/summarize.
4. If the answer depends on an attachment, open the printed path directly (see below).

### Attachments — you get the real file path, by default

Mailbird auto-downloads attachments to disk. **`read` always lists them with the full local path**, so
you never need an export step — just open the path with your normal file tools (read the PDF, view the
image, parse the spreadsheet).

```
Attach  : 2 (+1 inline hidden — --all to show)
  [40359] Report.pdf (52.7 KB, APPLICATION/PDF)
        C:\Users\Keith\AppData\Local\Mailbird\Store\A\40359\Report.pdf
  [40361] Screenshot.png (317.6 KB, IMAGE/PNG)
        C:\Users\Keith\AppData\Local\Mailbird\Store\A\40361\Screenshot.png
```

```powershell
& $cli read 113846                        # headers + attachment paths + body (paths come free)
& $cli attachments 113846                 # just the attachments for one message
& $cli attachments 113846 --json          # id, fileName, size, contentType, inline, downloaded, path
& $cli attachments 113846 --all           # include inline cid: images too
& $cli list --has-attachments --days 30   # only messages that actually have attachments
& $cli attachment save 40359 40361 --out C:\temp\out    # copy blobs to a writable folder
```

**How to use them:**
- Take the `path` from `read`/`attachments` and open it directly — it's a normal file on disk. To read a
  PDF, pass that path to your file-reading tool; same for images and documents.
- The store files are **read-only**; never write to or delete anything under `…\Mailbird\Store\A\`. If a
  tool needs a writable copy (or the user wants the file somewhere convenient), use `attachment save`.
- **`downloaded: false` / `(not downloaded)`** means the row exists but Mailbird never fetched the blob —
  there is no local file and `path` is null. Common for older archived mail. Tell the user to open that
  message in Mailbird to pull it down; don't claim you read a file that isn't there.
- **Inline images are hidden by default.** Most attachment rows are `cid:` images embedded in HTML bodies
  (signature logos, tracking pixels) — noise. `read`/`attachments` show only real attachments unless you
  pass `--all`. The `att` column in `list`/`search` counts real attachments only.
- The `[40359]` number is the **attachment id** (what `attachment save` takes) — not the message id.

### Search syntax
- Default: plain words are AND, punctuation-safe (e.g. `search "self-test report"`).
- `--raw`: FTS5 syntax — phrases, `col:term` (Subject/Body/From_/To_/Cc/Bcc), `AND`/`OR`/`NOT`, `term*`.

### Notes
- Read-only; never modifies the DB; safe while Mailbird runs.
- The body shown is Mailbird's indexed text (HTML rendered to text) — ideal for reading/summarizing;
  it is not the raw MIME source.
- Dates are UTC. `rd` (IsRead): 1 = read, 0 = unread. `att` = count of real (non-inline) attachments.
- Don't dump large raw rows back to the user — summarize and cite message `Id`s.
- Attachment blobs live in the `A` folder next to `Store.db` (`A\<attachmentId>\<filename>`); the CLI
  resolves that for you. `MAILBIRD_STORE_DB` moves both the DB and the attachment root together.
- Override the database location with the `MAILBIRD_STORE_DB` environment variable if needed.

See `references/store-db-schema.md` for the relevant Store.db tables/columns.
