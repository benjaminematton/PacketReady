# PacketReady — Documentation Style

> One design language across `design.md`, `build-plan.md`, and `impl/phase-*.md`.
> Read once. Replicate by example. This file is itself the example.

| | |
|---|---|
| **Scope** | All docs under `/docs/`. Code comments and READMEs out of scope. |
| **Audience** | Future-me, and the Atano reader who opens one doc cold. |
| **Owner** | Ben |
| **Status** | Active — v1, 2026-05-21 |
| **Companion** | [design.md](./design.md), [build-plan.md](./build-plan.md) |

---

## 0 · Premise

The docs are an artifact someone reads, not a wiki someone searches. They're written to be read top-to-bottom by one reader — a credentialing or platform engineer evaluating the work — on a 13" screen, in a single sitting. Every choice below serves that reader. Anything that doesn't is cut.

The aesthetic is **field manual, not blog post**. Tight margins. Numbered spines. Decisions visible without scrolling. No decoration that isn't load-bearing. Healthcare-adjacent work earns the manual register; in this domain, trust is built through visible rigor, not through tone.

---

## 1 · Voice

**Terse. Declarative. First-person singular when there is a position to take.**

| Do | Don't |
|---|---|
| "I'm building the score path first." | "We will be implementing the score path as the initial phase." |
| "Atano's homepage promises X. The feature doesn't exist." | "It appears that Atano may not currently offer X functionality." |
| "Mock CAQH. Live integration is out." | "Real CAQH integration is currently considered out of scope at this time." |
| Em-dash as a structural beat — like this — sparingly. | Parentheticals nested (inside other parentheticals (recursively)). |

**Five voice rules.**

1. **One sentence per thought.** If a thought needs two sentences, it was two thoughts. Split or cut.
2. **Decisions over prose.** Surface the choice; bury the deliberation. The reader wants the verdict, not the courtroom transcript.
3. **No hedge words.** Strike *probably*, *might*, *seems to*, *I think*, *kind of*, *somewhat*. If the claim is uncertain, mark it `[unresolved]` and move on.
4. **Cite every number.** Every quantitative claim names its dataset and N inline. "95% extraction accuracy" → "95% extraction accuracy (eval set v1, N=50)." Unsourced numbers in healthcare read as a red flag — and they are.
5. **Define acronyms per document, on first use.** Healthcare is dense: CAQH, NPPES, NPI, NPDB, OIG, SAM, PSV, RCM, ICP. A reader opening `phase-1.md` cold shouldn't have to hunt back through `design.md` to learn what NPPES expands to.

> *Single-sentence pull-quotes* sit under H1 only. They're the doc's thesis in one breath.

---

## 2 · The document spine

Every doc in `/docs/` opens with the same five-element spine, in this order:

```
# Title — Subtitle

> One-sentence pull-quote. The doc's thesis.

| | |
|---|---|
| **Status** | …
| **Owner**  | Ben
| **Data**   | synthetic only · mocked PSV · no PHI
| **…**      | …

---

## 1 · First section
```

The spine elements:

