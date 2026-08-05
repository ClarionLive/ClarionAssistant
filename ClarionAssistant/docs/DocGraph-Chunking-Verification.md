# DocGraph PDF Chunking — Verification Set & Baseline

**Purpose:** prove whether a rewrite of `DocGraphService.ChunkPdfText` makes PDF retrieval
single-query reliable. Baseline captured against the **current (broken) index** so the
before/after comparison survives the re-chunk.

**Baseline captured:** 2026-08-04, immediately after the PdfPig re-ingest of all 40 bundled PDFs.
**Index state at capture:** 162 libraries / 28,684 chunks.
**Extraction:** PdfPig (fixed). **Chunking:** unchanged/broken — this is what the set measures.

> The PDF *text* is correct as of this baseline. Every failure below is a **chunk boundary,
> heading, or ranking** failure, not a text-extraction failure. Do not confuse the two when
> reading the results.

## Scope note — PDF only

Keyword-heading counts by format (Charlie's DB query): **pdf 486, chm 0, web 0, htm 0, html 0.**
`ChunkPdfText` is the sole offender. The CHM/HTML paths already emit class-qualified headings
and are the reference shape to match. `ExtractClassName` already exists at
`DocGraphService.cs:1838` and feeds the HTML path (`:495` → `:559`/`:607`); the PDF path never
calls it.

## Pass criteria

A test **passes** when the correct answer is obtainable in **one** `query_docs` call, where the
answer content appears in the **top 3** results and is **unambiguously attributable** to the
right class without the caller already knowing which class to filter for.

---

## T1 — Class-qualified subsection retrieval (the headline failure)

**Query:** `query_docs(query="ASCIIFileClass non-virtual methods categories", library="ABC Library Reference")`

**Expected answer:** three categories — Housekeeping (one-time) Use (`Init`, `Kill`);
Mainstream Use (`GetLastLineNo`, `GetLine`, `GetPercentile`, `SetPercentile`);
Occasional Use (`GetFilename`, `Reset`).

**BASELINE: FAIL.** 5/5 results were TOC/index chunks (`ASCIIFileClass..... 52`). Zero body
content. Required **5 separate queries** by obscure method name to assemble the answer.

## T2 — Cross-class subsection collision

**Query:** `query_docs(query="Mainstream Use", library="ABC Library Reference")`

**Expected:** results distinguishable by owning class.

**BASELINE: FAIL.** 5 results, each a **single orphaned line**, no class context in any of them:

```
Mainstream Use:  TakeNewSelectionD  handle Event:NewSelections
Mainstream Use:  ResizeV   resize and reposition all controls
Mainstream Use:  TakeEventVI handle events for the CHECK control
Mainstream Use:  TakeEventVI handle events for the ENTRY control
Mainstream Use:  TakeEventV  handle events for the edit control
```

Five different classes. Nothing in the chunk text identifies any of them. Same for
"Occasional Use" — returns PrintPreviewClass, ASCIIViewerClass, QueryListClass, QueryFormClass
and ASCIIFileClass interleaved. **`library=` filtering cannot help: they are all in one PDF.**

## T3 — TOC/index chunks outranking prose

**Query:** `query_docs(query="ASCIIFileClass", library="ABC Library Reference")`

**Expected:** the class overview/prose in the top 3.

**BASELINE: FAIL.** 5/5 dot-leader index chunks, zero prose. Dot-leader chunks are
**8,241 of 28,684 = 28.7% of the entire index** and are nearly pure keyword, so bm25 ranks them
above real content for any class-name query.

## T4 — Systemic check, second class

**Query:** `query_docs(query="ErrorClass non-virtual methods three categories housekeeping mainstream occasional", library="ABC Library Reference")`

**BASELINE: FAIL.** 3/3 TOC chunks. Confirms T1/T3 are systemic, not an ASCIIFileClass quirk.

## T5 — Method definition lands under its own heading

**Query:** `query_docs(query="GetLastLineNo", library="ABC Library Reference")`

**Expected:** definition ("returns the number of the last line in the file, and indexes the
entire file", Return Data Type LONG) in a chunk headed `GetLastLineNo`.

**BASELINE: PARTIAL.** Answer *was* reachable in one query, but: top hit was the TOC line
`GetLastLineNo ..... 57`, and the definition arrived inside a chunk headed **`Example`** whose
content opens with the tail of the *previous* method's example (`GetFilename`), crosses a page
break, then reaches the definition. Right answer, wrong boundaries.

## T6 — Bogus keyword headings

**Check:** chunks whose `heading` is a Clarion keyword lifted from example code.

**BASELINE: FAIL.** ACCEPT ×209, PROGRAM ×205, RETURN ×45, CASE ×18, "RETURN FALSE" ×17 (486 total,
all PDF). The chunk actually containing the ASCIIFileClass Functional Organization prose is
titled **`ACCEPT (cont.)`**. Also observed: `PROGRAM (cont.)`, `ID              LONG (cont.)`,
`EQ (cont.) (cont.)`, `DISPOSE (cont.) (cont.) (cont.)`.

## T7 — CHM path must not regress

**Query:** `query_docs(query="ASCIIFileClass Functional Organization Expected Use", library="ClarionHelp")`

**BASELINE: PASS (headings).** Headings are class-qualified — `ASCIIFileClass`,
`AsciiFileClass Methods` — and zero keyword junk. Caveat for honesty: the CHM chunks returned
here are **navigational link lists, not prose**, so CHM is a good reference for *heading shape*
but is not itself answering T1. Re-run after the change purely to confirm no regression.

## T8 — Extraction regression guard (must stay passing)

**Query:** `query_docs(query="@D6 dd/mm/yyyy date picture format result", library="LanguageReference")`

**Expected:** `@D6  dd/mm/yyyy  31/10/1959` and `@D14  mm/yyyy  10/1959`, whole table aligned.

**BASELINE: PASS.** This is the PdfPig fix from earlier today. Any re-chunk **must not** break
it — the table is whitespace-aligned multi-column text and is the most fragile thing in the
corpus. Highest-value regression guard in this set.

---

## Baseline summary

| Test | What it measures | Baseline |
|------|------------------|----------|
| T1 | Class-qualified subsection retrieval | **FAIL** (5 queries needed) |
| T2 | Cross-class collision | **FAIL** (no class context at all) |
| T3 | TOC noise in ranking | **FAIL** (5/5 index chunks) |
| T4 | Systemic across classes | **FAIL** (3/3 index chunks) |
| T5 | Method def under own heading | **PARTIAL** (right answer, wrong chunk) |
| T6 | Bogus keyword headings | **FAIL** (486 chunks) |
| T7 | CHM path unaffected | **PASS** (guard) |
| T8 | PdfPig extraction correct | **PASS** (guard) |

---

# Post-fix results — commit 6ccd068

**Run:** 2026-08-04, by CA-Terminal-1-CC, through the live `query_docs` tool (not hand-written SQL).
**Index state at run:** 162 libraries / 17,234 chunks / 1,149 tagged `index` — matches the
implementer's reported state exactly.
**Order:** T8 first, as the regression guard.

| Test | Baseline | Post-fix | Note |
|------|----------|----------|------|
| T1 | FAIL (5 queries) | **PASS** | Answer chunk is #1: `ASCIIFileClass > Functional Organization`, all three categories present |
| T2 | FAIL (no class context) | **PASS** | 5/5 results class-attributed with full section body |
| T3 | FAIL (5/5 index) | **PASS** | 5/5 prose, zero index chunks |
| T4 | FAIL (3/3 index) | **PASS** | Answer at #1; also confirms the stale-parent fix (`ErrorClass > SetCategory`) |
| T5 | PARTIAL | **PASS** | Top hit is the definition under `ASCIIFileClass > GetLastLineNo`, Return Data Type LONG |
| T6 | FAIL (486) | **IMPROVED, not zero** | See "T6 re-measure" below — the 486 keyword headings are gone, other mis-titles remain |
| T7 | PASS (guard) | **PASS** | CHM untouched: class-qualified headings, `topic=help`, zero keyword junk |
| T8 | PASS (guard) | **PASS** | `@D6 dd/mm/yyyy 31/10/1959`, `@D14 mm/yyyy 10/1959`, table aligned, top hit |

Supplementary ranking checks:

- `PROP:ImageBits` — LangRef #1 (heading `PROP:ImageBits`), ClarionHelp #2. The CHM masking is gone.
- `PROP:NumTabs number of TABs in a SHEET` — LangRef defining chunk now #1; `OCXLOADIMAGE`, which
  merely cited it, no longer outranks it.

## Index retag did not over-reach

The retag grew 530 → 1,149 tagged `index`. That growth is clean:

- **0** index-tagged chunks contain no dot-leader lines at all.
- The 12 longest index-tagged chunks are all genuine contents/TOC pages.

No prose was buried.

## Residual A — 12 pure-index fragments still tagged `section`

The retag rule requires **≥3 dot-leader lines**. Fragments of 1–2 lines cannot reach it, so a chunk
that is **100% dot-leader lines** still escapes when it is short:

```
ABC Library Reference   Section 24   1 line   "Undo (undo action) .......... 289"
ABC Library Reference   Section 89   2 lines
InternetBuilderClassReference   HttpPageBaseClass > Methods   2 lines  (x2)
```

Corpus-wide: **12** chunks, all 1–2 lines, across 9 libraries. The ≥50% set and the 100% set are
*identical*, so there are no partial escapers — the gap is exactly "short and pure".

**Observed harm: none.** Two of the twelve wear a class breadcrumb (`HttpPageBaseClass > Methods`),
which is the shape that caused the original problem, but a natural query for that class returns real
prose in all top 3. Cheap fix if wanted: retag when `leaders == lines` regardless of count. Not urgent.

## Residual B — the two deliberate exclusions are correct

Reproduced the implementer's ≥3-leader measure exactly: **2** ABC chunks, both the agreed ones.

| id | heading | ratio | verdict |
|----|---------|-------|---------|
| 141663 | `Section 1` | 16% (4/25) | Copyright page — real prose. **Agree, leave as `section`.** |
| 142999 | `WindowResizeClass > Update` | 21% (11/53) | Real method prose. **Agree, leave as `section`.** |

**But 142999 is class-mis-attributed.** Its content is `WindowManager.Update` — "calls
BrowseClass.UpdateViewRecord for each BrowseClass object added by the AddItem method" — not
`WindowResizeClass.Update`. The class latch carried over past its section. Separate bug from the
topic tag; worth a look because a wrong class breadcrumb is worse than a missing one.

## T6 re-measure — predicate restated

The original "183 genuinely-mis-titled" predicate was never written to a script and is not
recoverable from the chat log. **Do not compare the numbers below to 183.** They use a stated
predicate so future runs are comparable to *these*:

- **T6-crude** — leaf heading is all-caps, contains no lowercase, length ≥2, `topic<>'index'`,
  PDF libraries only: **667**.
- **T6-strict** — crude, minus chunks the document itself defines (content opens `LEAF (` or
  `LEAF,`, the reference-manual convention): **111**.

By library (strict): LanguageReference 65, TemplateLanguageReference 7, ReportWriter 7,
ReportWriterRetainFlow 7, TemplateGuide 6, DatabaseDrivers 6, others ≤2.

Confirmed gone: `(cont.)` headings **0**; bare `ACCEPT`/`PROGRAM`/`RETURN`/`CASE` leaves **0**.
`PROP:*` headings: **290**.

**ABC is not quite zero.** 3 crude, of which 1 is legitimate (`QFC` is a real property) and **2 are
genuine keyword mis-titles**:

- `ToolbarUpdateClass > Methods > ASSERT` — heading lifted from example code `ASSERT(~ERRORCODE())`.
  Note this one *evades* the strict filter, because the code line `ASSERT(...)` is indistinguishable
  from the doc's own `NAME (description)` convention. Any strict measure will under-count this shape.
- `QueryVisualClass > Properties > QC` — content is the `QueryVisualClass Properties` preamble.

## Residual C — stale continuation headings, quantified

The known `LIKE (part 12)` case is one of **1,955** chunks corpus-wide whose heading is a `(part N)`
continuation with N≥2: IDEReference 265, LanguageReference 244, NetTalk book 228,
AdvancedTopicsReferenceGuide 119, DatabaseDrivers 110, IDEUsersGuide 100, others below.

These are not *wrong*, but the heading carries no retrieval value. T1–T8 all pass without addressing
it, so it is not blocking; it is the largest remaining heading-quality item by volume.

## Minor observations

- The `PROP:NumTabs` chunk opens with the tail of `PROP:NoTips` — the boundary starts one property
  early. Answer and heading are both correct; cosmetic.
- `class_name` carries case variants for the same class (`ASCIIFileClass` and `AsciiFileClass` both
  present in ABC), which will split any future `class_name=` filter or grouping.

## Re-ingest procedure (required after any chunker change)

Pass `vendor` **explicitly** — the default (folder name) creates duplicate libraries instead of
replacing, because `libraries` is `UNIQUE(vendor, name)`:

| Folder | vendor |
|--------|--------|
| `C:\Clarion12\docs` (recursive — covers dfd + In-Memory-Driver) | `SoftVelocity` |
| `C:\Clarion12\accessory\Documents\BoxSoft` | `BoxSoft` |
| `C:\Clarion12\accessory\Documents\CapeSoft` | `CapeSoft` |
| `C:\Clarion11-13372\accessory\Documents\Capesoft\NetTalk` | `Capesoft` (lowercase s) |

`ingest_docs` writes the **bundled** DB. Settings → Data → Personal → Add Folder writes a
**different** DB and will not fix these rows.

## Notes for the implementer

- Chunk counts are **not** a quality signal. BoxSoft counts *dropped* under PdfPig
  (QuickBooks-Export 164 → 79) while text quality improved; SoftVelocity counts rose 4×.
  Judge this change on T1–T8, not on totals.
- `UNIQUE(library_id, class_name, method_name, topic, heading)` (`:127`) with `INSERT OR REPLACE`
  (`:2186`) looks like it should collapse same-named chunks. It does **not** — `method_name` is
  NULL for PDF chunks and SQLite treats NULLs as distinct in unique indexes, so it never fires.
  No data is being lost today. It becomes a live trap the moment `method_name` is populated,
  which fix (a) may well do — **if you start setting `method_name`, re-check this constraint.**
- Store the breadcrumb for FTS but strip it from displayed snippets, or every result opens with
  boilerplate (Charlie's note).

---

# Post-fix results — commit ecd2896 (on 60c1d02)

**Run:** 2026-08-04, by CA-Terminal-1-CC. T1–T7 through the live `query_docs` tool; corpus measures
by SQL against `%APPDATA%\ClarionAssistant\docgraph.db`, diffed against the
`docgraph.db.pre-classlatch` backup (the 6ccd068 state verified in the section above).
**Index state at run:** 162 libraries / 17,264 chunks / 1,161 tagged `index` — matches the
implementer's reported state exactly.
**Order:** T8 first, as the regression guard.

| Test | 6ccd068 | ecd2896 | Note |
|------|---------|---------|------|
| T1 | PASS | **PASS** | Answer at #1, `ASCIIFileClass > Functional Organization`, all three categories |
| T2 | PASS | **PASS** | 5/5 class-attributed; ConstantClass, PopupClass, QueryListVisual, WindowResizeClass, ToolbarClass |
| T3 | PASS | **PASS** | 5/5 prose, zero index chunks |
| T4 | PASS | **PASS** | `ErrorClass > Functional Organization` at #1 |
| T5 | PASS | **PASS** | Definition at #1 under `ASCIIFileClass > GetLastLineNo`, Return Data Type LONG |
| T6 | IMPROVED | **FLAT** | crude 667 → 667, strict 111 → 112, ABC crude 3 → 3. Unchanged by this pass |
| T7 | PASS | **PASS** | CHM untouched |
| T8 | PASS | **PASS** | `@D6 dd/mm/yyyy 31/10/1959`, `@D14 mm/yyyy 10/1959`, table aligned, top hit |

**The PROP: A/B — the thing this pass most needed to prove.** Both re-run without a `library=`
filter, which is the whole point of the test:

- `PROP:ImageBits` — **LanguageReference #1** (heading `PROP:ImageBits`, bare), ClarionHelp #2.
- `PROP:NumTabs number of TABs in a SHEET` — **LanguageReference #1** (heading `PROP:NumTabs`,
  bare), ClarionHelp #2.

The OLE regression is **not** back. Corpus-wide, class-prefixed `PROP:` headings are **0**
(one row matches `%> PROP:%` — `Prop:Project > Prop:SqlFilter > Prop:SqlOrder` in DriverKit — but
that is a legitimately nested breadcrumb, not a class prefix).

## The OLE regression — why a breadcrumb change is not cosmetic

Broadening the class-section pattern to accept names not ending in `Class` also let ALL-CAPS tokens
through. LanguageReference has an "OLE Properties" section, so **`OLE` latched as a class and
claimed 341 chunks**, rewriting headings from `PROP:ImageBits` to `OLE > PROP:ImageBits`.

No chunk's *content* was wrong. Every affected chunk held exactly the right text under exactly the
right leaf name. The damage was entirely in ranking: **the heading participates in the tier
expression**, so a bare `PROP:ImageBits` sits in the exact-heading tier while `OLE > PROP:ImageBits`
falls out of it into the LIKE tier — and ClarionHelp took rank #1 back. The win from 4465b05 was
silently undone by a change that looked like a pure metadata improvement.

The lesson, stated for whoever touches a breadcrumb next: **the heading is not a label, it is an
input to ranking.** A breadcrumb edit reorders results even when every chunk's content is correct,
and no content-level check will catch it. Any change that touches heading composition must re-run
the ranking A/Bs, not just inspect the headings.

The fix: a class identifier must be **MixedCase** — it must contain at least one lowercase
character. Every real class does (`WindowManager`, `IListControl`, `TagHTMLHelp`); every token of
the offending kind does not (`OLE`, `SHEET`, `ENTRY`). Verified corpus-wide: **0** latched
(non-fallback) `class_name` values are ALL-CAPS. The guard holds everywhere, not only in
LanguageReference.

## Class-latch broadening — what actually moved

ABC distinct `class_name`: **63 → 82**, and the arithmetic reconciles exactly:

- **−5** casing merges: `AsciiFileClass`, `AsciiPrintClass`, `AsciiSearchClass`, `AsciiViewerClass`,
  `ToolbarListboxClass` folded into their canonical spellings.
- **+24** newly latched: FileManager 116, ReportManager 92, WindowManager 65, ViewManager 37,
  cwRTF 29, ToolbarTarget 27, RelationManager 23, RuleManager 21, IReportGenerator 20,
  DbAuditManager 16, QueryFormVisual 16, TransactionManager 14, QueryListVisual 13, RulesManager 9,
  TagHTMLHelp 8, ToolbarListBoxClass 8, DbChangeManager 7, WindowComponent 5, IDbChangeAudit 4,
  DbLogFileManager 3, ErrorLogInterface 3, BrowseQueue 2, IListControl 2, StandardBehavior 2.

All 24 spot-checked against their headings; all are real ABC classes or interfaces. `RuleManager`
and `RulesManager` are **both** genuine and distinct (`BrokenRuleCount`/`AddRule` vs
`BrokenRulesCount`/`AddRulesCollection`) — not a casing split. Corpus-wide casing splits: **0**.
ABC `class_name` values containing a space or non-identifier character: **0** — no subsection word
leaked.

`142999` is fixed: the chunk is now `WindowManager > Update` and its content is
`WindowManager.Update`, as reported.

**The broadening reached beyond ABC.** Four other libraries also gained a class — this was not
predicted and is where the surprises were:

| Library | New class | Chunks | Verdict |
|---------|-----------|--------|---------|
| TemplateGuide | `MenuStyleManager` | 66 | **Win** — real class, was library-name fallback |
| DynamicFileDriverReference | `DynFile` | 51 | **Win** — real class, was library-name fallback |
| InternetBuilderClassReference | `SubmitItem` | 11 | Mixed — see below |
| Super Security - Documentation | `Class` | 11 | **Defect** — see below |

## Defect 1 — the bare word `Class` latched in Super Security

BoxSoft's manual has a section header reading literally `Class Properties`, so **`Class` latched as
a class name over 11 chunks**. It passes the MixedCase guard (it has lowercase), and the content is
in fact the `Security` class — so the correct breadcrumb is `Security`, not `Class`.

This is strictly worse than the library-name fallback it replaced: `Class` looks authoritative,
and it will split or poison any `class_name=` filter. One of the 11 is an index page
(`Auditing Field Edits During a Change Operation 47 - B - Backdoor 60`) now wearing `Class > Methods`.

Checked against a 30-word stoplist (`class`, `methods`, `properties`, `overview`, `concepts`,
`section`, `chapter`, `appendix`, `example`, `contents`, `index`, …): this is the **only**
generic-word latch corpus-wide. Blast radius is 11 chunks in one third-party library. Cheap fix:
reject a bare structural noun as a class identifier.

## Defect 2 — `SubmitItem` over-runs into `HtmlClass` (pre-existing, NOT a regression)

In InternetBuilderClassReference, chunks `160024`–`160033` are attributed to `SubmitItem` but belong
to `HtmlClass`: chunk 160024 opens `HtmlClass Properties 529 AppletCount …` and 160027 opens
`CHAPTER 17 HTML CLASS 527 Overview`. The leaves (`Browser`, `Client`, `FirstControl`,
`FirstSelectable`, `JavaLibraryZip`, `UseFonts`) are all HtmlClass properties.

`SubmitItem` itself is a legitimate new latch — chunk 160023 is genuinely
`SubmitItemClass.Reset`, its only method. The latch then runs past the end of its one-method
section.

**This is not a regression.** Before the change those same 10 chunks read `LayoutHtmlClass` — also
wrong, also a carry-over. The label moved from one wrong class to another wrong class; severity is
unchanged. `HtmlClass` does latch correctly elsewhere (37 chunks), so this is a boundary gap at a
chapter opening, not a failure of the class pattern. It is the same shape as the original
`WindowResizeClass > Update` bug, in a library nobody had looked at.

## Corrections to the implementer's reported numbers

Two, both small:

1. **Short pure-index fragments are 12 → 1, not 12 → 0.** One survivor: `InMemoryDriverRef`,
   heading `Section 24`, content `ValidateRecord (evaluate filter during load and save).. 67`.
   Its leader is **two** dots, not three, so it escapes any `...` predicate. Every three-dot short
   fragment is gone (measured 0). Worth fixing only if the leader test is cheap to relax.

2. **T6 is flat, not improved.** crude 667 → 667, strict 111 → 112, ABC crude 3 → 3 (`ASSERT`,
   `QC`, `QFC`, unchanged). This pass didn't target T6 and didn't move it; noting it so the next
   run isn't read as a regression. The 667 reproduces the prior predicate exactly, which is a
   useful confirmation that the measure itself is stable.

## Index retag still has not over-reached — re-confirmed at 1,161

Previously cleared at 1,149. At the new count of **1,161**: **0** index-tagged chunks contain no
dot-leader lines at all. The additional 12 rows are the short pure-index fragments correctly
retagged. No prose was buried.

`(part N)` continuation headings with N≥2: 1,955 → **1,930**. Still the largest remaining
heading-quality item by volume, still not blocking.

## Method note — before/after diffs on this DB must dedupe the join key

The first pass of the class-move detector joined old and new chunks on `SUBSTR(content,1,150)` and
produced ~200 rows of apparent class moves in InternetBuilderClassReference. **All of it was an
artifact.** Many chunks in that manual share an identical boilerplate opening, so the join
cross-produced. The tell was **symmetric pairs** — `WebHtmlTabClass → WebButtonClass 1` alongside
`WebButtonClass → WebHtmlTabClass 1`, and `CapeSoft ↔ FileExplorer 19/19`. Real moves are directional;
symmetric pairs are always a many-to-many join.

Re-running with a 1:1 guard — full `content` equality, and the content required to be unique within
its library on **both** sides (15,711 of 17,264 chunks pair 1:1) — collapsed those ~200 rows to
**one** real row, the `LayoutHtmlClass → SubmitItem` move above. Anyone repeating this comparison
should use the guarded form; the unguarded one manufactures regressions that do not exist.
