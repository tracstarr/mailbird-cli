---
name: email-style-analysis
description: >-
  Analyze the user's own SENT emails (read-only, via the bundled mailbird CLI) to learn how they
  write — greetings, sign-offs, brevity, tone, pet phrases, and register by recipient — then write
  the result to a local "writing-style" memory file that the mailbird draft/compose skill reads so
  generated drafts sound like the user. Use whenever the user wants to study, learn, profile,
  capture, or refresh their email writing style / voice / mannerisms, or asks you to "review my
  emails and learn how I write."
---

# Email writing-style analysis

Builds a reusable **writing-style profile** from a sample of the user's own sent mail, so the
mailbird `draft` / `compose` capability can match their voice. Everything is **read-only** against
Mailbird's local database (it reuses the sibling **mailbird** skill's CLI).

## What it produces
A practical drafting guide written to the user's **local Claude memory**:
`…\.claude\projects\<project-slug>\memory\mailbird-writing-style.md` (+ a one-line pointer in that
folder's `MEMORY.md`). The mailbird draft skill picks it up from there.

> **Privacy — important.** The corpus and the profile contain personal mail. This repo is a **public
> project** and release bundles ship `.claude/skills/`, so the profile must **never** be written into
> the repo. Write it only to the user's local memory dir, and delete the temp corpus/slice files when
> done. (This skill itself is generic and contains no personal data, so it's safe to ship.)

## When to run
- **First time:** to create the profile.
- **Refresh:** when style may have drifted, or the user wants it rebuilt from more/newer mail.

---

## Steps

### 1. Pick the account
Find the user's primary sent mailbox. Set `$cli` per the mailbird skill, then:
```powershell
$cli = "<...>\.claude\skills\mailbird\bin\mailbird-cli.exe"
& $cli accounts                 # choose the AccountId whose Username is the user's main address
& $cli folders <accountId>      # confirm the sent-mail folder name (usually "Sent")
```
Account 1 / folder `Sent` is the default. If the user wants their voice across contexts (work +
personal), either run once on the richest account or run per-account and synthesize together.

### 2. Extract a spread sample (read-only)
Run the bundled script. It samples across a **wide window** (not just the latest thread), strips
quoted reply text, keeps only authored bodies, and writes a corpus + slice files:
```powershell
$extract = "<...>\.claude\skills\email-style-analysis\scripts\extract-sent-corpus.ps1"
& $extract -Account 1 -Target 200 -Slices 4
```
It prints `kept`, `distinctRecipients`, `dateRange`, and the slice paths. The keep-rate is < 100%
(short/empty notes are dropped) — if `kept` is well under the target, re-run with a larger `-Target`.
Aim for ≥ ~150 kept across many recipients for a stable profile.

### 3. Analyze the slices in parallel
Launch **one analysis agent per slice** (general-purpose), each given the rubric in
[references/analysis-rubric.md](references/analysis-rubric.md) with its slice path substituted in.
Each returns a structured, evidence-cited report. Optionally read one slice yourself to ground-truth
the agents' findings.

### 4. Synthesize → the style memory
Merge the reports into **one** profile and write it to the user's memory dir as
`mailbird-writing-style.md`, then add a pointer line to `MEMORY.md`. Use the memory format (frontmatter
`name` / `description` / `metadata.type: user`; the `description` must mention email drafting so it is
recalled at draft time). Make it **actionable**, with:
- a one-line core-voice summary;
- the **fingerprints** (the tells: sign-off form, greeting habits, dash style, etc.);
- a **register-by-recipient table** (greeting + body + sign-off for each recipient type);
- **pet phrases** to weave in, and the author's **request / pushback / follow-up** shapes;
- a **DO / DON'T** list, and 3–5 short **worked examples** in different registers.

Tell the draft skill to capture **rhythm, brevity, phrasing, and register — but not to reproduce
typos or deliberate lowercase "i"** (drafts are reviewed before sending; keep them clean).

### 5. Clean up
Delete the temp corpus and slice files (e.g. `Remove-Item $env:TEMP\style_corpus*.txt`).

---

## How the draft skill uses it
Because the profile lives in the user's memory, it is recalled when drafting. When you (or the
mailbird **draft** / **compose** flow) write an email body, **match the profile**: pick the register
from the recipient table, use the author's greeting/sign-off forms and pet phrases, and keep the
length/tone consistent with their voice.
