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
