# Clarion Assistant — a short history

> ### ⚠️ This is not a SoftVelocity product
>
> Clarion Assistant is an **independent, community project**. It is not made by, endorsed by, affiliated with, or supported by SoftVelocity, the owners of Clarion. If something in Clarion Assistant breaks, please don't ask SoftVelocity about it — [file an issue here](https://github.com/ClarionLive/ClarionAssistant/issues) instead.
>
> It was created by **John Hickey**, and it's free.

---

## What it is

An AI coding assistant that lives **inside** the Clarion IDE — not in a browser tab beside it. It docks as a pane, reads the file you have open, understands your solution's symbols and your dictionary, and can navigate you to code, write into embeds, and answer questions about a codebase it has actually indexed.

Along the way it grew a modern editing surface: a Monaco-based Clarion source editor and embeditor with real syntax highlighting, folding, find & replace, code completion, and diffing.

## Where it came from

John Hickey started it on **19 March 2026** and made the repository public eight days later, on **27 March 2026**, with `v1.0.0-beta`. It began as a way to get an AI assistant into the IDE where the work actually happens, and kept going because people kept asking for more.

There's a detail worth stating plainly, because it's the whole point of the thing: **Clarion Assistant was itself built largely in collaboration with Claude** — Anthropic's AI — with John directing the work, making the calls, and reviewing what shipped. A tool for AI-assisted Clarion development, built by AI-assisted Clarion development. The 73,000 lines of C# behind it are what that looks like in practice over four months.

## The story so far

**March – April 2026 · the assistant pane** (`v1` → `v3`)
The IDE chat panel, the first MCP tools that let the assistant actually drive the editor, class-model templates, database schema intelligence, source-control integration, and a multi-version installer covering several Clarion editions.

**April – May 2026 · a choice of engines** (`v4.0` → `v4.6`)
Clarion Assistant stopped being tied to one AI backend. GitHub Copilot CLI and OpenAI Codex CLI joined Claude Code as selectable engines, alongside a redesigned settings dialog, a security-hardening pass, external MCP client access, and support for your own custom MCP servers.

**June – July 2026 · the editor era** (`v5.0` → `v5.5`)
The biggest shift. Monaco became the default Clarion source editor; the CA Embeditor arrived, letting you work on multiple procedures at once and save straight back to the `.app`. Then Code Snippets, the Smart Formatter, a full Find & Replace suite, the Document Structure outline, signature help, dark mode, split panes, editable diffs, drag-and-drop from the Data pad — and a long accuracy campaign on the CodeGraph indexer driven by real production Clarion code.

The most recent release, [`v5.5.0`](https://github.com/ClarionLive/ClarionAssistant/releases/tag/v5.5.0), shipped on **31 July 2026**.

## By the numbers

*As of 1 August 2026 — roughly 4½ months in.*

| | |
|---|---:|
| First commit | 19 March 2026 |
| Repository made public | 27 March 2026 |
| Releases published | **17** |
| Release cadence | one every ~7.4 days |
| Commits | **586** |
| Merged pull requests | **70** |
| Issues filed / closed | **95 / 85** (89%) |
| People who have filed an issue | **14** |
| People with commits in the repo | **9** |
| Installer downloads | **529** |
| Tracked files | 304 |

**Code:** ~73,400 lines of C#, ~29,700 lines of HTML/JS for the editor and pad surfaces, ~19,900 lines of Markdown documentation, plus PowerShell tooling and Clarion sources.

**Supported:** Clarion 10, 11, 11.1 and 12 (32-bit IDE), each with its own build.

## Contributors

Clarion Assistant is small enough that every single person here made a visible difference. Names are as published on their GitHub profiles.

### Code

| | GitHub | |
|---|---|---|
| **John Hickey** — creator and maintainer | [@peterparker57](https://github.com/peterparker57) | |
| **geircodes** | [@geircodes](https://github.com/geircodes) | Largest external contributor by a wide margin — the CodeGraph accuracy campaign, split panes, the outline, completion scoping, and a great deal of the editor's reliability work |
| **Mark Sarson** | [@msarson](https://github.com/msarson) | Signature help, go-to-implementation, real-scope embeds, and the Clarion language server that Clarion Assistant's LSP features ride on |
| **Adrián Santarelli** | [@asantarelli](https://github.com/asantarelli) | Dictionary- and CodeGraph-aware completion, snippet parameter splitting |
| **Dinko Bačun** | [@bdinko](https://github.com/bdinko) | Indicio d.o.o. |
| **Jesper Z. Laugesen** | [@Aarhusdk](https://github.com/Aarhusdk) | SOFTdanmark ApS |
| **Paul Konyk** | [@OkayPlunk](https://github.com/OkayPlunk) | Clarion 11.1 as a distinct target, VS2026 build tools, portable installs |
| **Tomislav Tadin** | [@Turpija12](https://github.com/Turpija12) | |
| **Michael Boorman** | [@12boormi](https://github.com/12boormi) | Dr Smash Software |

### Reports, testing and design input

Several of the sharpest improvements came from people who never opened a pull request — the bug reports that came with a reproduction, the feature arguments that changed a design decision, and the "this doesn't feel right" notes that turned into real fixes.

| | GitHub | |
|---|---|---|
| **Mike Hanson** | [@BoxSoft](https://github.com/BoxSoft) | BoxSoft Corporation — native embeditor parity, the case for quoting code instead of line numbers, and a lot of hard-won detail |
| **Carl T. Barnes** | [@CarlTBarnes](https://github.com/CarlTBarnes) | |
| **Kevin Erskine** | [@KevinErskine](https://github.com/KevinErskine) | Ragazzi Enterprise, LLC |
| **William Atchison** | [@bill-atchison](https://github.com/bill-atchison) | |
| **armisoftware** | [@armisoftware](https://github.com/armisoftware) | ARMi software solutions — OMIT/COMPILE folding, fold-all, theming |
| **AdrianoAlv** | [@AdrianoAlv](https://github.com/AdrianoAlv) | |
| **firehooper** | [@firehooper](https://github.com/firehooper) | |

*If you belong on this page under a different name, or would rather not appear at all, open an issue and it will be changed.*

## Get it

- **Latest release:** https://github.com/ClarionLive/ClarionAssistant/releases/latest — the installer is code-signed
- **Source:** https://github.com/ClarionLive/ClarionAssistant
- **Issues and feature requests:** https://github.com/ClarionLive/ClarionAssistant/issues

Contributions are welcome, and as the list above shows, they genuinely shape where this goes.
