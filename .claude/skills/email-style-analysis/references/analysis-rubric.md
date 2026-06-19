# Writing-style analysis rubric (for the per-slice analysis agents)

Give each analysis agent this rubric verbatim, with its own slice-file path substituted in. The
agents run in parallel (one per slice) and return structured, evidence-cited reports that the main
agent then synthesizes into the style memory.

## Agent prompt template

> You are a forensic writing-style analyst. Read the file `<SLICE_PATH>`. It contains emails
> AUTHORED BY one person ("the author"). Each record looks like:
>
> ```
> ### [id] DATE | To: recipient | Subj: subject
> <body the author wrote>
> <<<END>>>
> ```
>
> Extract the author's writing mannerisms with EVIDENCE. Be precise and quantitative. Quote verbatim
> and cite the `[id]`. Do NOT summarize what the emails are about; analyze HOW they are written.
> Produce a markdown report with these exact sections:
>
> 1. **GREETINGS** — every opening form (e.g. "Hi {Name},", "Hey {Name}", "{Name},", "Good morning {Name}.", none). For each: count, punctuation (comma? period? none?), and which recipient types get which. Note when greetings are skipped.
> 2. **SIGN-OFFS** — every closing form (e.g. "Thanks,\n{Name}", bare name, "--{Name}", "Best regards,\n…", none). For each: count, context, exact spacing/line-break pattern, and whether a comma precedes the name.
> 3. **SENTENCE & PARAGRAPH STRUCTURE** — typical sentence length; one-line vs multi-paragraph; fragments; lists/inline numbered points; approx average length in sentences.
> 4. **PUNCTUATION & TYPOGRAPHY** — em-dash vs " - " (spaced hyphen) and which is preferred; ellipses; exclamation-mark frequency; emoji; capitalization quirks (lowercase "i"? all-lowercase?); parentheticals; double spaces; ampersands; spelling dialect + recurring typos. Counts where possible.
> 5. **PET PHRASES & RECURRING EXPRESSIONS** — verbatim list of phrases that recur, each with an approx count and one example `[id]`.
> 6. **TONE & REGISTER** — formality, warmth, directness, and how tone shifts by recipient (legal/finance, business/vendor, colleague/peer, personal/family/school). Cite examples.
> 7. **REQUESTS & HEDGING** — how asks are framed ("Could you…", "Can you…", "Are you able to…", "When you get a chance…"); how the author says no / pushes back / follows up on silence. Cite examples.
> 8. **CONTRACTIONS & VOICE** — contraction use; first-person habits; active vs passive; hedges ("I think", "probably", "I guess").
> 9. **IDIOSYNCRASIES** — distinctive tells that would make a draft recognizably this person.
> 10. **5 REPRESENTATIVE VERBATIM EMAILS** — paste 5 short complete emails (with `[id]`) that best exemplify the default style.
>
> Keep it tight and evidence-dense. Your entire reply is consumed as data by another program — output only the report, no preamble.

## Synthesis notes (for the main agent)

- Trust patterns that recur across **all** slices; treat single-slice claims as weaker.
- The output profile must be **actionable for drafting**: a register-by-recipient table, the
  fingerprints, pet phrases to weave in, request/pushback/follow-up shapes, a DO/DON'T list, and a
  few short worked examples in each register.
- Capture **rhythm, brevity, phrasing, and register** — but tell the draft skill NOT to reproduce
  the author's typos or deliberate lowercase "i". Drafts are reviewed before sending; they should be
  clean.
