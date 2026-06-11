# mailbird-cli

Compose drafts and search your local **Mailbird** mail from the command line — plus a Claude Code
**skill** that wraps it. Windows only.

- **compose** — open a pre-filled draft in the running Mailbird for you to review and send
  (`mailto:` handoff; **never auto-sends**)
- **search / list / read** — full-text search and read your mail straight from Mailbird's local
  database (**read-only**)

Everything stays on your machine. The database is opened read-only, and nothing is sent without you
clicking Send in Mailbird.

## Quick start

```powershell
git clone <your-repo-url>
cd mailbird-cli
.\build.ps1            # compile the self-contained CLI into the skill (needs the .NET 8 SDK)
```

Use the CLI directly:

```powershell
$cli = ".\.claude\skills\mailbird\bin\mailbird-cli.exe"
& $cli compose --to you@example.com --subject "Hi" --body "Hello there"
& $cli search "invoice overdue" --limit 10
& $cli read 12345
```

Or install it as a Claude Code skill (available in any session):

```powershell
.\install.ps1          # copies the skill to ~/.claude/skills/mailbird
```

## Commands

| Command | Description |
|---|---|
| `compose --to A --subject S --body B` | Open a pre-filled draft for review (`--cc` `--bcc` `--body-file` `--dry-run`). |
| `search <query> [--account ID] [--limit N] [--raw]` | Full-text search (subject/body/from/to). |
| `list [--folder NAME] [--account ID] [--from SUBSTR] [--unread] [--days N] [--limit N]` | Recent messages. |
| `read <messageId> [--max CHARS]` | One message: headers + body text. |
| `accounts` / `folders [accountId]` | Discover mailboxes / folders. |
| `tables` / `schema <like>` / `sql <query>` | Raw read-only access to the database. |

Append `--json` to any read/search command for machine-readable output. Run the CLI with no
arguments for full usage.

## Releases

Tagged releases include a prebuilt **`mailbird-<version>.skill`** bundle (the skill plus the compiled
CLI) on the [Releases page](https://github.com/tracstarr/mailbird-cli/releases) — no build needed.
It's a zip; extract it into your skills folder to install:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory("mailbird-v0.1.0.skill", "$HOME\.claude\skills")
```

Releases are produced automatically by CI when a `v*` tag is pushed (see `.github/workflows/release.yml`).

## Build from source

Requires the **.NET 8 SDK**.

```powershell
.\build.ps1                       # self-contained single-file exe (no runtime needed to run)
.\build.ps1 -FrameworkDependent   # small build (~2 MB) that needs the .NET 8 runtime
.\build.ps1 -Package              # also writes dist/mailbird.skill (a shareable bundle)
```

The compiled binary is staged into `.claude/skills/mailbird/bin/` (git-ignored — it's a build
artifact).

## Project layout

```
mailbird-cli/
├─ src/mailbird-cli/            # the CLI source (one C# project)
│  ├─ mailbird-cli.csproj
│  └─ Program.cs
├─ .claude/skills/mailbird/     # the Claude Code skill (bundles the built CLI)
│  ├─ SKILL.md
│  └─ references/store-db-schema.md
├─ build.ps1                    # compile the CLI into the skill (+ package)
├─ install.ps1                  # install the skill to ~/.claude/skills
└─ .github/workflows/ci.yml     # build check
```

## How it works

- **compose** builds a `mailto:` URL and hands it to Mailbird (its registered `mailto:` handler),
  which opens a pre-filled composer in the running instance. It is draft-only and never sends.
- **search / read** open `%LOCALAPPDATA%\Mailbird\Store\Store.db` read-only and use Mailbird's
  built-in FTS5 full-text index. Override the location with the `MAILBIRD_STORE_DB` environment
  variable.

## Limitations

- Windows + Mailbird only.
- compose: draft-only; From is Mailbird's default account (not selectable via `mailto:`); plain-text
  body; no HTML or attachments.
- read/search: read-only — never modifies the database.

## License

MIT — see [LICENSE](LICENSE).
