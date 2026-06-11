# Mailbird Store.db — tables/columns used by this skill

Read-only reference for the `mbdb` engine. DB location: `%LOCALAPPDATA%\Mailbird\Store\Store.db`
(override with `MAILBIRD_STORE_DB`). SQLite. **Never write to it.**

## Full-text index — `FTS_Messages` (the workhorse)
FTS5 virtual table. Its `rowid` equals `Messages.Id`. Columns hold denormalized text, usable for
both search (`MATCH`) and display:
- `Subject`, `Body` (message text, HTML rendered to plain text), `From_`, `To_`, `Cc`, `Bcc`.

Search example: `... FROM FTS_Messages WHERE FTS_Messages MATCH 'invoice' ORDER BY rank`.
Snippet for context: `snippet(FTS_Messages, 1, '«', '»', '…', 6)` (column 1 = Body).

## `Messages`
Per-message metadata. Key columns this skill uses:
- `Id` (PK, = FTS rowid), `AccountId`, `ReceivedAt_UTC` (UTC datetime, sortable string),
  `Subject`, `IsRead` (1/0), `MailMessageId`, `IsReadReceiptsEnabled`.
- A message links to folders via `Folders_Messages(FolderId, MessageId)`; a message can be in
  multiple folders.

## `Folders`
- `Id`, `AccountId`, `Name` (e.g. Inbox, Sent, Archived), `ParentId`.

## `Accounts`
- `Id` (the AccountId used to scope queries), `Username` (the email address), `Server_Host`.
- Note: there is no `Address`/`Name` column — use `Username`.

## `Messages_Contacts`
Per-recipient rows: `MessageId`, `ContactId`, `Type` (role), `Email`, `Name`, plus open-tracking
columns Mailbird populates for messages it sent with tracking on:
`ReadReceipts_TrackingId`, `ReadReceipts_OpenedLastTimeUtc`, `ReadReceipts_Metadata`.

## Bodies
`Messages.Source` is usually NULL; raw bodies live in `MessageBodies(Id, Data, IsHtml, ...)` and may
be compressed. Prefer `FTS_Messages.Body` for readable text — no decompression needed.