1. **H1** — `Project — Subtitle` format. Em-dash, not colon. Never numbered.
2. **Pull-quote** — single blockquote, one sentence, italics permitted but not required. No links.
3. **Metadata table** — borderless-looking 2-column. 3–6 rows. Always includes **Status**, always includes **Data** (the compliance posture in one line), and always includes a companion-doc link if siblings exist.
4. **`---`** — horizontal rule. The reader's signal that orientation is over and the doc begins.
5. **Numbered H2** — starts at `## 1 · …` for design/impl docs; starts at `## North star` for build-plan-style strategic docs (they're navigational, not specifications).

**Why the Data row.** Healthcare readers scan for data-handling posture before they read the body. Putting it in the spine answers the unspoken question — *am I about to read about real PHI?* — in the first 5 seconds. The line is one phrase, mid-dot-separated, never a paragraph.

This spine is non-negotiable. Sibling docs that share it are recognizable in the first 200ms.

---

## 3 · Headings & numbering

The heading system is a **three-tier spine with one optional rung**.

| Level | Use | Example |
|---|---|---|
| `# H1` | Document title only. Once per file. | `# PacketReady — Design Doc` |
| `## H2` | Top-level sections. Numbered with `· ` separator. | `## 7 · Subsystems` |
| `### H3` | Subsections under H2. Numbered as `N.M`. | `### 7.4 Validator suite` |
| `#### H4` | Rare. File-level callouts inside impl docs. No number. | `#### LicenseStatusValidator.cs` |

**Numbering rules.**

- H2 numbers run consecutively in document order. No gaps, no `## 7 · Subsystems` followed by `## 9 · Eval plan`.
- Subsystem-style enumerations (the 8 PacketReady subsystems) are the **only** place sub-numbering survives into H3. Everything else flattens to H3 without numbers when there's no real hierarchy.
- Don't write `## Section 7: Subsystems`. Write `## 7 · Subsystems`. The `·` (U+00B7 middle dot) is the project's separator. It reads quieter than a colon.

**No emoji in headings.** Ever. The glyph vocabulary in §4 lives in body text only.

---

## 4 · The callout vocabulary

Six glyphs. Fixed meanings. Used inline at the start of a paragraph or list item — no boxes, no admonition syntax, no GitHub `> [!NOTE]` blocks (they render inconsistently and lock you into a tooling chain).

| Glyph | Meaning | Use when |
|---|---|---|
| `▎` | **Rule / invariant** | Stating a non-negotiable. "▎ Every agent decision is auditable." |
| `→` | **Next / leads to** | Sequencing within prose. "→ Phase 2 lands the eval harness." |
| `✦` | **Decision** | Marking a recorded choice. "✦ Self-host Langfuse over hosted." |
| `⚠` | **Risk / open question** | Surfacing something unresolved. "⚠ Rubric weights are a guess until P4." |
| `◇` | **Aside** | A digression that earns its keep. Use once per doc, max. |
| `✕` | **Rejected** | An option considered and discarded. "✕ Intake-first ordering — score lands too late." |

**The rule of glyphs.** A glyph is a promise that the next sentence is doing the work the glyph names. If a `✦` sentence doesn't record a decision, demote it to plain text. Glyphs aren't decoration.

**Frequency budget.** No more than one glyph per ~150 words on average. If you're reaching for a glyph for the third time in a paragraph, the paragraph is doing too much; split it.

---

## 5 · Tables

Tables earn their weight by replacing a paragraph, not supplementing one. Three table archetypes are sanctioned; everything else converts to prose or a list.

**5.1 Metadata table** — the doc-spine table (§2). Always borderless-looking, 2 columns, bold left, plain right.

**5.2 Comparison table** — for tradeoffs the reader is meant to scan, not read.

```
| Approach            | Pros                  | Cons                       |
|---------------------|-----------------------|----------------------------|
| Bottom-up build     | Orderly dependencies  | No demo until P4+          |
| Intake-first        | Familiar pattern      | Score lands too late       |
| Score-first (chosen)| Hardest claim first   | Validator surface evolves  |
```

The chosen row is annotated `(chosen)` in the leftmost cell. No bolding, no color, no ✓ — the parenthetical does the work.

**5.3 Metric table** — measurable targets, with units in the metric name and a single target column.

```
| Metric                                          | Target  |
|-------------------------------------------------|---------|
| Extraction field accuracy (clean PDFs)          | ≥ 95%   |
| Score correlation w/ human label (Spearman)     | ≥ 0.80  |
```

**Anti-pattern.** Tables with 6+ columns. A table that doesn't fit on a 13" screen has stopped being a table and started being a spreadsheet.

---

## 6 · Code, diagrams, ASCII

**6.1 Fenced code is always labeled.** Language tag for syntax highlighting; a leading comment with the file path when the snippet corresponds to a real file.

```csharp
// apps/api/Application/Scoring/ScoreSynthesizer.cs
public ReadinessScore Synthesize(IReadOnlyList<ValidatorResult> results) { … }
```

**6.2 Architecture diagrams are ASCII box-drawing.** Mermaid is rejected — it renders inconsistently across viewers and the source is unreadable in raw form. Box-drawing renders identically everywhere and is grep-able.

Conventions:

- Boxes use `┌─┐ │ └─┘`. Single-line, not double.
- Flow arrows: `─▶` for data flow, `┄┄▶` for async/eventual.
- Layer headers go above the boxes, not inside them.
- Maximum width 64 chars — fits a side-by-side editor and a phone-rendered preview.

**6.3 Inline code (backticks)** is reserved for: file paths, type names, environment variables, exact strings the reader will type or grep. *Not* for emphasis — emphasis uses **bold** or *italic*.

---

## 7 · Lists

Three list shapes, each with a fixed semantic.

| Shape | Means | Looks like |
|---|---|---|
| `- bullet` | Unordered set. Reordering doesn't change meaning. | `- Provider · Profile · Issue · Score` |
| `1. numbered` | Sequence. Reordering breaks the doc. | Build steps, phase order. |
| `- [ ] checkbox` | Definition-of-done. Each item is verifiable in a single observation. | DoD blocks in impl docs. |

**Checkbox DoD rule.** Every checkbox is something a human can verify in under 30 seconds. "Score logic is correct" is not a DoD item; "`Dr. Yellow` scores `62`" is.

**Decision-log items** use the `✦` glyph from §4, one line each:

```
✦ Hangfire over Quartz — UI helps the demo (2026-05-21)
✦ Self-host Langfuse — privacy story (2026-05-21)
✦ Score-first phase order — hardest claim first (2026-05-21)
```

Date in parens. Author implicit (one-person project). When this becomes a team doc, add `— @who`.

---

## 8 · Status & severity tags

Two fixed vocabularies. No synonyms.

**8.1 Doc status** — in the metadata table only.

| Tag | Means |
|---|---|
| **Draft** | Substance is forming. Reader should expect motion. |
| **Active** | Living doc, kept current as work moves. |
| **Closed** | Frozen. Captures a moment. Updates only as errata. |

**8.2 Issue severity** — in score-related content.

| Tag | Score penalty | Means |
|---|---|---|
| **Critical** | −25 | Blocks submission. |
| **Major** | −10 | Likely first-pass denial. |
| **Minor** | −3 | Cosmetic / soft conflict. |

Severity words are **bold** in body text. Never italicized, never colored, never wrapped in a custom span. The bold is the badge.

---

## 9 · Cross-references

Internal links use **relative paths** with anchor fragments when pointing at a section.

```markdown
See [design.md §7.4](./design.md#74-validator-suite) for the validator contract.
```

External links carry the bare URL or a short anchor — never marketing pull-quotes from the destination.

**Section anchor format.** GitHub auto-slugs headings: `## 7 · Subsystems` becomes `#7--subsystems` (note: middle dot becomes empty, producing the double dash). Test the anchor before publishing; if it doesn't resolve, rename the section before you start inventing custom anchors.

**External citations.** Regulations, payer documents, vendor pages, papers — always carry both a publication date and an accessed date. Healthcare claims rot fast (rule changes, FAQ updates, deprecated endpoints), and an undated citation can't be audited.

```
- [CAQH ProView API docs](https://...) — pub 2024-09, accessed 2026-05-21
- CMS Medicare Provider Enrollment final rule, 89 FR 41450 — pub 2024-05-13, accessed 2026-05-20
- Anthropic, *Building Effective Agents* — pub 2024-12, accessed 2026-05-20
```

---

## 10 · The "what to cut" list

If the doc is over budget, cut in this order:

1. **Decorative adjectives.** "Robust", "scalable", "seamless", "elegant". Always.
2. **Throat-clearing intros.** "It's worth noting that…", "In order to…", "What we're trying to do here is…".
3. **Repeated framings.** If §2 sets up the problem and §3 sets it up again with different words, delete §3's setup.
4. **Tables under 3 rows.** Convert to a sentence or a 2-item list.
5. **Footnotes.** If it matters, inline it. If it doesn't, cut it.
6. **The first paragraph of every section.** Read the doc with all of them deleted. Restore only the ones whose absence broke the read.

---

## 11 · Rejected directions

For the record, since style choices are easier to defend when the alternatives are visible.

✕ **Admonition blocks** (`> [!NOTE]`, `> [!WARNING]`). GitHub-only, render as branded boxes that fight the doc's voice. The §4 glyph vocabulary does the same work without locking the doc to a renderer.

✕ **Mermaid diagrams.** See §6.2. The source-vs-render gap is unacceptable in a doc meant to be read both rendered and raw.

✕ **Front-matter (YAML headers).** Tooling-coupled and invisible to readers. The §2 metadata table is the rendered equivalent and serves both audiences.

✕ **Emoji in headings or status badges.** Reads as casual. The §4 glyph set is the considered alternative.

✕ **Heavy use of `<details>` collapsibles.** A reader who has to click to read isn't reading. Reserved for genuinely long appendices (eval-set listings, full schema dumps) — not for hiding "extra context."

✕ **Color or HTML styling.** Markdown-only. Renders identically in GitHub, VS Code preview, plain editors, and printed PDFs. The cost of color is portability; the value of color, in a doc this terse, is near-zero.

---

## 12 · How to use this

Open the doc you're writing. Open this file in a split. When you reach for a callout, table, or heading style, mirror what's here. When something doesn't fit, **don't invent a new pattern** — first check whether the thing you're trying to say can be said in the existing vocabulary. Nine times out of ten, it can.

The tenth time is a real gap. Add the new pattern here first, then use it. The doc set is small enough that consistency is cheap and drift is expensive.

→ Next: read [design.md](./design.md) and [build-plan.md](./build-plan.md) with this open. The patterns above are already in use; this file just names them.
