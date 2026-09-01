---
name: clarion-solution
# prettier-ignore
description: Orient yourself in a folder containing a Clarion solution (.sln with .cwproj, or .clw/.inc/.app files). Establishes which solution you are in, whether its CodeGraph is indexed, and which Clarion intelligence tools are available. Use this FIRST, before answering questions about the codebase — it is what turns "read files and guess" into cross-solution symbol lookup, vendor documentation search, and live compiler-accurate navigation.
version: 1.0.0
---

# Working in a Clarion solution

You are in a folder with Clarion code. Before answering questions about it, find out what you are
looking at. Reading `.clw` files one at a time is the slowest and least reliable way to understand
a Clarion codebase, and it is what you will default to if you skip this.

## First moves

1. **`get_solution_info`** — which solution, which Clarion version, which redirection file, and
   crucially whether a CodeGraph database already exists (`isIndexed`).
2. **If `isIndexed` is false, run `index_solution`.** It parses every `.clw`/`.inc` the redirection
   file can reach — typically thousands of files including the ABC library — into a queryable
   database. A large solution takes minutes; a small one seconds. Do it once, then query it
   repeatedly.
3. **Then answer questions with `query_codegraph`,** not by opening files.

If those tools are not available to you, say so plainly rather than silently falling back to
reading files: it means Clarion Assistant is not installed, or its standalone MCP server is not
registered. The user needs to know, because the difference in what you can do is enormous.

## What you have, and when to reach for it

| Need | Tool |
|---|---|
| Where is this symbol defined / who calls it, across the whole solution | `query_codegraph` (SQL) |
| Same question, but the file was edited this session | `lsp_definition` / `lsp_references` / `lsp_hover` |
| How do I use this third-party template or class | `query_docs` |
| What tables/columns/keys does the dictionary define | `query_schema`, `get_table`, `search_tables` |
| Did my edit compile | `lsp_diagnostics` |
| Something I learned that should outlive this session | `add_knowledge` / `query_knowledge` |

**CodeGraph vs LSP.** CodeGraph is a snapshot: fast, cross-solution, bulk queries — and stale the
moment you edit. LSP reads live buffers and is authoritative for a file you just changed, but is
per-file rather than solution-wide. Use CodeGraph for "find everything", LSP for "is this correct
right now".

## Three things that will otherwise waste your time

**`query_codegraph` is real SQL** against a documented schema — `symbols`, `relationships`,
`projects`, `indexed_files`. Read the tool's own description before writing a query; it documents
the columns and the traps (MAP prototypes never receive call edges, template-generated code is
duplicated per app, a synthetic `__Libraries__` project owns the redirection-pulled headers).

**Symbol ids are not stable across a re-index.** Do not cache an id across an `index_solution`
call — resolve by name and file path instead. A stale id matches nothing, silently.

**Documentation library names carry versions.** CapeSoft's StringTheory is ingested as
`StringTheory3`, NetTalk as `NetTalk14`. `query_docs` accepts the bare name and matches the
versioned one, but if you go looking in `list_doc_libraries` output, that is what you will see.

## Clarion source conventions that bite immediately

- Labels start in **column 1**; code is indented. A label in the wrong column changes meaning.
- Files are **CRLF, ANSI or UTF-8 without BOM**. A BOM breaks the Clarion compiler.
- `.inc` declares (CLASS bodies, MAP prototypes), `.clw` implements. `sync_check` compares them.
- Generated `.clw` files are regenerated from the `.app`; editing them is a no-op that looks like
  it worked. Check whether a file is template-generated before proposing an edit to it.

For language syntax, data structures and template authoring, use the `clarion` skill. This one is
only about finding your way around a solution.
